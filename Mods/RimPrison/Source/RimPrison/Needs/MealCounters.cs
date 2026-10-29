using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Serving counters share the meals out. Vanilla hauling puts every meal on the counter nearest the stove until that
    /// one is full, so in a prison with a counter by every cell block the far ones stay empty and everybody walks to the
    /// first. A meal goes to the emptiest counter instead (when the difference is clear), so each counter has food for
    /// the inmates who live near it.
    /// </summary>
    public static class MealCounters
    {
        /// <summary>A counter has to be this much emptier (share of its capacity: about 7 meals on a 3-cell counter) before a meal skips the nearer one.</summary>
        public const float Margin = 0.08f;

        public static ThingDef CounterDef => RPLaborDefOf.RP_ServingCounter;

        public static bool IsMeal(Thing t) => IsMeal(t.def) && !(t is Building);

        public static bool IsMeal(ThingDef def) =>
            def.ingestible is { } ing && ing.preferability >= FoodPreferability.MealAwful && ing.preferability <= FoodPreferability.MealLavish;

        /// <summary>How many of this meal the counter holds: its cells, the stacks a cell takes, and the <i>meal's</i> stack size (the building's own stackLimit is 1).</summary>
        public static int Capacity(Building_Storage counter, ThingDef meal) => counter.AllSlotCellsList().Count * counter.def.building.maxItemsInCell * meal.stackLimit;

        public static int Meals(Building_Storage counter)
        {
            int n = 0;
            foreach (IntVec3 c in counter.AllSlotCells())
            {
                List<Thing> things = counter.Map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                    if (IsMeal(things[i])) n += things[i].stackCount;
            }
            return n;
        }

        public static List<Building_Storage> On(Map map)
        {
            var result = new List<Building_Storage>();
            if (CounterDef == null) return result;
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(CounterDef))
                if (b is Building_Storage { Spawned: true } s) result.Add(s);
            return result;
        }

        private static float Fill(Building_Storage counter, ThingDef meal) => Capacity(counter, meal) is var cap && cap > 0 ? Meals(counter) / (float)cap : 1f;

        /// <summary>The cell for this meal: the vanilla choice, or one on a clearly emptier counter of the same priority.</summary>
        public static IntVec3 Balance(Thing meal, Pawn carrier, Map map, Faction faction, IntVec3 vanilla)
        {
            if (CounterDef == null || vanilla.GetSlotGroup(map)?.parent is not Building_Storage chosen || chosen.def != CounterDef) return vanilla;
            StoragePriority priority = chosen.GetStoreSettings().Priority;
            float bestFill = Fill(chosen, meal.def) - Margin;
            IntVec3 best = vanilla;
            foreach (Building_Storage other in On(map))
            {
                if (other == chosen || !other.HaulDestinationEnabled || other.GetStoreSettings().Priority != priority || !other.GetStoreSettings().AllowedToAccept(meal)) continue;
                float fill = Fill(other, meal.def);
                if (fill >= bestFill) continue;
                foreach (IntVec3 c in other.AllSlotCells())
                {
                    if (!StoreUtility.IsGoodStoreCell(c, map, meal, carrier, faction) || carrier != null && !carrier.CanReach(c, PathEndMode.ClosestTouch, Danger.Some)) continue;
                    best = c;
                    bestFill = fill;
                    break;
                }
            }
            return best;
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    public static class Patch_StoreUtility_TryFindBestBetterStoreCellFor
    {
        public static void Postfix(Thing t, Pawn carrier, Map map, Faction faction, ref IntVec3 foundCell, ref bool __result)
        {
            if (!__result || !MealCounters.IsMeal(t)) return;
            foundCell = MealCounters.Balance(t, carrier, map, faction, foundCell);
        }
    }
}
