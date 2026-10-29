using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    // Arrivals: those carried over, the marshals' prisoners, new inmates on the way to holding and their escorts.
    public partial class ShuttleSystem
    {
        /// <summary>New inmates still aboard stay on the world's books, and come with the next daily shuttle.</summary>
        private void CarryOver(Building_StateShuttle shuttle)
        {
            if (shuttle?.passengers == null) return;
            foreach (Pawn p in shuttle.passengers.InnerListForReading.ToList())
            {
                shuttle.passengers.Remove(p);
                if (p.Destroyed || p.Dead) continue;
                if (!Find.WorldPawns.Contains(p)) Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                // (The marshals' prisoners stay on their own list: carried over as a new inmate, they would come back through the intake and not through the return.)
                // (A custody contract's cohort member who was not unloaded stays the contract's: pending, first on the next roster after the marshals' prisoners.)
                if (!carriedOver.Contains(p) && MarshalOf(p) == null && !CustodyContracts.Holds(p)) carriedOver.Add(p);
            }
        }

        /// <summary>Those carried over who can still come (alive); the list is cleaned of the rest.</summary>
        public List<Pawn> LivingCarriedOver()
        {
            carriedOver.RemoveAll(p => p == null || p.Destroyed || p.Dead || p.Spawned || p.Inmate()?.security == null);
            return new List<Pawn>(carriedOver);
        }

        // ---- the marshals' prisoners ----------------------------------------------------------------------------

        public MarshalEntry MarshalOf(Pawn p)
        {
            for (int i = 0; i < marshals.Count; i++)
                if (marshals[i].pawn == p) return marshals[i];
            return null;
        }

        public bool IsMarshal(Pawn p) => MarshalOf(p) != null;

        /// <summary>The state's marshals have this prisoner for the prison (a rearrest that came due, an escapee they caught): they wait for the next daily shuttle, first on its roster.</summary>
        public void AddMarshal(Pawn p, MarshalKind kind, string reason = null, int escapedTick = -1)
        {
            if (p == null || MarshalOf(p) != null) return;
            Find.WorldPawns.ForcefullyKeptPawns.Add(p);
            marshals.Add(new MarshalEntry { pawn = p, kind = kind, reason = reason, escapedTick = escapedTick, sinceTick = Now });
        }

        /// <summary>The prisoner steps off: they are no longer waiting (null if they were not a marshals' prisoner).</summary>
        public MarshalEntry TakeMarshal(Pawn p)
        {
            MarshalEntry e = MarshalOf(p);
            if (e != null) marshals.Remove(e);
            return e;
        }

        /// <summary>
        /// The marshals' prisoners who can still come, in the order they were brought in. The list is cleaned of the rest: the dead and the gone, one who is back by another road, and one who has
        /// waited <see cref="MarshalWaitDays"/> days (no bed, no shuttle: the state sends them to another prison).
        /// </summary>
        public List<MarshalEntry> LivingMarshals()
        {
            for (int i = marshals.Count - 1; i >= 0; i--)
            {
                MarshalEntry e = marshals[i];
                Pawn p = e.pawn;
                if (!StillWaiting(e, out bool timedOut))
                {
                    if (p != null) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                    marshals.RemoveAt(i);
                    // (Gone or dead is no news; the state's marshals sending one to another prison is.)
                    if (timedOut && p != null)
                    {
                        Messages.Message("RP_MarshalsGaveUp".Translate(p.LabelShort, MarshalWaitDays), MessageTypeDefOf.NeutralEvent, historical: false);
                        Story.Record(RPChronicleDefOf.RP_Chr_Intake, 2, "RP_Chr_MarshalsGaveUp".Translate(p.LabelShort), p);
                    }
                }
            }
            return new List<MarshalEntry>(marshals);
        }

        /// <summary>How many of the marshals' prisoners are waiting for a shuttle, as <see cref="LivingMarshals"/> would leave the list (for a screen: it changes nothing).</summary>
        public int MarshalsWaiting()
        {
            int n = 0;
            foreach (MarshalEntry e in marshals)
                if (StillWaiting(e, out _)) n++;
            return n;
        }

        private bool StillWaiting(MarshalEntry e, out bool timedOut)
        {
            Pawn p = e.pawn;
            bool aboard = p != null && visit?.shuttle?.passengers != null && visit.shuttle.passengers.Contains(p);
            bool gone = p == null || p.Destroyed || p.Dead || p.Spawned || p.Inmate()?.security == null;
            timedOut = !gone && !aboard && Now - e.sinceTick > MarshalWaitDays * GenDate.TicksPerDay;
            return !gone && !timedOut;
        }

        private void ArrivalEscortsEnd(Map map)
        {
            // Arrivals still waiting for their walk go on foot: the shuttle is gone, the guards are free.
            foreach (ArrivalEntry e in arrivals)
                if (e.escortId < 0) e.walkOnTick = Mathf.Min(e.WalkOnAt, Now);
        }

        // ---- escorts ------------------------------------------------------------------------------------------

        /// <summary>The guard has the new inmate by the arm: they walk to holding together.</summary>
        public static void Notify_Escorted(Pawn inmate, Pawn guard)
        {
            if (Sys?.ArrivalOf(inmate) is not { } e) return;
            e.escortId = guard.thingIDNumber;
            e.escortTick = Now;
            Sys.escortsStarted++;
            // Whatever they were doing is over: they are wanted.
            if (inmate.CurJob != null) inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (AutoTest.Active) RPLog.Message($"[shuttle] {guard.LabelShort} is escorting {inmate.LabelShort} to holding");
        }

        /// <summary>The guard's walk is over: if the inmate is still on the way, nobody is walking them any more.</summary>
        public static void Notify_EscortEnded(Pawn inmate, Pawn guard)
        {
            if (Sys?.ArrivalOf(inmate) is { } e && e.escortId == guard.thingIDNumber) e.escortId = -1;
        }

        /// <summary>The new inmate is inside the prison: in a zone of it, in their cell, or in a part of the map that is closed off from its edge.</summary>
        public static bool IsInside(Pawn p)
        {
            if (p.Inmate() is { processed: true }) return true;
            if (PrisonMap.Of(p.Map)?.zones.ZoneAt(p.Position) != null) return true;
            return p.GetDistrict() is { TouchesMapEdge: false };
        }

        /// <summary>New inmates who have been outside the perimeter for ten hours or more and cannot get in (no holding zone and no free bed, a gate locked, a break at the pad).</summary>
        public List<Pawn> StuckArrivals(Map map)
        {
            var stuck = new List<Pawn>();
            for (int i = 0; i < arrivals.Count; i++)
                if (arrivals[i].pawn is { Spawned: true } p && p.Map == map && Now - arrivals[i].sinceTick > ArrivalTimeoutTicks && !IsInside(p)) stuck.Add(p);
            return stuck;
        }

        // ---- arrivals -----------------------------------------------------------------------------------------

        public ArrivalEntry ArrivalOf(Pawn p)
        {
            for (int i = 0; i < arrivals.Count; i++)
                if (arrivals[i].pawn == p) return arrivals[i];
            return null;
        }

        public static bool IsArrival(Pawn p) => Sys?.ArrivalOf(p) != null;

        public void AddArrival(Pawn p)
        {
            if (ArrivalOf(p) != null) return;
            arrivals.Add(new ArrivalEntry { pawn = p, sinceTick = Now, walkOnTick = Now + EscortWaitTicks });
            if (p.Inmate() is { } c)
            {
                c.inTransit = true;
                if (p.Spawned) p.Map.reachability.ClearCacheFor(p);
            }
        }

        /// <summary>The new inmate is in their own cell: they are no longer on the way in.</summary>
        public void Notify_Processed(Pawn p)
        {
            if (ArrivalOf(p) is { } e) EndArrival(e);
        }

        public void EndArrival(ArrivalEntry e)
        {
            arrivals.Remove(e);
            ClearTransit(e.pawn);
        }

        /// <summary>
        /// The new inmate's job until they reach holding (their cell, without a holding zone): wait by the ramp for a guard, then walk with them; after a wait,
        /// walk on their own. Null when they are not an arrival, or once there.
        /// </summary>
        public static Job ArrivalJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            ShuttleSystem s = Sys;
            ArrivalEntry e = s?.ArrivalOf(pawn);
            if (e == null) return null;
            bool holding = pm.zones.Any(RPDefOf.RP_Holding);
            if (holding ? pm.zones.ZoneAt(pawn.Position) == RPDefOf.RP_Holding : comp.processed)
            {
                s.EndArrival(e);
                return null;
            }
            // Without a holding zone they walk to their cells as they always have (the regime's own job), now through the gate.
            if (!holding) return null;
            int now = Now;
            if (e.escortId >= 0)
            {
                Pawn guard = null;
                List<Pawn> guards = pm.Guards;
                for (int i = 0; i < guards.Count && guard == null; i++)
                    if (guards[i].thingIDNumber == e.escortId) guard = guards[i];
                if (guard is { Spawned: true } && guard.CurJob is { } gj && gj.def == RPShuttleDefOf.RP_EscortArrival && gj.targetA.Thing == pawn)
                {
                    // They walk to the cell the guard is taking them to, at the guard's pace (the guard's job is a walk; never a follow that is done the moment they stand
                    // beside the guard: it was given again in the same tick, ten times over). At the spot already, or no way there: they wait for the guard to be done.
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
            if (now > e.WalkOnAt)
            {
                IntVec3 cell = RegimeActivityWorker.RandomReachableZoneCell(pawn, pm, RPDefOf.RP_Holding);
                if (cell.IsValid)
                {
                    Job go = JobMaker.MakeJob(JobDefOf.Goto, cell);
                    go.locomotionUrgency = LocomotionUrgency.Walk;
                    return go;
                }
            }
            // Lined up by the ramp.
            return Stand();
        }

        private static Job Stand()
        {
            Job stand = JobMaker.MakeJob(JobDefOf.Wait);
            stand.expiryInterval = 120;
            stand.checkOverrideOnExpire = true;
            return stand;
        }
    }
}
