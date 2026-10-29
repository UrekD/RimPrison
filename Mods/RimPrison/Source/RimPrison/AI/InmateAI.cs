using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Gate for the inmate think tree (inserted before vanilla's prisoner branch).</summary>
    public class ThinkNode_ConditionalInmate : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn) => InmateUtility.IsActiveInmate(pawn) && PrisonGame.Instance != null;
    }

    /// <summary>Needs that override the regime when urgent: toilet, starving, exhausted.</summary>
    public class JobGiver_InmateUrgentNeeds : ThinkNode_JobGiver
    {
        private static readonly JobGiver_GetFood getFood = new JobGiver_GetFood();

        protected override Job TryGiveJob(Pawn pawn)
        {
            CompInmate own = pawn.Inmate();
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            // (On a walk with a guard no errand of theirs uses the doors the guard's pass opens, the toilet and the canteen included: the guard has them back within the hour. A supermax inmate leaves the
            // cell only with a guard, so their toilet is the one of the room they are in, never the nearest one the doors will let them reach, and waiting for a guard is a walk in this.)
            bool escortOnly = own.security is { escortOnly: true };
            bool withGuardOrWaiting = own.leg != null || escortOnly && EscortWalk.Stranded(pawn, own);
            Need bladder = pawn.needs.TryGetNeed(RPDefOf.RP_Bladder);
            if (bladder != null && bladder.CurLevel < 0.3f && own.leg == null)
            {
                // (Survival: the toilet of a room kept for other levels is theirs when no other can be had.)
                CompFixture toilet = FixtureFinder.FindFor(pawn, RPDefOf.RP_Bladder, ownRoomOnly: escortOnly, survival: true);
                if (toilet != null) return FixtureFinder.UseJob(toilet);
            }
            // (Not on a walk with a guard, or waiting for one: they would walk off to the canteen for it; the meal is brought to the cell.)
            Need_Food hunger = pawn.needs.food;
            // (A meal in their own cell, brought to the condemned, the punished and the shut in, is eaten once it fits the stomach: the errand below starts at 15% and every cell meal left them urgently hungry for the hour it took,
            // and at 30% a fifth of a meal (it is 0.9 of an adult's 1.0) was lost over the top.)
            bool mealInCell = hunger != null && hunger.CurCategory == HungerCategory.Hungry && hunger.CurLevelPercentage <= WorkGiver_DeliverCellMeal.EatsInTheCellBelow
                && pawn.GetRoom() is { IsPrisonCell: true } cellRoom && WorkGiver_DeliverCellMeal.FoodInRoom(cellRoom);
            if (hunger != null && (hunger.CurCategory >= HungerCategory.UrgentlyHungry || mealInCell) && !withGuardOrWaiting
                && (!HungerStrike.Fasting(pawn.Inmate()) || hunger.CurLevelPercentage < HungerStrike.BreakBelowFood))
            {
                Job eat = getFood.TryIssueJobPackage(pawn, default).Job;
                // (Vanilla takes the nearest meal whatever room it is in: one in a patch kept for other levels goes through the canteen search, which tries the patches their level may use first and
                // falls back to the nearest meal, so nobody starves for a sign on a door. The meal vanilla found stays theirs when that search finds none.)
                if (eat != null && own.security != null && pm != null && pm.zones.AnyKept && eat.targetA.Thing is { Spawned: true } found && !pm.zones.Allows(found.Position, own.security))
                {
                    Thing allowedMeal = RegimeWorker_Eat.FindCanteenMeal(pawn, pm, RPDefOf.RP_Canteen);
                    if (allowedMeal != null && pm.zones.Allows(allowedMeal.Position, own.security)) eat = RegimeWorker_Eat.IngestJob(pawn, allowedMeal);
                    else KeptRooms.MarkSurvivalMeal(eat);
                }
                if (eat != null && (hunger.CurCategory >= HungerCategory.UrgentlyHungry
                    || eat.targetA.Thing is { } meal && meal.GetRoom() == pawn.GetRoom() && WorkGiver_DeliverCellMeal.MealFits(hunger, FoodUtility.GetNutrition(pawn, meal, meal.def)))) return eat;
            }
            CompInmate comp = pawn.Inmate();
            // (Not on the ramp, still on the way in: an interrupted walk would drop it there.)
            if (comp != null && Find.TickManager.TicksGame >= comp.nextDrugCheckTick && !ShuttleSystem.IsArrival(pawn))
            {
                comp.nextDrugCheckTick = Find.TickManager.TicksGame + GenDate.TicksPerHour;
                Job drugs = ContrabandUtility.TryUseDrugs(pawn, comp);
                if (drugs != null) return drugs;
            }
            if (pawn.needs.rest != null && pawn.needs.rest.CurLevel < 0.12f)
            {
                Building_Bed bed = pawn.ownership?.OwnedBed;
                if (bed != null && pawn.CanReserveAndReach(bed, PathEndMode.OnCell, Danger.Some, bed.SleepingSlotsCount)) return JobMaker.MakeJob(JobDefOf.LayDown, bed);
            }
            return null;
        }
    }

    /// <summary>Runs the current regime activity's worker, following its fallback chain.</summary>
    public class JobGiver_InmateRegime : ThinkNode_JobGiver
    {
        private static List<PrisonZoneDef> holdingZones;
        private static List<PrisonZoneDef> HoldingZones => holdingZones ??= new List<PrisonZoneDef> { RPDefOf.RP_Holding };

        protected override Job TryGiveJob(Pawn pawn)
        {
            PrisonGame game = PrisonGame.Instance;
            CompInmate comp = pawn.Inmate();
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (comp.security == null) InmateGenerator.AssignProfile(pawn, comp, null);
            // Just off the state shuttle: wait by the ramp for a guard, and walk to holding with them.
            if (ShuttleSystem.ArrivalJob(pawn, comp, pm) is { } offTheShuttle) return offTheShuttle;
            if (comp.awaitingSearch)
            {
                // Intake: wait in holding until a guard has searched them (released to the cells after six hours anyway).
                if (Find.TickManager.TicksGame - System.Math.Max(comp.holdingSinceTick, comp.admittedTick) > IntakeSystem.SearchTimeoutTicks || !pm.zones.Any(RPDefOf.RP_Holding))
                {
                    comp.awaitingSearch = false;
                    PrisonGame.Instance.stats.intakeUnsearched++;
                }
                else return RegimeActivityWorker.WanderInZones(pawn, pm, HoldingZones) ?? RegimeActivityWorker.WaitJob(pawn, 300);
            }
            if (!comp.processed)
            {
                // New arrival: head for the cell if the way is open, otherwise wait to be escorted.
                Job toCell = RegimeActivityWorker.GoToOwnCell(pawn);
                if (toCell != null) return toCell;
                return RegimeActivityWorker.WaitJob(pawn, 600);
            }
            RegimeActivityDef act = game.CurrentActivity(pawn);
            bool optional = comp.forcedActivity == null && !game.LockedDown(comp);
            // Called to a parole hearing: to the hearing room (an escort walks them in), before anything else on the schedule.
            if (optional && ParoleScene.JobFor(pawn, comp, pm) is { } hearing) return hearing;
            // Out of the cell only with a guard (supermax): the walk a guard is leading them on, else the cell (or the bed at night), whatever the slot: no class, visit or yard of their own.
            if (comp.security is { escortOnly: true } && comp.forcedActivity == null)
            {
                if (EscortWalk.JobFor(pawn, comp, pm) is { } led) return led;
                // Out of the cell with nobody (a hearing is over, a patient is well again, a guard was lost): they stand where they are until a guard comes (EscortKind.Home), they do not walk the prison alone.
                if (EscortWalk.Stranded(pawn, comp)) return RegimeActivityWorker.WaitJob(pawn, 120);
                if (!act.lockup) act = RPDefOf.RP_Lockup;
                optional = false;
            }
            // Enrolled in a session: to their own session's room (whatever slot it takes its attendees from).
            if (optional && ProgramSystem.SessionOf(pawn, pm) is { } session && ProgramSystem.AttendJob(pawn, session, pm) is { } attend) return attend;
            // Sent to a funeral (the friends and the gang of the dead, let out of the regime for the hour as a class is): to the grave, the chapel or the pad.
            if (optional && Funerals.JobFor(pawn, pm) is { } mourn) return mourn;
            if (optional && comp.visitor != null && !act.lockup)
            {
                Pawn v = comp.visitor;
                // (Not into a patch kept for other levels: a visitor waiting in one is sent home.)
                // (The victim's family waits for the counsellor to sit down with them, an hour at the most: see Amends.ReadyToTalk.)
                if (v.Spawned && pm.zones.ZoneAt(v.Position) == RPVisitDefOf.RP_Visitation && VisitorSearch.Cleared(comp) && Amends.ReadyToTalk(pawn, comp) && VisitationSystem.KeepVisitInBounds(pawn, comp, pm)
                    && pm.zones.Allows(v.Position, comp.security) && pawn.CanReach(v, PathEndMode.Touch, Danger.Some))
                    return JobMaker.MakeJob(RPVisitDefOf.RP_VisitTalk, v);
            }
            for (int depth = 0; act != null && depth < 4; depth++)
            {
                Job job = act.Worker.TryGiveJob(pawn, comp, pm);
                if (job != null)
                {
                    if (job.expiryInterval < 0 && (job.def == JobDefOf.Goto || job.def == JobDefOf.GotoWander || job.def == JobDefOf.LayDownAwake))
                    {
                        job.expiryInterval = Rand.Range(1200, 2400);
                        job.checkOverrideOnExpire = true;
                    }
                    return job;
                }
                // A confined inmate whose shower slot found no shower goes back to the confinement, not to free time.
                act = comp.forcedActivity != null && act != comp.forcedActivity ? comp.forcedActivity : act.fallback;
            }
            return null;
        }
    }

    /// <summary>Behavior of one regime activity. Subclasses override <see cref="TryGiveJob"/>.</summary>
    public class RegimeActivityWorker
    {
        public RegimeActivityDef def;

        public virtual Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm) => WanderInZones(pawn, pm, def.zones);

        // ---- shared helpers -------------------------------------------------------------------------------------

        public static Job WaitJob(Pawn pawn, int ticks)
        {
            Job job = JobMaker.MakeJob(JobDefOf.Wait_Wander);
            job.expiryInterval = ticks;
            return job;
        }

        /// <summary>Walk into the own cell when outside it and the way is open; null when inside or unreachable.</summary>
        public static Job GoToOwnCell(Pawn pawn)
        {
            Building_Bed bed = pawn.ownership?.OwnedBed;
            if (bed == null) return null;
            Room cell = bed.GetRoom();
            if (cell == null || pawn.GetRoom() == cell) return null;
            if (!pawn.CanReach(bed, PathEndMode.Touch, Danger.Deadly)) return null;
            IntVec3 dest = bed.Position;
            if (CellFinder.TryFindRandomCellNear(bed.Position, pawn.Map, 2, c => c.GetRoom(pawn.Map) == cell && c.Standable(pawn.Map), out IntVec3 near))
                dest = near;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }

        /// <summary>Alternates short waits with wandering inside the current room.</summary>
        public static Job WanderInRoom(Pawn pawn)
        {
            Room room = pawn.GetRoom();
            if (pawn.mindState.nextMoveOrderIsWait)
            {
                pawn.mindState.nextMoveOrderIsWait = false;
                return WaitJob(pawn, Rand.Range(200, 600));
            }
            pawn.mindState.nextMoveOrderIsWait = true;
            IntVec3 dest = RCellFinder.RandomWanderDestFor(pawn, pawn.Position, 6f, (p, c, root) => c.GetRoom(p.Map) == room, Danger.Deadly);
            if (!dest.IsValid || dest == pawn.Position) return WaitJob(pawn, Rand.Range(200, 600));
            return JobMaker.MakeJob(JobDefOf.GotoWander, dest);
        }

        /// <summary>
        /// Go to one of the zones (in the activity's order of preference) and wander inside it: in a patch the inmate's level may use (see <see cref="ZoneGrid.SetKept"/>), the nearest of the
        /// zone's patches first. Null if no zone is reachable.
        /// </summary>
        public static Job WanderInZones(Pawn pawn, PrisonMap pm, List<PrisonZoneDef> zones)
        {
            if (pm == null || zones.NullOrEmpty()) return null;
            PrisonZoneDef here = pm.zones.ZoneAt(pawn.Position);
            SecurityLevelDef level = KeptRooms.LevelOf(pawn);
            // (Standing in a patch kept for other levels is not "here": they are sent to one they may use.)
            if (here != null && zones.Contains(here) && pm.zones.Allows(pawn.Position, level))
            {
                if (pawn.mindState.nextMoveOrderIsWait)
                {
                    pawn.mindState.nextMoveOrderIsWait = false;
                    return WaitJob(pawn, Rand.Range(250, 900));
                }
                pawn.mindState.nextMoveOrderIsWait = true;
                IntVec3 dest = RCellFinder.RandomWanderDestFor(pawn, pawn.Position, 8f, (p, c, root) => pm.zones.ZoneAt(c) == here && pm.zones.Allows(c, level), Danger.Deadly);
                if (dest.IsValid && dest != pawn.Position) return JobMaker.MakeJob(JobDefOf.GotoWander, dest);
                return WaitJob(pawn, Rand.Range(250, 900));
            }
            for (int z = 0; z < zones.Count; z++)
            {
                IntVec3 target = RandomReachableZoneCell(pawn, pm, zones[z]);
                if (target.IsValid)
                {
                    Job job = JobMaker.MakeJob(JobDefOf.Goto, target);
                    job.locomotionUrgency = LocomotionUrgency.Walk;
                    return job;
                }
            }
            return null;
        }

        /// <summary>A reachable standable cell of the zone for this pawn: only in patches its security level may use, the nearest patch first and then the others (<see cref="KeptRooms.PickCell"/>).</summary>
        public static IntVec3 RandomReachableZoneCell(Pawn pawn, PrisonMap pm, PrisonZoneDef zone) => KeptRooms.PickCell(pawn, pm, zone, KeptRooms.LevelOf(pawn));

        /// <summary>The same for a pawn who goes there on an inmate's account (a visitor to the visiting room): in the patches that inmate's level may use.</summary>
        public static IntVec3 RandomReachableZoneCell(Pawn pawn, PrisonMap pm, PrisonZoneDef zone, SecurityLevelDef level) => KeptRooms.PickCell(pawn, pm, zone, level);
    }

    /// <summary>In bed asleep (or lying awake once rested).</summary>
    public class RegimeWorker_Sleep : RegimeActivityWorker
    {
        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            Building_Bed bed = pawn.ownership?.OwnedBed;
            if (bed == null || !pawn.CanReach(bed, PathEndMode.OnCell, Danger.Deadly)) return null;
            // Someone else is being carried into it (a guard rescuing an injured inmate): wait in the cell.
            if (!pawn.CanReserve(bed, bed.SleepingSlotsCount)) return WanderInRoom(pawn);
            if (RestUtility.DisturbancePreventsLyingDown(pawn)) return WanderInRoom(pawn);
            // Vanilla's lie-down fails at once on a bed it will not let them use now (the cell no longer counts as a
            // prison cell, the bed was set to another owner type...): don't hand out a job that ends in the same tick
            // and gets handed out again (seen: "started 10 jobs in one tick"). Null: the regime falls back (the cell).
            if (!RestUtility.CanUseBedNow(bed, pawn, checkSocialProperness: false)) return null;
            if (!pawn.InBed() && pawn.needs.TryGetNeed(RPDefOf.RP_Bladder) is { CurLevel: < 0.6f })
            {
                CompFixture toilet = FixtureFinder.FindFor(pawn, RPDefOf.RP_Bladder, ownRoomOnly: true);
                if (toilet != null) return FixtureFinder.UseJob(toilet);
            }
            bool rested = pawn.needs.rest != null && pawn.needs.rest.CurLevel >= 0.95f;
            // Rested before the lights come on: a book from the library, in the cell, rather than lying awake.
            if (rested && LibrarySystem.TryRead(pawn, comp) is { } read) return read;
            return JobMaker.MakeJob(pawn.needs.rest == null || !rested ? JobDefOf.LayDown : JobDefOf.LayDownAwake, bed);
        }
    }

    /// <summary>Confined to the cell: go there, then idle inside.</summary>
    public class RegimeWorker_Lockup : RegimeActivityWorker
    {
        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            Job toCell = GoToOwnCell(pawn);
            if (toCell != null) return toCell;
            Room cell = InmateUtility.CellOf(pawn);
            if (cell != null && pawn.GetRoom() != cell) return WaitJob(pawn, 600); // locked out: staff escort them in
            return FaithUtility.TryPrayInCell(pawn, comp) ?? LibrarySystem.TryRead(pawn, comp) ?? WanderInRoom(pawn);
        }
    }

    /// <summary>Canteen: take a meal from the canteen and eat it at a table; otherwise hang around.</summary>
    public class RegimeWorker_Eat : RegimeActivityWorker
    {
        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            if (pm == null || def.zones.Count == 0 || !pm.zones.Any(def.zones[0])) return null;
            Need_Food food = pawn.needs.food;
            // Hunger strikers sit in the canteen and refuse the tray.
            if (food != null && food.CurLevelPercentage < 0.9f && !HungerStrike.Fasting(comp))
            {
                Thing meal = null;
                bool survival = false;
                for (int i = 0; i < def.zones.Count && meal == null; i++) meal = FindCanteenMeal(pawn, pm, def.zones[i], out survival);
                if (meal != null) return IngestJob(pawn, meal, survival);
            }
            return WanderInZones(pawn, pm, def.zones);
        }

        /// <param name="survival">The meal is the survival fallback's (see <see cref="FindCanteenMeal(Pawn, PrisonMap, PrisonZoneDef, out bool)"/>): eating it is in place wherever it lies.</param>
        public static Job IngestJob(Pawn pawn, Thing meal, bool survival = false)
        {
            float nutrition = FoodUtility.GetNutrition(pawn, meal, meal.def);
            Job job = JobMaker.MakeJob(JobDefOf.Ingest, meal);
            job.count = FoodUtility.WillIngestStackCountOf(pawn, meal.def, nutrition);
            if (survival) KeptRooms.MarkSurvivalMeal(job);
            return job;
        }

        /// <summary>
        /// A meal in the zone, in a canteen patch the inmate's level may use (see <see cref="ZoneGrid.SetKept"/>) while there is one with a meal they can reach; when there is none the nearest
        /// meal of the zone is still theirs, kept patch or not, so that nobody starves because of where the warden keeps the canteens (the tests log it).
        /// </summary>
        public static Thing FindCanteenMeal(Pawn pawn, PrisonMap pm, PrisonZoneDef zone) => FindCanteenMeal(pawn, pm, zone, out _);

        /// <param name="survival">The meal found is the nearest one of a patch kept for other levels, because none their level may use had one: the job for it is to be marked (<see cref="IngestJob"/>).</param>
        public static Thing FindCanteenMeal(Pawn pawn, PrisonMap pm, PrisonZoneDef zone, out bool survival)
        {
            survival = false;
            SecurityLevelDef level = KeptRooms.LevelOf(pawn);
            if (level == null || !pm.zones.AnyKept) return FindMealFor(pawn, pm, zone, null);
            Thing meal = FindMealFor(pawn, pm, zone, level);
            if (meal != null) return meal;
            meal = FindMealFor(pawn, pm, zone, null);
            survival = meal != null;
            if (meal != null && AutoTest.Active && fedOutOfBounds.Add(level)) RPLog.Message($"[RimPrison:test] kept rooms: no {zone.defName} patch kept for {level.defName} has a meal they can reach, so one in a patch kept for other levels is theirs (nobody starves)");
            return meal;
        }

        private static readonly HashSet<SecurityLevelDef> fedOutOfBounds = new HashSet<SecurityLevelDef>();

        /// <summary>A meal in the zone, the nearest by walking (vanilla's closest-thing search falls back to straight-line distance,
        /// which picks a counter behind a wall over one a few doors away); only in patches this level may use when a level is given. Stacks are shared (Ingest reserves one meal of a stack,
        /// up to 10 eaters), so a busy counter does not block the queue.</summary>
        private static Thing FindMealFor(Pawn pawn, PrisonMap pm, PrisonZoneDef zone, SecurityLevelDef onlyFor)
        {
            Map map = pawn.Map;
            mealsByCell.Clear();
            int meals = 0;
            List<Thing> indexedMeals = MealsIn(map, pm, zone);
            if (AutoTest.Active) Shadow.CheckMeals(map, pm, zone, indexedMeals);
            foreach (Thing t in indexedMeals)
                if (CanteenMeal(t, pm, zone) && (onlyFor == null || pm.zones.Allows(t.Position, onlyFor)) && pawn.CanReserve(t, 10, 1) && !mealsByCell.ContainsKey(t.Position))
                {
                    mealsByCell.Add(t.Position, t);
                    meals ^= t.Position.x * 73856093 ^ t.Position.z * 19349663; // (which cells hold a meal, in any order)
                }
            if (mealsByCell.Count == 0) return null;
            // Meals that nobody in this district can walk to (a wing's own counters are empty and the others' are behind a staff door)
            // cost a flood fill and a region search per call, at the moment the inmates are starving: remember the answer for a while.
            District district = pawn.GetDistrict();
            NoMeal entry = default;
            bool known = district != null && noMeal.TryGetValue((district, zone, onlyFor), out entry) && entry.meals == meals && entry.clears == pm.reachabilityClears && Find.TickManager.TicksGame - entry.tick < NoMealTicks;
            if (known && entry.nothing) return null;
            if (!known && pawn.Spawned && InmateWalking.TryFindNearest(map, pawn.Position, mealsByCell.ContainsKey, WalkSearchCells, out IntVec3 at, out _)
                && mealsByCell[at] is { } near && pawn.CanReach(near, PathEndMode.ClosestTouch, Danger.Deadly))
                return near;
            Thing far = GenClosest.ClosestThingReachable(pawn.Position, map, ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree),
                PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f, t => mealsByCell.TryGetValue(t.Position, out Thing m) && m == t);
            if (district != null && !known)
            {
                if (noMeal.Count > 256) noMeal.Clear();
                noMeal[(district, zone, onlyFor)] = new NoMeal { tick = Find.TickManager.TicksGame, meals = meals, clears = pm.reachabilityClears, nothing = far == null };
            }
            return far;
        }

        /// <summary>The food things lying in a zone, kept while no food thing has been added to or taken off the map and no zone repainted (the lister's own state hash says so):
        /// every inmate who asked walked every food thing of the map, which in a prison with a stocked pantry is most of them.</summary>
        private sealed class MealIndex
        {
            public int mapId = -1, listHash, zoneVersion;
            public readonly List<Thing> things = new List<Thing>();
        }

        private static readonly Dictionary<PrisonZoneDef, MealIndex> mealIndexes = new Dictionary<PrisonZoneDef, MealIndex>();

        internal static List<Thing> MealsIn(Map map, PrisonMap pm, PrisonZoneDef zone)
        {
            if (!mealIndexes.TryGetValue(zone, out MealIndex index)) mealIndexes[zone] = index = new MealIndex();
            int hash = map.listerThings.StateHashOfGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
            if (index.mapId == map.uniqueID && index.listHash == hash && index.zoneVersion == pm.zones.Version) return index.things;
            index.mapId = map.uniqueID;
            index.listHash = hash;
            index.zoneVersion = pm.zones.Version;
            index.things.Clear();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                if (t.def.IsIngestible && t.def.ingestible.preferability >= FoodPreferability.MealAwful && t.def.ingestible.preferability <= FoodPreferability.MealLavish
                    && !(t is Building) && pm.zones.ZoneAt(t.Position) == zone)
                    index.things.Add(t);
            return index.things;
        }

        private const int WalkSearchCells = 40000;
        /// <summary>How long (a tenth of an hour of game time) a district is taken to have no reachable meal, unless the meals on offer or the doors change.</summary>
        private const int NoMealTicks = 250;
        private static readonly Dictionary<IntVec3, Thing> mealsByCell = new Dictionary<IntVec3, Thing>();

        private struct NoMeal
        {
            public int tick, meals, clears;
            /// <summary>Not even the vanilla search found one (otherwise only the walking fill came up empty).</summary>
            public bool nothing;
        }

        private static readonly Dictionary<(District, PrisonZoneDef, SecurityLevelDef), NoMeal> noMeal = new Dictionary<(District, PrisonZoneDef, SecurityLevelDef), NoMeal>();

        /// <summary>Forget which districts could not reach a meal (tests change the layout between calls).</summary>
        public static void ForgetNoMeal()
        {
            noMeal.Clear();
            fedOutOfBounds.Clear();
            KeptRooms.ForgetSurvivalMeals();
            mealIndexes.Clear();
            WorkGiver_DeliverCellMeal.ForgetCanteenReach();
        }

        public static bool CanteenMeal(Thing t, PrisonMap pm, PrisonZoneDef zone) =>
            t.def.IsIngestible && t.def.ingestible.preferability >= FoodPreferability.MealAwful && t.def.ingestible.preferability <= FoodPreferability.MealLavish
            && !(t is Building) && t.Spawned && pm.zones.ZoneAt(t.Position) == zone && t.IngestibleNow;
    }

    /// <summary>Showers: wash if not clean, then free time in the shower block.</summary>
    public class RegimeWorker_Shower : RegimeActivityWorker
    {
        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            Need hygiene = pawn.needs.TryGetNeed(RPDefOf.RP_Hygiene);
            if (hygiene != null && hygiene.CurLevel < 0.8f)
            {
                CompFixture shower = FixtureFinder.FindFor(pawn, RPDefOf.RP_Hygiene, def.zones.Count > 0 ? def.zones[0] : null);
                if (shower != null) return FixtureFinder.UseJob(shower);
            }
            return WanderInZones(pawn, pm, def.zones);
        }
    }

    /// <summary>Free time: recreation and exercise where possible, otherwise roam the yard/common areas.</summary>
    public class RegimeWorker_FreeTime : RegimeActivityWorker
    {
        private static readonly JobGiver_GetJoy getJoy = CreateJoyGiver();

        /// <summary>Hygiene below this and free time starts with a wash (it was 30%: below 55% they are grimy, below 35% dirty, and the thought is -2, -5 and -10 mood; the showcase's inmates sat at 20-45% by day three).</summary>
        public const float FreeTimeWashBelow = 0.5f;

        private static JobGiver_GetJoy CreateJoyGiver()
        {
            var giver = new JobGiver_GetJoy();
            giver.ResolveReferences(); // allocates joyGiverChances; think-tree nodes get this from the loader
            return giver;
        }

        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            // Contraband lying around (thrown over the wall, dropped in a scuffle) disappears into pockets.
            Thing loose = ContrabandUtility.FindLooseContraband(pawn, pm);
            if (loose != null)
            {
                Job grab = JobMaker.MakeJob(JobDefOf.TakeInventory, loose);
                grab.count = loose.stackCount;
                return grab;
            }
            // Missed a meal: the canteen serves leftovers in free time.
            if (pawn.needs.food != null && pawn.needs.food.CurLevelPercentage < 0.6f && !HungerStrike.Fasting(comp))
            {
                Thing meal = RegimeWorker_Eat.FindCanteenMeal(pawn, pm, RPDefOf.RP_Canteen, out bool survival);
                if (meal != null) return RegimeWorker_Eat.IngestJob(pawn, meal, survival);
            }
            // Missed the shower slot (or a day's wear is already showing): wash in free time, before the mood penalties start (grimy at 55%).
            if (pawn.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { CurLevel: < FreeTimeWashBelow })
            {
                CompFixture shower = FixtureFinder.FindFor(pawn, RPDefOf.RP_Hygiene, RPDefOf.RP_Shower);
                if (shower != null) return FixtureFinder.UseJob(shower);
            }
            Job pray = FaithUtility.TryPray(pawn, comp, pm);
            if (pray != null) return pray;
            Job workout = ExerciseUtility.TryWorkout(pawn);
            if (workout != null) return workout;
            Need family = pawn.needs.TryGetNeed(RPVisitDefOf.RP_Family);
            if (family != null && family.CurLevel < 0.4f && Rand.Chance(0.4f))
            {
                CompFixture phone = FixtureFinder.FindFor(pawn, RPVisitDefOf.RP_Family);
                if (phone != null && pm.zones.ZoneAt(phone.parent.Position) is not { staffOnly: true }) return FixtureFinder.UseJob(phone);
            }
            Job shop = CommissarySystem.TryShop(pawn, comp, pm);
            if (shop != null) return shop;
            if (pawn.needs.joy != null && pawn.needs.joy.CurLevel < 0.7f && Rand.Chance(0.6f))
            {
                Job joy = getJoy.TryIssueJobPackage(pawn, default).Job;
                if (joy != null && JoyTargetAllowed(pawn, pm, joy, def.zones)) return joy;
            }
            return WanderInZones(pawn, pm, def.zones);
        }

        /// <summary>Joy givers search the whole reachable area; keep inmates to the free-time zones (or their own cell).</summary>
        private static bool JoyTargetAllowed(Pawn pawn, PrisonMap pm, Job job, List<PrisonZoneDef> zones)
        {
            LocalTargetInfo t = job.targetA;
            if (!t.IsValid) return false;
            IntVec3 cell = t.Thing?.Position ?? t.Cell;
            if (pm == null) return true;
            if (zones.Contains(pm.zones.ZoneAt(cell))) return KeptRooms.Allows(pm, cell, pawn);
            return cell.GetRoom(pawn.Map) == InmateUtility.CellOf(pawn);
        }
    }
}
