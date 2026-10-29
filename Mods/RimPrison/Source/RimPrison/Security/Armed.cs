using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    public enum ArmedResponsePolicy : byte
    {
        /// <summary>Armed guards never open fire on their own.</summary>
        HoldFire,
        /// <summary>Shoot inmates trying to escape.</summary>
        Escapes,
        /// <summary>Shoot escapees, rioters and anyone attacking staff.</summary>
        EscapesAndViolence,
    }

    public static class ArmedResponse
    {
        /// <summary>The policy in force: the warden's, unless special measures hold armed guards from firing on rioters.</summary>
        public static ArmedResponsePolicy Effective(PrisonGame game) => SpecialMeasures.LimitArmed(game.armedResponse);

        /// <summary>The project that brings armed guards (the policy has nothing to act on before it), or null when it is done.</summary>
        public static BureaucracyDef LockedBy => Unlocks.GateOf(RPStaffRoleDefOf.RP_ArmedGuard) is { } gate && !Unlocks.IsUnlocked(RPStaffRoleDefOf.RP_ArmedGuard) ? gate : null;

        public static bool IsArmed(Pawn p) => p.equipment?.Primary is { } w && w.def.IsRangedWeapon;

        public static bool ShouldShoot(Pawn target)
        {
            if (target == null || !target.Spawned || target.Downed || !target.IsPrisonerOfColony) return false;
            // Nobody shoots the hostage-takers during the stand-off (one who strays out would be shot, and killing them all ends it at no cost).
            if (HostageCrisis.Holding(target)) return false;
            ArmedResponsePolicy policy = Effective(PrisonGame.Instance);
            if (policy == ArmedResponsePolicy.HoldFire) return false;
            MentalState ms = target.MentalState;
            // (An escape nobody has seen is not one the guards shoot at: see CountSystem.)
            if (ms is MentalState_Escaping) return !CountSystem.IsHidden(target);
            return policy == ArmedResponsePolicy.EscapesAndViolence && ms != null && ms.def.IsAggro;
        }

        /// <summary>
        /// The targets each guard gave up on (no spot to shoot from: behind walls or a locked door, out of sight from anywhere in range), by guard and target, until when. The work scan asked only
        /// whether a target should be shot and was within 45 cells, so it handed the same job back the moment it ended: "started 10 jobs in one tick ... RP_ArmedResponse". (By guard and target,
        /// not the last target alone: two rioters out of sight, and the scan would hand the guard the one, then the other, then the first again.)
        /// </summary>
        private static readonly Dictionary<(int guard, int target), int> gaveUp = new Dictionary<(int, int), int>();
        private static readonly List<(int, int)> expired = new List<(int, int)>();
        public const int GaveUpTicks = 300;

        public static void Notify_GaveUp(Pawn guard, Pawn target)
        {
            int now = Find.TickManager.TicksGame;
            if (gaveUp.Count > 200)
            {
                expired.Clear();
                foreach (var kv in gaveUp)
                    if (kv.Value - now <= 0 || kv.Value - now > GaveUpTicks) expired.Add(kv.Key);
                foreach ((int, int) key in expired) gaveUp.Remove(key);
            }
            gaveUp[(guard.thingIDNumber, target.thingIDNumber)] = now + GaveUpTicks;
        }

        /// <summary>Did this guard give up on this target a moment ago? (An entry further off than the wait is from another game: not read.)</summary>
        public static bool GaveUpOn(Pawn guard, Pawn target)
        {
            if (!gaveUp.TryGetValue((guard.thingIDNumber, target.thingIDNumber), out int until)) return false;
            int left = until - Find.TickManager.TicksGame;
            return left > 0 && left <= GaveUpTicks;
        }
    }

    /// <summary>Emergency work for armed staff: open fire on escapees (and rioters, by policy).</summary>
    public class WorkGiver_ArmedResponse : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Armed(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !GuardUtility.IsGuard(pawn) || !ArmedResponse.IsArmed(pawn) || ArmedResponse.Effective(PrisonGame.Instance) == ArmedResponsePolicy.HoldFire || pawn.WorkTagIsDisabled(WorkTags.Violent)
            || (!forced && InmateCandidates.Shared.Armed(pawn.Map).Count == 0);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && ArmedResponse.ShouldShoot(inmate) && pawn.Position.InHorDistOf(inmate.Position, 45f) && !ArmedResponse.GaveUpOn(pawn, inmate) && pawn.CanReserve(inmate, 3, 0, null, forced)
            // (An armed guard in the care reserve is kept from the response unless the trouble is near them or their patient: see CareDetail.Holds.)
            && (forced || !CareDetail.Holds(pawn, inmate));

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Job job = JobMaker.MakeJob(RPArmedDefOf.RP_ArmedResponse, t);
            job.expiryInterval = 900;
            // Trained marksmen: no rounds into bystanders (staff or other inmates).
            job.preventFriendlyFire = true;
            return job;
        }
    }

    public class JobDriver_ArmedResponse : JobDriver
    {
        private Pawn Target => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !ArmedResponse.ShouldShoot(Target));
            // (The job could not go on: no spot to shoot from, no way to it, or the target was no longer one to shoot. The scan leaves this target to this guard for a while, see ArmedResponse.GaveUpOn.)
            AddFinishAction(c =>
            {
                if (c == JobCondition.Incompletable && Target != null) ArmedResponse.Notify_GaveUp(pawn, Target);
            });
            yield return Toils_Combat.TrySetJobToUseAttackVerb(TargetIndex.A);
            Toil gotoPos = Toils_Combat.GotoCastPosition(TargetIndex.A);
            yield return gotoPos;
            Toil jumpIfCannotHit = Toils_Jump.JumpIfTargetNotHittable(TargetIndex.A, gotoPos);
            yield return jumpIfCannotHit;
            yield return Toils_Combat.CastVerb(TargetIndex.A, canHitNonTargetPawns: false);
            yield return Toils_Jump.Jump(jumpIfCannotHit);
        }
    }

    /// <summary>A guard tower: whoever mans it watches every inmate within range, over walls.</summary>
    public class CompProperties_GuardTower : CompProperties
    {
        public float radius = 16f;

        public CompProperties_GuardTower() => compClass = typeof(CompGuardTower);
    }

    public class CompGuardTower : ThingComp
    {
        /// <summary>Tick until which the tower counts as manned (refreshed by the manning job).</summary>
        public int mannedUntil = -1;

        public CompProperties_GuardTower Props => (CompProperties_GuardTower)props;

        public bool Manned => Find.TickManager.TicksGame < mannedUntil;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            PrisonMap.Of(parent.Map)?.RegisterTower(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            PrisonMap.Of(map)?.DeregisterTower(this);
        }

        public bool Covers(IntVec3 c) => Manned && c.InHorDistOf(parent.Position, Props.radius);

        public override void PostDrawExtraSelectionOverlays() => GenDraw.DrawRadiusRing(parent.Position, Props.radius);

        public override string CompInspectStringExtra() => (Manned ? "RP_TowerManned" : "RP_TowerEmpty").Translate();
    }

    /// <summary>Guards with nothing more urgent man empty towers (above patrolling).</summary>
    public class WorkGiver_ManTower : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RPArmedDefOf.RP_GuardTower);

        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t.TryGetComp<CompGuardTower>() is { Manned: false } && (forced || !CareDetail.IsReserve(pawn)) && pawn.CanReserveAndReach(t, PathEndMode.OnCell, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPArmedDefOf.RP_ManTower, t);
    }

    public class JobDriver_ManTower : JobDriver
    {
        /// <summary>How often a guard on a tower asks whether something outranks it (vanilla's expiry check: the think tree is consulted and a better job takes over, else the watch goes on).</summary>
        public const int OverrideCheckTicks = 120;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            // (A plain wait of 2500 ticks kept a guard on the tower through a fight between inmates eight cells off: vanilla's flee or attack answers only a threat to the guard. Every two seconds the
            // think tree is asked now, and the subdue, an escort or a search takes the guard down, as from a patrol.)
            job.expiryInterval = OverrideCheckTicks;
            job.checkOverrideOnExpire = true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoCell(job.targetA.Thing.Position, PathEndMode.OnCell);
            Toil watch = Toils_General.Wait(2500);
            watch.handlingFacing = true;
            watch.tickIntervalAction = delta =>
            {
                if (job.targetA.Thing.TryGetComp<CompGuardTower>() is { } tower) tower.mannedUntil = Find.TickManager.TicksGame + 300;
                if (pawn.IsHashIntervalTick(200, delta)) pawn.Rotation = Rot4.Random;
            };
            yield return watch;
        }
    }

    [DefOf]
    public static class RPArmedDefOf
    {
        public static JobDef RP_ArmedResponse;
        public static JobDef RP_ManTower;
        public static ThingDef RP_GuardTower;

        static RPArmedDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPArmedDefOf));
    }
}
