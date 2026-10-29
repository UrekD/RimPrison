using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Test runs only: each answer that now comes from an index, a cache or a reordered search is also worked out the plain way it used to be, and the answers that differ are counted
    /// (logged at the end of a run: <c>[RimPrison:test] shadow</c>). A cached or indexed answer that is wrong shows up here whatever the plan's own checks look at.
    /// </summary>
    public static class Shadow
    {
        public static int meals, mealsDiffer, stoves, stovesDiffer, foodRooms, foodRoomsDiffer, fixtures, fixturesDiffer, watched, watchedDiffer, draws, drawsDiffer;
        public static string first = "";

        private static void Note(string what)
        {
            if (first.Length < 600) first += what + " | ";
        }

        public static string Summary() =>
            $"meals {mealsDiffer}/{meals}, stoves {stovesDiffer}/{stoves}, food in room {foodRoomsDiffer}/{foodRooms}, fixtures {fixturesDiffer}/{fixtures}, watched {watchedDiffer}/{watched}, draw {drawsDiffer}/{draws}; first: {first}";

        /// <summary>The meals the canteen search would consider, by the index and by walking every food thing.</summary>
        public static void CheckMeals(Map map, PrisonMap pm, PrisonZoneDef zone, List<Thing> indexed)
        {
            meals++;
            var viaIndex = new HashSet<Thing>(indexed.Where(t => RegimeWorker_Eat.CanteenMeal(t, pm, zone)));
            var viaWalk = new HashSet<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Where(t => RegimeWorker_Eat.CanteenMeal(t, pm, zone)));
            if (viaIndex.SetEquals(viaWalk)) return;
            mealsDiffer++;
            Note($"meals {viaIndex.Count} indexed vs {viaWalk.Count} walked at tick {Find.TickManager.TicksGame}");
        }

        public static void CheckStoves(Map map, PrisonMap pm, List<Building> found)
        {
            stoves++;
            var walk = new List<Building>();
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
                if (InmateJobWorker_Kitchen.IsStove(b) && pm.zones.ZoneAt(b.Position) == RPLaborDefOf.RP_Kitchen) walk.Add(b);
            if (walk.Count == found.Count && walk.All(found.Contains)) return;
            stovesDiffer++;
            Note($"stoves {found.Count} vs {walk.Count}");
        }

        public static void CheckFoodInRoom(Room room, bool answer)
        {
            foodRooms++;
            bool legacy = false;
            foreach (Pawn p in room.Map.mapPawns.AllPawnsSpawned)
                if (p.carryTracker?.CarriedThing is { } carried && carried.def.IsNutritionGivingIngestible && p.GetRoom() == room) legacy = true;
            if (!legacy)
                foreach (Region region in room.Regions)
                    foreach (Thing thing in region.ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                        if (thing.def.IsNutritionGivingIngestible && thing.def.ingestible.HumanEdible) legacy = true;
            if (legacy == answer) return;
            foodRoomsDiffer++;
            Note($"food in room {answer} vs {legacy}");
        }

        public static void CheckFixture(Pawn pawn, NeedDef need, PrisonZoneDef zone, bool ownRoomOnly, bool survival, CompFixture answer)
        {
            fixtures++;
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            List<CompFixture> all = pm.Fixtures;
            Room room = pawn.GetRoom();
            CompFixture best = null;
            SecurityLevelDef level = KeptRooms.LevelOf(pawn);
            // (The kept-rooms rule first; the urgent toilet falls back to any when none is had.)
            for (int pass = 0; pass < 2 && best == null; pass++)
            {
                if (pass == 1 && !(survival && level != null && pm.zones.AnyKept)) break;
                float bestDist = float.MaxValue;
                for (int i = 0; i < all.Count; i++)
                {
                    CompFixture f = all[i];
                    if (f.Props.need != need || !f.Usable) continue;
                    bool sameRoom = room != null && f.parent.GetRoom() == room;
                    if (ownRoomOnly && !sameRoom) continue;
                    if (zone != null && !sameRoom && pm.zones.ZoneAt(f.parent.Position) != zone) continue;
                    if (pass == 0 && !pm.zones.Allows(f.UseCell, level)) continue;
                    float d = (f.parent.Position - pawn.Position).LengthHorizontalSquared;
                    if (sameRoom) d *= 0.1f;
                    if (d >= bestDist) continue;
                    if (!pawn.CanReserve(f.parent) || !pawn.CanReach(f.UseCell, PathEndMode.OnCell, Danger.Some)) continue;
                    best = f;
                    bestDist = d;
                }
            }
            if (best == answer) return;
            fixturesDiffer++;
            Note($"fixture {answer?.parent.def.defName}@{answer?.parent.Position} vs {best?.parent.def.defName}@{best?.parent.Position}");
        }

        public static void CheckWatched(Pawn p, PrisonMap pm, bool answer)
        {
            watched++;
            if (answer == TensionUtility.Watchers(p, pm) > 0f) return;
            watchedDiffer++;
            Note($"watched {answer} vs {TensionUtility.Watchers(p, pm)}");
        }

        public static void CheckDraw(Map map, float answer)
        {
            draws++;
            float walk = 0f;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
                if (b.GetComp<CompPowerTrader>() is { PowerOn: true } trader && trader.Props.PowerConsumption > 0f) walk += trader.Props.PowerConsumption;
            if (UnityEngine.Mathf.Approximately(walk, answer)) return;
            drawsDiffer++;
            Note($"draw {answer} vs {walk}");
        }
    }
}
