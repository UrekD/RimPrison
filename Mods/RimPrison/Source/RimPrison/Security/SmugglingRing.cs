using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public enum RingRole : byte
    {
        /// <summary>The inmate the ring is built around (the arc's cast).</summary>
        Dealer,
        /// <summary>An inmate who carries for the dealer: stock that reaches them is handed on.</summary>
        Courier,
        /// <summary>The outside supplier, who comes in as a visitor or writes (a name, never a pawn).</summary>
        Relative,
        /// <summary>A corrupt member of staff on the dealer's payroll.</summary>
        Officer,
    }

    /// <summary>
    /// One member of a ring, by thing id and name (like a debt, so it outlives the pawn on the map). Whether the warden has
    /// <see cref="known"/> of it is the whole point: nothing shown or written may name a link that is not.
    /// </summary>
    public class RingLink : IExposable
    {
        public RingRole role;
        /// <summary>The inmate or officer; for the outside supplier, the inmate they come to see (never shown). -1 none.</summary>
        public int pawnId = -1;
        public string name;
        public bool known;
        /// <summary>A supplier link that is a smokeleaf patch in the prison garden (the link of one gardener, <see cref="pawnId"/>), not someone outside.</summary>
        public bool garden;
        /// <summary>Taken out by a bust (and, for a while after, cut off from every way in: see <see cref="RingSystem.SupplyCut"/>), or turned by the warden.</summary>
        public bool handled, turned;
        public int handledTick = -1;
        /// <summary>For a courier: how many drugs in their pockets came in through the ring (a visit, the post, an officer), and so are the dealer's to be handed on. What they bought is theirs.</summary>
        public int carried;

        public void ExposeData()
        {
            Scribe_Values.Look(ref role, "role");
            Scribe_Values.Look(ref pawnId, "pawn", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref known, "known");
            Scribe_Values.Look(ref garden, "garden");
            Scribe_Values.Look(ref handled, "handled");
            Scribe_Values.Look(ref turned, "turned");
            Scribe_Values.Look(ref handledTick, "handledTick", -1);
            Scribe_Values.Look(ref carried, "carried");
        }

        public string Describe() => $"{role}:{pawnId}:{name}:{known}{handled}{turned}{garden}:{handledTick}:{carried}";
    }

    /// <summary>The ring behind an <see cref="StoryArc"/>: its links (the dealer first), the heat the finds have raised, and its schedule.</summary>
    public class SmugglingRing : IExposable
    {
        public List<RingLink> links = new List<RingLink>();
        public float heat;
        public int lastAskTick = -999999, nextRecruitTick, brokenUntilTick = -1;
        /// <summary>
        /// The Intel tab's "more links suspected" marker. It is read from the links when the ring forms and at the daily pass, and a find that leaves nothing unknown clears it; it does not follow the
        /// links live (a delivery that went unfound adds a link out of the warden's sight, and a marker that came on at that moment would tell them something got in).
        /// </summary>
        public bool moreShown;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref links, "links", LookMode.Deep);
            Scribe_Values.Look(ref heat, "heat");
            Scribe_Values.Look(ref lastAskTick, "lastAsk", -999999);
            Scribe_Values.Look(ref nextRecruitTick, "nextRecruit");
            Scribe_Values.Look(ref brokenUntilTick, "brokenUntil", -1);
            Scribe_Values.Look(ref moreShown, "moreShown");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                links ??= new List<RingLink>();
                links.RemoveAll(l => l == null);
            }
        }

        public RingLink Dealer => links.Find(l => l.role == RingRole.Dealer);

        public string Describe() => $"{heat:F1}:{lastAskTick}:{nextRecruitTick}:{brokenUntilTick}:{moreShown}:[{string.Join(",", links.Select(l => l.Describe()))}]";
    }

    [DefOf]
    public static class RPRingDefOf
    {
        public static StoryArcDef RP_Arc_SmugglingRing;

        static RPRingDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRingDefOf));
    }

    /// <summary>One piece of the chain the Intel tab draws: a known link by name, or the one marker for the links the warden has not found.</summary>
    public readonly struct RingPart
    {
        public readonly string text;
        public readonly RingLink link;

        public RingPart(string text, RingLink link)
        {
            this.text = text;
            this.link = link;
        }

        public bool Known => link != null;
    }

    /// <summary>
    /// Smuggling rings: the supply chain behind the contraband, made explicit. Every active dealer has a ring (a background story arc, the
    /// dealer its cast): the dealer, courier inmates (gang mates and regular customers), an outside supplier who visits or writes, and a
    /// corrupt officer on the dealer's payroll. The links are the network that already exists (patrons, visits, the post), so the ring is
    /// read from the game's own state and watches it: each find that traces back to it raises its heat. All of it is hidden. The warden
    /// learns a link only from what is found (a frisk, a stash, a visitor or a letter caught, contraband traced to its source) or told (a
    /// snitch's tip, a phone tap, the psychologist, a turned member): each step reveals one link, and the Intel tab draws the chain with
    /// one marker for "more links suspected" while any is still missing (never a mark or a count per link). Then the warden may bust the ring now (the links known are taken out; the rest stays and the
    /// dealer rebuilds, or, if the dealer is among them, the ring breaks), wait for more, or turn an officer or a courier. A ring
    /// with every link found and handled is a milestone and lifts the security grade.
    /// Event-driven: nothing here runs per tick; the hourly pass is O(links), the daily one O(inmates + staff).
    /// </summary>
    public static class RingSystem
    {
        public const int CutDays = 5, RebuildDays = 2, AskCooldownDays = 3, VisitBanDays = 8, MaxCouriers = 2, RepeatBonusDays = 10, NeverBusted = -999999;
        public const float HeatPerFind = 15f, HeatTraced = 10f, HeatTap = 8f, AskHeat = 35f, HeatDecay = 2f, NervousHeat = 50f;
        public const float RecruitChance = 0.5f, TipChance = 0.5f, TapChance = 0.7f, TraceDealerChance = 0.5f, HandoverChance = 0.3f, TurnedExposedChance = 0.06f;

        /// <summary>The source a smokeleaf patch in the prison garden gives a ring (the name of its supplier link, see <see cref="Notify_BroughtIn"/>): a supplier of its own, next to an outside one.</summary>
        public const string GardenSource = "the garden";

        private static int Now => Find.TickManager.TicksGame;

        public static List<StoryArc> Arcs => PrisonGame.Instance?.story.arcs;

        public static bool IsDealer(CompInmate c) => c != null && c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer));

        // ---- lookups ---------------------------------------------------------------------------------------------

        /// <summary>The rings running now (a ring that was broken stays until its cut is over).</summary>
        public static IEnumerable<StoryArc> Rings()
        {
            List<StoryArc> arcs = Arcs;
            if (arcs == null) yield break;
            for (int i = 0; i < arcs.Count; i++)
                if (!arcs[i].done && arcs[i].ring != null && arcs[i].def == RPRingDefOf.RP_Arc_SmugglingRing) yield return arcs[i];
        }

        public static StoryArc ArcOfDealer(Pawn dealer)
        {
            foreach (StoryArc a in Rings())
                if (a.A == dealer) return a;
            return null;
        }

        /// <summary>The link of this inmate or officer in a ring (still in it: not taken out by a bust), and the ring.</summary>
        public static RingLink LinkOf(Pawn p, out StoryArc arc, RingRole? role = null)
        {
            foreach (StoryArc a in Rings())
                foreach (RingLink l in a.ring.links)
                    if (!l.handled && l.pawnId == p.thingIDNumber && l.role != RingRole.Relative && (role == null || l.role == role))
                    {
                        arc = a;
                        return l;
                    }
            arc = null;
            return null;
        }

        public static StoryArc ArcOf(Pawn inmate) => LinkOf(inmate, out StoryArc a) != null ? a : null;

        /// <summary>Whether this inmate or officer has a link in this ring, taken out or not (the outside supplier's link is nobody's but the supplier's).</summary>
        public static bool InRing(StoryArc arc, Pawn p) => arc.ring.links.Exists(l => l.pawnId == p.thingIDNumber && l.role != RingRole.Relative);

        public static bool Visible(StoryArc arc) => arc.ring.links.Exists(l => l.known);

        public static List<StoryArc> VisibleRings()
        {
            var visible = new List<StoryArc>();
            foreach (StoryArc a in Rings())
                if (Visible(a)) visible.Add(a);
            return visible;
        }

        /// <summary>The ring has cut this inmate off from every way in: they were taken out of one lately (stock, visits and post stop for a few days).</summary>
        public static bool SupplyCut(Pawn p)
        {
            if (p.Inmate() is { } pc && pc.supplyCutUntilTick > Now) return true;
            foreach (StoryArc a in Rings())
                foreach (RingLink l in a.ring.links)
                    if (l.handled && l.pawnId == p.thingIDNumber && l.role != RingRole.Officer && Now - l.handledTick < CutDays * GenDate.TicksPerDay) return true;
            return false;
        }

        private static Pawn PawnOf(Map map, int id)
        {
            if (map == null || id < 0) return null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (p.thingIDNumber == id && !p.Dead) return p;
            return StaffOf(map, id);
        }

        /// <summary>Staff of this prison who are in a caravan or a pod: on no map and not at home, but not gone.</summary>
        private static bool StaffAway(int id)
        {
            foreach (Pawn p in PawnsFinder.AllCaravansAndTravellingTransporters_Alive)
                if (p.thingIDNumber == id && p.IsFreeColonist && p.GetComp<CompStaff>()?.role != null) return true;
            return false;
        }

        private static Pawn StaffOf(Map map, int id)
        {
            if (map == null || id < 0) return null;
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.thingIDNumber == id) return p;
            return null;
        }

        public static string LinkLine(RingLink l) => IsGarden(l) ? "RP_RingLine_Garden".Translate().ToString() : ("RP_RingLine_" + l.role).Translate(l.name).ToString();

        /// <summary>The supplier link that is a gardener's patch in the garden, not someone outside.</summary>
        public static bool IsGarden(RingLink l) => l.garden;

        /// <summary>What the Intel tab calls a link: its name, or for the garden's patch what it is.</summary>
        public static string DisplayName(RingLink l) => IsGarden(l) ? "RP_RingGardenName".Translate().ToString() : l.name;

        // ---- forming ---------------------------------------------------------------------------------------------

        /// <summary>A ring around this dealer, with the links their own state gives it (none if there is a ring for them, or too many already).</summary>
        public static StoryArc Form(Pawn dealer, Map map, bool ignoreLimit = false)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || ArcOfDealer(dealer) != null || !ignoreLimit && Rings().Count() >= RPRingDefOf.RP_Arc_SmugglingRing.maxConcurrent) return null;
            if (dealer.Inmate() is not { activeFlag: true } dc || !IsDealer(dc)) return null;
            // (Not through TryStartArc: a dealer in a feud or a debt still has a ring, and a ring keeps nobody out of a story.)
            var arc = new StoryArc { def = RPRingDefOf.RP_Arc_SmugglingRing, id = game.story.nextArcId++, startTick = Now, nextTick = Now, ring = new SmugglingRing() };
            arc.pawns.Add(dealer);
            // (A dealer the warden has taken out before is a known one: the new ring starts with their link found.)
            var dealerLink = new RingLink { role = RingRole.Dealer, pawnId = dealer.thingIDNumber, name = dealer.LabelShort, known = dc.ringBustedTick > NeverBusted };
            arc.ring.links.Add(dealerLink);
            if (dealerLink.known) KeepFileInStep(dealerLink, dc);
            game.story.arcs.Add(arc);
            Sync(arc, map);
            arc.ring.moreShown = HasUnknown(arc);
            return arc;
        }

        /// <summary>
        /// Reads the links the game's own state gives the ring: the corrupt staff who work for the dealer or their gang are its officers, and a
        /// link whose person is gone (released, fired, no longer on the payroll) is dropped. Called when it forms and every day.
        /// </summary>
        public static void Sync(StoryArc arc, Map map)
        {
            SmugglingRing ring = arc.ring;
            Pawn dealer = arc.A;
            CompInmate dc = dealer?.Inmate();
            if (dc == null) return;
            // The file and the chain agree: a dealer the warden already knows for one (a session, an inquiry into an overdose) is the ring's known dealer.
            if (ring.Dealer is { known: false, handled: false } dl && dc.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer) && dc.revealed.Contains(r))) Reveal(arc, dl, "file", map, announce: false);
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>() is { corrupt: true, doubleAgent: false } sc && CorruptionSystem.IsClient(sc, dealer, dc)
                    && !ring.links.Exists(l => l.role == RingRole.Officer && l.pawnId == s.thingIDNumber))
                    ring.links.Add(new RingLink { role = RingRole.Officer, pawnId = s.thingIDNumber, name = s.LabelShort });
            for (int i = ring.links.Count - 1; i >= 0; i--)
            {
                RingLink l = ring.links[i];
                if (l.handled || l.role == RingRole.Dealer) continue;
                if (l.role == RingRole.Officer)
                {
                    Pawn s = StaffOf(map, l.pawnId);
                    CompStaff sc = s?.GetComp<CompStaff>();
                    if (sc == null)
                    {
                        // Away in a caravan or a pod: neither on the map nor at home, but still theirs; nobody has acted.
                        if (StaffAway(l.pawnId)) continue;
                        // Let go (the warden fired them, or an inquiry did): known, it is done with; else it was never theirs to find.
                        if (l.known) (l.handled, l.handledTick) = (true, Now);
                        else ring.links.RemoveAt(i);
                    }
                    else if (sc.doubleAgent)
                    {
                        // Turned (by internal affairs, or from here): they work against the ring now, and tell what they know at their own pace (see TurnedMembersReport).
                        l.turned = true;
                    }
                    else if (!sc.corrupt || !CorruptionSystem.IsClient(sc, dealer, dc)) ring.links.RemoveAt(i);
                }
                else if (l.role == RingRole.Courier)
                {
                    if (PawnOf(map, l.pawnId) is not { } p || p.Inmate() is not { activeFlag: true } pc) ring.links.RemoveAt(i);
                    else if (DebtSystem.InCustody(pc) || DeathRowSystem.Ext(pc) != null)
                    {
                        // Out of reach of the yard (protective custody, death row): no carrying for anyone. Known, it is done with; else it was never theirs to find.
                        if (l.known) (l.handled, l.handledTick) = (true, Now);
                        else ring.links.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>A courier joins: a gang mate or a regular customer of the dealer (a few days between recruits). <paramref name="force"/> skips the dice.</summary>
        public static RingLink Recruit(StoryArc arc, Map map, bool force = false)
        {
            SmugglingRing ring = arc.ring;
            Pawn dealer = arc.A;
            CompInmate dc = dealer?.Inmate();
            if (dc == null || arc.stage != 0 || Now < ring.nextRecruitTick || ring.links.Count(l => l.role == RingRole.Courier && !l.handled && !l.turned) >= MaxCouriers) return null;
            if (!force && !Rand.Chance(RecruitChance)) return null;
            var candidates = new List<(Pawn p, float weight)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == dealer || p.Inmate() is not { activeFlag: true } c || IsDealer(c) || c.snitchExposed || c.HasReputation(ReputationDefOf.RP_Snitch)) continue;
                if (DebtSystem.InCustody(c) || DeathRowSystem.Ext(c) != null || ArcOf(p) != null) continue;
                // Not one who was taken out of this ring (or any) lately, and not an addict the dealer sells to: they would carry back what they bought.
                bool customer = c.lastDealerId == dealer.thingIDNumber;
                if (InRing(arc, p) || SupplyCut(p) || customer && ReputationBehavior_Dealer.HasAddiction(p)) continue;
                // (A gardener with a patch of their own has something to sell: the dealer wants them.)
                float w = (dc.gang != null && c.gang == dc.gang ? 3f : 0f) + (customer ? 2f : 0f) + (GardenPatches.Owns(PrisonMap.Of(map), p) ? 4f : 0f);
                if (w > 0f) candidates.Add((p, w));
            }
            if (!candidates.TryRandomElementByWeight(x => x.weight, out (Pawn p, float weight) pick)) return null;
            ring.nextRecruitTick = Now + RebuildDays * GenDate.TicksPerDay;
            var link = new RingLink { role = RingRole.Courier, pawnId = pick.p.thingIDNumber, name = pick.p.LabelShort };
            ring.links.Add(link);
            return link;
        }

        // ---- the daily and hourly passes -------------------------------------------------------------------------

        /// <summary>Daily: every active dealer has a ring; each ring reads its links, cools, recruits, hears from its turned members and may put its case to the warden. <paramref name="only"/>: one dealer's ring alone (tests).</summary>
        public static void Daily(Map map, Pawn only = null)
        {
            if (PrisonGame.Instance == null || RPRingDefOf.RP_Arc_SmugglingRing == null) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if ((only == null || p == only) && p.Inmate() is { activeFlag: true } c && IsDealer(c) && !p.Downed) Form(p, map);
            foreach (StoryArc arc in Rings().ToList())
                if (only == null || arc.A == only) DailyRing(arc, map);
        }

        private static void DailyRing(StoryArc arc, Map map)
        {
            SmugglingRing ring = arc.ring;
            Sync(arc, map);
            if (arc.done) return;
            int now = Now;
            // The cut is over (a handled dealer's link is the last to go: it ends the ring).
            ring.links.RemoveAll(l => l.handled && l.role != RingRole.Dealer && now - l.handledTick >= CutDays * GenDate.TicksPerDay);
            if (arc.stage == 1)
            {
                if (now >= ring.brokenUntilTick) arc.done = true;
                return;
            }
            ring.heat = Mathf.Max(0f, ring.heat - HeatDecay);
            // A ring the guards are close to is nervous.
            if (ring.heat >= NervousHeat)
                foreach (RingLink l in ring.links)
                    if (!l.handled && l.role is RingRole.Dealer or RingRole.Courier && PawnOf(map, l.pawnId)?.Inmate() is { } mc) mc.tension = Mathf.Min(100f, mc.tension + 3f);
            Recruit(arc, map);
            TurnedMembersReport(arc, map);
            ring.moreShown = HasUnknown(arc);
            MaybeAsk(arc, map);
        }

        /// <summary>Hourly (the arc's pass): a courier with stock hands it to the dealer when no guard is looking; the case may be put to the warden.</summary>
        public static void Hourly(StoryArc arc, Map map)
        {
            if (arc.stage != 0) return;
            TryHandover(arc, map);
            MaybeAsk(arc, map);
        }

        /// <summary>The drugs in an inmate's pockets.</summary>
        public static int DrugsCarried(Pawn p)
        {
            int n = 0;
            if (p.inventory != null)
                foreach (Thing t in p.inventory.innerContainer)
                    if (ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Drug }) n += t.stackCount;
            return n;
        }

        /// <summary>
        /// A courier carrying drugs that came in through the ring, in the dealer's room and unwatched, passes them on. Returns whether something changed hands.
        /// What the courier bought (from this dealer or another) or found is theirs: only the count <see cref="RingLink.carried"/> of what the ring brought goes back.
        /// </summary>
        public static bool TryHandover(StoryArc arc, Map map, bool force = false)
        {
            Pawn dealer = arc.A;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || dealer == null || !dealer.Spawned || dealer.Downed || dealer.InMentalState || !dealer.Awake() || dealer.inventory == null) return false;
            foreach (RingLink l in arc.ring.links)
            {
                // (A turned courier works for the warden now: they pass nothing on, and do not hold a place a new recruit could take.)
                if (l.role != RingRole.Courier || l.handled || l.turned || PawnOf(map, l.pawnId) is not { Spawned: true } courier || courier.inventory == null) continue;
                if (courier.Downed || courier.InMentalState || !courier.Awake() || courier.GetRoom() != dealer.GetRoom() || !courier.Position.InHorDistOf(dealer.Position, 10f)) continue;
                if (TensionUtility.Watched(courier, pm) || TensionUtility.Watched(dealer, pm)) continue;
                // (Some of it may be gone since: used, found, given away.)
                l.carried = Mathf.Min(l.carried, DrugsCarried(courier));
                if (l.carried <= 0 || !force && !Rand.Chance(HandoverChance)) continue;
                int before = DrugsCarried(courier), wanted = l.carried;
                foreach (Thing drug in courier.inventory.innerContainer.Where(t => ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Drug }).ToList())
                {
                    int take = Mathf.Min(drug.stackCount, wanted);
                    if (take <= 0) continue;
                    courier.inventory.innerContainer.TryTransferToContainer(drug, dealer.inventory.innerContainer, take);
                    wanted -= take;
                }
                // (The count of what moved is what left the pockets: a transfer that merges into the dealer's stack reports none.)
                int passed = before - DrugsCarried(courier);
                l.carried -= passed;
                if (passed > 0) return true;
            }
            return false;
        }

        /// <summary>Each turned member tells the warden one more link a day (an inside source); a turned courier may be found out.</summary>
        private static void TurnedMembersReport(StoryArc arc, Map map)
        {
            foreach (RingLink l in arc.ring.links.Where(x => x.turned && !x.handled).ToList())
            {
                if (l.role == RingRole.Courier && Rand.Chance(TurnedExposedChance)) ExposeTurned(arc, l, map);
                else RevealNext(arc, map, "source");
            }
        }

        /// <summary>The ring works out who has been talking: the courier is out of it and in trouble (and says so, as the warden knew them).</summary>
        private static void ExposeTurned(StoryArc arc, RingLink l, Map map)
        {
            arc.ring.links.Remove(l);
            if (PawnOf(map, l.pawnId) is not { } p || p.Inmate() is not { } c) return;
            c.snitchExposed = true;
            InformantTask.End(p, c);
            FearSystem.Scare(c, 30f);
            c.tension = Mathf.Min(100f, c.tension + 15f);
            Find.LetterStack.ReceiveLetter("RP_RingTurnedExposedLabel".Translate(), "RP_RingTurnedExposedText".Translate(l.name), LetterDefOf.ThreatSmall, p);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_RingTurnedExposed".Translate(l.name), p);
        }

        // ---- what the warden learns ------------------------------------------------------------------------------

        private static string How(string how) => ("RP_RingHow_" + how).Translate();

        /// <summary>The warden learns this link (once): a chronicle line, and a message when <paramref name="announce"/>. Names this link only.</summary>
        private static bool Reveal(StoryArc arc, RingLink link, string how, Map map, bool announce = true)
        {
            if (link == null || link.known) return false;
            link.known = true;
            Concepts.Teach(Concepts.Ring);
            if (!HasUnknown(arc)) arc.ring.moreShown = false;
            PrisonGame.Instance.stats.ringLinksFound++;
            Pawn p = link.role == RingRole.Relative ? null : PawnOf(map, link.pawnId);
            if (link.role == RingRole.Dealer) KeepFileInStep(link, p?.Inmate());
            TaggedString line = "RP_RingLinkFound".Translate(LinkLine(link), How(how));
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, line, p);
            if (announce) Messages.Message(line, LookAt(p), MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        /// <summary>Something to jump to for this person: only if they are on a map (an officer at home or suspended is not).</summary>
        public static LookTargets LookAt(Pawn p) => p is { Spawned: true } ? new LookTargets(p) : LookTargets.Invalid;

        /// <summary>The warden's file on the dealer says what the ring's chain says: a dealer known to the ring has their reputation shown on their file (and the other way round: see <see cref="Sync"/>).</summary>
        private static void KeepFileInStep(RingLink dealerLink, CompInmate c)
        {
            if (c == null || dealerLink.role != RingRole.Dealer) return;
            foreach (ReputationDef r in c.reputations)
                if (r.behaviorClass == typeof(ReputationBehavior_Dealer) && !c.revealed.Contains(r)) c.revealed.Add(r);
        }

        private static readonly RingRole[] RevealOrder = { RingRole.Officer, RingRole.Relative, RingRole.Courier, RingRole.Dealer };

        /// <summary>One link nobody knew of is told: the officer first, then the supplier, a courier, the dealer.</summary>
        private static RingLink RevealNext(StoryArc arc, Map map, string how, RingLink not = null, bool announce = true)
        {
            foreach (RingRole role in RevealOrder)
            {
                RingLink l = arc.ring.links.Find(x => x.role == role && !x.known && !x.handled && x != not);
                if (l != null && Reveal(arc, l, how, map, announce)) return l;
            }
            return null;
        }

        private static void AddHeat(StoryArc arc, float amount) => arc.ring.heat = Mathf.Min(100f, arc.ring.heat + amount);

        /// <summary>
        /// Contraband was found on this inmate (a frisk, a cell search). On a member of a ring: its heat rises and that link is found. On someone
        /// the dealer sold to: the dealer may be traced. On someone a ring's officer brought it to, when the trace to the officer held
        /// (<paramref name="traced"/>): the officer's link is found.
        /// </summary>
        public static void Notify_Found(Pawn inmate, CompInmate c, Pawn staff, bool traced, string how = "find")
        {
            if (inmate.MapHeld is not { } map) return;
            if (LinkOf(inmate, out StoryArc own) is { } mine)
            {
                AddHeat(own, HeatPerFind);
                Reveal(own, mine, how, map);
                MaybeAsk(own, map);
            }
            else if (c.lastDealerId >= 0)
            {
                foreach (StoryArc a in Rings())
                    if (a.A?.thingIDNumber == c.lastDealerId)
                    {
                        // (Heat with the trace only, unless the dealer is known already: a find on a customer that traced nothing must not heat a ring whose dealer nobody has found.)
                        if (a.ring.Dealer is { known: true } || Rand.Chance(TraceDealerChance))
                        {
                            AddHeat(a, HeatTraced);
                            Reveal(a, a.ring.Dealer, "trace", map);
                        }
                        MaybeAsk(a, map);
                        break;
                    }
            }
            // (An officer on the payroll of two dealers is traced into both rings.)
            if (traced && staff != null)
                foreach (StoryArc staffArc in Rings().ToList())
                    if (staffArc.ring.links.Find(l => !l.handled && l.role == RingRole.Officer && l.pawnId == staff.thingIDNumber) is { } officer)
                    {
                        AddHeat(staffArc, HeatTraced);
                        Reveal(staffArc, officer, "trace", map);
                        MaybeAsk(staffArc, map);
                    }
        }

        /// <summary>
        /// A visitor was caught with something for this inmate, or a letter for them was opened: on a ring member that is a find (the inmate's link,
        /// and the outside supplier, who is named at the door or on the envelope).
        /// </summary>
        public static void Notify_FoundOnWay(Pawn inmate, string relative, string how)
        {
            if (inmate.MapHeld is not { } map || LinkOf(inmate, out StoryArc arc) == null) return;
            AddHeat(arc, HeatPerFind);
            Reveal(arc, LinkOf(inmate, out _), how, map);
            // (The supplier only if this is the one the ring has: another relative at the door is nobody's link.)
            if (EnsureSupplier(arc, inmate, relative) is { } supplier && supplier.name == relative) Reveal(arc, supplier, how, map);
            MaybeAsk(arc, map);
        }

        /// <summary>
        /// Something came in for this inmate by a visit or the post, unfound: a ring's member has an outside supplier (the first to come is its link, unknown), and
        /// a courier now carries stock for the dealer (<paramref name="given"/>: what was put in their pockets).
        /// </summary>
        public static void Notify_BroughtIn(Pawn inmate, string relative, ThingDef given = null)
        {
            if (LinkOf(inmate, out StoryArc arc) is not { } link) return;
            EnsureSupplier(arc, inmate, relative);
            Carrying(link, given);
        }

        /// <summary>
        /// A smokeleaf patch in the garden was found (pulled up by the staff gardener, a guard, or a cell search): on the gardener who tended it, a member of a ring, that is a find (their
        /// link, the heat), and the garden, which is named by what was found, is a link the warden now knows.
        /// </summary>
        public static void Notify_GardenFound(Pawn gardener)
        {
            if (gardener.MapHeld is not { } map || LinkOf(gardener, out StoryArc arc) is not { } mine) return;
            AddHeat(arc, HeatPerFind);
            Reveal(arc, mine, "garden", map);
            if (arc.ring.links.Find(l => !l.handled && IsGarden(l) && l.pawnId == gardener.thingIDNumber) is { } garden) Reveal(arc, garden, "garden", map);
            MaybeAsk(arc, map);
        }

        /// <summary>A guard on the dealer's payroll put this in an inmate's pockets: if they are in a ring, a courier carries it for the dealer.</summary>
        public static void Notify_Delivered(Pawn inmate, ThingDef given)
        {
            if (LinkOf(inmate, out _) is { } link) Carrying(link, given);
        }

        private static void Carrying(RingLink link, ThingDef given)
        {
            if (link.role == RingRole.Courier && given != null && ContrabandUtility.Ext(given) is { kind: ContrabandKind.Drug }) link.carried += ContrabandUtility.DeliveryCount(given);
        }

        private static RingLink EnsureSupplier(StoryArc arc, Pawn owner, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return arc.ring.links.Find(l => l.role == RingRole.Relative && !l.handled && !l.garden);
            // (The garden is a supplier of its own, one for each gardener whose harvests feed the ring: a ring with an outside supplier still has the patch, and the other way round.)
            bool garden = relative == GardenSource;
            RingLink l = arc.ring.links.Find(x => x.role == RingRole.Relative && !x.handled && x.garden == garden && (!garden || x.pawnId == owner.thingIDNumber));
            if (l != null) return l;
            l = new RingLink { role = RingRole.Relative, pawnId = owner.thingIDNumber, name = relative, garden = garden };
            arc.ring.links.Add(l);
            return l;
        }

        /// <summary>A phone tap overheard a delivery being arranged: if the caller is in a ring, one more link (the officer who brings it, else the supplier) is found, and the heat rises.</summary>
        public static bool Notify_Tapped(Pawn inmate, bool force = false)
        {
            if (LinkOf(inmate, out StoryArc arc) == null || inmate.MapHeld is not { } map) return false;
            bool found = (force || Rand.Chance(TapChance)) && RevealNext(arc, map, "tap") != null;
            // (Heat rises with what the tap gives away, not with the tap: a call that overheard nothing must not show on the ring's heat, which would tell the warden the caller is in one.)
            if (found) AddHeat(arc, HeatTap);
            MaybeAsk(arc, map);
            return found;
        }

        /// <summary>A session with the psychologist: a member of a ring says more than they meant to (their own part first, then someone else's).</summary>
        public static bool Notify_Assessed(Pawn inmate, float chance)
        {
            if (LinkOf(inmate, out StoryArc arc) is not { } mine || inmate.MapHeld is not { } map || !Rand.Chance(chance)) return false;
            bool found = Reveal(arc, mine, "session", map) || RevealNext(arc, map, "session", mine) != null;
            MaybeAsk(arc, map);
            return found;
        }

        /// <summary>
        /// A snitch's tip: when the snitch is near someone in a ring (<paramref name="near"/>: the inmates close to them), they may name one link
        /// the warden does not know. Returns the tip's words (naming that link only), or null. <paramref name="map"/>: the map the tip is about (null: the snitch's; a unit officer at home is on none).
        /// </summary>
        public static string TryTip(Pawn snitch, List<Pawn> near, float chance = TipChance, Map map = null)
        {
            map ??= snitch.MapHeld;
            if (map == null) return null;
            foreach (StoryArc arc in Rings().ToList())
            {
                if (arc.stage != 0 || arc.ring.links.Exists(l => l.pawnId == snitch.thingIDNumber && l.role != RingRole.Relative)) continue;
                if (!arc.ring.links.Exists(l => !l.handled && l.role is RingRole.Dealer or RingRole.Courier && near.Exists(o => o.thingIDNumber == l.pawnId))) continue;
                if (!Rand.Chance(chance)) continue;
                RingLink told = RevealNext(arc, map, "tip", announce: false);
                if (told == null) continue;
                MaybeAsk(arc, map);
                return "RP_TipRing".Translate(LinkLine(told));
            }
            return null;
        }

        // ---- the warden's moves ----------------------------------------------------------------------------------

        /// <summary>Links a bust would take out now: the known ones not already handled or turned.</summary>
        public static List<RingLink> Bustable(StoryArc arc) => arc.ring.links.Where(l => l.known && !l.handled && !l.turned).ToList();

        /// <summary>Whether the ring has a link nobody has found.</summary>
        public static bool HasUnknown(StoryArc arc) => arc.ring.links.Exists(l => !l.known && !l.handled);

        /// <summary>What the Intel tab shows of the links nobody has found: the marker as of the last daily pass (see <see cref="SmugglingRing.moreShown"/>), never the links as they are now.</summary>
        public static bool MoreSuspected(StoryArc arc) => arc.ring.moreShown;

        /// <summary>Links the warden could turn: a known officer on the payroll, a known courier.</summary>
        public static List<RingLink> Turnable(StoryArc arc, RingRole? role = null)
        {
            var list = new List<RingLink>();
            if (arc.done || arc.stage != 0) return list;
            Map map = arc.A?.MapHeld;
            foreach (RingLink l in arc.ring.links)
            {
                if (!l.known || l.handled || l.turned || role != null && l.role != role) continue;
                if (l.role == RingRole.Officer && StaffOf(map, l.pawnId)?.GetComp<CompStaff>() is { corrupt: true, doubleAgent: false }) list.Add(l);
                else if (l.role == RingRole.Courier && PawnOf(map, l.pawnId)?.Inmate() is { activeFlag: true }) list.Add(l);
            }
            return list;
        }

        /// <summary>The one of these a letter's "turn" turns (it names no one): the courier or officer who is worth most to the warden as a source, whose stock the ring counts on (what they carry is not shown anywhere), else the first.</summary>
        public static RingLink BestToTurn(List<RingLink> links) => links.OrderByDescending(l => l.carried).First();

        /// <summary>
        /// Takes out every known link at once: inmates lose what they carry and hide (and their visits stop for a while), the officer is let go and
        /// arrested, the outside supplier is barred from the visits. If nothing is left unknown, the dealer is among them and the ring was more than
        /// the dealer, it is a milestone and lifts the security grade (not for a dealer the warden took out less than <see cref="RepeatBonusDays"/> days
        /// ago); if the dealer is among them the ring is broken; else the ring stays and the dealer rebuilds it. Returns the number taken out.
        /// </summary>
        /// <summary>
        /// The pawns a letter or a line may point at for these links: the dealer, the couriers and the officer. Never the outside supplier: their link stores the inmate they come to see
        /// (so the ring can cut that inmate off), who may be a courier or the dealer the warden has not found.
        /// </summary>
        private static List<Pawn> Named(IEnumerable<RingLink> links, Map map, bool spawnedOnly = false) =>
            links.Where(l => l.role != RingRole.Relative).Select(l => PawnOf(map, l.pawnId)).Where(p => p != null && (!spawnedOnly || p.Spawned)).ToList();

        public static int Bust(StoryArc arc, Map map)
        {
            SmugglingRing ring = arc.ring;
            if (arc.done || arc.stage != 0) return 0;
            List<RingLink> targets = Bustable(arc);
            if (targets.Count == 0) return 0;
            PrisonGame game = PrisonGame.Instance;
            var names = new List<string>();
            foreach (RingLink l in targets)
            {
                Handle(l, map);
                names.Add(RingSystem.DisplayName(l));
            }
            string list = string.Join(", ", names);
            bool dealerGone = ring.Dealer is { handled: true };
            // (A dealer on their own is no ring to break whole, and one taken out lately is not a new milestone: the ring forms again around them and the same finds would pay twice.)
            CompInmate dealerInmate = dealerGone ? PawnOf(map, ring.Dealer.pawnId)?.Inmate() : null;
            bool lately = dealerInmate != null && Now - dealerInmate.ringBustedTick < RepeatBonusDays * GenDate.TicksPerDay;
            bool whole = dealerGone && !lately && ring.links.Exists(l => l.role != RingRole.Dealer) && !ring.links.Exists(l => !l.handled && !l.turned);
            if (dealerInmate != null) dealerInmate.ringBustedTick = Now;
            List<Pawn> named = Named(targets, map);
            var look = new LookTargets(named.Where(p => p.Spawned).ToList());
            if (whole)
            {
                game.stats.ringsBusted++;
                game.today.ringsBusted++;
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 3, "RP_Chr_RingBroken".Translate(list), named.ToArray());
                Find.LetterStack.ReceiveLetter("RP_RingBrokenLabel".Translate(), (targets.Count > 1 ? "RP_RingBrokenText" : "RP_RingBrokenTextOne").Translate(list, CutDays), LetterDefOf.PositiveEvent, look);
            }
            else
            {
                game.stats.ringsPartial++;
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, (dealerGone ? "RP_Chr_RingCut" : "RP_Chr_RingPartial").Translate(list), named.ToArray());
                Messages.Message((dealerGone ? "RP_RingCutMessage" : "RP_RingPartialMessage").Translate(list), look, MessageTypeDefOf.PositiveEvent, historical: false);
            }
            ring.heat = 0f;
            if (dealerGone)
            {
                // Without its dealer there is no ring until they are free to start another.
                arc.stage = 1;
                ring.brokenUntilTick = Now + CutDays * GenDate.TicksPerDay;
            }
            else ring.nextRecruitTick = Now + RebuildDays * GenDate.TicksPerDay;
            return targets.Count;
        }

        private static void Handle(RingLink l, Map map)
        {
            l.handled = true;
            l.handledTick = Now;
            Pawn p = PawnOf(map, l.pawnId);
            // (The cut is theirs, and outlasts the ring: a ring that falls apart does not give them their visits back.)
            if (l.role != RingRole.Officer && p?.Inmate() is { } cutOff) cutOff.supplyCutUntilTick = Now + CutDays * GenDate.TicksPerDay;
            switch (l.role)
            {
                case RingRole.Dealer:
                case RingRole.Courier:
                    if (p?.Inmate() is not { } c) return;
                    int taken = Confiscate(p, c);
                    PrisonMap pm = PrisonMap.Of(p.MapHeld);
                    // (On record only for what was taken: a link known from a tip, a tap or a session has nothing on them to be written up for.)
                    if (pm != null && taken > 0)
                    {
                        MisconductUtility.Record(p, c, pm, RPContrabandDefOf.RP_ContrabandPossession, evidence: true, chronicle: false);
                        if (l.role == RingRole.Dealer) MisconductUtility.Record(p, c, pm, RPReputationDefOf.RP_Dealing, evidence: true, chronicle: false);
                    }
                    StopSupply(c, tipOff: true);
                    break;
                case RingRole.Relative:
                    // (The garden's patch is pulled up with it.)
                    if (IsGarden(l)) GardenPatches.Clear(map, l.pawnId);
                    // The supplier is barred by name (see SupplierBarred): nothing is put on the file of the inmate they come to see, who may be one the warden has not found, and a search a tip has set up is still to come.
                    break;
                default:
                    if (p?.GetComp<CompStaff>() is { corrupt: true, doubleAgent: false } sc) CorruptionSystem.Dismiss(p, sc, arrest: true);
                    break;
            }
        }

        /// <summary>The outside supplier of this name (a relative's label, as a visit or a letter gives it) was taken out of a ring lately: they come to nobody and write to nobody for a while.</summary>
        public static bool SupplierBarred(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            foreach (StoryArc a in Rings())
                foreach (RingLink l in a.ring.links)
                    if (l.handled && l.role == RingRole.Relative && l.name == label && Now - l.handledTick < VisitBanDays * GenDate.TicksPerDay) return true;
            return false;
        }

        /// <summary>No visit for a long while, nothing arranged, no tip-off pending.</summary>
        private static void StopSupply(CompInmate c, bool tipOff)
        {
            c.visitBanUntilTick = Mathf.Max(c.visitBanUntilTick, Now + VisitBanDays * GenDate.TicksPerDay);
            c.smuggleArranged = false;
            c.nextVisitFlagged = false;
            c.visitorContraband = null;
            if (tipOff) c.suspected = false;
        }

        /// <summary>Everything the inmate carries or has hidden in the cell is taken (and counted as found, and put on the map of finds).</summary>
        public static int Confiscate(Pawn p, CompInmate c)
        {
            PrisonMap pm = PrisonMap.Of(p.MapHeld);
            var found = new List<Thing>();
            ContrabandUtility.CarriedContraband(p, found);
            found.AddRange(c.stash);
            int n = 0;
            foreach (Thing t in found)
            {
                ContrabandKind kind = ContrabandUtility.Ext(t.def).kind;
                if (t.ParentHolder is Pawn_EquipmentTracker eq) eq.Remove((ThingWithComps)t);
                else if (t.holdingOwner != null) t.holdingOwner.Remove(t);
                ToolCount.Notify_Taken(t, ToolSource.Raid);
                t.Destroy();
                n++;
                pm?.AddFind(p.PositionHeld, kind, FindSource.Raid);
            }
            PrisonGame.Instance.stats.contrabandFound += n;
            PrisonGame.Instance.today.contrabandFound += n;
            return n;
        }

        /// <summary>The warden turns this link: an officer becomes a double agent (as internal affairs would make them), a courier an informant. Returns whether it worked.</summary>
        public static bool Turn(StoryArc arc, RingLink l, Map map)
        {
            if (!Turnable(arc).Contains(l)) return false;
            Pawn p = PawnOf(map, l.pawnId);
            if (l.role == RingRole.Officer) CorruptionSystem.Turn(p, p.GetComp<CompStaff>(), map);
            else
            {
                CompInmate c = p.Inmate();
                if (!c.reputations.Contains(ReputationDefOf.RP_Snitch)) c.reputations.Add(ReputationDefOf.RP_Snitch);
                if (!c.revealed.Contains(ReputationDefOf.RP_Snitch)) c.revealed.Add(ReputationDefOf.RP_Snitch);
            }
            l.turned = true;
            PrisonGame.Instance.stats.ringMembersTurned++;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_RingTurned".Translate(l.name), p);
            Messages.Message("RP_RingTurnedMessage".Translate(l.name), LookAt(p), MessageTypeDefOf.PositiveEvent, historical: false);
            return true;
        }

        // ---- the case put to the warden --------------------------------------------------------------------------

        /// <summary>Hot enough, and something the warden can act on: the letter. Returns whether it was sent.</summary>
        public static bool MaybeAsk(StoryArc arc, Map map, bool force = false)
        {
            SmugglingRing ring = arc.ring;
            if (arc.done || arc.stage != 0 || !Visible(arc) || map == null) return false;
            if (!force && (ring.heat < AskHeat || Now - ring.lastAskTick < AskCooldownDays * GenDate.TicksPerDay)) return false;
            if (Bustable(arc).Count == 0 && Turnable(arc).Count == 0) return false;
            ring.lastAskTick = Now;
            // (What a bust would take out, and nothing else: a link already taken out or turned is no part of the choice.)
            List<RingLink> bustable = Bustable(arc);
            List<string> lines = bustable.Select(l => "  - " + LinkLine(l)).ToList();
            List<Pawn> targets = Named(bustable, map, spawnedOnly: true);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "RP_RingAskLabel".Translate(), "RP_RingAskText".Translate(string.Join("\n", lines), ring.heat.ToString("F0")), map, new LookTargets(targets));
            letter.arcId = arc.id;
            letter.pawns.AddRange(targets);
            letter.StartTimeout(GenDate.TicksPerHour * arc.def.decisionHours);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }

        // ---- what the Intel tab draws ----------------------------------------------------------------------------

        private static readonly RingRole[] ChainOrder = { RingRole.Relative, RingRole.Officer, RingRole.Courier, RingRole.Dealer };

        /// <summary>
        /// The chain from the outside in: the links the warden knows by name (outside supplier, officer, couriers, dealer), then, while there is a link left to find, one marker
        /// for "more links suspected" (never a mark per link, or a count: how many are left would tell the warden what happened out of their sight).
        /// </summary>
        public static List<RingPart> Chain(StoryArc arc)
        {
            var parts = new List<RingPart>();
            foreach (RingRole role in ChainOrder)
                foreach (RingLink l in arc.ring.links)
                    if (l.role == role && l.known) parts.Add(new RingPart(DisplayName(l), l));
            if (MoreSuspected(arc)) parts.Add(new RingPart("RP_IntelRingMore".Translate(), null));
            return parts;
        }

        /// <summary>The pawn to jump to for a ring: a known link that is on the map, the dealer first.</summary>
        public static Pawn Focus(StoryArc arc)
        {
            Map map = arc.A?.MapHeld;
            // (In role order, the dealer first: a pass per role, not a sort, since the Intel tab asks per ring per event.)
            for (RingRole role = RingRole.Dealer; role <= RingRole.Officer; role++)
                foreach (RingLink l in arc.ring.links)
                    if (l.role == role && l.known && role != RingRole.Relative && PawnOf(map, l.pawnId) is { Spawned: true } p) return p;
            return null;
        }

        /// <summary>For the snapshot of a save/load round trip: every ring, in one string.</summary>
        public static string Describe() => string.Join(";", Rings().OrderBy(a => a.id).Select(a => $"{a.id}:{a.stage}:{a.A?.thingIDNumber}:{a.startTick}:{a.ring.Describe()}"));
    }

    /// <summary>The story arc of one ring: the dealer is its cast, the rest is read from the game (see <see cref="RingSystem"/>).</summary>
    public class SmugglingRingArc : StoryArcWorker
    {
        private static readonly string[] Keys = { "RP_Ring_Bust", "RP_Ring_Wait", "RP_Ring_TurnOfficer", "RP_Ring_TurnCourier" };

        public override bool Valid(StoryArc arc) => base.Valid(arc) && RingSystem.IsDealer(arc.A?.Inmate());

        public override void Hourly(StoryArc arc, Map map) => RingSystem.Hourly(arc, map);

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override int TimeoutChoice(StoryArc arc) => 1;

        public override string Disabled(StoryArc arc, int option)
        {
            if (arc.ring == null) return null;
            // (A ring already busted from the Intel tab, or ended, is not the one this letter was about: waiting is all that is left.)
            if (arc.stage != 0 || arc.done) return option == 1 ? null : "RP_RingAlreadyDealtWith".Translate().ToString();
            return option switch
            {
                0 => RingSystem.Bustable(arc).Count == 0 ? "RP_RingNothingToBust".Translate().ToString() : null,
                2 => RingSystem.Turnable(arc, RingRole.Officer).Count == 0 ? "RP_RingNoOneToTurn".Translate().ToString() : null,
                3 => RingSystem.Turnable(arc, RingRole.Courier).Count == 0 ? "RP_RingNoOneToTurn".Translate().ToString() : null,
                _ => null,
            };
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            if (arc.ring == null || arc.stage != 0 || arc.done) return;
            switch (option)
            {
                case 0:
                    RingSystem.Bust(arc, map);
                    break;
                case 2:
                case 3:
                    if (RingSystem.Turnable(arc, option == 2 ? RingRole.Officer : RingRole.Courier) is { Count: > 0 } who) RingSystem.Turn(arc, RingSystem.BestToTurn(who), map);
                    break;
            }
        }

        /// <summary>The dealer's leaving ends it; anyone else leaving takes their link with them.</summary>
        public override void Notify_Leaving(StoryArc arc, Pawn p)
        {
            if (arc.A == p)
            {
                base.Notify_Leaving(arc, p);
                return;
            }
            if (arc.ring == null) return;
            // (The supplier of a courier who is gone, if the warden has found them, stays: they come for the dealer's people now. A gardener's patch is overgrown with them, and their garden link goes too.)
            foreach (RingLink l in arc.ring.links)
                if (l.role == RingRole.Relative && l.known && !l.garden && l.pawnId == p.thingIDNumber && arc.A != null) l.pawnId = arc.A.thingIDNumber;
            arc.ring.links.RemoveAll(l => l.pawnId == p.thingIDNumber && l.role != RingRole.Dealer);
        }

        /// <summary>A ring the warden knew of falls apart: said once, without naming a link the warden does not know.</summary>
        public override void Cancelled(StoryArc arc)
        {
            if (arc.ring == null || !RingSystem.Visible(arc)) return;
            RingLink dealer = arc.ring.Dealer;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, dealer is { known: true } ? "RP_Chr_RingEndedDealer".Translate(dealer.name) : "RP_Chr_RingEnded".Translate());
        }
    }
}
