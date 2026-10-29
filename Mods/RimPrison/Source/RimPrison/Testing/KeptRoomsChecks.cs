using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// Rooms kept for a security level (<see cref="ZoneGrid.SetKept"/>, <see cref="KeptRooms"/>): a free-time cell pick that keeps out of a patch kept for other levels and goes to the nearest one it may use first, a meal in an
    /// allowed canteen first (and the nearest when none is, so nobody starves), the compliance reading, the program venue, a repaint handing the levels on, and real ticks. The prison has one yard and one canteen, painted
    /// in one piece: each check cuts a strip out of the biggest patch of the kind (<see cref="KeptScene"/>), which makes two patches in the walls the inmates already walk in and puts the strip back when it is done.
    /// A patch's levels in a save are in the saveload plan's snapshot (<see cref="SeedKeptRoomsForReload"/>). Written without a game run: none of it has been run yet.
    /// </summary>
    public static partial class AutoTest
    {
        private static void KeptFail(string what) => problems0.Add("kept rooms: " + what);

        private static string KeptSignature(PrisonMap pm) =>
            string.Join(";", pm.zones.Patches.Select(p => $"{p.zone.defName}:{p.anchor}:{p.cells}")) + "|" + KeptRooms.Describe(pm.map);

        // ---- what a failure says it saw ----------------------------------------------------------------------------------------------------------------------

        /// <summary>"Maximum+Medium" for the levels a patch is kept for, "all" for none (every level).</summary>
        private static string KeptLevelsText(IReadOnlyList<SecurityLevelDef> levels) => levels == null || levels.Count == 0 ? "all" : string.Join("+", levels.Select(l => l.defName));

        /// <summary>A cell as the checks read it: its patch (zone, levels it is kept for) or none, its zone, and the vanilla room it is in.</summary>
        private static string KeptCellText(PrisonMap pm, IntVec3 c)
        {
            if (!c.IsValid || !c.InBounds(pm.map)) return "no cell";
            int patch = pm.zones.PatchAt(c);
            string where = patch < 0 ? $"in no patch (zone {pm.zones.ZoneAt(c)?.defName ?? "none"})" : $"in patch {patch} of {pm.zones.Patches[patch].zone.defName} kept for {KeptLevelsText(pm.zones.KeptFor(patch))}";
            Room room = c.GetRoom(pm.map);
            return $"{c} {where}, {(room == null ? "no room" : "room " + room.ID)}";
        }

        /// <summary>A pawn as the checks read it: level, where it stands, what it is doing and where that is going.</summary>
        private static string KeptPawnText(PrisonMap pm, Pawn p)
        {
            if (p == null) return "nobody";
            Job job = p.CurJob;
            string doing = job == null ? "no job" : job.def.defName + (job.targetA.IsValid ? " to " + KeptCellText(pm, job.targetA.HasThing ? job.targetA.Thing.Position : job.targetA.Cell) : "");
            return $"{p.LabelShort} (level {KeptRooms.LevelOf(p)?.defName ?? "none"}, {(p.Spawned ? "at " + KeptCellText(pm, p.Position) : "not spawned")}, {doing})";
        }

        /// <summary>
        /// Every patch of the zone as the checks see it: its cells, the standable ones per vanilla room (a patch is a connected stretch of paint and need not be in one room, and a standing program counts
        /// its cells room by room), the levels it is kept for, and how many of the zone's cells are in no patch.
        /// </summary>
        private static string KeptZoneText(PrisonMap pm, PrisonZoneDef zone)
        {
            var parts = new List<string>();
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
            {
                if (patches[i].zone != zone) continue;
                string rooms = string.Join(", ", pm.zones.CellsOfPatch(i).Where(c => c.Standable(pm.map)).GroupBy(c => c.GetRoom(pm.map)?.ID ?? -1).OrderBy(g => g.Key).Select(g => $"room {g.Key}: {g.Count()}"));
                parts.Add($"patch {i} at {patches[i].anchor} ({patches[i].cells} cells, standable by room [{rooms}], kept for {KeptLevelsText(pm.zones.KeptFor(i))})");
            }
            return $"{zone.defName} {(parts.Count == 0 ? "has no patch" : string.Join("; ", parts))}; {pm.zones.LooseCellsOf(zone).Count} loose cells";
        }

        /// <summary>An inmate of this level, put together as the regime sees one that is out and about (and, when given, on that activity all day); every need full, so that nothing but the regime sends them anywhere.</summary>
        private static Pawn KeptInmate(Map map, Rollback r, SecurityLevelDef level, RegimeActivityDef forced = null)
        {
            Pawn p = Throwaway(map, r, level);
            CompInmate c = p.Inmate();
            (c.security, c.processed, c.awaitingSearch, c.forcedActivity) = (level, true, false, forced);
            c.RefreshActiveFlag();
            KeptFill(p);
            return p;
        }

        private static void KeptFill(Pawn p)
        {
            // (No needs once the pawn is dead: a real-ticks check can lose its pawn to the event sweep's leftovers.)
            if (p.needs == null) return;
            foreach (Need n in p.needs.AllNeeds) n.CurLevelPercentage = 1f;
        }

        /// <summary>A real-ticks check lost its pawn (carried off, put in a bed, dead): the check is told as not run, not failed.</summary>
        private sealed class KeptLost : Exception
        {
            public KeptLost(string message) : base(message) { }
        }

        /// <summary>Runs a check's body, telling a <see cref="KeptLost"/> as "not run" (the log line, no problem).</summary>
        private static void KeptUnlessLost(Action body)
        {
            try
            {
                body();
            }
            catch (KeptLost e)
            {
                RPLog.Message($"[RimPrison:test] kept rooms: not run, {e.Message}");
            }
        }

        /// <summary>
        /// Null when the pawn is spawned and alive; otherwise what became of it and why: who holds it (vanilla takes the pather off a despawned pawn, the needs and jobs off a dead one), what that pawn's job is,
        /// and, for a downed or dead one, the labels of its hediffs: a guard carrying a calm free-time inmate would be a product fault, a downed one the event sweep's leftover.
        /// </summary>
        private static string KeptGoneText(Pawn p)
        {
            if (p.Spawned && !p.Dead) return null;
            IThingHolder holder = p.ParentHolder;
            Thing top = p.SpawnedParentOrMe;
            Pawn carrier = holder is Pawn_CarryTracker tracker ? tracker.pawn : top as Pawn;
            if (carrier == p) carrier = null;
            string who = p.Dead ? "dead" : "not spawned";
            string held = holder == null ? "held by nothing" : $"held by {holder} (on the map as {top?.ToString() ?? "nothing"})";
            string by = carrier == null ? "no carrier" : $"carrier {carrier.LabelShort} (job {carrier.CurJobDef?.defName ?? "none"}{(carrier.CurJob is { } cj && cj.targetA.IsValid ? " at " + cj.targetA : "")}, {(carrier.Spawned ? "at " + carrier.Position : "not spawned")})";
            string hediffs = p.Dead || p.Downed ? $"; {(p.Dead ? "dead" : "downed")}, hediffs [{string.Join(", ", p.health?.hediffSet?.hediffs.Select(h => h.Label) ?? Enumerable.Empty<string>())}]" : "";
            return $"{p.LabelShort} is {who}: {held}, {by}, own job {p.CurJobDef?.defName ?? "none"}{hediffs}";
        }

        private static void KeptPlace(Pawn p, IntVec3 cell)
        {
            p.pather?.StopDead();
            p.Position = cell;
            p.Notify_Teleported();
            p.jobs?.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>
        /// A zone's biggest patch with a strip cut out of it: two patches, a small one and a big one, the unpainted strip between them (which the inmates walk across) and a free cell on it beside each. Puts it all back
        /// when the check ends: the paint, the levels each patch is kept for and the gangs' turf claims (a repaint makes the turf book look again at its patches).
        /// </summary>
        private sealed class KeptScene
        {
            public const int Width = 5, SmallMin = 6;
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonZoneDef zone;
            private readonly string what, signature0;
            private readonly List<(int anchor, PrisonZoneDef zone, SecurityLevelDef[] levels)> kept0 = new List<(int, PrisonZoneDef, SecurityLevelDef[])>();
            private readonly List<(TurfClaim claim, int anchor, List<IntVec3> footprint)> claims0;
            private readonly List<IntVec3> strip = new List<IntVec3>();
            public IntVec3 smallCell, bigCell, besideSmall, besideBig;

            private KeptScene(Map map, PrisonZoneDef zone, string what)
            {
                this.map = map;
                this.zone = zone;
                this.what = what;
                pm = PrisonMap.Of(map);
                signature0 = KeptSignature(pm);
                List<ZonePatch> patches = pm.zones.Patches;
                for (int i = 0; i < patches.Count; i++)
                    if (pm.zones.KeptFor(i) is { Count: > 0 } levels) kept0.Add((patches[i].anchor, patches[i].zone, levels.ToArray()));
                claims0 = pm.turf.claims.Select(c => (c, c.anchor, c.footprint)).ToList();
            }

            /// <summary>What the scene looks like now: the cell of each patch the checks use, and the zone's patches with their cells per room and the levels they are kept for at this moment.</summary>
            public string Layout() => $"scene: small-patch cell {KeptCellText(pm, smallCell)}, big-patch cell {KeptCellText(pm, bigCell)}, beside them {besideSmall} and {besideBig}; {KeptZoneText(pm, zone)}";

            /// <summary>A problem with the scene as it was when the check saw it.</summary>
            public void Fail(string what) => KeptFail($"{what} [{Layout()}]");

            /// <summary>The small patch's index now, the big one's (the indexes change whenever the paint does: they are looked up by a cell).</summary>
            public int Small => pm.zones.PatchAt(smallCell);

            public int Big => pm.zones.PatchAt(bigCell);

            public void Paint(bool on)
            {
                foreach (IntVec3 c in strip) pm.zones.Set(c, on ? zone : null);
            }

            /// <summary>The scene for the zone, or null with a problem noted (a premise that failed, never a skipped check) when there is no patch to cut.</summary>
            public static KeptScene Begin(Map map, Rollback r, string what, PrisonZoneDef zone, bool quiet = false)
            {
                var s = new KeptScene(map, zone, what);
                if (!s.Cut())
                {
                    s.Restore();
                    if (quiet) return null;
                    KeptFail($"{what}: premise: no patch of {zone.defName} to cut into a small one and a big one with a free cell between them (the biggest has {s.pm.zones.Patches.Where(p => p.zone == zone).Select(p => p.cells).DefaultIfEmpty(0).Max()} cells; the strip is {Width} wide and the small side needs {SmallMin}+ cells; {KeptZoneText(s.pm, zone)})");
                    return null;
                }
                r.Add(s.Restore);
                return s;
            }

            private bool Cut()
            {
                ZoneGrid zones = pm.zones;
                List<ZonePatch> patches = zones.Patches;
                int biggest = -1;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone == zone && (biggest < 0 || patches[i].cells > patches[biggest].cells)) biggest = i;
                if (biggest < 0) return false;
                var all = zones.CellsOf(zone).Where(c => zones.PatchAt(c) == biggest).ToList();
                var inPatch = new HashSet<IntVec3>(all);
                for (int axis = 0; axis < 2; axis++)
                {
                    int axisNow = axis;
                    int Coord(IntVec3 c) => axisNow == 0 ? c.x : c.z;
                    int Other(IntVec3 c) => axisNow == 0 ? c.z : c.x;
                    IntVec3 At(int coord, int other) => axisNow == 0 ? new IntVec3(coord, 0, other) : new IntVec3(other, 0, coord);
                    bool Free(IntVec3 c) => c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetFirstPawn(map) == null;
                    int min = all.Min(Coord), max = all.Max(Coord);
                    for (int lo = min + 1; lo + Width < max; lo++)
                    {
                        int left = all.Count(c => Coord(c) < lo), right = all.Count(c => Coord(c) >= lo + Width);
                        if (left < SmallMin || right <= left) continue;
                        IntVec3 smallEdge = IntVec3.Invalid, smallStand = IntVec3.Invalid, bigEdge = IntVec3.Invalid, bigStand = IntVec3.Invalid;
                        foreach (int other in all.Where(c => Coord(c) == lo - 1).Select(Other).Distinct())
                            if (inPatch.Contains(At(lo - 1, other)) && inPatch.Contains(At(lo, other)) && Free(At(lo - 1, other)) && Free(At(lo, other)))
                            {
                                (smallEdge, smallStand) = (At(lo - 1, other), At(lo, other));
                                break;
                            }
                        foreach (int other in all.Where(c => Coord(c) == lo + Width).Select(Other).Distinct())
                            if (inPatch.Contains(At(lo + Width, other)) && inPatch.Contains(At(lo + Width - 1, other)) && Free(At(lo + Width, other)) && Free(At(lo + Width - 1, other)))
                            {
                                (bigEdge, bigStand) = (At(lo + Width, other), At(lo + Width - 1, other));
                                break;
                            }
                        if (!smallEdge.IsValid || !bigEdge.IsValid) continue;
                        strip.Clear();
                        strip.AddRange(all.Where(c => Coord(c) >= lo && Coord(c) < lo + Width));
                        Paint(false);
                        int a = zones.PatchAt(smallEdge), b = zones.PatchAt(bigEdge);
                        if (a >= 0 && b >= 0 && a != b && zones.Patches[a].zone == zone && zones.Patches[b].zone == zone && zones.Patches[a].cells < zones.Patches[b].cells)
                        {
                            (smallCell, bigCell, besideSmall, besideBig) = (smallEdge, bigEdge, smallStand, bigStand);
                            return true;
                        }
                        Paint(true);
                    }
                }
                strip.Clear();
                return false;
            }

            /// <summary>Everything as it was before the cut, and a problem noted if it is not.</summary>
            public void Restore()
            {
                Paint(true);
                for (int i = 0; i < pm.zones.Patches.Count; i++) pm.zones.SetKept(i, null);
                foreach ((int anchor, PrisonZoneDef z, SecurityLevelDef[] levels) in kept0)
                {
                    int i = pm.zones.PatchAt(anchor);
                    if (i >= 0 && pm.zones.Patches[i].zone == z) pm.zones.SetKept(i, levels);
                }
                foreach ((TurfClaim claim, int anchor, List<IntVec3> footprint) in claims0) (claim.anchor, claim.footprint) = (anchor, footprint);
                pm.turf.Replace(claims0.Select(x => x.claim));
                string now = KeptSignature(pm);
                if (now != signature0) KeptFail($"{what}: the patches and the levels they are kept for were not put back (were {signature0}, now {now})");
            }

            /// <summary>Draws of the free-time cell pick for the pawn: how many land in the small patch, in the big one, elsewhere in the zone, and how many find nothing.</summary>
            public (int small, int big, int other, int none) Draw(Pawn p, int draws)
            {
                int small = Small, big = Big, inSmall = 0, inBig = 0, other = 0, none = 0;
                for (int i = 0; i < draws; i++)
                {
                    IntVec3 c = RegimeActivityWorker.RandomReachableZoneCell(p, pm, zone);
                    if (!c.IsValid) none++;
                    else if (pm.zones.PatchAt(c) == small) inSmall++;
                    else if (pm.zones.PatchAt(c) == big) inBig++;
                    else other++;
                }
                return (inSmall, inBig, other, none);
            }
        }

        private static string KeptDrawText((int small, int big, int other, int none) d) => $"{d.small} in the small patch, {d.big} in the big one, {d.other} elsewhere, {d.none} found nothing";

        // ---- the free-time pick ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// With two yard patches, one kept for Maximum: a Medium inmate's free-time pick (many draws, the kept patch the one nearest them) never lands in it and a Maximum inmate's does; with the patch back to all, both may. The
        /// wander job the free-time worker gives a Medium inmate does not go to it either.
        /// </summary>
        private static void CheckKeptRoomsFreeTimePick(Map map)
        {
            Guarded("kept rooms: a free-time pick keeps out of a patch kept for another level", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "free-time pick", RPDefOf.RP_Yard);
                if (s == null) return;
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium), maximum = KeptInmate(map, r, RPSecurityDefOf.RP_Maximum);
                string Seen() => string.Join("; ", new[] { medium, maximum }.Select(x => KeptPawnText(s.pm, x)));
                KeptPlace(medium, s.besideSmall);
                KeptPlace(maximum, s.besideSmall);
                const int Draws = 150;
                var open = s.Draw(medium, Draws);
                if (open.small == 0 || open.small + open.big + open.other == 0) s.Fail($"free-time pick: with both yard patches open to all, a Medium inmate beside the small one never picked it ({KeptDrawText(open)}) (seen: {Seen()})");

                s.pm.zones.SetKept(s.Small, new[] { RPSecurityDefOf.RP_Maximum });
                var keptMedium = s.Draw(medium, Draws);
                var keptMax = s.Draw(maximum, Draws);
                if (keptMedium.small != 0 || keptMedium.big + keptMedium.other == 0) s.Fail($"free-time pick: with the small yard patch kept for Maximum, a Medium inmate beside it picked cells in it or none anywhere else ({KeptDrawText(keptMedium)}) (seen: {Seen()})");
                if (keptMax.small == 0) s.Fail($"free-time pick: with the small yard patch kept for Maximum, a Maximum inmate beside it never picked it ({KeptDrawText(keptMax)}) (seen: {Seen()})");
                Job wander = RegimeActivityWorker.WanderInZones(medium, s.pm, new List<PrisonZoneDef> { s.zone });
                if (wander == null || s.pm.zones.PatchAt(wander.targetA.Cell) == s.Small) s.Fail($"free-time pick: the wander job given to a Medium inmate beside a yard patch kept for Maximum was {(wander == null ? "none" : "to a cell of it")} (seen: {Seen()})");

                s.pm.zones.SetKept(s.Small, null);
                var back = s.Draw(medium, Draws);
                if (back.small == 0) s.Fail($"free-time pick: with the patch back to all, a Medium inmate beside it never picked it ({KeptDrawText(back)}) (seen: {Seen()})");
            });
        }

        /// <summary>The patch nearest the inmate comes first: beside the small patch they pick it (nearly always: the big one is five cells off), beside the big one they pick that; the others only when none can be reached.</summary>
        private static void CheckKeptRoomsNearestFirst(Map map)
        {
            Guarded("kept rooms: a free-time pick goes to the nearest patch first", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "nearest first", RPDefOf.RP_Yard);
                if (s == null) return;
                Pawn p = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                string Seen() => string.Join("; ", new[] { p }.Select(x => KeptPawnText(s.pm, x)));
                const int Draws = 120;
                KeptPlace(p, s.besideSmall);
                var nearSmall = s.Draw(p, Draws);
                if (nearSmall.small < Draws * 9 / 10) s.Fail($"nearest first: beside the small yard patch an inmate picked it in only {nearSmall.small} of {Draws} draws ({KeptDrawText(nearSmall)}; the big one is {KeptScene.Width} cells off) (seen: {Seen()})");
                KeptPlace(p, s.besideBig);
                var nearBig = s.Draw(p, Draws);
                if (nearBig.big < Draws * 9 / 10) s.Fail($"nearest first: beside the big yard patch an inmate picked it in only {nearBig.big} of {Draws} draws ({KeptDrawText(nearBig)}) (seen: {Seen()})");
                // (With the near patch kept from them, the next nearest is the one: the others come after.)
                s.pm.zones.SetKept(s.Big, new[] { RPSecurityDefOf.RP_Maximum });
                var kept = s.Draw(p, Draws);
                if (kept.big != 0 || kept.small == 0) s.Fail($"nearest first: beside the big yard patch kept for Maximum, an inmate of Medium did not go to the small one instead ({KeptDrawText(kept)}) (seen: {Seen()})");
            });
        }

        /// <summary>
        /// Nearest means the nearest cell, not the nearest bounding box: on open ground an L-shaped yard patch (arms along two sides of a square) and a small patch in the corner the L leaves open, the inmate standing
        /// beside the small one inside the L's bounding box. The L's box holds the inmate, its cells are ten cells off; the small patch is one cell off. Told, not failed, when the map has no open ground for it.
        /// </summary>
        private static void CheckKeptRoomsNearestExact(Map map)
        {
            Guarded("kept rooms: the nearest patch is the one with the nearest cell, not the nearest bounding box", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                const int Size = 12, Margin = 2;
                bool Open(IntVec3 c) => c.InBounds(map) && c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetFirstPawn(map) == null && pm.zones.ZoneAt(c) == null && c.GetRoof(map) == null;
                IntVec3 origin = IntVec3.Invalid;
                for (int x = Margin; x + Size + Margin < map.Size.x && !origin.IsValid; x += 3)
                    for (int z = Margin; z + Size + Margin < map.Size.z && !origin.IsValid; z += 3)
                        if (new CellRect(x - Margin, z - Margin, Size + 2 * Margin, Size + 2 * Margin).Cells.All(Open)) origin = new IntVec3(x, 0, z);
                if (!origin.IsValid)
                {
                    RPLog.Message($"[RimPrison:test] kept rooms: nearest patch not run, the map has no open {Size + 2 * Margin} by {Size + 2 * Margin} square of ground to paint a yard on");
                    return;
                }
                PrisonZoneDef zone = RPDefOf.RP_Yard;
                string signature0 = KeptSignature(pm);
                var claims0 = pm.turf.claims.Select(c => (claim: c, c.anchor, c.footprint)).ToList();
                var painted = new List<IntVec3>();
                r.Add(() =>
                {
                    foreach (IntVec3 c in painted) pm.zones.Set(c, null);
                    foreach ((TurfClaim claim, int anchor, List<IntVec3> footprint) in claims0) (claim.anchor, claim.footprint) = (anchor, footprint);
                    pm.turf.Replace(claims0.Select(x => x.claim));
                    if (KeptSignature(pm) != signature0) KeptFail($"nearest exact: the painted patches were not taken back (were {signature0}, now {KeptSignature(pm)})");
                });
                IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);
                void Paint(int x0, int z0, int x1, int z1)
                {
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            pm.zones.Set(At(x, z), zone);
                            painted.Add(At(x, z));
                        }
                }
                Paint(0, 0, Size - 1, 1);
                Paint(0, 2, 1, Size - 1);
                Paint(Size - 4, Size - 4, Size - 2, Size - 2);
                IntVec3 smallCell = At(Size - 3, Size - 3), lCell = At(0, 0);
                int small = pm.zones.PatchAt(smallCell), l = pm.zones.PatchAt(lCell);
                if (small < 0 || l < 0 || small == l)
                {
                    KeptFail($"nearest exact: premise: the painted L and the small square did not make two patches (the L's cell is in patch {l}, the small square's in patch {small}; origin {origin}; {KeptZoneText(pm, zone)})");
                    return;
                }
                Pawn p = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                KeptPlace(p, At(Size - 1, Size - 1));
                map.reachability.ClearCache();
                int inSmall = 0, inL = 0;
                for (int i = 0; i < 40; i++)
                {
                    IntVec3 c = RegimeActivityWorker.RandomReachableZoneCell(p, pm, zone);
                    if (c.IsValid && pm.zones.PatchAt(c) == small) inSmall++;
                    else if (c.IsValid && pm.zones.PatchAt(c) == l) inL++;
                }
                if (inSmall != 40) KeptFail($"nearest exact: beside a small patch, inside the bounding box of an L-shaped one whose cells are ten cells off, the pick went to the small one in {inSmall} of 40 draws ({inL} to the L, {40 - inSmall - inL} elsewhere or nowhere; the small patch is {small}, the L is {l}; {KeptPawnText(pm, p)}; the L's bounds {pm.zones.Patches[l].bounds}, the small one's {pm.zones.Patches[small].bounds}; {KeptZoneText(pm, zone)})");
            });
        }

        // ---- meals ----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A canteen kept for another level is not chosen while an allowed one has a meal to reach; with every canteen kept for others the nearest meal is still theirs (nobody starves: the search logs it in a test run).
        /// </summary>
        private static void CheckKeptRoomsMeals(Map map)
        {
            Guarded("kept rooms: a meal in an allowed canteen first, the nearest when none is", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "meals", RPDefOf.RP_Canteen);
                if (s == null) return;
                PrisonZoneDef zone = s.zone;
                ThingDef mealDef = DefDatabase<ThingDef>.GetNamed("MealSimple");
                IntVec3 NearestFree(IntVec3 to, int patch) => s.pm.zones.CellsOf(zone).Where(c => s.pm.zones.PatchAt(c) == patch && c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item))
                    .OrderBy(c => (c - to).LengthHorizontalSquared).FirstOrDefault();
                IntVec3 nearAt = NearestFree(s.besideSmall, s.Small), farAt = NearestFree(s.besideBig, s.Big);
                if (!nearAt.IsValid || !farAt.IsValid)
                {
                    s.Fail($"meals: premise: no free cell in each canteen patch to put a meal on");
                    return;
                }
                Thing Meal(IntVec3 at)
                {
                    Thing meal = ThingMaker.MakeThing(mealDef);
                    GenSpawn.Spawn(meal, at, map);
                    r.Add(() =>
                    {
                        if (!meal.Destroyed) meal.Destroy();
                    });
                    return meal;
                }
                Thing near = Meal(nearAt), far = Meal(farAt);
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium), maximum = KeptInmate(map, r, RPSecurityDefOf.RP_Maximum);
                string Seen() => string.Join("; ", new[] { medium, maximum }.Select(x => KeptPawnText(s.pm, x)));
                KeptPlace(medium, s.besideSmall);
                KeptPlace(maximum, s.besideSmall);
                map.reachability.ClearCache();
                Thing Pick(Pawn p)
                {
                    RegimeWorker_Eat.ForgetNoMeal();
                    return RegimeWorker_Eat.FindCanteenMeal(p, s.pm, zone);
                }
                string Where(Thing t) => t == null ? "none" : t == near ? "the meal beside them" : t == far ? "the meal in the big patch" : $"another meal at {t.Position} in patch {s.pm.zones.PatchAt(t.Position)}";

                Thing open = Pick(medium);
                if (open != near) s.Fail($"meals: with every canteen patch open to all, the meal beside the inmate was not the one found ({Where(open)}) (seen: {Seen()})");

                s.pm.zones.SetKept(s.Small, new[] { RPSecurityDefOf.RP_Maximum });
                Thing allowed = Pick(medium);
                if (allowed == null) s.Fail($"meals: premise: with the small canteen patch kept for Maximum the Medium inmate reached no meal in the big one (seen: {Seen()})");
                else if (s.pm.zones.PatchAt(allowed.Position) == s.Small) s.Fail($"meals: a Medium inmate was sent to a meal in a canteen patch kept for Maximum while another patch had meals ({Where(allowed)}) (seen: {Seen()})");
                Thing forMax = Pick(maximum);
                if (forMax != near) s.Fail($"meals: a Maximum inmate beside the canteen patch kept for Maximum was not given the meal in it ({Where(forMax)}) (seen: {Seen()})");

                // Every canteen patch of the prison kept for Maximum: the Medium inmate is still fed, from the nearest meal.
                List<ZonePatch> patches = s.pm.zones.Patches;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone == zone) s.pm.zones.SetKept(i, new[] { RPSecurityDefOf.RP_Maximum });
                Thing nobodyStarves = Pick(medium);
                if (nobodyStarves != near) s.Fail($"meals: with every canteen patch kept for Maximum a Medium inmate was not given the nearest meal ({Where(nobodyStarves)}): they would starve (seen: {Seen()})");
            });
        }

        // ---- compliance -----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>An inmate standing in a patch kept for other levels is out of place for free time; in one they may use, in place.</summary>
        private static void CheckKeptRoomsCompliance(Map map)
        {
            Guarded("kept rooms: the compliance reading", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "compliance", RPDefOf.RP_Yard);
                if (s == null) return;
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium), maximum = KeptInmate(map, r, RPSecurityDefOf.RP_Maximum);
                string Seen() => string.Join("; ", new[] { medium, maximum }.Select(x => KeptPawnText(s.pm, x)));
                RegimeActivityDef free = RPDefOf.RP_FreeTime;
                bool Placed(Pawn p, IntVec3 at)
                {
                    KeptPlace(p, at);
                    return RegimePlacement.InExpectedPlace(p, free, s.pm);
                }
                if (!Placed(medium, s.smallCell)) s.Fail($"compliance: with every yard patch open to all, an inmate in the yard on free time is out of place (seen: {Seen()})");
                s.pm.zones.SetKept(s.Small, new[] { RPSecurityDefOf.RP_Maximum });
                if (Placed(medium, s.smallCell)) s.Fail($"compliance: a Medium inmate in a yard patch kept for Maximum counts as in place (seen: {Seen()})");
                if (!Placed(maximum, s.smallCell)) s.Fail($"compliance: a Maximum inmate in the yard patch kept for Maximum is out of place (seen: {Seen()})");
                if (!Placed(medium, s.bigCell)) s.Fail($"compliance: a Medium inmate in a yard patch that is not kept from them is out of place (seen: {Seen()})");
                s.pm.zones.SetKept(s.Small, null);
                if (!Placed(medium, s.smallCell)) s.Fail($"compliance: with the patch back to all, a Medium inmate in it is out of place (seen: {Seen()})");
            });
        }

        // ---- the program venue ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A class does not take a room in a patch kept for other levels from the inmates it is for; one of the levels it is kept for may.</summary>
        private static void CheckKeptRoomsVenue(Map map)
        {
            Guarded("kept rooms: the program venue", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef prog = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(p => p.Zone == RPProgramDefOf.RP_Classroom && p.Seats.Count > 0);
                if (prog == null)
                {
                    KeptFail($"venue: premise: no program that sits in the classroom ({DefDatabase<ProgramDef>.AllDefsListForReading.Count} programs, {DefDatabase<ProgramDef>.AllDefsListForReading.Count(x => x.Zone == RPProgramDefOf.RP_Classroom)} in the classroom zone, none with seats)");
                    return;
                }
                var state = new List<(int anchor, SecurityLevelDef[] levels)>();
                List<ZonePatch> patches = pm.zones.Patches;
                for (int i = 0; i < patches.Count; i++)
                    if (pm.zones.KeptFor(i) is { Count: > 0 } levels) state.Add((patches[i].anchor, levels.ToArray()));
                r.Add(() =>
                {
                    for (int i = 0; i < pm.zones.Patches.Count; i++) pm.zones.SetKept(i, null);
                    foreach ((int anchor, SecurityLevelDef[] levels) in state)
                        if (pm.zones.PatchAt(anchor) is >= 0 and var i) pm.zones.SetKept(i, levels);
                });
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium), maximum = KeptInmate(map, r, RPSecurityDefOf.RP_Maximum);
                bool Found(params Pawn[] forWhom) => ProgramSystem.TryFindVenue(map, pm, prog, out _, out int seats, out _, forWhom) && seats > 0;
                string Seen() => $"program {prog.defName}, the inmates are {KeptPawnText(pm, medium)} and {KeptPawnText(pm, maximum)}, levels living in the prison [{string.Join(", ", KeptRooms.LevelsInPrison(map).Select(l => l.defName))}], {pm.sessions.Count(x => x.Active && x.program.Zone == prog.Zone)} sessions running in the zone; {KeptZoneText(pm, prog.Zone)}";
                // (Told, not failed: the venue search also wants a teacher and the hour, which the live prison may not give at that moment.)
                if (!Found(medium)) RPLog.Message($"[RimPrison:test] kept rooms: venue not run, {prog.defName} finds no room in the classroom with every patch open to all now");
                else
                {
                    for (int i = 0; i < patches.Count; i++)
                        if (patches[i].zone == RPProgramDefOf.RP_Classroom) pm.zones.SetKept(i, new[] { RPSecurityDefOf.RP_Maximum });
                    if (Found(medium)) KeptFail($"venue: a class found a room for a Medium inmate in a classroom kept for Maximum ({Seen()})");
                    if (!Found(maximum)) KeptFail($"venue: a class found no room for a Maximum inmate in a classroom kept for Maximum ({Seen()})");
                    if (!Found(medium, maximum)) KeptFail($"venue: a class for a Medium and a Maximum inmate found no room in a classroom kept for Maximum (one of them may use it) ({Seen()})");
                    if (!ProgramSystem.TryFindVenue(map, pm, prog, out _, out _, out _) && KeptRooms.LevelsInPrison(map).Contains(RPSecurityDefOf.RP_Maximum))
                        KeptFail($"venue: with no one named, a classroom kept for a level that has inmates in the prison was not found (the Programs screen would say there is no room) ({Seen()})");
                }
            });
        }

        // ---- a repaint ------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A patch cut in two by repainting keeps its levels in both parts; two patches made one by repainting keep the bigger part's; a zone that cannot be kept (a wing, the kitchen) refuses.
        /// </summary>
        private static void CheckKeptRoomsRepaint(Map map)
        {
            Guarded("kept rooms: a repaint hands the levels on", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "repaint", RPDefOf.RP_Yard);
                if (s == null) return;
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
                // Joined again, the big part's levels (none) win over the small part's.
                s.pm.zones.SetKept(s.Small, new[] { max });
                s.Paint(true);
                int whole = s.pm.zones.PatchAt(s.bigCell);
                if (s.pm.zones.PatchAt(s.smallCell) != whole || s.pm.zones.KeptFor(whole) != null)
                    s.Fail($"repaint: a small patch kept for Maximum joined to a bigger one kept for none: the whole is kept for {string.Join("+", s.pm.zones.KeptFor(whole) ?? new List<SecurityLevelDef>())} (patches {s.pm.zones.PatchAt(s.smallCell)} and {whole})");
                // Cut again, kept for Maximum on the big part; joined, the bigger part's levels win over none.
                s.Paint(false);
                s.pm.zones.SetKept(s.Big, new[] { max });
                s.Paint(true);
                whole = s.pm.zones.PatchAt(s.bigCell);
                if (s.pm.zones.KeptFor(whole) is not { Count: 1 } joined || joined[0] != max) s.Fail("repaint: a big patch kept for Maximum joined to a small one kept for none lost its levels");
                // One patch kept for Maximum, cut in two: both parts are.
                s.Paint(false);
                if (s.Small == s.Big || s.pm.zones.KeptFor(s.Small) is not { Count: 1 } a || a[0] != max || s.pm.zones.KeptFor(s.Big) is not { Count: 1 } b || b[0] != max)
                    s.Fail("repaint: a patch kept for Maximum, cut in two, did not keep its levels in both parts");
                // Kept for nothing: asked of a zone that cannot be kept.
                List<ZonePatch> patches = s.pm.zones.Patches;
                int refused = patches.FindIndex(p => !p.zone.keepable);
                if (refused >= 0 && (s.pm.zones.SetKept(refused, new[] { max }) || s.pm.zones.KeptFor(refused) != null))
                    s.Fail($"repaint: a patch of {patches[refused].zone.defName}, which is not a kind inmates share, could be kept for a level");
            });
        }

        // ---- real ticks ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// What a walk of the real-ticks check saw: how long it ran, whether and where the inmate arrived, what they aimed at, and each change of job (at most a dozen). An inmate carried off (or dead) ends the walk:
        /// when and why are kept.
        /// </summary>
        private sealed class KeptWalk
        {
            public bool arrived, aimedAtSmall, headedForAllowed, carriedOff;
            public int arrivedIn = -1, ticks, carriedAt = -1;
            public string carriedText;
            public readonly List<string> trace = new List<string>();

            public string Text => $"{ticks} ticks, {(arrived ? $"arrived in patch {arrivedIn}" : "arrived nowhere")}, aimed at the small patch: {aimedAtSmall}, aimed at a patch their level may use: {headedForAllowed}; jobs: [{string.Join(" | ", trace)}]{(carriedOff ? $"; carried off at tick {carriedAt}: {carriedText}" : "")}";
        }

        /// <summary>
        /// A Medium inmate on free time (the activity forced, every need full) beside the small yard patch walks to it while it is open to all, and to the other patch, never into it, while it is kept for Maximum. The
        /// walk is given a long cap (the nearest allowed patch may be a long way round by the paths, not by the straight line the pick measures): the inmate is judged by where they are heading, and by arriving when
        /// they do; the pick itself (what the free-time worker is offered from where they stand) is judged first, with draws, and the length of the way to the other patch is in every message.
        /// </summary>
        private static void CheckKeptRoomsRealTicks(Map map)
        {
            Guarded("kept rooms: on real ticks a free-time inmate walks to another yard", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "real ticks", RPDefOf.RP_Yard);
                if (s == null) return;
                const int WalkCap = 6000;
                Pawn p = KeptInmate(map, r, RPSecurityDefOf.RP_Medium, RPDefOf.RP_FreeTime);
                SecurityLevelDef level = KeptRooms.LevelOf(p);
                string PathText(IntVec3 from, IntVec3 to)
                {
                    // (Not for a pawn that is carried off: vanilla's path finder logs an error for one that is spawned nowhere, and the events plan fails on it.)
                    if (!p.Spawned) return "not worked out (the inmate is not spawned)";
                    using PawnPath path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(p, Danger.Deadly));
                    return path.Found ? $"{path.NodesReversed.Count} cells (straight {(to - from).LengthHorizontal:F0})" : $"no path (straight {(to - from).LengthHorizontal:F0})";
                }
                string Seen() => $"{KeptPawnText(s.pm, p)}; the way from beside the small patch to the big patch's cell {PathText(s.besideSmall, s.bigCell)}, to the small patch's cell {PathText(s.besideSmall, s.smallCell)}; active inmate: {InmateUtility.IsActiveInmate(p)}";
                if (!InmateUtility.IsActiveInmate(p))
                {
                    s.Fail($"real ticks: premise: the throwaway Medium inmate is not an active inmate ({Seen()})");
                    return;
                }
                // (A bed of their own that they could lie down in. Without one, vanilla's wardens take an inmate to a bed in another room, which is right for the prison (WorkGiver_Warden_TakeToBed: the free-time
                // inmate with no bed in their wing is escorted to one, a guard's EscortPrisonerToBed) and not what this check is about: a run's log has "carried off at tick 769" with that job.)
                Building_Bed own = IntakeSystem.FreePrisonerBeds(map, level, p.gender).FirstOrDefault(b => RestUtility.IsValidBedFor(b, p, p, true, false, false, GuestStatus.Prisoner));
                if (own == null)
                {
                    RPLog.Message("[RimPrison:test] kept rooms: real ticks not run, no free prisoner bed a Medium inmate could use to give the throwaway one (a guard would escort an inmate with none to a bed)");
                    return;
                }
                p.ownership.ClaimBedIfNonMedical(own);
                if (RestUtility.FindBedFor(p, p, true, false, GuestStatus.Prisoner) != own)
                {
                    RPLog.Message($"[RimPrison:test] kept rooms: real ticks not run, the throwaway's own bed at {own.Position} is not one they could lie down in ({Seen()})");
                    return;
                }
                KeptWalk WalkToAYard()
                {
                    var w = new KeptWalk();
                    // (The inmate may be carried off, put in a bed or killed on real ticks: vanilla takes the pather off a despawned pawn and the needs off a dead one. The walk ends there, and says why.)
                    bool CarriedOff(int tick)
                    {
                        if (KeptGoneText(p) is not { } gone) return false;
                        (w.carriedOff, w.carriedAt, w.carriedText) = (true, tick, gone);
                        RPLog.Message($"[RimPrison:test] kept rooms: real ticks, the inmate was carried off at tick {tick} of a walk: {gone}");
                        return true;
                    }
                    if (CarriedOff(0)) return w;
                    KeptPlace(p, s.besideSmall);
                    s.pm.QueueRegimeInterrupt(p);
                    string last = null;
                    for (int t = 0; t < WalkCap && !w.arrived; t++)
                    {
                        if (t % 10 == 0 && !p.Spawned)
                        {
                            // (Carried off by a guard - to a cell, the hole, a bed: a despawned pawn has no pather, and is not walking anywhere.)
                            if (last != "not spawned" && w.trace.Count < 12) w.trace.Add($"tick {t}: not spawned ({(p.ParentHolder is Pawn_CarryTracker ct ? "carried by " + ct.pawn.LabelShort : p.ParentHolder?.ToString() ?? "nowhere")})");
                            last = "not spawned";
                        }
                        else if (t % 10 == 0)
                        {
                            KeptFill(p);
                            IntVec3 aim = p.CurJob is { } job && (job.def == JobDefOf.Goto || job.def == JobDefOf.GotoWander) && job.targetA.IsValid ? (job.targetA.HasThing ? job.targetA.Thing.Position : job.targetA.Cell) : IntVec3.Invalid;
                            if (!aim.IsValid && p.pather is { Moving: true }) aim = p.pather.Destination.Cell;
                            int aimPatch = aim.IsValid ? s.pm.zones.PatchAt(aim) : -1;
                            if (aimPatch >= 0 && aimPatch == s.Small) w.aimedAtSmall = true;
                            else if (aimPatch >= 0 && s.pm.zones.Allows(aimPatch, level)) w.headedForAllowed = true;
                            string now = $"{p.CurJobDef?.defName ?? "no job"} to {(aim.IsValid ? KeptCellText(s.pm, aim) : "nowhere")}";
                            if (now != last && w.trace.Count < 12) w.trace.Add($"tick {t}: {now}, standing at {p.Position}");
                            last = now;
                        }
                        Find.TickManager.DoSingleTick();
                        w.ticks = t + 1;
                        if (CarriedOff(w.ticks)) break;
                        if (p.Spawned && s.pm.zones.ZoneAt(p.Position) == s.zone)
                        {
                            w.arrived = true;
                            w.arrivedIn = s.pm.zones.PatchAt(p.Position);
                        }
                    }
                    return w;
                }
                // (Open to all, it is the near one: that is what proves the inmate is walking by the free-time worker at all.)
                KeptWalk open = WalkToAYard();
                if (open.carriedOff)
                {
                    RPLog.Message($"[RimPrison:test] kept rooms: real ticks not run, the inmate was carried off during the walk with every yard patch open to all, so the kept walk was not run ({open.Text}; {Seen()})");
                    return;
                }
                if (!open.arrived || open.arrivedIn != s.Small)
                {
                    // (Told, not failed: the free-time worker's pick is a few tries at random cells, and the control can miss the near patch on a run; the kept case is what is judged.)
                    RPLog.Message($"[RimPrison:test] kept rooms: real ticks not run, with every yard patch open to all a Medium inmate on free time beside the small one did not walk into it within {WalkCap} ticks (the small one is patch {s.Small}; {open.Text}; {Seen()})");
                    return;
                }
                s.pm.zones.SetKept(s.Small, new[] { RPSecurityDefOf.RP_Maximum });
                KeptPlace(p, s.besideSmall);
                // The pick first: what the free-time worker is offered from beside the small patch must be another patch (a product fault when it is not), however long the walk to it.
                var offered = s.Draw(p, 40);
                if (offered.small != 0 || offered.big + offered.other == 0)
                    s.Fail($"real ticks: the pick offered a Medium inmate beside a yard patch kept for Maximum {(offered.small != 0 ? "a cell of it" : "no other yard")} ({KeptDrawText(offered)}; {Seen()})");
                KeptWalk walk = WalkToAYard();
                if (walk.carriedOff && !walk.aimedAtSmall) RPLog.Message($"[RimPrison:test] kept rooms: real ticks not run, the inmate was carried off during the walk beside a yard patch kept for Maximum ({walk.Text}; {Seen()})");
                else if (walk.aimedAtSmall || walk.arrived && walk.arrivedIn == s.Small) s.Fail($"real ticks: a Medium inmate on free time walked {(walk.aimedAtSmall ? "towards" : "into")} the yard patch kept for Maximum ({walk.Text}; {Seen()})");
                else if (walk.arrived) RPLog.Message($"[RimPrison:test] kept rooms: a Medium inmate on free time walked past the small yard patch kept for Maximum into patch {walk.arrivedIn} ({walk.ticks} ticks)");
                else if (!walk.headedForAllowed) s.Fail($"real ticks: a Medium inmate on free time beside a yard patch kept for Maximum was heading for no other yard in {WalkCap} ticks ({walk.Text}; {Seen()})");
                else RPLog.Message($"[RimPrison:test] kept rooms: a Medium inmate on free time beside a yard patch kept for Maximum was still walking to another yard after {WalkCap} ticks, heading for one they may use ({walk.Text}; {Seen()})");
            });
        }

        // ---- an inmate's own errands, the cell actually used ---------------------------------------------------------------------------------------------

        /// <summary>A fixture of the def with its use cell at the given cell (turned until it is), spawned beside the scene's patches; removed when the check ends.</summary>
        private static Thing KeptSpawnFixture(Map map, Rollback r, string defName, IntVec3 at, IntVec3 useCell)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
            foreach (Rot4 rot in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                if (ThingUtility.InteractionCellWhenAt(def, at, rot, map) != useCell) continue;
                Thing t = GenSpawn.Spawn(ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null), at, map, rot);
                r.Add(() =>
                {
                    if (!t.Destroyed) t.Destroy();
                });
                return t;
            }
            return null;
        }

        /// <summary>
        /// A phone or a toilet whose use cell is in a patch kept for other levels is not used by another level while one in an allowed patch exists (whatever the zone asked for: none here), and the urgent toilet
        /// is used when it is the only one (nobody wets themselves for a sign on a door) while the free-time phone is not. Each fixture stands on the strip between the patches, the cell it is used from in a patch
        /// (so a check of the building's own cell would let it through). The prison's other toilets and phones are taken out of the list of fixtures meanwhile, and put back.
        /// </summary>
        private static void CheckKeptRoomsErrands(Map map)
        {
            Guarded("kept rooms: an inmate's own errands keep to the rooms of their level", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "errands", RPDefOf.RP_Yard);
                if (s == null) return;
                PrisonMap pm = s.pm;
                List<CompFixture> fixtures0 = pm.Fixtures.ToList();
                r.Add(() =>
                {
                    pm.Fixtures.Clear();
                    pm.Fixtures.AddRange(fixtures0);
                });
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium), maximum = KeptInmate(map, r, max);
                string Seen() => string.Join("; ", new[] { medium, maximum }.Select(x => KeptPawnText(s.pm, x)));
                foreach ((string defName, NeedDef need, string what) in new[] { ("RP_Toilet", RPDefOf.RP_Bladder, "toilet"), ("RP_PhoneBooth", RPVisitDefOf.RP_Family, "phone") })
                {
                    Thing near = KeptSpawnFixture(map, r, defName, s.besideSmall, s.smallCell), far = KeptSpawnFixture(map, r, defName, s.besideBig, s.bigCell);
                    if (near == null || far == null)
                    {
                        s.Fail($"errands: premise: no way to turn a {what} to be used from the cell of the {(near == null ? "small" : "big")} patch (seen: {Seen()})");
                        return;
                    }
                    pm.Fixtures.RemoveAll(f => f.Props.need == need && f.parent != near && f.parent != far);
                    map.reachability.ClearCache();
                    KeptPlace(medium, s.besideSmall);
                    KeptPlace(maximum, s.besideSmall);
                    string Name(CompFixture f) => f == null ? "none" : f.parent == near ? $"the {what} used from the small patch" : f.parent == far ? $"the {what} used from the big patch" : $"another {what}";
                    if (FixtureFinder.FindFor(medium, need)?.parent != near) s.Fail($"errands: with every patch open to all a Medium inmate beside the small patch was not given the nearest {what} ({Name(FixtureFinder.FindFor(medium, need))}) (seen: {Seen()})");
                    s.pm.zones.SetKept(s.Small, new[] { max });
                    CompFixture kept = FixtureFinder.FindFor(medium, need);
                    if (kept?.parent != far) s.Fail($"errands: with the small patch kept for Maximum a Medium inmate was given {Name(kept)}, not the {what} of the allowed patch (asked with no zone: the rule is the inmate's, not the zone's) (seen: {Seen()})");
                    if (FixtureFinder.FindFor(maximum, need)?.parent != near) s.Fail($"errands: a Maximum inmate beside the patch kept for Maximum was not given its {what} ({Name(FixtureFinder.FindFor(maximum, need))}) (seen: {Seen()})");
                    s.pm.zones.SetKept(s.Big, new[] { max });
                    CompFixture none = FixtureFinder.FindFor(medium, need);
                    if (none != null) s.Fail($"errands: with both patches kept for Maximum a Medium inmate was given {Name(none)} for a free-time errand (seen: {Seen()})");
                    if (need == RPDefOf.RP_Bladder)
                    {
                        if (FixtureFinder.FindFor(medium, need, survival: true)?.parent != near) s.Fail($"errands: the urgent toilet was not the nearest one when every toilet was in a kept patch (seen: {Seen()})");
                        KeptFill(medium);
                        medium.needs.TryGetNeed(RPDefOf.RP_Bladder).CurLevel = 0.1f;
                        Job urgent = new JobGiver_InmateUrgentNeeds().TryIssueJobPackage(medium, default).Job;
                        if (urgent?.def != RPDefOf.RP_UseFixture || urgent.targetA.Thing != near) s.Fail($"errands: an urgent bladder with the only toilets in kept patches did not send a Medium inmate to the nearest ({urgent?.def.defName ?? "no job"} {urgent?.targetA}) (seen: {Seen()})");
                        else RPLog.Message("[RimPrison:test] kept rooms: an urgent bladder is let into the only toilet there is, kept for Maximum");
                        KeptFill(medium);
                    }
                    s.pm.zones.SetKept(s.Small, null);
                    s.pm.zones.SetKept(s.Big, null);
                    near.Destroy();
                    far.Destroy();
                }
            });
        }

        /// <summary>
        /// The commissary counter in a patch kept for another level is not shopped at; the same inmate shops at a counter of an allowed patch (the counter is the thing on the cell, the shop's own search finds it).
        /// </summary>
        private static void CheckKeptRoomsCommissary(Map map)
        {
            Guarded("kept rooms: the commissary counter of a kept patch is not shopped at", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "commissary", RPDefOf.RP_Yard);
                if (s == null) return;
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                string Seen() => string.Join("; ", new[] { medium }.Select(x => KeptPawnText(s.pm, x)));
                CompInmate c = medium.Inmate();
                c.cash = 1000;
                // (Shopping is for the bored, and for those with money.)
                medium.needs.joy.CurLevel = 0.3f;
                KeptPlace(medium, s.besideSmall);
                ThingDef counterDef = RPCommissaryDefOf.RP_CommissaryCounter;
                Thing counter = GenSpawn.Spawn(ThingMaker.MakeThing(counterDef, counterDef.MadeFromStuff ? GenStuff.DefaultStuffFor(counterDef) : null), s.smallCell, map);
                ThingDef itemDef = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d => d.HasModExtension<CommissaryItemExtension>() && CommissarySystem.PriceOf(d) <= c.cash);
                if (itemDef == null)
                {
                    counter.Destroy();
                    s.Fail($"commissary: premise: no commissary item to put on the counter (seen: {Seen()})");
                    return;
                }
                Thing item = GenSpawn.Spawn(ThingMaker.MakeThing(itemDef), s.smallCell, map);
                r.Add(() =>
                {
                    if (!item.Destroyed) item.Destroy();
                    if (!counter.Destroyed) counter.Destroy();
                });
                map.reachability.ClearCache();
                // (A trip to this counter: the closest stocked counter within forty cells is taken, and a real one of the prison, open to them, may be the one.)
                bool Shops()
                {
                    for (int i = 0; i < 60; i++)
                        if (CommissarySystem.TryShop(medium, c, s.pm) is { } job && job.targetA.Thing == item) return true;
                    return false;
                }
                if (!Shops()) s.Fail($"commissary: premise: with the counter in a patch open to all the inmate never went shopping in 60 tries (seen: {Seen()})");
                s.pm.zones.SetKept(s.Small, new[] { max });
                if (Shops()) s.Fail($"commissary: a Medium inmate went shopping at a counter in a yard patch kept for Maximum (seen: {Seen()})");
            });
        }

        // ---- the urgent meal ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// An urgently hungry inmate whose nearest meal (the one vanilla's search takes) is in a canteen patch kept for another level is given a meal of an allowed patch; with every canteen patch kept from them the
        /// nearest is theirs (survival), and while they eat it the compliance reading takes the kept patch for in place.
        /// </summary>
        private static void CheckKeptRoomsUrgentMeal(Map map)
        {
            Guarded("kept rooms: the urgent meal goes through the rule, with the survival fallback", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "urgent meal", RPDefOf.RP_Canteen);
                if (s == null) return;
                PrisonZoneDef zone = s.zone;
                ThingDef mealDef = DefDatabase<ThingDef>.GetNamed("MealSimple");
                IntVec3 NearestFree(IntVec3 to, int patch) => s.pm.zones.CellsOf(zone).Where(c => s.pm.zones.PatchAt(c) == patch && c.Standable(map) && c.GetFirstBuilding(map) == null && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item))
                    .OrderBy(c => (c - to).LengthHorizontalSquared).FirstOrDefault();
                IntVec3 nearAt = NearestFree(s.besideSmall, s.Small), farAt = NearestFree(s.besideBig, s.Big);
                if (!nearAt.IsValid || !farAt.IsValid)
                {
                    s.Fail($"urgent meal: premise: no free cell in each canteen patch to put a meal on");
                    return;
                }
                Thing Meal(IntVec3 at)
                {
                    Thing meal = ThingMaker.MakeThing(mealDef);
                    GenSpawn.Spawn(meal, at, map);
                    r.Add(() =>
                    {
                        if (!meal.Destroyed) meal.Destroy();
                    });
                    return meal;
                }
                Thing near = Meal(nearAt);
                Meal(farAt);
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                string Seen() => string.Join("; ", new[] { medium }.Select(x => KeptPawnText(s.pm, x)));
                KeptPlace(medium, s.besideSmall);
                map.reachability.ClearCache();
                RegimeWorker_Eat.ForgetNoMeal();
                medium.needs.food.CurLevelPercentage = 0.05f;
                if (medium.needs.food.CurCategory < HungerCategory.UrgentlyHungry)
                {
                    s.Fail($"urgent meal: premise: the inmate is only {medium.needs.food.CurCategory} at 5% (seen: {Seen()})");
                    return;
                }
                Thing vanilla = new JobGiver_GetFood().TryIssueJobPackage(medium, default).Job?.targetA.Thing;
                if (vanilla != near)
                {
                    // (Told, not failed: vanilla's food search weighs other meals of the live prison too, and may prefer one; the kept case needs it to want the near one.)
                    RPLog.Message($"[RimPrison:test] kept rooms: urgent meal not run, vanilla's search did not take the meal beside the inmate ({vanilla?.Position.ToString() ?? "none"})");
                    return;
                }
                s.pm.zones.SetKept(s.Small, new[] { max });
                Job allowed = new JobGiver_InmateUrgentNeeds().TryIssueJobPackage(medium, default).Job;
                if (allowed?.def != JobDefOf.Ingest) s.Fail($"urgent meal: with the nearest canteen patch kept for Maximum an urgently hungry Medium inmate was given {allowed?.def.defName ?? "no job"} (seen: {Seen()})");
                else if (allowed.targetA.Thing == near || s.pm.zones.PatchAt(allowed.targetA.Thing.Position) == s.Small) s.Fail($"urgent meal: an urgently hungry Medium inmate was sent for a meal in a canteen patch kept for Maximum while another had one (seen: {Seen()})");
                List<ZonePatch> patches = s.pm.zones.Patches;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone == zone) s.pm.zones.SetKept(i, new[] { max });
                RegimeWorker_Eat.ForgetNoMeal();
                Job survival = new JobGiver_InmateUrgentNeeds().TryIssueJobPackage(medium, default).Job;
                if (survival?.def != JobDefOf.Ingest) s.Fail($"urgent meal: with every canteen patch kept for Maximum an urgently hungry Medium inmate was given {survival?.def.defName ?? "no job"}: they would starve (seen: {Seen()})");
                else
                {
                    RPLog.Message("[RimPrison:test] kept rooms: an urgently hungry inmate is fed from a canteen kept for others when none is left for them");
                    // The survival fallback's job is marked: eating it in a kept canteen during Eat is in place; an ordinary Ingest after it is not, and the mark is gone with the job.
                    RegimeActivityDef eat = DefDatabase<RegimeActivityDef>.GetNamed("RP_Eat");
                    KeptPlace(medium, s.smallCell);
                    medium.jobs.StartJob(survival, JobCondition.InterruptForced);
                    if (medium.CurJob != survival) s.Fail($"urgent meal: premise: the Medium inmate did not start the survival meal (seen: {Seen()})");
                    else
                    {
                        if (!RegimePlacement.InExpectedPlace(medium, eat, s.pm)) s.Fail($"urgent meal: a Medium inmate eating the survival meal in a canteen kept for Maximum, with every canteen kept, counts as out of place during Eat (seen: {Seen()})");
                        medium.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                        if (KeptRooms.EatingOutOfNecessity(medium)) s.Fail($"urgent meal: the survival mark outlived its job (seen: {Seen()})");
                        medium.jobs.StartJob(RegimeWorker_Eat.IngestJob(medium, near), JobCondition.InterruptForced);
                        if (medium.CurJobDef != JobDefOf.Ingest) s.Fail($"urgent meal: premise: the Medium inmate did not start an ordinary meal (seen: {Seen()})");
                        else if (RegimePlacement.InExpectedPlace(medium, eat, s.pm)) s.Fail($"urgent meal: a Medium inmate eating an ordinary meal in a canteen kept for Maximum counts as in place during Eat (seen: {Seen()})");
                        medium.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    }
                    KeptRooms.ForgetSurvivalMeals();
                }
            });
        }

        // ---- a visit --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A visit under way whose visitor is in (or on the way to) a visitation patch that is kept for other levels since is ended (the visitor leaves; nobody is relocated).
        /// </summary>
        private static void CheckKeptRoomsVisit(Map map)
        {
            Guarded("kept rooms: a visit in a room kept since is ended", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                if (!pm.zones.Any(RPVisitDefOf.RP_Visitation))
                {
                    KeptFail($"visit: premise: no visitation zone ({KeptZoneText(pm, RPVisitDefOf.RP_Visitation)})");
                    return;
                }
                var state = new List<(int anchor, SecurityLevelDef[] levels)>();
                for (int i = 0; i < pm.zones.Patches.Count; i++)
                    if (pm.zones.KeptFor(i) is { Count: > 0 } levels) state.Add((pm.zones.Patches[i].anchor, levels.ToArray()));
                r.Add(() =>
                {
                    for (int i = 0; i < pm.zones.Patches.Count; i++) pm.zones.SetKept(i, null);
                    foreach ((int anchor, SecurityLevelDef[] levels) in state)
                        if (pm.zones.PatchAt(anchor) is >= 0 and var i) pm.zones.SetKept(i, levels);
                });
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
                Pawn inmate = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                CompInmate c = inmate.Inmate();
                List<Relative> family = Relatives.Of(c);
                while (family.Count(x => x.CanVisit) < 1) family.Add(Relatives.Make());
                Relatives.Recount(c);
                // What the check saw of the visit: the inmate, the visitor (where it stands, in which patch and what it is doing), the lord's toil and spot, and the visitation patches with the levels they are kept for.
                string Seen()
                {
                    Pawn v = c.visitor;
                    Lord lord = v?.GetLord();
                    return $"the inmate {KeptPawnText(pm, inmate)}; the visitor {(v == null ? "is gone (the visit ended)" : KeptPawnText(pm, v))}; lord toil {lord?.CurLordToil?.GetType().Name ?? "none"}, the lord's spot {(lord?.LordJob is LordJob_VisitInmate lj ? KeptCellText(pm, lj.Spot) : "none")}; {KeptZoneText(pm, RPVisitDefOf.RP_Visitation)}";
                }
                bool Visit(out LordJob_VisitInmate job)
                {
                    job = null;
                    if (!VisitationSystem.SpawnVisitor(map, inmate, c, booth: false) || c.visitor is not { } visitor) return false;
                    r.Add(() =>
                    {
                        if (!visitor.Destroyed) visitor.Destroy();
                    });
                    job = visitor.GetLord()?.LordJob as LordJob_VisitInmate;
                    return job != null;
                }
                // Every visitation patch kept for Maximum since: the visit is ended.
                if (!Visit(out LordJob_VisitInmate ended))
                {
                    KeptFail($"visit: premise: no visitor came (the inmate {KeptPawnText(pm, inmate)}, {Relatives.Of(c).Count(x => x.CanVisit)} relatives who can visit; {KeptZoneText(pm, RPVisitDefOf.RP_Visitation)})");
                    return;
                }
                for (int i = 0; i < pm.zones.Patches.Count; i++)
                    if (pm.zones.Patches[i].zone == RPVisitDefOf.RP_Visitation) pm.zones.SetKept(i, new[] { max });
                if (VisitationSystem.KeepVisitInBounds(inmate, c, pm) || c.visitor != null) KeptFail($"visit: with every visitation patch kept for Maximum since, the visit of a Medium inmate went on ({Seen()})");
                for (int i = 0; i < pm.zones.Patches.Count; i++) pm.zones.SetKept(i, null);
                // One patch of two kept since, the visitor waiting in it: the visit is ended too, nobody is relocated.
                KeptScene s = KeptScene.Begin(map, r, "visit", RPVisitDefOf.RP_Visitation, quiet: true);
                if (s == null)
                {
                    RPLog.Message("[RimPrison:test] kept rooms: the visitation zone has no patch big enough to cut in two here; a visitor waiting in one kept patch of two is not checked");
                    return;
                }
                if (!Visit(out _))
                {
                    s.Fail($"visit: premise: no second visitor came ({Seen()})");
                    return;
                }
                Pawn visitor2 = c.visitor;
                // (On the patch's own cell: beside it is the unpainted strip, which is in no patch and no zone, and a visitor standing there is not waiting in the patch - what the first run of this check did.)
                KeptPlace(visitor2, s.smallCell);
                s.pm.zones.SetKept(s.Small, new[] { max });
                if (s.pm.zones.PatchAt(visitor2.Position) != s.Small || s.pm.zones.ZoneAt(visitor2.Position) != RPVisitDefOf.RP_Visitation)
                    s.Fail($"visit: premise: the visitor is not standing in the small visitation patch ({Seen()})");
                else if (VisitationSystem.KeepVisitInBounds(inmate, c, s.pm) || c.visitor != null) s.Fail($"visit: a visit with the visitor waiting in a patch kept for Maximum since went on (it should end) ({Seen()})");
            });
        }

        // ---- compliance, the exceptions --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The kept-patch verdict comes before the acceptance of a class (an inmate in a classroom kept since is out of place), but assigned work (cleaning) and a survival meal are in place wherever they are.
        /// </summary>
        private static void CheckKeptRoomsComplianceBeforeSanction(Map map)
        {
            Guarded("kept rooms: the verdict comes before the sanctioned acceptance, with its exceptions", map, r => KeptUnlessLost(() =>
            {
                KeptScene s = KeptScene.Begin(map, r, "compliance 2", RPDefOf.RP_Yard);
                if (s == null) return;
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium), maximum = KeptInmate(map, r, max);
                string Seen() => string.Join("; ", new[] { medium, maximum }.Select(x => KeptPawnText(s.pm, x)));
                RegimeActivityDef free = RPDefOf.RP_FreeTime;
                s.pm.zones.SetKept(s.Small, new[] { max });
                bool Doing(Pawn p, Job job)
                {
                    // (A tick passes first: vanilla's job-storm error is at ten jobs started by one pawn in one tick, and every call ends two pawns' jobs and starts one of its own, five calls on the one throwaway
                    // made it. The jobs ended here start no new one: placing the pawn below does, once.)
                    Find.TickManager.DoSingleTick();
                    // (The pawn placed and given the job may have been carried off or killed by that tick: the check is not run then, not failed. The other's job is gone with a death.)
                    if (KeptGoneText(p) is { } gone) throw new KeptLost($"the {(p == medium ? "Medium" : "Maximum")} inmate was lost on the tick before a job was given ({gone})");
                    // (The cell is reserved by whoever has it: the other's job ends first.)
                    medium.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    maximum.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    KeptPlace(p, s.smallCell);
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    return p.CurJob == job;
                }
                // A class in a room kept since: out of place for the one it is kept from, in place for the one it is kept for.
                if (!Doing(medium, JobMaker.MakeJob(RPProgramDefOf.RP_AttendClass, s.smallCell))) s.Fail($"compliance 2: premise: the Medium inmate did not start the class job (seen: {Seen()})");
                else if (RegimePlacement.InExpectedPlace(medium, free, s.pm)) s.Fail($"compliance 2: a Medium inmate in a class in a patch kept for Maximum since counts as in place (seen: {Seen()})");
                if (!Doing(maximum, JobMaker.MakeJob(RPProgramDefOf.RP_AttendClass, s.smallCell))) s.Fail($"compliance 2: premise: the Maximum inmate did not start the class job (seen: {Seen()})");
                else if (!RegimePlacement.InExpectedPlace(maximum, free, s.pm)) s.Fail($"compliance 2: a Maximum inmate in a class in a patch kept for Maximum is out of place (seen: {Seen()})");
                // Assigned work: mopping a floor in the kept patch.
                Filth filth = FilthMaker.TryMakeFilth(s.smallCell, map, ThingDefOf.Filth_Dirt) ? s.smallCell.GetThingList(map).OfType<Filth>().FirstOrDefault() : null;
                if (filth == null) s.Fail($"compliance 2: premise: no filth to clean (seen: {Seen()})");
                else
                {
                    r.Add(() =>
                    {
                        if (!filth.Destroyed) filth.Destroy();
                    });
                    Job clean = JobMaker.MakeJob(JobDefOf.Clean);
                    clean.AddQueuedTarget(TargetIndex.A, filth);
                    if (!Doing(medium, clean)) s.Fail($"compliance 2: premise: the Medium inmate did not start cleaning (seen: {Seen()})");
                    else if (!RegimePlacement.InExpectedPlace(medium, free, s.pm)) s.Fail($"compliance 2: a Medium inmate cleaning (assigned work) in a patch kept for Maximum counts as out of place (seen: {Seen()})");
                }
                // A survival meal: eaten where the fallback sent them.
                ThingDef mealDef = DefDatabase<ThingDef>.GetNamed("MealSimple");
                Thing meal = GenSpawn.Spawn(ThingMaker.MakeThing(mealDef), s.besideBig, map);
                r.Add(() =>
                {
                    if (!meal.Destroyed) meal.Destroy();
                });
                KeptFill(medium);
                medium.needs.food.CurLevelPercentage = 0.5f;
                if (!Doing(medium, RegimeWorker_Eat.IngestJob(medium, meal)) || medium.CurJobDef != JobDefOf.Ingest) s.Fail($"compliance 2: premise: the Medium inmate did not start eating (seen: {Seen()})");
                else
                {
                    if (RegimePlacement.InExpectedPlace(medium, free, s.pm)) s.Fail($"compliance 2: a Medium inmate eating a meal in a patch kept for Maximum, with an allowed one to be had, counts as in place (seen: {Seen()})");
                    // (No new job started: a half-hungry inmate beside a meal would start eating it again at once.)
                    medium.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                if (!Doing(medium, RegimeWorker_Eat.IngestJob(medium, meal, survival: true)) || medium.CurJobDef != JobDefOf.Ingest) s.Fail($"compliance 2: premise: the Medium inmate did not start eating the survival meal (seen: {Seen()})");
                else
                {
                    if (!RegimePlacement.InExpectedPlace(medium, free, s.pm)) s.Fail($"compliance 2: a Medium inmate eating the survival meal in a patch kept for Maximum counts as out of place (seen: {Seen()})");
                    medium.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    if (RegimePlacement.InExpectedPlace(medium, free, s.pm)) s.Fail($"compliance 2: a Medium inmate no longer eating, still in a patch kept for Maximum, counts as in place (seen: {Seen()})");
                }
                KeptRooms.ForgetSurvivalMeals();
            }));
        }

        // ---- venues: ranking and capacity -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A room with seats (or standing cells) in patches kept for some levels admits only an inmate whose level may use every one of those patches, whatever the patch they would stand in: a mixed class in a yard of a
        /// patch kept for Maximum and another kept for Medium admits nobody from the two levels where one room holds both, with a patch both may use as well it admits those allowed in both, and with nothing kept all of
        /// them, as before. Which rooms hold which patches is the prison's to say, not the check's: a patch is a connected stretch of paint and a yard painted through a gate has one patch in two vanilla rooms, so
        /// the expected answer is worked out here room by room from the standable cells of the zone (the cells the standing venue counts) and the patch each is in, never from where the two cells the scene uses are.
        /// </summary>
        private static void CheckKeptRoomsVenueByPatch(Map map)
        {
            Guarded("kept rooms: a room admits only those allowed in every patch it has seats in", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "venue by patch", RPDefOf.RP_Yard);
                if (s == null) return;
                SecurityLevelDef max = RPSecurityDefOf.RP_Maximum, med = RPSecurityDefOf.RP_Medium;
                Pawn m1 = KeptInmate(map, r, med), m2 = KeptInmate(map, r, med), x1 = KeptInmate(map, r, max);
                Pawn[] cohort = { m1, m2, x1 };
                var prog = new ProgramDef { zone = s.zone, standing = true };
                var admitted = new List<Pawn>();
                bool Run() => ProgramSystem.TryFindVenue(map, s.pm, prog, out _, out _, out _, cohort, admitted);
                string Names(IEnumerable<Pawn> pawns) => pawns.Any() ? string.Join("+", pawns.Select(p => KeptRooms.LevelOf(p).defName)) : "nobody";
                // The standing venue as the check counts it: the zone's standable cells per room, the kept patches those cells are in, the places the room has, and who of the cohort it admits.
                (int best, string report, List<(int id, List<Pawn> admits)> rooms) Expect()
                {
                    var cells = new Dictionary<Room, (int count, HashSet<int> patches)>();
                    foreach (IntVec3 c in s.pm.zones.CellsOf(s.zone))
                    {
                        if (!c.Standable(map) || c.GetRoom(map) is not { } room) continue;
                        if (!cells.TryGetValue(room, out var entry)) cells[room] = entry = (0, new HashSet<int>());
                        int patch = s.pm.zones.PatchAt(c);
                        if (patch >= 0 && s.pm.zones.KeptFor(patch) != null) entry.patches.Add(patch);
                        cells[room] = (entry.count + 1, entry.patches);
                    }
                    int most = 0;
                    var lines = new List<string>();
                    var perRoom = new List<(int, List<Pawn>)>();
                    foreach (KeyValuePair<Room, (int count, HashSet<int> patches)> kv in cells.OrderBy(kv => kv.Key.ID))
                    {
                        int places = Math.Clamp(kv.Value.count / ProgramSystem.StandingCellsPerAttendee, 1, ProgramSystem.MaxStanding);
                        List<Pawn> takes = cohort.Where(p => kv.Value.patches.All(patch => s.pm.zones.Allows(patch, KeptRooms.LevelOf(p)))).Take(places).ToList();
                        bool claimed = s.pm.sessions.Any(sess => sess.Active && sess.program.Zone == s.zone && sess.anchor.IsValid && sess.anchor.GetRoom(map) == kv.Key);
                        if (claimed) takes.Clear();
                        lines.Add($"room {kv.Key.ID}: {kv.Value.count} standable cells, {places} places, kept patches [{string.Join(", ", kv.Value.patches.OrderBy(x => x).Select(x => $"{x} for {KeptLevelsText(s.pm.zones.KeptFor(x))}"))}], {(claimed ? "held by a class, " : "")}admits {Names(takes)}");
                        perRoom.Add((kv.Key.ID, takes));
                        most = Math.Max(most, takes.Count);
                    }
                    return (most, string.Join("; ", lines), perRoom);
                }
                void Verify(string stage)
                {
                    var (best, report, rooms) = Expect();
                    bool found = Run();
                    // (Which room is chosen between rooms that admit the same number is the venue search's: any room that admits that many and all of whom the search named.)
                    bool sameAsARoom = rooms.Any(x => x.admits.Count == best && admitted.Count == best && admitted.All(x.admits.Contains));
                    if (found != best > 0 || admitted.Count != best || best > 0 && !sameAsARoom)
                        s.Fail($"venue by patch: {stage}: a standing class of {Names(cohort)} found {(found ? "a room" : "no room")} and admitted {Names(admitted)}, but the rooms of the yard admit at most {best} ({report})");
                }
                if (!Run())
                {
                    RPLog.Message($"[RimPrison:test] kept rooms: venue by patch not run, a standing class finds no room in the yard with nothing kept now ({KeptZoneText(s.pm, s.zone)})");
                    return;
                }
                // Nothing kept: all of them, as before.
                Verify("with nothing kept");
                if (admitted.Count != 3) s.Fail($"venue by patch: with nothing kept a class of three admitted {admitted.Count} ({Names(admitted)})");
                // A patch each: Big for Maximum, Small for Medium. A room holding both admits nobody of the two levels; a room holding one admits the level it is kept for.
                s.pm.zones.SetKept(s.Big, new[] { max });
                s.pm.zones.SetKept(s.Small, new[] { med });
                Verify("the big patch kept for Maximum and the small one for Medium");
                // The big patch kept for both: the Mediums are allowed in both patches, the Maximum is not in the small one (kept for Medium).
                s.pm.zones.SetKept(s.Big, new[] { max, med });
                Verify("the big patch kept for Maximum and Medium and the small one for Medium");
                // The small one open again: both levels are allowed in every room (one patch kept for both, the other for all).
                s.pm.zones.SetKept(s.Small, null);
                Verify("the big patch kept for Maximum and Medium and the small one open");
                if (admitted.Count == 0) s.Fail("venue by patch: with a patch kept for Maximum and Medium and the rest open nobody of the cohort was admitted anywhere");
                s.pm.zones.SetKept(s.Big, null);
                s.pm.zones.SetKept(s.Small, null);
                // The cache of the levels in the prison is per map as well as per tick.
                var cache = new KeptRooms.PerMapTickCache<object>();
                object a = new object(), b = new object();
                if (cache.Get(1, 10, () => a) != a || cache.Get(1, 10, () => b) != a) s.Fail("venue by patch: the per-map cache did not keep its value within a map and a tick");
                if (cache.Get(2, 10, () => b) != b) s.Fail("venue by patch: the per-map cache gave one map's value to another in the same tick");
                if (cache.Get(2, 11, () => a) != a) s.Fail("venue by patch: the per-map cache gave one tick's value to the next");
            });
        }

        // ---- the cached patch cells, and a remnant -----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The cell lists cached with the patches (each patch's cells, the loose ones) agree with the zone's cells and follow a repaint; a remnant of one to three cells left of a patch by a repaint is below the patch
        /// threshold, in none and so open to all (it loses the restriction, as DESIGN.md says), and the patch it came from is back, unkept, when repainted whole.
        /// </summary>
        private static void CheckKeptRoomsPatchCells(Map map)
        {
            Guarded("kept rooms: cached patch cells, and a remnant below the threshold", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "patch cells", RPDefOf.RP_Yard);
                if (s == null) return;
                ZoneGrid zones = s.pm.zones;
                int Total() => Enumerable.Range(0, zones.Patches.Count).Where(i => zones.Patches[i].zone == s.zone).Sum(i => zones.CellsOfPatch(i).Count) + zones.LooseCellsOf(s.zone).Count;
                if (Total() != zones.CellsOf(s.zone).Count || zones.CellsOfPatch(s.Small).Count != zones.Patches[s.Small].cells) s.Fail($"patch cells: the cached cells of the patches and the loose ones ({Total()}) are not the zone's cells ({zones.CellsOf(s.zone).Count})");
                if (!ReferenceEquals(zones.CellsOfPatch(s.Big), zones.CellsOfPatch(s.Big))) s.Fail("patch cells: a patch's cells were worked out again without a repaint");
                int smallCells = zones.Patches[s.Small].cells;
                s.Paint(true);
                if (zones.CellsOfPatch(zones.PatchAt(s.bigCell)).Count <= smallCells) s.Fail("patch cells: after a repaint the patch's cell list was the old one");
                s.Paint(false);
                // A remnant: the small patch kept for Maximum, repainted away but for three cells.
                zones.SetKept(s.Small, new[] { RPSecurityDefOf.RP_Maximum });
                List<IntVec3> small = zones.CellsOfPatch(s.Small).ToList();
                List<IntVec3> remnant = small.Take(3).ToList(), removed = small.Skip(3).ToList();
                r.Add(() =>
                {
                    foreach (IntVec3 c in removed) zones.Set(c, s.zone);
                });
                foreach (IntVec3 c in removed) zones.Set(c, null);
                Pawn medium = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                foreach (IntVec3 c in remnant)
                {
                    if (zones.PatchAt(c) != -1) s.Fail($"patch cells: a remnant of three cells {c} is still in a patch");
                    else if (!zones.Allows(c, medium.Inmate().security)) s.Fail($"patch cells: a remnant of three cells {c} kept its restriction (it is below the patch threshold: open to all)");
                    else if (!zones.LooseCellsOf(s.zone).Contains(c)) s.Fail($"patch cells: a remnant cell {c} is not among the zone's loose cells");
                }
                if (zones.AnyKept && Enumerable.Range(0, zones.Patches.Count).Any(i => zones.Patches[i].zone == s.zone && zones.KeptFor(i) != null)) s.Fail("patch cells: after the repaint some yard patch is still kept");
            });
        }

        // ---- the saveload plan ------------------------------------------------------------------------------------------------------------------------------

        private static int keptSeedAnchor = -1;
        private static SecurityLevelDef[] keptSeedBefore;

        /// <summary>Saveload plan, before the snapshot: the biggest yard patch kept for Medium and Maximum, which the snapshot (and the check after the reload) reads back.</summary>
        private static void SeedKeptRoomsForReload(Map map)
        {
            keptSeedAnchor = -1;
            PrisonMap pm = PrisonMap.Of(map);
            List<ZonePatch> patches = pm.zones.Patches;
            int pick = -1;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].zone == RPDefOf.RP_Yard && (pick < 0 || patches[i].cells > patches[pick].cells)) pick = i;
            if (pick < 0)
            {
                reloadProblems.Add($"saveload: no yard patch to keep for a level ({KeptZoneText(pm, RPDefOf.RP_Yard)})");
                return;
            }
            keptSeedBefore = pm.zones.KeptFor(pick)?.ToArray();
            keptSeedAnchor = patches[pick].anchor;
            pm.zones.SetKept(pick, new[] { RPSecurityDefOf.RP_Medium, RPSecurityDefOf.RP_Maximum });
        }

        private static void CheckKeptRoomsAfterReload(Map map)
        {
            if (keptSeedAnchor < 0) return;
            PrisonMap pm = PrisonMap.Of(map);
            int i = pm.zones.PatchAt(keptSeedAnchor);
            IReadOnlyList<SecurityLevelDef> levels = i < 0 ? null : pm.zones.KeptFor(i);
            if (i < 0 || pm.zones.Patches[i].zone != RPDefOf.RP_Yard || levels == null || levels.Count != 2 || levels[0] != RPSecurityDefOf.RP_Medium || levels[1] != RPSecurityDefOf.RP_Maximum)
            {
                reloadProblems.Add($"a yard patch's levels did not survive the save: kept for {string.Join("+", levels?.Select(l => l.defName) ?? new string[0])} (saved Medium+Maximum), the patch was first cell {keptSeedAnchor}, now patch {i}; {KeptZoneText(pm, RPDefOf.RP_Yard)}; whole snapshot {KeptRooms.Describe(map)})");
                return;
            }
            RPLog.Message("[RimPrison:test] saveload kept rooms: the yard patch is still kept for Medium and Maximum");
            // (Put back as the game had it.)
            pm.zones.SetKept(i, keptSeedBefore);
        }
    }
}
