using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Released, paroled and transferred inmates on their way out (see <see cref="ShuttleSystem.outbound"/>): they board the shuttle, or wait in reception for it.</summary>
    public class ThinkNode_ConditionalOutbound : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn) => PrisonGame.Instance != null && pawn.Spawned && !pawn.Downed && !pawn.IsFreeColonist && ShuttleSystem.Sys?.OutboundOf(pawn) != null;
    }

    /// <summary>
    /// What a released, paroled or transferred inmate does until the shuttle takes them: board it when it is down; otherwise see to hunger, tiredness and the weather as any
    /// prisoner does (vanilla's own givers: the released would never reach them, the node above them walks them off the map); otherwise wait in holding if the prison
    /// has one (indoors, under a roof), else in reception by the pad.
    /// </summary>
    public class JobGiver_Outbound : ThinkNode_JobGiver
    {
        private static readonly JobGiver_GetFood getFood = new JobGiver_GetFood();
        private static readonly JobGiver_GetRest getRest = new JobGiver_GetRest();
        private static readonly JobGiver_SeekSafeTemperature seekSafeTemperature = new JobGiver_SeekSafeTemperature();

        protected override Job TryGiveJob(Pawn pawn)
        {
            ShuttleSystem s = ShuttleSystem.Sys;
            Map map = pawn.Map;
            if (ShuttleSystem.Boardable(map) is { } shuttle && pawn.CanReach(shuttle.InteractionCell, PathEndMode.OnCell, Danger.Deadly))
            {
                // A transfer is walked to the ramp by a guard (they wait for one; with none for half an hour they go on their own).
                if (ShuttleSystem.TransferWalk(pawn, s.OutboundOf(pawn), shuttle) is { } escorted) return escorted;
                Job board = JobMaker.MakeJob(RPShuttleDefOf.RP_BoardShuttle, shuttle);
                board.locomotionUrgency = LocomotionUrgency.Walk;
                return board;
            }
            if (NeedJob(pawn) is { } need) return need;
            PrisonMap pm = PrisonMap.Of(map);
            // Holding is where they wait until the shuttle is down (it is inside, roofed, and the new inmates and the staff are there).
            if (pm != null && pm.zones.Any(RPDefOf.RP_Holding))
            {
                if (pm.zones.ZoneAt(pawn.Position) == RPDefOf.RP_Holding) return Wait();
                IntVec3 cell = RegimeActivityWorker.RandomReachableZoneCell(pawn, pm, RPDefOf.RP_Holding);
                if (cell.IsValid) return GoTo(cell);
            }
            IntVec3 anchor = s.ReceptionAnchor(map);
            if (!anchor.IsValid)
            {
                // No place for a shuttle anywhere near: they walk out as vanilla would have had them.
                if (!RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exit)) return null;
                Job leave = JobMaker.MakeJob(JobDefOf.Goto, exit);
                leave.exitMapOnArrival = true;
                return leave;
            }
            // They stand by the pad, off the hull's spot and the way down (a wandering crowd would hold the shuttle up).
            if (!pawn.Position.InHorDistOf(anchor, ShuttleSystem.ReceptionRadius) || ShuttleSystem.KeepClear(anchor).Contains(pawn.Position))
            {
                IntVec3 spot = s.ReceptionSpot(pawn, anchor);
                if (spot.IsValid && spot != pawn.Position) return GoTo(spot);
            }
            return Wait();
        }

        /// <summary>Hungry, exhausted or in a bad temperature: what any prisoner would do about it (null when nothing is pressing or nothing can be done).</summary>
        private static Job NeedJob(Pawn pawn)
        {
            if (pawn.needs?.food is { CurCategory: >= HungerCategory.Hungry } && getFood.TryIssueJobPackage(pawn, default).Job is { } eat) return eat;
            if (pawn.needs?.rest is { CurLevel: < 0.12f } && getRest.TryIssueJobPackage(pawn, default).Job is { } sleep) return sleep;
            return seekSafeTemperature.TryIssueJobPackage(pawn, default).Job;
        }

        private static Job Wait()
        {
            Job wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 600;
            wait.checkOverrideOnExpire = true;
            return wait;
        }

        private static Job GoTo(IntVec3 spot)
        {
            if (!spot.IsValid) return null;
            Job go = JobMaker.MakeJob(JobDefOf.Goto, spot);
            go.locomotionUrgency = LocomotionUrgency.Walk;
            return go;
        }
    }

    /// <summary>Walk to the ramp and go aboard.</summary>
    public class JobDriver_BoardShuttle : JobDriver
    {
        private Building_StateShuttle Shuttle => (Building_StateShuttle)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            AddFailCondition(() => !ShuttleSystem.BoardingOpen(Shuttle));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            yield return Toils_General.Do(() => ShuttleSystem.Board(pawn, Shuttle));
        }
    }

    /// <summary>Guards meet the new arrivals at the ramp and walk them to holding, one guard to an inmate.</summary>
    public class WorkGiver_EscortArrival : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || ShuttleSystem.Sys is not { arrivals.Count: > 0 };

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (ArrivalEntry e in ShuttleSystem.Sys.arrivals)
                if (e.pawn is { Spawned: true } p && p.Map == pawn.Map) yield return p;
        }

        // (A new inmate in a mental break is not led by the arm: the guard would walk to holding alone, wait, and come back for them, as long as the break lasts.)
        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && ShuttleSystem.Sys.ArrivalOf(inmate) is { escortId: < 0 } && PrisonMap.Of(pawn.Map) is { } pm && pm.zones.Any(RPDefOf.RP_Holding)
            && pm.zones.ZoneAt(inmate.Position) != RPDefOf.RP_Holding && !inmate.Downed && !inmate.InMentalState && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)
            // (The arrival can walk to holding: behind a locked gate the guard would meet them at the ramp, lead them to the gate, fail, and come back for them, for as long as it stays shut.)
            && RegimeActivityWorker.RandomReachableZoneCell(inmate, pm, RPDefOf.RP_Holding).IsValid;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IntVec3 cell = RegimeActivityWorker.RandomReachableZoneCell((Pawn)t, PrisonMap.Of(pawn.Map), RPDefOf.RP_Holding);
            if (!cell.IsValid || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return null;
            Job job = JobMaker.MakeJob(RPShuttleDefOf.RP_EscortArrival, t, cell);
            job.count = 1;
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }
    }

    /// <summary>The guard collects the arrival at the ramp, walks them into holding, and is done once they are there.</summary>
    public class JobDriver_EscortArrival : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        private bool InHolding => PrisonMap.Of(Inmate.Map)?.zones.ZoneAt(Inmate.Position) == RPDefOf.RP_Holding;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            AddFinishAction(_ => Notify_Ended());
            Toil meet = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            meet.FailOn(() => !ShuttleSystem.IsArrival(Inmate));
            yield return meet;
            yield return Toils_General.Do(() => ShuttleSystem.Notify_Escorted(Inmate, pawn));
            Toil lead = Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            lead.FailOn(() => !ShuttleSystem.IsArrival(Inmate));
            yield return lead;
            Toil wait = Toils_General.Wait(400);
            wait.AddEndCondition(() => InHolding ? JobCondition.Succeeded : JobCondition.Ongoing);
            yield return wait;
        }

        private void Notify_Ended()
        {
            if (Inmate != null) ShuttleSystem.Notify_EscortEnded(Inmate, pawn);
        }
    }

    /// <summary>Guards walk a transferred inmate to the shuttle (one guard to an inmate): the way out of a troublemaker is not a stroll.</summary>
    public class WorkGiver_EscortTransfer : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !GuardUtility.IsGuard(pawn) || ShuttleSystem.Sys is not { outbound.Count: > 0 } || ShuttleSystem.Boardable(pawn.Map) == null;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (OutboundEntry e in ShuttleSystem.Sys.outbound)
                if (e.kind == OutboundKind.Transferred && e.escortId < 0 && e.pawn is { Spawned: true } p && p.Map == pawn.Map) yield return p;
        }

        // (A transfer in a mental break is not led by the arm: the guard would walk to the ramp alone and come back for them, as long as the break lasts.)
        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && ShuttleSystem.Sys.OutboundOf(inmate) is { kind: OutboundKind.Transferred, escortId: < 0 } && ShuttleSystem.Boardable(pawn.Map) is { } shuttle
            && !inmate.Downed && !inmate.InMentalState && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)
            && inmate.CanReach(shuttle.InteractionCell, PathEndMode.OnCell, Danger.Deadly) && pawn.CanReach(shuttle.InteractionCell, PathEndMode.Touch, Danger.Deadly);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (ShuttleSystem.Boardable(pawn.Map) is not { } shuttle) return null;
            Job job = JobMaker.MakeJob(RPShuttleDefOf.RP_EscortTransfer, t, shuttle.InteractionCell);
            job.count = 1;
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }
    }

    /// <summary>The guard collects the transfer where they wait, walks them to the ramp and stays until they are aboard.</summary>
    public class JobDriver_EscortTransfer : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        private bool Escortable => ShuttleSystem.Sys?.OutboundOf(Inmate) is { kind: OutboundKind.Transferred } && ShuttleSystem.Boardable(pawn.Map) != null;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(_ => Notify_Ended());
            Toil meet = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch).FailOnDespawnedOrNull(TargetIndex.A);
            meet.FailOn(() => !Escortable);
            yield return meet;
            yield return Toils_General.Do(() => ShuttleSystem.Notify_TransferEscorted(Inmate, pawn));
            Toil lead = LeadAtTheirPace().FailOnDespawnedOrNull(TargetIndex.A);
            lead.FailOn(() => !Escortable);
            yield return lead;
            // At the ramp: they board beside the guard, and the walk is done once they are aboard (gone from the map).
            Toil wait = Toils_General.Wait(600);
            wait.AddEndCondition(() => Inmate is not { Spawned: true } ? JobCondition.Succeeded : JobCondition.Ongoing);
            yield return wait;
        }

        /// <summary>
        /// The guard keeps this many steps ahead of the inmate on their way, no more (a lame inmate, or a guard with a bad leg, was walked to the ramp by a guard who was twenty cells ahead). (<see cref="CatchUp"/>, the
        /// number of steps within which a guard who had stopped went on, was the stop-and-go of the old rule and is not read now; kept, reported.)
        /// </summary>
        public const float FallBehind = 6f, CatchUp = 3f;

        /// <summary>Ticks between two looks at where the inmate is going and how far apart the two are.</summary>
        private const int LookEvery = 10;

        /// <summary>How long (ticks) the guard stays with an inmate who stands still away from the ramp (they have not started, or wait at a door) before it goes on to the ramp without them.</summary>
        public const int StillMost = 1500;

        /// <summary>
        /// Where the guard walks to now: the cell <see cref="FallBehind"/> steps ahead of the inmate on the inmate's own way, while the inmate is walking and has further to go than that; the ramp when they have not;
        /// and, while the inmate is not walking and is not at the ramp (they have not started, or wait at a door), the guard stays with them (<paramref name="hold"/>) for <see cref="StillMost"/> ticks
        /// (<paramref name="stillTicks"/> is how long they have stood) and then goes on to the ramp. The guard follows the way the inmate walks, and does not find their own to the ramp: two ways round a block
        /// of the same length are not the same walk (the guard went round the one side and the slow inmate round the other, sixteen steps from each other, in a third of the runs; and a guard judging by
        /// the straight line to the ramp, or by the steps left to it, called that "ahead of them" or "not ahead" as the walls fell). The goal is the cell; how the guard walks to it is <see cref="Judge"/>'s.
        /// </summary>
        internal static IntVec3 GoalFor(Pawn inmate, IntVec3 ramp, int stillTicks, out bool hold)
        {
            hold = false;
            PawnPath way = inmate.pather.Moving ? inmate.pather.curPath : null;
            if (way is { Found: true } && way.NodesLeftCount > (int)FallBehind + 1) return way.Peek((int)FallBehind);
            if (way is { Found: true } || inmate.Position.InHorDistOf(ramp, 4f)) return ramp;
            hold = stillTicks < StillMost;
            return ramp;
        }

        /// <summary>The guard's own way to the goal is this many steps longer than the way along the inmate's (to them, and on with them to the goal) before it counts as going round.</summary>
        public const int GoesRoundBy = 3;

        /// <summary>
        /// Where the guard walks to now (<see cref="GoalFor"/> with the guard's way judged, see <see cref="Judge"/>): a cell, the ramp, or the inmate themselves (followed to Touch).
        /// </summary>
        internal static LocalTargetInfo WalkTarget(Pawn guard, Pawn inmate, IntVec3 ramp, int stillTicks, out bool hold)
        {
            IntVec3 goal = GoalFor(inmate, ramp, stillTicks, out hold);
            return hold ? new LocalTargetInfo(goal) : Judge(guard, inmate, ramp, goal);
        }

        /// <summary>
        /// The goal on the inmate's way as the guard can walk to it. A cell the guard cannot get to (a door shut to staff): the ramp, by the guard's own way. A cell the guard's own way reaches only by going round
        /// (<see cref="WayGoesRound"/>): the inmate, followed. The pathfinder weighs a cell's cost against the walker's pace, so a quick guard's cheapest way to a cell six steps on can go round what the slow
        /// inmate walks through (doors, rough ground): the guard handed that cell walked away from them, 22 steps apart by the inmate's way, with the goal right at every look (census c84, slow transfer). Followed,
        /// they walk the inmate's own cells until the guard's way to the goal is along theirs again.
        /// </summary>
        internal static LocalTargetInfo Judge(Pawn guard, Pawn inmate, IntVec3 ramp, IntVec3 goal)
        {
            if (goal == ramp) return goal;
            if (!guard.CanReach(goal, PathEndMode.OnCell, Danger.Deadly)) return ramp;
            return WayGoesRound(guard, inmate, goal, out _, out _) ? new LocalTargetInfo(inmate) : new LocalTargetInfo(goal);
        }

        /// <summary>The guard's own way to the goal (<paramref name="own"/> steps) is longer by more than <see cref="GoesRoundBy"/> than walking to the inmate (<paramref name="toThem"/>) and on along their way to it.</summary>
        internal static bool WayGoesRound(Pawn guard, Pawn inmate, IntVec3 goal, out int own, out int toThem)
        {
            Map map = guard.Map;
            own = StepsOfWalking(map, guard.Position, goal, guard);
            // (To touch them, and the step onto their cell: they can stand where nobody may stop, a door, a sandbag.)
            toThem = 0;
            if (!guard.Position.AdjacentTo8WayOrInside(inmate.Position))
            {
                using PawnPath path = map.pathFinder.FindPathNow(guard.Position, inmate, TraverseParms.For(guard, Danger.Deadly), null, PathEndMode.Touch);
                toThem = path.Found ? path.NodesReversed.Count : -1;
            }
            else if (guard.Position != inmate.Position) toThem = 1;
            return own >= 0 && toThem >= 0 && own > toThem + (int)FallBehind + 1 + GoesRoundBy;
        }

        internal static int StepsOfWalking(Map map, IntVec3 from, IntVec3 to, Pawn who)
        {
            if (from == to) return 0;
            using PawnPath path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(who, Danger.Deadly));
            return path.Found ? path.NodesReversed.Count - 1 : -1;
        }

        /// <summary>The walk to the ramp, at the pace of the inmate who is walked, and by their way (see <see cref="GoalFor"/>).</summary>
        private Toil LeadAtTheirPace()
        {
            int restarts = 0, sinceLook = 0, still = 0;
            bool holding = false, following = false;
            IntVec3 going = IntVec3.Invalid, judged = IntVec3.Invalid;
            LocalTargetInfo judgedTarget = LocalTargetInfo.Invalid;
            Toil lead = ToilMaker.MakeToil("LeadTransfer");
            lead.defaultCompleteMode = ToilCompleteMode.Never;
            lead.initAction = () =>
            {
                holding = following = false;
                going = judged = IntVec3.Invalid;
                sinceLook = LookEvery;
                still = 0;
            };
            lead.tickAction = () =>
            {
                if (Inmate is not { Spawned: true } inmate)
                {
                    ReadyForNextToil();
                    return;
                }
                if (++sinceLook >= LookEvery)
                {
                    sinceLook = 0;
                    still = inmate.pather.Moving ? 0 : still + LookEvery;
                    IntVec3 goal = GoalFor(inmate, TargetB.Cell, still, out holding);
                    // (Judged once for each goal: the inmate takes a step every few looks. A cell the guard cannot get to goes to the ramp, one the guard's own way reaches only by going round to following them.)
                    if (!holding && goal != judged)
                    {
                        judged = goal;
                        judgedTarget = Judge(pawn, inmate, TargetB.Cell, goal);
                    }
                    if (holding)
                    {
                        if (pawn.pather.Moving) pawn.pather.StopDead();
                        going = IntVec3.Invalid;
                        following = false;
                    }
                    else if (judgedTarget.HasThing)
                    {
                        if (!following || !pawn.pather.Moving) pawn.pather.StartPath(inmate, PathEndMode.Touch);
                        following = true;
                        going = IntVec3.Invalid;
                    }
                    else if (judgedTarget.Cell != going || following)
                    {
                        following = false;
                        going = judgedTarget.Cell;
                        if (going == TargetB.Cell) pawn.pather.StartPath(TargetB, PathEndMode.Touch);
                        else pawn.pather.StartPath(going, PathEndMode.OnCell);
                    }
                }
                if (holding || going != TargetB.Cell || pawn.pather.Moving) return;
                // Not walking, and the walk was to the ramp: at it, or a walk that was cut short (a door, a blocked path).
                if (ReachabilityImmediate.CanReachImmediate(pawn.Position, TargetB, pawn.Map, PathEndMode.Touch, pawn)) ReadyForNextToil();
                else if (restarts++ < 3) pawn.pather.StartPath(TargetB, PathEndMode.Touch);
                else EndJobWith(JobCondition.Incompletable);
            };
            return lead;
        }

        private void Notify_Ended()
        {
            if (Inmate != null) ShuttleSystem.Notify_TransferEscortEnded(Inmate, pawn);
        }
    }

    /// <summary>Guards with nothing more urgent stand watch on the pad while the shuttle is down (as many as Deployment asks for): nobody slips aboard, and the crates are searched.</summary>
    public class WorkGiver_PadDuty : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RPShuttleDefOf.RP_StateShuttle);

        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !GuardUtility.IsGuard(pawn) || ShuttleSystem.Sys is not { padGuards: > 0, visit.phase: ShuttlePhase.Landed };

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Building_StateShuttle shuttle && ShuttleSystem.VisitOf(shuttle) is { phase: ShuttlePhase.Landed } v && (v.departTick - Find.TickManager.TicksGame > 400 || ShuttleSystem.Staying(v, shuttle))
            && ShuttleSystem.PadPostFree(pawn, shuttle) && ShuttleSystem.PostCell(pawn, shuttle).IsValid;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IntVec3 post = ShuttleSystem.PostCell(pawn, (Building_StateShuttle)t);
            if (!post.IsValid) return null;
            Job job = JobMaker.MakeJob(RPShuttleDefOf.RP_PadDuty, t, post);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }
    }

    public class JobDriver_PadDuty : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            AddEndCondition(() => ShuttleSystem.VisitOf((Building_StateShuttle)job.targetA.Thing) is { phase: ShuttlePhase.Landed } ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            Toil watch = Toils_General.Wait(GenDate.TicksPerHour * 3);
            watch.handlingFacing = true;
            watch.tickIntervalAction = delta =>
            {
                if (pawn.IsHashIntervalTick(60, delta)) pawn.rotationTracker.FaceCell(job.targetA.Thing.Position);
            };
            yield return watch;
        }
    }

    /// <summary>
    /// A release that did not go through <see cref="ReleaseSystem.Release"/> (the warden's own Release order, vanilla's) still goes by the shuttle: vanilla
    /// has just sent them to the map edge, and the outbound pass takes them over. It is not a release the prison counts: the order can free a lifer or someone on
    /// death row, and counting it would raise the reform grade for nothing.
    /// </summary>
    [HarmonyPatch(typeof(GenGuest), nameof(GenGuest.PrisonerRelease))]
    public static class Patch_GenGuest_PrisonerRelease_Shuttle
    {
        public static void Postfix(Pawn p)
        {
            if (PrisonGame.Instance == null || ShuttleSystem.RegisteringOwnRelease || p == null || p.Dead || !p.Spawned || p.Map is not { IsPlayerHome: true } || p.Inmate() == null) return;
            // One of the colony's own who was held here is a free colonist again, and stays.
            if (p.IsFreeColonist) return;
            // The warden's own order, not a release the prison served out: they leave by the shuttle, and are not counted as released (no reform score for free).
            PrisonGame.Instance.shuttle.Register(p, OutboundKind.Released, counted: false);
        }
    }
}
