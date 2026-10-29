using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Faith: some inmates are religious (rolled at intake) and have a faith need. In free time they pray in the
    /// chapel; a chaplain leading a service there doubles the comfort and takes the edge off their tension. A spiritual
    /// guidance class taught in the chapel counts as the service for as long as it runs.
    /// </summary>
    public static class FaithUtility
    {
        public const float ReligiousChance = 0.35f;
        public const float PrayBelow = 0.55f;
        /// <summary>Faith restored per hour of prayer in the chapel (doubled with a service).</summary>
        public const float PrayerPerHour = 0.25f;

        public static Need Need(Pawn p) => p.needs?.TryGetNeed(RPFaithDefOf.RP_Faith);

        /// <summary>Passed spiritual guidance: faith needs less tending.</summary>
        public static bool Guided(Pawn p) => p.Inmate() is { } c && c.passedPrograms.Any(d => d.deepensFaith);

        /// <summary>Free time: a religious inmate low on faith goes to pray in the chapel (else in their cell).</summary>
        public static Job TryPray(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (!c.religious || Need(p) is not { CurLevel: < PrayBelow }) return null;
            List<IntVec3> chapel = pm.zones.CellsOf(RPFaithDefOf.RP_Chapel);
            if (chapel.Count == 0) return TryPrayInCell(p, c);
            // A free seat in the chapel, else any free standable chapel cell.
            for (int tries = 0; tries < 12; tries++)
            {
                IntVec3 cell = chapel.RandomElement();
                if (!cell.Standable(p.Map) || !KeptRooms.Allows(pm, cell, p) || !p.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.Some)) continue;
                if (cell.GetFirstPawn(p.Map) != null) continue;
                Job job = JobMaker.MakeJob(RPFaithDefOf.RP_Pray, cell);
                job.expiryInterval = GenDate.TicksPerHour * 2;
                return job;
            }
            return TryPrayInCell(p, c);
        }

        /// <summary>Locked in (lockup, death row, no chapel): pray quietly in the cell, at half the comfort.</summary>
        public static Job TryPrayInCell(Pawn p, CompInmate c)
        {
            if (!c.religious || Need(p) is not { CurLevel: < PrayBelow }) return null;
            Room cell = InmateUtility.CellOf(p);
            if (cell == null || p.GetRoom() != cell) return null;
            // (The place they stand may be somebody else's to pray at, or walk through: the job reserves it, and a job whose reservation fails is an error in the log, every time it is tried.)
            if (!p.CanReserve(p.Position)) return null;
            Job job = JobMaker.MakeJob(RPFaithDefOf.RP_Pray, p.Position);
            job.expiryInterval = GenDate.TicksPerHour * 2;
            return job;
        }

        public static bool InChapel(Pawn p) => PrisonMap.Of(p.Map)?.zones.ZoneAt(p.Position) == RPFaithDefOf.RP_Chapel;

        /// <summary>A chaplain leading a service in the chapel, or teaching spiritual guidance there (praying inmate must be in the chapel too).</summary>
        public static bool ServiceFor(Pawn worshipper)
        {
            if (!InChapel(worshipper)) return false;
            foreach (Pawn s in worshipper.Map.mapPawns.FreeColonistsSpawned)
                if ((s.CurJobDef == RPFaithDefOf.RP_LeadService || s.CurJobDef == RPFuneralDefOf.RP_LeadFuneral || s.CurJobDef == RPProgramDefOf.RP_TeachClass) && InChapel(s) && s.Position.InHorDistOf(worshipper.Position, 12f)) return true;
            return false;
        }

        /// <summary>
        /// Sitting in a spiritual guidance class is the service itself: faith comes back at the service rate while the chaplain is teaching
        /// in the chapel, within reach of the voice (the chapel rate otherwise, as <see cref="ServiceFor"/>), and tension eases.
        /// </summary>
        public static void Attend(Pawn p, ProgramSession s, int delta)
        {
            if (Need(p) is not { } faith) return;
            bool taught = s.teacher is { } t && t.CurJobDef == RPProgramDefOf.RP_TeachClass && InChapel(t) && t.Position.InHorDistOf(p.Position, 12f);
            faith.CurLevel += PrayerPerHour * (taught ? 2f : 1f) * delta / GenDate.TicksPerHour;
            if (taught && p.Inmate() is { } c) c.tension = Mathf.Max(0f, c.tension - 3f * delta / GenDate.TicksPerHour);
        }
    }

    /// <summary>Faith falls half as fast for inmates who passed spiritual guidance.</summary>
    public class Need_Faith : Need_Fixture
    {
        public Need_Faith(Pawn pawn) : base(pawn)
        {
        }

        protected override float FallFactor => base.FallFactor;
    }

    public class JobDriver_Pray : JobDriver
    {
        private const float PerHour = FaithUtility.PrayerPerHour;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil pray = Toils_General.Wait(GenDate.TicksPerHour * 3 / 2);
            pray.socialMode = RandomSocialMode.Off;
            pray.initAction = () => PrisonGame.Instance.stats.prayers++;
            pray.tickIntervalAction = delta =>
            {
                if (FaithUtility.Need(pawn) is not { } faith) return;
                if (pawn.IsHashIntervalTick(250, delta))
                {
                    serviceCached = FaithUtility.ServiceFor(pawn);
                    inChapelCached = FaithUtility.InChapel(pawn);
                }
                bool service = serviceCached;
                faith.CurLevel += PerHour * (service ? 2f : inChapelCached ? 1f : 0.5f) * delta / GenDate.TicksPerHour;
                if (service && pawn.Inmate() is { } c) c.tension = Mathf.Max(0f, c.tension - 3f * delta / GenDate.TicksPerHour);
                if (faith.CurLevel >= 0.99f) ReadyForNextToil();
            };
            yield return pray;
        }

        private bool serviceCached, inChapelCached = true;
    }

    /// <summary>The chaplain leads a service in the chapel whenever inmates are praying there.</summary>
    public class WorkGiver_LeadService : WorkGiver
    {
        public override Job NonScanJob(Pawn pawn)
        {
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (pm == null) return null;
            Pawn worshipper = null;
            foreach (Pawn p in pawn.Map.mapPawns.PrisonersOfColonySpawned)
                if (p.CurJobDef == RPFaithDefOf.RP_Pray && FaithUtility.InChapel(p)) { worshipper = p; break; }
            if (worshipper == null) return null;
            if (!CellFinder.TryFindRandomCellNear(worshipper.Position, pawn.Map, 4,
                    c => pm.zones.ZoneAt(c) == RPFaithDefOf.RP_Chapel && c.Standable(pawn.Map) && c.GetFirstPawn(pawn.Map) == null && pawn.CanReach(c, PathEndMode.OnCell, Danger.Some), out IntVec3 spot))
                return null;
            Job job = JobMaker.MakeJob(RPFaithDefOf.RP_LeadService, spot);
            job.expiryInterval = GenDate.TicksPerHour * 2;
            return job;
        }
    }

    public class JobDriver_LeadService : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // The chaplain at a funeral (the job of its own def, which does not give way to anything casual): the service is there for the funeral's hour, with no praying inmate needed to keep it going.
            if (job.def == RPFuneralDefOf.RP_LeadFuneral)
            {
                AddFailCondition(() => Funerals.GatheringFor(pawn) == null);
                yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
                Toil service = ToilMaker.MakeToil("LeadTheFuneral");
                service.defaultCompleteMode = ToilCompleteMode.Never;
                service.handlingFacing = true;
                service.socialMode = RandomSocialMode.Off;
                service.tickIntervalAction = delta =>
                {
                    pawn.rotationTracker.FaceCell(job.targetB.Cell);
                    pawn.skills?.Learn(SkillDefOf.Social, 0.04f * delta);
                };
                yield return service;
                yield break;
            }
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil lead = Toils_General.Wait(GenDate.TicksPerHour * 2);
            lead.tickIntervalAction = delta =>
            {
                pawn.skills?.Learn(SkillDefOf.Social, 0.04f * delta);
                if (pawn.IsHashIntervalTick(2500, delta) && !PrayersPresent()) ReadyForNextToil();
            };
            yield return lead;
        }

        private bool PrayersPresent()
        {
            foreach (Pawn p in pawn.Map.mapPawns.PrisonersOfColonySpawned)
                if (p.CurJobDef == RPFaithDefOf.RP_Pray && FaithUtility.InChapel(p) && p.Position.InHorDistOf(pawn.Position, 12f)) return true;
            return false;
        }
    }

    [DefOf]
    public static class RPFaithDefOf
    {
        public static NeedDef RP_Faith;
        public static PrisonZoneDef RP_Chapel;
        public static JobDef RP_Pray;
        public static JobDef RP_LeadService;

        static RPFaithDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPFaithDefOf));
    }
}
