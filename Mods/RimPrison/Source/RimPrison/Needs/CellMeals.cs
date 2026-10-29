using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Inmates who eat in a cell (death row, punishment, lockdown) get a meal brought well before they are hungry;
    /// vanilla wardens only deliver once a prisoner is already hungry, which leaves them starving whenever guards
    /// are busy.
    /// </summary>
    public class WorkGiver_DeliverCellMeal : WorkGiver_Warden
    {
        /// <summary>A meal is brought to the cell below this level: a meal is nine tenths of the stomach (MealSimple and MealFine give 0.9 of an adult's 1.0), and the inmate eats it in the cell only once it fits (<see cref="EatsInTheCellBelow"/>), so delivering earlier costs no meal and is more room for a cook who is busy or has to walk (at 0.4 the condemned were urgently hungry before it came).</summary>
        private const float DeliverBelow = 0.5f;

        /// <summary>A meal that lies in the inmate's own cell is eaten once it fits, give or take this much spilled over a full stomach (a fifth of a meal: eaten at 30% a fifth of every cell meal was lost).</summary>
        private const float SpillOk = 0.1f;

        /// <summary>Whether a meal of this nutrition fits the inmate's stomach (up to <see cref="SpillOk"/> over): the level below which a meal in the cell is eaten.</summary>
        public static bool MealFits(Need_Food food, float nutrition) => food.CurLevel + nutrition <= food.MaxLevel + SpillOk;

        /// <summary>The cheap pre-test for <see cref="MealFits"/> with the commonest meals (0.9): at or under this share of the stomach.</summary>
        public const float EatsInTheCellBelow = 0.2f;

        /// <summary>Cooks on the stoves are what feeds everybody else: at most this share of the cooks (at least one) walk meals out at once.</summary>
        public const float CookShareDelivering = 0.5f;

        private static bool IsCook(Pawn p) => p.GetComp<CompStaff>()?.role is { } role && role.workTypes.Contains(RPVanillaDefOf.Cooking);

        /// <summary>
        /// A cook may take a meal out unless enough of the other cooks are already doing it. The delivery outranks cooking, and with
        /// every confined inmate far from the counters (a long walk each) all the cooks left the kitchen at once, the counters ran dry,
        /// and the inmates who were free to walk to them starved as well.
        /// </summary>
        public static bool CookMayDeliver(Pawn cook)
        {
            if (!IsCook(cook)) return true;
            int cooks = 0, delivering = 0;
            foreach (Pawn p in cook.Map.mapPawns.FreeColonistsSpawned)
            {
                if (p == cook || !IsCook(p)) continue;
                cooks++;
                if (p.CurJobDef == JobDefOf.DeliverFood) delivering++;
            }
            return delivering < Mathf.Max(1, Mathf.FloorToInt((cooks + 1) * CookShareDelivering));
        }

        /// <summary>
        /// The tests of <see cref="JobOnThing"/> that do not depend on who is asked (an active inmate who is not fasting, hungry enough, with a cell or a spot to be fed in, shut in or stranded, and no meal in the room or near them): the inmates
        /// a meal is waiting for. The care reserve's queue reads it (see <see cref="CareDetail"/>).
        /// </summary>
        public static bool MealCandidate(Pawn inmate)
        {
            if (inmate.Inmate() is not { activeFlag: true } c || HungerStrike.Fasting(c)) return false;
            // Inmates who eat in their cell: cell-meal levels (death row), punishment and lockdown.
            bool inTheCell = c.security is { cellMeals: true } || c.forcedActivity != null || PrisonGame.Instance.LockedDown(c);
            if (inmate.needs?.food == null || inmate.needs.food.CurLevelPercentage >= (inTheCell ? DeliverBelow : StrandedBelow) || !inmate.guest.CanBeBroughtFood) return false;
            Room cell = inmate.GetRoom();
            if (cell == null) return false;
            // Anyone else who is shut in and hungry with no way to a counter (the doors are shut for the night, or the wing they were moved to has no way to one) is fed as well: the urgent
            // errand to the canteen fails there, and the regime's meal slots can be ten hours apart. So is one with no cell to be in (a bed destroyed in a riot and no other free: they stand in the
            // wing's corridor, and starved there for days). Not one who can walk to a counter that is only empty for now: the cooks fill it, and a meal each by hand would keep it empty.
            // (The cheap tests first: a food search is only for a cell-sized room, or the spot an inmate in the open stands on, never for the whole outdoors.)
            if (!ToBeBroughtOne(inTheCell, cell.IsPrisonCell, () => StrandedWithoutMeal(inmate, cell))) return false;
            return !(cell.IsPrisonCell ? FoodInRoom(cell) : FoodNear(inmate));
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!ShouldTakeCareOfPrisoner(pawn, t) || !CookMayDeliver(pawn) || t is not Pawn inmate || !MealCandidate(inmate)) return null;
            Room cell = inmate.GetRoom();
            if (!FoodUtility.TryFindBestFoodSourceFor(pawn, inmate, false, out Thing food, out ThingDef foodDef, canRefillDispenser: false, canUseInventory: true,
                    canUsePackAnimalInventory: false, allowForbidden: false, allowCorpse: false, allowSociallyImproper: false, allowHarvest: false,
                    forceScanWholeMap: false, ignoreReservations: false, calculateWantedStackCount: true)) return null;
            if (food.GetRoom() == cell) return null;
            Job job = JobMaker.MakeJob(JobDefOf.DeliverFood, food, inmate);
            job.count = FoodUtility.WillIngestStackCountOf(inmate, foodDef, FoodUtility.GetNutrition(inmate, food, foodDef));
            job.targetC = RCellFinder.SpotToChewStandingNear(inmate, food);
            return job;
        }

        /// <summary>Not in a cell-meals situation: hungry below this (the urgent errand to the canteen starts at 15%) and with no canteen meal they can walk to, a meal is brought.</summary>
        public const float StrandedBelow = 0.25f;

        /// <summary>Whether a meal is carried to an inmate who is hungry enough: those who eat in a cell, when they are in one, and anyone with no meal they can walk to.</summary>
        public static bool ToBeBroughtOne(bool eatsInTheCell, bool inACell, System.Func<bool> stranded) => eatsInTheCell && inACell || stranded();

        /// <summary>Shut in (in a cell, or with no cell to be in) and no way to walk to a canteen counter, whether a meal lies on one or not.</summary>
        internal static bool StrandedWithoutMeal(Pawn inmate, Room room)
        {
            if (!room.IsPrisonCell && InmateUtility.CellOf(inmate) != null) return false;
            PrisonMap pm = PrisonMap.Of(inmate.Map);
            return pm != null && !CanWalkToCanteen(inmate, pm);
        }

        private struct CanteenReach
        {
            public int tick, clears, zones;
            public bool reachable;
        }

        private static readonly Dictionary<District, CanteenReach> canteenReach = new Dictionary<District, CanteenReach>();

        /// <summary>The cached answers of <see cref="CanWalkToCanteen"/> are dropped (tests change the layout between calls).</summary>
        public static void ForgetCanteenReach() => canteenReach.Clear();

        /// <summary>
        /// Whether the inmate can walk to a cell of a canteen zone (the doors as they are now). Kept per district for a tenth of an hour, until the doors or the zones change:
        /// the answer is asked for every inmate under 25% on every cook's and warden's work scan.
        /// </summary>
        internal static bool CanWalkToCanteen(Pawn inmate, PrisonMap pm)
        {
            PrisonZoneDef canteen = RPDefOf.RP_Canteen;
            if (!inmate.Spawned || !pm.zones.Any(canteen)) return false;
            District district = inmate.GetDistrict();
            int now = Find.TickManager.TicksGame;
            if (district != null && canteenReach.TryGetValue(district, out CanteenReach entry) && entry.clears == pm.reachabilityClears && entry.zones == pm.zones.Version && now - entry.tick < CacheTicks) return entry.reachable;
            bool reachable = InmateWalking.TryFindNearest(inmate.Map, inmate.Position, c => pm.zones.ZoneAt(c) == canteen, WalkSearchCells, out IntVec3 at, out _) && inmate.CanReach(at, PathEndMode.OnCell, Danger.Deadly);
            if (district != null)
            {
                if (canteenReach.Count > 256) canteenReach.Clear();
                canteenReach[district] = new CanteenReach { tick = now, clears = pm.reachabilityClears, zones = pm.zones.Version, reachable = reachable };
            }
            return reachable;
        }

        private const int WalkSearchCells = 40000;
        private const int CacheTicks = 250;

        /// <summary>A meal lies (or is being carried) within a few cells of an inmate who is not in a cell: the meal that was dropped for them or for the one next to them.</summary>
        internal static bool FoodNear(Pawn inmate)
        {
            Map map = inmate.Map;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(inmate.Position, 4f, true))
            {
                if (!c.InBounds(map)) continue;
                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                    if (things[i].def.IsNutritionGivingIngestible && things[i].def.ingestible.HumanEdible && things[i] is not Building
                        || things[i] is Pawn p && p != inmate && p.carryTracker?.CarriedThing is { } carried && carried.def.IsNutritionGivingIngestible) return true;
            }
            return false;
        }

        internal static bool FoodInRoom(Room room)
        {
            bool answer = FoodInRoomFast(room);
            if (AutoTest.Active) Shadow.CheckFoodInRoom(room, answer);
            return answer;
        }

        private static bool FoodInRoomFast(Room room)
        {
            // A meal on its way in (carried by a warden already in the cell) counts too. (Whoever stands in the room is in the lister of one of its
            // regions: no need to ask every pawn of the map where they are.)
            foreach (Region region in room.Regions)
            {
                List<Thing> all = region.ListerThings.AllThings;
                for (int i = 0; all != null && i < all.Count; i++)
                    if (all[i] is Pawn p && p.carryTracker?.CarriedThing is { } carried && carried.def.IsNutritionGivingIngestible && p.GetRoom() == room) return true;
            }
            foreach (Region region in room.Regions)
                foreach (Thing thing in region.ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                    if (thing.def.IsNutritionGivingIngestible && thing.def.ingestible.HumanEdible) return true;
            return false;
        }
    }

    /// <summary>
    /// Injured inmates in bed are spoon-fed well before they are hungry (vanilla waits until they are, and with
    /// guards busy after a fight they starve in bed).
    /// </summary>
    public class WorkGiver_FeedInjuredInmate : WorkGiver_Warden
    {
        private const float FeedBelow = 0.4f;

        /// <summary>The tests of <see cref="JobOnThing"/> that do not depend on who is asked: an inmate who is not fasting, is fed by hand, and is hungry enough (the care reserve's queue reads it, see <see cref="CareDetail"/>).</summary>
        public static bool Candidate(Pawn inmate) =>
            inmate.Inmate() is { } c && !HungerStrike.Fasting(c) && WardenFeedUtility.ShouldBeFed(inmate) && inmate.needs?.food is { } food && food.CurLevelPercentage < FeedBelow;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!ShouldTakeCareOfPrisoner(pawn, t) || t is not Pawn inmate || !Candidate(inmate)) return null;
            if (!FoodUtility.TryFindBestFoodSourceFor(pawn, inmate, inmate.needs.food.CurCategory == HungerCategory.Starving, out Thing food, out ThingDef foodDef,
                    canRefillDispenser: false, canUseInventory: true, canUsePackAnimalInventory: false, allowForbidden: false, allowCorpse: false)) return null;
            Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, food, inmate);
            job.count = FoodUtility.WillIngestStackCountOf(inmate, foodDef, FoodUtility.GetNutrition(inmate, food, foodDef));
            return job;
        }
    }
}
