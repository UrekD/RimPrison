using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Blows a war rival has landed on a held patch, net of the holder's answers (see <see cref="TurfClaim.Raids"/>).</summary>
    public class TurfRaid : IExposable
    {
        public GangDef gang;
        public int net;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref gang, "gang");
            Scribe_Values.Look(ref net, "net");
        }
    }

    /// <summary>A patch of a zone a gang holds. The patch is named by the index of its first cell (<see cref="ZonePatch.anchor"/>).</summary>
    public class TurfClaim : IExposable
    {
        public PrisonZoneDef zone;
        public int anchor;
        public GangDef gang;
        public int sinceTick;
        /// <summary>Per raiding gang, the blows it has landed here minus the answers of the holder (settled when the war between the two ends).</summary>
        public List<TurfRaid> raiders = new List<TurfRaid>();
        /// <summary>The cells of the patch as last seen (not saved: worked out after a load), so a claim finds its patch again after a repaint.</summary>
        internal List<IntVec3> footprint;

        /// <summary>Blows from <paramref name="raider"/> not yet answered (negative: answers ahead of the blows).</summary>
        public int Raids(GangDef raider)
        {
            foreach (TurfRaid r in raiders)
                if (r.gang == raider) return r.net;
            return 0;
        }

        public void AddRaids(GangDef raider, int delta)
        {
            foreach (TurfRaid r in raiders)
                if (r.gang == raider)
                {
                    r.net += delta;
                    return;
                }
            raiders.Add(new TurfRaid { gang = raider, net = delta });
        }

        public void ClearRaids(GangDef raider) => raiders.RemoveAll(r => r.gang == raider);

        public void ExposeData()
        {
            Scribe_Defs.Look(ref zone, "zone");
            Scribe_Values.Look(ref anchor, "anchor");
            Scribe_Defs.Look(ref gang, "gang");
            Scribe_Values.Look(ref sinceTick, "since");
            Scribe_Collections.Look(ref raiders, "raiders", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                raiders ??= new List<TurfRaid>();
                raiders.RemoveAll(r => r?.gang == null);
            }
        }
    }

    /// <summary>
    /// A map's gang turf: who holds which patch (saved, so a flip is not told twice after a load) and the day's presence
    /// tally behind the next verdict (not saved: a load part-way through a day judges the hours seen after it).
    /// </summary>
    public class TurfBook : IExposable
    {
        public List<TurfClaim> claims = new List<TurfClaim>();
        private readonly Dictionary<int, TurfClaim> byAnchor = new Dictionary<int, TurfClaim>();
        private int reconciledVersion = -1;

        internal sealed class GangCount
        {
            public int sum, peak, now;
        }

        internal sealed class PatchTally
        {
            public int people;
            public readonly Dictionary<GangDef, GangCount> gangs = new Dictionary<GangDef, GangCount>();
        }

        internal readonly Dictionary<int, PatchTally> tallies = new Dictionary<int, PatchTally>();

        public TurfClaim At(int anchor) => byAnchor.TryGetValue(anchor, out TurfClaim c) ? c : null;

        public void Set(ZoneGrid zones, PrisonZoneDef zone, int anchor, GangDef gang, int sinceTick)
        {
            TurfClaim c = At(anchor);
            if (c == null)
            {
                c = new TurfClaim { zone = zone, anchor = anchor };
                claims.Add(c);
                byAnchor[anchor] = c;
            }
            c.gang = gang;
            c.sinceTick = sinceTick;
            c.raiders.Clear();
            // (Taken now, from the patch as it is: a repaint before the next reconcile must still find the claim's patch.)
            c.footprint = FootprintOf(zones, zones.PatchAt(anchor));
        }

        public void Remove(int anchor)
        {
            if (byAnchor.Remove(anchor)) claims.RemoveAll(c => c.anchor == anchor);
        }

        /// <summary>Replaces every claim (a test putting the map's turf back).</summary>
        public void Replace(IEnumerable<TurfClaim> to)
        {
            claims = to.ToList();
            Reindex();
            reconciledVersion = -1;
        }

        private void Reindex()
        {
            byAnchor.Clear();
            foreach (TurfClaim c in claims) byAnchor[c.anchor] = c;
        }

        private static List<IntVec3> FootprintOf(ZoneGrid zones, int patchIndex)
        {
            if (patchIndex < 0) return null;
            var cells = new List<IntVec3>(zones.Patches[patchIndex].cells);
            foreach (IntVec3 c in zones.CellsOf(zones.Patches[patchIndex].zone))
                if (zones.PatchAt(c) == patchIndex) cells.Add(c);
            return cells;
        }

        /// <summary>
        /// The patch a claim is on now: the one of its zone that holds most of the cells its patch had (a claim that has not seen its patch yet:
        /// the one under its anchor). -1 when there is none; <paramref name="overlap"/> is how many cells it shares.
        /// </summary>
        private static int PatchOf(ZoneGrid zones, TurfClaim c, out int overlap)
        {
            overlap = 0;
            List<ZonePatch> patches = zones.Patches;
            if (c.footprint == null)
            {
                int at = zones.PatchAt(c.anchor);
                if (at < 0 || patches[at].zone != c.zone) return -1;
                overlap = 1;
                return at;
            }
            var counts = new Dictionary<int, int>();
            foreach (IntVec3 cell in c.footprint)
            {
                int pi = zones.PatchAt(cell);
                if (pi >= 0 && patches[pi].zone == c.zone) counts[pi] = (counts.TryGetValue(pi, out int n) ? n : 0) + 1;
            }
            int best = -1;
            foreach (KeyValuePair<int, int> kv in counts)
                if (kv.Value > overlap || kv.Value == overlap && kv.Key < best) (best, overlap) = (kv.Key, kv.Value);
            return best;
        }

        /// <summary>
        /// After the zones were painted: a claim follows its patch (to the patch that has most of the cells its own had: one that grew over a
        /// lower cell is named by that cell now, and one that lost its first cell by the next), and goes quietly when it has no patch left, no
        /// longer the same zone, or merged into one a claim with more of it holds. Cheap when nothing changed. Returns where each claim whose
        /// patch changed its name moved (old anchor to new), for whatever else names a patch by its anchor (a war's stake).
        /// </summary>
        public IReadOnlyDictionary<int, int> Reconcile(ZoneGrid zones)
        {
            if (reconciledVersion == zones.Version) return NoMoves;
            reconciledVersion = zones.Version;
            List<ZonePatch> patches = zones.Patches;
            var patchOf = new Dictionary<TurfClaim, int>();
            var overlapOf = new Dictionary<TurfClaim, int>();
            var holder = new Dictionary<int, TurfClaim>();
            foreach (TurfClaim c in claims)
            {
                int pi = PatchOf(zones, c, out int overlap);
                if (pi < 0 || !c.zone.gangTurf) continue;
                patchOf[c] = pi;
                overlapOf[c] = overlap;
                if (!holder.TryGetValue(pi, out TurfClaim other) || overlap > overlapOf[other]) holder[pi] = c;
            }
            var kept = new List<TurfClaim>(claims.Count);
            Dictionary<int, int> moves = null;
            foreach (TurfClaim c in claims)
            {
                if (!patchOf.TryGetValue(c, out int pi) || holder[pi] != c) continue;
                int anchor = patches[pi].anchor;
                if (anchor != c.anchor) (moves ??= new Dictionary<int, int>())[c.anchor] = anchor;
                c.anchor = anchor;
                c.footprint = FootprintOf(zones, pi);
                kept.Add(c);
            }
            claims = kept;
            Reindex();
            return moves ?? NoMoves;
        }

        private static readonly Dictionary<int, int> NoMoves = new Dictionary<int, int>();

        internal void Present(int anchor, GangDef gang)
        {
            if (!tallies.TryGetValue(anchor, out PatchTally t)) tallies[anchor] = t = new PatchTally();
            t.people++;
            if (gang == null) return;
            if (!t.gangs.TryGetValue(gang, out GangCount gc)) t.gangs[gang] = gc = new GangCount();
            gc.sum++;
            gc.now++;
        }

        /// <summary>One hourly pass is over: the most of a gang seen in one go is kept (a lone pair never holds a place).</summary>
        internal void EndSample()
        {
            foreach (PatchTally t in tallies.Values)
                foreach (GangCount gc in t.gangs.Values)
                {
                    gc.peak = Mathf.Max(gc.peak, gc.now);
                    gc.now = 0;
                }
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref claims, "claims", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                claims ??= new List<TurfClaim>();
                claims.RemoveAll(c => c?.zone == null || c.gang == null);
                Reindex();
                reconciledVersion = -1;
            }
        }
    }

    /// <summary>
    /// Gang turf. A gang holds a yard, common room or canteen patch after a day in which 60% or more of the inmates seen
    /// there were its members (and at least three of them were there at once); it keeps it while it still makes up 40%,
    /// and loses it when it falls below that, when another gang takes it, or when nobody of the gang is left inside.
    /// Presence is counted in one pass over the inmates an hour (<see cref="Sample"/>) into a tally per patch, and judged once a day
    /// (<see cref="Daily"/>). Rival gang members on held turf are afraid and on edge, the holders are at ease and their dealers
    /// deal more; a gang war has a stake (a patch, picked when it starts), attacks prefer turf, and when the war ends the patch
    /// changes hands if the invaders landed more blows than the holders answered.
    /// Hidden gangs stay hidden: what the warden is shown (overlay, rooms line, intel, chronicle, letters) names a holder only when
    /// that gang is known (<see cref="GangKnown"/>), and a hidden gang's turf is simply not shown.
    /// </summary>
    public static class GangTurf
    {
        public const int ClaimPercent = 60, KeepPercent = 40, MinMembers = 3, RaidsToTake = 2;
        /// <summary>Hourly tension on a rival's turf; kept under the 1.2 of each rival gang member nearby (<see cref="GangUtility.TensionFromGangs"/>).</summary>
        public const float RivalTurfTension = 1f;
        public const float OwnTurfRelief = 0.6f, RivalTurfFear = 3f, OwnTurfFearRelief = 2f;
        public const float DealChance = 0.5f, DealChanceOwnTurf = 0.8f, RecruitChance = 0.1f, RecruitChanceOwnTurf = 0.2f;
        private const string TruceEnd = "RP_Chr_GangWarEnd_Truce";

        // ---- who holds what -----------------------------------------------------------------------------------------------

        /// <summary>Brings the claims up to date with the painted zones, and a war's stake with them (a stake is a patch's anchor, which moves with its claim).</summary>
        public static void Reconcile(PrisonMap pm)
        {
            IReadOnlyDictionary<int, int> moves = pm.turf.Reconcile(pm.zones);
            if (moves.Count == 0 || PrisonGame.Instance?.story.arcs is not { } arcs) return;
            foreach (StoryArc arc in arcs)
                if (moves.TryGetValue(arc.stake, out int to)) arc.stake = to;
        }

        /// <summary>The gang that holds the patch under a cell, known or not (null: no patch, or nobody holds it).</summary>
        public static GangDef HolderAt(PrisonMap pm, IntVec3 c)
        {
            if (pm == null) return null;
            int pi = pm.zones.PatchAt(c);
            if (pi < 0) return null;
            Reconcile(pm);
            return pm.turf.At(pm.zones.Patches[pi].anchor)?.gang;
        }

        public static GangDef HolderAt(Pawn p) => p.Spawned ? HolderAt(PrisonMap.Of(p.Map), p.Position) : null;

        /// <summary>The warden knows this gang: an inmate of it is on file as a member.</summary>
        public static bool GangKnown(Map map, GangDef g)
        {
            if (g == null) return false;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColony;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { gangKnown: true } c && c.gang == g && !inmates[i].guest.Released) return true;
            return false;
        }

        /// <summary>Every gang the warden knows (an inmate of it is on file as a member), added to <paramref name="into"/>.</summary>
        public static void KnownGangs(Map map, HashSet<GangDef> into)
        {
            List<Pawn> inmates = map.mapPawns.PrisonersOfColony;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { gangKnown: true, gang: not null } c && !inmates[i].guest.Released) into.Add(c.gang);
        }

        /// <summary>The holder of the patch under a cell if the warden may be told (a known gang), else null.</summary>
        public static GangDef KnownHolderAt(PrisonMap pm, IntVec3 c, Map map)
        {
            GangDef holder = HolderAt(pm, c);
            return GangKnown(map, holder) ? holder : null;
        }

        public static bool OnOwnTurf(Pawn p, CompInmate c, PrisonMap pm) => c.gang != null && HolderAt(pm, p.Position) == c.gang;

        /// <summary>
        /// The turf term of <see cref="GangUtility.TensionFromGangs"/>: tension on a rival gang's turf, relief on their own. <paramref name="cause"/>
        /// is what to call the tension, and names turf only when the warden knows both gangs (else it is trouble on the wing: the cause is
        /// stored and read back in places that do not check what the warden knows).
        /// </summary>
        public static float TensionTerm(Pawn p, CompInmate c, PrisonMap pm, out string cause)
        {
            cause = null;
            if (c.gang == null)
            {
                // (An ex-member on their old gang's ground: as on a rival's, and never named, which would tell of a gang the warden may not know.)
                if (!GangExit.ExActive(c) || HolderAt(pm, p.Position) != c.exGang) return 0f;
                cause = "RP_Cause_WingTrouble";
                return RivalTurfTension;
            }
            GangDef holder = HolderAt(pm, p.Position);
            if (holder == null) return 0f;
            if (holder == c.gang) return -OwnTurfRelief;
            if (!GangUtility.AreRivals(holder, c.gang)) return 0f;
            cause = c.gangKnown && GangKnown(p.Map, holder) ? "RP_Cause_Turf" : "RP_Cause_WingTrouble";
            return RivalTurfTension;
        }

        /// <summary>The turf term of fear: positive on a rival's turf, negative (relief) on their own.</summary>
        public static float FearTerm(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (c.gang == null) return GangExit.ExActive(c) && HolderAt(pm, p.Position) == c.exGang ? RivalTurfFear : 0f;
            GangDef holder = HolderAt(pm, p.Position);
            if (holder == null) return 0f;
            return holder == c.gang ? -OwnTurfFearRelief : GangUtility.AreRivals(holder, c.gang) ? RivalTurfFear : 0f;
        }

        // ---- presence, and the daily verdict ------------------------------------------------------------------------------

        /// <summary>
        /// Hourly, in the prison's hour pass: one pass over the inmates, counting who stands on which turf patch. The same pass counts, for the rumour mill, who of each wing stood in a shared patch with others of it (see
        /// <see cref="Rumours.Saw"/>; only while a rumour is live). <paramref name="turf"/> false: the rumour count alone (a check's, which must not add to the day's turf tally).
        /// </summary>
        public static void Sample(Map map, PrisonMap pm, bool turf = true)
        {
            ZoneGrid zones = pm.zones;
            List<ZonePatch> patches = zones.Patches;
            bool mill = Rumours.BeginSample(pm);
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (p.Inmate() is not { activeFlag: true } c || p.Downed) continue;
                int pi = zones.PatchAt(p.Position);
                bool shared = pi >= 0 && patches[pi].zone.gangTurf;
                if (mill) Rumours.Saw(c.security, shared ? pi : -1);
                if (!shared || !turf) continue;
                // (At a funeral, or just back from one: a gang's members in one place for the hour are nobody's turf: see Funerals.Away.)
                if (Funerals.Away(p)) continue;
                // (An ex-member of a gang is nobody's to count while the old gang hates them: not one of the people there, not one of a gang.)
                if (c.gang == null && GangExit.ExActive(c)) continue;
                pm.turf.Present(patches[pi].anchor, c.gang);
            }
            if (turf) pm.turf.EndSample();
        }

        private static bool Anyone(Map map, GangDef g)
        {
            List<Pawn> inmates = map.mapPawns.PrisonersOfColony;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate()?.gang == g && !inmates[i].guest.Released && !inmates[i].Dead) return true;
            return false;
        }

        /// <summary>Once a day: judges the day's tally, per patch, and starts the next day's.</summary>
        public static void Daily(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            TurfBook book = pm.turf;
            Reconcile(pm);
            var changes = new List<(int patchIndex, GangDef from, GangDef to)>();
            List<ZonePatch> patches = pm.zones.Patches;
            for (int pi = 0; pi < patches.Count; pi++)
            {
                ZonePatch patch = patches[pi];
                if (!patch.zone.gangTurf) continue;
                // (A patch kept for a gang by a deal with the warden is not judged: the deal's end lets it go.)
                if (GangDeals.KeepsPatch(patch.anchor)) continue;
                GangDef holder = book.At(patch.anchor)?.gang;
                bool gone = holder != null && !Anyone(map, holder);
                bool seen = book.tallies.TryGetValue(patch.anchor, out TurfBook.PatchTally t) && t.people > 0;
                GangDef winner = null;
                if (seen)
                    foreach (KeyValuePair<GangDef, TurfBook.GangCount> kv in t.gangs)
                        if (kv.Value.peak >= MinMembers && kv.Value.sum * 100 >= t.people * ClaimPercent) winner = kv.Key;
                if (winner != null)
                {
                    if (winner != holder) changes.Add((pi, holder, winner));
                }
                else if (holder != null && (gone || seen && (t.gangs.TryGetValue(holder, out TurfBook.GangCount own) ? own.sum : 0) * 100 < t.people * KeepPercent))
                    changes.Add((pi, holder, null));
            }
            book.tallies.Clear();
            foreach ((int patchIndex, GangDef from, GangDef to) in changes) Change(map, pm, patchIndex, from, to, war: false);
        }

        /// <summary>A patch changes hands (or is let go); the chronicle and a letter say so, for known gangs only.</summary>
        private static void Change(Map map, PrisonMap pm, int patchIndex, GangDef from, GangDef to, bool war)
        {
            ZonePatch patch = pm.zones.Patches[patchIndex];
            if (to == null) pm.turf.Remove(patch.anchor);
            else pm.turf.Set(pm.zones, patch.zone, patch.anchor, to, Find.TickManager.TicksGame);
            string place = PlaceName(pm.zones, patchIndex);
            bool toKnown = GangKnown(map, to), fromKnown = GangKnown(map, from);
            var cell = new IntVec3((int)patch.center.x, 0, (int)patch.center.z);
            if (toKnown)
            {
                // (Told only as far as the warden knows: a taker who is known is not said to have taken it from a gang that is not.)
                bool taken = from != null && fromKnown;
                string line = taken ? (war ? "RP_Chr_TurfWarTaken" : "RP_Chr_TurfTaken").Translate(to.label, place, from.label) : "RP_Chr_TurfClaimed".Translate(to.label, place);
                Story.Record(RPChronicleDefOf.RP_Chr_Gangs, war ? 2 : 1, line);
                Find.LetterStack.ReceiveLetter(taken ? "RP_TurfTakenLabel".Translate(to.LabelCap, place) : "RP_TurfClaimedLabel".Translate(to.LabelCap, place),
                    taken ? "RP_TurfTakenText".Translate(to.label, place, from.label) : "RP_TurfClaimedText".Translate(to.label, place),
                    taken ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent, new LookTargets(cell, map));
            }
            else if (fromKnown) Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_TurfLost".Translate(from.label, place));
        }

        /// <summary>"the yard", or "the yard (area 2)" when the map has more of them.</summary>
        public static string PlaceName(ZoneGrid zones, int patchIndex)
        {
            List<ZonePatch> patches = zones.Patches;
            PrisonZoneDef zone = patches[patchIndex].zone;
            int same = 0, nth = 0;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].zone == zone)
                {
                    same++;
                    if (i <= patchIndex) nth++;
                }
            return same > 1 ? "RP_TurfPlaceNth".Translate(zone.label, nth).ToString() : "RP_TurfPlace".Translate(zone.label).ToString();
        }

        // ---- what the warden is shown ----------------------------------------------------------------------------------------

        /// <summary>Every patch held by a gang the warden knows, with that gang (for overlays, lines and intel).</summary>
        public static IEnumerable<(int patchIndex, TurfClaim claim)> KnownTurf(Map map, PrisonMap pm)
        {
            Reconcile(pm);
            List<ZonePatch> patches = pm.zones.Patches;
            for (int pi = 0; pi < patches.Count; pi++)
                if (pm.turf.At(patches[pi].anchor) is { } claim && GangKnown(map, claim.gang)) yield return (pi, claim);
        }

        /// <summary>"held by Ravens" for a zone's row in the rooms list: the known holders of its patches (null when there are none).</summary>
        public static string ZoneLine(Map map, PrisonZoneDef zone)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || !zone.gangTurf) return null;
            var holders = new List<string>();
            foreach ((int pi, TurfClaim claim) in KnownTurf(map, pm))
                if (claim.zone == zone && !holders.Contains(claim.gang.label)) holders.Add(claim.gang.label);
            return holders.Count == 0 ? null : "RP_RoomsTurf".Translate(string.Join(", ", holders)).ToString();
        }

        /// <summary>The places a known gang holds, for the intel tab ("the yard, the canteen"); null when none.</summary>
        public static string HeldLine(Map map, GangDef gang)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return null;
            var places = new List<string>();
            foreach ((int pi, TurfClaim claim) in KnownTurf(map, pm))
                if (claim.gang == gang) places.Add(PlaceName(pm.zones, pi));
            return places.Count == 0 ? null : "RP_IntelTurfLine".Translate(string.Join(", ", places)).ToString();
        }

        // ---- gang wars ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The patch a war is fought over (the patch's anchor, -1 for none): of those held by either gang, the one the rivals are standing on
        /// most, or the biggest.
        /// </summary>
        public static int PickStake(Map map, GangDef a, GangDef b)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return -1;
            Reconcile(pm);
            var rivalsOn = new Dictionary<int, int>();
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                GangDef g = inmates[i].Inmate()?.gang;
                if (g != a && g != b) continue;
                int pi = pm.zones.PatchAt(inmates[i].Position);
                if (pi < 0 || pm.turf.At(pm.zones.Patches[pi].anchor) is not { } claim || claim.gang == g) continue;
                int anchor = pm.zones.Patches[pi].anchor;
                rivalsOn[anchor] = (rivalsOn.TryGetValue(anchor, out int n) ? n : 0) + 1;
            }
            int best = -1;
            long bestScore = -1;
            foreach (ZonePatch patch in pm.zones.Patches)
            {
                if (pm.turf.At(patch.anchor) is not { } claim || claim.gang != a && claim.gang != b) continue;
                long score = (rivalsOn.TryGetValue(patch.anchor, out int n) ? n : 0) * 100000L + patch.cells;
                if (score <= bestScore) continue;
                best = patch.anchor;
                bestScore = score;
            }
            return best;
        }

        /// <summary>"the yard" for a war's stake (null for none, or a patch that is gone).</summary>
        public static string StakeName(Map map, int stake)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || stake < 0) return null;
            int pi = pm.zones.PatchAt(stake);
            return pi < 0 ? null : PlaceName(pm.zones, pi);
        }

        /// <summary>How much a fight at a cell is on the war's ground: 2 on the stake, 1 on a place either gang holds, 0 elsewhere.</summary>
        public static int WarRank(PrisonMap pm, IntVec3 c, GangDef a, GangDef b, int stake)
        {
            int pi = pm.zones.PatchAt(c);
            if (pi < 0) return 0;
            int anchor = pm.zones.Patches[pi].anchor;
            if (anchor == stake) return 2;
            Reconcile(pm);
            GangDef holder = pm.turf.At(anchor)?.gang;
            return holder != null && (holder == a || holder == b) ? 1 : 0;
        }

        /// <summary>
        /// A war attack landed at a cell: on the victims' own ground it is a raid the holders have yet to answer; on the attackers' own, an answer.
        /// Kept as a net count per raiding gang, so it does not matter which came first.
        /// </summary>
        public static void Notify_WarBlow(Map map, IntVec3 at, GangDef attacker, GangDef victim)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            int pi = pm.zones.PatchAt(at);
            if (pi < 0) return;
            Reconcile(pm);
            if (pm.turf.At(pm.zones.Patches[pi].anchor) is not { } claim) return;
            if (claim.gang == victim) claim.AddRaids(attacker, 1);
            else if (claim.gang == attacker) claim.AddRaids(victim, -1);
        }

        /// <summary>
        /// The war between two gangs is over: a patch one of them holds goes to the other if that one raided it at least <see cref="RaidsToTake"/>
        /// times more than the holders answered, and still has someone inside (not after a truce, which leaves everything as it was). The raids of
        /// these two on each other's turf are forgotten either way; those of any other gang are not.
        /// </summary>
        public static void SettleWar(Map map, GangDef a, GangDef b, bool peace)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            Reconcile(pm);
            foreach (TurfClaim claim in pm.turf.claims.ToArray())
            {
                if (claim.gang != a && claim.gang != b) continue;
                GangDef raider = claim.gang == a ? b : a;
                int raids = claim.Raids(raider);
                claim.ClearRaids(raider);
                if (peace || raids < RaidsToTake || !Anyone(map, raider)) continue;
                int pi = pm.zones.PatchAt(claim.anchor);
                if (pi >= 0) Change(map, pm, pi, claim.gang, raider, war: true);
            }
        }

        /// <summary>The war arc's ending key that means a truce (nothing changes hands).</summary>
        public static bool IsPeace(string endKey) => endKey == TruceEnd;
    }
}
