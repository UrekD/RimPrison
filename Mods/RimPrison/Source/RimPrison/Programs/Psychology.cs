using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The psychologist: assesses every new inmate once (hidden reputations and gang ties come to light) and gives
    /// therapy to tense inmates (tension down, a short mood lift; once per inmate every two days).
    /// </summary>
    public static class PsychologyUtility
    {
        public const float TherapyAbove = 45f;
        public const int TherapyCooldown = GenDate.TicksPerDay * 2;
        public const float AssessRevealChance = 0.6f;
        public const float TherapyRevealChance = 0.25f;

        public static bool CanTalk(Pawn inmate) =>
            inmate.Spawned && inmate.Awake() && !inmate.Downed && !inmate.InMentalState && inmate.Inmate() is { activeFlag: true, processed: true };

        public static bool WantsSession(CompInmate c) =>
            !c.assessed || (c.tension >= TherapyAbove && Find.TickManager.TicksGame - c.lastTherapyTick >= TherapyCooldown) || PsychWing.ReviewDue(c) || WatchList.AssessmentDue(c);

        public static void Complete(Pawn psychologist, Pawn inmate)
        {
            CompInmate c = inmate.Inmate();
            if (c == null) return;
            PrisonGame.Instance.stats.therapySessions++;
            bool first = !c.assessed;
            // Each hidden fact comes out with a chance: likely at the first assessment, sometimes in later sessions.
            float chance = c.assessed ? TherapyRevealChance : AssessRevealChance;
            c.assessed = true;
            c.psychSessions++;
            var found = new List<string>();
            foreach (ReputationDef r in c.reputations)
                if (r.hidden && !c.revealed.Contains(r) && Rand.Chance(chance))
                {
                    c.revealed.Add(r);
                    found.Add(r.label);
                }
            if (c.gang != null && !c.gangKnown && Rand.Chance(chance))
            {
                GangUtility.Reveal(c);
                found.Add(c.gang.label);
            }
            // (A member of a smuggling ring may let on about it.)
            RingSystem.Notify_Assessed(inmate, chance);
            if (found.Count > 0)
                Messages.Message("RP_AssessmentFound".Translate(inmate.LabelShort, string.Join(", ", found)), inmate, MessageTypeDefOf.NeutralEvent);
            if (c.tension >= TherapyAbove * 0.8f)
            {
                c.tension = Mathf.Max(0f, c.tension - 15f);
                c.lastTherapyTick = Find.TickManager.TicksGame;
                inmate.needs.mood?.thoughts.memories.TryGainMemory(RPPsychDefOf.RP_TherapyHelped);
            }
            psychologist.skills?.Learn(SkillDefOf.Social, 300f);
            // (A trigger and a day of very low mood: the assessment flags them at risk, see WatchList.)
            WatchList.Notify_Session(psychologist, inmate, c);
            // (Two breaks in ten days, or a trait that the first assessment finds: the psych wing.)
            PsychWing.Review(inmate, c, first);
        }
    }

    public class WorkGiver_Therapy : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Therapy(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !forced && InmateCandidates.Shared.Therapy(pawn.Map).Count == 0;

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the psychologist (able to talk, due a session).</summary>
        public static bool Candidate(Pawn inmate) => PsychologyUtility.CanTalk(inmate) && PsychologyUtility.WantsSession(inmate.Inmate());

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate)
            && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPPsychDefOf.RP_Therapy, t);
    }

    public class JobDriver_Therapy : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !PsychologyUtility.CanTalk(Inmate));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil talk = Toils_General.Wait(1200);
            talk.handlingFacing = true;
            // The inmate sits down for the session (unless they are busy with something that must not be cut).
            talk.initAction = () =>
            {
                if (Inmate.CurJob is not { } cur || cur.def.casualInterruptible)
                    Inmate.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 1250), JobCondition.InterruptForced);
            };
            talk.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            talk.WithProgressBarToilDelay(TargetIndex.A);
            // The inmate walks off anyway (regime change, emergency): the session is over.
            talk.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, 3f));
            yield return talk;
            Toil done = ToilMaker.MakeToil("TherapyDone");
            done.initAction = () => PsychologyUtility.Complete(pawn, Inmate);
            yield return done;
        }
    }

    [DefOf]
    public static class RPPsychDefOf
    {
        public static JobDef RP_Therapy;
        public static ThoughtDef RP_TherapyHelped;

        static RPPsychDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPPsychDefOf));
    }
}
