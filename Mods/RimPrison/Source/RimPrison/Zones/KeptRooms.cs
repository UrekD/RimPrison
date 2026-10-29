using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Rooms kept for a security level (see <see cref="ZoneGrid.SetKept"/>): what the inmates' own choices read. Read through the zone choice only, never through a door's verdict: the
    /// door patch runs on path threads and reads only what was worked out for it.
    /// </summary>
    public static class KeptRooms
    {
        /// <summary>The security level an inmate's choices are made at; null for anyone who is not an inmate (they are kept out of nothing).</summary>
        public static SecurityLevelDef LevelOf(Pawn p) => p?.Inmate()?.security;

        /// <summary>May the pawn be in this cell? A patch kept for other levels is out of bounds for an inmate; everything else is open.</summary>
        public static bool Allows(PrisonMap pm, IntVec3 cell, Pawn p) => pm.zones.Allows(cell, LevelOf(p));

        /// <summary>
        /// For a thing used from beside it (a counter, a shelf): its own cell when that is in a patch, else the cells round it that are - an impassable counter's cell is in no patch (patches are made of
        /// the cells one can stand on), so the counter of a yard kept for Maximum read as open to all and a Medium inmate shopped at it. Allowed when a cell beside it in a patch is allowed, or none is in one.
        /// </summary>
        public static bool AllowsTouch(PrisonMap pm, IntVec3 cell, Pawn p)
        {
            SecurityLevelDef level = LevelOf(p);
            if (level == null || !pm.zones.AnyKept) return true;
            int own = pm.zones.PatchAt(cell);
            if (own >= 0) return pm.zones.Allows(own, level);
            bool anyPatch = false;
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(cell, p.Map)))
            {
                int patch = pm.zones.PatchAt(c);
                if (patch < 0) continue;
                anyPatch = true;
                if (pm.zones.Allows(patch, level)) return true;
            }
            return !anyPatch;
        }

        private static readonly List<(int patch, int dist, bool exact)> byDistance = new List<(int, int, bool)>();
        private static readonly List<int> avoid = new List<int>(), lastResort = new List<int>();
        private const int Tries = 8;

        /// <summary>
        /// A standable cell of the zone that the pawn can walk to, in a patch kept for the level or for all: the nearest such patch first and then the others, a random cell of the patch. Cells
        /// painted in bits too small to be a patch come last. Invalid when none can be reached. How near a patch is is the straight distance to its nearest cell, so an L-shaped patch whose
        /// bounding box is near does not beat a small patch that is nearer: the box (cached with the patches) is only a lower bound, and a patch whose bound cannot beat the nearest found so far
        /// is never measured unless the nearer ones are all unreachable. A patch that someone the warden keeps the pawn apart from (<see cref="KeepApart"/>) stands in, or is walking to, is tried after
        /// all the others and the loose cells (when it is the only one, it is the pick).
        /// </summary>
        public static IntVec3 PickCell(Pawn pawn, PrisonMap pm, PrisonZoneDef zone, SecurityLevelDef level)
        {
            ZoneGrid zones = pm.zones;
            if (zones.CellsOf(zone).Count == 0) return IntVec3.Invalid;
            byDistance.Clear();
            List<ZonePatch> patches = zones.Patches;
            IntVec3 from = pawn.Position;
            // A patch someone the warden keeps them apart from stands in (or is walking to) is tried last of all: one patch lookup for each of their orders, no scan.
            KeepApart.PartnerPatches(pawn, pm, avoid);
            lastResort.Clear();
            for (int pi = 0; pi < patches.Count; pi++)
            {
                if (patches[pi].zone != zone || !zones.Allows(pi, level)) continue;
                if (avoid.Count > 0 && avoid.Contains(pi))
                {
                    lastResort.Add(pi);
                    continue;
                }
                CellRect r = patches[pi].bounds;
                int dx = System.Math.Max(System.Math.Max(r.minX - from.x, from.x - r.maxX), 0), dz = System.Math.Max(System.Math.Max(r.minZ - from.z, from.z - r.maxZ), 0);
                byDistance.Add((pi, dx * dx + dz * dz, false));
            }
            while (byDistance.Count > 0)
            {
                // (The smallest bound or distance first, a measured one before a bound that equals it: what is measured first is the nearest once it is the smallest.)
                int next = 0;
                for (int i = 1; i < byDistance.Count; i++)
                    if (byDistance[i].dist < byDistance[next].dist || byDistance[i].dist == byDistance[next].dist && (byDistance[i].exact && !byDistance[next].exact || byDistance[i].exact == byDistance[next].exact && byDistance[i].patch < byDistance[next].patch)) next = i;
                (int patch, int dist, bool exact) g = byDistance[next];
                if (!g.exact)
                {
                    byDistance[next] = (g.patch, NearestCellDistance(zones.CellsOfPatch(g.patch), from), true);
                    continue;
                }
                byDistance.RemoveAt(next);
                IntVec3 pick = RandomIn(pawn, zones.CellsOfPatch(g.patch));
                if (pick.IsValid) return pick;
            }
            // (The loose cells, painted in bits too small to be a patch, are open to all and come last whatever their distance.)
            IntVec3 loose = RandomIn(pawn, zones.LooseCellsOf(zone));
            if (loose.IsValid) return loose;
            foreach (int patch in lastResort)
            {
                IntVec3 pick = RandomIn(pawn, zones.CellsOfPatch(patch));
                if (pick.IsValid) return pick;
            }
            return IntVec3.Invalid;
        }

        private static int NearestCellDistance(List<IntVec3> cells, IntVec3 from)
        {
            int best = int.MaxValue;
            for (int i = 0; i < cells.Count; i++) best = System.Math.Min(best, (cells[i] - from).LengthHorizontalSquared);
            return best;
        }

        /// <summary>A reachable standable cell among these, by a few random draws.</summary>
        private static IntVec3 RandomIn(Pawn pawn, List<IntVec3> pool)
        {
            Map map = pawn.Map;
            for (int i = 0; i < Tries && pool.Count > 0; i++)
            {
                IntVec3 c = pool.RandomElement();
                if (c.Standable(map) && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return c;
            }
            return IntVec3.Invalid;
        }

        /// <summary>
        /// Where an inmate may sit to eat when vanilla's chair (or standing spot) is in a patch kept for other levels: a free chair at a table whose seat is in a patch they may use (the same
        /// conditions as vanilla's), else a standing spot in one. Invalid when there is none, and vanilla's pick stands (the meal has been taken, it is eaten wherever it can be).
        /// </summary>
        public static IntVec3 AllowedChairOrSpot(Pawn pawn, Thing ingestible, PrisonMap pm)
        {
            Map map = pawn.Map;
            SecurityLevelDef level = LevelOf(pawn);
            float radius = ingestible.def?.ingestible?.chairSearchRadius ?? 0f;
            if (radius > 0f)
            {
                IntVec3 seat = IntVec3.Invalid;
                Thing chair = GenClosest.ClosestThingReachable(pawn.Position, map, ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial), PathEndMode.OnCell, TraverseParms.For(pawn), radius, t =>
                {
                    if (t.def.building == null || !t.def.building.isSittable || t.IsForbidden(pawn) || !pawn.CanReserve(t) || !t.IsSociallyProper(pawn) || t.IsBurning()) return false;
                    if (t.Position.GetDangerFor(pawn, map) != Danger.None || !Toils_Ingest.TryFindFreeSittingSpotOnThing(t, pawn, out IntVec3 spot) || !pm.zones.Allows(spot, level)) return false;
                    for (int i = 0; i < 4; i++)
                        if ((spot + GenAdj.CardinalDirections[i]).GetEdifice(map) is { } edifice && edifice.def.surfaceType == SurfaceType.Eat)
                        {
                            seat = spot;
                            return true;
                        }
                    return false;
                });
                if (chair != null && seat.IsValid) return seat;
            }
            IntVec3 standing = RCellFinder.SpotToChewStandingNear(pawn, ingestible, c => pm.zones.Allows(c, level) && pawn.CanReserveSittableOrSpot(c));
            return standing.IsValid && pm.zones.Allows(standing, level) ? standing : IntVec3.Invalid;
        }

        /// <summary>
        /// Ingest jobs (by <c>loadID</c>) made by the survival fallback: the meal, or the only seat for it, is in a patch kept for other levels because none their level may use had one to reach. Not saved: a
        /// reload drops the marks, and a meal eaten across a reload is then out of place once (accepted). Cleared when the job ends (see <see cref="Patch_Pawn_JobTracker_CleanupCurrentJob_KeptRooms"/>).
        /// </summary>
        private static readonly HashSet<int> survivalMeals = new HashSet<int>();

        /// <summary>The job is the survival fallback's meal: while the inmate eats it, it is in place.</summary>
        public static void MarkSurvivalMeal(Job job)
        {
            if (job != null) survivalMeals.Add(job.loadID);
        }

        /// <summary>Eating a meal the survival fallback sent them to (see <see cref="MarkSurvivalMeal"/>).</summary>
        public static bool EatingOutOfNecessity(Pawn pawn) => survivalMeals.Count > 0 && pawn.CurJob is { } job && job.def == JobDefOf.Ingest && survivalMeals.Contains(job.loadID);

        public static void EndSurvivalMeal(Job job)
        {
            if (survivalMeals.Count > 0 && job != null) survivalMeals.Remove(job.loadID);
        }

        public static void ForgetSurvivalMeals() => survivalMeals.Clear();

        /// <summary>A value worked out once per map per tick (the last one asked is kept): what one map answers is never given for another, nor one tick's for the next.</summary>
        public sealed class PerMapTickCache<T>
        {
            private T value;
            private int tick = -1, mapId = int.MinValue;

            public T Get(int map, int now, System.Func<T> make)
            {
                if (tick == now && mapId == map) return value;
                value = make();
                (tick, mapId) = (now, map);
                return value;
            }
        }

        /// <summary>Levels that have an inmate living in the prison (the map's inmates, whatever they are doing).</summary>
        public static HashSet<SecurityLevelDef> LevelsInPrison(Map map)
        {
            var levels = new HashSet<SecurityLevelDef>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate()?.security is { } l) levels.Add(l);
            return levels;
        }

        /// <summary>
        /// A kept patch no inmate living in the prison may use: it is kept for levels, there are inmates, and none of them is of those levels.
        /// </summary>
        public static bool NobodyMayUse(HashSet<SecurityLevelDef> living, IReadOnlyList<SecurityLevelDef> keptFor)
        {
            if (keptFor == null || keptFor.Count == 0) return false;
            return living.Count > 0 && !keptFor.Any(living.Contains);
        }

        /// <summary>"for: Minimum, Medium" (by level order), or "for: all".</summary>
        public static string ForLabel(IReadOnlyList<SecurityLevelDef> keptFor) =>
            "RP_KeptFor".Translate(keptFor == null || keptFor.Count == 0 ? "RP_KeptAll".Translate().ToString() : string.Join(", ", keptFor.Select(l => l.LabelCap.ToString()))).ToString();

        /// <summary>Every kept patch as "zone@first cell:levels", in the order of the patches (the saveload plan compares it before and after a reload).</summary>
        public static string Describe(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return "";
            var parts = new List<string>();
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
                if (pm.zones.KeptFor(i) is { Count: > 0 } levels) parts.Add($"{patches[i].zone.defName}@{patches[i].anchor}:{string.Join("+", levels.Select(l => l.defName))}");
            return string.Join(",", parts);
        }

        /// <summary>The levels the menu offers: those the warden has unlocked, and any a patch is kept for already.</summary>
        public static List<SecurityLevelDef> MenuLevels(IReadOnlyList<SecurityLevelDef> keptFor) =>
            DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(l => Unlocks.IsUnlocked(l) || keptFor != null && keptFor.Contains(l)).OrderBy(l => l.uiOrder).ToList();
    }
}

namespace RimPrison
{
    /// <summary>
    /// An inmate's meal is eaten at a chair (or a spot) vanilla picks by distance alone: when it is in a patch of the same room kept for other levels, a seat in one they may use is looked for
    /// instead (<see cref="KeptRooms.AllowedChairOrSpot"/>); none, and vanilla's pick stands. Main thread, and only past the first test when some patch is kept.
    /// </summary>
    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.TryFindChairOrSpot))]
    public static class Patch_Toils_Ingest_TryFindChairOrSpot_KeptRooms
    {
        public static void Postfix(Pawn pawn, Thing ingestible, ref IntVec3 cell, ref bool __result)
        {
            if (!__result || pawn?.Map == null || KeptRooms.LevelOf(pawn) is not { } level) return;
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (pm == null || !pm.zones.AnyKept || pm.zones.Allows(cell, level)) return;
            IntVec3 allowed = KeptRooms.AllowedChairOrSpot(pawn, ingestible, pm);
            if (allowed.IsValid) cell = allowed;
            else KeptRooms.MarkSurvivalMeal(pawn.CurJob);
        }
    }
}

namespace RimPrison
{
    /// <summary>The survival-meal mark of an Ingest job goes when the job ends, however it ends.</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class Patch_Pawn_JobTracker_CleanupCurrentJob_KeptRooms
    {
        public static void Prefix(Pawn_JobTracker __instance) => KeptRooms.EndSurvivalMeal(__instance.curJob);
    }
}
