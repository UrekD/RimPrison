using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// A guard walks an inmate who may not walk alone (maximum, supermax, the hole, death row) out of a wing that burns, to the place planned for them, and back to their own bed after the all-clear. It is the supermax escort's walk
    /// (<see cref="EscortWalk"/>, <see cref="EscortKind.Evacuation"/>): the guard has them by the arm, the doors on the route open to them and no other, and it ends the moment the guard stops holding them. One guard, one inmate. The job's
    /// target B is the place (the cell near the bed on the way back), its count 0 out and 1 back. After a load every toil makes the walk again (<see cref="EnsureLeg"/>): the walk is not saved, the job is.
    /// </summary>
    public class JobDriver_EvacuateInmate : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        private EscortPhase Phase => job.count == 1 ? EscortPhase.Back : EscortPhase.Out;

        /// <summary>The longest the guard stays with them at the place (or at the bed) before the walk is given up, in ticks.</summary>
        public const int StayMaxTicks = GenDate.TicksPerHour;

        private bool loaded;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) loaded = true;
        }

        private EscortLeg Leg => Inmate?.Inmate()?.leg;

        /// <summary>Where the walk goes: the job's target B (the place out, or the cell near the bed back, chosen once when the job was made: <see cref="EscortWalk.HomeCell"/> picks a random cell and is not asked every tick).</summary>
        private IntVec3 Goal => job.targetB.IsValid ? job.targetB.Cell : Phase == EscortPhase.Back ? EscortWalk.HomeCell(Inmate) : IntVec3.Invalid;

        /// <summary>When the guard got to the place and began to wait for the inmate there (not saved: after a load, the first tick of the wait).</summary>
        private int stayFrom = -1;

        private EscortLeg Hold() => EscortWalk.Hold(Inmate, pawn, EscortKind.Evacuation, Goal, Phase);

        /// <summary>For the saveload check: walks made again after a load (a count; the walk itself is never saved).</summary>
        internal static int rebuiltAfterLoad;

        private bool EnsureLeg()
        {
            if (Leg != null) return true;
            if (!loaded) return false;
            loaded = false;
            if (Hold() == null) return false;
            rebuiltAfterLoad++;
            // (As on a fresh start (the Do toil): whatever the inmate was doing (a wait, read from the save) is over, and they follow the walk at once; otherwise the guard walks off and the leg is lost.)
            if (Inmate.CurJob is { } current && current.def != JobDefOf.Ingest) Inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            return true;
        }

        private bool Wanted => EvacuationSystem.EntryOf(Inmate) is { Moving: true } && !Inmate.Downed && !Inmate.InAggroMentalState;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !Wanted);
            AddFinishAction(condition =>
            {
                if (AutoTest.Active) RPLog.Message($"[RimPrison:evac] {pawn.LabelShort}'s walk with {Inmate?.LabelShort} ended: {condition} ({Phase}, the inmate at {(Inmate is { Spawned: true } im ? im.Position.ToString() : "?")}, the goal {job.targetB.Cell})");
                EscortWalk.Release(Inmate, pawn);
                EvacuationSystem.Notify_GuardDone(Inmate, pawn, condition);
            });
            Toil meet = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            meet.AddPreInitAction(() => job.locomotionUrgency = LocomotionUrgency.Jog);
            meet.FailOn(() => Inmate.Inmate().leg != null && Leg.guard != pawn);
            yield return meet;
            yield return Toils_General.Do(() =>
            {
                EvacuationSystem.Claim(Inmate, pawn);
                Hold();
                IntVec3 goal = Goal;
                if (Leg == null || !goal.IsValid || !Inmate.CanReach(goal, PathEndMode.OnCell, Danger.Deadly))
                {
                    EvacuationSystem.Notify_NoWay(Inmate);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (Inmate.CurJob is { } current && current.def != JobDefOf.Ingest) Inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            });
            Toil lead = Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            lead.AddPreInitAction(() => job.locomotionUrgency = LocomotionUrgency.Walk);
            lead.tickIntervalAction = delta => Hold();
            lead.FailOn(() => !EnsureLeg() || Leg.failed);
            yield return lead;
            Toil stay = Toils_General.Wait(StayMaxTicks);
            stay.AddPreInitAction(() => stayFrom = Find.TickManager.TicksGame);
            stay.tickIntervalAction = delta =>
            {
                EscortLeg leg = Hold();
                if (leg == null) return;
                int now = Find.TickManager.TicksGame;
                if (stayFrom < 0) stayFrom = now;
                bool there = Phase == EscortPhase.Back ? EscortWalk.InOwnCell(Inmate) || Inmate.InBed() : leg.arrivedTick >= 0;
                // (Nobody came: counted from when the guard got there, not from the start of the walk - a long walk on a big map is no no-show, and the way back never sets the leg's arrival.)
                if (!there && now - stayFrom > SupermaxSystem.NoShowTicks) leg.failed = true;
                // (A walk that did not get there ends as one that did not (the guards back off, and the log says so): round 2's log called such a walk "Succeeded".)
                if (leg.failed && !there) EndJobWith(JobCondition.Incompletable);
                else if (there || leg.failed) ReadyForNextToil();
            };
            stay.FailOn(() => !EnsureLeg());
            yield return stay;
        }
    }

    /// <summary>A guard or a doctor carries a downed inmate to the place planned for them (the evacuation's own carry: the existing rescue would put them in a bed of the wing that burns) and lays them down there.</summary>
    public class JobDriver_CarryEvacuee : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOn(() => EvacuationSystem.EntryOf(Inmate) is not { Moving: true });
            AddFinishAction(condition =>
            {
                if (AutoTest.Active) RPLog.Message($"[RimPrison:evac] {pawn.LabelShort}'s carry of {Inmate?.LabelShort} ended: {condition} (at {pawn.Position}, the goal {job.targetB.Cell})");
                // (Short of the place: put down where the guard stands, or a step away from a fire there.)
                if (pawn.carryTracker?.CarriedThing != null) pawn.carryTracker.TryDropCarriedThing(pawn.Spawned ? EvacuationSystem.SafeDropCell(pawn) : pawn.PositionHeld, ThingPlaceMode.Near, out Thing _);
                EvacuationSystem.Notify_GuardDone(Inmate, pawn, condition);
            });
            Toil goTo = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.A);
            goTo.AddPreInitAction(() => job.locomotionUrgency = LocomotionUrgency.Jog);
            Toil carryOn = Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            yield return Toils_Jump.JumpIf(carryOn, () => pawn.IsCarryingPawn(Inmate));
            yield return goTo;
            Toil pickUp = Toils_Haul.StartCarryThing(TargetIndex.A);
            pickUp.AddPreInitAction(() => EvacuationSystem.Claim(Inmate, pawn));
            yield return pickUp;
            carryOn.FailOn(() => !pawn.IsCarryingPawn(Inmate));
            // (Planned again on the way (the way ahead burns): the place is target B, changed by the plan, and the guard turns for it with the inmate still in their arms.)
            carryOn.AddPreTickIntervalAction(delta =>
            {
                if (pawn.pather.Moving && pawn.pather.Destination.Cell != job.targetB.Cell) pawn.pather.StartPath(job.targetB, PathEndMode.OnCell);
            });
            yield return carryOn;
            Toil lay = ToilMaker.MakeToil("LayDownEvacuee");
            lay.initAction = () =>
            {
                // (Got to the old place as the plan changed it: on to the new one, never put down across the map.)
                if (!pawn.Position.InHorDistOf(job.targetB.Cell, 1.5f))
                {
                    JumpToToil(carryOn);
                    return;
                }
                // (On the cell planned for them, where the guard stands. Vanilla's Near puts a downed pawn on no cell a standing pawn is on - the guard's own - and lays them on the first free cell beside it, which at the end of a
                // patch is out of the place: not arrived, carried back to the same cell and laid off it again (stability round 22: eight carries of one inmate between (89, 109) and (90, 109)). When the cell cannot take
                // them, the nearest cell of the place.)
                IntVec3 goal = job.targetB.Cell;
                if (pawn.carryTracker.CarriedThing is not { } carried || goal.Standable(pawn.Map) && pawn.carryTracker.TryDropCarriedThing(goal, ThingPlaceMode.Direct, out Thing _)) return;
                PrisonMap pm = PrisonMap.Of(pawn.Map);
                pawn.carryTracker.innerContainer.TryDrop(carried, goal, pawn.Map, ThingPlaceMode.Near, out Thing _, null, c => pm == null || EvacuationSystem.InPlace(pm, goal, c));
            };
            yield return lay;
        }
    }

    /// <summary>
    /// Emergency warden work of an evacuation (below the riot response and the carry to solitary, above the rescue and all routine care): a guard takes the inmate who waits for one - walks them out with the supermax escort's
    /// pass, or carries them when they are down (back to the bed, after the all-clear, with the rescue's own carry). The doctors' version carries the downed only, as they do the rescue. Nothing is offered when no evacuation runs.
    /// </summary>
    public class WorkGiver_EvacuateInmate : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        private bool DoctorsGiver => def.workType == WorkTypeDefOf.Doctor;

        private static bool IsDoctor(Pawn p) => p.GetComp<CompStaff>()?.role is { } role && role.workTypes.Contains(WorkTypeDefOf.Doctor) && !HostageCrisis.IsHeld(p);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !(DoctorsGiver ? IsDoctor(pawn) : GuardUtility.IsGuard(pawn)) || !EvacuationSystem.Running(pawn.Map) || InmateCandidates.Shared.Evacuate(pawn.Map).Count == 0;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Evacuate(pawn.Map);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Pawn inmate || !EvacuationSystem.NeedsGuard(inmate)) return false;
            if (DoctorsGiver && !inmate.Downed) return false;
            EvacEntry e = EvacuationSystem.EntryOf(inmate);
            if (!pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)) return false;
            if (inmate.Downed && PrisonMap.Of(pawn.Map).evac.phase == EvacPhase.Back) return BedOf(inmate, pawn) != null;
            return pawn.CanReach(e.dest, PathEndMode.OnCell, Danger.Deadly);
        }

        private static Building_Bed BedOf(Pawn inmate, Pawn carrier) =>
            inmate.ownership?.OwnedBed is { Spawned: true } bed && carrier.CanReserveAndReach(bed, PathEndMode.OnCell, Danger.Deadly) ? bed : null;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var inmate = (Pawn)t;
            EvacEntry e = EvacuationSystem.EntryOf(inmate);
            if (e == null) return null;
            bool back = PrisonMap.Of(pawn.Map).evac.phase == EvacPhase.Back;
            if (inmate.Downed)
            {
                if (back)
                {
                    Building_Bed bed = BedOf(inmate, pawn);
                    if (bed == null) return null;
                    Job toBed = JobMaker.MakeJob(JobDefOf.TakeWoundedPrisonerToBed, inmate, bed);
                    toBed.count = 1;
                    return toBed;
                }
                // (One pawn is carried: the carry toil reads the count, as vanilla's carry jobs set it.)
                Job carry = JobMaker.MakeJob(RPEvacDefOf.RP_CarryEvacuee, inmate, e.dest);
                carry.count = 1;
                return carry;
            }
            IntVec3 goal = back ? EscortWalk.HomeCell(inmate) : e.dest;
            if (!goal.IsValid) return null;
            Job walk = JobMaker.MakeJob(RPEvacDefOf.RP_EvacuateInmate, inmate, goal);
            walk.count = back ? 1 : 0;
            walk.locomotionUrgency = LocomotionUrgency.Walk;
            return walk;
        }
    }

    /// <summary>
    /// Vanilla's warden carry to a bed (a downed inmate to the bed vanilla finds, their own) and its escort of an inmate who cannot reach their bed by themselves back to it (<c>WorkGiver_Warden_TakeToBed</c>) make no job for an inmate on
    /// the roll call of a wing being emptied (<see cref="EvacuationSystem.BeingEmptied"/>): both would take them back into the wing - the downed one laid at the point to their bed in it, the supermax inmate in the cage (who cannot
    /// reach their bed, the cell being shut to them) to their cell. Taking them off the giver's candidate list (<see cref="VanillaGiverLists.TakeToBedCandidate"/>) was not enough: the work scan looks for a warden giver's pawns
    /// region by region round the guard first (its thing request is every pawn) and reads the list only when that finds none, so a guard standing at the point still found them (round 2's runs: both ended in their own beds in the
    /// wing). The prison's rescue takes a downed one at the point to a medical bed outside the wing (<see cref="EvacuationSystem.BedAllowed"/>).
    /// </summary>
    /// <summary>An evacuee who breaks into a riot, an assault or an escape is off the roll call at once (<see cref="EvacuationSystem.Notify_Broke"/>): the pass for the doors of their route does not stay on until the next beat.</summary>
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class Patch_MentalStateHandler_TryStartMentalState_Evacuee
    {
        private static readonly AccessTools.FieldRef<MentalStateHandler, Pawn> PawnOf = AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");

        public static void Postfix(MentalStateHandler __instance, bool __result)
        {
            if (!__result || PrisonGame.Instance == null || PawnOf(__instance) is not { } p || !EvacuationSystem.Running(p.MapHeld) || p.Inmate() is not { activeFlag: true }) return;
            EvacuationSystem.Notify_Broke(p);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Warden_TakeToBed), nameof(WorkGiver_Warden_TakeToBed.TryMakeJob))]
    public static class Patch_WorkGiver_Warden_TakeToBed_Evacuees
    {
        public static bool Prefix(Thing t, ref Job __result)
        {
            if (PrisonGame.Instance == null || t is not Pawn p || !EvacuationSystem.BeingEmptied(p)) return true;
            __result = null;
            return false;
        }
    }
}
