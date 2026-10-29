using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the smuggling rings (findings 1-13; see <see cref="RingSystem"/>). Each fails without its fix, is its own method called once from
    /// FireAllEvents (right after the rings' own check, <see cref="RingTest"/>), works on throwaway inmates and staff numbered from 91000 (with names of their own, so that a
    /// line can be searched for them), runs no ticks, sets aside the real rings, inmates and guards while it runs and puts back what it touches (<see cref="Rollback"/>).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The events plan's entry: its own dice (the same every run, and the checks after it keep the game's stream), and nothing left behind on the map.</summary>
        private static void CheckRingRegressions(Map map)
        {
            Rand.PushState(20261021);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            try
            {
                CheckRingCourierHandsOverOnlyRingStock(map);
                CheckRingTipsComeAfterTunnelAndContrabandTips(map);
                CheckRingChainHidesHowManyAreLeft(map);
                CheckRingBrokenWholeIsNotFarmed(map);
                CheckRingBustedCourierIsNotRecruitedAgain(map);
                CheckRingBustRecordsOnlyWhatWasTaken(map);
                CheckRingCutOffDealerGetsNothingFromStaff(map);
                CheckRingFindsOverlayShowsOnlyRecentMarks(map);
                CheckRingLetterListsWhatABustTakesAndGoesStale(map);
                CheckRingTurnedOfficerIsReportedAtTheTurnedPace(map);
                CheckRingLookTargetsNeedAMap(map);
                CheckRingDealerFileAndChainAgree(map);
                CheckRingSmallerItems(map);
            }
            finally
            {
                Rand.PopState();
            }
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            if (leftBehind.Count > 0) problems0.Add($"ring regression checks left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map");
        }

        /// <summary>A quiet corner of the prison for a ring check: no real ring, no real inmate in play, no guard, camera or tower in sight, throwaway inmates and staff in the yard.</summary>
        private sealed class RingScene
        {
            public readonly Map map;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly PrisonMap pm;
            public readonly Rollback r;
            public readonly ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
            public readonly ThingDef drug = ContrabandUtility.AllContraband.First(d => ContrabandUtility.Ext(d).kind == ContrabandKind.Drug);
            public readonly GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
            private readonly List<IntVec3> yard;
            private int spot;

            public RingScene(Map map, Rollback r)
            {
                this.map = map;
                this.r = r;
                pm = PrisonMap.Of(map);
                List<IntVec3> all = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => c.Standable(map)).ToList();
                yard = all.Count == 0 ? all : all.OrderBy(c => c.DistanceToSquared(all[0])).Take(30).ToList();
                if (yard.Count < 6) throw new InvalidOperationException($"no yard to test in ({yard.Count} cells)");
                List<StoryArc> arcs0 = g.story.arcs;
                int nextArc0 = g.story.nextArcId;
                var lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                List<ContrabandFind> finds0 = pm.finds;
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(x => x != null).Select(x => (inmate: x, active: x.activeFlag)).ToList();
                var guards0 = pm.Guards.ToList();
                var cameras0 = pm.Cameras.ToList();
                var towers0 = pm.Towers.ToList();
                var gangStaff = StaffCommute.AllStaff(map).Select(x => x.GetComp<CompStaff>()).Where(x => x.patronGang != null).Select(x => (staff: x, gang: x.patronGang)).ToList();
                r.Add(() =>
                {
                    g.story.arcs = arcs0;
                    g.story.nextArcId = nextArc0;
                    g.story.lastArcStart = lastStart0;
                    pm.finds = finds0;
                    foreach ((CompInmate inmate, bool active) in real) inmate.activeFlag = active;
                    pm.Guards.Clear();
                    pm.Guards.AddRange(guards0);
                    pm.Cameras.Clear();
                    pm.Cameras.AddRange(cameras0);
                    pm.Towers.Clear();
                    pm.Towers.AddRange(towers0);
                    foreach ((CompStaff staff, GangDef gang) in gangStaff) staff.patronGang = gang;
                });
                // (Debts are set aside as well: a snitch's tip would name a real one, and make it known.)
                List<InmateDebt> debts0 = g.story.debts;
                r.Add(() => g.story.debts = debts0);
                g.story.debts = new List<InmateDebt>();
                g.story.arcs = arcs0.Where(a => a.def != RPRingDefOf.RP_Arc_SmugglingRing).ToList();
                pm.finds = new List<ContrabandFind>();
                foreach ((CompInmate inmate, bool _) in real) inmate.activeFlag = false;
                pm.Guards.Clear();
                pm.Cameras.Clear();
                pm.Towers.Clear();
                foreach ((CompStaff staff, GangDef _) in gangStaff) staff.patronGang = null;
            }

            public int Now => Find.TickManager.TicksGame;

            public void Place(Pawn p, int cell)
            {
                p.pather.StopDead();
                p.Position = yard[cell % yard.Count];
                p.Notify_Teleported();
            }

            /// <summary>A new inmate in the yard: no cash, no reputations (a dealer if meant), a gang if given.</summary>
            public Pawn Inmate(string name, GangDef gang = null, bool dealer = false)
            {
                Pawn p = Throwaway(map, r);
                p.Name = new NameSingle(name);
                CompInmate c = p.Inmate();
                (c.activeFlag, c.gang, c.gangKnown, c.cash, c.tension, c.fear, c.want, c.snitchExposed, c.lastDealerId, c.ringBustedTick) = (true, gang, false, 0, 20f, 0f, null, false, -1, RingSystem.NeverBusted);
                c.reputations.Clear();
                c.revealed.Clear();
                // (A generated inmate may arrive addicted: they would buy from the dealer, and no ring takes on a customer who is.)
                ClearAddictions(p);
                if (dealer) c.reputations.Add(dealerRep);
                Place(p, spot++);
                return p;
            }

            /// <summary>A corrupt guard on the payroll of this inmate, all-work timetable and full rest (awake, and on site).</summary>
            public Pawn Officer(string name, Pawn patron)
            {
                Pawn s = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Name = new NameSingle(name);
                if (s.timetable != null)
                    for (int hour = 0; hour < 24; hour++) s.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                if (s.needs?.rest != null) s.needs.rest.CurLevel = 1f;
                CompStaff sc = s.GetComp<CompStaff>();
                (sc.corrupt, sc.patron, sc.patronGang, sc.integrity, sc.suspicion, sc.doubleAgent, sc.exposedCorrupt) = (true, patron, null, 20f, 0f, false, false);
                sc.evidence.Clear();
                return s;
            }

            public StoryArc Form(Pawn dealer) => RingSystem.Form(dealer, map);

            /// <summary>The courier a ring takes on (the dealer's customer or gang mate): the one candidate there is.</summary>
            public RingLink Recruit(StoryArc ring)
            {
                ring.ring.nextRecruitTick = 0;
                return RingSystem.Recruit(ring, map, force: true);
            }

            public RingLink Link(StoryArc a, RingRole role) => a.ring.links.Find(l => l.role == role);

            public int Known(StoryArc a) => a.ring.links.Count(l => l.known);

            /// <summary>The warden learns this inmate's own link (a session with the psychologist).</summary>
            public void Assess(Pawn inmate) => RingSystem.Notify_Assessed(inmate, 1f);

            /// <summary>The ring's cut is over and the dealer, still a dealer, has a new ring (as at midnight: the old one ends, then the next day a new one forms).</summary>
            public StoryArc NextRing(StoryArc old, Pawn dealer)
            {
                foreach (RingLink l in old.ring.links) l.handledTick -= (RingSystem.CutDays + 1) * GenDate.TicksPerDay;
                AgeTheSupplyCuts(map);
                old.ring.brokenUntilTick = Now - 1;
                RingSystem.Daily(map, only: dealer);
                RingSystem.Daily(map, only: dealer);
                return RingSystem.ArcOfDealer(dealer);
            }

            public IEnumerable<string> Told() => r.NewEntries.Select(e => e.text).Concat(r.NewLetters.Select(l => l.Label.ToString() + " " + ((l as ChoiceLetter)?.Text.ToString() ?? "")));

            public ChoiceLetter_PrisonDecision AskedAbout(StoryArc a) => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.arcId == a.id);

            public static void Fail(string text) => problems0.Add("ring regression: " + text);
        }

        // ---- 1: a courier hands the dealer only what came in through the ring ---------------------------------------------------------------------

        /// <summary>
        /// A courier who is the dealer's customer holds what they bought; the old handover gave "the first drug in their pockets" back, so the dealer sold it again. Now only what a
        /// visit, the post or an officer brought for them goes back (<see cref="RingLink.carried"/>), and a customer who is an addict is not taken on as a courier.
        /// </summary>
        private static void CheckRingCourierHandsOverOnlyRingStock(Map map)
        {
            Guarded("ring regression: a courier hands over only what came in through the ring", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer11", dealer: true), courier = s.Inmate("Zcourier11");
                CompInmate dc = dealer.Inmate(), cc = courier.Inmate();
                cc.lastDealerId = dealer.thingIDNumber;
                StoryArc ring = s.Form(dealer);
                RingLink link = s.Recruit(ring);
                if (link == null || link.pawnId != courier.thingIDNumber)
                {
                    RingScene.Fail($"the dealer's customer was not taken on as a courier ({link?.Describe() ?? "nobody"})");
                    return;
                }
                // The customer buys from the dealer, as they do hourly: that is theirs.
                s.Place(dealer, 1);
                s.Place(courier, 2);
                cc.cash = ReputationBehavior_Dealer.Price;
                cc.tension = 50f;
                ContrabandUtility.Give(dealer, s.drug);
                bool bought = ((ReputationBehavior_Dealer)s.dealerRep.Behavior).Deal(dealer, dc, s.pm) && courier.inventory.innerContainer.Any(t => t.def == s.drug);
                if (!bought)
                {
                    RingScene.Fail("the dealer's sale to the courier did not happen, so the check has nothing to look at");
                    return;
                }
                int dealerHad = RingSystem.DrugsCarried(dealer);
                bool handedBack = RingSystem.TryHandover(ring, map, force: true);
                if (handedBack || RingSystem.DrugsCarried(dealer) != dealerHad || RingSystem.DrugsCarried(courier) != 1)
                    RingScene.Fail($"a courier handed the dealer the drug they had just bought (handed over {handedBack}, the dealer holds {RingSystem.DrugsCarried(dealer)} of the {dealerHad} they had, the courier {RingSystem.DrugsCarried(courier)})");
                // A visit then brings stock for the courier: that much, and only that, goes to the dealer (the bought one stays).
                int delivery = ContrabandUtility.DeliveryCount(s.drug);
                ContrabandUtility.Give(courier, s.drug);
                RingSystem.Notify_BroughtIn(courier, "cousin Zsup11", s.drug);
                int courierHad = RingSystem.DrugsCarried(courier);
                bool passed = RingSystem.TryHandover(ring, map, force: true);
                if (!passed || RingSystem.DrugsCarried(dealer) != dealerHad + delivery || RingSystem.DrugsCarried(courier) != courierHad - delivery || link.carried != 0)
                    RingScene.Fail($"a courier with {delivery} from a visit and one they bought handed over {(passed ? "" : "nothing, or ")}{RingSystem.DrugsCarried(dealer) - dealerHad} (expected {delivery}) and kept {RingSystem.DrugsCarried(courier)} (expected {courierHad - delivery}), still carrying {link.carried} for the ring");
                // A guard on the dealer's payroll brings it: counted too, and not for a ring's non-courier.
                RingSystem.Notify_Delivered(courier, s.drug);
                RingSystem.Notify_Delivered(dealer, s.drug);
                if (link.carried != delivery || s.Link(ring, RingRole.Dealer).carried != 0)
                    RingScene.Fail($"a delivery by an officer counted {link.carried} for the courier (expected {delivery}) and {s.Link(ring, RingRole.Dealer).carried} for the dealer (expected none)");
                // A customer who is an addict is not taken on, though a courier's place is free.
                Pawn addict = s.Inmate("Zaddict11");
                addict.Inmate().lastDealerId = dealer.thingIDNumber;
                HediffDef addiction = DefDatabase<HediffDef>.AllDefsListForReading.FirstOrDefault(d => typeof(Hediff_Addiction).IsAssignableFrom(d.hediffClass));
                if (addiction == null)
                {
                    RingScene.Fail("no addiction hediff to test with");
                    return;
                }
                Hediff added = HediffMaker.MakeHediff(addiction, addict);
                addict.health.AddHediff(added);
                r.Add(() => addict.health.RemoveHediff(added));
                RingLink second = s.Recruit(ring);
                if (second != null) RingScene.Fail($"the ring took on {second.name}, a customer of the dealer who is an addict, as a courier");
            });
        }

        // ---- 2: ring tips rank after tunnel and contraband tips ------------------------------------------------------------------------------------

        /// <summary>Ring tips were asked before the tunnel and contraband tips: a snitch next to a dealer's courier named the courier half the time and let the tunnel go. Now the ring is told after them.</summary>
        private static void CheckRingTipsComeAfterTunnelAndContrabandTips(Map map)
        {
            Guarded("ring regression: ring tips come after tunnel and contraband tips", map, r =>
            {
                var s = new RingScene(map, r);
                // (Nobody else near has anything to tell, and no member of staff is on the take: as the economy check of debt tips does it.)
                var others = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null)
                    .Select(c => (c, c.tunnelKnown, c.suspected, c.tunnelProgress, c.cellSearchPending, c.informedBy, c.gang, c.gangKnown)).ToList();
                var staff = map.mapPawns.FreeColonistsSpawned.Select(x => x.GetComp<CompStaff>()).Where(c => c != null && !c.doubleAgent).ToList();
                try
                {
                    foreach ((CompInmate c, bool _, bool _, float _, bool _, Pawn _, GangDef _, bool _) in others) (c.tunnelKnown, c.suspected, c.gang) = (true, true, null);
                    foreach (CompStaff c in staff) c.doubleAgent = true;
                    Pawn snitch = s.Inmate("Zsnitch12"), digger = s.Inmate("Zdigger12"), carrier = s.Inmate("Zcarrier12"), dealer = s.Inmate("Zdealer12", dealer: true), customer = s.Inmate("Zcustomer12");
                    CompInmate sc = snitch.Inmate(), dc = digger.Inmate(), kc = carrier.Inmate();
                    customer.Inmate().lastDealerId = dealer.thingIDNumber;
                    StoryArc ring = s.Form(dealer);
                    s.Recruit(ring);
                    RingSystem.Notify_BroughtIn(dealer, "cousin Zsup12");
                    if (ring.ring.links.Count != 3 || s.Known(ring) != 0)
                    {
                        RingScene.Fail($"the ring had {ring.ring.links.Count} links and {s.Known(ring)} known, not three and none");
                        return;
                    }
                    // A tunnel being dug nearby: the tunnel, every time (the old order gave the ring half of them).
                    string tunnel = "RP_TipTunnel".Translate(digger.LabelShort).ToString();
                    var tunnelTips = new List<string>();
                    for (int i = 0; i < 30; i++)
                    {
                        (dc.tunnelProgress, dc.tunnelKnown, dc.cellSearchPending, dc.informedBy) = (0.5f, false, false, null);
                        tunnelTips.Add(InformantSystem.TryTip(snitch, sc));
                    }
                    if (tunnelTips.Any(t => t != tunnel) || s.Known(ring) != 0)
                        RingScene.Fail($"with a tunnel being dug next door the snitch's tips were {string.Join(" | ", tunnelTips.Distinct())} (the tunnel's, 30 times, expected), and {s.Known(ring)} links of the ring were told");
                    // Contraband in someone's pockets: that too.
                    (dc.tunnelProgress, dc.tunnelKnown) = (0f, true);
                    ContrabandUtility.Give(carrier, s.drug);
                    string contraband = "RP_TipContraband".Translate(carrier.LabelShort).ToString();
                    var contrabandTips = new List<string>();
                    for (int i = 0; i < 30; i++)
                    {
                        (kc.suspected, kc.informedBy) = (false, null);
                        contrabandTips.Add(InformantSystem.TryTip(snitch, sc));
                    }
                    if (contrabandTips.Any(t => t != contraband) || s.Known(ring) != 0)
                        RingScene.Fail($"with contraband in someone's pockets the snitch's tips were {string.Join(" | ", contrabandTips.Distinct())} (the contraband's, 30 times, expected), and {s.Known(ring)} links of the ring were told");
                    // Nothing else to tell: the ring.
                    kc.suspected = true;
                    int tries = 0;
                    string ringTip = null;
                    while (ringTip == null && tries++ < 80) ringTip = InformantSystem.TryTip(snitch, sc);
                    if (ringTip == null || s.Known(ring) != 1)
                        RingScene.Fail($"with nothing else to tell the snitch told {(ringTip == null ? "nothing in 80 tips" : "\"" + ringTip + "\"")} and {s.Known(ring)} links of the ring are known (one expected)");
                }
                finally
                {
                    foreach (CompStaff c in staff) c.doubleAgent = false;
                    foreach ((CompInmate c, bool known, bool suspected, float progress, bool pending, Pawn informed, GangDef gang, bool gangKnown) in others)
                        (c.tunnelKnown, c.suspected, c.tunnelProgress, c.cellSearchPending, c.informedBy, c.gang, c.gangKnown) = (known, suspected, progress, pending, informed, gang, gangKnown);
                }
            });
        }

        // ---- 3: the chain does not count what is left -----------------------------------------------------------------------------------------------

        /// <summary>The chain drew a "?" per unknown link, so a guard corrupted overnight, a search that failed or a courier released showed on the Intel tab. Now: the known links and one marker.</summary>
        private static void CheckRingChainHidesHowManyAreLeft(Map map)
        {
            Guarded("ring regression: the chain does not count the links still to find", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer13", dealer: true), courier = s.Inmate("Zcourier13");
                courier.Inmate().lastDealerId = dealer.thingIDNumber;
                // (A guard on the dealer's payroll is a link nobody has found: so the ring is not just its dealer.)
                s.Officer("Zofficer13", dealer);
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                string more = "RP_IntelRingMore".Translate().ToString();
                string Chain() => string.Join(" > ", RingSystem.Chain(ring).Select(p => p.text));
                string before = Chain();
                if (before != "Zdealer13 > " + more) RingScene.Fail($"the chain of a ring with the dealer found reads \"{before}\" (the dealer and the one marker expected)");
                // Out of the warden's sight: a courier joins, a search at the door fails, a guard is bought, the courier goes.
                var events = new List<(string what, string chain)>();
                s.Recruit(ring);
                events.Add(("a courier joined", Chain()));
                RingSystem.Notify_BroughtIn(courier, "cousin Zsup13");
                events.Add(("a delivery got past the search", Chain()));
                s.Officer("Zofficer13b", dealer);
                RingSystem.Sync(ring, map);
                events.Add(("a guard took the dealer's money", Chain()));
                LeaveStory(courier);
                events.Add(("the courier left", Chain()));
                foreach ((string what, string chain) in events)
                    if (chain != before) RingScene.Fail($"the chain read \"{chain}\" after {what} (it read \"{before}\" before: nothing the warden can see changed)");
                // Everything found: no marker.
                for (int i = 0; i < 8 && RingSystem.HasUnknown(ring); i++) RingSystem.Notify_Tapped(dealer, force: true);
                if (RingSystem.HasUnknown(ring) || Chain().Contains(more) || !RingSystem.Chain(ring).All(p => p.Known))
                    RingScene.Fail($"a ring with every link found still shows a marker: \"{Chain()}\" ({ring.ring.links.Count(l => !l.known)} unknown)");
                // No count in the words either.
                if (!"RP_IntelRingUnknownTip".Translate().ToString().All(ch => !char.IsDigit(ch)) || !more.All(ch => !char.IsDigit(ch)))
                    RingScene.Fail("the marker or its tooltip carries a number");
            });
        }

        /// <summary>The cut a bust put on the inmates it took out (kept on the inmate, <see cref="CompInmate.supplyCutUntilTick"/>) has run out, as the links' own clocks are moved back to say.</summary>
        private static void AgeTheSupplyCuts(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (p.Inmate() is { supplyCutUntilTick: >= 0 } c) c.supplyCutUntilTick -= (RingSystem.CutDays + 2) * GenDate.TicksPerDay;
        }

        private static void LeaveStory(Pawn p) => PrisonGame.Instance.story.Notify_Leaving(p);

        // ---- 4: breaking a ring whole is not farmed ------------------------------------------------------------------------------------------------

        /// <summary>
        /// A ring that was only its dealer was "broken whole" once the dealer was found; the dealer started again, was found again, and each time paid +20 to the security grade.
        /// Now a whole ring has a link besides the dealer, a dealer taken out less than ten days ago pays nothing again, a new ring around a dealer taken out before has them found, and the
        /// letter does not say "together" of one person nor "for good".
        /// </summary>
        private static void CheckRingBrokenWholeIsNotFarmed(Map map)
        {
            Guarded("ring regression: a ring broken whole is not farmed", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonGame g = s.g;
                // (a) A dealer on their own: found, busted, nothing whole about it.
                Pawn lone = s.Inmate("Zdealer14", dealer: true);
                StoryArc loneRing = s.Form(lone);
                s.Assess(lone);
                int whole0 = g.stats.ringsBusted, today0 = g.today.ringsBusted, partial0 = g.stats.ringsPartial;
                int taken = RingSystem.Bust(loneRing, map);
                if (taken != 1 || g.stats.ringsBusted != whole0 || g.today.ringsBusted != today0 || g.stats.ringsPartial != partial0 + 1
                    || r.NewLetters.Any(l => l.Label == "RP_RingBrokenLabel".Translate().ToString()) || r.NewEntries.Any(e => e.importance == 3 && e.text.Contains("Zdealer14")))
                    RingScene.Fail($"a ring that was only its dealer, busted, took out {taken} and was counted as broken whole {g.stats.ringsBusted - whole0} (today {g.today.ringsBusted - today0}) and in part {g.stats.ringsPartial - partial0} (none, none, one expected), or sent the letter or made a milestone");
                // (b) A dealer taken out before: the next ring has them found; busted again within ten days it is no milestone, after ten days it is.
                Pawn dealer = s.Inmate("Zdealer15", dealer: true), courier = s.Inmate("Zcourier15");
                courier.Inmate().lastDealerId = dealer.thingIDNumber;
                CompInmate dc = dealer.Inmate();
                StoryArc ring = s.Form(dealer);
                s.Recruit(ring);
                s.Assess(dealer);
                s.Assess(courier);
                whole0 = g.stats.ringsBusted;
                if (RingSystem.Bust(ring, map) != 2 || g.stats.ringsBusted != whole0 + 1 || dc.ringBustedTick != s.Now)
                    RingScene.Fail($"a ring of a dealer and a courier, both found, was not broken whole ({g.stats.ringsBusted - whole0} counted, bust tick {dc.ringBustedTick} for now {s.Now})");
                StoryArc again = s.NextRing(ring, dealer);
                if (again == null || again == ring || !s.Link(again, RingRole.Dealer).known || s.Known(again) != 1 || !dc.KnownReputation(s.dealerRep))
                    RingScene.Fail($"the new ring around a dealer the warden took out has {(again == null ? "not formed" : s.Known(again) + " links known")} and their file {(dc.KnownReputation(s.dealerRep) ? "shows" : "does not show")} the trade (the dealer known and shown expected)");
                if (again == null) return;
                s.Recruit(again);
                s.Assess(courier);
                dc.ringBustedTick -= 6 * GenDate.TicksPerDay;
                whole0 = g.stats.ringsBusted;
                partial0 = g.stats.ringsPartial;
                int today1 = g.today.ringsBusted;
                RingSystem.Bust(again, map);
                if (g.stats.ringsBusted != whole0 || g.today.ringsBusted != today1 || g.stats.ringsPartial != partial0 + 1)
                    RingScene.Fail($"the same dealer taken out again six days on was a milestone again (whole {g.stats.ringsBusted - whole0}, today {g.today.ringsBusted - today1}, in part {g.stats.ringsPartial - partial0}: none, none, one expected)");
                // (c) Ten days on: it counts.
                StoryArc third = s.NextRing(again, dealer);
                if (third == null) return;
                s.Recruit(third);
                s.Assess(courier);
                dc.ringBustedTick = s.Now - (RingSystem.RepeatBonusDays + 1) * GenDate.TicksPerDay;
                whole0 = g.stats.ringsBusted;
                RingSystem.Bust(third, map);
                if (g.stats.ringsBusted != whole0 + 1) RingScene.Fail($"a dealer taken out {RingSystem.RepeatBonusDays + 1} days before was no milestone ({g.stats.ringsBusted - whole0} counted)");
                // (d) Words: not "for good" (a dealer starts another), and "was taken out" of one person.
                string confirm = "RP_IntelRingBustConfirmWhole".Translate("X").ToString();
                if (confirm.Contains("for good")) RingScene.Fail($"the bust confirmation still says the ring is broken for good: \"{confirm}\"");
                Pawn d2 = s.Inmate("Zdealer16", dealer: true), c2 = s.Inmate("Zcourier16");
                c2.Inmate().lastDealerId = d2.thingIDNumber;
                StoryArc ring2 = s.Form(d2);
                s.Recruit(ring2);
                s.Assess(c2);
                RingSystem.Bust(ring2, map);
                // Only the courier was found: that was in part. The dealer is found, and the dealer alone is taken out with the ring already in pieces.
                s.Assess(d2);
                int lettersBefore = r.NewLetters.Count();
                RingSystem.Bust(ring2, map);
                Letter broken = r.NewLetters.Skip(lettersBefore).FirstOrDefault(l => l.Label == "RP_RingBrokenLabel".Translate().ToString());
                string text = (broken as ChoiceLetter)?.Text.ToString() ?? "";
                if (broken == null || text.Contains("were taken out") || !text.Contains("Zdealer16 was taken out"))
                    RingScene.Fail($"the letter for a ring broken by taking out one person reads \"{text}\" (or was not sent: {broken == null}); \"Zdealer16 was taken out\" expected");
            });
        }

        // ---- 5: a busted courier is not taken on again ----------------------------------------------------------------------------------------

        /// <summary>A courier taken out by a partial bust was a candidate again two days later (their link no longer counted) and still under the supply cut.</summary>
        private static void CheckRingBustedCourierIsNotRecruitedAgain(Map map)
        {
            Guarded("ring regression: a busted courier is not taken on again", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer17", s.ravens, dealer: true), other = s.Inmate("Zdealer18", s.ravens, dealer: true), courier = s.Inmate("Zcourier17", s.ravens);
                StoryArc ring = s.Form(dealer), ring2 = s.Form(other);
                RingLink link = s.Recruit(ring);
                if (link == null || link.pawnId != courier.thingIDNumber)
                {
                    RingScene.Fail($"the gang mate was not taken on as the courier ({link?.Describe()})");
                    return;
                }
                s.Assess(courier);
                if (RingSystem.Bust(ring, map) != 1) RingScene.Fail("the courier found was not taken out");
                RingLink back = s.Recruit(ring);
                if (back != null) RingScene.Fail($"the ring took {back.name}, taken out of it two days ago, on as a courier again");
                // Another ring does not take on someone who is cut off either.
                RingLink elsewhere = s.Recruit(ring2);
                if (elsewhere != null) RingScene.Fail($"another ring took {elsewhere.name} on while they are cut off from every way in");
            });
        }

        // ---- 6: only what was taken is on the record -----------------------------------------------------------------------------------------

        /// <summary>A bust wrote possession (and dealing) up "with evidence" on every link, also on one known from a tip or a session who carried nothing.</summary>
        private static void CheckRingBustRecordsOnlyWhatWasTaken(Map map)
        {
            Guarded("ring regression: a bust puts on record only what it took", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer19", dealer: true), courier = s.Inmate("Zcourier19");
                courier.Inmate().lastDealerId = dealer.thingIDNumber;
                CompInmate dc = dealer.Inmate(), cc = courier.Inmate();
                StoryArc ring = s.Form(dealer);
                s.Recruit(ring);
                s.Assess(dealer);
                s.Assess(courier);
                int dealerRecords = dc.records.Count, courierRecords = cc.records.Count;
                // Empty-handed: nothing to write up.
                int taken = RingSystem.Bust(ring, map);
                if (taken != 2 || dc.records.Count != dealerRecords || cc.records.Count != courierRecords)
                    RingScene.Fail($"a bust that found nothing on two links put {dc.records.Count - dealerRecords + cc.records.Count - courierRecords} entries on record (none expected; {taken} taken out)");
                // With drugs in their pockets: on record.
                Pawn d2 = s.Inmate("Zdealer20", dealer: true), c2 = s.Inmate("Zcourier20");
                c2.Inmate().lastDealerId = d2.thingIDNumber;
                StoryArc ring2 = s.Form(d2);
                s.Recruit(ring2);
                s.Assess(d2);
                s.Assess(c2);
                ContrabandUtility.Give(d2, s.drug);
                ContrabandUtility.Give(c2, s.drug);
                int records2 = d2.Inmate().records.Count, recordsC2 = c2.Inmate().records.Count;
                RingSystem.Bust(ring2, map);
                if (d2.Inmate().records.Count <= records2 || c2.Inmate().records.Count <= recordsC2)
                    RingScene.Fail("a bust that took drugs from the dealer and the courier did not put them on record");
            });
        }

        // ---- 7: the supply cut stops staff deliveries ----------------------------------------------------------------------------------------

        /// <summary>A corrupt guard went on delivering to a dealer who was cut off from every way in, though the letter said nothing comes in.</summary>
        private static void CheckRingCutOffDealerGetsNothingFromStaff(Map map)
        {
            Guarded("ring regression: the supply cut stops a guard's delivery", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer21", dealer: true);
                Pawn officer = s.Officer("Zofficer21", dealer);
                CompStaff sc = officer.GetComp<CompStaff>();
                StoryArc ring = s.Form(dealer);
                s.Place(dealer, 1);
                s.Place(officer, 2);
                if (officer.GetRoom() != dealer.GetRoom())
                {
                    RingScene.Fail("the guard and the dealer are not in one room in the yard");
                    return;
                }
                int Deliveries() => RingSystem.DrugsCarried(dealer);
                bool Hours(int n)
                {
                    for (int i = 0; i < n; i++)
                    {
                        sc.carryingToday = true;
                        CorruptionSystem.Hourly(officer, sc);
                        if (Deliveries() > 0) return true;
                    }
                    return false;
                }
                // (As a control, the same guard and dealer without the cut: the delivery is made.)
                bool control = Hours(80);
                if (!control)
                {
                    RingScene.Fail("a corrupt guard with the dealer in reach made no delivery in 80 hours, so the check has nothing to look at");
                    return;
                }
                foreach (Thing t in dealer.inventory.innerContainer.ToList()) t.Destroy();
                RingLink dealerLink = s.Link(ring, RingRole.Dealer);
                (dealerLink.handled, dealerLink.handledTick) = (true, s.Now);
                bool cutDelivery = Hours(80);
                if (cutDelivery || !RingSystem.SupplyCut(dealer)) RingScene.Fail($"a corrupt guard delivered to a dealer taken out of their ring lately (delivered {cutDelivery}, cut {RingSystem.SupplyCut(dealer)})");
            });
        }

        // ---- 8: old marks are not drawn -----------------------------------------------------------------------------------------------------

        /// <summary>The finds are pruned only when the next is made, and the overlay drew an expired mark faintly; now a find older than its thirty days is not drawn.</summary>
        private static void CheckRingFindsOverlayShowsOnlyRecentMarks(Map map)
        {
            Guarded("ring regression: the map of finds shows only the last thirty days", map, r =>
            {
                int now = Find.TickManager.TicksGame;
                var recent = new ContrabandFind { cell = IntVec3.Zero, kind = ContrabandKind.Drug, source = FindSource.Raid, tick = now - (PrisonMap.FindsDays - 1) * GenDate.TicksPerDay };
                var expired = new ContrabandFind { cell = IntVec3.Zero, kind = ContrabandKind.Drug, source = FindSource.Raid, tick = now - (PrisonMap.FindsDays + 1) * GenDate.TicksPerDay };
                var fresh = new ContrabandFind { cell = IntVec3.Zero, kind = ContrabandKind.Drug, source = FindSource.Raid, tick = now };
                if (!FindsOverlay.Shown(recent, now) || !FindsOverlay.Shown(fresh, now) || FindsOverlay.Shown(expired, now))
                    RingScene.Fail($"the map of finds shows a mark 29 days old {FindsOverlay.Shown(recent, now)}, a new one {FindsOverlay.Shown(fresh, now)} and one 31 days old {FindsOverlay.Shown(expired, now)} (yes, yes, no expected)");
            });
        }

        // ---- 9: the letter lists what a bust takes, and is dead once the ring has been busted ----------------------------------------------

        /// <summary>The letter listed every known link (also those already taken out) under "every link above is taken out", and went on offering a bust after the ring was busted from the Intel tab.</summary>
        private static void CheckRingLetterListsWhatABustTakesAndGoesStale(Map map)
        {
            Guarded("ring regression: the ring's letter lists what a bust takes and goes stale", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer22", dealer: true), live = s.Inmate("Zlive22"), gone = s.Inmate("Zgone22");
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                ring.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = live.thingIDNumber, name = "Zlive22", known = true });
                ring.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = gone.thingIDNumber, name = "Zgone22", known = true, handled = true, handledTick = s.Now - 1000 });
                ring.ring.links.Add(new RingLink { role = RingRole.Officer, pawnId = -1, name = "Zturned22", known = true, turned = true });
                if (!RingSystem.MaybeAsk(ring, map, force: true))
                {
                    RingScene.Fail("the ring did not put its case to the warden");
                    return;
                }
                ChoiceLetter_PrisonDecision ask = s.AskedAbout(ring);
                string text = ask?.Text.ToString() ?? "";
                if (ask == null || !Mentions(text, "Zdealer22") || !Mentions(text, "Zlive22") || Mentions(text, "Zgone22") || Mentions(text, "Zturned22") || ask.pawns.Contains(gone))
                    RingScene.Fail($"the letter lists links a bust does not take out (dealer {Mentions(text, "Zdealer22")}, live courier {Mentions(text, "Zlive22")}, courier taken out already {Mentions(text, "Zgone22")}, turned officer {Mentions(text, "Zturned22")}; the first two only expected)");
                if (ask == null) return;
                // The warden busts it from the Intel tab; the letter, still open, must not offer it again.
                RingSystem.Bust(ring, map);
                // (A link found since: a bust would have something to take again, if the ring were not already over.)
                ring.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = live.thingIDNumber, name = "Zlater22", known = true });
                IReadOnlyList<string> options = ask.decision.Worker.Options(ask);
                var offered = new List<int>();
                for (int i = 0; i < options.Count; i++)
                    if (ask.decision.Worker.Disabled(ask, i) == null) offered.Add(i);
                // (Only waiting is left, and choosing a bust does nothing.)
                int busted0 = s.g.stats.ringsBusted, partial0 = s.g.stats.ringsPartial;
                ask.decision.Worker.Choose(ask, map, 0);
                if (offered.Any(i => i != 1) || s.g.stats.ringsBusted != busted0 || s.g.stats.ringsPartial != partial0)
                    RingScene.Fail($"after the ring was busted its old letter still offers options {string.Join(",", offered)} (only 1, waiting, expected)");
            });
        }

        // ---- 10: a turned officer tells at the pace of the turned -----------------------------------------------------------------------

        /// <summary>A guard turned by internal affairs was revealed in the ring at once as "internal affairs", on top of the turned member's own link a day.</summary>
        private static void CheckRingTurnedOfficerIsReportedAtTheTurnedPace(Map map)
        {
            Guarded("ring regression: a turned officer is reported at the pace of the turned", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer23", dealer: true);
                Pawn officer = s.Officer("Zofficer23", dealer);
                StoryArc ring = s.Form(dealer);
                RingLink officerLink = s.Link(ring, RingRole.Officer);
                if (officerLink == null || s.Known(ring) != 0)
                {
                    RingScene.Fail("the ring has no officer, or a link known, to begin with");
                    return;
                }
                // Internal affairs turned them, as the ring's own turning does.
                officer.GetComp<CompStaff>().doubleAgent = true;
                RingSystem.Sync(ring, map);
                if (!officerLink.turned || s.Known(ring) != 0 || s.Told().Any(t => t.Contains("Zofficer23")))
                    RingScene.Fail($"a guard turned by internal affairs was marked turned {officerLink.turned} and the warden told of {s.Known(ring)} links at once, or a line names them (turned, none, no line expected)");
                RingSystem.Daily(map, only: dealer);
                string how = "RP_RingHow_ia".Translate().ToString();
                if (s.Known(ring) != 1 || r.NewEntries.Any(e => e.text.Contains(how)))
                    RingScene.Fail($"the day after, {s.Known(ring)} links are known (one expected: the turned member's a day) and {(r.NewEntries.Any(e => e.text.Contains(how)) ? "a line credits internal affairs" : "none credits internal affairs")}");
            });
        }

        // ---- 11: look targets only for pawns on a map -------------------------------------------------------------------------------------

        /// <summary>A message about an officer at home (or suspended) pointed at a pawn on no map.</summary>
        private static void CheckRingLookTargetsNeedAMap(Map map)
        {
            Guarded("ring regression: a message points only at someone on a map", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer24", dealer: true);
                Pawn atHome = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true), onSite = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (atHome.Spawned || !onSite.Spawned)
                {
                    RingScene.Fail($"the officer at home is spawned {atHome.Spawned} or the one on site is not ({onSite.Spawned})");
                    return;
                }
                if (RingSystem.LookAt(atHome) != null || RingSystem.LookAt(null) != null || RingSystem.LookAt(onSite) is not { IsValid: true })
                    RingScene.Fail("the look target of a guard at home is not empty, or the one of a guard on site is not valid");
                // Through the real message: the officer on the dealer's payroll is at home and a tap reveals them. (The name is matched without the case: a message begins with a capital, and a generated nickname such as "greydoubt" does not.)
                CompStaff sc = atHome.GetComp<CompStaff>();
                (sc.corrupt, sc.patron, sc.patronGang) = (true, dealer, null);
                StoryArc ring = s.Form(dealer);
                if (!ring.ring.links.Exists(l => l.role == RingRole.Officer && l.pawnId == atHome.thingIDNumber))
                {
                    RingScene.Fail("the officer at home is not in the ring");
                    return;
                }
                RingSystem.Notify_Tapped(dealer, force: true);
                var live = (List<Message>)typeof(Messages).GetField("liveMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                Message last = live?.LastOrDefault(m => m.text.Contains("Zofficer") || m.text.IndexOf(atHome.LabelShort, StringComparison.OrdinalIgnoreCase) >= 0);
                if (last == null || last.lookTargets != null && last.lookTargets.IsValid)
                    RingScene.Fail($"the message that a guard at home was found points somewhere ({(last == null ? "no such message was found" : last.lookTargets?.PrimaryTarget.ToString())})");
                live?.RemoveAll(m => m == last);
            });
        }

        // ---- 12: the Intel tab and the file agree -------------------------------------------------------------------------------------------

        /// <summary>The dealer found in the chain was not a dealer on their file, and the other way round.</summary>
        private static void CheckRingDealerFileAndChainAgree(Map map)
        {
            Guarded("ring regression: the chain and the inmate file agree on the dealer", map, r =>
            {
                var s = new RingScene(map, r);
                // The chain finds the dealer: the file shows the trade.
                Pawn dealer = s.Inmate("Zdealer25", dealer: true);
                CompInmate dc = dealer.Inmate();
                StoryArc ring = s.Form(dealer);
                if (dc.KnownReputation(s.dealerRep) || s.Link(ring, RingRole.Dealer).known) RingScene.Fail("the dealer is known to begin with");
                s.Assess(dealer);
                if (!s.Link(ring, RingRole.Dealer).known || !dc.KnownReputation(s.dealerRep))
                    RingScene.Fail($"the dealer is known in the chain {s.Link(ring, RingRole.Dealer).known} but not on their file {dc.KnownReputation(s.dealerRep)}");
                // The file shows the trade (a session, an inquiry): the chain finds the dealer, at the next reading of the ring.
                Pawn other = s.Inmate("Zdealer26", dealer: true);
                CompInmate oc = other.Inmate();
                StoryArc ring2 = s.Form(other);
                oc.revealed.Add(s.dealerRep);
                RingSystem.Daily(map, only: other);
                if (!s.Link(ring2, RingRole.Dealer).known) RingScene.Fail("a dealer whose file shows the trade is not the ring's known dealer");
                if (s.Known(ring2) != 1) RingScene.Fail($"{s.Known(ring2)} links of the second ring are known (just the dealer expected)");
            });
        }

        // ---- 13: the small items -----------------------------------------------------------------------------------------------------------------

        private static void CheckRingSmallerItems(Map map)
        {
            Guarded("ring regression: smaller ring items", map, r =>
            {
                var s = new RingScene(map, r);
                SecurityLevelDef custody = DefDatabase<SecurityLevelDef>.GetNamedSilentFail("RP_Protective");
                // A courier who leaves takes their own link, not the supplier the warden has found.
                Pawn dealer = s.Inmate("Zdealer27", dealer: true), courier = s.Inmate("Zcourier27");
                courier.Inmate().lastDealerId = dealer.thingIDNumber;
                StoryArc ring = s.Form(dealer);
                s.Recruit(ring);
                RingSystem.Notify_BroughtIn(courier, "cousin Zsup27");
                RingSystem.Notify_FoundOnWay(courier, "cousin Zsup27", "post");
                RingLink supplier = s.Link(ring, RingRole.Relative);
                if (supplier is not { known: true })
                {
                    RingScene.Fail("the supplier was not found");
                    return;
                }
                LeaveStory(courier);
                if (!ring.ring.links.Contains(supplier) || !supplier.known || ring.ring.links.Exists(l => l.role == RingRole.Courier))
                    RingScene.Fail($"a courier released took the supplier the warden had found with them (supplier still there {ring.ring.links.Contains(supplier)}, known {supplier.known}), or stayed in the ring themselves");

                // The supplier is taken out: nothing was taken from the inmate they come to see, and a search a tip has set up is still to come.
                Pawn d2 = s.Inmate("Zdealer28", dealer: true);
                StoryArc ring2 = s.Form(d2);
                RingSystem.Notify_BroughtIn(d2, "cousin Zsup28");
                RingSystem.Notify_Tapped(d2, force: true);
                if (!s.Link(ring2, RingRole.Relative).known || s.Link(ring2, RingRole.Dealer).known)
                {
                    RingScene.Fail("a tap did not find the supplier alone");
                    return;
                }
                d2.Inmate().suspected = true;
                RingSystem.Bust(ring2, map);
                if (!d2.Inmate().suspected || !s.Link(ring2, RingRole.Relative).handled)
                    RingScene.Fail($"taking the supplier out cleared the search a tip had set up on the inmate they visit ({!d2.Inmate().suspected}), or did not take the supplier out");

                // A courier who is moved to protective custody is out of the ring (known: taken out; unknown: gone).
                if (custody != null)
                {
                    Pawn d3 = s.Inmate("Zdealer29", s.ravens, dealer: true), known = s.Inmate("Zknown29", s.ravens), hidden = s.Inmate("Zhidden29", s.ravens);
                    StoryArc ring3 = s.Form(d3);
                    s.Recruit(ring3);
                    s.Recruit(ring3);
                    s.Assess(known);
                    SecurityLevelDef sec1 = known.Inmate().security, sec2 = hidden.Inmate().security;
                    r.Add(() => (known.Inmate().security, hidden.Inmate().security) = (sec1, sec2));
                    int couriers = ring3.ring.links.Count(l => l.role == RingRole.Courier);
                    (known.Inmate().security, hidden.Inmate().security) = (custody, custody);
                    RingSystem.Sync(ring3, map);
                    if (couriers != 2 || ring3.ring.links.Exists(l => l.role == RingRole.Courier && !l.handled))
                        RingScene.Fail($"couriers in protective custody ({couriers} in the ring) are still carrying: {string.Join(", ", ring3.ring.links.Select(l => l.Describe()))}");
                }

                // An officer in a caravan is neither on the map nor at home: still on the payroll, nobody has acted.
                Pawn d4 = s.Inmate("Zdealer30", dealer: true);
                Pawn away = s.Officer("Zofficer30", d4);
                StoryArc ring4 = s.Form(d4);
                RingLink officerLink = s.Link(ring4, RingRole.Officer);
                officerLink.known = true;
                away.DeSpawn();
                Caravan caravan = CaravanMaker.MakeCaravan(new[] { away }, Faction.OfPlayer, map.Tile, addToWorldPawnsIfNotAlready: true);
                r.Add(() =>
                {
                    if (!caravan.Destroyed) caravan.Destroy();
                });
                RingSystem.Sync(ring4, map);
                if (officerLink.handled || !ring4.ring.links.Contains(officerLink))
                    RingScene.Fail($"a known officer who is away in a caravan was marked taken out ({officerLink.handled}) or dropped ({!ring4.ring.links.Contains(officerLink)}) without anyone acting");
            });
        }
    }
}
