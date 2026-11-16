using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks and test helpers from stability round 29 (see WORKLOG): the held scenes (the vocation and escort scenes' inmates, the post probes' guards), what a cut-short drug-test round owes, and the line that says what
    /// ended a job a premise stands on. Each runs in its own <see cref="Guarded"/> scope from <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        private static void S29Fail(string check, string what) => problems0.Add($"stability 29 ({check}): {what}");

        /// <summary>The pawn whose job ends are written down (see <see cref="S29WatchEnds"/>) and what was written.</summary>
        private static Pawn s29Watched;
        private static readonly List<string> s29Ends = new List<string>();

        /// <summary>
        /// From now on every job that ends on this pawn is written down - the job, how it ended, the tick, and the calls that ended it - until the rollback (<see cref="Patch_JobTracker_CleanupCurrentJob_S29Watch"/>): a premise
        /// that stands on a job (a walk begun, a wait held) says in its failure line what ended the job, not only that it is gone.
        /// </summary>
        private static void S29WatchEnds(Pawn p, Rollback r)
        {
            s29Watched = p;
            s29Ends.Clear();
            r.Add(() =>
            {
                s29Watched = null;
                s29Ends.Clear();
            });
        }

        /// <summary>The ends written down for the watched pawn, for a failure line.</summary>
        private static string S29Ends(Pawn p) =>
            s29Watched != p ? "no job ends watched" : s29Ends.Count == 0 ? "no job of theirs ended in the window" : "jobs of theirs that ended: " + string.Join(" | ", s29Ends);

        internal static bool S29Watches(Pawn p) => p != null && p == s29Watched && s29Ends.Count < 6;

        internal static void S29NoteEnd(Pawn p, Job job, JobCondition condition, string trace)
        {
            if (!S29Watches(p) || job == null) return;
            s29Ends.Add($"{job.def.defName} {condition} at tick {Find.TickManager.TicksGame} ({Find.TickManager.TicksGame - job.startTick} after it began, by {trace})");
        }

        // ---- the scenes hold their people --------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The vocation scenes' inmates are held against the wardens' take-to-bed work and the routine frisk (<see cref="HoldInmate"/>): a warden carried the tailor off in the middle of the bench's cycle (s28neg2: "job
        /// Carried"). The scene's inmate is held from the moment it is made, and the hold goes with the rollback.
        /// </summary>
        private static void CheckVocationInmatesAreHeld(Map map)
        {
            Guarded("stability 29: a vocation scene's inmate is held against the wardens", map, r =>
            {
                Pawn p = VocationInmate(map, r, "held29");
                if (!DrugTestInmateHeld(p)) S29Fail("vocation scene", "the scene's inmate is not held against the wardens' take-to-bed work and the routine frisk");
            });
            // (After the rollback of the scene the hold is gone with it: a list that kept the pawn would grow with every check.)
            Pawn gone = null;
            Guarded("stability 29: a vocation scene's hold ends with the scene", map, r =>
            {
                gone = VocationInmate(map, r, "gone29");
            });
            if (gone != null && DrugTestInmateHeld(gone)) S29Fail("vocation scene", "the scene's inmate is still held after the scene was rolled back");
        }

        /// <summary>
        /// A post probe's guard is offered the post and the patrol by the ordinary work node, and on the emergency node only the answer to trouble (the check's own rowdy) and work that is not the prison's Warden work: the
        /// rescue and the carry of an inmate are not (<see cref="Patch_JobGiver_Work_PostSceneGuards"/>), so that no real inmate's downing takes a scene guard from the post (s28neg2: "job TakeWoundedPrisonerToBed"). The
        /// patch's own answer is asked, with a scene guard and with a real one (whom the hold leaves alone).
        /// </summary>
        private static void CheckPostSceneGuardsAreNotCalledToTheWardensWork(Map map)
        {
            Guarded("stability 29: a post scene's guard is not called to rescue or carry on the emergency node", map, r =>
            {
                const string check = "post guards held";
                var scene = new PostScene(map, r);
                Pawn guard = scene.Guard(IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
                Pawn real = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                var emergency = new JobGiver_Work { emergency = true };
                var ordinary = new JobGiver_Work();
                WorkGiver Of(string def) => DefDatabase<WorkGiverDef>.GetNamedSilentFail(def)?.Worker;
                bool Allowed(JobGiver_Work node, Pawn who, WorkGiver giver)
                {
                    bool result = true;
                    Patch_JobGiver_Work_PostSceneGuards.Postfix(node, who, giver, ref result);
                    return result;
                }
                WorkGiver rescue = Of("RP_RescueInmate"), punish = Of("RP_TakeToPunishment"), trouble = Of("RP_RespondToTrouble"), post = Of("RP_ManPost"), patrol = Of("RP_Patrol");
                if (rescue == null || punish == null || trouble == null)
                {
                    S29Fail(check, $"premise: a work giver is missing (rescue {rescue != null}, take to solitary {punish != null}, respond to trouble {trouble != null})");
                    return;
                }
                if (Allowed(emergency, guard, rescue) || Allowed(emergency, guard, punish))
                    S29Fail(check, "a scene guard may use the rescue or the carry to solitary on the emergency node: a real inmate's downing takes them from the post");
                if (!Allowed(emergency, guard, trouble)) S29Fail(check, "a scene guard may not answer trouble on the emergency node: the emergency check's rowdy goes unanswered");
                if (Of("FightFires") is { } fires && !Allowed(emergency, guard, fires)) S29Fail(check, "a scene guard may not fight a fire on the emergency node");
                if (post != null && !Allowed(ordinary, guard, post)) S29Fail(check, "a scene guard is not offered the post on the ordinary node");
                if (patrol != null && !Allowed(ordinary, guard, patrol)) S29Fail(check, "a scene guard is not offered the patrol on the ordinary node");
                if (Allowed(ordinary, guard, rescue)) S29Fail(check, "a scene guard may use the rescue on the ordinary node");
                if (!Allowed(emergency, real, rescue) || !Allowed(ordinary, real, rescue)) S29Fail(check, "a real guard was held from the rescue by the scenes' hold");
            });
        }

        // ---- a guard sent to a far post arrives --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard sent to a post 10 or more cells away stands on it within the walk's time (<see cref="JobDriver_ManPost"/>: the 30-tick look at whether the guard may hold the post runs through the walk too, so a walk it
        /// cut would be given again from where the guard stands, never finish, and fail here). The walk is timed from the pathfinder's own cost for it, twice over; the failure line names the walk, the jobs of the guard that
        /// ended and what ended them. (Round 2 of stability 29: the handover check judged its guard at the tick the job was given and said the walk was cut; the walk was never waited for. The product's walk is judged here, with a walk
        /// long enough to cross the end condition's looks and the job's own 120-tick look.)
        /// </summary>
        private static void CheckPostGuardWalksFarAndStands(Map map)
        {
            Guarded("stability 29: a guard sent to a post 10+ cells off stands on it within the walk's time", map, r =>
            {
                const string check = "far walk";
                if (!PostSetup(map, r, check, 1, out PostScene s, out CompGuardPost post, out List<Pawn> guards, 10f, 16f)) return;
                Pawn guard = guards[0];
                float crow = guard.Position.DistanceTo(post.parent.Position);
                if (crow < 10f)
                {
                    RPLog.Message($"[RimPrison:test] stability 29: no start 10 cells from the post on this map (the guard is {crow:0} off), the far walk is not judged");
                    return;
                }
                S29WatchEnds(guard, r);
                s.Open(post);
                s.Man(post, out bool _, guard);
                if (post.Stander != guard)
                {
                    S29Fail(check, $"premise: the guard was not sent to the post within 1500 ticks (the scene's guards are held): {PostWhy(map, post)}; {S29Ends(guard)}");
                    return;
                }
                using PawnPath path = map.pathFinder.FindPathNow(guard.Position, post.parent.Position, TraverseParms.For(guard, Danger.Some));
                int cells = path.Found ? path.NodesLeftCount : 0, budget = (int)(path.TotalCost * 2f) + 2 * JobDriver_ManPost.HoldCheckTicks;
                if (!path.Found)
                {
                    S29Fail(check, $"premise: no path from the guard to the post: {PostWhy(map, post)}");
                    return;
                }
                int ticks = s.Run(() => PostScene.Standing(post), budget, () => s.Open(post));
                if (!PostScene.Standing(post) || post.Stander != guard)
                    S29Fail(check, $"the guard {guard.LabelShort}, sent to a post {crow:0} cells off ({cells} cells by path, cost {path.TotalCost:0}), was not on it after {ticks} of {budget} ticks: {PostWhy(map, post)}; {S29Ends(guard)}");
                else
                    RPLog.Message($"[RimPrison:test] stability 29: the guard stood on a post {crow:0} cells off ({cells} by path, cost {path.TotalCost:0}) after {ticks} ticks of {budget}; {S29Ends(guard)}");
            });
        }

        // ---- a cut-short round owes the kit, not what the doctor's next job holds ------------------------------------------------------------------------------------

        /// <summary>
        /// A round that is cut short is judged on the kit: a doctor who goes on to an ordinary haul after it ("still carrying WakeUp28412 ... job HaulToCell", stability28-s28d) carries something the round never picked up. The
        /// judgement (<see cref="DtCarriesKit"/>) says kit for a kit and not for anything else.
        /// </summary>
        private static void CheckDrugTestCutRoundJudgesTheKit(Map map)
        {
            Guarded("stability 29: a cut-short round is judged on the kit in the doctor's hands, not on any thing", map, r =>
            {
                Pawn doctor = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor);
                doctor.carryTracker.innerContainer.ClearAndDestroyContents();
                Thing other = ThingMaker.MakeThing(ThingDefOf.Silver);
                other.stackCount = 2;
                if (doctor.carryTracker.TryStartCarry(other, 2) != 2)
                {
                    S29Fail("kit judgement", "premise: the doctor could not take up a stack of silver");
                    return;
                }
                if (DtCarriesKit(doctor)) S29Fail("kit judgement", "a doctor carrying a stack of silver is judged to carry a kit");
                doctor.carryTracker.innerContainer.ClearAndDestroyContents();
                Thing kit = ThingMaker.MakeThing(DrugTesting.Kit);
                if (doctor.carryTracker.TryStartCarry(kit, 1) != 1)
                {
                    S29Fail("kit judgement", "premise: the doctor could not take up a kit");
                    return;
                }
                if (!DtCarriesKit(doctor)) S29Fail("kit judgement", "a doctor carrying a kit is not judged to carry one");
                doctor.carryTracker.innerContainer.ClearAndDestroyContents();
            });
        }

        // ---- the job-end line --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The line a premise failure reads names what ended the watched pawn's job: the job's end is written down however it ends (a new job's start included), and only for the watched pawn.</summary>
        private static void CheckJobEndsAreWrittenDown(Map map)
        {
            Guarded("stability 29: the ends of a watched pawn's jobs are written down", map, r =>
            {
                Pawn watched = Throwaway(map, r), other = Throwaway(map, r);
                S29WatchEnds(watched, r);
                Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                wait.expiryInterval = 600;
                watched.jobs.StartJob(wait, JobCondition.InterruptForced);
                other.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture), JobCondition.InterruptForced);
                other.jobs.StopAll();
                if (s29Ends.Count != 0) S29Fail("job ends", $"a job of a pawn that is not watched was written down ({S29Ends(watched)})");
                Job next = JobMaker.MakeJob(JobDefOf.Wait);
                next.expiryInterval = 600;
                watched.jobs.StartJob(next, JobCondition.InterruptForced);
                if (!S29Ends(watched).Contains(JobDefOf.Wait_MaintainPosture.defName))
                    S29Fail("job ends", $"the end of the watched pawn's job was not written down when another job took its place ({S29Ends(watched)})");
                watched.jobs.StopAll();
            });
        }
    }

    /// <summary>
    /// Test diagnostics: the end of a watched pawn's current job (<see cref="AutoTest.S29WatchEnds"/>), however it comes - a job ended, or one started in its place (the job tracker cleans up the old job in both) - with the
    /// condition and the calls that did it.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    [TestOnly]
    public static class Patch_JobTracker_CleanupCurrentJob_S29Watch
    {
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition, Pawn ___pawn)
        {
            if (!AutoTest.Active || !AutoTest.S29Watches(___pawn) || __instance.curJob == null) return;
            AutoTest.S29NoteEnd(___pawn, __instance.curJob, condition, string.Join(" < ", new System.Diagnostics.StackTrace(2, false).GetFrames().Take(10).Select(f => f.GetMethod()?.Name)));
        }
    }
}
