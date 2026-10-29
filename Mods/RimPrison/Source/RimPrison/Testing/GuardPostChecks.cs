using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Guard posts (<see cref="GuardPosts"/>): a post a guard on shift is sent to in its hours, the guard named for it first, an emergency before it, the state's chief over it, a post taken down
    /// releasing its guard, and a guard standing there a watcher. Real ticks on throwaway guards (the post is put up as far from the real guards as the map allows, so that the nearest free guard is
    /// one of these); everything is rolled back. The round trip of a post's hours and guard is in the saveload plan's snapshot (<see cref="SeedGuardPostsForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void GuardPostFail(string what) => problems0.Add("guard posts: " + what);

        /// <summary>
        /// The throwaway guards of the running post scenes. The prison's hourly pass writes a guard's shift into their timetable (a break hour, the commute hour, sleep from 22:00, the night flag of the daily balance) and sends the
        /// off-shift home and the guards with a break hour to the staff room in the same tick; a walked check crosses an hour now and then (the trace of stability10-s10g has the pass inside the two-posts check), and a
        /// guard on a post was off duty for the ticks until <see cref="PostScene.Run"/> wrote the timetable back: the post let go of them (its end condition looks every 30 ticks), the other guard took it and the walk began
        /// again at the end of the wait ("post two: On the way - Brollo ... 17 cells left of 18", stability10-s10b, regime-r1a), or the named guard the check had set off shift was on duty in the window and stood at the post
        /// ("with the named guard off shift, Norma stood at the post", regime-r1a). So the pass does not write a scene guard's shift at all (<see cref="Patch_ShiftSystem_Apply_HeldDoctors"/>, as for the drug-test doctors).
        /// </summary>
        private static readonly List<Pawn> postGuards = new List<Pawn>();

        /// <summary>A throwaway guard of a running post scene, or of a care check that waits for a job to be given (<see cref="careHeld"/>): the hourly pass leaves their shift as the scene wrote it.</summary>
        internal static bool PostGuardHeld(Pawn p) => postGuards.Contains(p) || careHeld.Contains(p);

        /// <summary>A throwaway guard of a running post scene (see <see cref="Patch_JobGiver_Work_PostSceneGuards"/>).</summary>
        internal static bool PostSceneGuard(Pawn p) => postGuards.Contains(p);

        /// <summary>For each throwaway guard of a post scene: how often the ordinary work node was asked for them, what it gave the last time, and when (see <see cref="Patch_JobGiver_Work_PostSceneTrace"/>).</summary>
        private static readonly Dictionary<Pawn, (int asked, string last, int tick)> postThinks = new Dictionary<Pawn, (int, string, int)>();

        internal static void NotePostThink(Pawn p, Job job) =>
            postThinks[p] = (postThinks.TryGetValue(p, out var t) ? t.asked + 1 : 1, job == null ? "nothing" : job.def.defName, Find.TickManager.TicksGame);

        /// <summary>The inmates a post scene has set running amok for its own guards: the only trouble a scene guard answers while a scene runs (see <see cref="Patch_WorkGiver_RespondToTrouble_PostSceneGuards"/>).</summary>
        private static readonly List<Pawn> postRowdies = new List<Pawn>();

        /// <summary>Whether a scene guard may be sent to this inmate: the scene's own rowdy, never the live prison's trouble.</summary>
        internal static bool PostSceneMayRespondTo(Pawn guard, Pawn inmate) => !postGuards.Contains(guard) || postRowdies.Contains(inmate);

        /// <summary>The work node's looks at a guard, for a failure line: how many, and what the last one gave.</summary>
        private static string PostThinks(Pawn p) =>
            postThinks.TryGetValue(p, out var t) ? $"work node asked {t.asked}x, last gave {t.last} {Find.TickManager.TicksGame - t.tick} ticks ago" : "work node never asked";

        /// <summary>
        /// Whether the guard is out of a post scene's way: a running scene is about its own guards, and a real guard on duty is one more who takes a post (the nearer, the one the throwaways give way to
        /// in <see cref="GuardPosts.CanTake"/>), keeps it or walks a long way round the walls to it. See <see cref="Patch_GuardPosts_OnDuty_Scene"/>.
        /// </summary>
        internal static bool PostSceneExcludes(Pawn p) => postGuards.Count > 0 && !postGuards.Contains(p);

        /// <summary>Throwaway guards on shift, and a post to man (up on the cell that is farthest from the real guards the map has).</summary>
        private sealed class PostScene
        {
            public readonly Map map;
            public readonly Rollback r;
            public readonly PrisonMap pm;
            private readonly List<(Pawn guard, bool onShift)> guards = new List<(Pawn, bool)>();
            private readonly List<Pawn> real;
            private int lastHour, hoursCrossed;

            /// <summary>How many times the hour turned while the scene ran (the hourly pass in the middle of a walk).</summary>
            public string HourText => $"the hour turned {hoursCrossed} time(s) in the check, now {Hour}:00";

            public PostScene(Map map, Rollback r)
            {
                this.map = map;
                this.r = r;
                pm = PrisonMap.Of(map);
                real = map.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard).ToList();
                lastHour = Hour;
            }

            public int Hour => GenLocalDate.HourOfDay(map);

            /// <summary>A guard on shift (every hour of the timetable is work) who can fight, at the cell.</summary>
            public Pawn Guard(IntVec3 at)
            {
                Pawn g = null;
                for (int i = 0; i < 8 && (g == null || g.WorkTagIsDisabled(WorkTags.Violent)); i++) g = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guards.Add((g, true));
                postGuards.Add(g);
                postThinks.Remove(g);
                r.Add(() => { postGuards.Remove(g); postThinks.Remove(g); });
                Shift(g, true);
                Place(g, at);
                return g;
            }

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather?.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            /// <summary>On shift (work all day) or off it (asleep all day). Kept so by <see cref="Run"/>: the hourly pass writes the timetable again.</summary>
            public void Shift(Pawn g, bool on)
            {
                int i = guards.FindIndex(x => x.guard == g);
                guards[i] = (g, on);
                Apply(g, on);
            }

            private static void Apply(Pawn g, bool on)
            {
                for (int h = 0; h < 24; h++) g.timetable.SetAssignment(h, on ? TimeAssignmentDefOf.Work : TimeAssignmentDefOf.Sleep);
            }

            /// <summary>The cell farthest from the real guards that a guard can reach from where this one stands and a post can be put up on.</summary>
            public IntVec3 Site(Pawn from)
            {
                IntVec3 best = IntVec3.Invalid;
                float bestAway = -1f;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(from.Position, 30f, true))
                {
                    if (!c.InBounds(map) || !c.Standable(map) || c.GetEdifice(map) != null || c.GetFirstPawn(map) != null) continue;
                    float away = real.Count == 0 ? 99f : real.Min(g => g.Position.DistanceTo(c));
                    if (away <= bestAway || GuardPosts.CellNear(map, c) != c || !from.CanReach(c, PathEndMode.OnCell, Danger.Some)) continue;
                    // (Room to stand about it, and to put an inmate beside it.)
                    if (GenAdj.AdjacentCellsAndInside.Count(d => (c + d).InBounds(map) && (c + d).Standable(map)) < 6) continue;
                    (best, bestAway) = (c, away);
                }
                return best;
            }

            /// <summary>A free cell the guard can reach, between <paramref name="min"/> and <paramref name="max"/> cells from the post.</summary>
            public IntVec3 Start(Pawn g, IntVec3 post, float min, float max)
            {
                foreach (IntVec3 c in GenRadial.RadialCellsAround(post, max, false).Where(c => c.DistanceTo(post) >= min).OrderBy(c => c.DistanceTo(post)))
                    if (c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && g.CanReach(c, PathEndMode.OnCell, Danger.Some)) return c;
                return IntVec3.Invalid;
            }

            public CompGuardPost Post(IntVec3 site)
            {
                Thing t = GuardPosts.Place(map, site);
                if (t == null) return null;
                r.Add(() =>
                {
                    if (!t.Destroyed) t.Destroy();
                });
                return t.TryGetComp<CompGuardPost>();
            }

            /// <summary>A second post 4-6 cells from the first, on the cell nearest to <paramref name="nearTo"/> that the guard can reach; null when there is none.</summary>
            public CompGuardPost Second(CompGuardPost first, Pawn nearTo)
            {
                IntVec3 best = IntVec3.Invalid;
                float bestAway = float.MaxValue;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(first.parent.Position, 6f, true))
                {
                    if (c.DistanceTo(first.parent.Position) < 4f || !c.InBounds(map) || !c.Standable(map) || c.GetFirstPawn(map) != null || GuardPosts.CellNear(map, c) != c) continue;
                    float away = c.DistanceTo(nearTo.Position);
                    if (away < bestAway && nearTo.CanReach(c, PathEndMode.OnCell, Danger.Some)) (best, bestAway) = (c, away);
                }
                return best.IsValid ? Post(best) : null;
            }

            /// <summary>
            /// Real ticks until the post has someone on it (at most 1500). <paramref name="stayedFree"/>: every watched guard stayed free (idle, or on a post) all the way: a throwaway guard is a real one, and a frisk, a meal, a search or
            /// an escort can take one the moment <see cref="Place"/> ends its job, which is told and not failed.
            /// </summary>
            public bool Man(CompGuardPost post, out bool stayedFree, params Pawn[] watch)
            {
                bool free = true;
                Run(() =>
                {
                    foreach (Pawn w in watch) free &= GuardPosts.IdleOrPosted(w);
                    return post.Stander != null;
                }, 1500);
                stayedFree = free;
                return post.Stander != null;
            }

            /// <summary>Hours that cover now, today and the next four, whatever the clock does in the check (and over midnight if it is late).</summary>
            public void Open(CompGuardPost p) => (p.fromHour, p.toHour) = ((Hour + 23) % 24, (Hour + 5) % 24);

            /// <summary>Hours that begin in two hours' time: closed now, and for as long as the check runs.</summary>
            public void Closed(CompGuardPost p) => (p.fromHour, p.toHour) = ((Hour + 2) % 24, (Hour + 5) % 24);

            /// <summary>Real ticks until the condition holds (at most <paramref name="max"/>); returns the ticks used.</summary>
            public int Run(Func<bool> done, int max, Action each = null)
            {
                int ticks = 0;
                while (ticks < max && !done())
                {
                    if (Hour != lastHour)
                    {
                        // (The hourly pass runs in the tick the hour turns: counted, so that a failure says whether one was crossed.)
                        lastHour = Hour;
                        hoursCrossed++;
                    }
                    if (ticks % 10 == 0)
                    {
                        foreach ((Pawn g, bool on) in guards)
                        {
                            Apply(g, on);
                            if (g.InMentalState) g.MentalState.RecoverFromState();
                        }
                        each?.Invoke();
                    }
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                return ticks;
            }

            public static bool OnPost(Pawn g, CompGuardPost post) => g.CurJobDef == RPPostDefOf.RP_ManPost && g.CurJob.targetA.Thing == post.parent;

            public static bool Standing(CompGuardPost post) => post.Stander is { } g && g.Position == post.parent.Position;

            /// <summary>Anyone at all on the way to the post or standing there (the real guards too).</summary>
            public bool AnyoneOn(CompGuardPost post) => map.mapPawns.FreeColonistsSpawned.Any(g => OnPost(g, post));
        }

        /// <summary>Everything the check needs: a scene with a post up and the guards placed, or why it cannot be run (a failure of the check's premise is told, not hidden).</summary>
        private static bool PostSetup(Map map, Rollback r, string what, int guardCount, out PostScene s, out CompGuardPost post, out List<Pawn> guards)
        {
            s = new PostScene(map, r);
            post = null;
            guards = new List<Pawn>();
            Pawn probe = s.Guard(IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
            IntVec3 site = s.Site(probe);
            if (!site.IsValid)
            {
                GuardPostFail($"{what}: no cell for a post that a guard can reach");
                return false;
            }
            post = s.Post(site);
            if (post == null)
            {
                GuardPostFail($"{what}: the post could not be put up at {site}");
                return false;
            }
            guards.Add(probe);
            // (The nearest first: 3-4 cells off, the next 7-9.)
            for (int i = 1; i < guardCount; i++) guards.Add(s.Guard(IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map))));
            for (int i = 0; i < guardCount; i++)
            {
                IntVec3 start = i == 0 ? s.Start(guards[i], site, 3f, 5f) : s.Start(guards[i], site, 7f, 10f);
                if (!start.IsValid) start = s.Start(guards[i], site, 2f, 14f);
                if (!start.IsValid)
                {
                    GuardPostFail($"{what}: no place for guard {i + 1} to start from, near {site}");
                    return false;
                }
                s.Place(guards[i], start);
            }
            return true;
        }

        /// <summary>
        /// Why no guard took the post, or why the one who did is not there: for each guard on duty, free and able for it, whether the post would be theirs (`CanTake`), their job, their distance, their Warden work and the post's
        /// status; and for whoever has the post, their walk (<see cref="PostWalkText"/>) and, when the check kept one, where they were along the way (a failure's message).
        /// </summary>
        private static string PostWhy(Map map, CompGuardPost post, List<string> trail = null)
        {
            IntVec3 at = post.parent.Position;
            WorkTypeDef warden = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Warden");
            var lines = map.mapPawns.FreeColonistsSpawned.Where(g => GuardUtility.IsGuard(g) && GuardPosts.OnDuty(g)).OrderBy(g => g.Position.DistanceToSquared(at)).Take(8).Select(g =>
                $"{g.LabelShort} ({(PostGuardHeld(g) ? "scene guard" : "real guard")}) {g.Position.DistanceTo(at):0}c job {g.CurJobDef?.defName ?? "none"} free {GuardPosts.Free(g, post)} able {GuardPosts.FreeAndAble(g, post)} takes {GuardPosts.CanTake(g, post)} " +
                $"{(PostSceneGuard(g) ? PostThinks(g) + " " : "")}warden work {(warden == null || g.workSettings == null ? "?" : g.workSettings.WorkIsActive(warden) + ":" + g.workSettings.GetPriority(warden))} reach {g.CanReserveAndReach(post.parent, PathEndMode.OnCell, Danger.Some)}");
            string walk = post.Stander is { } stander ? $"; the walk: {PostWalkText(map, stander, post)}" : "";
            string went = trail is { Count: > 0 } ? $"; along the way (tick: who, where): {string.Join(", ", trail)}" : "";
            return $"[post at {at} open {post.OpenNow} status '{post.StatusText()}' named {post.Assigned?.LabelShort ?? "none"}; on duty: {string.Join("; ", lines)}{walk}{went}]";
        }

        /// <summary>
        /// A guard on the way to a post, as the walk is read: how far it is as the crow flies and by the path the pathfinder finds from where they stand now (cells and cost, a cost near a tick for a cell at the pace of a walk), what their
        /// pather is doing (moving or not, where to, the cell it is entering next and who stands on it, whether it moved in the last second), how fast they can go, and what stands on the post's cell. A guard who is held is told from one
        /// whose walk is long.
        /// </summary>
        private static string PostWalkText(Map map, Pawn g, CompGuardPost post)
        {
            IntVec3 at = post.parent.Position;
            using PawnPath path = map.pathFinder.FindPathNow(g.Position, at, TraverseParms.For(g, Danger.Some));
            string route = path.Found ? $"a path of {path.NodesLeftCount} cells costing {path.TotalCost:0}" : "no path";
            Pawn_PathFollower f = g.pather;
            IntVec3 next = f.nextCell;
            Pawn blocker = next.IsValid && next.InBounds(map) ? next.GetFirstPawn(map) : null;
            Pawn onPost = at.GetFirstPawn(map);
            return $"{g.LabelShort} at {g.Position}, {g.Position.DistanceTo(at):0} cells off as the crow flies, {route} from there; job {g.CurJobDef?.defName ?? "none"} (urgency {g.CurJob?.locomotionUrgency}), moving {f.Moving} to {f.Destination} " +
                $"(next cell {next} with {blocker?.LabelShort ?? "nobody"} on it, moved in the last 60 ticks {f.MovedRecently(60)}, {f.curPath?.NodesLeftCount.ToString() ?? "no"} cells left on its path), move speed {g.GetStatValue(StatDefOf.MoveSpeed):0.0} " +
                $"(moving {g.health.capacities.GetLevel(PawnCapacityDefOf.Moving):P0}), on the post's cell: {onPost?.LabelShort ?? "nobody"}, {g.Position.GetDoor(map)?.LabelShort ?? "no door"} under them";
        }

        // ---- the hours -------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The hours of a post: all day when from and to are the same, a window that may run past midnight, to the hour and not in it.</summary>
        private static void CheckGuardPostHoursCover()
        {
            var post = new CompGuardPost();
            bool Covers(int from, int to, int h)
            {
                (post.fromHour, post.toHour) = (from, to);
                return post.CoversHour(h);
            }
            bool allDay = Enumerable.Range(0, 24).All(h => Covers(0, 0, h) && Covers(9, 9, h));
            bool day = Enumerable.Range(0, 24).All(h => Covers(6, 18, h) == (h >= 6 && h < 18));
            bool overMidnight = Enumerable.Range(0, 24).All(h => Covers(22, 6, h) == (h >= 22 || h < 6));
            bool lateStart = Enumerable.Range(0, 24).All(h => Covers(23, 1, h) == (h == 23 || h == 0));
            if (!allDay || !day || !overMidnight || !lateStart)
                GuardPostFail($"the hours: all day {allDay}, 6 to 18 {day}, 22 to 6 {overMidnight}, 23 to 1 {lateStart}");
        }

        // ---- manning, the window, a post taken down, a watcher ----------------------------------------------------------------------------------------------------

        private static void CheckGuardPostManning(Map map)
        {
            CheckGuardPostHoursCover();
            Guarded("guard post: manning, its hours, taken down, a watcher", map, r =>
            {
                if (!PostSetup(map, r, "manning", 1, out PostScene s, out CompGuardPost post, out List<Pawn> guards)) return;
                Pawn guard = guards[0];
                IntVec3 cell = post.parent.Position;

                // Outside its hours nobody is sent (not this guard, nor any real one), and it says so.
                s.Closed(post);
                s.Run(() => false, 400, () => s.Closed(post));
                if (s.AnyoneOn(post) || post.OpenNow || post.StatusText() != "RP_PostOutsideHours".Translate().ToString())
                    GuardPostFail($"outside its hours a guard was sent to the post or it did not say so (on it: {s.AnyoneOn(post)}, open {post.OpenNow}, '{post.StatusText()}')");

                // Within them a free guard on shift is on RP_ManPost at it.
                s.Open(post);
                bool firstFree = true;
                int ticks = s.Run(() => PostScene.Standing(post), 1500, () => firstFree &= GuardPosts.IdleOrPosted(guard));
                // (A guard on the way is the post manned, only not yet there: a real guard who took it first can have a long walk round the walls - 26 cells off in a line and still walking at 1500
                // ticks in a census run, while the nearer throwaway rightly left it to them. They are given the time to arrive.)
                if (!PostScene.Standing(post) && post.Stander != null) ticks += s.Run(() => PostScene.Standing(post), 3000);
                Pawn stander = post.Stander;
                // (Told, not failed, when nobody came and the throwaway guard was taken by other work meanwhile - a delivery of food, a search - with no real guard free either.)
                if ((stander == null || !PostScene.Standing(post)) && stander == null && !firstFree)
                {
                    RPLog.Message("[RimPrison:test] guard post: nobody manned it, the throwaway guard was taken by other work, the rest skipped");
                    return;
                }
                RPLog.Message($"[RimPrison:test] guard post: a guard stood at the post after {ticks} ticks ({stander?.LabelShort ?? "nobody"}, {(stander == guard ? "the throwaway guard" : "another")})");
                if (stander == null || !PostScene.Standing(post))
                {
                    GuardPostFail($"a free guard on shift did not stand at an open post within 1500 ticks {PostWhy(map, post)} (jobs: {string.Join(", ", map.mapPawns.FreeColonistsSpawned.Where(g => GuardUtility.IsGuard(g) && g.Position.InHorDistOf(cell, 12f)).Select(g => g.LabelShort + ":" + (g.CurJobDef?.defName ?? "none")))}, the throwaway guard on duty {GuardPosts.OnDuty(guard)} free {GuardPosts.Free(guard, post)})");
                    return;
                }
                if (post.StatusText() != "RP_PostStanding".Translate(stander.LabelShort).ToString() || !post.CompInspectStringExtra().Contains(stander.LabelShort) || !post.CompInspectStringExtra().Contains(post.HoursText()))
                    GuardPostFail($"the post's inspect text does not say who stands there and the hours: '{post.CompInspectStringExtra()}'");
                // Another guard is not sent to a post that has one.
                Pawn spare = s.Guard(IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
                IntVec3 spareAt = s.Start(spare, cell, 3f, 6f);
                if (spareAt.IsValid) s.Place(spare, spareAt);
                s.Run(() => false, 400);
                if (map.mapPawns.FreeColonistsSpawned.Count(g => PostScene.OnPost(g, post)) > 1) GuardPostFail("two guards were sent to the same post");
                s.Place(spare, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
                s.Shift(spare, false);

                // A guard standing there is a watcher by the rules a guard is: an inmate beside the post is watched, and a misconduct of theirs is seen (the control: not with nobody there).
                if (PostScene.Standing(post) && post.Stander == stander)
                {
                    Pawn inmate = Throwaway(map, r);
                    IntVec3 beside = GenRadial.RadialCellsAround(cell, 3f, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && GenSight.LineOfSight(cell, c, map, skipFirstCell: true));
                    if (!beside.IsValid) RPLog.Message("[RimPrison:test] guard post watcher: no free cell in sight of the post, skipped");
                    else
                    {
                        s.Place(inmate, beside);
                        CompInmate c = inmate.Inmate();
                        c.records.Clear();
                        MisconductUtility.Record(inmate, c, s.pm, RPComplaintDefOf.RP_Insult, chronicle: false);
                        bool seen = c.records.Count > 0 && c.records[c.records.Count - 1].seen;
                        bool watched = TensionUtility.Watched(inmate, s.pm) && TensionUtility.GuardsNear(inmate, s.pm) >= 1;
                        c.pendingPunishmentHours = 0;
                        if (!watched || !seen) GuardPostFail($"an inmate beside a manned post is not watched or their misconduct was not seen (watched {watched}, seen {seen})");
                    }
                }

                // The window ends: the guard leaves, and is not sent back.
                s.Closed(post);
                int leaving = s.Run(() => !PostScene.OnPost(stander, post), 200, () => s.Closed(post));
                if (PostScene.OnPost(stander, post)) GuardPostFail($"the guard stayed at the post {leaving} ticks after its hours ended");
                s.Run(() => false, 200, () => s.Closed(post));
                if (s.AnyoneOn(post)) GuardPostFail("a guard was sent back to the post outside its hours");

                // Destroyed (a deconstructed post leaves the map the same way): the guard's job ends.
                s.Open(post);
                // (Told, not failed, when the throwaway guard was taken by other work meanwhile and no real guard was free either: a census run.)
                bool stayedFree = true;
                s.Run(() => PostScene.Standing(post), 1500, () => stayedFree &= GuardPosts.IdleOrPosted(guard));
                Pawn again = post.Stander;
                if (again == null)
                {
                    if (stayedFree) GuardPostFail("the post was not manned again when its hours began");
                    else RPLog.Message("[RimPrison:test] guard post: not manned again, the throwaway guard was taken by other work, the rest skipped");
                    return;
                }
                post.parent.Destroy();
                s.Run(() => !PostScene.OnPost(again, post), 10);
                if (PostScene.OnPost(again, post)) GuardPostFail($"a guard was kept at a post that was destroyed (job {again.CurJobDef?.defName ?? "none"})");
            });
        }

        // ---- the guard named for it --------------------------------------------------------------------------------------------------------------------------------

        private static void CheckGuardPostAssignedGuard(Map map)
        {
            Guarded("guard post: the guard named for it", map, r =>
            {
                if (!PostSetup(map, r, "assigned guard", 2, out PostScene s, out CompGuardPost post, out List<Pawn> guards)) return;
                Pawn near = guards[0], far = guards[1];
                IntVec3 cell = post.parent.Position, nearAt = near.Position, farAt = far.Position;
                if (nearAt.DistanceToSquared(cell) >= farAt.DistanceToSquared(cell))
                {
                    GuardPostFail("the check's own premise: the guard meant to be the nearer is not");
                    return;
                }
                void Reset()
                {
                    s.Closed(post);
                    s.Run(() => !s.AnyoneOn(post), 200, () => s.Closed(post));
                    s.Place(near, nearAt);
                    s.Place(far, farAt);
                }

                // Without a name: the nearest free guard goes (the control).
                // (Idle guards wander: the one placed nearer can be the farther one by the time the post is taken, so the distances are read at the tick before the pick.)
                s.Open(post);
                bool nearStayedFree = true, nearCloserAtPick = true;
                int nearSq = 0, farSq = 0;
                s.Run(() =>
                {
                    nearStayedFree &= GuardPosts.IdleOrPosted(near);
                    if (post.Stander != null) return true;
                    (nearSq, farSq) = (near.Position.DistanceToSquared(cell), far.Position.DistanceToSquared(cell));
                    nearCloserAtPick = nearSq < farSq;
                    return false;
                }, 1500);
                Pawn first = post.Stander;
                if (first == null) GuardPostFail("with no guard named, no one was sent to the post within 1500 ticks");
                else if (first == far && !nearStayedFree) RPLog.Message("[RimPrison:test] guard post: the nearer guard was taken by other work before the pick, the control is skipped");
                else if (first == far && !nearCloserAtPick) RPLog.Message($"[RimPrison:test] guard post: the guard placed nearer had wandered farther by the pick (squared distances {nearSq} and {farSq}), the control is skipped");
                else if (first == far) GuardPostFail($"with no guard named, the farther of two free guards was sent to the post (squared distances at the pick {nearSq} and {farSq})");
                RPLog.Message($"[RimPrison:test] guard post: no guard named, {first?.LabelShort ?? "nobody"} went ({(first == near ? "the nearer of the two" : first == far ? "the farther of the two" : "a real guard")})");
                Reset();

                // The far one is named, and on shift: they go, not the nearer.
                post.assigned = far;
                s.Open(post);
                s.Man(post, out bool farStayedFree, far);
                Pawn named = post.Stander;
                if (named != far && !farStayedFree) RPLog.Message("[RimPrison:test] guard post: the named guard was taken by other work before the pick, skipped");
                else if (named != far) GuardPostFail($"with the farther guard named and on shift, {named?.LabelShort ?? "nobody"} stood at the post (named {far.LabelShort}, the nearer is {near.LabelShort})");
                Reset();

                // The named guard is off shift: another goes.
                s.Shift(far, false);
                s.Open(post);
                bool nearFree = true;
                s.Run(() => post.Stander != null, 1500, () => nearFree &= GuardPosts.IdleOrPosted(near));
                Pawn other = post.Stander;
                // (Told, not failed, when the only guard left on shift was taken by other work all the wait.)
                if (other == null && !nearFree) RPLog.Message("[RimPrison:test] guard post: the guard left on shift was taken by other work, the off-shift named guard's check skipped");
                else if (other == null || other == far) GuardPostFail($"with the named guard off shift, {other?.LabelShort ?? "nobody"} stood at the post (another guard was to go; the named guard {far.LabelShort} on duty {GuardPosts.OnDuty(far)}, timetable {far.timetable.GetAssignment(s.Hour)?.defName} at {s.Hour}:00; {s.HourText}) {PostWhy(map, post)}");
            });
        }

        // ---- two posts ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Two open posts a few cells apart: a guard on one is no one "who will go" to the other, so both are manned; and the guard named for one goes to it even with the other nearer.</summary>
        private static void CheckGuardPostTwoPosts(Map map)
        {
            Guarded("guard post: two open posts are both manned, the named guard on the post that names them", map, r =>
            {
                if (!PostSetup(map, r, "two posts", 2, out PostScene s, out CompGuardPost a, out List<Pawn> guards)) return;
                Pawn g0 = guards[0], g1 = guards[1];
                IntVec3 g0At = g0.Position, g1At = g1.Position;
                CompGuardPost b = s.Second(a, g0);
                if (b == null)
                {
                    RPLog.Message("[RimPrison:test] guard post two posts: no cell 4-6 cells from the first post, skipped");
                    return;
                }
                IntVec3 aAt = a.parent.Position, bAt = b.parent.Position;
                bool Both() => PostScene.Standing(a) && PostScene.Standing(b);
                void Reset()
                {
                    s.Closed(a);
                    s.Closed(b);
                    s.Run(() => !s.AnyoneOn(a) && !s.AnyoneOn(b), 200, () =>
                    {
                        s.Closed(a);
                        s.Closed(b);
                    });
                    s.Place(g0, g0At);
                    s.Place(g1, g1At);
                }

                // Both open, two free guards, the nearer of them standing nearer the second post too: both posts get a guard (the first guard on a post is not the "closer guard" that keeps the other from going).
                if (g0At.DistanceToSquared(bAt) >= g1At.DistanceToSquared(bAt)) RPLog.Message("[RimPrison:test] guard post two posts: the first guard is not nearer the second post, the check is the weaker for it");
                s.Open(a);
                s.Open(b);
                bool stayedFree = true;
                int ticks = s.Run(Both, 1500, () => stayedFree &= GuardPosts.IdleOrPosted(g0) && GuardPosts.IdleOrPosted(g1));
                RPLog.Message($"[RimPrison:test] guard post two posts: {(Both() ? "both manned" : "not both manned")} after {ticks} ticks ({a.Stander?.LabelShort ?? "nobody"} and {b.Stander?.LabelShort ?? "nobody"})");
                if (!Both())
                {
                    if (stayedFree) GuardPostFail($"with two posts open and two free guards, a post was not manned within 1500 ticks (post one: {a.StatusText()}; post two: {b.StatusText()}; {s.HourText}) post one {PostWhy(map, a)} post two {PostWhy(map, b)}");
                    else RPLog.Message("[RimPrison:test] guard post two posts: a throwaway guard was taken by other work, skipped");
                    return;
                }
                Reset();

                // The guard nearer the second post is named for the first: they stand on the first and the other guard on the second.
                if (g0At.DistanceToSquared(bAt) >= g0At.DistanceToSquared(aAt))
                {
                    RPLog.Message("[RimPrison:test] guard post two posts: the named guard is not nearer the second post than the first, skipped");
                    return;
                }
                a.assigned = g0;
                s.Open(a);
                s.Open(b);
                stayedFree = true;
                // (Until the named guard stands on their own post, not until both are manned: a real guard can take the first post before the named one comes free, and gives it up to them at the
                // next two-second look - a census run stopped at the first tick both were manned, by two real guards.)
                s.Run(() => Both() && a.Stander == g0, 1500, () => stayedFree &= GuardPosts.IdleOrPosted(g0) && GuardPosts.IdleOrPosted(g1));
                if (!Both())
                {
                    if (stayedFree) GuardPostFail($"with a guard named for one of two open posts, a post was not manned within 1500 ticks (post one: {a.StatusText()}; post two: {b.StatusText()})");
                    else RPLog.Message("[RimPrison:test] guard post two posts: a throwaway guard was taken by other work, skipped");
                }
                else if (a.Stander != g0 || b.Stander == g0)
                {
                    // (Told, not failed, when the named guard was taken by other work on the way: the posts were rightly manned by the others, as in the waits above.)
                    if (!stayedFree) RPLog.Message($"[RimPrison:test] guard post two posts: the named guard or the other was taken by other work, skipped (g0 {g0.CurJobDef?.defName ?? "none"}, first post {a.Stander?.LabelShort}, second {b.Stander?.LabelShort})");
                    else
                        GuardPostFail($"the guard named for the first post stood at {(b.Stander == g0 ? "the second, which is nearer them" : "neither")} (first post: {a.Stander?.LabelShort}, second: {b.Stander?.LabelShort}, named {g0.LabelShort}; {s.HourText}) post one {PostWhy(map, a)} post two {PostWhy(map, b)}");
                }
            });
        }

        // ---- an emergency first ------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckGuardPostEmergencyFirst(Map map)
        {
            Guarded("guard post: an emergency outranks it", map, r =>
            {
                if (!PostSetup(map, r, "emergency", 1, out PostScene s, out CompGuardPost post, out List<Pawn> guards)) return;
                Pawn guard = guards[0];
                if (guard.WorkTagIsDisabled(WorkTags.Violent))
                {
                    RPLog.Message("[RimPrison:test] guard post emergency: no guard who can fight, skipped");
                    return;
                }
                s.Open(post);
                var trail = new List<string>();
                int looks = 0;
                void Track()
                {
                    if (looks++ % 25 == 0 && post.Stander is { } on) trail.Add($"{(looks - 1) * 10}: {on.LabelShort} {on.Position}");
                }
                s.Run(() => PostScene.Standing(post), 1500, Track);
                // (A guard on the way is the post manned, only not yet there: a real guard who took it first, as in the manning check, has a long walk round the walls, and is given the time to arrive; one who does not move
                // in it is told by the walk the failure gives.)
                if (!PostScene.Standing(post) && post.Stander != null) s.Run(() => PostScene.Standing(post), 3000, Track);
                // (Whoever stands there is the guard the emergency must call away: a real guard nearer the post can take it before the throwaway does - a census run.)
                if (!PostScene.Standing(post) || post.Stander is not { } standing)
                {
                    GuardPostFail($"nobody stood at the post to be called away from it within 1500 ticks (stander {post.Stander?.LabelShort ?? "nobody"}; 3000 more were given to one on the way) {PostWhy(map, post, trail)}");
                    return;
                }
                if (standing != guard)
                {
                    if (standing.WorkTagIsDisabled(WorkTags.Violent))
                    {
                        RPLog.Message($"[RimPrison:test] guard post emergency: {standing.LabelShort} took the post and cannot fight, skipped");
                        return;
                    }
                    guard = standing;
                }
                // (Somebody real is running amok within this guard's reach: the alarm is the game's own, and this check cannot tell it from the one it makes.)
                if (GuardUtility.AlarmFor(guard) || map.mapPawns.PrisonersOfColonySpawned.Any(x => x.InMentalState))
                {
                    RPLog.Message("[RimPrison:test] guard post emergency: an alarm is already on in the prison, skipped");
                    return;
                }
                // An inmate runs amok a few cells off (reserved by this guard first, so that no other can be sent to them: the check is about this guard's post).
                Pawn rowdy = Throwaway(map, r);
                r.Add(() => rowdy.mindState.mentalStateHandler.Reset());
                postRowdies.Add(rowdy);
                r.Add(() => postRowdies.Remove(rowdy));
                IntVec3 cell = s.Start(guard, post.parent.Position, 4f, 7f);
                if (!cell.IsValid)
                {
                    RPLog.Message("[RimPrison:test] guard post emergency: no free cell a few cells from the post, skipped");
                    return;
                }
                s.Place(rowdy, cell);
                guard.Reserve(rowdy, guard.CurJob, 1, -1, null, false);
                rowdy.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, transitionSilently: true);
                bool alarm = GuardUtility.AlarmFor(guard);
                int waited = s.Run(() => !PostScene.OnPost(guard, post), 2 * GuardPosts.OverrideCheckTicks + 60);
                RPLog.Message($"[RimPrison:test] guard post emergency: alarm {alarm}, the guard left the post after {waited} ticks (now {guard.CurJobDef?.defName ?? "none"})");
                // (Sent to restrain them: the alarm took the guard from the post, not a fright or a wound.)
                // (Off the post for the emergency: restraining them, or vanilla's own answer for a colonist set to flee from danger (the hostility response outranks every work, the patrol's too, so a
                // guard on patrol would flee as well): the post gave way either way.)
                if (!alarm || guard.CurJobDef != RPSecurityDefOf.RP_Subdue && guard.CurJobDef != JobDefOf.FleeAndCower)
                    GuardPostFail($"an inmate running amok did not take the guard from the post (alarm for the guard {alarm}; the job after {waited} ticks is {guard.CurJobDef?.defName ?? "none"}, restraining or vanilla's flee expected)");
            });
        }

        // ---- no break with an alarm up ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard with an inmate running amok that they would be sent to is not free for a staff break (<see cref="StaffBreaks.Busy"/>, what the hourly pass asks), and a break they are already on ends within a few seconds
        /// (the break is a long job that nothing asks the think tree during). The pre-fix guard was free for it, and stayed on it (stability9-s9a: "the job after 10 ticks is RP_StaffBreak").
        /// </summary>
        private static void CheckGuardPostNoBreakInAlarm(Map map)
        {
            Guarded("guard post: no break with an alarm up", map, r =>
            {
                if (!PostSetup(map, r, "break", 1, out PostScene s, out _, out List<Pawn> guards)) return;
                Pawn guard = guards[0];
                // (A line that starts "NOT RUN": the check did not reach the code under test. A negative run counts as evidence only with no such line for this check: grep for it.)
                void NotRun(string why) => RPLog.Message($"[RimPrison:test] guard post break: NOT RUN, {why}");
                if (guard.WorkTagIsDisabled(WorkTags.Violent))
                {
                    NotRun("no guard who can fight");
                    return;
                }
                if (GuardUtility.AlarmFor(guard) || map.mapPawns.PrisonersOfColonySpawned.Any(x => x.InMentalState))
                {
                    NotRun("an alarm is already on in the prison");
                    return;
                }
                // The premise made true by construction: whatever the think tree gave the guard after the placing (a delivery, a haul: not casual) is replaced by a casual Wait, which a break may cut.
                guard.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 600), JobCondition.InterruptForced);
                if (StaffBreaks.Busy(guard, map))
                {
                    NotRun($"the guard is busy for a break with no alarm up despite a casual Wait (job {guard.CurJobDef?.defName ?? "none"}, downed {guard.Downed}, mental {guard.InMentalState}, drafted {guard.Drafted}, burning {guard.IsBurning()}, riot {RiotPolice.RiotOngoing(map)}, lockdown {PrisonGame.Instance.LockdownActive})");
                    return;
                }
                // The vanilla hostility response (the default, Flee) is in the constant think tree, asked every 30 ticks for any job: it took the guard off the break within 10 ticks of the rowdy (s10negB: the break's own end
                // condition reverted, and the check still passed), so the check could not tell the two apart. Set to Ignore, only the break's own alarm end condition can end the break (as for a guard whose player set it so).
                if (guard.playerSettings == null)
                {
                    NotRun("the guard has no player settings to set the hostility response on");
                    return;
                }
                HostilityResponseMode response0 = guard.playerSettings.hostilityResponse;
                guard.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
                r.Add(() => guard.playerSettings.hostilityResponse = response0);
                Pawn rowdy = Throwaway(map, r);
                r.Add(() => rowdy.mindState.mentalStateHandler.Reset());
                postRowdies.Add(rowdy);
                r.Add(() => postRowdies.Remove(rowdy));
                IntVec3 cell = s.Start(guard, guard.Position, 2f, 7f);
                if (!cell.IsValid)
                {
                    NotRun("no free cell near the guard");
                    return;
                }
                s.Place(rowdy, cell);
                // (Placing the rowdy next to the guard ended no job of the guard's: no tick has gone by since the Wait.)
                guard.Reserve(rowdy, guard.CurJob, 1, -1, null, false);
                rowdy.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, transitionSilently: true);
                if (!GuardUtility.AlarmFor(guard))
                {
                    NotRun("the inmate running amok sounded no alarm for the guard");
                    return;
                }
                if (!StaffBreaks.Busy(guard, map)) GuardPostFail("a guard with an inmate running amok to restrain is free for a staff break");
                // A break already begun (the alarm came after it began): it ends.
                guard.jobs.StartJob(JobMaker.MakeJob(RPStaffRoomDefOf.RP_StaffBreak, guard.Position), JobCondition.InterruptForced);
                if (guard.CurJobDef != RPStaffRoomDefOf.RP_StaffBreak)
                {
                    NotRun($"the break could not be started on the guard ({guard.CurJobDef?.defName ?? "none"})");
                    return;
                }
                // Starting the break let go of what the Wait held, the rowdy too: held again under the break, so that no real guard takes them from this one before the break asks (the alarm is "nobody else on them yet").
                guard.Reserve(rowdy, guard.CurJob, 1, -1, null, false);
                int waited = s.Run(() => guard.CurJobDef != RPStaffRoomDefOf.RP_StaffBreak, 2 * StaffBreaks.AlarmCheckTicks + 30);
                if (guard.CurJobDef == RPStaffRoomDefOf.RP_StaffBreak)
                {
                    GuardPostFail($"a guard on a staff break was not called off it by an inmate running amok within {waited} ticks (alarm for the guard {GuardUtility.AlarmFor(guard)})");
                    return;
                }
                // Called off it, the guard's own think tree goes to the inmate (restraining them, or vanilla's flee: the emergency check accepts both). Another guard may have been first to them: that is not this guard's fault, and not run.
                int after = s.Run(() => guard.CurJobDef == RPSecurityDefOf.RP_Subdue || guard.CurJobDef == JobDefOf.FleeAndCower, 120);
                if (guard.CurJobDef != RPSecurityDefOf.RP_Subdue && guard.CurJobDef != JobDefOf.FleeAndCower)
                {
                    if (GuardUtility.AlarmFor(guard)) GuardPostFail($"a guard called off a staff break by an inmate running amok did not go to them within {after} ticks (the job is {guard.CurJobDef?.defName ?? "none"}, the alarm for the guard still up)");
                    else NotRun($"the second half (the guard goes to the inmate after the break), no alarm for the guard any more after {after} ticks: another guard took the inmate (the guard's job is {guard.CurJobDef?.defName ?? "none"})");
                }
            });
        }

        // ---- the state's chief ----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckGuardPostChiefHasThePatrols(Map map)
        {
            Guarded("guard post: the state's chief has the patrols", map, r =>
            {
                if (!PostSetup(map, r, "chief", 1, out PostScene s, out CompGuardPost post, out List<Pawn> guards)) return;
                Pawn guard = guards[0];
                StateOversight o = PrisonGame.Instance.story.oversight;
                Pawn chief = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                CompStaff cc = chief.GetComp<CompStaff>();
                (Pawn chief0, bool posted0) = (o.chief, cc.statePosted);
                r.Add(() => (o.chief, cc.statePosted) = (chief0, posted0));
                void ChiefIn(bool inPost) => (o.chief, cc.statePosted) = inPost ? (chief, true) : (chief0, posted0);

                // The chief in post: nobody is sent, and the post says so.
                ChiefIn(true);
                if (!SpecialMeasures.ChiefInPost)
                {
                    GuardPostFail("the check's own premise: the state's chief is not in post");
                    return;
                }
                s.Open(post);
                // (The made-up chief is held in post through the wait: the game's own passes may take a made-up chief off it, and then the post opens rightly.)
                // (The whole wait, not until someone is on it: a guard already on the way from the scene's setup is on it at the first tick, and the job lets go at its next 30-tick look.)
                s.Run(() =>
                {
                    ChiefIn(true);
                    return false;
                }, 400);
                if (!SpecialMeasures.ChiefInPost) GuardPostFail("the check's own premise: the state's chief left the post during the wait");
                else if (s.AnyoneOn(post) || post.OpenNow || post.StatusText() != "RP_PostSuspended".Translate().ToString())
                    GuardPostFail($"with the state's chief in post a guard was sent to the post, or it did not say it is suspended (on it {s.AnyoneOn(post)}, open {post.OpenNow}, '{post.StatusText()}')");

                // The chief gone: the post is manned (the control: the guard was free all along).
                ChiefIn(false);
                if (SpecialMeasures.ChiefInPost) RPLog.Message("[RimPrison:test] guard post chief: a real chief is in post, the second half is skipped");
                else
                {
                    // (Told, not failed, when the throwaway guard was taken by other work meanwhile: the same rule as the other waits.)
                    bool controlFree = true;
                    s.Run(() => PostScene.Standing(post), 1500, () => controlFree &= GuardPosts.IdleOrPosted(guard));
                    Pawn stander = post.Stander;
                    if (stander == null || !PostScene.Standing(post))
                    {
                        if (controlFree) GuardPostFail("(control) with the chief gone no guard stood at the post within 1500 ticks");
                        else RPLog.Message("[RimPrison:test] guard post chief: the control not run, the throwaway guard was taken by other work");
                        return;
                    }
                    // The chief arrives with a guard standing there: they leave.
                    ChiefIn(true);
                    int left = s.Run(() => !PostScene.OnPost(stander, post), 120);
                    if (PostScene.OnPost(stander, post)) GuardPostFail($"a guard stayed at the post {left} ticks after the state's chief took the patrols");
                    s.Run(() => s.AnyoneOn(post), 200);
                    if (s.AnyoneOn(post)) GuardPostFail("a guard was sent back to the post while the state's chief had the patrols");
                }
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------------------------------------

        private static int postSeedId = -1, postSeedGuardId = -1;
        private static bool postSeedIsNew;
        private static (int from, int to, Pawn assigned) postSeedBefore;
        private const int PostSeedFrom = 21, PostSeedTo = 5;

        /// <summary>Saveload plan, before the snapshot: a post (the prison's own, else one put up for this) with hours that are not the default and a guard named, which the snapshot reads back after the reload.</summary>
        private static void SeedGuardPostsForReload(Map map)
        {
            postSeedId = postSeedGuardId = -1;
            postSeedIsNew = false;
            Pawn guard = map.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard).OrderBy(x => x.thingIDNumber).FirstOrDefault();
            if (guard == null)
            {
                reloadProblems.Add("saveload: no guard to name for a guard post");
                return;
            }
            CompGuardPost post = GuardPosts.Posts(map).FirstOrDefault();
            if (post == null)
            {
                post = GuardPosts.Place(map, guard.Position)?.TryGetComp<CompGuardPost>();
                postSeedIsNew = post != null;
            }
            if (post == null)
            {
                reloadProblems.Add("saveload: no cell for a guard post");
                return;
            }
            postSeedBefore = (post.fromHour, post.toHour, post.assigned);
            (post.fromHour, post.toHour, post.assigned) = (PostSeedFrom, PostSeedTo, guard);
            (postSeedId, postSeedGuardId) = (post.parent.thingIDNumber, guard.thingIDNumber);
        }

        private static void CheckGuardPostAfterReload(Map map)
        {
            if (postSeedId < 0) return;
            CompGuardPost post = GuardPosts.Posts(map).FirstOrDefault(p => p.parent.thingIDNumber == postSeedId);
            if (post == null)
            {
                reloadProblems.Add("the guard post did not come back after the save");
                return;
            }
            if (post.fromHour != PostSeedFrom || post.toHour != PostSeedTo || post.assigned?.thingIDNumber != postSeedGuardId)
                reloadProblems.Add($"a guard post's hours or guard changed in the round trip: {post.fromHour} to {post.toHour}, guard {post.assigned?.thingIDNumber} (saved {PostSeedFrom} to {PostSeedTo}, guard {postSeedGuardId})");
            else RPLog.Message("[RimPrison:test] saveload guard post: the hours and the named guard are intact");
            // (Put back as the game had it: the post goes if the seed put it up.)
            if (postSeedIsNew) post.parent.Destroy();
            else (post.fromHour, post.toHour, post.assigned) = postSeedBefore;
        }
    }

    /// <summary>
    /// Autotest: while a guard-post scene runs, a guard who is not one of its throwaways is not on duty for a post (<see cref="AutoTest.PostSceneExcludes"/>): they are not sent to the scene's posts, do not
    /// keep the throwaways from them as a nearer free guard, and are not counted free.
    /// </summary>
    [HarmonyPatch(typeof(GuardPosts), nameof(GuardPosts.OnDuty))]
    [TestOnly]
    public static class Patch_GuardPosts_OnDuty_Scene
    {
        public static void Postfix(Pawn p, ref bool __result)
        {
            if (__result && AutoTest.Active && AutoTest.PostSceneExcludes(p)) __result = false;
        }
    }

    /// <summary>
    /// Autotest: a post scene's throwaway guard is offered the post and the patrol (the filler that gives way to it) by the ordinary work node (<see cref="AutoTest.PostSceneGuard"/>), and no other work: the post is warden work, and so is
    /// everything that outranks it (a cell meal, a frisk, an escort, a search): a guard of the emergency check that was on a meal delivery (stability11-s11b: "Forsythia (scene guard) 61c job DeliverFood") stood at no post to be called away
    /// from. It is the work node's own scan that is told which givers the guard may use (<c>PawnCanUseWorkGiver</c>), so the post is still found, and given, by <see cref="WorkGiver_ManPost"/> (its skips, its scan, its job) and the patrol
    /// still looks for a post every two seconds (<see cref="JobDriver_Patrol"/>); the emergency node (the alarm the check raises) is left alone. (Round 1 of stability12 replaced the node's result with the post the giver would give, and none when it gives none, and the guards then wandered in all three runs and did not take the post though
    /// <c>CanTake</c> held at the end of the wait; what was different for them is the patrol, which has a guard with no post look again every 120 ticks, and the work giver's own skips, both of which the node's scan keeps now. The cause is not proven: the work node's looks are in the failure line.)
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_Work), "PawnCanUseWorkGiver")]
    [TestOnly]
    public static class Patch_JobGiver_Work_PostSceneGuards
    {
        public static void Postfix(JobGiver_Work __instance, Pawn pawn, WorkGiver giver, ref bool __result)
        {
            if (__result && AutoTest.Active && !__instance.emergency && AutoTest.PostSceneGuard(pawn) && giver is not (WorkGiver_ManPost or WorkGiver_Patrol)) __result = false;
        }
    }

    /// <summary>
    /// Autotest: a post scene's throwaway guard is not sent to the live prison's trouble (an inmate of the real prison fighting, due for a restraint) by the emergency node while the scene runs, as the ordinary work node is held to the post
    /// and the patrol above: a census run (custody-c2b) had the emergency check's guard on RP_Subdue to a real inmate, the work node never asked, so nobody stood at the post to be called away from it. The scene's own rowdy is answered as always.
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_RespondToTrouble), nameof(WorkGiver_RespondToTrouble.HasJobOnThing))]
    [TestOnly]
    public static class Patch_WorkGiver_RespondToTrouble_PostSceneGuards
    {
        public static void Postfix(Pawn pawn, Thing t, ref bool __result)
        {
            if (__result && AutoTest.Active && t is Pawn inmate && !AutoTest.PostSceneMayRespondTo(pawn, inmate)) __result = false;
        }
    }

    /// <summary>Autotest: what the ordinary work node gave a post scene's guard, for the failure line (<see cref="AutoTest.PostThinks"/>).</summary>
    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    [TestOnly]
    public static class Patch_JobGiver_Work_PostSceneTrace
    {
        public static void Postfix(JobGiver_Work __instance, Pawn pawn, ThinkResult __result)
        {
            if (AutoTest.Active && !__instance.emergency && AutoTest.PostSceneGuard(pawn)) AutoTest.NotePostThink(pawn, __result.Job);
        }
    }
}
