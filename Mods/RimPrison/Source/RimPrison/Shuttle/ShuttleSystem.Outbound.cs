using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    // Outbound: the released, paroled and transferred waiting for the shuttle, boarding, and the transfers' escorts.
    public partial class ShuttleSystem
    {
        // ---- outbound -----------------------------------------------------------------------------------------

        public OutboundEntry OutboundOf(Pawn p)
        {
            for (int i = 0; i < outbound.Count; i++)
                if (outbound[i].pawn == p) return outbound[i];
            return null;
        }

        /// <summary>
        /// On their way out: released (by the warden, or by vanilla's own order), paroled or transferred, and not yet gone. Such a pawn is no longer one of the prison's
        /// inmates: no sentence to serve (or to release again), no daily payment, no bed, no say in who leads a gang, no command to give; they only wait for the shuttle.
        /// </summary>
        public static bool OnTheWayOut(Pawn p) => p != null && (p.guest is { Released: true } || Sys?.OutboundOf(p) != null);

        /// <summary>The inmates of the map, not counting those on their way out.</summary>
        public static int InmateCount(Map map)
        {
            int n = 0;
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
                if (!OnTheWayOut(prisoners[i])) n++;
            return n;
        }

        /// <summary>Set while the prison's own release (or transfer) calls vanilla's: the postfix on vanilla's release must not take the pawn over a second time.</summary>
        public static bool RegisteringOwnRelease;

        /// <summary>A released, paroled or transferred inmate is on their way out: to the shuttle, or to wait for it. One who is already on the way keeps the kind they left as.</summary>
        /// <param name="counted">False for vanilla's own Release order: no release for the reform grade.</param>
        /// <param name="reoffends">A reoffence follows the release: counted with it when they leave.</param>
        /// <param name="caseId">The supervision case that starts when they leave (see <see cref="Supervision"/>).</param>
        public void Register(Pawn p, OutboundKind kind, bool counted = true, bool reoffends = false, int caseId = -1)
        {
            // (A pawn the release made a free colonist, one of the colony's own who was held here, stays: there is nobody to send away.)
            if (p.IsFreeColonist) return;
            // (Released, paroled or transferred: a custody contract's bonus for them is forfeit, from the moment they are on their way.)
            CustodyContracts.Notify_Departed(p, "RP_CustodyEnd_" + kind);
            if (OutboundOf(p) == null) outbound.Add(new OutboundEntry { pawn = p, sinceTick = Now, kind = kind, counted = counted, reoffends = reoffends, caseId = caseId });
            // A transfer is sent out like a release (they leave the prison's custody: no regime, the doors as they are for the released), but they are not counted as released.
            if (kind == OutboundKind.Transferred && p.guest is { Released: false } guest)
            {
                p.ownership?.UnclaimAll();
                guest.Released = true;
                guest.SetNoInteraction();
                p.Inmate()?.RefreshActiveFlag();
            }
            // Whoever was walking to the exit on vanilla's order stops: the way out is the shuttle.
            p.jobs?.EndCurrentJob(JobCondition.InterruptForced);
        }

        public void Forget(Pawn p)
        {
            bool had = outbound.RemoveAll(e => e.pawn == p) > 0;
            had |= arrivals.RemoveAll(e => e.pawn == p) > 0;
            if (had) ClearTransit(p);
        }

        private static void ClearTransit(Pawn p)
        {
            if (p?.Inmate() is not { } c) return;
            c.inTransit = false;
            if (p.Spawned) p.Map.reachability.ClearCacheFor(p);
        }

        /// <summary>The inmate has left the map (boarded, or taken off by an ambulance): a release counts now, with the reoffence that was to follow it.</summary>
        public static void Notify_Left(Pawn p)
        {
            ShuttleSystem s = Sys;
            if (s == null) return;
            OutboundEntry e = s.OutboundOf(p);
            if (e == null) return;
            s.outbound.Remove(e);
            if (e.kind == OutboundKind.Transferred || !e.counted) return;
            PrisonGame.Instance.stats.released++;
            if (e.reoffends) PrisonGame.Instance.stats.reoffended++;
            // (Supervision starts at the physical departure: the days of review are counted from the moment they are gone.)
            if (e.caseId >= 0) Supervision.Notify_Departed(PrisonGame.Instance, e.caseId);
        }


        /// <summary>The pawn reaches the ramp and goes aboard (a stowaway who a guard is watching for is stopped here).</summary>
        public static void Board(Pawn pawn, Building_StateShuttle shuttle)
        {
            ShuttleSystem s = Sys;
            ShuttleVisit v = VisitOf(shuttle);
            bool stowing = s?.stowaway == pawn;
            if (stowing && s.StowawayAtRamp(pawn, shuttle)) return;
            // (A stowaway is no passenger: they are an escape, counted as one.)
            if (v != null && !stowing) v.boarded++;
            RPVanillaDefOf.Shuttle_PawnLoaded.PlayOneShot(new TargetInfo(shuttle.Position, shuttle.Map));
            if (s?.OutboundOf(pawn) is { kind: OutboundKind.Transferred })
            {
                // A transfer is sent to another facility, not set free: no freed-prisoner goodwill, no world pawn to keep.
                Notify_Left(pawn);
                pawn.DeSpawn();
                Find.WorldPawns.PassToWorld(pawn, RimWorld.Planet.PawnDiscardDecideMode.Discard);
                return;
            }
            pawn.ExitMap(false, Rot4.Invalid);
        }

        /// <summary>The guard has the transferred inmate by the arm: they walk to the ramp together.</summary>
        public static void Notify_TransferEscorted(Pawn inmate, Pawn guard)
        {
            if (Sys?.OutboundOf(inmate) is not { kind: OutboundKind.Transferred } e) return;
            e.escortId = guard.thingIDNumber;
            e.escortTick = Now;
            Sys.escortsStarted++;
            // Whatever they were doing (waiting for the shuttle) is over: they are wanted.
            if (inmate.CurJob != null) inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (AutoTest.Active) RPLog.Message($"[shuttle] {guard.LabelShort} is escorting the transfer {inmate.LabelShort} to the shuttle");
        }

        /// <summary>
        /// The guard's walk is over (an alarm, a shift's end, a draft): if the transfer has not boarded, nobody is walking them any more, and the walk to the ramp they were on stops where they stand (it is a
        /// job of its own, which nothing asks again until it is done): they wait for the next guard as one whose guard has not come yet (<see cref="TransferWalk"/>). One already on the job of boarding, beside the
        /// ramp, goes on.
        /// </summary>
        public static void Notify_TransferEscortEnded(Pawn inmate, Pawn guard)
        {
            if (Sys?.OutboundOf(inmate) is not { } e || e.escortId != guard.thingIDNumber) return;
            e.escortId = -1;
            if (inmate.Spawned && inmate.CurJobDef == JobDefOf.Goto) inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>A transfer waits this long for a guard once the shuttle is down; then they walk to the ramp on their own (as the arrivals do after <see cref="EscortWaitTicks"/>).</summary>
        public static bool WalksOnAlone(OutboundEntry e, ShuttleVisit v) => e.escortId < 0 && Now - Mathf.Max(e.sinceTick, v.landedTick) > EscortWaitTicks;

        /// <summary>
        /// The transfer's job while the shuttle is down and boarding: waiting for a guard, walking with the guard that has them, or, with the guard at the ramp, null (they board
        /// with the guard beside them: the guard's job is a walk and theirs is a walk to the same spot, never a follow that is done the moment they stand beside the guard). Null too for one
        /// who is not a transfer, or who no guard came for in <see cref="EscortWaitTicks"/>.
        /// </summary>
        public static Job TransferWalk(Pawn pawn, OutboundEntry e, Building_StateShuttle shuttle)
        {
            if (e is not { kind: OutboundKind.Transferred } || VisitOf(shuttle) is not { } v || WalksOnAlone(e, v)) return null;
            if (e.escortId >= 0)
            {
                // (By the colonists on the map, not the prison's list of guards: that list is made again every 250 ticks, and a guard who came on shift since was not on it, so the walk
                // was forgotten the moment it began and the inmate stood beside the guard until they went on alone.)
                Pawn guard = null;
                IReadOnlyList<Pawn> colonists = pawn.Map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < colonists.Count && guard == null; i++)
                    if (colonists[i].thingIDNumber == e.escortId) guard = colonists[i];
                if (guard is { Spawned: true } && guard.CurJob is { } gj && gj.def == RPShuttleDefOf.RP_EscortTransfer && gj.targetA.Thing == pawn)
                {
                    if (pawn.Position.InHorDistOf(shuttle.InteractionCell, 3f)) return null;
                    IntVec3 target = gj.targetB.Cell;
                    if (target.IsValid && pawn.Position != target && pawn.CanReach(target, PathEndMode.OnCell, Danger.Deadly))
                    {
                        Job walk = JobMaker.MakeJob(JobDefOf.Goto, target);
                        walk.locomotionUrgency = LocomotionUrgency.Walk;
                        return walk;
                    }
                    return Stand();
                }
                e.escortId = -1;
            }
            return Stand();
        }
    }
}
