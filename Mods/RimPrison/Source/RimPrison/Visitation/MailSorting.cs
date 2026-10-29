using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Guards with nothing more urgent sort the post at a table in the mailroom.</summary>
    public class WorkGiver_SortMail : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !GuardUtility.IsGuard(pawn) || PrisonGame.Instance == null || !PrisonGame.Instance.mail.AnyWaiting(pawn.Map, pawn);

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => MailSystem.Tables(pawn.Map);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            PrisonGame.Instance.mail.AnyWaiting(pawn.Map, pawn) && !t.IsForbidden(pawn) && pawn.CanReserveAndReach(t, PathEndMode.Touch, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPMailDefOf.RP_SortMail, t);
    }

    /// <summary>
    /// Walk to the table, go through the post (longer when every letter is read), hand the letter on, and carry on while there
    /// is post for anyone else, up to <see cref="LettersPerTrip"/> letters and <see cref="MaxSortingTicks"/> at the table (a walk to the mailroom for one
    /// letter at a time left a big prison's post piling up). The job is not casually interruptible, so it ends itself when an alarm
    /// sounds that this guard would be sent to (<see cref="GuardUtility.AlarmFor"/>: a riot, an escape, a fight; checked every <see cref="AlarmCheckTicks"/>,
    /// walking and sorting alike; the letter in hand waits on the table), and the cap bounds the time for everything else (an inmate who owes
    /// punishment waits for the trip to end; ending trips for them would leave the post unsorted on a day with many of them).
    /// </summary>
    public class JobDriver_SortMail : JobDriver
    {
        public const int LettersPerTrip = 6;
        /// <summary>The longest a trip spends at the table (the walk there is not counted: alarms end that too, and a capped walk made every letter a fresh trip across the prison): no new letter is started that would end past it (an hour of game time, 42 s at normal speed; alarms end it sooner, so
        /// the cap only bounds a quiet hour: half an hour left a 48-cell prison's post piling up).</summary>
        public const int MaxSortingTicks = 2500;
        public const int AlarmCheckTicks = 30;

        private Thing Table => job.targetA.Thing;
        private int sorted;
        /// <summary>When the first letter of this trip was started (-1: not yet).</summary>
        private int sortingSince = -1;

        /// <summary>Another letter this trip: only if it would be sorted within <see cref="MaxSortingTicks"/> of the first one's start.</summary>
        public static bool MayStartLetter(int sortingTicks, MailPolicy policy) => sortingTicks + MailSystem.SortTicks(policy) <= MaxSortingTicks;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => !PrisonGame.Instance.mail.AnyWaiting(pawn.Map, pawn));
            this.AddEndCondition(() =>
            {
                if (!pawn.IsHashIntervalTick(AlarmCheckTicks) || GuardUtility.AlarmInmate(pawn) is not { } alarm) return JobCondition.Ongoing;
                if (AutoTest.Active) RPLog.Message($"[RimPrison:mailtrip] alarm ends {pawn.LabelShort}'s trip after {Find.TickManager.TicksGame - job.startTick} ticks, {sorted} letters: {alarm.LabelShort} {alarm.MentalStateDef?.defName} at {alarm.Position}, guard at {pawn.Position}");
                return JobCondition.InterruptOptional;
            });
            this.AddFinishAction(c =>
            {
                if (!AutoTest.Active) return;
                if (pawn.IsPrisonerOfColony) AutoTest.Notify_ClerkTrip(sorted);
                RPLog.Message($"[RimPrison:mailtrip] {(pawn.IsPrisonerOfColony ? "clerk" : "guard")} {pawn.LabelShort}'s trip ends {c} after {Find.TickManager.TicksGame - job.startTick} ticks, {sorted} letters, {PrisonGame.Instance.mail.waiting.Count} waiting");
            });
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil sort = Toils_General.Wait(MailSystem.SortTicks(PrisonGame.Instance.mailPolicy));
            Action wait = sort.initAction;
            sort.initAction = () =>
            {
                if (sortingSince < 0) sortingSince = Find.TickManager.TicksGame;
                wait();
            };
            sort.handlingFacing = true;
            sort.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(Table);
                pawn.skills?.Learn(SkillDefOf.Intellectual, 0.05f * delta);
            };
            sort.WithProgressBarToilDelay(TargetIndex.A);
            yield return sort;
            Toil done = ToilMaker.MakeToil("MailSorted");
            done.initAction = () =>
            {
                sorted++;
                PrisonGame.Instance.mail.SortNext(pawn);
            };
            yield return done;
            yield return Toils_Jump.JumpIf(sort, () => sorted < LettersPerTrip && MayStartLetter(Find.TickManager.TicksGame - sortingSince, PrisonGame.Instance.mailPolicy)
                && PrisonGame.Instance.mail.AnyWaiting(pawn.Map, pawn));
        }
    }

    /// <summary>
    /// Mailroom duty: a trusted inmate (minimum security) sorts the post, so the guards need not. Like the kitchen, the
    /// mailroom is behind an Auto door that trusted workers may pass during work hours; without one there is no slot.
    /// </summary>
    public class InmateJobWorker_Mailroom : InmateJobWorker
    {
        public override int Capacity(Map map) => MailSystem.InmateClerkSlots(map);

        /// <summary>Post waits for them to sort: the clerk's shift is the hours the letters wait for (a class started in it took the clerk away from the table).</summary>
        public override bool HasWork(Pawn inmate) => PrisonGame.Instance.mail.AnyWaiting(inmate.Map, inmate);

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            if (!PrisonGame.Instance.mail.AnyWaiting(inmate.Map, inmate)) return null;
            foreach (Thing table in MailSystem.Tables(inmate.Map))
                if (inmate.CanReserveAndReach(table, PathEndMode.Touch, Danger.Some)) return JobMaker.MakeJob(RPMailDefOf.RP_SortMail, table);
            return null;
        }
    }
}
