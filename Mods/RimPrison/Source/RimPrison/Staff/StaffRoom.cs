using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPStaffRoomDefOf
    {
        public static PrisonZoneDef RP_StaffRoom;
        public static JobDef RP_StaffBreak;
        public static ThoughtDef RP_ProperBreak;

        static RPStaffRoomDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPStaffRoomDefOf));
    }

    /// <summary>
    /// The staff room: staff on site spend their shift breaks there (a seat if one is free) and come back better for it,
    /// the more so the better the room (impressiveness: shabby, decent, fine). Without one they take their breaks
    /// wherever vanilla recreation takes them, with no lift. Nobody is pulled off a carry, a meal, or (guards) a riot or a
    /// lockdown.
    /// </summary>
    public static class StaffBreaks
    {
        public const int BreakTicks = 2000, SettledTicks = 600, AlarmCheckTicks = 30;
        public const float DecentImpressiveness = 40f, FineImpressiveness = 65f;

        public static int StageFor(float impressiveness) => impressiveness < DecentImpressiveness ? 0 : impressiveness < FineImpressiveness ? 1 : 2;

        /// <summary>Busy with something a break must not cut short.</summary>
        public static bool Busy(Pawn p, Map map)
        {
            if (p.Downed || p.InMentalState || p.Drafted || p.IsBurning() || p.carryTracker?.CarriedThing is Pawn) return true;
            if (p.CurJobDef == RPStaffRoomDefOf.RP_StaffBreak || p.CurJobDef == JobDefOf.Ingest) return true;
            if (p.CurJob != null && !p.CurJob.def.casualInterruptible) return true;
            // The board of a hearing that is sitting stays in the chair.
            if (PrisonMap.Of(map) is { } pm && ParoleScene.SittingNow(p, pm)) return true;
            // Guards stay at their posts through a riot or a lockdown, and do not go on a break with an inmate acting out that they would be sent to.
            return GuardUtility.IsGuard(p) && (RiotPolice.RiotOngoing(map) || PrisonGame.Instance.LockdownActive || GuardUtility.AlarmFor(p));
        }

        /// <summary>Hourly: staff whose break starts now head for the staff room.</summary>
        public static void Hourly(Map map, int hour)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || !pm.zones.Any(RPStaffRoomDefOf.RP_StaffRoom)) return;
            foreach (Pawn p in StaffLives.OnSite(map))
                if (p.timetable?.GetAssignment(hour) == TimeAssignmentDefOf.Joy && !Busy(p, map)) TrySend(p, pm);
        }

        public static bool TrySend(Pawn p, PrisonMap pm)
        {
            Map map = p.Map;
            List<IntVec3> room = pm.zones.CellsOf(RPStaffRoomDefOf.RP_StaffRoom);
            // A free seat first (reserved the way vanilla reserves seats, so diners and breakers never share one), else
            // any free spot in the room.
            IntVec3 spot = room.Where(c => c.GetEdifice(map) is { def.building.isSittable: true } && p.CanReserveSittableOrSpot(c) && p.CanReach(c, PathEndMode.OnCell, Danger.Some))
                .InRandomOrder().FirstOrFallback(IntVec3.Invalid);
            if (!spot.IsValid)
                spot = room.Where(c => c.Standable(map) && c.GetFirstPawn(map) == null && p.CanReserveSittableOrSpot(c) && p.CanReach(c, PathEndMode.OnCell, Danger.Some))
                    .InRandomOrder().FirstOrFallback(IntVec3.Invalid);
            if (!spot.IsValid) return false;
            p.jobs.StartJob(JobMaker.MakeJob(RPStaffRoomDefOf.RP_StaffBreak, spot), JobCondition.InterruptForced);
            return true;
        }

        /// <summary>The lift, refreshed to this break's room (an older break's memory would keep its stage).</summary>
        public static void GiveLift(Pawn p)
        {
            if (p.needs?.mood == null || ThoughtMaker.MakeThought(RPStaffRoomDefOf.RP_ProperBreak) is not Thought_Memory m) return;
            m.SetForcedStage(StageFor(p.GetRoom()?.GetStat(RoomStatDefOf.Impressiveness) ?? 0f));
            p.needs.mood.thoughts.memories.RemoveMemoriesOfDef(RPStaffRoomDefOf.RP_ProperBreak);
            p.needs.mood.thoughts.memories.TryGainMemory(m);
            PrisonGame.Instance.stats.staffBreaks++;
        }
    }

    /// <summary>Walk to the staff room and take the break there: a seat at the table, a chat, a coffee.</summary>
    public class JobDriver_StaffBreak : JobDriver
    {
        private int settledAt = -1, nextAlarmCheckTick;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.ReserveSittableOrSpot(job.targetA.Cell, job, errorOnFailed);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref settledAt, "settledAt", -1);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // (A guard on a break is called away by an alarm they would be sent to: the walk to the room and the break itself are long jobs that nothing asks the think tree during - see JobDriver_Count.)
            nextAlarmCheckTick = Find.TickManager.TicksGame + StaffBreaks.AlarmCheckTicks;
            AddEndCondition(() =>
            {
                int now = Find.TickManager.TicksGame;
                if (now < nextAlarmCheckTick) return JobCondition.Ongoing;
                nextAlarmCheckTick = now + StaffBreaks.AlarmCheckTicks;
                return GuardUtility.AlarmFor(pawn) ? JobCondition.InterruptOptional : JobCondition.Ongoing;
            });
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil rest = ToilMaker.MakeToil("StaffBreak");
            rest.initAction = () =>
            {
                settledAt = Find.TickManager.TicksGame;
                // Face the table if the seat is at one.
                foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(pawn))
                    if (c.InBounds(pawn.Map) && c.GetEdifice(pawn.Map) is { def.surfaceType: SurfaceType.Eat })
                    {
                        pawn.rotationTracker.FaceCell(c);
                        break;
                    }
            };
            rest.tickIntervalAction = delta =>
            {
                pawn.needs?.joy?.GainJoy(0.000144f * delta, JoyKindDefOf.Social);
                pawn.GainComfortFromCellIfPossible(delta);
                // The lift once they have actually had some of the break.
                if (settledAt >= 0 && Find.TickManager.TicksGame - settledAt >= StaffBreaks.SettledTicks)
                {
                    settledAt = -1;
                    StaffBreaks.GiveLift(pawn);
                }
            };
            rest.handlingFacing = true;
            rest.defaultCompleteMode = ToilCompleteMode.Delay;
            rest.defaultDuration = StaffBreaks.BreakTicks;
            rest.socialMode = RandomSocialMode.Normal;
            yield return rest;
        }
    }
}
