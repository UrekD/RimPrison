using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the performance pass (perf.md): each fix that changed where an answer comes from (a candidate list, a cache, a count) is checked against the
    /// plain way of getting the same answer, on the live prison and on throwaway inmates. Each is its own method called once from FireAllEvents.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckPerfPass(Map map)
        {
            CheckCandidateListsCoverTheGivers(map);
            CheckFreeBedCountIsQuiet(map);
            CheckWorkPassFlipIsLocal(map);
            CheckRegimeInterruptsAreSpread(map);
            CheckCachedAnswersMatchTheWalk(map);
            CheckMealIndexFollowsTheMeals(map);
        }

        /// <summary>The guard work givers scan short lists of inmates: no inmate a giver would act on may be missing from its list, and a list follows what happens to an inmate.</summary>
        private static void CheckCandidateListsCoverTheGivers(Map map)
        {
            Guarded("candidate lists", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                InmateCandidates lists = InmateCandidates.Shared;
                Pawn guard = pm.Guards.FirstOrDefault(g => g.Spawned && !g.Downed);
                if (guard == null)
                {
                    problems0.Add("candidate lists: no guard on the map to ask the work givers as");
                    return;
                }
                Pawn punished = Throwaway(map, r);
                CompInmate pc = punished.Inmate();
                int hours0 = pc.pendingPunishmentHours;
                r.Add(() =>
                {
                    pc.pendingPunishmentHours = hours0;
                    lists.Invalidate();
                });
                lists.Invalidate();
                bool listedBefore = lists.Subdue(map).Contains(punished);
                pc.pendingPunishmentHours = 2;
                lists.Invalidate();
                bool listedWith = lists.Subdue(map).Contains(punished);
                pc.pendingPunishmentHours = 0;
                lists.Invalidate();
                bool listedAfter = lists.Subdue(map).Contains(punished);
                if (listedBefore || !listedWith || listedAfter) problems0.Add($"candidate lists: an inmate owing punishment was in the list to subdue {listedBefore} before, {listedWith} while owing (true expected), {listedAfter} after");
                var givers = new (WorkGiver_Scanner giver, System.Func<InmateCandidates, List<Thing>> list, string name)[]
                {
                    (new WorkGiver_RespondToTrouble(), l => l.Subdue(map), "respond"), (new WorkGiver_TakeToPunishment(), l => l.PunishmentCarry(map), "punishment"),
                    (new WorkGiver_RescueInmate(), l => l.Rescue(map), "rescue"), (new WorkGiver_CarryOutExecution(), l => l.Execution(map), "execution"),
                    (new WorkGiver_Therapy(), l => l.Therapy(map), "therapy"), (new WorkGiver_Frisk(), l => l.Frisk(map), "frisk"), (new WorkGiver_ArmedResponse(), l => l.Armed(map), "armed"),
                };
                lists.Invalidate();
                foreach (Pawn inmate in map.mapPawns.PrisonersOfColonySpawned)
                    foreach ((WorkGiver_Scanner giver, System.Func<InmateCandidates, List<Thing>> list, string name) in givers)
                        if (giver.HasJobOnThing(guard, inmate, forced: true) && !list(lists).Contains(inmate))
                            problems0.Add($"candidate lists: the {name} giver has a job on {inmate.LabelShort} but its list does not hold them");
                // (An empty list lets the scan be skipped, not forced menus.)
                lists.Invalidate();
                var punish = new WorkGiver_TakeToPunishment();
                bool skipsEmpty = lists.PunishmentCarry(map).Count > 0 || punish.ShouldSkip(guard);
                if (!skipsEmpty) problems0.Add("candidate lists: the carry-to-punishment giver does not skip a scan with nobody to carry");
            });
        }

        /// <summary>The screens count free beds without shuffling (a screen must not move the game's random numbers), and the count is the list's.</summary>
        private static void CheckFreeBedCountIsQuiet(Map map)
        {
            Guarded("free bed count", map, r =>
            {
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
                {
                    List<Building_Bed> list = IntakeSystem.FreePrisonerBeds(map, sec);
                    int all = IntakeSystem.FreeBedCount(map, sec), wing = IntakeSystem.FreeBedCount(map, sec, inWingOnly: true);
                    int wingInList = list.Count(b => IntakeSystem.WingOf(b) == sec);
                    if (all != list.Count || wing != wingInList) problems0.Add($"free bed count: {sec.defName} counts {all}/{wing} (list {list.Count}/{wingInList})");
                }
                Pawn who = Throwaway(map, r);
                SecurityLevelDef level = DefDatabase<SecurityLevelDef>.AllDefsListForReading.First();
                Rand.PushState(7);
                InmateUtility.HasRoomIn(who, level);
                IntakeSystem.FreeBedCount(map);
                IntakeSystem.FreeBedCount(map, level, inWingOnly: true);
                float after = Rand.Value;
                Rand.PopState();
                Rand.PushState(7);
                float fresh = Rand.Value;
                Rand.PopState();
                if (after != fresh) problems0.Add("free bed count: asking whether there is room (what a screen does) moved the game's random numbers");
            });
        }

        /// <summary>A work pass flipping clears that inmate's cached reachability only: it does not ask for a door pass, and does not clear the map's cache.</summary>
        private static void CheckWorkPassFlipIsLocal(Map map)
        {
            Guarded("work pass flip", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                pm.ForceDoorUpdate();
                if (pm.DoorsDirty) problems0.Add("work pass flip: the door pass is still due after a forced one");
                Pawn p = Throwaway(map, r);
                int clears = pm.reachabilityClears;
                pm.Notify_WorkPassChanged(p);
                if (pm.DoorsDirty || pm.reachabilityClears != clears) problems0.Add($"work pass flip: asked for a door pass ({pm.DoorsDirty}) or cleared the map's reachability ({pm.reachabilityClears - clears} times)");
            });
        }

        /// <summary>A regime change sends inmates to a new job a dozen a tick, and only those still on the job they had.</summary>
        private static void CheckRegimeInterruptsAreSpread(Map map)
        {
            Guarded("regime interrupts", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                int n = PrisonMap.InterruptsPerTick * 3 + 5;
                var inmates = new List<Pawn>();
                for (int i = 0; i < n; i++)
                {
                    Pawn p = Throwaway(map, r);
                    p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_Wander), JobCondition.InterruptForced);
                    inmates.Add(p);
                }
                // (Whatever the prison's own pass has queued is drained first.)
                for (int i = 0; i < 200 && pm.PendingInterrupts > 0; i++) pm.DrainInterrupts();
                foreach (Pawn p in inmates) pm.QueueRegimeInterrupt(p);
                foreach (Pawn p in inmates) pm.QueueRegimeInterrupt(p);
                if (pm.PendingInterrupts != n) problems0.Add($"regime interrupts: {pm.PendingInterrupts} queued for {n} inmates queued twice (once each expected)");
                // One of them moved on by themselves: not interrupted again.
                Pawn moved = inmates[0];
                Job own = JobMaker.MakeJob(JobDefOf.Wait_Wander);
                moved.jobs.StartJob(own, JobCondition.InterruptForced);
                pm.DrainInterrupts();
                int left = n - PrisonMap.InterruptsPerTick;
                if (pm.PendingInterrupts != left) problems0.Add($"regime interrupts: {pm.PendingInterrupts} left after one tick's worth of {PrisonMap.InterruptsPerTick} (expected {left})");
                for (int i = 0; i < 10 && pm.PendingInterrupts > 0; i++) pm.DrainInterrupts();
                if (pm.PendingInterrupts != 0) problems0.Add($"regime interrupts: {pm.PendingInterrupts} still queued after {n / PrisonMap.InterruptsPerTick + 10} ticks of draining");
                if (moved.CurJob != own) problems0.Add("regime interrupts: an inmate who had moved on to another job was interrupted again");
            });
        }

        /// <summary>Answers now taken from a list, a cache or a count are the ones the plain walk gives.</summary>
        private static void CheckCachedAnswersMatchTheWalk(Map map)
        {
            Guarded("cached answers", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                // Watched: the first guard, tower or camera that settles it is the same answer as the whole count being over nothing.
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (TensionUtility.Watched(p, pm) != TensionUtility.Watchers(p, pm) > 0f)
                        problems0.Add($"cached answers: {p.LabelShort} is watched={TensionUtility.Watched(p, pm)} but the count is {TensionUtility.Watchers(p, pm)}");
                // The mentor pair: one pass against every mentor against every inmate.
                IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
                bool walk = inmates.Any(m => Mentors.CanMentor(m) && inmates.Any(x => Mentors.Fits(m, x)));
                if (Mentors.AnyPair(map) != walk) problems0.Add($"cached answers: a mentor pair is {Mentors.AnyPair(map)} by the pass and {walk} by the walk");
                // Stoves by def against every building.
                int byWalk = 0;
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    if (InmateJobWorker_Kitchen.IsStove(b) && pm.zones.ZoneAt(b.Position) == RPLaborDefOf.RP_Kitchen && b.Spawned && !b.IsBrokenDown() && !InmateJobWorker_Kitchen.Burning(b)
                        && (b.TryGetComp<CompRefuelable>() is not { } fuel || fuel.HasFuel) && (b.TryGetComp<CompPowerTrader>() is not { } power || power.PowerOn)) byWalk++;
                int byDef = new InmateJobWorker_Kitchen().Capacity(map);
                if (byWalk != byDef) problems0.Add($"cached answers: {byDef} usable kitchen stoves by def, {byWalk} by walking the buildings");
                // What the prison draws: power traders against every building.
                float drawWalk = 0f;
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    if (b.GetComp<CompPowerTrader>() is { PowerOn: true } trader && trader.Props.PowerConsumption > 0f) drawWalk += trader.Props.PowerConsumption;
                if (!UnityEngine.Mathf.Approximately(drawWalk, UtilitySystem.Draw(map))) problems0.Add($"cached answers: the draw is {UtilitySystem.Draw(map)} by the power traders, {drawWalk} by the buildings");
                // Each cell door's verdict from the cached beds against the live room: a door pass must give the same answers.
                PrisonGame game = PrisonGame.Instance;
                int hour = GenLocalDate.HourOfDay(map);
                int now = Find.TickManager.TicksGame;
                foreach (CompPrisonDoor d in pm.Doors.Where(x => x.parent.Spawned && x.cellRoom != null && x.mode == PrisonDoorMode.Auto))
                {
                    int lockup0 = d.lockupSince;
                    bool cached = pm.CellDoorOpen(d, game, now, hour);
                    d.lockupSince = lockup0;
                    pm.ForceDoorUpdate();
                    bool afterRefresh = pm.CellDoorOpen(d, game, now, hour);
                    d.lockupSince = lockup0;
                    if (cached != afterRefresh) problems0.Add($"cached answers: the cell door at {d.parent.Position} is {cached} on the beds kept by the last pass and {afterRefresh} on fresh ones");
                }
            });
        }

        /// <summary>The canteen's meals are indexed until a food thing is added or taken off the map: a meal put down is found at once, and one taken away is gone.</summary>
        private static void CheckMealIndexFollowsTheMeals(Map map)
        {
            Guarded("meal index", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                IntVec3 cell = pm.zones.CellsOf(RPDefOf.RP_Canteen).FirstOrDefault(c => c.Standable(map) && c.GetFirstItem(map) == null);
                if (!cell.IsValid)
                {
                    problems0.Add("meal index: no free cell in the canteen to put a meal on");
                    return;
                }
                Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                r.Add(() =>
                {
                    if (!meal.Destroyed) meal.Destroy();
                });
                GenSpawn.Spawn(meal, cell, map);
                bool listed = RegimeWorker_Eat.MealsIn(map, pm, RPDefOf.RP_Canteen).Contains(meal);
                meal.Destroy();
                bool gone = !RegimeWorker_Eat.MealsIn(map, pm, RPDefOf.RP_Canteen).Contains(meal);
                if (!listed || !gone) problems0.Add($"meal index: a meal put on the canteen floor was listed {listed} (true expected), and after it was destroyed gone {gone} (true expected)");
            });
        }
    }
}
