using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks from stability round 28 (see WORKLOG): the held scenes of the tool count and the break, a throwaway's mental-state conditions, and the post's watch across its hour. Each runs in its own <see cref="Guarded"/>
    /// scope from <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        private static void S28Fail(string check, string what) => problems0.Add($"stability 28 ({check}): {what}");

        /// <summary>
        /// What hurts a pawn who is down, as health conditions (<c>Heart attack</c>, an injury, an illness), or null when nothing on them does: a downed pawn with no condition on them is not hurt by anything this knows of, the scene
        /// is broken. A condition is outside the check's doing: no check here gives one (c88-ev2: <c>Heart attack</c> on a scene guard, a random incident of the game, and the guard lay in <c>Wait_Downed</c> for 2322 ticks).
        /// </summary>
        private static string DownedBy(Pawn p)
        {
            if (p?.health?.hediffSet is not { } set) return null;
            List<string> hurt = set.hediffs.Where(h => h.def.isBad && h.Visible).Select(h => h.Part != null ? $"{h.LabelCap} ({h.Part.Label})" : h.LabelCap.ToString()).ToList();
            return hurt.Count == 0 ? null : string.Join(", ", hurt);
        }

        /// <summary>
        /// The probes' scene guards are held (<see cref="Patch_JobGiver_Work_PostSceneGuards"/>: no rescue, carry or watch of a real inmate), so a probe that cannot judge is the hold failing, and fails with where the guards are
        /// (a probe that returned with a line in the log was a probe that never ran, stability 28 round 2: the guard on <c>TakeWoundedPrisonerToBed</c>).
        /// </summary>
        private static void S28NotJudged(string check, string why, Map map, CompGuardPost post, Pawn watched = null) =>
            S28Fail(check, $"premise: {why}; the scene's guards are held, so this is not judged and not told: {PostWhy(map, post)}{(watched != null ? "; " + S29Ends(watched) : "")}{(watched is { Downed: true } ? $"; the guard is down, hurt by: {DownedBy(watched) ?? "nothing on their health"}" : "")}");

        /// <summary>
        /// A guard's watch at a post is a job that waits an hour (<see cref="JobDriver_ManPost"/>) and is given again by the work scan: the night's pass (<see cref="EscapePlans.Catcher"/>) finds a catcher only in a guard who has
        /// that job at that tick, so the guard is looked at at the end of every real tick of an hour and a bit - the state the next tick's hourly pass finds - and the post must be manned at each. A tick unmanned with the guard
        /// idle is the watch lapsing (<c>break (caught in the act)</c> in trusty-t7d: "no subdue" at a manned post, the real ticks run exactly one hour, the length of the watch); the guard called away to other work is told, not
        /// failed. The job's own renewal is told with the tick it fell on.
        /// </summary>
        private static void CheckPostWatchAcrossTheHour(Map map)
        {
            Guarded("stability 28: a manned post stays manned through the watch's hour (real ticks)", map, r => PostWatchHour(map, r, hurt: false));
            // (The same hour with the guard hurt down at its start: a guard down from a condition of their own is told as not judged, and not counted as the post unmanned or a call to other work - c88-ev2.)
            Guarded("stability 30: a guard who goes down in the watch is told, not failed", map, r => PostWatchHour(map, r, hurt: true));
        }

        private static void PostWatchHour(Map map, Rollback r, bool hurt)
        {
            string check = hurt ? "the post's watch, guard hurt" : "the post's watch";
            {
                if (!PostSetup(map, r, check, 1, out PostScene s, out CompGuardPost post, out List<Pawn> guards)) return;
                Pawn guard = guards[0];
                S29WatchEnds(guard, r);
                s.Open(post);
                s.Man(post, out bool _, guard);
                s.Run(() => PostScene.Standing(post), 1500, () => s.Open(post));
                if (!PostScene.Standing(post) || post.Stander != guard)
                {
                    S28NotJudged(check, "the scene guard did not stand at the post, the watch across the hour is not judged", s.map, post, guard);
                    return;
                }
                // (The job's id is read before the guard is hurt: a guard hurt down has no job, and reading it after threw in s30a, s30b and s30neg - "stability 30: ... threw System.NullReferenceException".)
                int first = guard.CurJob?.loadID ?? -1;
                if (hurt) HealthUtility.DamageUntilDowned(guard, allowBleedingWounds: false);
                int tick = 0, gapFrom = -1, gapTicks = 0, renewedAt = -1, calledAway = 0, downedAt = -1;
                string gapJob = null;
                var awayJobs = new HashSet<string>();
                // (A guard who goes down ends the watch: their lying there is neither the post unmanned nor a call to other work, c88-ev2 "Wait_Downed" - the run stops at the tick they go down.)
                int ran = s.Run(() => downedAt >= 0, GenDate.TicksPerHour + 100, () => s.Open(post), () =>
                {
                    tick++;
                    if (renewedAt < 0 && guard.CurJob?.loadID != first) renewedAt = tick;
                    if (guard.Downed)
                    {
                        downedAt = tick;
                        return;
                    }
                    if (post.Stander == guard && guard.Position == post.parent.Position) return;
                    string job = guard.CurJobDef?.defName ?? "none";
                    if (guard.CurJobDef is null || guard.CurJobDef == RPSecurityDefOf.RP_Patrol || guard.CurJobDef == JobDefOf.Wait_Wander || guard.CurJobDef == JobDefOf.GotoWander || guard.CurJobDef == JobDefOf.Wait
                        || guard.CurJobDef == JobDefOf.Wait_MaintainPosture)
                    {
                        if (gapFrom < 0) (gapFrom, gapJob) = (tick, job);
                        gapTicks++;
                    }
                    else
                    {
                        calledAway++;
                        awayJobs.Add(job);
                    }
                });
                if (hurt && downedAt < 0) S28Fail(check, $"premise: the guard {guard.LabelShort} was hurt until down and was not down in {ran} ticks (job {guard.CurJobDef?.defName ?? "none"})");
                if (gapTicks > 0)
                    S28Fail(check, $"the guard {guard.LabelShort} left the post unmanned for {gapTicks} tick(s) of {ran}, from tick {gapFrom} (job {gapJob}; the watch's job was replaced at tick {(renewedAt < 0 ? "-" : renewedAt.ToString())}; {s.HourText})");
                if (calledAway > 0)
                    S28Fail(check, $"premise: the guard {guard.LabelShort} was called away to other work for {calledAway} of {ran} ticks ({string.Join(", ", awayJobs)}) though the scene's guards are held, so the watch was not judged ({s.HourText})");
                if (downedAt >= 0)
                {
                    // (Down from a health condition of their own, outside the check: the rest of the hour is not judged, and said so. Down with nothing on their health to say why is the scene broken.)
                    string by = DownedBy(guard);
                    if (by == null) S28Fail(check, $"the guard {guard.LabelShort} went down at tick {downedAt} of the watch with no health condition that hurts them (job {guard.CurJobDef?.defName ?? "none"}; {S29Ends(guard)}; {s.HourText})");
                    else RPLog.Message($"[RimPrison:test] stability 28: the watch was not judged past tick {downedAt} of {GenDate.TicksPerHour + 100}: the guard {guard.LabelShort} went down, hurt by {by} (outside the check; {s.HourText})");
                    return;
                }
                RPLog.Message($"[RimPrison:test] stability 28: the post stayed manned for {ran} real ticks (job renewed at tick {(renewedAt < 0 ? "never" : renewedAt.ToString())}, unmanned {gapTicks}, called away {calledAway}; {s.HourText})");
            }
        }

        /// <summary>
        /// Round 2: the watch carries on while the guard may hold the post and ends at once when another should have it, not on a timer (<see cref="JobDriver_ManPost"/>). A guard stands at an open post; another, free
        /// and on shift, is named for it: the one standing is off it within a few looks of the watch (<see cref="JobDriver_ManPost.HoldCheckTicks"/>, not at the hour's end: the check fails if the watch never ends) and the named one
        /// is sent. Then the post's hours end: the named guard leaves it within the same few looks. Told, not failed, when the scene's guards were taken by other work (a real guard's, a frisk's).
        /// </summary>
        private static void CheckPostWatchEndsForANamedGuard(Map map)
        {
            const string check = "the post's handover";
            Guarded("stability 28: a watch ends when a named guard should take the post, and when the post closes", map, r =>
            {
                if (!PostSetup(map, r, check, 2, out PostScene s, out CompGuardPost post, out List<Pawn> guards)) return;
                Pawn first = guards[0], named = guards[1];
                S29WatchEnds(first, r);
                s.Open(post);
                s.Man(post, out bool _, first);
                // (Man returns when the post has someone on it, that is when the guard is sent: the walk is waited out, as the watch check above does. This line was missing, and the guard was judged at the tick the job was given,
                // 3 cells off, in every run: "work node asked 2x, last gave RP_ManPost 0 ticks ago", s29a-c and s28neg2.)
                s.Run(() => PostScene.Standing(post), 1500, () => s.Open(post));
                if (!PostScene.Standing(post) || post.Stander != first)
                {
                    S28NotJudged(check, "the first scene guard did not stand at the post, the handover is not judged", s.map, post, first);
                    return;
                }
                // (Some real guard may be free and nearer, but a named guard who is free and able goes first: if they are not free, the premise is not there.)
                if (!GuardPosts.FreeAndAble(named, post))
                {
                    S28NotJudged(check, $"the guard to be named is not free for the post (job {named.CurJobDef?.defName ?? "none"}), the handover is not judged", s.map, post, first);
                    return;
                }
                const int looks = 3 * JobDriver_ManPost.HoldCheckTicks;
                // With no name the one standing keeps the post, a free guard beside them or not (the control: the watch does not end for nothing, and two guards do not hand it back and forth).
                int held = s.Run(() => !PostScene.OnPost(first, post), 4 * looks, () => s.Open(post));
                if (!PostScene.OnPost(first, post))
                {
                    if (GuardPosts.IdleOrPosted(first)) S28Fail(check, $"with nobody named, the guard {first.LabelShort} left the post after {held} ticks (job {first.CurJobDef?.defName ?? "none"}; {s.HourText})");
                    else S28NotJudged(check, $"the standing guard was called to {first.CurJobDef?.defName ?? "none"} before the name, the handover is not judged", s.map, post, first);
                    return;
                }
                post.assigned = named;
                int left = s.Run(() => !PostScene.OnPost(first, post), looks, () => s.Open(post));
                bool stillThere = PostScene.OnPost(first, post);
                if (stillThere)
                    S28Fail(check, $"the guard {first.LabelShort} was still on the post {left} ticks after {named.LabelShort} was named for it (the watch never ends: job {first.CurJobDef?.defName ?? "none"}; {s.HourText})");
                S29WatchEnds(named, r);
                int sent = s.Run(() => post.Stander == named, 600, () => s.Open(post));
                if (post.Stander != named)
                    RPLog.Message($"[RimPrison:test] stability 28: the named guard was not sent within {sent} ticks of the first leaving (stander {post.Stander?.LabelShort ?? "nobody"}, named free {GuardPosts.IdleOrPosted(named)}), the take-over told and not failed");
                else
                {
                    s.Run(() => PostScene.Standing(post), 1500, () => s.Open(post));
                    if (!PostScene.Standing(post) || post.Stander != named)
                    {
                        S28NotJudged(check, "the named guard did not arrive, the post's closing is not judged", s.map, post, named);
                        return;
                    }
                    // The post's hours end while they stand: the watch ends within a few looks.
                    s.Closed(post);
                    int closed = s.Run(() => !PostScene.OnPost(named, post), looks, () => s.Closed(post));
                    if (PostScene.OnPost(named, post))
                        S28Fail(check, $"the guard {named.LabelShort} was still on the post {closed} ticks after its hours ended (open {post.OpenNow}; {s.HourText})");
                }
                RPLog.Message($"[RimPrison:test] stability 28: the first guard left {left} ticks after the name, the named guard {(post.Stander == named ? "was sent" : "was not sent")}");
            });
        }

        /// <summary>
        /// The scenes' people are the scenes': the tool count's worker (a real guard carried them to a bed in the middle of the proof's count, trusty-t5d) is held against the wardens' take-to-bed work and the routine frisk,
        /// as the drug-test scenes' inmates are (<see cref="HoldInmate"/>), and a throwaway's conditions that start mental states are taken off (dementia, which had the burial's workman wandering about confused, trusty-t6b)
        /// while a condition with none stays. The control: with the hold off, the frisk work is offered the same worker.
        /// </summary>
        private static void CheckScenesHoldTheirPeople(Map map)
        {
            Guarded("stability 28: a scene's inmate is held against the wardens, a throwaway's dementia is taken off", map, r =>
            {
                const string check = "held scenes";
                ToolScene t = ToolScene.Build(map, r, check);
                if (t == null) return;
                Pawn worker = t.Worker("Held");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (!DrugTestInmateHeld(worker))
                    S28Fail(check, "the tool count's worker is not held against the wardens' take-to-bed work and the routine frisk");
                else
                {
                    // (The routine frisk's roll decided, the worker out in the open and due: the guard is offered them with the hold off, and not with it on.)
                    var giver = new WorkGiver_Frisk();
                    bool? roll0 = WorkGiver_Frisk.RoutineRollForTests;
                    WorkGiver_Frisk.RoutineRollForTests = true;
                    r.Add(() => WorkGiver_Frisk.RoutineRollForTests = roll0);
                    bool held = giver.HasJobOnThing(guard, worker, true);
                    dtInmates.Remove(worker);
                    bool control = giver.HasJobOnThing(guard, worker, true);
                    HoldInmate(worker, r);
                    WorkGiver_Frisk.RoutineRollForTests = roll0;
                    if (!control) RPLog.Message("[RimPrison:test] stability 28: the guard is offered no frisk of the scene's worker even with the hold off, the frisk not judged");
                    else if (held) S28Fail(check, "a guard is offered a frisk of the tool count's worker while the worker is held");
                }

                // Dementia is a condition of the brain that starts "wandering around confused" by itself.
                Pawn staff = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                HediffDef dementia = DefDatabase<HediffDef>.GetNamedSilentFail("Dementia"), harmless = HediffDefOf.Malnutrition;
                BodyPartRecord brain = staff.health.hediffSet.GetBrain();
                if (dementia == null || brain == null)
                {
                    RPLog.Message("[RimPrison:test] stability 28: no dementia def or no brain on the throwaway, the mental-state conditions not judged");
                    return;
                }
                staff.health.AddHediff(HediffMaker.MakeHediff(dementia, staff, brain));
                staff.health.AddHediff(harmless);
                NoMentalStateHediffs(staff);
                bool demented = staff.health.hediffSet.HasHediff(dementia), kept = staff.health.hediffSet.HasHediff(harmless);
                if (demented || !kept)
                    S28Fail(check, $"a throwaway's conditions that start mental states were not taken off alone: dementia still there {demented}, a condition with none kept {kept}");
            });
        }
    }
}
