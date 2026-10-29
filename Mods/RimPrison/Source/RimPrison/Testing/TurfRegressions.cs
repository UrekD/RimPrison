using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of gang turf (B1-B6, and the gaps it found in <see cref="TurfTest"/>: patches that change shape,
    /// a war ending through its own hook, a hidden gang and a known one in either order, the riot letter's cause). Each is its own method,
    /// called once from FireAllEvents, and each fails without the fix it is named for. They work on throwaway inmates standing in the yard
    /// (<see cref="TurfScene"/>) and put back what they touch in a finally (zones painted, turf, wars, truces, who is in a gang).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>A map's yard with nine throwaway inmates in it, the map's turf, wars and gangs made empty, and everything put back by <see cref="Restore"/>.</summary>
        private sealed class TurfScene
        {
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly TurfBook book;
            public readonly GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"),
                brotherhood = DefDatabase<GangDef>.GetNamed("RP_Gang_Brotherhood");
            public readonly ZonePatch yard;
            public readonly string place;
            /// <summary>Every cell of the yard patch, and those of them the crew can stand on (not the patch's first cell, which some checks paint over).</summary>
            public readonly List<IntVec3> yardAll, crewCells;
            public readonly IntVec3 spot;
            public readonly List<Pawn> crew = new List<Pawn>();
            private readonly PrisonGame g = PrisonGame.Instance;
            private readonly Rollback r;
            private readonly List<(CompInmate c, GangDef gang, bool active)> real0;
            private readonly List<TurfClaim> claims0;
            private readonly List<KeyValuePair<int, TurfBook.PatchTally>> tallies0;
            private readonly List<GangTruce> truces0;
            private readonly List<StoryArc> arcs0;
            private readonly Dictionary<StoryArcDef, int> lastStart0;
            private readonly int nextArc0;
            private readonly Dictionary<IntVec3, PrisonZoneDef> painted = new Dictionary<IntVec3, PrisonZoneDef>();

            private TurfScene(Map map, Rollback r, ZonePatch yard, List<IntVec3> yardAll, List<IntVec3> crewCells)
            {
                this.map = map;
                this.r = r;
                this.yard = yard;
                this.yardAll = yardAll;
                this.crewCells = crewCells;
                spot = crewCells[0];
                pm = PrisonMap.Of(map);
                book = pm.turf;
                place = GangTurf.PlaceName(pm.zones, pm.zones.PatchAt(yard.anchor));
                // (Taken now, before anything is changed.)
                real0 = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).Select(c => (c, c.gang, c.activeFlag)).ToList();
                claims0 = book.claims.ToList();
                tallies0 = book.tallies.ToList();
                truces0 = g.story.truces;
                arcs0 = g.story.arcs;
                lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                nextArc0 = g.story.nextArcId;
            }

            /// <summary>The scene for the biggest yard patch, or null (with a problem noted) when the prison has none to stand on.</summary>
            public static TurfScene Begin(Map map, Rollback r, string check)
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<ZonePatch> patches = pm.zones.Patches;
                int yardIndex = -1;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone == RPDefOf.RP_Yard && (yardIndex < 0 || patches[i].cells > patches[yardIndex].cells)) yardIndex = i;
                if (yardIndex < 0)
                {
                    problems0.Add($"{check}: the prison has no yard patch (four cells or more) to hold");
                    return null;
                }
                var all = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => pm.zones.PatchAt(c) == yardIndex).ToList();
                // (Highest cell first; the patch's first cell is left out, so that painting over it moves nobody off the patch.)
                var standable = all.Where(c => c.Standable(map) && map.cellIndices.CellToIndex(c) != patches[yardIndex].anchor).OrderByDescending(c => map.cellIndices.CellToIndex(c)).ToList();
                if (standable.Count == 0)
                {
                    problems0.Add($"{check}: no standable yard cell to stand on");
                    return null;
                }
                return new TurfScene(map, r, patches[yardIndex], all, standable);
            }

            /// <summary>Makes the map's turf, wars and truces empty and nobody (but the crew) part of a gang or counted; the crew stand in the yard.</summary>
            public void Arm()
            {
                foreach (CompInmate c in real0.Select(x => x.c)) (c.gang, c.activeFlag) = (null, false);
                book.Replace(new TurfClaim[0]);
                book.tallies.Clear();
                g.story.truces = new List<GangTruce>();
                g.story.arcs = arcs0.Where(a => a.def != RPGangWarDefOf.RP_Arc_GangWar).ToList();
                for (int i = 0; i < 9; i++)
                {
                    Pawn p = Throwaway(map, r);
                    crew.Add(p);
                    Place(p, crewCells[i % crewCells.Count]);
                    p.Inmate().tension = 0f;
                }
            }

            /// <summary>Puts everything back (the crew first: one who is cast in a war and discarded settles it, which the turf put back after undoes).</summary>
            public void Restore()
            {
                foreach (Pawn p in crew)
                {
                    p.MentalState?.RecoverFromState();
                    if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                }
                Unpaint();
                g.story.arcs = arcs0;
                g.story.lastArcStart.Clear();
                foreach (KeyValuePair<StoryArcDef, int> kv in lastStart0) g.story.lastArcStart[kv.Key] = kv.Value;
                g.story.nextArcId = nextArc0;
                g.story.truces = truces0;
                book.Replace(claims0);
                book.tallies.Clear();
                foreach (KeyValuePair<int, TurfBook.PatchTally> kv in tallies0) book.tallies[kv.Key] = kv.Value;
                foreach ((CompInmate c, GangDef gang, bool active) in real0) (c.gang, c.activeFlag) = (gang, active);
            }

            public static void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            public ZoneGrid Zones => pm.zones;

            public int Index(IntVec3 c) => map.cellIndices.CellToIndex(c);

            /// <summary>The gang of crew member <paramref name="i"/>, and whether the warden knows it.</summary>
            public void Be(int i, GangDef gang, bool known = true) => (crew[i].Inmate().gang, crew[i].Inmate().gangKnown, crew[i].Inmate().activeFlag) = (gang, known && gang != null, true);

            public void Everyone(Func<int, GangDef> gangOf, bool known = true)
            {
                for (int i = 0; i < crew.Count; i++) Be(i, gangOf(i), known);
            }

            /// <summary>A day is over: the hour's pass counted who was there, and the daily pass judged it.</summary>
            public void Day()
            {
                GangTurf.Sample(map, pm);
                GangTurf.Daily(map);
            }

            public GangDef Holder() => GangTurf.HolderAt(pm, spot);

            /// <summary>The yard is held by <paramref name="gang"/> now (as a day's verdict would have it, without the line and letter).</summary>
            public void Hold(GangDef gang) => book.Set(pm.zones, yard.zone, yard.anchor, gang, Find.TickManager.TicksGame);

            // ---- the zones, painted and put back ----

            public void Paint(IntVec3 c, PrisonZoneDef zone)
            {
                if (!painted.ContainsKey(c)) painted[c] = pm.zones.ZoneAt(c);
                pm.zones.Set(c, zone);
            }

            public void Unpaint()
            {
                foreach (KeyValuePair<IntVec3, PrisonZoneDef> kv in painted) pm.zones.Set(kv.Key, kv.Value);
                painted.Clear();
            }

            /// <summary>The anchor of the patch of the yard's zone that holds most of these cells now (-1 none): what a claim on their old patch must follow.</summary>
            public int AnchorHoldingMost(IEnumerable<IntVec3> cells)
            {
                var counts = new Dictionary<int, int>();
                foreach (IntVec3 c in cells)
                {
                    int pi = pm.zones.PatchAt(c);
                    if (pi >= 0 && pm.zones.Patches[pi].zone == yard.zone) counts[pi] = (counts.TryGetValue(pi, out int n) ? n : 0) + 1;
                }
                return counts.Count == 0 ? -1 : pm.zones.Patches[counts.OrderByDescending(kv => kv.Value).First().Key].anchor;
            }

            // ---- wars ----

            /// <summary>Starts a war (<paramref name="weary"/>: one that is fought out, so that its next hour ends it), or notes a problem and returns null.</summary>
            public StoryArc War(GangDef a, GangDef b, bool weary)
            {
                g.story.lastArcStart.Remove(RPGangWarDefOf.RP_Arc_GangWar);
                StoryArc arc = GangWarArc.Start(map, a, b);
                if (arc == null)
                {
                    problems0.Add($"turf test: could not start a war between the {a.defName} and the {b.defName}");
                    return null;
                }
                if (weary)
                {
                    arc.stage = GangWarArc.TitForTat;
                    int mark = Find.TickManager.TicksGame - (GangWarArc.WearyHours + 1) * GenDate.TicksPerHour;
                    arc.markTick = mark == GangWarArc.NoMark ? mark - 1 : mark;
                }
                return arc;
            }

            /// <summary>The war's hour passes (a weary or truced war ends in it, through its own hook).</summary>
            public void Hour(StoryArc arc) => ((GangWarArc)arc.def.Worker).Hourly(arc, map);

            /// <summary>An attack by <paramref name="attacker"/> on a member of <paramref name="victim"/> lands in the yard.</summary>
            public void Blow(GangDef attacker, GangDef victim) => GangTurf.Notify_WarBlow(map, spot, attacker, victim);

            public void ClearTruces() => g.story.truces.Clear();

            public void Truce(GangDef a, GangDef b) => g.story.truces.Add(new GangTruce { a = a, b = b, untilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay });

            // ---- what was told ----

            public (HashSet<ChronicleEntry> entries, HashSet<Letter> letters) Mark() =>
                (new HashSet<ChronicleEntry>(g.story.entries), new HashSet<Letter>(Find.LetterStack.LettersListForReading));

            /// <summary>The gang lines of the chronicle written since the mark.</summary>
            public List<string> LinesSince((HashSet<ChronicleEntry> entries, HashSet<Letter> letters) mark) =>
                g.story.entries.Where(e => e.cat == RPChronicleDefOf.RP_Chr_Gangs && !mark.entries.Contains(e)).Select(e => e.text).ToList();

            /// <summary>The lines since the mark that are about the yard changing hands (a war's own end names its gangs, not the place).</summary>
            public List<string> TurfLinesSince((HashSet<ChronicleEntry> entries, HashSet<Letter> letters) mark) => LinesSince(mark).Where(l => l.Contains(place)).ToList();

            /// <summary>The letters sent since the mark, label and text.</summary>
            public List<string> LettersSince((HashSet<ChronicleEntry> entries, HashSet<Letter> letters) mark) =>
                Find.LetterStack.LettersListForReading.Where(l => !mark.letters.Contains(l)).Select(l => l.Label.ToString() + " | " + (l is ChoiceLetter c ? c.Text.ToString() : "")).ToList();
        }

        private static string Told(List<string> lines, List<string> letters) => $"lines [{string.Join(" / ", lines)}], letters [{string.Join(" / ", letters.Select(l => l.Split('|')[0].Trim()))}]";

        // ---- B3: a claim follows its patch through a repaint --------------------------------------------------------------

        /// <summary>
        /// A holder's claim follows its patch when the patch changes: the warden paints over the patch's first cell and the claim is still the
        /// gang's (named by the patch's new first cell), with no new "now hold" line or letter a day later; a patch repainted as another zone
        /// loses its claim without a word; and two patches that merge keep the claim that had more of the merged patch.
        /// </summary>
        private static void CheckTurfClaimFollowsItsPatch(Map map)
        {
            Guarded("turf claim follows patch", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf reconcile");
                if (s == null) return;
                try
                {
                    s.Arm();
                    s.Everyone(i => i < 6 ? s.ravens : null);
                    s.Day();
                    if (s.Holder() != s.ravens)
                    {
                        problems0.Add("turf reconcile: the Ravens did not claim the yard to begin with");
                        return;
                    }
                    int oldAnchor = s.yard.anchor;
                    var mark = s.Mark();

                    // The patch's first cell is painted over.
                    IntVec3 first = map.cellIndices.IndexToCell(oldAnchor);
                    s.Paint(first, null);
                    int expected = s.AnchorHoldingMost(s.yardAll.Where(c => c != first));
                    GangTurf.Reconcile(s.pm);
                    TurfClaim followed = expected < 0 ? null : s.book.At(expected);
                    bool follows = expected >= 0 && expected != oldAnchor && followed?.gang == s.ravens && s.book.claims.Count == 1 && s.book.At(oldAnchor) == null;
                    s.Day();
                    bool quiet = s.LinesSince(mark).Count == 0 && s.LettersSince(mark).Count == 0 && s.book.claims.Count == 1 && s.book.At(expected)?.gang == s.ravens;
                    if (!follows || !quiet)
                        problems0.Add($"turf reconcile: the claim did not follow its patch when the patch's first cell was painted over (claim on {expected}: {followed?.gang?.defName}, claims {s.book.claims.Count}, old anchor still claimed {s.book.At(oldAnchor) != null}); "
                                      + $"a day later it {(quiet ? "was quiet" : "told: " + Told(s.LinesSince(mark), s.LettersSince(mark)))}");

                    // The rest of the patch is painted as another zone: the claim goes, quietly.
                    PrisonZoneDef other = DefDatabase<PrisonZoneDef>.AllDefsListForReading.First(z => !z.gangTurf);
                    foreach (IntVec3 c in s.yardAll) s.Paint(c, other);
                    GangTurf.Reconcile(s.pm);
                    bool gone = s.book.claims.Count == 0 && s.LinesSince(mark).Count == 0 && s.LettersSince(mark).Count == 0;
                    if (!gone) problems0.Add($"turf reconcile: a patch repainted as {other.defName} kept its claim ({s.book.claims.Count}) or told of losing it ({Told(s.LinesSince(mark), s.LettersSince(mark))})");
                    s.Unpaint();

                    // Two patches merge: the claim that had more of the merged patch keeps it (the other, listed first, is dropped).
                    s.book.Replace(new TurfClaim[0]);
                    IntVec3 origin = IntVec3.Invalid;
                    for (int x = 2; x < map.Size.x - 8 && !origin.IsValid; x++)
                        for (int z = 2; z < map.Size.z - 5 && !origin.IsValid; z++)
                        {
                            bool free = true;
                            for (int dx = -1; dx <= 6 && free; dx++)
                                for (int dz = -1; dz <= 2 && free; dz++) free = s.Zones.ZoneAt(new IntVec3(x + dx, 0, z + dz)) == null;
                            if (free) origin = new IntVec3(x, 0, z);
                        }
                    if (!origin.IsValid)
                    {
                        problems0.Add("turf reconcile: no free ground to paint two yard patches on");
                        return;
                    }
                    // (Three columns and two rows on the left, two columns on the right, one column of gap between.)
                    foreach (int dx in new[] { 0, 1, 2, 4, 5 })
                        foreach (int dz in new[] { 0, 1 }) s.Paint(origin + new IntVec3(dx, 0, dz), RPDefOf.RP_Yard);
                    int left = s.Index(origin), right = s.Index(origin + new IntVec3(4, 0, 0));
                    s.book.Set(s.Zones, RPDefOf.RP_Yard, right, s.saints, 1);
                    s.book.Set(s.Zones, RPDefOf.RP_Yard, left, s.ravens, 1);
                    GangTurf.Reconcile(s.pm);
                    bool apart = s.book.claims.Count == 2 && s.book.At(left)?.gang == s.ravens && s.book.At(right)?.gang == s.saints;
                    foreach (int dz in new[] { 0, 1 }) s.Paint(origin + new IntVec3(3, 0, dz), RPDefOf.RP_Yard);
                    GangTurf.Reconcile(s.pm);
                    bool merged = s.book.claims.Count == 1 && s.book.At(left)?.gang == s.ravens;
                    if (!apart || !merged)
                        problems0.Add($"turf reconcile: two claimed patches (6 and 4 cells) did not start as two ({apart}) or the bigger one's claim did not keep the merged patch ({merged}: {string.Join(", ", s.book.claims.Select(c => c.gang.defName + "@" + c.anchor))}, want {s.ravens.defName}@{left})");
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- B4: a war's stake follows its patch ------------------------------------------------------------------------------

        /// <summary>
        /// A war fought over the yard keeps fighting over it when the yard grows over a lower cell (its first cell, which names it, moves):
        /// the stake is the new first cell, attacks still count the yard as the stake, and it moves back when that cell is painted over.
        /// </summary>
        private static void CheckTurfStakeFollowsItsPatch(Map map)
        {
            Guarded("turf stake follows patch", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf stake");
                if (s == null) return;
                try
                {
                    s.Arm();
                    s.Everyone(i => i < 3 ? s.saints : s.ravens);
                    s.Hold(s.ravens);
                    StoryArc war = s.War(s.saints, s.ravens, weary: false);
                    if (war == null) return;
                    if (war.stake != s.yard.anchor)
                    {
                        problems0.Add($"turf stake: the war was not about the yard to begin with (stake {war.stake}, yard {s.yard.anchor})");
                        return;
                    }
                    // A free cell next to the yard with a lower index than its first cell: painting it makes it the patch's first.
                    IntVec3 ext = IntVec3.Invalid;
                    foreach (IntVec3 c in s.yardAll)
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 nb = c + d;
                            if (!ext.IsValid && nb.InBounds(map) && s.Zones.ZoneAt(nb) == null && s.Index(nb) < s.yard.anchor) ext = nb;
                        }
                    if (!ext.IsValid)
                    {
                        problems0.Add("turf stake: no free cell next to the yard, before its first, to grow it over");
                        return;
                    }
                    s.Paint(ext, RPDefOf.RP_Yard);
                    int grown = s.Zones.Patches[s.Zones.PatchAt(ext)].anchor;
                    GangTurf.Reconcile(s.pm);
                    bool moved = grown == s.Index(ext) && s.book.At(grown)?.gang == s.ravens && s.book.At(s.yard.anchor) == null && war.stake == grown;
                    bool still = GangTurf.WarRank(s.pm, s.spot, s.saints, s.ravens, war.stake) == 2 && GangTurf.StakeName(map, war.stake) == s.place;
                    if (!moved || !still)
                        problems0.Add($"turf stake: the yard grew over a lower cell and the war's stake did not follow (stake {war.stake}, patch now {grown}, claim there {s.book.At(grown)?.gang?.defName}, still the stake {still})");
                    // ...and back again when that cell is painted over.
                    s.Paint(ext, null);
                    GangTurf.Reconcile(s.pm);
                    bool back = war.stake == s.yard.anchor && s.book.At(s.yard.anchor)?.gang == s.ravens;
                    if (!back) problems0.Add($"turf stake: the stake did not follow the yard back when the cell was painted over (stake {war.stake}, want {s.yard.anchor})");
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- B1: raids belong to their own war, and a war ends through its hook ---------------------------------------------------

        /// <summary>
        /// The Saints raid the Ravens' yard twice while the Ravens are at war with the Brotherhood too. That war ending (fought out, or in a
        /// truce) must neither hand the yard to the Brotherhood nor wipe the Saints' raids; the Saints' war ending in a truce settles nothing and
        /// forgets its raids; and ending fought out (all through the war's own hourly hook) gives the Saints the yard, with its line.
        /// </summary>
        private static void CheckTurfRaidsBelongToTheirWar(Map map)
        {
            Guarded("turf raids belong to their war", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf war end");
                if (s == null) return;
                try
                {
                    s.Arm();
                    s.Everyone(i => i < 2 ? s.saints : i < 5 ? s.ravens : i < 7 ? s.brotherhood : null);
                    s.Hold(s.ravens);
                    s.Blow(s.saints, s.ravens);
                    s.Blow(s.saints, s.ravens);
                    var mark = s.Mark();

                    // The Ravens' other war is fought out.
                    StoryArc other = s.War(s.ravens, s.brotherhood, weary: true);
                    if (other == null) return;
                    s.Hour(other);
                    TurfClaim yard = s.book.At(s.yard.anchor);
                    bool otherEnded = other.done && s.Holder() == s.ravens && yard.Raids(s.saints) == 2 && s.TurfLinesSince(mark).Count == 0;
                    if (!otherEnded)
                        problems0.Add($"turf war end: the Ravens' war with the Brotherhood ending (done {other.done}) changed the yard, which only the Saints raided (holder {s.Holder()?.defName}, Saints' raids {yard.Raids(s.saints)}; {Told(s.LinesSince(mark), s.LettersSince(mark))})");

                    // ...and another is ended in a truce.
                    StoryArc truced = s.War(s.ravens, s.brotherhood, weary: false);
                    if (truced == null) return;
                    s.Truce(s.ravens, s.brotherhood);
                    s.Hour(truced);
                    s.ClearTruces();
                    bool trucedEnded = truced.done && s.Holder() == s.ravens && yard.Raids(s.saints) == 2;
                    if (!trucedEnded) problems0.Add($"turf war end: a truce between the Ravens and the Brotherhood wiped or settled the Saints' raids on the yard (done {truced.done}, holder {s.Holder()?.defName}, Saints' raids {yard.Raids(s.saints)})");

                    // The Saints' war ends in a truce: nothing changes hands and its raids are forgotten.
                    StoryArc peace = s.War(s.saints, s.ravens, weary: false);
                    if (peace == null) return;
                    s.Truce(s.saints, s.ravens);
                    s.Hour(peace);
                    s.ClearTruces();
                    bool peaceEnded = peace.done && s.Holder() == s.ravens && yard.Raids(s.saints) == 0 && s.TurfLinesSince(mark).Count == 0;
                    if (!peaceEnded) problems0.Add($"turf war end: the war ending in a truce did not leave the yard to the Ravens and forget the raids (done {peace.done}, holder {s.Holder()?.defName}, raids {yard.Raids(s.saints)}; {Told(s.LinesSince(mark), s.LettersSince(mark))})");

                    // Raided twice again, the Saints' war is fought out: the yard is theirs, with its line and letter.
                    s.Blow(s.saints, s.ravens);
                    s.Blow(s.saints, s.ravens);
                    StoryArc fought = s.War(s.saints, s.ravens, weary: true);
                    if (fought == null) return;
                    s.Hour(fought);
                    string line = "RP_Chr_TurfWarTaken".Translate(s.saints.label, s.place, s.ravens.label);
                    string letter = "RP_TurfTakenLabel".Translate(s.saints.LabelCap, s.place);
                    bool taken = fought.done && s.Holder() == s.saints && s.LinesSince(mark).Count(l => l == line) == 1 && s.LettersSince(mark).Count(l => l.StartsWith(letter)) == 1;
                    if (!taken) problems0.Add($"turf war end: the war fought out after two raids did not give the Saints the yard with its line and letter (done {fought.done}, holder {s.Holder()?.defName}; {Told(s.LinesSince(mark), s.LettersSince(mark))})");
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- B2: a war cancelled for want of a gang -----------------------------------------------------------------------------

        /// <summary>
        /// A war is cancelled when one gang has nobody left inside. The Ravens raided the Saints' yard twice and then the last Raven went: the yard
        /// stays the Saints' (it must not go to a gang with nobody in it, with a line and rival-turf effects for no one). If it is the holder who
        /// emptied, the raiders who are still inside do take it.
        /// </summary>
        private static void CheckTurfCancelledWarGivesNothingToTheEmptied(Map map)
        {
            Guarded("turf cancelled war", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf cancelled war");
                if (s == null) return;
                try
                {
                    s.Arm();
                    // The invaders are the ones who go.
                    s.Everyone(i => i < 3 ? s.saints : i < 6 ? s.ravens : null);
                    s.Hold(s.saints);
                    StoryArc war = s.War(s.saints, s.ravens, weary: true);
                    if (war == null) return;
                    s.Blow(s.ravens, s.saints);
                    s.Blow(s.ravens, s.saints);
                    var mark = s.Mark();
                    for (int i = 3; i < 6; i++) s.Be(i, null);
                    var worker = (GangWarArc)war.def.Worker;
                    bool invalid = !worker.Valid(war);
                    // (As the story's hourly pass does it.)
                    war.done = true;
                    worker.Cancelled(war);
                    bool stays = invalid && s.Holder() == s.saints && s.book.At(s.yard.anchor)?.gang == s.saints && s.TurfLinesSince(mark).Count == 0 && s.LettersSince(mark).Count == 0;
                    if (!stays) problems0.Add($"turf cancelled war: with the invaders gone the yard did not stay the holders' (cancelled {invalid}, holder {s.Holder()?.defName}; {Told(s.LinesSince(mark), s.LettersSince(mark))})");

                    // The holders are the ones who go.
                    s.Everyone(i => i < 3 ? s.saints : i < 6 ? s.ravens : null);
                    s.Hold(s.saints);
                    StoryArc second = s.War(s.saints, s.ravens, weary: true);
                    if (second == null) return;
                    s.Blow(s.ravens, s.saints);
                    s.Blow(s.ravens, s.saints);
                    for (int i = 0; i < 3; i++) s.Be(i, null);
                    var worker2 = (GangWarArc)second.def.Worker;
                    bool invalid2 = !worker2.Valid(second);
                    second.done = true;
                    worker2.Cancelled(second);
                    bool taken = invalid2 && s.Holder() == s.ravens;
                    if (!taken) problems0.Add($"turf cancelled war: with the holders gone the raiders still inside did not take the yard (cancelled {invalid2}, holder {s.Holder()?.defName})");
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- B5: answering a raid, in any order -----------------------------------------------------------------------------------

        /// <summary>
        /// Raids and the holders' answers are counted net, whichever came first: an answer before the raids cancels one of them just as an answer
        /// after, so one answer and two raids never flip the yard and one answer and three raids always do.
        /// </summary>
        private static void CheckTurfRaidAnsweringIsOrderIndependent(Map map)
        {
            Guarded("turf raid order", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf raid order");
                if (s == null) return;
                try
                {
                    s.Arm();
                    s.Everyone(i => i < 3 ? s.saints : i < 6 ? s.ravens : null);
                    // (R: a raid by the Saints on the Ravens' yard; A: an answer, the Ravens striking back.)
                    foreach ((string order, bool flips) in new[] { ("ARR", false), ("RRA", false), ("RAR", false), ("ARRR", true), ("RRRA", true), ("RARR", true) })
                    {
                        s.Hold(s.ravens);
                        foreach (char blow in order)
                        {
                            if (blow == 'R') s.Blow(s.saints, s.ravens);
                            else s.Blow(s.ravens, s.saints);
                        }
                        int net = s.book.At(s.yard.anchor).Raids(s.saints);
                        GangTurf.SettleWar(map, s.saints, s.ravens, peace: false);
                        GangDef holder = s.Holder();
                        if (net != order.Count(c => c == 'R') - order.Count(c => c == 'A') || (holder == s.saints) != flips)
                            problems0.Add($"turf raid order: blows {order} counted {net} and left the yard to the {holder?.defName} (flips {flips})");
                    }
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- B6: the overlay is rebuilt when what it shows changes --------------------------------------------------------------

        /// <summary>
        /// The turf overlay is rebuilt when the zones are painted, a claim changes or a gang becomes known (and shows it), and not otherwise,
        /// whatever the tick (the check asks for a refresh with no time passing, as a paused game does).
        /// </summary>
        private static void CheckTurfOverlayRebuildsOnChange(Map map)
        {
            Guarded("turf overlay", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf overlay");
                if (s == null) return;
                try
                {
                    s.Arm();
                    s.Everyone(i => i < 6 ? s.ravens : i < 8 ? s.saints : null);
                    s.Hold(s.ravens);
                    TurfOverlay o = s.pm.TurfShown;
                    o.Refresh();
                    int rebuilds = o.Rebuilds;
                    bool idle = !o.Refresh() && !o.Refresh() && o.Rebuilds == rebuilds;
                    bool tinted = o.At(s.spot) == s.ravens.color;
                    s.Paint(s.spot, null);
                    bool paintedOver = o.Refresh() && o.At(s.spot) == null;
                    s.Unpaint();
                    bool paintedBack = o.Refresh() && o.At(s.spot) == s.ravens.color;
                    for (int i = 0; i < 6; i++) s.crew[i].Inmate().gangKnown = false;
                    bool hidden = o.Refresh() && o.At(s.spot) == null;
                    for (int i = 0; i < 6; i++) s.crew[i].Inmate().gangKnown = true;
                    bool known = o.Refresh() && o.At(s.spot) == s.ravens.color;
                    s.Hold(s.saints);
                    bool flipped = o.Refresh() && o.At(s.spot) == s.saints.color;
                    bool idleAgain = !o.Refresh();
                    if (!idle || !tinted || !paintedOver || !paintedBack || !hidden || !known || !flipped || !idleAgain)
                        problems0.Add($"turf overlay: idle without change {idle}, tinted {tinted}, rebuilt on painting over {paintedOver} and back {paintedBack}, on the gang becoming hidden {hidden} and known {known}, on a flip {flipped}, idle again {idleAgain}");
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- a hidden gang and a known one, in either order ------------------------------------------------------------------------

        /// <summary>
        /// A known gang takes a hidden gang's yard: it is told as "claimed", with no mention of the gang it was taken from (a line, a letter);
        /// a hidden gang takes a known gang's yard: only that the known gang no longer holds it, and no letter.
        /// </summary>
        private static void CheckTurfHiddenAndKnownGangsInEitherOrder(Map map)
        {
            Guarded("turf hidden then known", map, r =>
            {
                TurfScene s = TurfScene.Begin(map, r, "turf hidden and known");
                if (s == null) return;
                try
                {
                    s.Arm();
                    // The hidden Saints hold it; six known Ravens take it.
                    s.Everyone(i => i < 3 ? s.saints : s.ravens);
                    for (int i = 0; i < 3; i++) s.Be(i, s.saints, known: false);
                    s.Hold(s.saints);
                    var mark = s.Mark();
                    s.Day();
                    string claimed = "RP_Chr_TurfClaimed".Translate(s.ravens.label, s.place);
                    string label = "RP_TurfClaimedLabel".Translate(s.ravens.LabelCap, s.place);
                    List<string> lines = s.LinesSince(mark), letters = s.LettersSince(mark);
                    bool told = s.Holder() == s.ravens && lines.Count(l => l == claimed) == 1 && letters.Count(l => l.StartsWith(label)) == 1 && lines.Count == 1 && letters.Count == 1;
                    bool leaked = lines.Concat(letters).Any(t => t.Contains(s.saints.label, StringComparison.OrdinalIgnoreCase));
                    if (!told || leaked) problems0.Add($"turf hidden and known: the known Ravens taking the hidden Saints' yard were not told as a plain claim, or named the Saints (holder {s.Holder()?.defName}, leaked {leaked}; {Told(lines, letters)})");

                    // The known Saints hold it; six hidden Ravens take it.
                    s.Everyone(i => i < 3 ? s.saints : s.ravens);
                    for (int i = 3; i < 9; i++) s.Be(i, s.ravens, known: false);
                    s.Hold(s.saints);
                    mark = s.Mark();
                    s.Day();
                    string lost = "RP_Chr_TurfLost".Translate(s.saints.label, s.place);
                    lines = s.LinesSince(mark);
                    letters = s.LettersSince(mark);
                    told = s.Holder() == s.ravens && lines.Count == 1 && lines[0] == lost && letters.Count == 0;
                    leaked = lines.Concat(letters).Any(t => t.Contains(s.ravens.label, StringComparison.OrdinalIgnoreCase));
                    if (!told || leaked) problems0.Add($"turf hidden and known: the hidden Ravens taking the known Saints' yard were not told as only a loss, or were named (holder {s.Holder()?.defName}, leaked {leaked}; {Told(lines, letters)})");
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        // ---- the riot letter and the misconduct message show the cause as far as the warden knows it ---------------------------------

        /// <summary>
        /// An inmate whose gang the warden does not know and whose tension is turf trouble starts a riot: the letter says trouble on the wing, not
        /// turf. The same inmate's misconduct message (one that is not seen, so does not reveal them) says so too.
        /// </summary>
        private static void CheckTurfRiotLetterHidesAHiddenCause(Map map)
        {
            Guarded("turf riot letter", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                TurfScene s = TurfScene.Begin(map, r, "turf riot letter");
                if (s == null) return;
                bool? forced0 = HostageCrisis.ForcedRoll;
                (int drama0, int beats0, int lastRiot0) = (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick);
                var archived = new List<IArchivable>();
                try
                {
                    s.Arm();
                    // (Nobody real joins: they are calm for the check, which the rollback puts back.)
                    foreach (Pawn real in map.mapPawns.PrisonersOfColonySpawned)
                        if (real.Inmate() is { } rc && rc.tension > 10f) rc.tension = 10f;
                    HostageCrisis.ForcedRoll = false;
                    List<Pawn> rioters = s.crew.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).Take(3).ToList();
                    if (rioters.Count < 3)
                    {
                        problems0.Add($"turf riot letter: could not run (only {rioters.Count} violent-capable throwaways for a riot)");
                        return;
                    }
                    foreach (Pawn p in rioters)
                    {
                        TurfScene.Place(p, s.spot);
                        p.Inmate().tension = 80f;
                    }
                    CompInmate boss = rioters[0].Inmate();
                    (boss.gang, boss.gangKnown, boss.mainCause) = (s.saints, false, "RP_Cause_Turf");
                    bool ran = RPRiotDefOf.RP_Riot.Worker.TryExecute(rioters[0], boss);
                    ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_RiotNegotiation"));
                    string text = letter?.Text.ToString() ?? "";
                    string wing = "RP_Cause_WingTrouble".Translate(), turf = "RP_Cause_Turf".Translate();
                    if (!ran || letter == null || !text.Contains(wing) || text.Contains(turf))
                        problems0.Add($"turf riot letter: the riot letter for a ringleader the warden knows no gang of did not say '{wing}' alone (riot {ran}, letter {letter != null}, says turf {text.Contains(turf)})");

                    // The misconduct message of an inmate nobody can see.
                    Pawn quiet = s.crew[8];
                    CompInmate qc = quiet.Inmate();
                    (qc.gang, qc.gangKnown, qc.mainCause) = (s.saints, false, "RP_Cause_Turf");
                    TurfScene.Place(quiet, new IntVec3(2, 0, 2));
                    if (!quiet.Position.InBounds(map) || TensionUtility.Watchers(quiet, s.pm) > 0f)
                    {
                        RPLog.Message("[RimPrison:test] turf riot letter: nowhere unwatched to record a misconduct; that half was skipped");
                        return;
                    }
                    var before = new HashSet<IArchivable>(Find.Archive.ArchivablesListForReading);
                    MisconductUtility.Record(quiet, qc, s.pm, DefDatabase<MisconductDef>.GetNamed("RP_EscapeAttempt"));
                    archived = Find.Archive.ArchivablesListForReading.Where(a => !before.Contains(a)).ToList();
                    string message = archived.OfType<Message>().Select(m => m.text).FirstOrDefault(t => t.Contains(quiet.LabelShort)) ?? "";
                    if (qc.gangKnown || !message.Contains(wing) || message.Contains(turf))
                        problems0.Add($"turf riot letter: the misconduct message for an unseen inmate with a hidden gang did not say '{wing}' alone (revealed {qc.gangKnown}, message '{message}')");
                }
                finally
                {
                    HostageCrisis.ForcedRoll = forced0;
                    foreach (IArchivable a in archived) Find.Archive.Remove(a);
                    s.Restore();
                    (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick) = (drama0, beats0, lastRiot0);
                }
            });
        }
    }
}
