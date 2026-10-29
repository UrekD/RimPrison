using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// Smuggling rings (events plan, last of the self-contained checks; <see cref="RingSystem"/>): a ring forming from a dealer's own state, each way the warden
    /// learns a link (a frisk, a stash, a tap, a tip, the psychologist; one link at a time, and nothing written or shown names a link not found yet), the
    /// letter and the bust (every known link handled, the supply cut, the ring broken whole or taken out in part and rebuilt), turning a member, the map
    /// of finds; and the save/load round trip. Throwaway pawns (numbered from 91000, with names of their own so that a line can be searched for them),
    /// no real ticks, and the real rings are set aside while it runs.
    /// </summary>
    public static partial class AutoTest
    {
        // ---- the events plan's check -----------------------------------------------------------------------------------------------

        private static void RingTest(Map map)
        {
            // Its own dice, so the check is the same every run and leaves the game's random stream where it found it (the checks after it have luck of their own).
            Rand.PushState(20261020);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            try
            {
                RingChecksBody(map);
            }
            finally
            {
                Rand.PopState();
            }
            // Nothing left behind: no throwaway pawn, no police officer, on the map (the check runs no ticks, so nobody else came or went).
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            List<Pawn> destroyed = pawnsBefore.Where(x => x.Destroyed && !x.Dead).ToList();
            if (leftBehind.Count > 0 || destroyed.Count > 0)
                problems0.Add($"ring: the check left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map or destroyed {string.Join(", ", destroyed.Select(x => x.LabelShort))}");
        }

        private static bool Mentions(string text, string name) => Regex.IsMatch(text ?? "", @"\b" + Regex.Escape(name) + @"\b");

        private static void RingChecksBody(Map map)
        {
            Guarded("smuggling rings", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
                ThingDef drug = ContrabandUtility.AllContraband.FirstOrDefault(d => ContrabandUtility.Ext(d).kind == ContrabandKind.Drug);
                ThingDef shiv = ContrabandUtility.AllContraband.FirstOrDefault(d => ContrabandUtility.Ext(d).kind == ContrabandKind.Weapon) ?? drug;
                GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints");
                List<IntVec3> yard = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => c.Standable(map)).ToList();
                if (drug == null || yard.Count < 6 || RPRingDefOf.RP_Arc_SmugglingRing == null)
                {
                    problems0.Add($"ring: nothing to test with (drug {drug?.defName}, {yard.Count} yard cells, ring arc {RPRingDefOf.RP_Arc_SmugglingRing?.defName})");
                    return;
                }
                yard = yard.OrderBy(c => c.DistanceToSquared(yard[0])).Take(30).ToList();

                // The real rings, marks and inmates are set aside (the real inmates are made idle, so that none of them is a candidate or a customer).
                List<StoryArc> arcs0 = g.story.arcs;
                int nextArc0 = g.story.nextArcId;
                var lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                List<ContrabandFind> finds0 = pm.finds;
                var active0 = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(x => x != null).Select(x => (inmate: x, active: x.activeFlag)).ToList();
                var guards0 = pm.Guards.ToList();
                var cameras0 = pm.Cameras.ToList();
                var towers0 = pm.Towers.ToList();
                r.Add(() =>
                {
                    g.story.arcs = arcs0;
                    g.story.nextArcId = nextArc0;
                    g.story.lastArcStart = lastStart0;
                    pm.finds = finds0;
                    foreach ((CompInmate inmate, bool active) in active0) inmate.activeFlag = active;
                    pm.Guards.Clear();
                    pm.Guards.AddRange(guards0);
                    pm.Cameras.Clear();
                    pm.Cameras.AddRange(cameras0);
                    pm.Towers.Clear();
                    pm.Towers.AddRange(towers0);
                });
                // (Real corrupt staff who work for a whole gang would be the officers of a test dealer in that gang: they are set aside as well.)
                var gangStaff = StaffCommute.AllStaff(map).Select(x => x.GetComp<CompStaff>()).Where(x => x.patronGang != null).Select(x => (staff: x, gang: x.patronGang)).ToList();
                r.Add(() =>
                {
                    foreach ((CompStaff staff, GangDef gang) in gangStaff) staff.patronGang = gang;
                });
                foreach ((CompStaff staff, GangDef _) in gangStaff) staff.patronGang = null;
                g.story.arcs = arcs0.Where(a => a.def != RPRingDefOf.RP_Arc_SmugglingRing).ToList();
                pm.finds = new List<ContrabandFind>();
                foreach ((CompInmate inmate, bool _) in active0) inmate.activeFlag = false;

                int spot = 0;
                Pawn Mk(string name, GangDef gang, bool isDealer = false)
                {
                    Pawn p = Throwaway(map, r);
                    p.Name = new NameSingle(name);
                    CompInmate c = p.Inmate();
                    (c.activeFlag, c.gang, c.gangKnown, c.cash, c.tension, c.fear, c.want, c.snitchExposed, c.lastDealerId) = (true, gang, false, 0, 20f, 0f, null, false, -1);
                    // (A random inmate arrives with reputations of their own: no snitch, and a dealer only when it is meant.)
                    c.reputations.Clear();
                    if (isDealer) c.reputations.Add(dealerRep);
                    p.pather.StopDead();
                    p.Position = yard[spot++ % yard.Count];
                    p.Notify_Teleported();
                    return p;
                }
                Pawn MkStaff(string name)
                {
                    Pawn s = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                    s.Name = new NameSingle(name);
                    return s;
                }
                Pawn MkOfficer(string name, Pawn patron)
                {
                    Pawn s = MkStaff(name);
                    CompStaff sc = s.GetComp<CompStaff>();
                    (sc.corrupt, sc.patron, sc.patronGang, sc.integrity, sc.suspicion) = (true, patron, null, 20f, 0f);
                    sc.evidence.Clear();
                    return s;
                }
                void Retire(params Pawn[] ps)
                {
                    foreach (Pawn p in ps) (p.Inmate().activeFlag, p.Inmate().gang) = (false, null);
                }
                // Everything the warden has been told since the check began: chronicle lines and letters (the words a hidden link must not appear in).
                bool Carries(StoryArc a, Pawn courierPawn, ThingDef def)
                {
                    RingSystem.Notify_BroughtIn(courierPawn, null, def);
                    return Link(a, RingRole.Courier).carried > 0;
                }
                string Told() => string.Join("\n", r.NewEntries.Select(e => e.text).Concat(r.NewLetters.Select(l => l.Label.ToString() + " " + ((l as ChoiceLetter)?.Text.ToString() ?? ""))));
                int Known(StoryArc a) => a.ring.links.Count(l => l.known);
                RingLink Link(StoryArc a, RingRole role) => a.ring.links.Find(l => l.role == role);
                ChoiceLetter_PrisonDecision AskedAbout(StoryArc a) => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.arcId == a.id);
                void ClearArrest(Pawn staff)
                {
                    Lord lord = staff.GetLord();
                    if (lord == null) return;
                    foreach (Pawn x in lord.ownedPawns.ToList())
                        if (x != staff && !x.Destroyed) x.Destroy(DestroyMode.Vanish);
                    if (map.lordManager.lords.Contains(lord)) map.lordManager.RemoveLord(lord);
                }
                string log = "";

                // ================================ A: a ring forms, is found link by link, and is broken whole ================================
                Pawn dealer = Mk("Zdealer1", ravens, isDealer: true), courier = Mk("Zcourier1", ravens), snitch = Mk("Zsnitch1", saints);
                Pawn guard = MkStaff("Zguard1"), officer = MkOfficer("Zofficer1", dealer);
                CompInmate dc = dealer.Inmate(), cc = courier.Inmate();
                r.Add(() => ClearArrest(officer));
                const string supplierName = "cousin Zorbexa";

                // ---- forming: the officer on the dealer's payroll is read from their state, a courier from their gang is recruited, the outside supplier comes with a delivery ----
                StoryArc ring = RingSystem.Form(dealer, map);
                RingLink dealerLink = ring == null ? null : Link(ring, RingRole.Dealer), officerLink = ring == null ? null : Link(ring, RingRole.Officer);
                bool formed = ring != null && ring.A == dealer && dealerLink?.pawnId == dealer.thingIDNumber && officerLink?.pawnId == officer.thingIDNumber && ring.ring.links.Count == 2
                              && Known(ring) == 0 && ring.stage == 0 && !ring.def.occupiesCast && !g.story.InArc(dealer) && RingSystem.Form(dealer, map) == null;
                if (!formed)
                {
                    problems0.Add($"ring: a dealer with a corrupt officer on their payroll got no hidden ring of dealer + officer, kept out of the story count, and none twice ({(ring == null ? "no ring" : string.Join(", ", ring.ring.links.Select(l => l.Describe())))})");
                    return;
                }
                RingLink recruited = RingSystem.Recruit(ring, map, force: true);
                bool courierOk = recruited is { role: RingRole.Courier, known: false } && recruited.pawnId == courier.thingIDNumber && RingSystem.Recruit(ring, map, force: true) == null;
                RingSystem.Notify_BroughtIn(dealer, supplierName);
                RingSystem.Notify_BroughtIn(dealer, "cousin Other");
                RingLink supplier = Link(ring, RingRole.Relative);
                bool supplierOk = supplier != null && supplier.name == supplierName && supplier.pawnId == dealer.thingIDNumber && !supplier.known && ring.ring.links.Count(l => l.role == RingRole.Relative) == 1;
                if (!courierOk || !supplierOk) problems0.Add($"ring: a courier from the dealer's gang ({courierOk}) and the outside supplier from a delivery ({supplierOk}) did not join the ring: {string.Join(", ", ring.ring.links.Select(l => l.Describe()))}");
                // Nothing is known: it is not listed (the Intel tab shows only rings with a known link), the chain is the one marker for more links (not one per link), nothing was written.
                string more = "RP_IntelRingMore".Translate().ToString();
                List<RingPart> hidden = RingSystem.Chain(ring);
                string toldNothing = Told();
                if (RingSystem.VisibleRings().Contains(ring) || hidden.Count != 1 || hidden[0].Known || hidden[0].text != more
                    || new[] { "Zdealer1", "Zcourier1", "Zofficer1", "Zorbexa" }.Any(n => Mentions(toldNothing, n)))
                    problems0.Add($"ring: a ring with nothing found is listed ({RingSystem.VisibleRings().Count} visible), its chain (the one marker expected) is {string.Join(" > ", hidden.Select(p => p.text))}, or something was written that names its links");
                log += $"formed with {ring.ring.links.Count} links";

                // ---- before a bust the dealer and the courier are open to visits and post ----
                float visitBefore = VisitationSystem.SmuggleChance(dealer, dc), postBefore = MailSystem.ContrabandChance(dc), courierVisitBefore = VisitationSystem.SmuggleChance(courier, cc);
                if (visitBefore <= 0f || postBefore <= 0f || courierVisitBefore <= 0f) problems0.Add($"ring: before a bust the dealer's visits ({visitBefore}) or post ({postBefore}) or the courier's visits ({courierVisitBefore}) brought nothing in");

                // ---- the courier hands stock to the dealer (no guard, camera or tower in sight, the same room), and not under a guard's eye ----
                pm.Guards.Clear();
                pm.Cameras.Clear();
                pm.Towers.Clear();
                (courier.Position, dealer.Position, guard.Position) = (yard[0], yard[1], yard[2]);
                foreach (Pawn p in new[] { courier, dealer, guard }) p.Notify_Teleported();
                // (What came in through the ring: a visit's, here.)
                bool handed = ContrabandUtility.Give(courier, drug) != null && Carries(ring, courier, drug) && RingSystem.TryHandover(ring, map, force: true)
                              && dealer.inventory.innerContainer.Any(t => t.def == drug) && !courier.inventory.innerContainer.Any(t => t.def == drug);
                if (!handed) problems0.Add($"ring: a courier with drugs in the dealer's room, unwatched, did not hand them over (watched {TensionUtility.Watchers(courier, pm)}, same room {courier.GetRoom() == dealer.GetRoom()})");
                pm.Guards.Add(guard);
                bool watched = ContrabandUtility.Give(courier, drug) != null && Carries(ring, courier, drug) && TensionUtility.Watched(courier, pm);
                if (!watched || RingSystem.TryHandover(ring, map, force: true)) problems0.Add($"ring: a courier handed stock to the dealer under a guard's eye (watched {watched})");
                pm.Guards.Clear();
                ring.def.Worker.Hourly(ring, map);
                // (Back to what the finds below start from: nothing on either of them.)
                foreach (Pawn p in new[] { dealer, courier })
                    foreach (Thing t in p.inventory.innerContainer.Where(x => ContrabandUtility.IsContraband(x)).ToList()) t.Destroy();

                // ---- a find on the courier: heat, and that link is known; no one else's ----
                ContrabandUtility.Give(courier, shiv);
                float heat0 = ring.ring.heat;
                int finds1 = pm.finds.Count, linksFound0 = g.stats.ringLinksFound;
                int n = ContrabandUtility.Search(guard, courier, 1f);
                bool courierFound = n > 0 && Link(ring, RingRole.Courier).known && !Link(ring, RingRole.Dealer).known && !Link(ring, RingRole.Officer).known && !Link(ring, RingRole.Relative).known
                                    && Mathf.Approximately(ring.ring.heat, heat0 + RingSystem.HeatPerFind) && g.stats.ringLinksFound == linksFound0 + 1 && RingSystem.VisibleRings().Contains(ring);
                if (!courierFound) problems0.Add($"ring: a find on the courier ({n}) did not raise the heat by {RingSystem.HeatPerFind} to {ring.ring.heat} and reveal only the courier's link ({string.Join(", ", ring.ring.links.Select(l => l.Describe()))})");
                ContrabandFind pocketFind = pm.finds.Count == finds1 + 1 ? pm.finds[^1] : null;
                if (pocketFind == null || pocketFind.cell != courier.Position || pocketFind.source != FindSource.Pockets || pocketFind.kind != ContrabandUtility.Ext(shiv).kind)
                    problems0.Add($"ring: a find on an inmate left {pm.finds.Count - finds1} marks on the map of finds, the last {pocketFind?.Describe()} (expected {courier.Position.x},{courier.Position.z}:{ContrabandUtility.Ext(shiv).kind}:Pockets)");
                string told = Told();
                if (!Mentions(told, "Zcourier1") || Mentions(told, "Zdealer1") || Mentions(told, "Zofficer1") || Mentions(told, "Zorbexa"))
                    problems0.Add($"ring: what the warden was told after the find on the courier names the courier ({Mentions(told, "Zcourier1")}) and must not name the dealer ({Mentions(told, "Zdealer1")}), the officer ({Mentions(told, "Zofficer1")}) or the supplier ({Mentions(told, "Zorbexa")})");
                if (AskedAbout(ring) != null) problems0.Add("ring: the warden was asked about a ring at heat 15");
                List<RingPart> chain1 = RingSystem.Chain(ring);
                if (chain1.Count != 2 || chain1.Count(p => p.Known) != 1 || chain1[0].text != "Zcourier1" || chain1[1].text != more)
                    problems0.Add($"ring: the chain after the find on the courier reads {string.Join(" > ", chain1.Select(p => p.text))}");
                log += ", find";

                // ---- a stash find on the dealer: that link, the heat, and a mark on the map of finds at the cell ----
                int finds2 = pm.finds.Count, found = 0;
                for (int tries = 0; tries < 12 && found == 0; tries++)
                {
                    dc.stash.TryAdd(ThingMaker.MakeThing(drug));
                    found = StashUtility.Search(guard, dealer, dc);
                }
                ContrabandFind stashFind = pm.finds.Count > finds2 ? pm.finds[finds2] : null;
                if (found == 0 || !Link(ring, RingRole.Dealer).known || !Mathf.Approximately(ring.ring.heat, heat0 + 2 * RingSystem.HeatPerFind)
                    || stashFind == null || stashFind.source != FindSource.Stash || stashFind.kind != ContrabandKind.Drug || stashFind.cell != guard.Position || stashFind.tick != Find.TickManager.TicksGame)
                    problems0.Add($"ring: a stash find on the dealer ({found}) did not reveal the dealer and raise the heat to {heat0 + 2 * RingSystem.HeatPerFind} (it is {ring.ring.heat}) with a mark at the guard's cell {guard.Position} on the map of finds ({stashFind?.Describe()})");
                if (Link(ring, RingRole.Officer).known || Link(ring, RingRole.Relative).known) problems0.Add("ring: a stash find on the dealer gave away the officer or the supplier");
                // The map keeps 30 days: an older mark goes when the next is made.
                pm.finds.Add(new ContrabandFind { cell = yard[3], kind = ContrabandKind.Tool, source = FindSource.Mail, tick = Find.TickManager.TicksGame - (PrisonMap.FindsDays + 1) * GenDate.TicksPerDay });
                int beforeOld = pm.finds.Count;
                pm.AddFind(yard[4], ContrabandKind.Phone, FindSource.Visitor);
                if (pm.finds.Count != beforeOld || pm.finds.Any(f => f.cell == yard[3])) problems0.Add($"ring: the map of finds kept a mark older than {PrisonMap.FindsDays} days ({pm.finds.Count} marks, was {beforeOld})");
                log += ", stash";

                // ---- a phone tap: the officer, one link at a time; the heat passes the line and the warden is asked, once ----
                float tapHeat0 = ring.ring.heat;
                int tapCalls = 0;
                bool oneAtATime = true;
                while (!Link(ring, RingRole.Officer).known && tapCalls < 40)
                {
                    int knownBefore = Known(ring);
                    CorruptionSystem.Notify_TappedArrangement(dealer, dc);
                    tapCalls++;
                    if (Known(ring) - knownBefore > 1) oneAtATime = false;
                }
                if (!Link(ring, RingRole.Officer).known || !oneAtATime || Link(ring, RingRole.Relative).known || Known(ring) != 3)
                    problems0.Add($"ring: phone taps ({tapCalls}) did not reveal the officer ({Link(ring, RingRole.Officer).known}) one link at a time ({oneAtATime}) and leave the supplier ({Link(ring, RingRole.Relative).known}) hidden ({Known(ring)} of 4 known)");
                if (ring.ring.heat < tapHeat0 + RingSystem.HeatTap) problems0.Add($"ring: phone taps did not raise the heat ({tapHeat0} -> {ring.ring.heat})");
                ChoiceLetter_PrisonDecision ask = AskedAbout(ring);
                if (ask == null) problems0.Add($"ring: the ring at heat {ring.ring.heat} with links known did not put its case to the warden");
                else
                {
                    IReadOnlyList<string> options = ask.decision.Worker.Options(ask);
                    string text = ask.Text.ToString();
                    if (options.Count != 4 || ask.decision.Worker.Disabled(ask, 0) != null || ask.decision.Worker.Disabled(ask, 2) != null || ask.decision.Worker.Disabled(ask, 3) != null)
                        problems0.Add($"ring: the ring's letter offers {options.Count} moves, bust {ask.decision.Worker.Disabled(ask, 0)}, turn the officer {ask.decision.Worker.Disabled(ask, 2)}, turn a courier {ask.decision.Worker.Disabled(ask, 3)} (four, none disabled)");
                    // (It was sent when the heat crossed the line, with the links known then: the dealer and the courier, the officer only if the first tap had found them.)
                    if (Mentions(text, "Zorbexa") || !Mentions(text, "Zdealer1") || !Mentions(text, "Zcourier1") || ask.pawns.Contains(officer) != Mentions(text, "Zofficer1") || ask.pawns.Any(p => p != dealer && p != courier && p != officer))
                        problems0.Add($"ring: the ring's letter names a link not found, or leaves out one that is (supplier {Mentions(text, "Zorbexa")}, dealer {Mentions(text, "Zdealer1")}, courier {Mentions(text, "Zcourier1")}, officer in text {Mentions(text, "Zofficer1")} and in the letter's targets {ask.pawns.Contains(officer)})");
                }
                if (RingSystem.MaybeAsk(ring, map)) problems0.Add("ring: the warden was asked again at once, inside the cooldown");
                if (Mentions(Told(), "Zorbexa")) problems0.Add("ring: the supplier is named before anyone found them (chronicle or letter)");
                log += ", tap";

                // ---- an informant's tip names one link, the supplier, and only that one; far from the ring a snitch has nothing to tell ----
                int knownBeforeTip = Known(ring);
                string tip = RingSystem.TryTip(snitch, new List<Pawn> { dealer }, chance: 1f);
                if (tip == null || !Mentions(tip, "Zorbexa") || Mentions(tip, "Zdealer1") || Mentions(tip, "Zofficer1") || Mentions(tip, "Zcourier1") || Known(ring) != knownBeforeTip + 1 || !Link(ring, RingRole.Relative).known)
                    problems0.Add($"ring: an informant's tip near a ring member said \"{tip}\" and left {Known(ring)} of 4 known (expected the supplier alone, {knownBeforeTip + 1} known)");
                string farTip = RingSystem.TryTip(snitch, new List<Pawn>(), chance: 1f);
                if (farTip != null) problems0.Add($"ring: a snitch near nobody in the ring told of it anyway (\"{farTip}\")");
                log += ", tip";

                // ---- the bust: every link is known; each is handled, the supply stops, the ring is broken whole ----
                foreach (Pawn p in new[] { dealer, courier })
                {
                    CompInmate c = p.Inmate();
                    ContrabandUtility.Give(p, drug);
                    c.stash.TryAdd(ThingMaker.MakeThing(drug));
                    c.smuggleArranged = true;
                    c.nextVisitFlagged = true;
                }
                int records0 = dc.records.Count + cc.records.Count;
                int busted0 = g.stats.ringsBusted, today0 = g.today.ringsBusted, partial0 = g.stats.ringsPartial;
                int handled = ring.ring.links.All(l => l.known) ? RingSystem.Bust(ring, map) : -1;
                bool allHandled = handled == 4 && ring.ring.links.All(l => l.handled);
                bool inmatesClean = !ContrabandUtility.CarriesContraband(dealer) && !ContrabandUtility.CarriesContraband(courier) && dc.stash.Count == 0 && cc.stash.Count == 0;
                bool officerGone = officer.GetComp<CompStaff>() is { role: null, exposedCorrupt: true };
                bool supplyStops = RingSystem.SupplyCut(dealer) && RingSystem.SupplyCut(courier) && VisitationSystem.SmuggleChance(dealer, dc) == 0f && VisitationSystem.SmuggleChance(courier, cc) == 0f
                                   && MailSystem.ContrabandChance(dc) == 0f && MailSystem.ContrabandChance(cc) == 0f && !dc.smuggleArranged && !cc.smuggleArranged && !dc.nextVisitFlagged
                                   && dc.visitBanUntilTick > Find.TickManager.TicksGame && cc.visitBanUntilTick > Find.TickManager.TicksGame;
                if (!allHandled || !inmatesClean || !officerGone || !supplyStops)
                    problems0.Add($"ring: a bust of a ring with every link known handled {handled} of 4 ({allHandled}), took what the inmates carry and hide ({inmatesClean}), let the officer go and arrested them ({officerGone}) and stopped the dealer's and the courier's visits and post ({supplyStops})");
                if (dc.records.Count + cc.records.Count <= records0) problems0.Add("ring: the dealer and the courier were not put on record for what was taken");
                if (ring.stage != 1 || ring.ring.brokenUntilTick <= Find.TickManager.TicksGame || g.stats.ringsBusted != busted0 + 1 || g.today.ringsBusted != today0 + 1 || g.stats.ringsPartial != partial0 || ring.ring.heat != 0f)
                    problems0.Add($"ring: a ring broken whole is stage {ring.stage}, broken until {ring.ring.brokenUntilTick}, counted {g.stats.ringsBusted - busted0} whole and {g.stats.ringsPartial - partial0} in part, heat {ring.ring.heat}");
                if (!r.NewEntries.Any(e => e.importance == 3 && e.cat == RPChronicleDefOf.RP_Chr_Security && Mentions(e.text, "Zdealer1") && Mentions(e.text, "Zcourier1") && Mentions(e.text, "Zofficer1") && Mentions(e.text, "Zorbexa")))
                    problems0.Add("ring: breaking a ring whole is not a milestone in the chronicle that names all its links");
                if (!r.NewLetters.Any(l => l.Label == "RP_RingBrokenLabel".Translate().ToString())) problems0.Add("ring: breaking a ring whole sent no letter");
                // The day's security grade is lifted (unless it was full already): the same day, with and without the ring.
                float gradeWith = PrisonGrading.Compute(map, g).security;
                g.today.ringsBusted = today0;
                float gradeWithout = PrisonGrading.Compute(map, g).security;
                g.today.ringsBusted = today0 + 1;
                if (gradeWith < gradeWithout || gradeWith <= gradeWithout && gradeWithout < 100f)
                    problems0.Add($"ring: breaking a ring whole did not lift the security grade ({gradeWithout:F1} -> {gradeWith:F1})");
                if (RingSystem.Bust(ring, map) != 0) problems0.Add("ring: a ring already broken was busted again");
                ClearArrest(officer);
                log += ", bust";

                // ---- the cut runs out, the ring ends, and the dealer (still a dealer) starts another, with no known link ----
                foreach (RingLink l in ring.ring.links) l.handledTick -= RingSystem.CutDays * GenDate.TicksPerDay + 10;
                AgeTheSupplyCuts(map);
                ring.ring.brokenUntilTick = Find.TickManager.TicksGame - 1;
                RingSystem.Daily(map, only: dealer);
                bool ended = ring.done;
                RingSystem.Daily(map, only: dealer);
                StoryArc next = RingSystem.ArcOfDealer(dealer);
                // (A dealer the warden has taken out is a known one: the new ring has that link found, and nothing else.)
                if (!ended || next == null || next == ring || Known(next) != 1 || !Link(next, RingRole.Dealer).known || RingSystem.SupplyCut(dealer))
                    problems0.Add($"ring: after the cut ran out the broken ring was done {ended} and the dealer's next ring is {(next == null ? "missing" : next == ring ? "the old one" : Known(next) + " links known")} (only the dealer known expected, no supply cut)");
                g.story.arcs.Remove(next);
                g.story.arcs.Remove(ring);
                Retire(dealer, courier, snitch);

                // ================================ B: a customer found; the psychologist; a partial bust; the dealer rebuilds ================================
                Pawn dealerB = Mk("Zdealer2", saints, isDealer: true), courierB = Mk("Zcourier2", saints), courierB2 = Mk("Zcourier3", saints), buyer = Mk("Zbuyer2", null);
                StoryArc ringB = RingSystem.Form(dealerB, map);
                RingLink cB = ringB == null ? null : RingSystem.Recruit(ringB, map, force: true);
                if (ringB == null || cB == null || cB.pawnId != courierB.thingIDNumber && cB.pawnId != courierB2.thingIDNumber)
                {
                    problems0.Add($"ring B: a ring for a second dealer with a gang mate as courier did not form ({ringB != null}, {cB?.Describe()})");
                    return;
                }
                Pawn courierHere = cB.pawnId == courierB.thingIDNumber ? courierB : courierB2, courierSpare = courierHere == courierB ? courierB2 : courierB;
                // Someone the dealer sold to is found with drugs: the heat rises (the trace to the dealer may hold); the courier is not given away.
                buyer.Inmate().lastDealerId = dealerB.thingIDNumber;
                ContrabandUtility.Give(buyer, drug);
                float heatB0 = ringB.ring.heat;
                ContrabandUtility.Search(guard, buyer, 1f);
                // (The heat rises with the trace and only then: a find that traced nothing must not heat a ring whose dealer is unknown. The dice decide whether it did.)
                float heatExpected = heatB0 + (Link(ringB, RingRole.Dealer).known ? RingSystem.HeatTraced : 0f);
                if (!Mathf.Approximately(ringB.ring.heat, heatExpected) || Link(ringB, RingRole.Courier).known)
                    problems0.Add($"ring B: a find on the dealer's customer moved the heat {heatB0} -> {ringB.ring.heat} (expected {heatExpected}: +{RingSystem.HeatTraced} with the dealer traced {Link(ringB, RingRole.Dealer).known}, else none) or gave away the courier ({Link(ringB, RingRole.Courier).known})");
                Retire(buyer);
                bool dealerTraced = Link(ringB, RingRole.Dealer).known;
                // The psychologist: the courier's own part first, nothing for someone outside the ring.
                bool session = RingSystem.Notify_Assessed(courierHere, 1f);
                if (!session || !Link(ringB, RingRole.Courier).known || Known(ringB) != (dealerTraced ? 2 : 1))
                    problems0.Add($"ring B: a session with the courier revealed {Known(ringB)} links (courier {Link(ringB, RingRole.Courier).known}; the dealer {dealerTraced} before)");
                if (RingSystem.Notify_Assessed(courierSpare, 1f)) problems0.Add("ring B: a session with someone outside the ring gave something away");
                // Only the courier is known to the warden from here (the dealer, if the trace gave them away, is unknown again for the sake of the bust below).
                Link(ringB, RingRole.Dealer).known = false;
                ContrabandUtility.Give(dealerB, drug);
                ContrabandUtility.Give(courierHere, drug);
                int partial1 = g.stats.ringsPartial, whole1 = g.stats.ringsBusted, today1 = g.today.ringsBusted, entriesBeforeBust = r.NewEntries.Count();
                int handledB = RingSystem.Bust(ringB, map);
                bool partialOk = handledB == 1 && Link(ringB, RingRole.Courier).handled && !Link(ringB, RingRole.Dealer).handled && ringB.stage == 0 && ringB.ring.heat == 0f && !ringB.done
                                 && g.stats.ringsPartial == partial1 + 1 && g.stats.ringsBusted == whole1 && g.today.ringsBusted == today1
                                 && !ContrabandUtility.CarriesContraband(courierHere) && ContrabandUtility.CarriesContraband(dealerB) && RingSystem.SupplyCut(courierHere) && !RingSystem.SupplyCut(dealerB);
                if (!partialOk)
                    problems0.Add($"ring B: a partial bust handled {handledB}, left the ring stage {ringB.stage} at heat {ringB.ring.heat} with the dealer untouched and still supplied, counted {g.stats.ringsPartial - partial1} in part and {g.stats.ringsBusted - whole1} whole");
                List<string> bustLines = r.NewEntries.Skip(entriesBeforeBust).Select(e => e.text).ToList();
                if (bustLines.Count == 0 || bustLines.Any(l => Mentions(l, "Zdealer2")) || !bustLines.Any(l => Mentions(l, courierHere.LabelShort)))
                    problems0.Add($"ring B: the lines about a partial bust must name the courier taken out and not the dealer nobody had found: {string.Join(" | ", bustLines)}");
                // Rebuilding: the dealer takes on another courier once the spacing is over (the one taken out is no candidate and no longer in the ring), and the cut ends in its time.
                Retire(courierHere);
                bool waits = RingSystem.Recruit(ringB, map, force: true) == null;
                ringB.ring.nextRecruitTick = Find.TickManager.TicksGame;
                RingLink spare = RingSystem.Recruit(ringB, map, force: true);
                if (!waits || spare == null || spare.pawnId != courierSpare.thingIDNumber || spare.known || RingSystem.LinkOf(courierHere, out _) != null)
                    problems0.Add($"ring B: after a partial bust the dealer took on a new courier at once ({!waits}) or not once the spacing was over ({spare?.Describe()}), or the courier taken out is still in the ring");
                foreach (RingLink l in ringB.ring.links.Where(x => x.handled)) l.handledTick -= RingSystem.CutDays * GenDate.TicksPerDay + 10;
                AgeTheSupplyCuts(map);
                RingSystem.Daily(map, only: dealerB);
                if (ringB.ring.links.Exists(l => l.handled) || RingSystem.SupplyCut(courierHere)) problems0.Add("ring B: the courier taken out stayed cut off after the cut was over");
                log += ", partial bust and rebuild";
                g.story.arcs.Remove(ringB);
                Retire(dealerB, courierB, courierB2, buyer);

                // ================================ C: turning a member; a courier and then the dealer leaving ================================
                Pawn dealerC = Mk("Zdealer3", ravens, isDealer: true), courierC = Mk("Zcourier4", ravens), officerC = MkOfficer("Zofficer3", dealerC);
                StoryArc ringC = RingSystem.Form(dealerC, map);
                RingLink cC = ringC == null ? null : RingSystem.Recruit(ringC, map, force: true);
                RingSystem.Notify_BroughtIn(dealerC, "cousin Qwixel");
                if (ringC == null || cC == null || Link(ringC, RingRole.Officer) == null || Link(ringC, RingRole.Relative) == null)
                {
                    problems0.Add($"ring C: a ring with an officer, a courier and a supplier did not form ({ringC != null}, {cC != null})");
                    return;
                }
                // Not found yet: nothing can be turned, nor busted.
                if (RingSystem.Turnable(ringC).Count != 0 || RingSystem.Bustable(ringC).Count != 0 || RingSystem.Turn(ringC, Link(ringC, RingRole.Officer), map))
                    problems0.Add("ring C: the warden could turn or bust a link they had not found");
                // The courier (a session) and the officer (a tap) are found, then turned.
                RingSystem.Notify_Assessed(courierC, 1f);
                RingSystem.Notify_Tapped(dealerC, force: true);
                RingLink oC = Link(ringC, RingRole.Officer), kC = Link(ringC, RingRole.Courier);
                if (!oC.known || !kC.known) problems0.Add($"ring C: the officer ({oC.known}) and the courier ({kC.known}) were not found for the turning");
                bool turnedOfficer = RingSystem.Turn(ringC, oC, map), turnedCourier = RingSystem.Turn(ringC, kC, map);
                CompStaff soc = officerC.GetComp<CompStaff>();
                bool turnedOk = turnedOfficer && turnedCourier && oC.turned && kC.turned && soc.doubleAgent && soc.exposedCorrupt && courierC.Inmate().KnownReputation(ReputationDefOf.RP_Snitch)
                                && RingSystem.Turnable(ringC).Count == 0 && RingSystem.Bustable(ringC).All(l => !l.turned);
                if (!turnedOk)
                    problems0.Add($"ring C: turning an officer ({turnedOfficer}) and a courier ({turnedCourier}) left them turned {oC.turned}/{kC.turned}, the officer a double agent {soc.doubleAgent}, the courier a known informant {courierC.Inmate().KnownReputation(ReputationDefOf.RP_Snitch)}");
                // The next day each turned member gives up one more link (the supplier, then the dealer); a turned courier may be found out meanwhile.
                int knownBeforeDay = Known(ringC);
                RingSystem.Daily(map, only: dealerC);
                if (Known(ringC) <= knownBeforeDay && ringC.ring.links.Exists(l => l.role == RingRole.Courier)) problems0.Add("ring C: the turned members told the warden nothing the next day");
                // Wait: nothing happens.
                string describedC = ringC.ring.Describe();
                ((SmugglingRingArc)ringC.def.Worker).Choose(ringC, map, 1);
                if (ringC.ring.Describe() != describedC) problems0.Add("ring C: waiting changed the ring");
                // A courier leaving takes their link; the dealer leaving ends the ring, and the line says so naming a dealer only if the warden knew them.
                bool courierStillIn = ringC.ring.links.Exists(l => l.role == RingRole.Courier && l.pawnId == courierC.thingIDNumber);
                int linksC = ringC.ring.links.Count;
                g.story.Notify_Leaving(courierC);
                if (courierStillIn && (ringC.ring.links.Count != linksC - 1 || ringC.ring.links.Exists(l => l.role == RingRole.Courier && l.pawnId == courierC.thingIDNumber)))
                    problems0.Add("ring C: a courier leaving left their link in the ring");
                bool dealerKnownC = Link(ringC, RingRole.Dealer).known;
                int entriesBeforeEnd = r.NewEntries.Count();
                g.story.Notify_Leaving(dealerC);
                string endLine = r.NewEntries.Skip(entriesBeforeEnd).Select(e => e.text).LastOrDefault() ?? "";
                if (!ringC.done || endLine == "" || Mentions(endLine, "Zdealer3") != dealerKnownC)
                    problems0.Add($"ring C: the dealer leaving left the ring done {ringC.done}, and the line about it {(Mentions(endLine, "Zdealer3") ? "names" : "leaves out")} a dealer the warden {(dealerKnownC ? "knew" : "did not know")}: \"{endLine}\"");
                g.story.arcs.Remove(ringC);
                Retire(dealerC, courierC);
                log += ", turned members";

                // ================================ D: a visitor caught, a letter opened ================================
                Pawn dealerD = Mk("Zdealer4", saints, isDealer: true), courierD = Mk("Zcourier5", saints);
                StoryArc ringD = RingSystem.Form(dealerD, map);
                RingLink cD = ringD == null ? null : RingSystem.Recruit(ringD, map, force: true);
                RingSystem.Notify_BroughtIn(courierD, "cousin Mira");
                if (ringD == null || cD == null || Link(ringD, RingRole.Relative) == null)
                {
                    problems0.Add($"ring D: a ring with a courier and a supplier did not form ({ringD != null}, {cD != null})");
                    return;
                }
                float heatD0 = ringD.ring.heat;
                // Someone else's relative caught with something for the courier: the courier is found, the supplier of the ring is not given away.
                RingSystem.Notify_FoundOnWay(courierD, "mother Other", "visit");
                bool courierOnly = Link(ringD, RingRole.Courier).known && !Link(ringD, RingRole.Relative).known && !Link(ringD, RingRole.Dealer).known && Mathf.Approximately(ringD.ring.heat, heatD0 + RingSystem.HeatPerFind);
                // The supplier's own letter opened: the supplier too.
                RingSystem.Notify_FoundOnWay(courierD, "cousin Mira", "post");
                bool supplierToo = Link(ringD, RingRole.Relative).known && !Link(ringD, RingRole.Dealer).known && Mentions(Told(), "Mira");
                if (!courierOnly || !supplierToo || !r.NewEntries.Any(e => Mentions(e.text, "Mira") && e.text.Contains("RP_RingHow_post".Translate().ToString())) || r.NewEntries.Any(e => Mentions(e.text, "Zdealer4")))
                    problems0.Add($"ring D: a find on the way in left the courier known alone ({courierOnly}) and then the supplier named on a letter ({supplierToo}), or the dealer named");
                log += ", visitor and post";
                g.story.arcs.Remove(ringD);
                Retire(dealerD, courierD);

                RPLog.Message($"[RimPrison:test] ring: {log}; {g.stats.ringLinksFound} links found, {g.stats.ringsBusted} rings broken whole and {g.stats.ringsPartial} in part, {g.stats.ringMembersTurned} members turned, {pm.finds.Count} marks on the map of finds");
            });
        }

        // ---- save/load: the round trip of the ring state -------------------------------------------------------------------------

        private static int ringSeedArcId = -1, ringSeedDealerId = -1;
        private static bool ringSeedAddedRep;
        private static int ringSeedBustTick, ringSeedBustWas;
        private static string ringSeedDescribed, ringSeedFinds;
        private static List<ContrabandFind> ringSeedMarks;
        private static (int busted, int partial, int found, int turned, int today) ringSeedStatsWas;

        /// <summary>A ring around a real inmate with all four roles, a link found, one turned, one taken out, heat, its spacing, and three marks on the map of finds; and the counters.</summary>
        private static void SeedRingForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
            Pawn dealer = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true } c && !RingSystem.IsDealer(c) && RingSystem.ArcOfDealer(x) == null);
            Pawn mate = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x != dealer && x.Inmate() != null);
            if (dealer == null || mate == null)
            {
                problems0.Add("saveload: no two inmates to seed a ring with");
                return;
            }
            dealer.Inmate().reputations.Add(dealerRep);
            ringSeedAddedRep = true;
            StoryArc arc = RingSystem.Form(dealer, map, ignoreLimit: true);
            if (arc == null)
            {
                dealer.Inmate().reputations.Remove(dealerRep);
                problems0.Add("saveload: the seeded ring could not be formed");
                return;
            }
            int t = Find.TickManager.TicksGame;
            arc.ring.links.RemoveAll(l => l.role != RingRole.Dealer);
            arc.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = mate.thingIDNumber, name = mate.LabelShort, known = true, carried = 3 });
            arc.ring.links.Add(new RingLink { role = RingRole.Relative, pawnId = dealer.thingIDNumber, name = "cousin Seedling", known = false });
            arc.ring.links.Add(new RingLink { role = RingRole.Relative, pawnId = mate.thingIDNumber, name = RingSystem.GardenSource, known = true, garden = true });
            arc.ring.links.Add(new RingLink { role = RingRole.Officer, pawnId = 424242, name = "Seedofficer", known = true, turned = true });
            arc.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = 535353, name = "Seedgone", known = true, handled = true, handledTick = t - 777 });
            (arc.ring.heat, arc.ring.lastAskTick, arc.ring.nextRecruitTick) = (42.5f, t - 1234, t + 4321);
            ringSeedArcId = arc.id;
            ringSeedDealerId = dealer.thingIDNumber;
            ringSeedBustWas = dealer.Inmate().ringBustedTick;
            ringSeedBustTick = dealer.Inmate().ringBustedTick = t - 888;
            ringSeedDescribed = arc.ring.Describe();
            ringSeedMarks = pm.finds;
            pm.finds = pm.finds.ToList();
            pm.finds.Add(new ContrabandFind { cell = new IntVec3(11, 0, 12), kind = ContrabandKind.Drug, source = FindSource.Stash, tick = t - 3000 });
            pm.finds.Add(new ContrabandFind { cell = new IntVec3(21, 0, 22), kind = ContrabandKind.Weapon, source = FindSource.Visitor, tick = t - 2000 });
            pm.finds.Add(new ContrabandFind { cell = new IntVec3(31, 0, 32), kind = ContrabandKind.Phone, source = FindSource.Raid, tick = t - 1000 });
            ringSeedFinds = string.Join(",", pm.finds.Select(f => f.Describe()));
            ringSeedStatsWas = (g.stats.ringsBusted, g.stats.ringsPartial, g.stats.ringLinksFound, g.stats.ringMembersTurned, g.today.ringsBusted);
            (g.stats.ringsBusted, g.stats.ringsPartial, g.stats.ringLinksFound, g.stats.ringMembersTurned, g.today.ringsBusted) = (3, 2, 9, 1, 1);
            RPLog.Message($"[RimPrison:test] saveload ring: {dealer.LabelShort}'s ring {ringSeedDescribed}; {pm.finds.Count} marks on the map of finds");
        }

        /// <summary>The seeded ring and marks came back whole, still the same ring, and the dealer still a dealer; then they go.</summary>
        private static void CheckRingAfterReload(Map map)
        {
            if (ringSeedArcId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            StoryArc arc = g.story.arcs.FirstOrDefault(a => a.id == ringSeedArcId && a.def == RPRingDefOf.RP_Arc_SmugglingRing);
            string after = arc?.ring?.Describe() ?? "(gone)";
            Pawn seeded = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == ringSeedDealerId);
            bool ringWhole = after == ringSeedDescribed && arc.A?.thingIDNumber == ringSeedDealerId && !arc.def.occupiesCast && seeded?.Inmate()?.ringBustedTick == ringSeedBustTick;
            string marks = string.Join(",", pm.finds.Select(f => f.Describe()));
            bool marksWhole = marks == ringSeedFinds;
            bool statsWhole = (g.stats.ringsBusted, g.stats.ringsPartial, g.stats.ringLinksFound, g.stats.ringMembersTurned, g.today.ringsBusted) == (3, 2, 9, 1, 1);
            RPLog.Message($"[RimPrison:test] saveload ring: {(ringWhole && marksWhole && statsWhole ? "intact" : "changed")} ({after}; {pm.finds.Count} marks)");
            if (!ringWhole || !marksWhole || !statsWhole)
                reloadProblems.Add($"the ring state changed in the round trip: ring {ringSeedDescribed} -> {after}; marks {ringSeedFinds} -> {marks}; counters whole {statsWhole}; the dealer's bust tick {ringSeedBustTick} -> {seeded?.Inmate()?.ringBustedTick}");
            if (arc != null) g.story.arcs.Remove(arc);
            Pawn dealer = seeded;
            if (dealer?.Inmate() != null) dealer.Inmate().ringBustedTick = ringSeedBustWas;
            if (ringSeedAddedRep && dealer?.Inmate() != null) dealer.Inmate().reputations.RemoveAll(rep => rep == DefDatabase<ReputationDef>.GetNamed("RP_Dealer"));
            pm.finds = ringSeedMarks ?? new List<ContrabandFind>();
            (g.stats.ringsBusted, g.stats.ringsPartial, g.stats.ringLinksFound, g.stats.ringMembersTurned, g.today.ringsBusted) = ringSeedStatsWas;
            ringSeedArcId = -1;
        }
    }
}
