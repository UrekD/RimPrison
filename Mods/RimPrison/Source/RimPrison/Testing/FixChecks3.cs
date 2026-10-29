using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the big-prison feeding change (counters, pace, mail trips, the service alert, the meal search).
    /// Each is its own method called once from FireAllEvents, works on throwaway pawns and puts back what it touches (the
    /// <see cref="Rollback"/>: its undo actions run last to first, in a finally, even when a check throws).
    /// </summary>
    public static partial class AutoTest
    {
        private static List<IntVec3> CanteenCounterCells(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            return MealCounters.On(map).Where(c => pm.zones.ZoneAt(c.Position) == RPDefOf.RP_Canteen).SelectMany(c => c.OccupiedRect()).ToList();
        }

        /// <summary>
        /// Room on the counter for <paramref name="n"/> more simple meals: a real counter in a running prison may hold nearly all it can (or other meals in the cell's slots), and
        /// <see cref="AddMeals"/> then fits none. Its own meals are set aside, one stack at a time (the other kinds of meal first, then the smallest), until they fit; the rollback
        /// puts them back (it runs after the added meals are gone, being registered before them).
        /// </summary>
        private static void MakeRoomForMeals(Rollback r, Map map, Building_Storage counter, int n)
        {
            int Room()
            {
                int room = 0;
                foreach (IntVec3 c in counter.AllSlotCellsList())
                {
                    List<Thing> things = c.GetThingList(map);
                    int stacks = things.Count(t => t.def.category == ThingCategory.Item);
                    room += Math.Max(0, counter.def.building.maxItemsInCell - stacks) * ThingDefOf.MealSimple.stackLimit;
                    foreach (Thing t in things)
                        if (t.def == ThingDefOf.MealSimple) room += t.def.stackLimit - t.stackCount;
                }
                return room;
            }
            var aside = new List<(Thing t, IntVec3 cell)>();
            r.Add(() =>
            {
                foreach ((Thing t, IntVec3 cell) in aside)
                    if (!t.Spawned && !t.Destroyed) GenSpawn.Spawn(t, cell, map);
            });
            while (Room() < n)
            {
                Thing next = counter.AllSlotCellsList().SelectMany(c => c.GetThingList(map)).Where(MealCounters.IsMeal)
                    .OrderBy(t => t.def == ThingDefOf.MealSimple ? 1 : 0).ThenBy(t => t.stackCount).FirstOrDefault();
                if (next == null) return;
                aside.Add((next, next.Position));
                next.DeSpawn();
            }
        }

        /// <summary>
        /// Puts <paramref name="n"/> simple meals on the counter (on its cells, stacks of up to the meal's stack limit); the rollback puts
        /// the counter's meals back as they were (new stacks destroyed, the old ones at their old size). False when they did not all fit.
        /// </summary>
        private static bool AddMeals(Rollback r, Map map, Building_Storage counter, int n)
        {
            List<IntVec3> cells = counter.AllSlotCellsList().ToList();
            var before = new List<(Thing t, int count)>();
            foreach (IntVec3 c in cells)
                foreach (Thing t in c.GetThingList(map))
                    if (MealCounters.IsMeal(t)) before.Add((t, t.stackCount));
            r.Add(() =>
            {
                foreach (IntVec3 c in cells)
                    foreach (Thing t in c.GetThingList(map).ToList())
                        if (MealCounters.IsMeal(t) && before.All(b => b.t != t)) t.Destroy();
                foreach ((Thing t, int count) in before)
                    if (!t.Destroyed) t.stackCount = count;
            });
            // (What is left to place is read off the counter: a meal that joins a part-full stack and has no slot of its own for the rest is placed in part and reported as not placed.)
            int had = MealCounters.Meals(counter);
            foreach (IntVec3 c in cells)
                while (n - (MealCounters.Meals(counter) - had) is var left && left > 0)
                {
                    Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                    meal.stackCount = Math.Min(left, meal.def.stackLimit);
                    if (!GenPlace.TryPlaceThing(meal, c, map, ThingPlaceMode.Direct))
                    {
                        if (!meal.Destroyed) meal.Destroy();
                        break;
                    }
                }
            return MealCounters.Meals(counter) - had == n;
        }

        // ---- 2: the counters' capacity is the meals they hold, so the margin is meals, not a fraction of one -----------------------

        /// <summary>
        /// A serving counter holds its cells' stacks of <i>meals</i> (10 to a stack), not stacks of the building's own stack limit (1): the capacity of a
        /// 3-cell counter is 90, and the "clearly emptier" margin about 7 meals. A meal stays with the counter the cook chose when the other is
        /// a few meals emptier (it was moved for a single meal's difference), and goes to the emptier one when it is well below.
        /// </summary>
        private static void CheckMealCountersUseTheMealsStack(Map map)
        {
            Guarded("meal counter capacity", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                ThingDef mealDef = ThingDefOf.MealSimple;
                List<Building_Storage> counters = MealCounters.On(map).Where(c => pm.zones.ZoneAt(c.Position) == RPDefOf.RP_Canteen && c.HaulDestinationEnabled).ToList();
                if (counters.Count < 2)
                {
                    problems0.Add($"meal counter test: needs two serving counters in the canteen, found {counters.Count}");
                    return;
                }
                Building_Storage a = counters[0], b = counters[1];
                int capacity = MealCounters.Capacity(a, mealDef);
                float margin = capacity * MealCounters.Margin;
                RPLog.Message($"[RimPrison:test] meal counters: {a.AllSlotCellsList().Count} cells x {a.def.building.maxItemsInCell} stacks x {mealDef.stackLimit} meals = capacity {capacity}, margin {margin:F1} meals");
                if (a.AllSlotCellsList().Count != 3 || a.def.building.maxItemsInCell != 3 || mealDef.stackLimit != 10)
                {
                    problems0.Add($"meal counter test: the counter is {a.AllSlotCellsList().Count} cells of {a.def.building.maxItemsInCell} stacks and a meal stacks to {mealDef.stackLimit} (3, 3 and 10 expected)");
                    return;
                }
                if (capacity != 90 || margin < 5f || margin > 10f)
                    problems0.Add($"meal counters: a 3-cell counter holds {capacity} meals (90 expected) and the margin is {margin:F1} meals (about 7 expected)");

                Thing meal = ThingMaker.MakeThing(mealDef);
                r.Add(() =>
                {
                    if (!meal.Destroyed) meal.Destroy();
                });
                // The counter the meal would go to, with this many more meals on it than the other one.
                Building_Storage Chosen(int difference, out Building_Storage other)
                {
                    (Building_Storage hi, Building_Storage lo) = MealCounters.Meals(a) >= MealCounters.Meals(b) ? (a, b) : (b, a);
                    int now = MealCounters.Meals(hi) - MealCounters.Meals(lo);
                    if (now < difference) AddMeals(r, map, hi, difference - now);
                    else if (now > difference) AddMeals(r, map, lo, now - difference);
                    other = lo;
                    return hi;
                }
                Building_Storage Where(Building_Storage hi) =>
                    MealCounters.Balance(meal, null, map, Faction.OfPlayer, hi.AllSlotCellsList()[0]).GetSlotGroup(map)?.parent as Building_Storage;

                Building_Storage close = Chosen(3, out Building_Storage closeLo);
                if (MealCounters.Meals(close) + 30 > capacity || MealCounters.Meals(closeLo) + 30 > capacity)
                {
                    problems0.Add("meal counter test: the counters are too full to set up");
                    return;
                }
                Building_Storage wentClose = Where(close);
                Building_Storage far = Chosen(15, out Building_Storage farLo);
                Building_Storage wentFar = Where(far);
                RPLog.Message($"[RimPrison:test] meal counters: 3 meals fuller stays {wentClose == close}, 15 meals fuller moves {wentFar == farLo}");
                if (wentClose != close)
                    problems0.Add("meal counters: a counter only 3 meals (of 90) fuller than the other lost the meal to it");
                if (wentFar != farLo)
                    problems0.Add("meal counters: a counter 15 meals (of 90) fuller than the other kept the meal");
            });
        }

        // ---- 3: who owes punishment keeps the slow pace --------------------------------------------------------------------------

        /// <summary>
        /// Active inmates walk at their own speed; one a guard has to restrain (owes punishment for what was seen) and one in a mental state keep the 35%
        /// of a prisoner in restraints, so that the guard (who needs 90 still ticks next to the inmate) can catch up. The pace is the game's own
        /// <see cref="Pawn.TicksPerMoveCardinal"/>.
        /// </summary>
        private static void CheckOwedPunishmentKeepsSlowPace(Map map)
        {
            Guarded("slow pace for who is wanted", map, r =>
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                int owed = c.pendingPunishmentHours;
                r.Add(() =>
                {
                    c.pendingPunishmentHours = owed;
                    p.mindState.mentalStateHandler.Reset();
                });
                c.pendingPunishmentHours = 0;
                float normal = p.TicksPerMoveCardinal;
                bool normalSlow = RestraintsUtility.InRestraints(p);
                c.pendingPunishmentHours = 6;
                float owing = p.TicksPerMoveCardinal;
                bool owingSlow = RestraintsUtility.InRestraints(p);
                c.pendingPunishmentHours = 0;
                p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, transitionSilently: true);
                bool mental = p.InMentalState;
                float raving = p.TicksPerMoveCardinal;
                bool ravingSlow = RestraintsUtility.InRestraints(p);
                p.mindState.mentalStateHandler.Reset();
                float calmAgain = p.TicksPerMoveCardinal;
                RPLog.Message($"[RimPrison:test] pace: ticks per cell {normal:F1} ordinary, {owing:F1} owing punishment, {raving:F1} in a mental state ({mental}), {calmAgain:F1} calm again");
                if (normalSlow || normal <= 0f || calmAgain != normal)
                    problems0.Add($"pace test: an ordinary inmate is slowed {normalSlow} ({normal:F1} ticks a cell, {calmAgain:F1} again once calm)");
                if (!owingSlow || owing < normal * 2.5f)
                    problems0.Add($"pace test: an inmate who owes punishment walks at {owing:F1} ticks a cell (ordinary {normal:F1}, slowed {owingSlow}); a guard cannot restrain them");
                if (!mental || !ravingSlow || raving < normal * 2.5f)
                    problems0.Add($"pace test: an inmate in a mental state ({mental}) walks at {raving:F1} ticks a cell (ordinary {normal:F1}, slowed {ravingSlow})");
            });
        }

        // ---- 4: a mail trip gives way to trouble and does not run long ---------------------------------------------------------------

        /// <summary>
        /// A guard sorting the post (a job that is not casually interruptible) ends the trip within a check of an alarm they would be sent to (an inmate
        /// running amok, nobody on it yet), on foot or at the table, and no letter is started that would end past the cap. An inmate who only owes punishment
        /// does not end it. (The inmate is reserved by the guard, so that another guard cannot take them first.)
        /// </summary>
        private static void CheckMailTripYieldsToAlarm(Map map)
        {
            Guarded("mail trip yields to an alarm", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                int sortRead = MailSystem.SortTicks(MailPolicy.ReadAll), sortSpot = MailSystem.SortTicks(MailPolicy.SpotChecks);
                bool capOk = JobDriver_SortMail.MayStartLetter(0, MailPolicy.ReadAll) && JobDriver_SortMail.MayStartLetter(JobDriver_SortMail.MaxSortingTicks - sortRead, MailPolicy.ReadAll)
                    && !JobDriver_SortMail.MayStartLetter(JobDriver_SortMail.MaxSortingTicks - sortRead + 1, MailPolicy.ReadAll) && !JobDriver_SortMail.MayStartLetter(JobDriver_SortMail.MaxSortingTicks, MailPolicy.SpotChecks)
                    && JobDriver_SortMail.MaxSortingTicks <= GenDate.TicksPerHour && JobDriver_SortMail.MaxSortingTicks >= sortRead;
                if (!capOk) problems0.Add($"mail trip cap: {JobDriver_SortMail.MaxSortingTicks} ticks, a letter takes {sortRead} (read) or {sortSpot} (spot check); the cap is not applied to the letter's end");

                // The mailroom: the map's own, else painted over a table in a staff zone.
                if (!MailSystem.HasMailroom(map))
                {
                    Building table = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.surfaceType == SurfaceType.Eat && pm.zones.ZoneAt(b.Position) is { staffOnly: true });
                    if (table == null)
                    {
                        problems0.Add("mail trip test: the prison has no mailroom and there is no table in a staff zone to paint one over");
                        return;
                    }
                    foreach (IntVec3 cell in table.OccupiedRect().Cells.ToList())
                    {
                        PrisonZoneDef old = pm.zones.ZoneAt(cell);
                        r.Add(() => pm.zones.Set(cell, old));
                        pm.zones.Set(cell, RPMailDefOf.RP_Mailroom);
                    }
                }
                MailPolicy policy0 = g.mailPolicy;
                List<MailLetter> waiting0 = g.mail.waiting.ToList();
                r.Add(() =>
                {
                    g.mailPolicy = policy0;
                    g.mail.waiting.Clear();
                    g.mail.waiting.AddRange(waiting0);
                });
                g.mailPolicy = MailPolicy.ReadAll; // (a letter takes 600 ticks: none is finished while the check runs)
                Pawn writer = Throwaway(map, r);
                g.mail.waiting.Clear();
                for (int i = 0; i < 3; i++) g.mail.waiting.Add(g.mail.Compose(writer, writer.Inmate(), RPMailDefOf.RP_Mail_Ordinary, hides: false));
                // (A guard who may fight at all: one who may not is never sent to an alarm.)
                Pawn guard = null;
                for (int i = 0; i < 8 && (guard == null || guard.WorkTagIsDisabled(WorkTags.Violent)); i++) guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Thing mailTable = guard.WorkTagIsDisabled(WorkTags.Violent) ? null : MailSystem.Tables(map).FirstOrDefault(t => guard.CanReserveAndReach(t, PathEndMode.Touch, Danger.Some));
                if (mailTable == null)
                {
                    RPLog.Message("[RimPrison:test] mail trip test: no free mail table or no guard who can fight, the job part is skipped");
                    return;
                }
                // At the table, far from the inmate who runs amok at the intake below (a guard next to them would flee, which also ends the job).
                IntVec3 beside = mailTable.OccupiedRect().AdjacentCells.FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null);
                if (!beside.IsValid)
                {
                    RPLog.Message("[RimPrison:test] mail trip test: no free cell beside the mail table, the job part is skipped");
                    return;
                }
                guard.Position = beside;
                guard.Notify_Teleported();
                // (Somebody real is running amok within this guard's reach: the alarm is the game's own, and this check cannot tell it from the one it makes.)
                if (GuardUtility.AlarmFor(guard) || map.mapPawns.PrisonersOfColonySpawned.Any(x => x.InMentalState))
                {
                    RPLog.Message("[RimPrison:test] mail trip test: an alarm is already on in the prison, the job part is skipped");
                    return;
                }
                guard.jobs.StartJob(JobMaker.MakeJob(RPMailDefOf.RP_SortMail, mailTable), JobCondition.InterruptForced);
                // An inmate who owes punishment, a guard's to restrain (reserved by this guard, so that another cannot take them first): no alarm,
                // the trip goes on past the check.
                Pawn rowdy = Throwaway(map, r);
                r.Add(() => rowdy.mindState.mentalStateHandler.Reset());
                rowdy.Inmate().pendingPunishmentHours = 6;
                guard.Reserve(rowdy, guard.CurJob, 1, -1, null, false);
                for (int i = 0; i < 70; i++) Find.TickManager.DoSingleTick();
                bool owedQuiet = guard.CurJobDef == RPMailDefOf.RP_SortMail && !GuardUtility.AlarmFor(guard) && GuardUtility.NeedsSubduing(rowdy) && GuardUtility.CanRespondTo(guard, rowdy);
                if (!owedQuiet)
                {
                    problems0.Add($"mail trip test: with an inmate who owes punishment (and nothing else) the guard's sorting ended or an alarm was seen (job now {guard.CurJobDef?.defName ?? "none"})");
                    return;
                }
                // The inmate runs amok: an alarm. The trip ends within a check.
                rowdy.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, transitionSilently: true);
                bool alarm = GuardUtility.AlarmFor(guard);
                int waited = 0;
                while (waited < 3 * JobDriver_SortMail.AlarmCheckTicks && guard.CurJobDef == RPMailDefOf.RP_SortMail)
                {
                    Find.TickManager.DoSingleTick();
                    waited++;
                }
                RPLog.Message($"[RimPrison:test] mail trip: alarm seen {alarm}, the guard left the post after {waited} ticks (now {guard.CurJobDef?.defName ?? "none"}, downed {guard.Downed})");
                // (Sent to restrain them: the alarm ended the job, not a fright or an injury.)
                if (!alarm || guard.CurJobDef != RPSecurityDefOf.RP_Subdue || waited > JobDriver_SortMail.AlarmCheckTicks + 2)
                    problems0.Add($"mail trip test: alarm for the guard {alarm}; the guard's job after {waited} ticks is {guard.CurJobDef?.defName ?? "none"} (sorting until the check, which runs every {JobDriver_SortMail.AlarmCheckTicks}, then restraining expected)");
            });
        }

        // ---- 5: the service alert's layout view ----------------------------------------------------------------------------------------

        private static CompPrisonDoor CellDoorOf(PrisonMap pm, Building_Bed bed) => bed.GetRoom() is { } room ? pm.Doors.FirstOrDefault(d => d.cellRoom == room) : null;

        /// <summary>A cell door the warden has locked is not a wall of the layout: the walking distance from its bed to a counter does not change.</summary>
        private static void CheckServiceLayoutIgnoresLockedCells(Map map)
        {
            Guarded("service layout: locked cells", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<IntVec3> counters = CanteenCounterCells(map);
                foreach (Building_Bed bed in pm.PrisonerBeds.Where(b => b.Spawned))
                {
                    CompPrisonDoor door = CellDoorOf(pm, bed);
                    if (door == null) continue;
                    var beds = new List<IntVec3> { bed.Position };
                    int open = InmateWalking.DistancesTo(map, counters, beds)[0];
                    if (open == int.MaxValue) continue;
                    PrisonDoorMode mode = door.mode;
                    r.Add(() => door.mode = mode);
                    door.mode = PrisonDoorMode.Locked;
                    int locked = InmateWalking.DistancesTo(map, counters, beds)[0];
                    RPLog.Message($"[RimPrison:test] service layout: bed {bed.Position} is {open} cells from a counter, {locked} with its cell door locked");
                    if (locked != open) problems0.Add($"service layout: a locked cell door makes the bed {(locked == int.MaxValue ? "unreachable" : locked + " cells away")} (open: {open})");
                    return;
                }
                problems0.Add("service layout test: no bed in a cell with a door that can reach a counter");
            });
        }

        /// <summary>Inmates who eat in a cell (death row, supermax) or are confined to one do not count towards the canteen's ratios and distances.</summary>
        private static void CheckServiceSkipsCellEaters(Map map)
        {
            Guarded("service alert: cell eaters", map, r =>
            {
                SecurityLevelDef cellEaters = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(d => d.cellMeals);
                if (cellEaters == null || map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && PrisonServices.UsesCanteen(x)) is not { } p)
                {
                    problems0.Add($"service alert test: no cell-meals security level ({cellEaters != null}) or no inmate who uses the canteen");
                    return;
                }
                CompInmate c = p.Inmate();
                (SecurityLevelDef security, RegimeActivityDef forced) = (c.security, c.forcedActivity);
                r.Add(() =>
                {
                    (c.security, c.forcedActivity) = (security, forced);
                    PrisonServices.Reset();
                });
                PrisonServices.Reset();
                int all = PrisonServices.Of(map).inmates;
                c.security = cellEaters;
                PrisonServices.Reset();
                int withoutDeathRow = PrisonServices.Of(map).inmates;
                c.security = security;
                c.forcedActivity = RPDefOf.RP_Lockup;
                PrisonServices.Reset();
                int withoutConfined = PrisonServices.Of(map).inmates;
                RPLog.Message($"[RimPrison:test] service alert: {all} inmates count, {withoutDeathRow} with one on a cell-meals level, {withoutConfined} with one confined");
                if (withoutDeathRow != all - 1 || withoutConfined != all - 1)
                    problems0.Add($"service alert: {all} inmates counted, {withoutDeathRow} with one eating in a cell by security level, {withoutConfined} with one confined (each {all - 1} expected)");
            });
        }

        /// <summary>
        /// Inmates walk diagonally, so the distance does: 30 cells across and 30 up is 30 diagonal steps (about 42 cells of walking, not 60), 30 across
        /// and 15 up is 36. Measured over open ground, where nothing can be shorter.
        /// </summary>
        private static void CheckWalkingDistanceIsEightConnected(Map map)
        {
            Guarded("walking distance is diagonal", map, r =>
            {
                CellRect open = CellRect.Empty;
                for (int x = 5; x + 36 < map.Size.x && open.IsEmpty; x += 7)
                    for (int z = 5; z + 36 < map.Size.z && open.IsEmpty; z += 7)
                    {
                        var rect = new CellRect(x, z, 31, 31);
                        if (rect.Cells.All(c => c.Walkable(map) && c.GetEdifice(map) == null)) open = rect;
                    }
                if (open.IsEmpty)
                {
                    problems0.Add("walking distance test: no open 31 x 31 ground on the map");
                    return;
                }
                IntVec3 start = new IntVec3(open.minX, 0, open.minZ);
                var targets = new List<IntVec3> { start + new IntVec3(30, 0, 30), start + new IntVec3(30, 0, 0), start + new IntVec3(30, 0, 15) };
                int[] steps = InmateWalking.DistancesTo(map, new[] { start }, targets);
                RPLog.Message($"[RimPrison:test] walking distance over open ground from {start}: diagonal {steps[0]}, straight {steps[1]}, mixed {steps[2]}");
                if (steps[0] != 42 || steps[1] != 30 || steps[2] != 36)
                    problems0.Add($"walking distance: 30 diagonal steps read {steps[0]} (42 expected), 30 straight {steps[1]} (30), 15 diagonal and 15 straight {steps[2]} (36)");
                bool found = InmateWalking.TryFindNearest(map, start, c => c == targets[0], 4000, out _, out int nearest);
                if (!found || nearest != 42) problems0.Add($"walking distance: the nearest-cell search reads {nearest} to the diagonal corner (42 expected)");
            });
        }

        // ---- 6: the hungry line's meal count is live ---------------------------------------------------------------------------------------

        /// <summary>The "although N meals lie on the counters" line counts them as they are now, not as the hourly numbers had them.</summary>
        private static void CheckHungryLineCountsLive(Map map)
        {
            Guarded("hungry line", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Building_Storage counter = MealCounters.On(map).FirstOrDefault(c => pm.zones.ZoneAt(c.Position) == RPDefOf.RP_Canteen);
                if (counter == null)
                {
                    problems0.Add("hungry line test: no serving counter in the canteen");
                    return;
                }
                MakeRoomForMeals(r, map, counter, 7);
                r.Add(PrisonServices.Reset);
                PrisonServices.Reset();
                int cached = PrisonServices.Of(map).meals;
                if (!AddMeals(r, map, counter, 7))
                {
                    problems0.Add($"hungry line test: the counter has no room for seven more meals ({MealCounters.Meals(counter)} on its {counter.AllSlotCellsList().Count} cells: {string.Join(", ", counter.AllSlotCellsList().SelectMany(c => c.GetThingList(map)).Where(t => t.def.category == ThingCategory.Item).Select(t => t.stackCount + " " + t.def.defName))})");
                    return;
                }
                string line = PrisonDiagnostics.HungryLine(map, 3).Resolve();
                string live = "RP_Problem_HungryMealsThere".Translate(3, cached + 7).Resolve(), stale = (cached > 0 ? "RP_Problem_HungryMealsThere".Translate(3, cached) : "RP_Problem_Hungry".Translate(3)).Resolve();
                RPLog.Message($"[RimPrison:test] hungry line with {cached} meals in the hourly numbers and {cached + 7} on the counters: \"{line}\"");
                if (line != live || line == stale)
                    problems0.Add($"hungry line: with {cached + 7} meals on the counters (hourly numbers: {cached}) it reads \"{line}\"");
            });
        }

        // ---- 7: the meal search remembers a district that cannot reach any ------------------------------------------------------------------------

        /// <summary>
        /// An inmate shut in a locked cell, with meals on the counters they cannot walk to: the first search spends a flood fill, the next in the same district
        /// (until the meals on offer or the doors change, or a tenth of an hour passes) none. An open door is noticed.
        /// </summary>
        private static void CheckCanteenSearchRemembersNoMeal(Map map)
        {
            Guarded("canteen search cache", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Building_Storage counter = MealCounters.On(map).FirstOrDefault(c => pm.zones.ZoneAt(c.Position) == RPDefOf.RP_Canteen);
                // (A cell with the one door and no other way out: a room with a second door, or one open to a corridor, is not shut by locking the first.)
                int DoorsOf(Building_Bed b) => b.GetRoom().ContainedAndAdjacentThings.OfType<Building_Door>().Distinct().Count();
                // (And a door that is shut: a locked door that stands open, somebody in the doorway or through it a moment ago, lets vanilla's reachability through as an open door does, and an inmate behind it is rightly sent to a meal.)
                bool DoorIsShut(Building_Bed b) => CellDoorOf(pm, b)?.parent is Building_Door d && ShutTheDoor(d);
                Building_Bed bed = pm.PrisonerBeds.FirstOrDefault(b => b.Spawned && CellDoorOf(pm, b) != null && DoorsOf(b) == 1 && b.GetRoom().Cells.Any(c => c.Standable(map) && c.GetFirstPawn(map) == null) && DoorIsShut(b))
                                   ?? pm.PrisonerBeds.FirstOrDefault(b => b.Spawned && CellDoorOf(pm, b) != null && b.GetRoom().Cells.Any(c => c.Standable(map) && c.GetFirstPawn(map) == null) && DoorIsShut(b));
                if (counter == null || bed == null)
                {
                    problems0.Add($"canteen search test: no serving counter ({counter != null}) or no cell with a door ({bed != null})");
                    return;
                }
                CompPrisonDoor door = CellDoorOf(pm, bed);
                IntVec3 inside = bed.GetRoom().Cells.First(c => c.Standable(map) && c.GetFirstPawn(map) == null);
                MakeRoomForMeals(r, map, counter, 5);
                if (!AddMeals(r, map, counter, 5))
                {
                    problems0.Add($"canteen search test: the counter has no room for meals ({MealCounters.Meals(counter)} on its {counter.AllSlotCellsList().Count} cells: {string.Join(", ", counter.AllSlotCellsList().SelectMany(c => c.GetThingList(map)).Where(t => t.def.category == ThingCategory.Item).Select(t => t.stackCount + " " + t.def.defName))})");
                    return;
                }
                RegimeWorker_Eat.ForgetNoMeal();
                r.Add(RegimeWorker_Eat.ForgetNoMeal);
                Pawn p = Throwaway(map, r);
                p.pather.StopDead();
                p.Position = inside;
                p.Notify_Teleported();
                PrisonDoorMode mode = door.mode;
                r.Add(() =>
                {
                    door.mode = mode;
                    pm.ForceDoorUpdate();
                });
                door.mode = PrisonDoorMode.Locked;
                pm.ForceDoorUpdate();
                bool doorOpen = door.parent is Building_Door { Open: true };
                int fills0 = InmateWalking.Fills;
                Thing first = RegimeWorker_Eat.FindCanteenMeal(p, pm, RPDefOf.RP_Canteen);
                int fills1 = InmateWalking.Fills;
                Thing second = RegimeWorker_Eat.FindCanteenMeal(p, pm, RPDefOf.RP_Canteen);
                int fills2 = InmateWalking.Fills;
                RegimeWorker_Eat.ForgetNoMeal();
                RegimeWorker_Eat.FindCanteenMeal(p, pm, RPDefOf.RP_Canteen);
                int fills3 = InmateWalking.Fills;
                door.mode = mode;
                pm.ForceDoorUpdate(); // (the doors changed: a fresh look)
                RegimeWorker_Eat.FindCanteenMeal(p, pm, RPDefOf.RP_Canteen);
                int fills4 = InmateWalking.Fills;
                RPLog.Message($"[RimPrison:test] canteen search from a locked cell: meal {first?.LabelShort ?? "none"}/{second?.LabelShort ?? "none"}, fills {fills1 - fills0} then {fills2 - fills1}, after forgetting {fills3 - fills2}, after the door changed {fills4 - fills3}");
                if (first != null || second != null)
                    problems0.Add($"canteen search test: an inmate in a locked cell was sent to a meal ({first?.LabelShort}/{second?.LabelShort} at {first?.Position}; the cell {bed.GetRoom().CellCount} cells with {DoorsOf(bed)} doors [{string.Join(", ", bed.GetRoom().ContainedAndAdjacentThings.OfType<Building_Door>().Distinct().Select(d => $"{d.Position} {d.GetComp<CompPrisonDoor>()?.mode} open {d.Open}"))}], the locked door {door.parent.Position} {door.mode} (open when searched: {doorOpen}), the pawn at {p.Position} can reach the counter {p.CanReach(counter, PathEndMode.Touch, Danger.Deadly)}, room is outdoors {bed.GetRoom().PsychologicallyOutdoors})");
                else if (fills1 - fills0 < 1 || fills2 != fills1 || fills3 <= fills2 || fills4 <= fills3)
                    problems0.Add($"canteen search cache: flood fills {fills1 - fills0} for the first search, {fills2 - fills1} for the second (0 expected), {fills3 - fills2} after forgetting (at least 1), {fills4 - fills3} after the doors changed (at least 1)");
            });
        }

        // ---- the Wants set-aside line keeps a hidden gang hidden --------------------------------------------------------------------------------

        /// <summary>
        /// A hidden-gang member with an announced goal who takes up a grudge against staff puts the old goal aside without a line (it would name them in the
        /// chronicle, with the gang they belong to); once the gang is known, the same replacement is told.
        /// </summary>
        private static void CheckHiddenGangKeepsSetAsideQuiet(Map map)
        {
            Guarded("hidden gang set-aside", map, r =>
            {
                r.Add(RememberGoals(map.mapPawns.PrisonersOfColonySpawned));
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.First();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn victim = Throwaway(map, r), mate = Throwaway(map, r);
                CompInmate vc = victim.Inmate(), mc = mate.Inmate();
                (vc.gang, vc.gangKnown) = (gang, false);
                (mc.gang, mc.gangKnown) = (gang, false);
                mc.staffMemories.Clear();
                mc.records = new List<MisconductRecord>();
                (mc.sentenceDays, mc.admittedTick, mc.lastHearingTick, mc.programsPassed, mc.hoursWorked, mc.tension) = (40, 0, -999999, 2, 0, 5f);
                mc.want = null;
                InmateWant parole = Wants.Form(mate, mc, RPWantDefOf.RP_Want_Parole);
                if (parole == null || !parole.announced)
                {
                    problems0.Add($"hidden set-aside test: no announced goal to put aside ({parole != null})");
                    return;
                }
                var hidden = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { gang: { } gx, gangKnown: false } && gx == gang).Select(x => x.thingIDNumber).ToHashSet();
                // (The first goal's own announcement is not what is looked for.)
                List<ChronicleEntry> setUp = r.NewEntries.ToList();
                IEnumerable<ChronicleEntry> Since() => r.NewEntries.Where(e => !setUp.Contains(e));
                StaffMemories.Notify_ShotDead(victim, guard);
                string setAside = WantLine("RP_Chr_WantSetAside", mate, parole);
                bool replaced = mc.want?.def == RPWantDefOf.RP_Want_Score && parole.status == WantStatus.Lost;
                if (!replaced) problems0.Add($"hidden set-aside test: the goal was not replaced by the grudge ({mc.want?.def?.defName}, the old one {parole.status})");
                else if (Lines(r, setAside) != 0 || Since().Any(e => e.ids.Any(hidden.Contains)))
                    problems0.Add($"hidden set-aside test: the chronicle names an undiscovered gang's member putting a goal aside: {string.Join(" / ", Since().Where(e => e.ids.Any(hidden.Contains)).Select(e => e.text))}");
                // Control: the gang known, the same replacement is told once (so the check above could have caught the line).
                mc.gangKnown = true;
                mc.want = null;
                mc.pastGrudges.Clear();
                InmateWant again = Wants.Form(mate, mc, RPWantDefOf.RP_Want_Parole);
                if (again == null)
                {
                    problems0.Add("hidden set-aside test: no second goal for the control");
                    return;
                }
                Wants.Notify_Grudge(mc);
                if (mc.want?.def != RPWantDefOf.RP_Want_Score || Lines(r, WantLine("RP_Chr_WantSetAside", mate, again)) != 1)
                    problems0.Add($"hidden set-aside test: with the gang known the old goal's set-aside was told {Lines(r, WantLine("RP_Chr_WantSetAside", mate, again))} times (once expected; goal now {mc.want?.def?.defName})");
            });
        }

        // ---- a served sentence on the floor: released once up, not before -----------------------------------------------------------

        /// <summary>
        /// An inmate whose sentence is served but who is down (a wound, food poisoning) is not released at the day's pass: vanilla's release starts a walk
        /// to the exit, which logs an error for a pawn that cannot path. Up again, the next pass lets them go. The other inmates' sentences are put
        /// out of reach for the check, so the pass releases nobody else.
        /// </summary>
        private static void CheckDownedInmateIsReleasedOnceUp(Map map)
        {
            Guarded("downed release", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                var sentences = new List<(CompInmate c, int days)>();
                foreach (Pawn real in map.mapPawns.PrisonersOfColony)
                    if (real.Inmate() is { } rc)
                    {
                        sentences.Add((rc, rc.sentenceDays));
                        rc.sentenceDays = int.MaxValue;
                    }
                r.Add(() =>
                {
                    foreach ((CompInmate c, int days) in sentences) c.sentenceDays = days;
                });
                Pawn served = Throwaway(map, r);
                CompInmate sc = served.Inmate();
                (sc.admittedTick, sc.sentenceDays) = (0, 0); // (served from the first tick: a negative tick of admission would count as no time at all)
                HealthUtility.DamageUntilDowned(served, allowBleedingWounds: false);
                if (!served.Downed)
                {
                    problems0.Add("downed release test: could not put the inmate on the floor");
                    return;
                }
                // (A release counts when the inmate leaves, by the shuttle: here, that they are released and on their way out.)
                ReleaseSystem.ReleaseServedSentences(g, map);
                bool waited = served.IsPrisonerOfColony && !served.guest.Released && g.shuttle.OutboundOf(served) == null;
                // Up again (the wounds gone): the next pass releases them.
                foreach (Hediff wound in served.health.hediffSet.hediffs.Where(h => h is Hediff_Injury).ToList()) served.health.RemoveHediff(wound);
                bool up = !served.Downed;
                ReleaseSystem.ReleaseServedSentences(g, map);
                bool released = served.guest.Released && g.shuttle.OutboundOf(served) is { kind: OutboundKind.Released };
                RPLog.Message($"[RimPrison:test] downed release: waited while down {waited}, up {up}, released once up {released}");
                if (!waited || !up || !released) problems0.Add($"downed release test: a served sentence while down {(waited ? "waited" : "was released")}, then up again {up} and released {released} (security {sc.security?.defName}, served {sc.DaysServed:F1} of {sc.sentenceDays} days, lifer {sc.IsLifer}, downed {served.Downed}, spawned {served.Spawned}, a prisoner {served.IsPrisonerOfColony}, listed {map.mapPawns.PrisonersOfColonySpawned.Contains(served)}, on the way out {g.shuttle.OutboundOf(served) != null})");
            });
        }
    }
}
