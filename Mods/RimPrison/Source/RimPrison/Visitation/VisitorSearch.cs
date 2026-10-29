using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>How visitors are checked before a contact visit.</summary>
    public enum VisitorSearchPolicy : byte
    {
        None,
        PatDown,
        Thorough,
    }

    [DefOf]
    public static class RPVisitorSearchDefOf
    {
        public static JobDef RP_SearchVisitor;
        public static ThingDef RP_MetalDetector;
        public static WorkGiverDef RP_SearchVisitor_WorkGiver;

        static RPVisitorSearchDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPVisitorSearchDefOf));
    }

    /// <summary>
    /// Visitor reception: before a contact visit a guard searches the visitor in the visitation room. A pat-down is
    /// quick and misses a lot; a thorough search finds most things but puts families off. Metal detectors in the room
    /// and K9 dogs nearby help. Smugglers who get caught are turned away and the inmate loses visits for a while. No
    /// guard within the hour: the visitor is waved through.
    /// </summary>
    public static class VisitorSearch
    {
        public const int PatDownTicks = 150, ThoroughTicks = 450;
        /// <summary>From arrival in the visitation room; after this the visit goes ahead unsearched.</summary>
        public const int WaveThroughTicks = GenDate.TicksPerHour;
        public const int BanTicks = GenDate.TicksPerDay * 5;

        /// <summary>The policy for this visit: flagged visits (phone taps) are always searched thoroughly.</summary>
        public static VisitorSearchPolicy PolicyFor(CompInmate c, PrisonGame game) =>
            c.visitFlagged ? VisitorSearchPolicy.Thorough : game.visitorSearch;

        /// <summary>The inmate may sit down with the visitor (searched, waved through, or no search needed).</summary>
        public static bool Cleared(CompInmate c)
        {
            if (!c.visitorSearchPending) return true;
            if (c.visitorArrivedTick < 0 || Find.TickManager.TicksGame - c.visitorArrivedTick < WaveThroughTicks) return false;
            // A guard is on it (the reservation goes when the search ends or is abandoned).
            if (c.visitor is { Spawned: true } sv && sv.Map.reservationManager.IsReservedByAnyoneOf(sv, Faction.OfPlayer)) return false;
            c.visitorSearchPending = false;
            PrisonGame.Instance.stats.visitorsWavedThrough++;
            if (AutoTest.Active && c.visitor is { } v)
                RPLog.Message($"[visit] {v.LabelShort} waved through at {v.Position} zone={PrisonMap.Of(v.Map)?.zones.ZoneAt(v.Position)?.defName} guards: "
                              + string.Join(", ", v.Map.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard).Select(g => $"{g.LabelShort}:{g.CurJobDef?.defName}/{g.timetable?.CurrentAssignment?.defName}")));
            return true;
        }

        private static Map cachedMap;
        private static int cachedTick = -1;
        private static bool cachedWaiting;

        /// <summary>
        /// A visitor waits in the visitation room and no guard has claimed the search yet. Polled by every patrolling
        /// guard's end condition, so the answer is cached for a second of game time (for the last map asked: the
        /// prison is one map).
        /// </summary>
        public static bool UnclaimedVisitorWaiting(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (map == cachedMap && now - cachedTick < 60 && now >= cachedTick) return cachedWaiting;
            cachedMap = map;
            cachedTick = now;
            cachedWaiting = ComputeUnclaimedVisitorWaiting(map);
            return cachedWaiting;
        }

        private static bool ComputeUnclaimedVisitorWaiting(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return false;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { visitorSearchPending: true, visitor: { Spawned: true } v } && pm.zones.ZoneAt(v.Position) == RPVisitDefOf.RP_Visitation
                    && !map.reservationManager.IsReservedByAnyoneOf(v, Faction.OfPlayer))
                    return true;
            return false;
        }

        /// <summary>The visitor-search work giver has a job for this guard right now.</summary>
        public static bool CanSearchAnyVisitor(Pawn guard)
        {
            WorkGiverDef def = RPVisitorSearchDefOf.RP_SearchVisitor_WorkGiver;
            if (def.Worker is not WorkGiver_SearchVisitor wg || wg.ShouldSkip(guard) || wg.MissingRequiredCapacity(guard) != null) return false;
            if (guard.workSettings == null || !guard.workSettings.WorkIsActive(def.workType) || guard.WorkTypeIsDisabled(def.workType)) return false;
            foreach (Thing v in wg.PotentialWorkThingsGlobal(guard))
                if (!v.IsForbidden(guard) && wg.HasJobOnThing(guard, v)) return true;
            return false;
        }

        public static float FindChance(Pawn visitor, CompInmate c, VisitorSearchPolicy policy)
        {
            ThingDef item = c.visitorContraband;
            if (item == null) return 0f;
            float chance = policy == VisitorSearchPolicy.Thorough ? 0.75f : 0.35f;
            if (c.visitFlagged) chance += 0.25f;
            ContrabandExtension ext = ContrabandUtility.Ext(item);
            if (ext.metal && DetectorInRoom(visitor)) chance += 0.35f;
            if (ext.kind == ContrabandKind.Drug && DogNear(visitor)) chance += 0.4f;
            return Mathf.Min(0.95f, chance);
        }

        private static bool DetectorInRoom(Pawn visitor)
        {
            Room room = visitor.GetRoom();
            if (room == null) return false;
            foreach (Building b in visitor.Map.listerBuildings.AllBuildingsColonistOfDef(RPVisitorSearchDefOf.RP_MetalDetector))
                if (b.GetRoom() == room) return true;
            return false;
        }

        private static bool DogNear(Pawn visitor)
        {
            foreach (Pawn a in visitor.Map.mapPawns.SpawnedColonyAnimals)
                if (a.Position.InHorDistOf(visitor.Position, 8f) && K9Utility.IsK9(a) && !a.Downed) return true;
            return false;
        }

        /// <summary>The guard has finished searching the visitor of <paramref name="inmate"/>.</summary>
        public static void Resolve(Pawn guard, Pawn visitor, Pawn inmate, CompInmate c)
        {
            PrisonGame game = PrisonGame.Instance;
            VisitorSearchPolicy policy = PolicyFor(c, game);
            c.visitorSearchPending = false;
            c.visitorStripSearched = policy == VisitorSearchPolicy.Thorough;
            game.stats.visitorSearches++;
            bool found = Rand.Chance(FindChance(visitor, c, policy));
            // A guard on the inmate's payroll waves their visitor through (and gets a cut).
            if (found && CorruptionSystem.LooksAway(guard, inmate) && guard.GetComp<CompStaff>() is { } gsc)
            {
                found = false;
                gsc.bribesTaken++;
                game.stats.bribes++;
            }
            c.visitFlagged = false;
            if (!found) return;
            ThingDef item = c.visitorContraband;
            c.visitorContraband = null;
            PrisonMap.Of(visitor.Map)?.AddFind(visitor.Position, ContrabandUtility.Ext(item).kind, FindSource.Visitor);
            RingSystem.Notify_FoundOnWay(inmate, c.visitorRelative, "visit");
            c.visitBanUntilTick = Find.TickManager.TicksGame + BanTicks;
            c.suspected = true;
            c.tension = Mathf.Min(100f, c.tension + 10f);
            game.stats.visitorContrabandFound++;
            game.today.contrabandFound++;
            Messages.Message("RP_VisitorCaught".Translate(guard.LabelShort, visitor.LabelShort, item.label, inmate.LabelShort), visitor, MessageTypeDefOf.NeutralEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_SmugglerCaught".Translate(visitor.LabelShort, inmate.LabelShort, item.label, guard.LabelShort), inmate, guard);
            // No visit: the visitor is held for the police (or bolts).
            VisitationSystem.EndVisit(c, null);
            Detention.Notify_VisitorCaught(guard, visitor);
        }
    }

    /// <summary>Guards search visitors waiting in the visitation room.</summary>
    public class WorkGiver_SearchVisitor : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (Pawn p in pawn.Map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { visitorSearchPending: true, visitor: { Spawned: true } v }) yield return v;
        }

        // (The care reserve takes no search: see CareDetail.)
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || !forced && CareDetail.IsReserve(pawn);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Pawn visitor || visitor.Downed) return false;
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (pm == null || pm.zones.ZoneAt(visitor.Position) != RPVisitDefOf.RP_Visitation) return false;
            return pawn.CanReserveAndReach(visitor, PathEndMode.Touch, Danger.Some, 1, -1, null, forced);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPVisitorSearchDefOf.RP_SearchVisitor, t, VisitationSystem.InmateOf(t as Pawn));
    }

    /// <summary>Walk up to the visitor, hold them, search, resolve.</summary>
    public class JobDriver_SearchVisitor : JobDriver
    {
        private Pawn Visitor => (Pawn)job.targetA.Thing;
        private Pawn Inmate => (Pawn)job.targetB.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Inmate?.Inmate() is not { visitorSearchPending: true } c || c.visitor != Visitor);
            Toil approach = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return approach;
            yield return Toils_Jump.JumpIf(approach, () => !pawn.Position.AdjacentTo8WayOrInside(Visitor.Position));
            int ticks = VisitorSearch.PolicyFor(Inmate.Inmate(), PrisonGame.Instance) == VisitorSearchPolicy.Thorough
                ? VisitorSearch.ThoroughTicks : VisitorSearch.PatDownTicks;
            Toil hold = ToilMaker.MakeToil("HoldVisitor");
            hold.initAction = () =>
            {
                Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                wait.expiryInterval = ticks + 30;
                Visitor.jobs.StartJob(wait, JobCondition.InterruptForced);
            };
            yield return hold;
            Toil search = Toils_General.Wait(ticks);
            search.handlingFacing = true;
            search.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Visitor);
            search.WithProgressBarToilDelay(TargetIndex.A);
            yield return search;
            Toil done = ToilMaker.MakeToil("VisitorSearched");
            done.initAction = () => VisitorSearch.Resolve(pawn, Visitor, Inmate, Inmate.Inmate());
            yield return done;
        }
    }
}
