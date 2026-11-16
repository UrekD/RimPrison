using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The control room (<see cref="Cctv"/>): with the policy on a camera is watched live only while a guard sits at a powered, unbroken CCTV monitor that carries it. Each check runs in an isolated scene (<see cref="CctvScene"/>:
    /// the dark checks' scene - a throwaway inmate on a lit free-time cell, one camera over it, the prison's own watchers put aside - plus a monitor out of sight of it, the real monitors' posts closed, and throwaway guards that
    /// the post scenes keep the real guards out of), on throwaway inmates, guards, cameras and monitors, rolled back. Every check fails without its change (said where it is made). Premises the live prison may not give are told, not
    /// failed. The round trip is in the saveload plan (<see cref="SeedCctvForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void CctvFail(string check, string what) => problems0.Add($"cctv ({check}): {what}");

        /// <summary>The policy "cameras need an operator" set for a check (and its cache rebuilt), put back by the rollback.</summary>
        private static void CctvPolicy(Rollback r, bool on)
        {
            if (PrisonGame.Instance is not { } g) return;
            bool before = g.camerasNeedOperator;
            g.camerasNeedOperator = on;
            r.Add(() =>
            {
                g.camerasNeedOperator = before;
                Cctv.RebuildAll();
            });
            Cctv.RebuildAll();
        }

        /// <summary>A free cell for a monitor facing <paramref name="rot"/> (north when not said) with a free cell to sit on in front of it, at least <paramref name="minAway"/> cells from <paramref name="around"/>, nearest first; invalid when there is none.</summary>
        private static IntVec3 CctvSite(Map map, IntVec3 around, float minAway, Func<IntVec3, bool> also = null, Rot4 rot = default)
        {
            ThingDef def = RPCctvDefOf.RP_CctvMonitor;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(around, 45f, true))
            {
                if (c.DistanceTo(around) < minAway) continue;
                IntVec3 sit = ThingUtility.InteractionCellWhenAt(def, c, rot, map);
                bool free = DarkScene.Free(map, sit) && sit.GetFirstPawn(map) == null;
                foreach (IntVec3 cell in GenAdj.OccupiedRect(c, rot, def.size)) free &= DarkScene.Free(map, cell) && cell.GetFirstPawn(map) == null;
                if (!free || !GenConstruct.CanPlaceBlueprintAt(def, c, rot, map).Accepted) continue;
                if (also != null && !also(c)) continue;
                return c;
            }
            return IntVec3.Invalid;
        }

        /// <summary>Cameras put up (taken down again by the rollback) until the prison lists <paramref name="total"/> of them, each at least 11 cells from the scene's inmate (out of the radius of 9) and away from the monitor.</summary>
        private static List<CompSecurityCamera> CctvCameras(CctvScene s, int total)
        {
            var cams = new List<CompSecurityCamera>(s.pm.Cameras);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(s.d.cell, 40f, true))
            {
                if (cams.Count >= total) break;
                if (c.DistanceTo(s.d.cell) < 11f || c.DistanceTo(s.monitor.parent.Position) < 3f || !DarkScene.Free(s.map, c) || c.GetFirstPawn(s.map) != null) continue;
                cams.Add(SpawnTestCamera(s.map, s.r, c));
            }
            return cams;
        }

        /// <summary>A powered CCTV monitor on the cell (north facing when not said), taken down again by the rollback (its post is open all day).</summary>
        private static CompCctvMonitor SpawnTestMonitor(Map map, Rollback r, IntVec3 at, Rot4 rot = default)
        {
            Thing monitor = ThingMaker.MakeThing(RPCctvDefOf.RP_CctvMonitor);
            monitor.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(monitor, at, map, rot);
            r.Add(() =>
            {
                if (!monitor.Destroyed) monitor.Destroy(DestroyMode.Vanish);
                Cctv.Rebuild(map);
            });
            if (monitor.TryGetComp<CompPowerTrader>() is { } power) power.PowerOn = true;
            return monitor.TryGetComp<CompCctvMonitor>();
        }

        /// <summary>
        /// The dark checks' scene with the policy on and a monitor in it: the inmate on a lit free-time cell with one camera over it (and the prison's own guards, cameras and towers put aside), a monitor of its own at least 15
        /// cells away (out of any guard's sight of the inmate), the real monitors' posts closed for the check, a throwaway guard on duty who can sit at it, and the cache worked out.
        /// </summary>
        private sealed class CctvScene
        {
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly Rollback r;
            public readonly DarkScene d;
            public readonly PostScene posts;
            public readonly CompCctvMonitor monitor;
            public readonly CompGuardPost post;
            public readonly IntVec3 sit, away;
            public readonly Pawn op;

            public CompSecurityCamera Camera => d.camera;

            private CctvScene(DarkScene d, Rollback r, PostScene posts, CompCctvMonitor monitor, IntVec3 away)
            {
                this.d = d;
                this.r = r;
                this.posts = posts;
                this.monitor = monitor;
                this.away = away;
                map = d.map;
                pm = d.pm;
                post = monitor.parent.GetComp<CompGuardPost>();
                sit = GuardPosts.StandCell(monitor.parent);
                op = posts.Guard(away);
            }

            /// <summary>Null (a problem told by the dark scene, or the premise told) when the map has no such spot.</summary>
            public static CctvScene TryMake(Map map, Rollback r, string check, bool reachableFromIntake = false)
            {
                DarkScene d = DarkScene.TryMake(map, r);
                if (d == null) return null;
                d.Glow(1f);
                CctvPolicy(r, true);
                d.pm.Cameras.Add(d.camera);
                // (The real monitors' posts are closed for the check, as the prison's own watchers are put aside: nobody real sits at one, and none carries the scene's camera.)
                int hour = GenLocalDate.HourOfDay(map);
                foreach (Thing real in map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor).ToList())
                {
                    if (real.TryGetComp<CompGuardPost>() is not { } realPost) continue;
                    (int from, int to) = (realPost.fromHour, realPost.toHour);
                    (realPost.fromHour, realPost.toHour) = ((hour + 2) % 24, (hour + 5) % 24);
                    r.Add(() => (realPost.fromHour, realPost.toHour) = (from, to));
                }
                r.Add(() => Cctv.Rebuild(map));
                var posts = new PostScene(map, r);
                IntVec3 away = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                Func<IntVec3, bool> reach = null;
                if (reachableFromIntake) reach = c => map.reachability.CanReach(away, c + IntVec3.South, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Some));
                IntVec3 site = CctvSite(map, d.cell, 15f, reach);
                if (!site.IsValid)
                {
                    RPLog.Message($"[RimPrison:test] cctv ({check}): not run - no free cell for a monitor 15 cells from the scene");
                    return null;
                }
                CompCctvMonitor monitor = SpawnTestMonitor(map, r, site);
                if (monitor?.parent.TryGetComp<CompGuardPost>() == null)
                {
                    CctvFail(check, "the monitor has no guard post comp");
                    return null;
                }
                var s = new CctvScene(d, r, posts, monitor, away);
                s.Rebuild();
                return s;
            }

            /// <summary>The cache is rebuilt by hand, as an immediate trigger does (a toggle, a built monitor): not the map's beat, which <see cref="CheckCctvBeat"/> waits for in real ticks.</summary>
            public void Rebuild() => Cctv.Rebuild(map);

            /// <summary>The guard sits at the screens (on the monitor's interaction cell; the beat is not run).</summary>
            public void Sit() => posts.Place(op, sit);

            /// <summary>The guard gets up and goes (the beat is not run).</summary>
            public void Leave() => posts.Place(op, away);

            public bool Seen() => TensionUtility.SeenAt(d.cell, map, pm);

            public float Watchers() => TensionUtility.Watchers(d.inmate, pm);

            /// <summary>The camera covers the inmate's cell (a premise: a camera that does not see it says nothing about who watches it).</summary>
            public bool Covers() => d.camera.Covers(d.cell);
        }

        private static void CheckCctv(Map map)
        {
            CheckCctvNewGame(map);
            CheckCctvScreens(map);
            CheckCctvManned(map);
            CheckCctvBeat(map);
            CheckCctvDark(map);
            CheckCctvFeeds(map);
            CheckCctvDeadCameras(map);
            CheckCctvSeat(map);
            CheckCctvWorkGiver(map);
            CheckCctvPolicyOff(map);
            CheckCctvOldSave(map);
            CheckCctvOldSaveLetter(map);
            CheckCctvSpecialMeasures(map);
            CheckCctvOverlay(map);
            CheckCctvOracle(map);
            CheckCctvPenitentiary(map);
        }

        // ---- a new game ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>What the policy and the migration flag were when the starter builder began, one entry per run of it (see <see cref="Patch_PrisonBuilder_BuildStarter_CctvFlags"/>).</summary>
        internal static readonly List<(bool need, bool migrated)> cctvFlagsAtBuild = new List<(bool, bool)>();

        /// <summary>
        /// The game this run started: its flags were set before the builder began (so before any monitor went up: the builder's own prefix saw them, on every start, with monitors or without), and it was never offered the old-save
        /// letter (fails if the new game's flags are set after the builder, or not at all, or the builder was not seen at all).
        /// </summary>
        private static void CheckCctvNewGame(Map map)
        {
            const string check = "new game";
            if (cctvFlagsAtBuild.Count == 0) CctvFail(check, "the starter builder was not seen to run in this game (the check cannot say when the flags were set)");
            foreach ((bool need, bool migrated) in cctvFlagsAtBuild)
                if (!need || !migrated) CctvFail(check, $"when the starter builder began the policy was {need} and migrated {migrated} (a new game's flags are set to true and true before it builds, so that none of its monitors meets an old save's game)");
            PrisonGame g = PrisonGame.Instance;
            if (!g.camerasNeedOperator || !g.cctvMigrated) CctvFail(check, $"a game this run started has the policy {g.camerasNeedOperator} and migrated {g.cctvMigrated} (a new game is set to true and true)");
            if (g.cctvLetterSent || StoryLetters.AnyOpen(Cctv.DecisionDef) || Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is(Cctv.DecisionDef)))
                CctvFail(check, "a new game was offered the old-save letter about the monitors");
            if (PrisonBuilder.Kind == Facility.Penitentiary && map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor).Count == 0) CctvFail(check, "the penitentiary has no monitors");
        }

        // ---- the screens and the operator ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// 1. Screens empty: the inmate under a working camera is not seen (the control: with the policy off the same camera sees them). A guard on the monitor's cell: seen (a camera is half a watcher), and the camera's cell is
        /// counted by the pawn, the cell and the record alike. The guard gone: not seen from the next beat. (Fails if the seen rule ignores the cache, or the cache does not need an operator.)
        /// </summary>
        private static void CheckCctvScreens(Map map)
        {
            Guarded("cctv: screens empty, an operator, the operator gone", map, r =>
            {
                const string check = "screens";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                if (!s.Covers())
                {
                    RPLog.Message("[RimPrison:test] cctv (screens): not judged - the scene's camera does not cover the inmate's cell");
                    return;
                }
                if (s.Seen() || s.Watchers() > 0f || TensionUtility.Watched(s.d.inmate, s.pm)) CctvFail(check, $"with nobody at the monitor a working camera over the inmate saw them (seen {s.Seen()}, {s.Watchers()} watchers)");
                // (The record agrees: a misconduct there is not seen.)
                CompInmate c = s.d.inmate.Inmate();
                c.records.Clear();
                MisconductUtility.Record(s.d.inmate, c, s.pm, s.d.quiet, chronicle: false);
                if (c.records.Count > 0 && c.records[c.records.Count - 1].seen) CctvFail(check, "a misconduct under a camera nobody watches was recorded as seen");
                c.pendingPunishmentHours = 0;
                // The control: the old rule.
                CctvPolicy(r, false);
                if (!s.Seen()) CctvFail(check, "(control) with the policy off the working camera did not see the inmate");
                CctvPolicy(r, true);
                // A guard at the screens.
                s.Sit();
                s.Rebuild();
                if (!s.monitor.Working || Cctv.OperatorOf(s.monitor.parent) != s.op)
                {
                    CctvFail(check, $"the check's own premise: the guard on the monitor's cell is not its operator (working {s.monitor.Working}, on duty {GuardPosts.OnDuty(s.op)}, at {s.op.Position} for {s.sit}, open {s.post.OpenNow})");
                    return;
                }
                if (!s.Seen() || !Mathf.Approximately(s.Watchers(), 0.5f) || !TensionUtility.Watched(s.d.inmate, s.pm))
                    CctvFail(check, $"with a guard at the monitor the camera did not see the inmate as half a watcher (seen {s.Seen()}, {s.Watchers()} watchers)");
                c.records.Clear();
                MisconductUtility.Record(s.d.inmate, c, s.pm, s.d.quiet, chronicle: false);
                if (c.records.Count == 0 || !c.records[c.records.Count - 1].seen) CctvFail(check, "a misconduct under a camera a guard is watching was not recorded as seen");
                c.pendingPunishmentHours = 0;
                // The guard gone: not seen from the next beat.
                s.Leave();
                s.Rebuild();
                if (s.Seen() || s.Watchers() > 0f) CctvFail(check, "after the guard left the monitor, the next beat still had the camera watched");
                // A guard who is on the monitor's cell but off duty (not at work) is nobody's operator.
                s.Sit();
                s.posts.Shift(s.op, false);
                s.Rebuild();
                if (s.Seen()) CctvFail(check, "a guard off shift at the monitor's cell was counted as its operator");
            });
        }

        /// <summary>
        /// 2. A guard really mans the monitor through the work giver, by real ticks: sent to the monitor's interaction cell (not the building's own cell, which is no place to stand), reserving the monitor, and standing there as
        /// its operator at the next beat; the monitor is one of the Deployment tab's posts; it ends when its hours do. (Fails if the job targets the building's cell - the guard never stands on the sit cell - or the work giver
        /// scans only the floor posts.)
        /// </summary>
        private static void CheckCctvManned(Map map)
        {
            Guarded("cctv: a guard mans the monitor through the work giver", map, r =>
            {
                const string check = "manning";
                CctvScene s = CctvScene.TryMake(map, r, check, reachableFromIntake: true);
                if (s == null) return;
                if (!GuardPosts.Posts(map).Contains(s.post)) CctvFail(check, "the monitor is not among the guard posts (the Deployment tab lists them)");
                // (Nobody at it: the screens are empty, whether nobody is on the way or a guard is - they are empty until the guard sits.)
                Pawn coming = s.post.Stander;
                string empty = (coming == null ? "RP_CctvScreensEmpty".Translate() : coming.Position == s.sit ? "RP_PostStanding".Translate(coming.LabelShort) : "RP_CctvScreensEmptyOnTheWay".Translate(coming.LabelShort)).ToString();
                if (!s.post.OpenNow || s.post.StatusText() != empty) CctvFail(check, $"an unmanned monitor of an open post did not say its screens are empty ('{s.post.StatusText()}', want '{empty}', open {s.post.OpenNow})");
                IntVec3 start = s.posts.Start(s.op, s.sit, 3f, 6f);
                if (!start.IsValid)
                {
                    RPLog.Message("[RimPrison:test] cctv (manning): not run - no free cell for the guard to start from");
                    return;
                }
                s.posts.Place(s.op, start);
                bool Seated() => s.op.CurJobDef == RPPostDefOf.RP_ManPost && s.op.CurJob.targetA.Thing == s.monitor.parent && s.op.Position == s.sit;
                bool free = true;
                // (On the way, every ten ticks: the monitor says its screens are empty until the guard sits - "Screens empty - X on the way", and not "On the way - X", which says a post is being watched.)
                int walking = 0;
                string wrongWords = null;
                int ticks = s.posts.Run(Seated, 1500, () =>
                {
                    free &= GuardPosts.IdleOrPosted(s.op);
                    if (s.post.Stander is not { } walker || walker.Position == s.sit) return;
                    walking++;
                    string want = "RP_CctvScreensEmptyOnTheWay".Translate(walker.LabelShort).ToString();
                    if (s.post.StatusText() != want) wrongWords ??= $"'{s.post.StatusText()}' for {walker.LabelShort} at {walker.Position}, want '{want}'";
                });
                if (wrongWords != null) CctvFail(check, $"a guard on the way to the monitor and not yet seated: the post said {wrongWords}");
                else if (walking == 0) RPLog.Message("[RimPrison:test] cctv (manning): the guard was never seen on the way (the status of a monitor with a guard on the way is not judged)");
                if (!Seated() && s.op.CurJobDef == RPPostDefOf.RP_ManPost) ticks += s.posts.Run(Seated, 2500);
                if (!Seated())
                {
                    if (!free)
                    {
                        RPLog.Message("[RimPrison:test] cctv (manning): not judged - the throwaway guard was taken by other work");
                        return;
                    }
                    CctvFail(check, $"a free guard on shift did not sit at an open monitor within {ticks} ticks (job {s.op.CurJobDef?.defName ?? "none"} at {s.op.Position}, the cell to sit on {s.sit}, the monitor's own cell {s.monitor.parent.Position}, on duty {GuardPosts.OnDuty(s.op)}, " +
                        $"free {GuardPosts.Free(s.op, s.post)}, takes {GuardPosts.CanTake(s.op, s.post)}, open {s.post.OpenNow}, status '{s.post.StatusText()}')");
                    return;
                }
                RPLog.Message($"[RimPrison:test] cctv (manning): the guard sat at the monitor after {ticks} ticks");
                if (s.sit == s.monitor.parent.Position) CctvFail(check, "the monitor's cell to sit on is the building's own cell");
                if (s.post.Stander != s.op || !s.post.StandingThere || s.post.StatusText() != "RP_PostStanding".Translate(s.op.LabelShort).ToString())
                    CctvFail(check, $"the post does not say who sits at the monitor ('{s.post.StatusText()}', stander {s.post.Stander?.LabelShort ?? "nobody"}, standing there {s.post.StandingThere})");
                s.Rebuild();
                if (!s.Camera.WatchedLive || s.monitor.carrying < 1 || s.monitor.seated != s.op) CctvFail(check, $"with a guard seated by the work giver the monitor carries {s.monitor.carrying} cameras (seated {s.monitor.seated?.LabelShort ?? "nobody"})");
                // The monitor's hours end: the guard leaves it and the camera is not watched from the next beat.
                s.posts.Closed(s.post);
                int left = s.posts.Run(() => !PostScene.OnPost(s.op, s.post), 200, () => s.posts.Closed(s.post));
                if (PostScene.OnPost(s.op, s.post)) CctvFail(check, $"the guard stayed at the monitor {left} ticks after its hours ended");
                s.Rebuild();
                if (s.Camera.WatchedLive) CctvFail(check, "the camera was still watched after the monitor's hours ended");
                s.posts.Shift(s.op, false);
            });
        }

        /// <summary>
        /// 3. A monitor that is unpowered or broken down shows nothing: its cameras are not watched (and are again when it is back), and nobody is sent to it - its post is not open, no free guard can take it, the work giver has no
        /// job there and it says the screens are dark - and the guard at it gets up (by real ticks). (Fails if the monitor's power or breakdown is not read, or a dark monitor still takes a guard.)
        /// </summary>
        private static void CheckCctvDark(Map map)
        {
            Guarded("cctv: a monitor with no power or broken down", map, r =>
            {
                const string check = "dark screens";
                CctvScene s = CctvScene.TryMake(map, r, check, reachableFromIntake: true);
                if (s == null) return;
                var giver = new WorkGiver_ManPost();
                s.Sit();
                s.Rebuild();
                if (!s.Camera.WatchedLive)
                {
                    CctvFail(check, "the check's own premise: the camera is not watched with a guard at the monitor");
                    return;
                }
                // (A guard who stands off the monitor, free: the one a working monitor would be given to, and a dark one must not be.)
                s.Leave();
                if (!s.post.OpenNow || !GuardPosts.CanTake(s.op, s.post) || !giver.HasJobOnThing(s.op, s.monitor.parent))
                {
                    RPLog.Message($"[RimPrison:test] cctv (dark screens): the dark monitor is not judged - a working one would not be taken by the free guard either (open {s.post.OpenNow}, takes {GuardPosts.CanTake(s.op, s.post)})");
                    return;
                }
                s.Sit();
                CompPowerTrader power = s.monitor.parent.GetComp<CompPowerTrader>();
                void Dark(string why)
                {
                    s.Rebuild();
                    s.Leave();
                    if (s.post.OpenNow || GuardPosts.CanTake(s.op, s.post) || giver.HasJobOnThing(s.op, s.monitor.parent) || GuardPosts.PostFor(s.op) == s.post)
                        CctvFail(check, $"{why}: a dark monitor's post is still open or still taken (open {s.post.OpenNow}, takes {GuardPosts.CanTake(s.op, s.post)}, the work giver has a job {giver.HasJobOnThing(s.op, s.monitor.parent)})");
                    if (s.post.StatusText() != "RP_CctvInspectDark".Translate().ToString()) CctvFail(check, $"{why}: the dark monitor's post says '{s.post.StatusText()}', not that the screens are dark");
                    s.Sit();
                }
                if (power != null)
                {
                    power.PowerOn = false;
                    s.Rebuild();
                    if (s.Camera.WatchedLive || s.monitor.carrying != 0) CctvFail(check, "a monitor with no power still carried its cameras");
                    Dark("no power");
                    power.PowerOn = true;
                    s.Rebuild();
                    if (!s.Camera.WatchedLive) CctvFail(check, "a monitor with its power back did not carry its camera again");
                }
                if (s.monitor.parent.TryGetComp<CompBreakdownable>() is { } breakdown)
                {
                    r.Add(() =>
                    {
                        if (breakdown.BrokenDown) breakdown.Notify_Repaired();
                    });
                    breakdown.DoBreakdown();
                    s.Rebuild();
                    if (s.Camera.WatchedLive || s.monitor.carrying != 0) CctvFail(check, "a monitor that broke down still carried its cameras");
                    Dark("broken down");
                    breakdown.Notify_Repaired();
                    // (The mains switch it back on at the next utilities pass: done by hand here.)
                    if (power != null) power.PowerOn = true;
                    s.Rebuild();
                    if (!s.Camera.WatchedLive) CctvFail(check, "a monitor that was repaired did not carry its camera again");
                }
                else CctvFail(check, "the monitor cannot break down (no breakdown comp)");
                // The guard at the screens when the power goes: by real ticks, sent and seated by the work giver, then the screens go dark and the guard gets up (the job's own check of the post).
                if (power == null) return;
                IntVec3 start = s.posts.Start(s.op, s.sit, 3f, 6f);
                if (!start.IsValid)
                {
                    RPLog.Message("[RimPrison:test] cctv (dark screens): the guard getting up is not judged - no free cell for the guard to start from");
                    return;
                }
                s.posts.Place(s.op, start);
                bool Seated() => s.op.CurJobDef == RPPostDefOf.RP_ManPost && s.op.CurJob.targetA.Thing == s.monitor.parent && s.op.Position == s.sit;
                s.posts.Run(Seated, 2500);
                if (!Seated())
                {
                    RPLog.Message("[RimPrison:test] cctv (dark screens): the guard getting up is not judged - the guard did not sit at the working monitor");
                    return;
                }
                power.PowerOn = false;
                int left = s.posts.Run(() => !PostScene.OnPost(s.op, s.post), 200, () => power.PowerOn = false);
                if (PostScene.OnPost(s.op, s.post)) CctvFail(check, $"the guard stayed at a monitor that lost its power {left} ticks after it did (it is no post then)");
                power.PowerOn = true;
                s.posts.Shift(s.op, false);
            });
        }

        /// <summary>
        /// 4. Nine toggled cameras on one manned monitor: eight are watched, the ninth waits; a high-priority camera displaces a normal one; a camera taken off the screens is not watched at once (the gizmo, no beat) and its place
        /// goes to the one that waited. (Fails if the monitor carries more than eight, ignores the priority, or a toggle waits for the beat.)
        /// </summary>
        private static void CheckCctvFeeds(Map map)
        {
            Guarded("cctv: eight feeds to a monitor, the priority, the toggle", map, r =>
            {
                const string check = "feeds";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                List<CompSecurityCamera> cams = CctvCameras(s, 9);
                if (cams.Count < 9 || s.pm.Cameras.Count != 9)
                {
                    RPLog.Message($"[RimPrison:test] cctv (feeds): not run - {cams.Count} cameras put up, the prison lists {s.pm.Cameras.Count}");
                    return;
                }
                s.Sit();
                s.Rebuild();
                int Watched() => cams.Count(c => c.WatchedLive);
                // (By thing id: the scene's camera is the oldest, the last one put up is the ninth.)
                CompSecurityCamera ninth = cams[8], eighth = cams[7];
                if (Watched() != Cctv.FeedsPerMonitor || ninth.WatchedLive || !s.Camera.WatchedLive || !eighth.WatchedLive)
                    CctvFail(check, $"nine cameras on the screens of one manned monitor: {Watched()} watched (want {Cctv.FeedsPerMonitor}), the ninth watched {ninth.WatchedLive}, the first {s.Camera.WatchedLive}, the eighth {eighth.WatchedLive}; the monitor carries {s.monitor.carrying}");
                // A high-priority camera takes the place of the last normal one: by its own gizmo, at capacity (the monitor is full, the ninth waits), at once and with no beat - a gizmo that did not rebuild leaves the ninth waiting.
                Gizmo priority = ninth.CompGetGizmosExtra().FirstOrDefault(gz => gz is Command_Action);
                if (priority is not Command_Action priorityCommand)
                {
                    CctvFail(check, "the camera has no priority button");
                    return;
                }
                priorityCommand.action();
                if (ninth.priority != CctvPriority.High || Watched() != Cctv.FeedsPerMonitor || !ninth.WatchedLive || eighth.WatchedLive || !s.Camera.WatchedLive)
                    CctvFail(check, $"a high-priority ninth camera by its button, with no beat: it is {ninth.priority}, {Watched()} watched, the ninth {ninth.WatchedLive} (want it watched), the eighth {eighth.WatchedLive} (want it waiting)");
                // The first is taken off the screens by its own gizmo: at once, no beat, and the one that waited is carried.
                Gizmo toggle = s.Camera.CompGetGizmosExtra().FirstOrDefault(gz => gz is Command_Toggle);
                if (toggle is not Command_Toggle toggleCommand || !toggleCommand.isActive())
                {
                    CctvFail(check, "the camera has no toggle for the screens, or it is not on");
                    return;
                }
                toggleCommand.toggleAction();
                if (s.Camera.WatchedLive || s.Seen() || !eighth.WatchedLive || Watched() != Cctv.FeedsPerMonitor)
                    CctvFail(check, $"a camera taken off the screens, with no beat: watched {s.Camera.WatchedLive}, seen {s.Seen()}, the eighth (the one that waited) {eighth.WatchedLive}, {Watched()} watched");
                if (s.Camera.onScreens || toggleCommand.isActive()) CctvFail(check, "the camera's toggle did not take it off the screens");
                // The priority by its own gizmo, back to normal: the nine less the one off are all carried.
                priorityCommand.action();
                if (ninth.priority != CctvPriority.Normal || cams.Skip(1).Any(c => !c.WatchedLive) || s.Camera.WatchedLive)
                    CctvFail(check, $"the priority back to normal with the first off the screens: the ninth is {ninth.priority}, watched {string.Join("", cams.Select(c => c.WatchedLive ? 1 : 0))} (want 011111111)");
            });
        }

        /// <summary>
        /// The map's own beat refreshes the cache (<c>PrisonMap</c> calls <see cref="Cctv.Beat"/> every 250 ticks), proved in real ticks with nothing rebuilt by hand: a guard sits at the monitor and the camera is unwatched until the beat's
        /// tick, then watched; the guard goes off duty and the camera stays watched until the beat's tick, then is not (within 250 ticks, and on a beat tick, never before). (Fails if the beat's call is taken out of the map's pass,
        /// or something else rebuilds on its own timing.)
        /// </summary>
        private static void CheckCctvBeat(Map map)
        {
            Guarded("cctv: the map's beat refreshes the cache within 250 ticks", map, r =>
            {
                const string check = "beat";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                if (!s.Covers())
                {
                    RPLog.Message("[RimPrison:test] cctv (beat): not judged - the scene's camera does not cover the inmate's cell");
                    return;
                }
                // (The beat ran in the tick that was just finished: this tick or the one before it, as the clock is read after the tick.)
                bool OnBeat(int tick) => tick % 250 == 0 || (tick - 1) % 250 == 0;
                void Change(string what, bool toWatched)
                {
                    int started = Find.TickManager.TicksGame;
                    // (The field itself, not WatchedLive: it is the cache the beat writes.)
                    int ticks = s.posts.Run(() => s.Camera.watched == toWatched, 260);
                    int at = Find.TickManager.TicksGame;
                    if (s.Camera.watched != toWatched) CctvFail(check, $"{what}: the cache was not refreshed in {ticks} ticks of play with nothing rebuilt by hand (from tick {started}) - the map's beat does not rebuild it");
                    else if (!OnBeat(at)) CctvFail(check, $"{what}: the cache changed at tick {at}, which is not the map's beat (from tick {started}): something else rebuilt it");
                    else RPLog.Message($"[RimPrison:test] cctv (beat): {what} - the cache followed at tick {at}, {ticks} ticks after");
                }
                // A guard arrives: not watched until the beat.
                s.Sit();
                if (Cctv.OperatorOf(s.monitor.parent) != s.op || !s.post.OpenNow || s.Camera.watched)
                {
                    RPLog.Message($"[RimPrison:test] cctv (beat): not judged - the guard at the screens is not its operator (on duty {GuardPosts.OnDuty(s.op)}, open {s.post.OpenNow}) or the camera is watched already ({s.Camera.watched})");
                    return;
                }
                Change("a guard sat at the screens", toWatched: true);
                if (!s.Camera.watched) return;
                // The guard goes off duty: watched until the beat.
                s.Leave();
                s.posts.Shift(s.op, false);
                Change("the guard went off duty", toWatched: false);
            });
        }

        /// <summary>
        /// A camera that does not work (no power, broken down) gives its place on the screens to the next: nine cameras on one manned monitor, one of the first eight loses power (then breaks down) - the ninth takes its place, eight
        /// are watched, and its inspect line says it has no picture; its power back, it has its place again. (Fails if a dead camera keeps a place on a screen.)
        /// </summary>
        private static void CheckCctvDeadCameras(Map map)
        {
            Guarded("cctv: a dead camera gives its place on the screens to the next", map, r =>
            {
                const string check = "dead cameras";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                List<CompSecurityCamera> cams = CctvCameras(s, 9);
                if (cams.Count < 9 || s.pm.Cameras.Count != 9)
                {
                    RPLog.Message($"[RimPrison:test] cctv (dead cameras): not run - {cams.Count} cameras put up, the prison lists {s.pm.Cameras.Count}");
                    return;
                }
                s.Sit();
                s.Rebuild();
                int Watched() => cams.Count(c => c.watched);
                CompSecurityCamera dead = cams[2], ninth = cams[8];
                CompPowerTrader power = dead.parent.GetComp<CompPowerTrader>();
                if (power == null || !dead.watched || ninth.watched || Watched() != Cctv.FeedsPerMonitor)
                {
                    CctvFail(check, $"the check's own premise: the third camera is watched {dead.watched}, the ninth {ninth.watched}, {Watched()} watched (want the first eight), a power comp {power != null}");
                    return;
                }
                void Dead(string how)
                {
                    s.Rebuild();
                    if (dead.watched || !ninth.watched || Watched() != Cctv.FeedsPerMonitor)
                        CctvFail(check, $"a camera {how} on a manned monitor of nine: it is watched {dead.watched} (want no), the ninth {ninth.watched} (want it to have the place), {Watched()} watched (want {Cctv.FeedsPerMonitor})");
                    if (dead.CompInspectStringExtra() is not { } line || !line.Contains("RP_CctvInspectNoPicture".Translate().ToString())) CctvFail(check, $"a camera {how}: its inspect line says '{dead.CompInspectStringExtra()}', not that it has no picture");
                }
                power.PowerOn = false;
                Dead("with no power");
                power.PowerOn = true;
                s.Rebuild();
                if (!dead.watched || ninth.watched) CctvFail(check, $"a camera with its power back did not take its place again (watched {dead.watched}, the ninth {ninth.watched})");
                if (dead.parent.TryGetComp<CompBreakdownable>() is { } breakdown)
                {
                    r.Add(() =>
                    {
                        if (breakdown.BrokenDown) breakdown.Notify_Repaired();
                    });
                    breakdown.DoBreakdown();
                    Dead("broken down");
                    breakdown.Notify_Repaired();
                    power.PowerOn = true;
                    s.Rebuild();
                    if (!dead.watched) CctvFail(check, "a camera that was repaired did not take its place again");
                }
            });
        }

        /// <summary>
        /// A floor post is never put on a monitor's seat (its interaction cell): the cell is known as a seat, the cell for a post near it is another, and a post put up at the seat goes elsewhere - a guard on a floor post standing
        /// there would be counted as the monitor's operator. (Fails if the post's cell search does not skip a seat.)
        /// </summary>
        private static void CheckCctvSeat(Map map)
        {
            Guarded("cctv: a floor post is not put on a monitor's seat", map, r =>
            {
                const string check = "seat";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                if (!GuardPosts.IsMonitorSeat(map, s.sit)) CctvFail(check, $"the monitor's seat {s.sit} is not known as one");
                IntVec3 cell = GuardPosts.CellNear(map, s.sit);
                if (cell == s.sit) CctvFail(check, "the cell for a floor post nearest to a monitor's seat is the seat");
                Thing post = GuardPosts.Place(map, s.sit);
                if (post != null)
                {
                    r.Add(() =>
                    {
                        if (!post.Destroyed) post.Destroy();
                    });
                    if (post.Position == s.sit) CctvFail(check, "a floor post put up at a monitor's seat was put on it");
                }
                else RPLog.Message("[RimPrison:test] cctv (seat): no floor post could be put up near the seat (the post's cell is not judged)");
            });
        }

        /// <summary>
        /// The work giver scans the posts and nothing else, and the guard goes to the seat: its request is undefined (the game's region search over every building is skipped), what it scans is the posts' own list (the floor
        /// spots and the monitors), reach is asked of the seat and not of the building (a monitor's own cell is no place to go), so a walled-in seat is no job, and a monitor facing another way has its seat on the right side of it.
        /// (Fails if the giver asks for every building, or lets the game test reach to the building.)
        /// </summary>
        private static void CheckCctvWorkGiver(Map map)
        {
            Guarded("cctv: the work giver scans only the posts and reaches the seat", map, r =>
            {
                const string check = "work giver";
                CctvScene s = CctvScene.TryMake(map, r, check, reachableFromIntake: true);
                if (s == null) return;
                var giver = new WorkGiver_ManPost();
                if (!giver.PotentialWorkThingRequest.IsUndefined) CctvFail(check, $"the work giver's request is {giver.PotentialWorkThingRequest}: the game would search the regions around every guard for buildings, and test each of them");
                List<Thing> scanned = giver.PotentialWorkThingsGlobal(s.op).ToList();
                if (!scanned.Contains(s.monitor.parent) || scanned.Count != GuardPosts.Posts(map).Count || scanned.Any(t => t.TryGetComp<CompGuardPost>() == null))
                    CctvFail(check, $"the work giver scans {scanned.Count} things ({scanned.Count(t => t.TryGetComp<CompGuardPost>() == null)} of them no post) for {GuardPosts.Posts(map).Count} posts, the monitor among them {scanned.Contains(s.monitor.parent)}");
                if (!giver.AllowUnreachable) CctvFail(check, "the work giver lets the game test the guard's reach to the monitor's own cell, which is no place to go (reach is the seat's, asked in HasJobOnThing)");
                s.Leave();
                if (!s.post.OpenNow || !giver.HasJobOnThing(s.op, s.monitor.parent))
                {
                    RPLog.Message($"[RimPrison:test] cctv (work giver): the seat and the rotated monitor are not judged - the free guard has no job at the working monitor (open {s.post.OpenNow})");
                    return;
                }
                // A walled-in seat is no job, though the monitor's own cell can be walked to.
                Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                GenSpawn.Spawn(wall, s.sit, map);
                r.Add(() =>
                {
                    if (!wall.Destroyed) wall.Destroy();
                });
                if (giver.HasJobOnThing(s.op, s.monitor.parent) || GuardPosts.CanTake(s.op, s.post)) CctvFail(check, "a monitor whose seat is walled in was still offered to a guard");
                wall.Destroy();
                if (!giver.HasJobOnThing(s.op, s.monitor.parent)) CctvFail(check, "the monitor was not offered again when its seat was clear");
                // A monitor facing east: its seat is west of it, not the north-facing monitor's south.
                IntVec3 site = CctvSite(map, s.sit, 3f, c => map.reachability.CanReach(s.away, ThingUtility.InteractionCellWhenAt(RPCctvDefOf.RP_CctvMonitor, c, Rot4.East, map), PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Some)), Rot4.East);
                if (!site.IsValid)
                {
                    RPLog.Message("[RimPrison:test] cctv (work giver): the rotated monitor is not judged - no free cell for one facing east");
                    return;
                }
                CompCctvMonitor turned = SpawnTestMonitor(map, r, site, Rot4.East);
                IntVec3 seat = GuardPosts.StandCell(turned.parent);
                if (turned.parent.Rotation != Rot4.East || seat != ThingUtility.InteractionCellWhenAt(RPCctvDefOf.RP_CctvMonitor, site, Rot4.East, map) || turned.parent.OccupiedRect().Contains(seat) || !GuardPosts.IsMonitorSeat(map, seat))
                    CctvFail(check, $"a monitor facing {turned.parent.Rotation} at {site} has its seat at {seat} (its own cells {turned.parent.OccupiedRect()})");
                else if (!giver.HasJobOnThing(s.op, turned.parent)) CctvFail(check, $"a free guard has no job at a monitor facing east, whose seat {seat} is free and reachable (open {turned.parent.GetComp<CompGuardPost>().OpenNow})");
            });
        }

        /// <summary>
        /// 5. The policy off: today's rule - a working camera over the inmate sees them with nobody at any monitor - and a monitor's post is not open (nobody is sent to screens that carry nothing). Fails if the policy is ignored.
        /// </summary>
        private static void CheckCctvPolicyOff(Map map)
        {
            Guarded("cctv: the policy off is today's rule", map, r =>
            {
                const string check = "policy off";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                if (!s.Covers())
                {
                    RPLog.Message("[RimPrison:test] cctv (policy off): not judged - the scene's camera does not cover the inmate's cell");
                    return;
                }
                if (s.Seen()) CctvFail(check, "the check's own premise: the camera saw the inmate with the policy on and nobody at the monitor");
                CctvPolicy(r, false);
                if (!s.Seen() || !Mathf.Approximately(s.Watchers(), 0.5f)) CctvFail(check, $"with the policy off a working camera did not watch by itself (seen {s.Seen()}, {s.Watchers()} watchers)");
                if (s.post.OpenNow || s.post.StatusText() != "RP_CctvPostIdle".Translate().ToString()) CctvFail(check, $"with the policy off the monitor's post is open or does not say nobody is sent ('{s.post.StatusText()}')");
                s.Sit();
                if (GuardPosts.CanTake(s.op, s.post)) CctvFail(check, "with the policy off a guard would be sent to a monitor");
                // On again: the same camera is unwatched again (a flip is at once).
                s.Leave();
                CctvPolicy(r, true);
                if (s.Seen()) CctvFail(check, "with the policy back on the camera still saw the inmate with nobody at the monitor");
            });
        }

        // ---- the flags: a new game, an old save, the letter ---------------------------------------------------------------------------------------------

        /// <summary>The three saved flags as a game saves them (<see cref="Cctv.ExposeFlags"/>), for the round trip below.</summary>
        public sealed class CctvFlags : IExposable
        {
            public bool need, migrated, sent;

            public void ExposeData() => Cctv.ExposeFlags(ref need, ref migrated, ref sent);
        }

        /// <summary>The flags saved and loaded as a game does it (Scribe), through a file of their own, with the keys in <paramref name="strip"/> taken out of the file before it is read (an old save has none).</summary>
        private static CctvFlags CctvRoundTrip(CctvFlags flags, params string[] strip)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "rp_cctv_roundtrip.xml");
            CctvFlags loaded = null;
            try
            {
                SafeSaver.Save(path, "cctvtest", () => Scribe_Deep.Look(ref flags, "flags"));
                if (strip.Length > 0)
                {
                    XDocument doc = XDocument.Load(path);
                    foreach (string key in strip) doc.Descendants(key).Remove();
                    doc.Save(path);
                }
                Scribe.loader.InitLoading(path);
                try
                {
                    Scribe_Deep.Look(ref loaded, "flags");
                    Scribe.loader.FinalizeLoading();
                }
                catch
                {
                    Scribe.ForceStop();
                    throw;
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
            return loaded;
        }

        /// <summary>
        /// 6a. An old save: a file with the policy's and the migration's keys stripped loads false and false (today's rule, the letter still owed); a turned-off policy survives a save and a load (with the migration kept), and so
        /// does a turned-on one. Fails if a flag's missing key loads true.
        /// </summary>
        private static void CheckCctvOldSave(Map map)
        {
            Guarded("cctv: an old save loads with today's rule", map, r =>
            {
                const string check = "old save";
                CctvFlags on = CctvRoundTrip(new CctvFlags { need = true, migrated = true, sent = true });
                if (on == null || !on.need || !on.migrated || !on.sent) CctvFail(check, $"flags set true did not come back true from a save and a load ({on?.need}/{on?.migrated}/{on?.sent})");
                CctvFlags off = CctvRoundTrip(new CctvFlags { need = false, migrated = true, sent = false });
                if (off == null || off.need || !off.migrated || off.sent) CctvFail(check, $"a policy turned off with the migration done did not come back so ({off?.need}/{off?.migrated}/{off?.sent})");
                CctvFlags old = CctvRoundTrip(new CctvFlags { need = true, migrated = true, sent = false }, "camerasNeedOperator", "cctvMigrated");
                if (old == null || old.need || old.migrated || old.sent) CctvFail(check, $"a save without the two keys loaded {old?.need}/{old?.migrated}/{old?.sent} (want false for each: today's rule, the letter owed)");
            });
        }

        /// <summary>
        /// 6b. An old save's game (policy off, migration not done) is offered the switch by one letter when a monitor is first built, never before one exists and never twice; accepting turns the policy on and settles the migration,
        /// declining settles it and leaves the policy off. Fails if the letter is sent before a monitor, twice, or not at all.
        /// </summary>
        private static void CheckCctvOldSaveLetter(Map map)
        {
            Guarded("cctv: the old save's letter", map, r =>
            {
                const string check = "letter";
                PrisonGame g = PrisonGame.Instance;
                (bool need, bool migrated, bool sent) = (g.camerasNeedOperator, g.cctvMigrated, g.cctvLetterSent);
                r.Add(() =>
                {
                    (g.camerasNeedOperator, g.cctvMigrated, g.cctvLetterSent) = (need, migrated, sent);
                    Cctv.RebuildAll();
                });
                IEnumerable<ChoiceLetter_PrisonDecision> Letters() => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(Cctv.DecisionDef));
                for (int round = 0; round < 2; round++)
                {
                    bool accept = round == 0;
                    string how = accept ? "accepted" : "declined";
                    foreach (ChoiceLetter_PrisonDecision old in Letters().ToList()) Find.LetterStack.RemoveLetter(old);
                    (g.camerasNeedOperator, g.cctvMigrated, g.cctvLetterSent) = (false, false, false);
                    Cctv.RebuildAll();
                    // Not before a monitor exists.
                    Cctv.Beat(map, PrisonMap.Of(map));
                    if (Letters().Any()) CctvFail(check, $"({how}) a letter was sent before any monitor was built");
                    IntVec3 site = CctvSite(map, IntakeSystem.IntakeSpot(map), 6f);
                    IntVec3 second = site.IsValid ? CctvSite(map, site, 6f) : IntVec3.Invalid;
                    if (!site.IsValid || !second.IsValid || second == site)
                    {
                        RPLog.Message("[RimPrison:test] cctv (letter): not run - no free cell for two monitors");
                        return;
                    }
                    SpawnTestMonitor(map, r, site);
                    if (Letters().Count() != 1 || !g.cctvLetterSent) CctvFail(check, $"({how}) a monitor built in a game with today's rule sent {Letters().Count()} letters (want one), sent flag {g.cctvLetterSent}");
                    if (g.camerasNeedOperator || g.cctvMigrated) CctvFail(check, $"({how}) the letter changed the policy ({g.camerasNeedOperator}) or settled the migration ({g.cctvMigrated}) before it was answered");
                    SpawnTestMonitor(map, r, second);
                    if (Letters().Count() != 1) CctvFail(check, $"({how}) a second monitor sent another letter ({Letters().Count()} in all)");
                    if (Letters().FirstOrDefault() is not { } letter) continue;
                    letter.answered = true;
                    if (accept) PrisonDecisions.Accept(letter);
                    else PrisonDecisions.Decline(letter);
                    if (g.camerasNeedOperator != accept || !g.cctvMigrated) CctvFail(check, $"({how}) the letter left the policy {g.camerasNeedOperator} (want {accept}) and the migration {g.cctvMigrated} (want done)");
                    Find.LetterStack.RemoveLetter(letter);
                }
            });
        }

        // ---- special measures, the overlay, the oracle -----------------------------------------------------------------------------------------------------

        /// <summary>
        /// 7. The state's chief in post suspends the floor posts, never a monitor's: the monitor is open, says so, is scanned by the work giver and is the post a free guard is sent to; and the danger map's and the Deployment tab's
        /// floor-post helpers do not count a monitor as a floor post. (Fails if the monitor is gated on the chief like a floor post.)
        /// </summary>
        private static void CheckCctvSpecialMeasures(Map map)
        {
            Guarded("cctv: the state's chief does not suspend a monitor's post", map, r =>
            {
                const string check = "special measures";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                StateOversight o = PrisonGame.Instance.story.oversight;
                Pawn chief = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                CompStaff cc = chief.GetComp<CompStaff>();
                (Pawn chief0, bool posted0) = (o.chief, cc.statePosted);
                r.Add(() => (o.chief, cc.statePosted) = (chief0, posted0));
                (o.chief, cc.statePosted) = (chief, true);
                if (!SpecialMeasures.ChiefInPost)
                {
                    CctvFail(check, "the check's own premise: the state's chief is not in post");
                    return;
                }
                s.posts.Place(s.op, s.sit);
                var giver = new WorkGiver_ManPost();
                if (!s.post.OpenNow || s.post.StatusText() == "RP_PostSuspended".Translate().ToString()) CctvFail(check, $"with the state's chief in post the monitor's post is suspended (open {s.post.OpenNow}, '{s.post.StatusText()}')");
                if (giver.ShouldSkip(s.op)) CctvFail(check, "with the state's chief in post the work giver skips the monitors");
                else if (!giver.HasJobOnThing(s.op, s.monitor.parent)) CctvFail(check, "with the state's chief in post the work giver has no job for a free guard on a monitor");
                if (GuardPosts.PostFor(s.op) != s.post) CctvFail(check, $"with the state's chief in post a free guard is not sent to the monitor (given {GuardPosts.PostFor(s.op)?.parent.LabelShort ?? "no post"})");
                if (!GuardPosts.Posts(map).Contains(s.post)) CctvFail(check, "the monitor is not among the posts the Deployment tab lists");
                bool floorPostNear = map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost).Any(t => t.Position.InHorDistOf(s.monitor.parent.Position, GuardPosts.NearRadius));
                if (!floorPostNear && GuardPosts.PostNear(map, s.monitor.parent.Position, GuardPosts.NearRadius)) CctvFail(check, "a monitor is counted as a floor post near the patch it stands at (the danger map would not offer a post there)");
                s.Rebuild();
                if (!s.Seen()) CctvFail(check, "with the state's chief in post the guard at the monitor did not make the camera watched");
            });
        }

        /// <summary>
        /// 8. The coverage overlay follows the cache: with an operator at the monitor it counts the camera over the cell, and after the operator leaves and the beat has run it is out of date (its device state changed) and, rebuilt,
        /// shows nothing there. (Fails if the overlay's devices do not include the watched state, or it still draws a camera nobody watches.)
        /// </summary>
        private static void CheckCctvOverlay(Map map)
        {
            Guarded("cctv: the coverage overlay changes when the operator leaves", map, r =>
            {
                const string check = "overlay";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                if (!s.Covers())
                {
                    RPLog.Message("[RimPrison:test] cctv (overlay): not judged - the scene's camera does not cover the inmate's cell");
                    return;
                }
                CoverageOverlay overlay = s.pm.Coverage;
                r.Add(() => overlay.Rebuild());
                s.Sit();
                s.Rebuild();
                overlay.Rebuild();
                if (!Mathf.Approximately(overlay.At(s.d.cell), 0.5f) || overlay.Stale(s.pm)) CctvFail(check, $"with an operator at the monitor the overlay counts {overlay.At(s.d.cell)} on the cell under the camera (want 0.5), out of date {overlay.Stale(s.pm)}");
                s.Leave();
                s.Rebuild();
                if (!overlay.Stale(s.pm)) CctvFail(check, "after the operator left and the beat ran, the overlay did not see that a device changed (it would not be rebuilt)");
                overlay.Rebuild();
                if (overlay.At(s.d.cell) > 0f) CctvFail(check, $"the overlay counts {overlay.At(s.d.cell)} on a cell whose only camera nobody watches");
                if (!Mathf.Approximately(overlay.At(s.d.cell), s.Watchers())) CctvFail(check, $"the overlay ({overlay.At(s.d.cell)}) and the count ({s.Watchers()}) differ");
            });
        }

        /// <summary>
        /// 9. The cache oracle (<see cref="Shadow.CheckCctv"/>, <see cref="Shadow.CheckCctvStale"/>). After a rebuild the cache and a plain walk (the manned monitors' room for feeds, a camera's rank among those on the screens)
        /// differ nowhere, with nobody at the monitor, with one, with the policy off and with nine cameras; a cache made wrong by hand is found. And before the rebuild, on the map's beat: the walk finds a cache gone stale -
        /// a camera taken off the screens, the priority changed at capacity, the policy flipped, each by hand with no rebuild - while what the beat is owed to refresh (the operator who left, the power that was cut) is not
        /// called stale. (Fails if the walk and the cache are not the same rule, or the oracle cannot tell a wrong or a stale cache.)
        /// </summary>
        private static void CheckCctvOracle(Map map)
        {
            Guarded("cctv: the cache and a plain walk agree, and a stale cache is found", map, r =>
            {
                const string check = "oracle";
                CctvScene s = CctvScene.TryMake(map, r, check);
                if (s == null) return;
                void Agree(string when)
                {
                    s.Rebuild();
                    int differ = Cctv.Mismatches(s.map, s.pm, out int asked);
                    if (differ != 0) CctvFail(check, $"{when}: the cache differs from the plain walk on {differ} of {asked} cameras right after a rebuild");
                }
                Agree("nobody at the monitor");
                s.Sit();
                Agree("a guard at the monitor");
                CctvCameras(s, 9);
                Agree($"{s.pm.Cameras.Count} cameras on one monitor");
                s.pm.Cameras[s.pm.Cameras.Count - 1].priority = CctvPriority.High;
                Agree("a high-priority camera");
                CctvPolicy(r, false);
                Agree("the policy off");
                CctvPolicy(r, true);
                // (The oracle can tell: a cache wrong by hand is found, and a rebuild makes it right.)
                s.Rebuild();
                CompSecurityCamera first = s.pm.Cameras[0];
                first.watched = !first.watched;
                if (Cctv.Mismatches(s.map, s.pm, out _) == 0) CctvFail(check, "a cache made wrong by hand was not found by the walk");
                s.Rebuild();
                if (Cctv.Mismatches(s.map, s.pm, out _) != 0) CctvFail(check, "a rebuild did not put a wrong cache right");

                // Before the rebuild: the map's beat, run by hand, with one change made to the cameras that nothing rebuilt for. How many cameras the walk found stale (the run's own count of them put back, as a check's
                // deliberate fault is not the run's).
                int StaleBy(Action change)
                {
                    s.Rebuild();
                    (int differ0, string note0) = (Shadow.cctvDiffer, Shadow.first);
                    change();
                    Cctv.Beat(s.map, s.pm);
                    int found = Shadow.cctvDiffer - differ0;
                    (Shadow.cctvDiffer, Shadow.first) = (differ0, note0);
                    return found;
                }
                CompSecurityCamera ninth = s.pm.Cameras[s.pm.Cameras.Count - 1];
                CompPowerTrader monitorPower = s.monitor.parent.GetComp<CompPowerTrader>();
                int calm = StaleBy(() => { });
                if (calm != 0) CctvFail(check, $"a beat with nothing changed found {calm} cameras stale");
                int left = StaleBy(() => s.Leave());
                if (left != 0) CctvFail(check, $"a beat after the operator left found {left} cameras stale (who sits is the beat's to refresh, up to 250 ticks)");
                s.Sit();
                if (monitorPower != null)
                {
                    int cut = StaleBy(() => monitorPower.PowerOn = false);
                    monitorPower.PowerOn = true;
                    if (cut != 0) CctvFail(check, $"a beat after the monitor lost its power found {cut} cameras stale (power is the beat's to refresh)");
                }
                s.Sit();
                int toggled = StaleBy(() => first.onScreens = false);
                first.onScreens = true;
                if (toggled == 0) CctvFail(check, "a camera taken off the screens by hand, with no rebuild, was not found stale by the beat's walk before it rebuilt");
                int reprioritised = StaleBy(() => ninth.priority = ninth.priority == CctvPriority.High ? CctvPriority.Normal : CctvPriority.High);
                if (reprioritised == 0) CctvFail(check, "a priority changed by hand at capacity, with no rebuild, was not found stale by the beat's walk before it rebuilt");
                int flipped = StaleBy(() => PrisonGame.Instance.camerasNeedOperator = false);
                PrisonGame.Instance.camerasNeedOperator = true;
                if (flipped == 0) CctvFail(check, "the policy flipped by hand, with no rebuild, was not found stale by the beat's walk before it rebuilt");
                s.Rebuild();
                if (Cctv.Mismatches(s.map, s.pm, out _) != 0) CctvFail(check, "the beat's rebuild did not leave the cache and the plain walk agreeing");
            });
        }

        // ---- the penitentiary ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// 11. The state penitentiary's control room: four monitors, each its own post, two on the day hours and two on the night hours (every hour has two open); the cameras it puts first (the yard, the canteens, the gates)
        /// are high priority and fit on two screens; two guards at the two monitors open now carry every one of them, working cameras that they are. (Fails if the layout has no monitors, the hours are not two and two, or the
        /// high-priority cameras are not watched with two operators.) Only in a run on the penitentiary.
        /// </summary>
        private static void CheckCctvPenitentiary(Map map)
        {
            if (PrisonBuilder.Kind != Facility.Penitentiary) return;
            Guarded("cctv: the penitentiary's control room", map, r =>
            {
                const string check = "penitentiary";
                PrisonMap pm = PrisonMap.Of(map);
                // (The guards put at the screens are taken down before this runs: the cache is worked out again without them.)
                r.Add(() => Cctv.Rebuild(map));
                IntVec3 origin = PrisonBuilder.Origin(map);
                List<CompGuardPost> monitors = map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor).Select(t => t.TryGetComp<CompGuardPost>()).Where(p => p != null).OrderBy(p => p.parent.thingIDNumber).ToList();
                if (monitors.Count != 4)
                {
                    CctvFail(check, $"the control room has {monitors.Count} monitors (want 4)");
                    return;
                }
                for (int hour = 0; hour < 24; hour++)
                {
                    int open = monitors.Count(p => p.CoversHour(hour));
                    if (open != 2) CctvFail(check, $"at {hour}:00 {open} monitors are open (want two: two on the day hours and two on the night hours)");
                }
                if (monitors.Count(p => p.CoversHour(12)) != 2 || monitors.Count(p => p.CoversHour(2)) != 2 || monitors.Any(p => p.CoversHour(12) && p.CoversHour(2))) CctvFail(check, "the monitors are not two for the day and two other for the night");
                foreach (CompGuardPost p in monitors)
                {
                    // (Both in the layout's own cells, local to the plot: the seat is the cell in front of the monitor, nothing may stand on it - the room's heater did, in the first round - and the gate's way is not the monitor's.)
                    IntVec3 sit = GuardPosts.StandCell(p.parent);
                    if (!sit.Standable(map) || !map.reachability.CanReach(origin + new IntVec3(59, 0, 6), sit, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors)))
                        CctvFail(check, $"nobody can sit at the monitor at {p.parent.Position - origin}: its seat {sit - origin} is not standable ({string.Join(", ", sit.GetThingList(map).Select(t => t.def.defName))} on it) or not reachable from the gate");
                }
                // The control room's north door opens onto the cell just inside it: a monitor stands on neither it nor the cells beside it that the staff walk across.
                IntVec3 inside = origin + new IntVec3(59, 0, 84);
                foreach (CompGuardPost p in monitors)
                    if (p.parent.OccupiedRect().Contains(inside) || p.parent.OccupiedRect().Contains(inside + IntVec3.West)) CctvFail(check, $"the monitor at {p.parent.Position - origin} stands in the way of the control room's north door (the cell inside it, {inside - origin})");
                if (!inside.Standable(map)) CctvFail(check, $"the cell inside the control room's north door, {inside - origin}, is not standable ({string.Join(", ", inside.GetThingList(map).Select(t => t.def.defName))} on it)");
                // The cameras that go on the screens first.
                List<CompSecurityCamera> high = pm.Cameras.Where(c => c.priority == CctvPriority.High).ToList();
                if (high.Count < 8 || high.Count > 2 * Cctv.FeedsPerMonitor) CctvFail(check, $"{high.Count} cameras are high priority (want from 8 to {2 * Cctv.FeedsPerMonitor}: the yard, the canteens, the gates)");
                if (high.Any(c => !c.onScreens)) CctvFail(check, "a high-priority camera is not on the screens");
                // Two guards at the two monitors open now, the real ones' posts put aside as they are in the scenes.
                int hourNow = GenLocalDate.HourOfDay(map);
                List<CompGuardPost> openNow = monitors.Where(p => p.CoversHour(hourNow)).ToList();
                var posts = new PostScene(map, r);
                IntVec3 away = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                CctvPolicy(r, true);
                foreach (CompGuardPost p in openNow)
                    posts.Place(posts.Guard(away), GuardPosts.StandCell(p.parent));
                Cctv.Rebuild(map);
                int watched = pm.Cameras.Count(c => c.WatchedLive);
                int unseen = high.Count(c => !c.WatchedLive);
                if (unseen > 0 || watched > 2 * Cctv.FeedsPerMonitor || watched < high.Count)
                    CctvFail(check, $"with two operators at the two monitors open at {hourNow}:00, {unseen} of {high.Count} high-priority cameras are not watched, and {watched} of {pm.Cameras.Count} cameras are (the two monitors carry at most {2 * Cctv.FeedsPerMonitor})");
                RPLog.Message($"[RimPrison:test] cctv (penitentiary): {monitors.Count} monitors, {high.Count} high-priority cameras of {pm.Cameras.Count}, {watched} watched with two operators at {hourNow}:00");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------------------

        private static int cctvSeedMonitorId = -1, cctvSeedGuardId = -1, cctvCarriedAtSave, cctvCarriedAtLoad;
        private static readonly List<int> cctvSeedCameraIds = new List<int>();
        private static string cctvAtSave, cctvAtLoad;
        private static List<TimeAssignmentDef> cctvSeedTimetable;

        /// <summary>The cameras' toggles, priorities and what the monitors carry, worked out now (the cache is rebuilt first: the snapshot is of the game as it is saved), with the three flags.</summary>
        private static string CctvDescribe(PrisonGame g, Map map)
        {
            Cctv.Rebuild(map);
            return $"{g.camerasNeedOperator}/{g.cctvMigrated}/{g.cctvLetterSent}:{CctvCache(map)}";
        }

        /// <summary>The cache as it stands (never rebuilt here): per camera its toggle, priority, watched answer and the monitor that carries it.</summary>
        private static string CctvCache(Map map) =>
            string.Join(",", PrisonMap.Of(map).Cameras.OrderBy(c => c.parent.thingIDNumber).Select(c => $"{c.parent.thingIDNumber}:{c.onScreens}:{(int)c.priority}:{c.watched}:{c.screenId}"));

        /// <summary>The cameras the seed's monitor carries now, in the cache.</summary>
        private static int CctvCarried(Map map) => PrisonMap.Of(map).Cameras.Count(c => c.watched && c.screenId == cctvSeedMonitorId);

        /// <summary>
        /// Saveload plan, before the snapshot: the letter's sent flag off its default, two cameras (one off the screens, one high priority; a third left as it is), a monitor and a real guard who mans it - on duty (their timetable
        /// made work all day if it was not), sent to it with the job that holds the seat, standing on its cell - and the cache worked out. The seed fails if the monitor carries nothing at the save (a seed that watched nothing would
        /// pass whether or not the load rebuilt the cache: <see cref="RecordCctvForReload"/>). The snapshot reads the flags, the toggles, the priorities and the answers back after the reload; the cache as it stands right after the load is
        /// held against the one the save had (<see cref="CheckCctvAfterReload"/>).
        /// </summary>
        private static void SeedCctvForReload(Map map)
        {
            cctvSeedMonitorId = cctvSeedGuardId = -1;
            cctvSeedTimetable = null;
            cctvSeedCameraIds.Clear();
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            // (An eligible guard: on duty now if there is one, else one who is made so below. Not the warden, not one in the care reserve, who stands at no post.)
            Pawn guard = map.mapPawns.FreeColonistsSpawned
                .Where(x => GuardUtility.IsGuard(x) && !x.Downed && !CareDetail.IsReserve(x) && x.GetComp<CompStaff>() is { role.warden: false } && x.timetable != null)
                .OrderByDescending(GuardPosts.OnDuty).ThenBy(x => x.thingIDNumber).FirstOrDefault();
            if (guard == null || pm == null)
            {
                reloadProblems.Add("saveload: no guard to sit at a monitor for the control room's seed");
                return;
            }
            IntVec3 site = CctvSite(map, guard.Position, 2f);
            if (!site.IsValid)
            {
                reloadProblems.Add($"saveload: no free cell for a monitor near {guard.Position}");
                return;
            }
            if (!GuardPosts.OnDuty(guard))
            {
                // (Put back after the load: the hourly pass writes the timetable again anyway.)
                cctvSeedTimetable = Enumerable.Range(0, 24).Select(h => guard.timetable.GetAssignment(h)).ToList();
                for (int h = 0; h < 24; h++) guard.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
            }
            // (Not rolled back by this seed: the things stay for the save, and the check after the load takes them down.)
            g.cctvLetterSent = true;
            var monitor = (Building)ThingMaker.MakeThing(RPCctvDefOf.RP_CctvMonitor);
            monitor.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(monitor, site, map, Rot4.North);
            if (monitor.GetComp<CompPowerTrader>() is { } power) power.PowerOn = true;
            cctvSeedMonitorId = monitor.thingIDNumber;
            cctvSeedGuardId = guard.thingIDNumber;
            var cams = new List<CompSecurityCamera>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(site, 12f, true))
            {
                if (cams.Count == 3) break;
                if (c.DistanceTo(site) < 3f || !DarkScene.Free(map, c) || c.GetFirstPawn(map) != null) continue;
                var camera = (Building)ThingMaker.MakeThing(RPDefOf.RP_SecurityCamera);
                camera.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(camera, c, map);
                if (camera.GetComp<CompPowerTrader>() is { } cp) cp.PowerOn = true;
                cams.Add(camera.GetComp<CompSecurityCamera>());
                cctvSeedCameraIds.Add(camera.thingIDNumber);
            }
            if (cams.Count == 3)
            {
                cams[0].onScreens = false;
                cams[1].priority = CctvPriority.High;
            }
            else reloadProblems.Add($"saveload: only {cams.Count} cameras could be put up for the control room's seed");
            // (The guard at the screens, on the job that holds the seat: sat down by hand, with nothing ticked before the save.)
            guard.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            guard.pather?.StopDead();
            guard.Position = GuardPosts.StandCell(monitor);
            guard.Notify_Teleported();
            guard.jobs?.StartJob(JobMaker.MakeJob(RPPostDefOf.RP_ManPost, monitor), JobCondition.InterruptForced);
            Cctv.Rebuild(map);
            cctvAtSave = null;
        }

        /// <summary>Saveload plan, after the snapshot is taken: what the cache says now, the answer the save had.</summary>
        private static void RecordCctvForReload(Map map)
        {
            if (cctvSeedMonitorId < 0) return;
            Cctv.Rebuild(map);
            cctvAtSave = CctvCache(map);
            cctvCarriedAtSave = CctvCarried(map);
            // (The seed must judge: a monitor that carries nothing at the save would let a load that never rebuilt the cache pass, as the cache starts as nothing watched.)
            if (cctvCarriedAtSave < 1)
            {
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == cctvSeedGuardId);
                Thing monitor = map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor).FirstOrDefault(t => t.thingIDNumber == cctvSeedMonitorId);
                reloadProblems.Add($"saveload: the control room's seed has nothing watched at the save, so the load proves nothing (the monitor carries {cctvCarriedAtSave}; the guard {guard?.LabelShort ?? "gone"} on duty {guard != null && GuardPosts.OnDuty(guard)}, " +
                    $"at {guard?.Position} for the seat {(monitor != null ? GuardPosts.StandCell(monitor).ToString() : "?")}, job {guard?.CurJobDef?.defName ?? "none"}; the post open {monitor?.TryGetComp<CompGuardPost>()?.OpenNow}; {PrisonMap.Of(map).Cameras.Count} cameras)");
            }
        }

        /// <summary>Saveload plan, first thing after the load (before the snapshot is read, which works the cache out again): what the cache says as the game left it.</summary>
        private static void RecordCctvAtLoad(Map map)
        {
            cctvAtLoad = cctvSeedMonitorId < 0 ? null : CctvCache(map);
            cctvCarriedAtLoad = cctvSeedMonitorId < 0 ? 0 : CctvCarried(map);
        }

        /// <summary>
        /// Saveload plan, after the load: the cache was rebuilt at load and is the beat's answer, not a stale one (the guard was at the screens when the game was saved, and the cache the load made is the one the save had, and the
        /// one the next beat makes); the seed's things are taken down.
        /// </summary>
        private static void CheckCctvAfterReload(Map map)
        {
            if (cctvSeedMonitorId < 0) return;
            PrisonMap pm = PrisonMap.Of(map);
            Thing monitor = map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor).FirstOrDefault(t => t.thingIDNumber == cctvSeedMonitorId);
            if (monitor == null) reloadProblems.Add("saveload: the control room's seed monitor did not come back after the save");
            else if (cctvAtSave == null || cctvAtLoad != cctvAtSave) reloadProblems.Add($"saveload: the cameras' watched answers right after the load are not the save's: {cctvAtLoad} (saved {cctvAtSave})");
            else if (cctvCarriedAtSave < 1 || cctvCarriedAtLoad != cctvCarriedAtSave) reloadProblems.Add($"saveload: the seed's monitor carried {cctvCarriedAtSave} cameras at the save and carries {cctvCarriedAtLoad} right after the load (before anything rebuilt the cache): the load did not work the watched answers out");
            else
            {
                Cctv.Beat(map, pm);
                string beat = CctvCache(map);
                if (beat != cctvAtLoad) reloadProblems.Add($"saveload: the first answer after the load ({cctvAtLoad}) is not the beat's ({beat})");
                else RPLog.Message($"[RimPrison:test] saveload cctv: the toggles, the priorities and the answers are intact, the monitor carries {cctvCarriedAtLoad} cameras right after the load, and the cache after the load is the beat's");
            }
            // (The guard's timetable, if the seed made them work all day.)
            if (cctvSeedTimetable != null && map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == cctvSeedGuardId) is { timetable: not null } seeded)
                for (int h = 0; h < 24; h++) seeded.timetable.SetAssignment(h, cctvSeedTimetable[h]);
            // (Put back as the game had it.)
            foreach (int id in cctvSeedCameraIds)
                if (pm.Cameras.FirstOrDefault(c => c.parent.thingIDNumber == id) is { } cam) cam.parent.Destroy();
            monitor?.Destroy();
            PrisonGame.Instance.cctvLetterSent = false;
            Cctv.Rebuild(map);
            cctvSeedMonitorId = cctvSeedGuardId = -1;
            cctvSeedTimetable = null;
            cctvSeedCameraIds.Clear();
        }
    }

    /// <summary>Autotest: the policy and the migration flag as the starter builder begins (see <see cref="AutoTest.cctvFlagsAtBuild"/>): a new game's flags are set before it, on any start, whether or not its facility has a monitor.</summary>
    [HarmonyPatch(typeof(PrisonBuilder), nameof(PrisonBuilder.BuildStarter))]
    [TestOnly]
    public static class Patch_PrisonBuilder_BuildStarter_CctvFlags
    {
        public static void Prefix()
        {
            if (PrisonGame.Instance is { } g) AutoTest.cctvFlagsAtBuild.Add((g.camerasNeedOperator, g.cctvMigrated));
        }
    }
}
