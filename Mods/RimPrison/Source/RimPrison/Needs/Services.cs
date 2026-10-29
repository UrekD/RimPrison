using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Whether the prison's meals and showers can keep up with its inmates, and say what to build when they cannot: a
    /// meal and a shower both have to be reached within their hour of the regime, so beyond a short walk from the cells,
    /// or with too few counters, seats, showers and cooks for the number of inmates, people go hungry and dirty with food
    /// and water in the building. The thresholds are rules of thumb (Prison Architect asks for about a cook per 10-15
    /// prisoners; the cooks here also carry meals to the confined), checked against the layout the player built, not against the inmates' moods.
    /// </summary>
    public static class PrisonServices
    {
        /// <summary>The longest walk from a bed to a serving counter or a shower (about 20 minutes at the inmates' pace, leaving most of the hour to eat or wash).</summary>
        public const int WalkLimit = 50;
        public const int InmatesPerCounter = 24, InmatesPerShower = 10, InmatesPerSeat = 4, InmatesPerCook = 12;
        /// <summary>The weights benches and the library: wanted once the prison holds this many inmates, and about one bench per <see cref="InmatesPerBench"/>, one shelf per <see cref="InmatesPerShelf"/>.</summary>
        public const int GymFrom = 12, InmatesPerBench = 20, LibraryFrom = 16, InmatesPerShelf = 50;

        public struct Report
        {
            public int inmates, counters, seats, showers, cooks, meals, benches, shelves;
            public int farFromCounter, farFromShower;
            public IntVec3 farCounterBed, farShowerBed;
        }

        private static int cacheKey = -1, cacheMap = -1;
        private static Report cached;

        /// <summary>The numbers for this map, recomputed at most once an hour of game time.</summary>
        public static Report Of(Map map)
        {
            int key = Find.TickManager.TicksGame / GenDate.TicksPerHour;
            if (key == cacheKey && map.uniqueID == cacheMap) return cached;
            cacheKey = key;
            cacheMap = map.uniqueID;
            return cached = Compute(map);
        }

        /// <summary>Forget the cached numbers (tests rebuild the prison within an hour).</summary>
        public static void Reset() => cacheKey = -1;

        private static Report Compute(Map map)
        {
            var r = new Report();
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return r;
            var counterCells = new List<IntVec3>();
            foreach (Building_Storage counter in MealCounters.On(map))
            {
                if (pm.zones.ZoneAt(counter.Position) != RPDefOf.RP_Canteen) continue;
                r.counters++;
                foreach (IntVec3 c in counter.OccupiedRect()) counterCells.Add(c);
            }
            foreach (IntVec3 c in pm.zones.CellsOf(RPDefOf.RP_Canteen))
                if (c.GetEdifice(map) is { def.building.isSittable: true }) r.seats++;
            var showerCells = new List<IntVec3>();
            foreach (CompFixture f in pm.Fixtures)
                if (f.Props.need == RPDefOf.RP_Hygiene && f.Usable && pm.zones.ZoneAt(f.parent.Position) == RPDefOf.RP_Shower)
                {
                    r.showers++;
                    showerCells.Add(f.parent.Position);
                }
            foreach (CompFixture f in pm.Fixtures)
                if (f.Props.need == RPExerciseDefOf.RP_Exercise && f.Usable && ExerciseUtility.InBenchZone(pm.zones.ZoneAt(f.parent.Position))) r.benches++;
            r.shelves = LibrarySystem.Shelves(map);
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>().role.workTypes.Contains(RPVanillaDefOf.Cooking)) r.cooks++;
            r.meals = MealsInCanteen(map, pm);

            var beds = new List<IntVec3>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || !UsesCanteen(p)) continue;
                r.inmates++;
                // (Not the hole's bed: one just out of solitary owns it for a while, and the warning would send the warden to build a canteen by the hole.)
                if (p.ownership?.OwnedBed is { Spawned: true } bed && !PunishmentUtility.IsSolitaryBed(bed)) beds.Add(bed.Position);
            }
            r.farFromCounter = FarFrom(map, counterCells, beds, out r.farCounterBed);
            r.farFromShower = FarFrom(map, showerCells, beds, out r.farShowerBed);
            return r;
        }

        /// <summary>
        /// Does this inmate walk to the canteen and the showers? Not one who eats in a cell (death row, supermax) or is confined to one
        /// (solitary, punishment): the meals come to them, and counting them would send the player to build a canteen at death row.
        /// </summary>
        public static bool UsesCanteen(Pawn p) => p.Inmate() is { } c && c.security is not { cellMeals: true } && c.forcedActivity == null;

        /// <summary>The meals lying on the canteen now (not the hourly count).</summary>
        public static int MealsInCanteen(Map map, PrisonMap pm)
        {
            int meals = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                if (MealCounters.IsMeal(t) && pm.zones.ZoneAt(t.Position) == RPDefOf.RP_Canteen) meals += t.stackCount;
            return meals;
        }

        /// <summary>How many of the beds are more than <see cref="WalkLimit"/> cells' walk from the nearest source (or cannot reach one), and the farthest of them.</summary>
        public static int FarFrom(Map map, List<IntVec3> sources, List<IntVec3> beds, out IntVec3 farthest)
        {
            int far = 0;
            farthest = IntVec3.Invalid;
            if (sources.Count == 0 || beds.Count == 0) return 0;
            int[] steps = InmateWalking.DistancesTo(map, sources, beds);
            int worst = WalkLimit;
            for (int i = 0; i < beds.Count; i++)
            {
                if (steps[i] <= WalkLimit) continue;
                far++;
                if (steps[i] > worst)
                {
                    worst = steps[i];
                    farthest = beds[i];
                }
            }
            return far;
        }

        public static int Needed(int inmates, int per) => (inmates + per - 1) / per;

        /// <summary>What to build or hire, in words (empty when the services keep up). Nothing is said for an empty prison.</summary>
        public static IEnumerable<string> Lines(Map map) => Items(map).Select(i => i.text);

        /// <summary>The same, each with the screen where it is fixed: the cooks are hired on Staff, everything else is built and painted from Rooms.</summary>
        public static IEnumerable<(string text, MainTabWindow_Prison.Tab? tab)> Items(Map map) => ItemsOf(Of(map));

        /// <summary>The lines for a set of numbers (<see cref="Items"/> reads them off the map).</summary>
        public static IEnumerable<(string text, MainTabWindow_Prison.Tab? tab)> ItemsOf(Report r)
        {
            if (r.inmates == 0) yield break;
            const MainTabWindow_Prison.Tab rooms = MainTabWindow_Prison.Tab.Rooms;
            if (r.counters < Needed(r.inmates, InmatesPerCounter)) yield return ("RP_Problem_FewCounters".Translate(r.counters, r.inmates, InmatesPerCounter), rooms);
            if (r.seats < Needed(r.inmates, InmatesPerSeat)) yield return ("RP_Problem_FewSeats".Translate(r.seats, r.inmates, Needed(r.inmates, InmatesPerSeat)), rooms);
            if (r.showers < Needed(r.inmates, InmatesPerShower)) yield return ("RP_Problem_FewShowers".Translate(r.showers, r.inmates, InmatesPerShower), rooms);
            if (r.cooks < Needed(r.inmates, InmatesPerCook)) yield return ("RP_Problem_FewCooks".Translate(r.cooks, r.inmates, InmatesPerCook), MainTabWindow_Prison.Tab.Staff);
            if (r.farFromCounter > 0)
                yield return ("RP_Problem_FarCanteen".Translate(r.farFromCounter, WalkLimit, r.farCounterBed.IsValid ? r.farCounterBed.x : 0, r.farCounterBed.IsValid ? r.farCounterBed.z : 0), rooms);
            if (r.farFromShower > 0)
                yield return ("RP_Problem_FarShowers".Translate(r.farFromShower, WalkLimit, r.farShowerBed.IsValid ? r.farShowerBed.x : 0, r.farShowerBed.IsValid ? r.farShowerBed.z : 0), rooms);
            foreach (string line in GymAndLibraryLines(r)) yield return (line, rooms);
        }

        /// <summary>The weights benches and shelves a prison this size should have (a library is only asked for once the warden can build one).</summary>
        public static IEnumerable<string> GymAndLibraryLines(Report r)
        {
            if (r.inmates >= GymFrom && r.benches < Needed(r.inmates, InmatesPerBench))
                yield return r.benches == 0 ? "RP_Problem_NoGym".Translate(r.inmates) : "RP_Problem_FewBenches".Translate(r.benches, r.inmates, InmatesPerBench);
            if (r.inmates >= LibraryFrom && r.shelves < Needed(r.inmates, InmatesPerShelf) && Unlocks.IsUnlocked(RPLibraryDefOf.RP_BookShelf))
                yield return r.shelves == 0 ? "RP_Problem_NoLibrary".Translate(r.inmates) : "RP_Problem_FewShelves".Translate(r.shelves, r.inmates, InmatesPerShelf);
        }
    }
}
