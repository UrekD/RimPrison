using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// Checks and test helpers from stability round 5 (see WORKLOG): the product bugs found while looking at the flaky events checks, each with the check that fails without its fix, and the
    /// helpers that make the checks that were wrong fair (a real class at the bench, a real arrival on the map, a door that was open).
    /// </summary>
    public static partial class AutoTest
    {
        // ---- the transfer escort walks the inmate's way ---------------------------------------------------------------------------------------------------

        /// <summary>Cells of the prison and of the ground round it that a pawn can stand on (a random sample, in a random order).</summary>
        private static List<IntVec3> WalkableSample(Map map, int most, float radius = 48f) =>
            GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, radius, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null).InRandomOrder().Take(most).ToList();

        /// <summary>How many cells of the guard's way from <paramref name="start"/> to the ramp lie further than two cells from every cell of the inmate's way: the two walks round different sides of something.</summary>
        private static int DivergenceOfTheWays(List<IntVec3> guardWay, List<IntVec3> inmateWay) =>
            guardWay.Count(g => inmateWay.All(i => g.DistanceToSquared(i) > 6f));

        /// <summary>
        /// A cell the slow inmate waits at, 30 to 40 steps from the ramp, where the guard's own way to the ramp and the inmate's part furthest (two ways of the same length round a block, found by the pathfinder for
        /// two pawns: in a third of the runs the guard went round one side and the slow inmate round the other, sixteen steps from each other); <see cref="IntVec3.Invalid"/> when no cell shows it (the walk
        /// then proves less, and the check says so).
        /// </summary>
        private static IntVec3 FarCellWhereTheWaysPartFurthest(Map map, Pawn walker, Pawn guard, IntVec3 ramp, out int divergence)
        {
            divergence = 0;
            IntVec3 best = IntVec3.Invalid;
            PrisonMap pm = PrisonMap.Of(map);
            foreach (IntVec3 c in WalkableSample(map, 700).Concat(pm.zones.CellsOf(RPDefOf.RP_Yard).Concat(pm.zones.CellsOf(RPDefOf.RP_Canteen)).InRandomOrder().Take(120)))
            {
                if (!walker.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
                List<IntVec3> inmateWay, guardWay;
                using (PawnPath path = map.pathFinder.FindPathNow(c, ramp, TraverseParms.For(walker, Danger.Deadly)))
                {
                    if (!path.Found || path.NodesReversed.Count is < 30 or > 40) continue;
                    inmateWay = path.NodesReversed.ToList();
                }
                using (PawnPath path = map.pathFinder.FindPathNow(c, ramp, TraverseParms.For(guard, Danger.Deadly)))
                {
                    if (!path.Found) continue;
                    guardWay = path.NodesReversed.ToList();
                }
                int apart = DivergenceOfTheWays(guardWay, inmateWay);
                if (apart > divergence)
                {
                    divergence = apart;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// The guard walking a transfer to the ramp goes by the inmate's own way, a few steps ahead of them (<see cref="JobDriver_EscortTransfer.GoalFor"/>): for an inmate who is walking, the cell
        /// <see cref="JobDriver_EscortTransfer.FallBehind"/> steps on along their path, not the ramp (the guard's own way to the ramp can be the other side of a block); with the ramp near, the ramp; for an
        /// inmate who stands somewhere that is not the ramp, nowhere (the guard stays with them).
        /// </summary>
        private static void CheckEscortWalksTheInmatesWay(Map map)
        {
            Guarded("stability 5: the transfer escort walks the inmate's way", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn inmate = Throwaway(map, r);
                IntVec3 ramp = IntVec3.Invalid;
                IntVec3 start = IntVec3.Invalid;
                // A start and a ramp 20 to 40 steps apart.
                List<IntVec3> sample = WalkableSample(map, 120);
                foreach (IntVec3 a in sample)
                {
                    foreach (IntVec3 b in sample)
                    {
                        int steps = a == b ? 0 : JobDriver_EscortTransfer.StepsOfWalking(map, a, b, inmate);
                        if (steps is >= 20 and <= 40) { start = a; ramp = b; break; }
                    }
                    if (start.IsValid) break;
                }
                if (!start.IsValid)
                {
                    RPLog.Message("[RimPrison:test] stability 5: no two cells 20 to 40 steps apart for the escort's way");
                    return;
                }
                guard.pather.StopDead();
                guard.Position = start;
                guard.Notify_Teleported(true, true);
                inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
                inmate.pather.StopDead();
                inmate.Position = start;
                inmate.Notify_Teleported(true, true);
                // Standing, away from the ramp, with the guard beside them: the guard stays. (Standing for sure: the teleport ends their job, and the next one can set them walking at once, which
                // the guard rightly follows - "does not stay with them" twice in a day's census.)
                inmate.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 600), JobCondition.InterruptForced);
                inmate.pather.StopDead();
                IntVec3 standing = JobDriver_EscortTransfer.GoalFor(inmate, ramp, 0, out bool holdStanding);
                // Standing, and the guard 20 steps off: the guard stays too (it is the inmate that is waited for).
                // Walking: the goal is on their way, not the ramp.
                inmate.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, ramp), JobCondition.InterruptForced);
                ShuttleTicks(() => inmate.pather.Moving && inmate.pather.curPath is { Found: true }, 60);
                PawnPath way = inmate.pather.curPath;
                if (!inmate.pather.Moving || way is not { Found: true })
                {
                    RPLog.Message("[RimPrison:test] stability 5: the check's inmate would not start walking to the ramp (no path), the escort's way is not checked");
                    return;
                }
                List<IntVec3> nodes = way.NodesReversed.ToList();
                IntVec3 goal = JobDriver_EscortTransfer.GoalFor(inmate, ramp, 0, out bool holdWalking);
                int ahead = way.NodesLeftCount - 1 - nodes.IndexOf(goal);
                RPLog.Message($"[RimPrison:test] stability 5: the escort's way: the inmate walks {way.NodesLeftCount} steps more to the ramp {ramp}; the guard's goal is {goal}, {ahead} steps on along their path (hold {holdWalking}); standing, hold {holdStanding} (goal {standing})");
                if (holdWalking || goal == ramp || !nodes.Contains(goal) || ahead < 1 || ahead > JobDriver_EscortTransfer.FallBehind + 2)
                    ReviewFail("escort way", $"for an inmate walking {way.NodesLeftCount} steps more to the ramp the guard's goal is {goal} ({(nodes.Contains(goal) ? ahead + " steps along their path" : "not on their path")}, the ramp {ramp}, hold {holdWalking}): the guard goes its own way to the ramp");
                inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
                inmate.pather.StopDead();
                if (!holdStanding) ReviewFail("escort way", "a guard beside an inmate who stands far from the ramp does not stay with them");
                JobDriver_EscortTransfer.GoalFor(inmate, ramp, JobDriver_EscortTransfer.StillMost, out bool holdLong);
                if (holdLong) ReviewFail("escort way", "a guard stays for ever with an inmate who does not move");
                // Standing at the ramp: the guard goes on to it.
                inmate.Position = ramp;
                inmate.Notify_Teleported(true, true);
                IntVec3 atRamp = JobDriver_EscortTransfer.GoalFor(inmate, ramp, 0, out bool holdAtRamp);
                if (holdAtRamp || atRamp != ramp) ReviewFail("escort way", $"for an inmate standing at the ramp the guard's goal is {atRamp} (hold {holdAtRamp}): the ramp, without holding, expected");
            });
        }

        // ---- a search that changes who owns a bed -----------------------------------------------------------------------------------------------------

        /// <summary>What a test wants done to the inmate each time a guard's cell search reaches them (null: nothing). See <see cref="Patch_ComplaintSystem_Notify_Searched_Test"/>.</summary>
        internal static Action<Pawn> DuringTheSearch;

        /// <summary>
        /// A guard turning a cell over (<see cref="TunnelSystem.SearchCell"/>) walks the beds of the room and each bed's owners. What a search turns up changes who owns a bed (a false complaint with an old
        /// injury sends the owner to a medical bed: "Collection was modified" in the last toil of the search, in an events run), so the walk is over copies.
        /// </summary>
        private static void CheckSearchSurvivesAnOwnerLeavingTheirBed(Map map)
        {
            Guarded("stability 5: a cell search survives an owner leaving their bed", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                // A sleeping spot of the check's own, in a room of the prison (no real inmate's bed is turned over).
                Room room = pm.PrisonerBeds.Where(b => b.Spawned && b.GetRoom() is { PsychologicallyOutdoors: false }).Select(b => b.GetRoom()).FirstOrDefault();
                IntVec3 spot = room?.Cells.FirstOrDefault(c => c.Standable(map) && c.GetFirstPawn(map) == null && c.GetEdifice(map) == null && c.GetThingList(map).All(t => t.def.category != ThingCategory.Item)) ?? IntVec3.Invalid;
                if (!spot.IsValid)
                {
                    RPLog.Message("[RimPrison:test] stability 5: no free cell in a cell of the prison for the search check's sleeping spot");
                    return;
                }
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.SleepingSpot);
                bed.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bed, spot, map);
                r.Add(() => { if (!bed.Destroyed) bed.Destroy(DestroyMode.Vanish); });
                Pawn owner = Throwaway(map, r);
                owner.ownership.ClaimBedIfNonMedical(bed);
                // (A member of staff with no role, so that nothing the search turns up is written up as a complaint.)
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                StaffRoleDef role = guard.GetComp<CompStaff>().role;
                guard.GetComp<CompStaff>().role = null;
                r.Add(() => guard.GetComp<CompStaff>().role = role);
                Room cell = bed.GetRoom();
                if (owner.ownership.OwnedBed != bed || cell == null || !cell.ContainedBeds.Contains(bed))
                {
                    RPLog.Message($"[RimPrison:test] stability 5: the search check's sleeping spot is not in a room's beds (owned {owner.ownership.OwnedBed == bed}, room {cell != null})");
                    return;
                }
                int left = 0;
                DuringTheSearch = who =>
                {
                    if (who != owner || who.ownership.OwnedBed == null) return;
                    left++;
                    who.ownership.UnclaimBed();
                };
                r.Add(() => DuringTheSearch = null);
                try
                {
                    TunnelSystem.SearchCell(guard, cell);
                }
                catch (InvalidOperationException e)
                {
                    ReviewFail("cell search", $"a search of a cell threw when an owner left their bed in the middle of it: {e.Message}");
                }
                if (left == 0) ReviewFail("cell search", "the search never reached the owner of the check's bed (nothing was tested)");
            });
        }

        // ---- the helpers of the fairer checks ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A visitor the prison's own visitation brought (<see cref="VisitationSystem"/>: the lord of the visit, <see cref="VisitationSystem.InmateOf"/>) for a real inmate, not for one of the checks' made-up inmates
        /// (numbers 90000 and up, whose visitors a check made and must clear away). The checks that tick the clock on let the real visits come, and a real visitor is no leak.
        /// </summary>
        private static bool IsRealVisitor(Pawn p) => VisitationSystem.InmateOf(p)?.Inmate() is { } c && c.inmateNumber < 90000;

        /// <summary>The pawns on the map that were not there before a check began, bar the real visitors (<see cref="IsRealVisitor"/>): those a check made and left. Throwaway staff and made-up inmates are among them.</summary>
        private static IEnumerable<Pawn> LeftBehind(Map map, HashSet<Pawn> before) => map.mapPawns.AllPawns.Where(x => !before.Contains(x) && !IsRealVisitor(x));

        /// <summary>
        /// The pawns a check left on the map: those that were not there before it, bar the real arrivals (a shuttle that came in while the check ran real ticks: its inmates were admitted after the check began, and carry
        /// the numbers of the prison's own, not the 91000 and up of the made-up ones, which are counted). A real arrival is no leak of the check, nor is a real visitor (<see cref="IsRealVisitor"/>).
        /// </summary>
        private static List<Pawn> LeftOnTheMap(Map map, HashSet<Pawn> before, int checksBegan)
        {
            PrisonGame g = PrisonGame.Instance;
            return map.mapPawns.AllPawnsSpawned.Where(x => !before.Contains(x) && !(x.Inmate() is { } c && c.inmateNumber < 90000 && c.admittedTick >= checksBegan) && g.shuttle.ArrivalOf(x) == null && !IsRealVisitor(x)).ToList();
        }

        /// <summary>
        /// Takes the real inmates off a tailor bench for the check that is about to use it: a class that sits at it (the attendee's job and reservation), a tailor who sews at it. (The events plan runs real days before:
        /// a real tailoring class at the bench held it, and the tailor of a check was given a walk round the workshop instead of a cycle.) Put back by nothing: a pawn who is sent off the bench goes back to the regime.
        /// </summary>
        private static void ClearTheBenchOfRealInmates(Map map, Thing bench)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                bool atTheBench = p.CurJob != null && (p.CurJob.targetA.Thing == bench || p.CurJob.targetB.Thing == bench);
                if (!atTheBench && !map.reservationManager.ReservedBy(bench, p)) continue;
                p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                map.reservationManager.ReleaseAllClaimedBy(p);
            }
        }

        /// <summary>
        /// A door that is shut and has nobody in it: a locked door that is standing open (somebody used it a moment ago, or stands in it) lets a pawn through as an open door does, whatever its mode says. Closes it when it
        /// can (nobody in the doorway), and says whether it is shut.
        /// </summary>
        private static bool ShutTheDoor(Building_Door door)
        {
            if (door.Open && !door.BlockedOpenMomentary) AccessTools.Method(typeof(Building_Door), "DoorTryClose").Invoke(door, null);
            return !door.Open;
        }

        // ---- a routine frisk waits for an inmate in the middle of something ---------------------------------------------------------------------------------

        /// <summary>
        /// A routine frisk is not offered for an inmate at an appointment (the book cart's round: a frisk ended it in an events run, "ended by JobDriver_Frisk"; a class, a hearing), and a guard
        /// who picked them before gives up on arriving (<see cref="WorkGiver_Frisk.WaitsForAppointment"/>); a frisk that is owed still goes ahead, and work is no appointment (the kitchen and the
        /// workshop are where the tools go missing). The routine roll is decided for the check; the appointment is the job the inmate has, set for the moment of the question.
        /// </summary>
        private static void CheckRoutineFriskWaitsForABusyInmate(Map map)
        {
            Guarded("a routine frisk waits for an inmate at an appointment", map, r =>
            {
                if (PrisonGame.Instance.shakedownUntilTick > Find.TickManager.TicksGame) { RPLog.Message("[RimPrison:test] routine frisk check skipped: a shakedown is on"); return; }
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                (c.suspected, c.awaitingSearch, c.lastSearchedTick) = (false, false, -9999999);
                WorkGiver_Frisk.RoutineRollForTests = true;
                r.Add(() => WorkGiver_Frisk.RoutineRollForTests = null);
                p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 600), JobCondition.InterruptForced);
                if (!WorkGiver_Frisk.Candidate(p)) { ReviewFail("routine frisk", $"the check's own premise: an idle inmate out of their cell is not offered a routine frisk (in their cell {p.GetRoom() == InmateUtility.CellOf(p)})"); return; }
                // (The job the question reads, for the moment of the question: a class or a round of the cart needs a session or a shelf to run.)
                Job real = p.jobs.curJob;
                (bool offered, bool waits) Ask(JobDef def)
                {
                    p.jobs.curJob = JobMaker.MakeJob(def, p.Position);
                    try { return (WorkGiver_Frisk.Candidate(p), WorkGiver_Frisk.WaitsForAppointment(p)); }
                    finally { p.jobs.curJob = real; }
                }
                foreach (JobDef appointment in new[] { RPLibraryDefOf.RP_DeliverBooks, RPProgramDefOf.RP_AttendClass, RPParoleDefOf.RP_AttendHearing })
                {
                    (bool offered, bool waits) = Ask(appointment);
                    if (offered || !waits) ReviewFail("routine frisk", $"a routine frisk is offered ({offered}), or does not wait on the guard's arrival ({waits}), for an inmate on {appointment.defName}");
                }
                foreach (JobDef work in new[] { DefDatabase<JobDef>.GetNamed("RP_InmateCook"), DefDatabase<JobDef>.GetNamed("RP_VisitTalk") })
                {
                    (bool offered, bool waits) = Ask(work);
                    if (!offered || waits) ReviewFail("routine frisk", $"a routine frisk is not offered ({offered}), or waits ({waits}), for an inmate on {work.defName}: work and visits are no appointment");
                }
                c.awaitingSearch = true;
                (bool owedOffered, bool owedWaits) = Ask(RPLibraryDefOf.RP_DeliverBooks);
                if (!owedOffered || owedWaits) ReviewFail("routine frisk", $"a frisk that is owed is not offered ({owedOffered}), or waits ({owedWaits}), for an inmate on the book cart's round");
            });
        }

        // ---- armed response gives up on a target it cannot shoot -------------------------------------------------------------------------------------------

        /// <summary>
        /// An armed guard sent to shoot a rioter they cannot shoot from anywhere (shut in a locked cell, out of sight) gives up, and the work scan does not hand the same job straight back
        /// (<see cref="ArmedResponse.GaveUpOn"/>): "Madam started 10 jobs in one tick. newJob=RP_ArmedResponse" in a showcase run, the cast position search failing at the job's start each time.
        /// </summary>
        private static void CheckArmedResponseGivesUpOnAnUnseenTarget(Map map)
        {
            Guarded("armed response gives up on a target it cannot shoot", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                if (SpecialMeasures.LimitArmed(ArmedResponsePolicy.EscapesAndViolence) != ArmedResponsePolicy.EscapesAndViolence) { RPLog.Message("[RimPrison:test] armed response check skipped: special measures hold fire"); return; }
                int DoorsOf(Building_Bed b) => b.GetRoom().ContainedAndAdjacentThings.OfType<Building_Door>().Distinct().Count();
                bool DoorIsShut(Building_Bed b) => CellDoorOf(pm, b)?.parent is Building_Door d && ShutTheDoor(d);
                // (Two cells, a rioter shut in each: with one remembered target a guard was handed the one, then the other, then the first again.)
                List<Building_Bed> beds = pm.PrisonerBeds.Where(b => b.Spawned && CellDoorOf(pm, b) != null && DoorsOf(b) == 1 && b.GetRoom().Cells.Any(c => c.Standable(map) && c.GetFirstPawn(map) == null))
                    .GroupBy(b => b.GetRoom()).Select(gr => gr.First()).Where(DoorIsShut).Take(2).ToList();
                if (beds.Count == 0) { ReviewFail("armed response", "the check's own premise: no cell with one door that is shut"); return; }
                ArmedResponsePolicy policy0 = g.armedResponse;
                r.Add(() => g.armedResponse = policy0);
                g.armedResponse = ArmedResponsePolicy.EscapesAndViolence;
                var targets = new List<Pawn>();
                foreach (Building_Bed b in beds)
                {
                    CompPrisonDoor d = CellDoorOf(pm, b);
                    PrisonDoorMode m = d.mode;
                    r.Add(() =>
                    {
                        d.mode = m;
                        pm.ForceDoorUpdate();
                    });
                    d.mode = PrisonDoorMode.Locked;
                    Pawn t = Throwaway(map, r);
                    t.pather.StopDead();
                    t.Position = b.GetRoom().Cells.First(c => c.Standable(map) && c.GetFirstPawn(map) == null);
                    t.Notify_Teleported();
                    targets.Add(t);
                }
                pm.ForceDoorUpdate();
                Building_Bed bed = beds[0];
                CompPrisonDoor door = CellDoorOf(pm, bed);
                Room cell = bed.GetRoom();
                Pawn target = targets[0];
                Pawn guard = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_ArmedGuard);
                IntVec3 outside = GenRadial.RadialCellsAround(door.parent.Position, 8f, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && c.GetRoom(map) != cell
                                                                                                               && c.GetDoor(map) == null && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
                if (!outside.IsValid) { ReviewFail("armed response", "the check's own premise: no cell outside the locked cell for the guard"); return; }
                guard.jobs.StopAll();
                guard.pather.StopDead();
                guard.Position = outside;
                guard.Notify_Teleported();
                foreach (Pawn t in targets)
                {
                    t.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, transitionSilently: true);
                    r.Add(() => t.MentalState?.RecoverFromState());
                }
                WorkGiver giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d => d.giverClass == typeof(WorkGiver_ArmedResponse)).Worker;
                var scanner = (WorkGiver_ArmedResponse)giver;
                if (!ArmedResponse.IsArmed(guard) || !ArmedResponse.ShouldShoot(target) || !scanner.HasJobOnThing(guard, target))
                {
                    ReviewFail("armed response", $"the check's own premise: armed {ArmedResponse.IsArmed(guard)}, a target to shoot {ArmedResponse.ShouldShoot(target)} ({target.MentalStateDef?.defName}), offered {scanner.HasJobOnThing(guard, target)}");
                    return;
                }
                // (The scan's lists of candidates are made again for the new rioter, as they would be within the tick in play.)
                InmateCandidates.Shared.Invalidate();
                int loops0 = JobLoopErrors;
                Job shoot = scanner.JobOnThing(guard, target);
                shoot.workGiverDef = giver.def;
                guard.jobs.StartJob(shoot, JobCondition.InterruptForced);
                if (guard.CurJobDef == RPArmedDefOf.RP_ArmedResponse && guard.CurJob.targetA.Thing == target)
                {
                    RPLog.Message($"[RimPrison:test] armed response check: the guard found a spot to shoot from (the cell is seen from outside), skipped; the guard at {guard.Position}, the target at {target.Position}");
                    return;
                }
                if (JobLoopErrors > loops0) ReviewFail("armed response", $"the guard was handed the shot at an unseen target again and again in one tick (now {guard.CurJobDef?.defName}, {targets.Count} targets)");
                if (scanner.HasJobOnThing(guard, target)) ReviewFail("armed response", "the guard who just gave up on a target is offered it again at once");
                // (Giving up on a second target does not forget the first: with one remembered target a guard was handed the one, then the other, then the first again.)
                if (targets.Count > 1)
                {
                    ArmedResponse.Notify_GaveUp(guard, targets[1]);
                    if (!ArmedResponse.GaveUpOn(guard, target) || !ArmedResponse.GaveUpOn(guard, targets[1])) ReviewFail("armed response", $"after giving up on a second target the guard remembers the first {ArmedResponse.GaveUpOn(guard, target)} and the second {ArmedResponse.GaveUpOn(guard, targets[1])}");
                }
                RPLog.Message($"[RimPrison:test] armed response check: {targets.Count} targets, the guard does {guard.CurJobDef?.defName}, job loop errors {JobLoopErrors - loops0}, gave up on the second {(targets.Count > 1 && ArmedResponse.GaveUpOn(guard, targets[1]))}");
            });
        }

        // ---- staff off shift go home, not to sleep on the floor -------------------------------------------------------------------------------------------

        /// <summary>
        /// A member of staff whose shift is over, kept past the hour's commute by a job that could not be cut short (a visitor's search, a class), is given the walk home by vanilla's rest when
        /// the job ends, not a sleep on the floor until the next hour's pass ("staff asleep on the floor": a guard still on RP_SearchVisitor at 22:00) (<see cref="StaffCommute.InsteadOfTheFloor"/>).
        /// Asked twice, fit and with the flu (medical rest): the second fails without stability round 21's change (the rule left one who needs medical rest on the floor).
        /// </summary>
        private static void CheckOffShiftStaffGoHomeNotToTheFloor(Map map)
        {
            Guarded("staff off shift go home, not to sleep on the floor", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                // (Whole for the first question: a generated guard can come with a wound or an illness that wants medical rest - "medical rest True" in a census run; the second asks with the flu.)
                guard.health.Reset();
                // (Another on site and on duty: the rule leaves the last one on site where they are, and a second throwaway hired at night went home by the same rule before the question.)
                Pawn onDuty = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                for (int h = 0; h < 24; h++) onDuty.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                if (onDuty.needs?.rest != null) onDuty.needs.rest.CurLevel = 1f;
                if (onDuty.CurJobDef == RPCommuteDefOf.RP_GoHome) onDuty.jobs.EndCurrentJob(JobCondition.InterruptForced);
                int hour = GenLocalDate.HourOfDay(map);
                for (int h = 0; h < 24; h++) guard.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                guard.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                guard.needs.rest.CurLevel = 0.2f;
                // (A bed for them would be theirs to sleep in, rightly: the map has no free colonist bed for the check to mean anything.)
                if (RestUtility.FindBedFor(guard) != null) { RPLog.Message("[RimPrison:test] floor sleep check skipped: a free colonist bed on the map"); return; }
                if (!StaffCommute.ShouldBeHome(guard, guard.GetComp<CompStaff>(), hour) || guard.ownership?.OwnedBed != null)
                {
                    ReviewFail("floor sleep", $"the check's own premise: the guard should be home ({StaffCommute.ShouldBeHome(guard, guard.GetComp<CompStaff>(), hour)}) and has no bed ({guard.ownership?.OwnedBed == null})");
                    return;
                }
                Job rest = new JobGiver_GetRest().TryIssueJobPackage(guard, default).Job;
                // (What the rule reads, for the line: the map, a lord, what they carry, the others on site and what they do.)
                string Why()
                {
                    var others = map.mapPawns.FreeColonistsSpawned.Where(o => o != guard && o.GetComp<CompStaff>() is { role: not null } osc && osc.leaveAtTick < 0 && !osc.role.warden && !HostageCrisis.IsHeld(o)).ToList();
                    return $"home map {map.IsPlayerHome}, lord {guard.GetLord()?.LordJob?.GetType().Name ?? "none"}, carrying {guard.carryTracker?.CarriedThing?.LabelShort ?? "nothing"}, burning {guard.IsBurning()}, drafted {guard.Drafted}, "
                           + $"mental {guard.MentalStateDef?.defName ?? "none"}, medical rest {HealthAIUtility.ShouldSeekMedicalRest(guard)}, others on site [{string.Join(", ", others.Select(o => $"{o.LabelShort}:{o.CurJobDef?.defName}"))}], a way out {StaffCommute.GoHomeJob(guard) != null}";
                }
                if (rest?.def == JobDefOf.LayDown && rest.targetA.Thing is not Building_Bed)
                    ReviewFail("floor sleep", $"a guard whose shift is over is given a sleep on the floor at {rest.targetA.Cell}, not the walk home ({Why()})");
                else if (rest?.def != RPCommuteDefOf.RP_GoHome)
                    ReviewFail("floor sleep", $"a guard whose shift is over is given {rest?.def?.defName ?? "nothing"} for rest (the walk home expected)");
                // The same guard ill (stability round 21: a counsellor kept past 22:00 by an escort, with medical rest, slept on the floor until the next hour's pass walked them off it): with no bed of the staff's
                // the floor is no sick bed - nobody tends a humanlike off a bed - so the walk home again.
                Hediff flu = HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Flu"), guard);
                flu.Severity = 0.2f;
                guard.health.AddHediff(flu);
                if (guard.Downed || !HealthAIUtility.ShouldSeekMedicalRest(guard))
                {
                    ReviewFail("floor sleep", $"the check's own premise: the guard with the flu needs medical rest ({HealthAIUtility.ShouldSeekMedicalRest(guard)}) and is not downed ({!guard.Downed})");
                    return;
                }
                if (RestUtility.FindBedFor(guard) is { } sickBed) { RPLog.Message($"[RimPrison:test] floor sleep check (ill): a bed for the ill guard at {sickBed.Position} ({sickBed.def.defName}, medical {sickBed.Medical}), not asked"); return; }
                Job sickRest = new JobGiver_GetRest().TryIssueJobPackage(guard, default).Job;
                if (sickRest?.def == JobDefOf.LayDown && sickRest.targetA.Thing is not Building_Bed)
                    ReviewFail("floor sleep", $"a guard whose shift is over and who needs medical rest is given a sleep on the floor at {sickRest.targetA.Cell}, not the walk home ({Why()})");
                else if (sickRest?.def != RPCommuteDefOf.RP_GoHome)
                    ReviewFail("floor sleep", $"a guard whose shift is over and who needs medical rest is given {sickRest?.def?.defName ?? "nothing"} for rest (the walk home expected)");
            });
        }

        /// <summary>
        /// A guard who is feeding a downed inmate by hand when their shift ends (the hour's commute pass) stays on the feeding, and goes home when it is done: FeedPatient was the one care job a pass could cut short
        /// (<c>Patches/CareJobs.xml</c> made the meal's delivery and the carry to a bed non-casual, not the spoon), so a guard in the middle of it was sent home with the inmate unfed ("Rhae downed at d2 h23 ... unfed until
        /// h03" in a census run; the guard the line named was only the first on the list, but the cut is real). The pass runs with the prison's own staff set aside, over a throwaway guard on the job, a second one on duty
        /// (the commute never empties the prison) and a downed inmate: the guard must be on the feeding after it, and, the feeding over, must be given the walk home by the next pass. Fails without the patch
        /// (the guard is given the walk home mid-feeding).
        /// </summary>
        private static void CheckFeedingGuardFinishesBeforeGoingHome(Map map)
        {
            Guarded("staff: a guard feeding a downed inmate when the shift ends finishes before the walk home", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RosterKeepTimetables(map, r);
                g.handoverHours = 0;
                RosterRealStaffAside(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.health.Reset();
                Pawn onDuty = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                onDuty.health.Reset();
                for (int h = 0; h < 24; h++)
                {
                    guard.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                    onDuty.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                }
                if (onDuty.needs?.rest != null) onDuty.needs.rest.CurLevel = 1f;
                onDuty.jobs.StopAll();
                int hour = GenLocalDate.HourOfDay(map);
                guard.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                Roster.Invalidate();
                // (The driver's own fail condition is "nobody is to feed this one" - vanilla's FeedPatientUtility.ShouldBeFed / WardenFeedUtility.ShouldBeFed both want the patient in a bed - and it is read when the job
                // starts: a downed inmate lying where they fell ended the job inside StartJob, and the guard's think tree gave them a patrol or the carry to a bed. So the inmate lies in a bed they own, with the guard
                // beside it, and the pass runs in the same tick: nothing is ticked between the job's start and the commute.)
                Building_Bed bed = PrisonMap.Of(map).PrisonerBeds.FirstOrDefault(b => b.Spawned && !b.Medical && !b.AnyOccupants && b.OwnersForReading.Count == 0 && b.GetRoom() is { } room && !room.PsychologicallyOutdoors
                    && GenAdj.CellsAdjacent8Way(new TargetInfo(b)).Any(c => c.InBounds(map) && c.Standable(map) && c.GetDoor(map) == null && c.GetRoom(map) == room));
                if (bed == null) { RPLog.Message("[RimPrison:test] feeding at the end of a shift skipped: no free prisoner bed with room beside it on the live map"); return; }
                IntVec3 beside = GenAdj.CellsAdjacent8Way(new TargetInfo(bed)).First(c => c.InBounds(map) && c.Standable(map) && c.GetDoor(map) == null && c.GetRoom(map) == bed.GetRoom());
                Pawn patient = Throwaway(map, r);
                HealthUtility.DamageUntilDowned(patient, allowBleedingWounds: false);
                patient.jobs.StopAll();
                patient.pather.StopDead();
                patient.Position = bed.GetSleepingSlotPos(0);
                patient.Notify_Teleported();
                patient.ownership.ClaimBedIfNonMedical(bed);
                r.Add(() => patient.ownership?.UnclaimBed());
                patient.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture), JobCondition.InterruptForced);
                patient.jobs.posture = PawnPosture.LayingInBed;
                Thing meal = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
                guard.inventory.innerContainer.TryAdd(meal);
                r.Add(() => { if (!meal.Destroyed) meal.Destroy(); });
                guard.jobs.StopAll();
                guard.pather.StopDead();
                guard.Position = beside;
                guard.Notify_Teleported();
                Job feed = JobMaker.MakeJob(JobDefOf.FeedPatient, meal, patient);
                feed.count = 1;
                guard.jobs.StartJob(feed, JobCondition.InterruptForced);
                CompStaff sc = guard.GetComp<CompStaff>();
                if (guard.CurJobDef != JobDefOf.FeedPatient || !patient.Downed || !StaffCommute.ShouldBeHome(guard, sc, hour) || StaffCommute.GoHomeJob(guard) == null)
                {
                    RPLog.Message($"[RimPrison:test] feeding at the end of a shift skipped, the scene could not be had: the guard is on {guard.CurJobDef?.defName ?? "no job"} (FeedPatient wanted), the inmate downed {patient.Downed}, in a bed {patient.InBed()} ({patient.CurrentBed()?.Position}), "
                        + $"to be fed {FoodUtility.ShouldBeFedBySomeone(patient)}, the guard should be home {StaffCommute.ShouldBeHome(guard, sc, hour)}, a way out {StaffCommute.GoHomeJob(guard) != null}");
                    return;
                }
                StaffCommute.SendOffShiftHome(map, g, hour);
                if (guard.CurJobDef != JobDefOf.FeedPatient)
                {
                    ReviewFail("feeding at the end of a shift", $"a guard in the middle of feeding a downed inmate, whose shift ended, is on {guard.CurJobDef?.defName ?? "no job"} after the hour's commute pass, not the feeding (FeedPatient casually interruptible {JobDefOf.FeedPatient.casualInterruptible})");
                    return;
                }
                // The feeding done (no new job started in its place, so that the next pass is the one that decides): the same pass now gives them the walk home.
                guard.jobs.EndCurrentJob(JobCondition.Succeeded, startNewJob: false);
                if (guard.carryTracker?.CarriedThing != null) guard.carryTracker.TryDropCarriedThing(guard.Position, ThingPlaceMode.Near, out _);
                StaffCommute.SendOffShiftHome(map, g, hour);
                if (guard.CurJobDef != RPCommuteDefOf.RP_GoHome)
                    ReviewFail("feeding at the end of a shift", $"a guard whose feeding is done and whose shift is over was not sent home by the next pass (job {guard.CurJobDef?.defName ?? "none"}, on site {guard.Spawned})");
            });
        }

        // ---- staff do not run wild -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>No member of staff can have vanilla's "run wild" break (the warden could not already): a guard who did kept the role and came back from home as a wild man.</summary>
        private static void CheckStaffDoNotRunWild(Map map)
        {
            Guarded("staff do not run wild", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                // (The patch's verdict on a break vanilla would allow: vanilla's own test also asks for a warm enough season, which the plan's map may not have.)
                bool Allowed(MentalBreakDef def)
                {
                    bool can = true;
                    Patch_MentalBreakWorker_BreakCanOccur_Warden.Postfix(def.Worker, guard, ref can);
                    return can;
                }
                MentalBreakDef sad = DefDatabase<MentalBreakDef>.GetNamed("Wander_Sad");
                if (Allowed(RPVanillaDefOf.RunWild)) ReviewFail("run wild", $"a guard may have the run-wild break ({guard.LabelShort})");
                if (!Allowed(sad)) ReviewFail("run wild", "a guard may not have a sad wander either (only the run into the wild is barred)");
            });
        }

        // ---- a kick out of the wrong bed ------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Vanilla's kick out of a bed the pawn does not lie in (the cleanup of a failed "take to bed" whose takee already lay in another bed) logs an error and clears their lying-in-bed posture
        /// while they still lie there; it is skipped (<see cref="Patch_RestUtility_KickOutOfBed_OtherBed"/>). The kick out of the bed they do lie in still works.
        /// </summary>
        private static void CheckKickOutOfTheWrongBed(Map map)
        {
            Guarded("a kick out of a bed the pawn is not in", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn p = Throwaway(map, r);
                // (Free, and free to reserve: a bed someone is on their way to cannot be lain in.)
                List<Building_Bed> beds = pm.PrisonerBeds.Where(b => b.Spawned && !b.AnyOccupants && b.OwnersForReading.Count == 0 && p.CanReserve(b)).Take(2).ToList();
                if (beds.Count < 2) { RPLog.Message("[RimPrison:test] kick check skipped: no two free prisoner beds"); return; }
                p.pather.StopDead();
                p.Position = beds[0].Position;
                p.Notify_Teleported();
                p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, beds[0]), JobCondition.InterruptForced);
                if (p.CurrentBed() != beds[0]) { ReviewFail("kick out of bed", $"the check's own premise: the inmate does not lie in the bed (on {p.CurJobDef?.defName}, in {p.CurrentBed()?.Position})"); return; }
                int errors0 = ErrorCount;
                RestUtility.KickOutOfBed(p, beds[1]);
                if (ErrorCount > errors0 || !p.GetPosture().InBed() || p.CurrentBed() != beds[0])
                    ReviewFail("kick out of bed", $"a kick out of a bed the inmate does not lie in logged {ErrorCount - errors0} errors, and they lie in a bed {p.GetPosture().InBed()} (the first {p.CurrentBed() == beds[0]})");
                RestUtility.KickOutOfBed(p, beds[0]);
                if (p.GetPosture().InBed()) ReviewFail("kick out of bed", "a kick out of the bed the inmate lies in did nothing");
            });
        }

        // ---- the colonist bar keeps clear of the prison panel ---------------------------------------------------------------------------------------------

        /// <summary>The colonist bar, centred, never reaches the prison panel at the top left, however many staff there are (<see cref="Patch_ColonistBarDrawLocsFinder_ClearOfPanel"/>).</summary>
        private static void CheckColonistBarClearOfPanel(Map map)
        {
            Guarded("the colonist bar keeps clear of the prison panel", map, r =>
            {
                // (The bar's own layout (headless the screen is 0 wide): on the narrowest screen the game allows (1024) the prison's own staff fill the width the bar may take, as forty-odd do on a wide one. Adding staff
                // for it was tried: forty hires, rolled back, and the library and burial plot checks after it failed in that run (not chased).)
                (int w0, int h0) = (UI.screenWidth, UI.screenHeight);
                try
                {
                    (UI.screenWidth, UI.screenHeight) = (1024, 768);
                    Find.ColonistBar.MarkColonistsDirty();
                    int entries = Find.ColonistBar.Entries.Count;
                    List<Vector2> locs = Find.ColonistBar.DrawLocs;
                    float leftEdge = locs.Count > 0 ? locs.Min(v => v.x) : float.MaxValue, panelEdge = 8f + PrisonHUD.Width;
                    if (entries < 6 || locs.Count != entries) ReviewFail("colonist bar", $"the check's own premise: the bar has {entries} entries and {locs.Count} places (6 or more expected)");
                    else if (leftEdge < panelEdge) ReviewFail("colonist bar", $"with {entries} on the colonist bar its first portrait is at x {leftEdge:F0}, on the prison panel (to x {panelEdge:F0}; screen 1024)");
                    RPLog.Message($"[RimPrison:test] colonist bar: {entries} entries, the first at x {leftEdge:F0} (the panel to {panelEdge:F0}, screen 1024)");
                }
                finally
                {
                    (UI.screenWidth, UI.screenHeight) = (w0, h0);
                    Find.ColonistBar.MarkColonistsDirty();
                }
            });
        }

        // ---- vanilla's "Need colonist beds" in a prison ------------------------------------------------------------------------------------------------------

        /// <summary>The staff go home after their shift: vanilla's "Need colonist beds" stays quiet however few beds there are for them (<see cref="Patch_Alert_NeedColonistBeds_GetReport"/>).</summary>
        private static void CheckNoColonistBedsAlert(Map map)
        {
            Guarded("no \"need colonist beds\" alert in a prison", map, r =>
            {
                Alert_NeedColonistBeds.AvailableColonistBeds(map, includeBabies: false, out int single, out int dbl, out _);
                // (The premise: fewer beds than staff, as in every prison of the mod; a guard more makes sure of it.)
                for (int i = 0; i < 2 && single >= 0 && dbl >= 0; i++)
                {
                    ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                    Alert_NeedColonistBeds.AvailableColonistBeds(map, includeBabies: false, out single, out dbl, out _);
                }
                if (single >= 0 && dbl >= 0) { ReviewFail("colonist beds", $"the check's own premise: the staff have beds enough ({single} single, {dbl} double left)"); return; }
                if (new Alert_NeedColonistBeds().GetReport().active) ReviewFail("colonist beds", $"vanilla's \"Need colonist beds\" is up in a prison whose staff go home ({single} single, {dbl} double)");
            });
        }

        // ---- an insulting spree whose target fell asleep ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard on an insulting spree whose target is being carried (or is asleep) is not handed the insult (<see cref="Patch_JobGiver_InsultingSpree_CanInsult"/>). The spree's job giver only asks whether
        /// the target can be reached, and reachability goes by where a carried pawn is held; the insult job fails at its first toil on a target that is not spawned, and the giver handed it out again, ten
        /// times in the tick ("started 10 jobs in one tick ... JobGiver_InsultingSpree" in a showcase run), until the mental state picked someone else on its own, slower clock.
        /// </summary>
        private static void CheckInsultingSpreeLeavesASleeperAlone(Map map)
        {
            Guarded("an insulting spree leaves a carried or sleeping inmate alone", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn carrier = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn carried = Throwaway(map, r), sleeper = Throwaway(map, r);
                if (!guard.mindState.mentalStateHandler.TryStartMentalState(DefDatabase<MentalStateDef>.GetNamed("InsultingSpree"), forced: true, transitionSilently: true)
                    || guard.MentalState is not MentalState_InsultingSpree spree)
                {
                    ReviewFail("insult spree", "the check's own premise: the guard could not be put on an insulting spree");
                    return;
                }
                bool Offered(Pawn target)
                {
                    spree.target = target;
                    spree.lastInsultTicks = -999999;
                    return new JobGiver_InsultingSpree().TryIssueJobPackage(guard, default).Job is { } given && given.def == JobDefOf.Insult;
                }
                // ---- awake and on their feet: the insult is given (the patch drops only what would fail) ----
                if (!Offered(sleeper)) ReviewFail("insult spree", $"the spree's job giver hands {guard.LabelShort} no insult of {sleeper.LabelShort}, who is awake and in reach");
                // ---- carried: the job would fail at once, and be given again in the same tick ----
                carrier.jobs.StopAll();
                if (carrier.carryTracker.TryStartCarry(carried, 1, reserve: false) == 0 || carried.Spawned || !guard.CanReach(carried, PathEndMode.Touch, Danger.Deadly)
                    || !SocialInteractionUtility.BestInteractableCell(guard, carried).IsValid)
                {
                    ReviewFail("insult spree", $"the check's own premise: the inmate is not carried (spawned {carried.Spawned}) or not in the guard's reach or interaction range as vanilla reckons it");
                    return;
                }
                if (Offered(carried)) ReviewFail("insult spree", $"the spree's job giver hands {guard.LabelShort} an insult of {carried.LabelShort}, who is being carried");
                int loops0 = JobLoopErrors;
                guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
                if (JobLoopErrors > loops0) ReviewFail("insult spree", $"{guard.LabelShort} was handed the insult of a carried inmate again and again in one tick (now {guard.CurJobDef?.defName})");
                RPLog.Message($"[RimPrison:test] insulting spree on a carried inmate: the guard does {guard.CurJobDef?.defName}, job loop errors {JobLoopErrors - loops0}");
                carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out _);
                // ---- asleep: the insult at the end of the job would be refused ----
                sleeper.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, sleeper.Position), JobCondition.InterruptForced);
                sleeper.jobs.curDriver.asleep = true;
                if (sleeper.Awake()) ReviewFail("insult spree", "the check's own premise: the sleeper is awake");
                else if (Offered(sleeper)) ReviewFail("insult spree", $"the spree's job giver hands {guard.LabelShort} an insult of {sleeper.LabelShort}, who is asleep");
                guard.MentalState?.RecoverFromState();
            });
        }
    }

    /// <summary>Autotest: lets a check do something to the inmate each time a cell search reaches them (see <see cref="AutoTest.DuringTheSearch"/>).</summary>
    [HarmonyPatch(typeof(ComplaintSystem), nameof(ComplaintSystem.Notify_Searched))]
    [TestOnly]
    public static class Patch_ComplaintSystem_Notify_Searched_Test
    {
        public static void Prefix(Pawn inmate) => AutoTest.DuringTheSearch?.Invoke(inmate);
    }
}
