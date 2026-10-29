using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The count's checks, round 2 (the review's fixes): a count called while another runs finishes it first; a runaway's own record (the misconduct path, the stowaway at the ramp) tells the warden nothing, and a sighting
    /// or a catch is told or dropped; a stowaway who missed the shuttle goes the ordinary way; the guard's walk gives way to an alarm, is never taken from a post or a tower, and counts its wing when it is finished.
    /// Self-contained like the rest (<see cref="CountScene"/>), rolled back.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckCountRoundTwo(Map map)
        {
            CheckCountNewOverRunning(map);
            CheckCountHiddenRecord(map);
            CheckCountStowawayAtTheRamp(map);
            CheckCountGuardJobRuns(map);
        }

        /// <summary>
        /// The pawn's name as a word of the text, not a piece of another word (the made-up inmates have short names: "Ri" is in "prison", "Richard" and "Rick", which a plain Contains found in the letter of the runner before and in any
        /// message that names another).
        /// </summary>
        private static bool NamesPawn(string text, Pawn p, bool ignoreCase = false) => Regex.IsMatch(text, $@"\b{Regex.Escape(p.LabelShort)}\b", ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

        private static bool CountSaid(Pawn p) => LiveMessages().Any(m => NamesPawn(m.text, p));

        // ---- A new count over a running one --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A count called while the one before it still runs (a regime with two of its hours side by side) does not overwrite it: the running one is finished as it stands (nobody walked: missed, with its line) and the
        /// new one begins with nothing carried over.
        /// </summary>
        private static void CheckCountNewOverRunning(Map map)
        {
            Guarded("count: a new count never overwrites a running one", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonMap pm = t.pm;
                t.GoneUnseen(EscapeWay.Walked);
                int now = Find.TickManager.TicksGame, later = now + GenDate.TicksPerHour;
                CountSystem.Begin(t.map, pm, t.g, now);
                if (!pm.count.Running)
                {
                    CountFail("new over running", "the check's own premise: a count called with a runner of the wing hidden has no wing to walk");
                    return;
                }
                pm.count.writeUps = 3;
                CountSystem.Begin(t.map, pm, t.g, later);
                CountState c = pm.count;
                if (!c.Running || c.startedTick != later || c.walked != 0 || c.writeUps != 0 || c.wingsLeft.Count == 0)
                    CountFail("new over running", $"the new count did not begin clean: running {c.Running}, started {c.startedTick} (wanted {later}), walked {c.walked}, written up {c.writeUps}, wings left {c.wingsLeft.Count}");
                if (c.lastMissedTick != later || t.Lines("RP_Chr_CountMissed".Translate(CountSystem.ClockAt(map, later)).Resolve()) != 1)
                    CountFail("new over running", $"the count it replaced was not finished as missed with its line (missed at {c.lastMissedTick}, wanted {later}; lines {t.Lines("RP_Chr_CountMissed".Translate(CountSystem.ClockAt(map, later)).Resolve())})");
                RPLog.Message("[RimPrison:test] count: a count called over a running one finished it as missed and began clean");
            });
        }

        // ---- A hidden runner's own record ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The escape attempt that starts a hidden escape through the misconduct path is written to the file unseen and tells nobody: no message, no tick on the desk board, no letter; a guard's sight of the runner on the map
        /// (the beat) tells the held letter; one who is caught or gives up before any count is dropped quietly; and the same attempt a guard sees is told as ever (the control).
        /// </summary>
        private static void CheckCountHiddenRecord(Map map)
        {
            Guarded("count: a runner nobody saw has a record that tells nothing", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                Pawn p = t.Inmate(t.s.cell);
                CompInmate c = p.Inmate();
                if (!EscapeUtility.TryFindEscapeRoute(p, out _))
                {
                    RPLog.Message("[RimPrison:test] count: no way off the map from the scene's spot, the hidden record is not judged");
                    return;
                }
                SecurityLevelDef level = c.security;
                int board0 = g.today.misconducts, onLevel0 = g.today.MisconductsOn(level);
                bool began = MisconductUtility.TryMisbehave(p, c, pm, RPShuttleDefOf.RP_EscapeAttempt);
                if (!began || !CountSystem.IsHidden(p))
                {
                    RPLog.Message($"[RimPrison:test] count: the escape attempt did not begin a hidden escape (began {began}, hidden {CountSystem.IsHidden(p)}), the hidden record is not judged");
                    return;
                }
                MisconductRecord rec = c.records.LastOrDefault();
                if (rec == null || rec.def != RPShuttleDefOf.RP_EscapeAttempt || rec.seen) CountFail("hidden record", $"the escape attempt nobody saw is not in the file as unseen (last record {rec?.def?.defName}, seen {rec?.seen})");
                if (CountSaid(p)) CountFail("hidden record", "the warden was given a message about an escape attempt nobody saw");
                if (g.today.misconducts != board0 || g.today.MisconductsOn(level) != onLevel0) CountFail("hidden record", $"the desk board counted a misconduct nobody saw (today {board0} -> {g.today.misconducts}, wing {onLevel0} -> {g.today.MisconductsOn(level)})");
                if (r.NewLetters.Any()) CountFail("hidden record", "a letter was sent of an escape attempt nobody saw: " + string.Join(" // ", t.LettersTold()));

                // A guard comes in sight of them on the map: the held letter is told, the record is gone.
                t.s.Watch(near: true);
                CountSystem.Tick(map, pm);
                if (pm.count.HiddenOf(p) != null || t.Told("RP_Count_SightedLabel".Translate().Resolve()) != 1)
                    CountFail("hidden record", $"a runner on the map a guard came in sight of was not told once (still held {pm.count.HiddenOf(p) != null}, {t.Told("RP_Count_SightedLabel".Translate().Resolve())} letter(s))");

                // The same attempt a guard sees is told as it always was (the message, the desk board): the control for the silence above.
                Pawn seen = t.Inmate(t.s.cell);
                int board1 = g.today.misconducts;
                bool beganSeen = MisconductUtility.TryMisbehave(seen, seen.Inmate(), pm, RPShuttleDefOf.RP_EscapeAttempt);
                if (!beganSeen) RPLog.Message("[RimPrison:test] count: the second escape attempt did not begin, the control is not judged");
                else if (CountSystem.IsHidden(seen) || !CountSaid(seen) || g.today.misconducts != board1 + 1)
                    CountFail("hidden record", $"an escape attempt a guard saw was not told as ever (hidden {CountSystem.IsHidden(seen)}, message {CountSaid(seen)}, desk board {board1} -> {g.today.misconducts})");

                // Nobody watches: caught, or given up, before any count: nobody ever knew of it and nothing is told.
                t.s.Watch();
                Pawn quitter = t.Inmate(t.s.cell);
                CountSystem.StartEscaping(quitter, "check", EscapeWay.Walked);
                if (!CountSystem.IsHidden(quitter))
                {
                    RPLog.Message("[RimPrison:test] count: the runner who gives up was not held as hidden, the drop is not judged");
                    return;
                }
                quitter.mindState.mentalStateHandler.Reset();
                int letters0 = r.NewLetters.Count();
                CountSystem.Tick(map, pm);
                if (pm.count.HiddenOf(quitter) != null || r.NewLetters.Count() != letters0) CountFail("hidden record", "a runner who gave up before any count is still held, or was told of");
                RPLog.Message("[RimPrison:test] count: a hidden runner's record told nothing; a sighting told the held letter; one who gave up was dropped quietly");
            });
        }

        // ---- The stowaway at the ramp ----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The real stowaway path (the shuttle landed, the runner picked by <see cref="ShuttleSystem.TryStowaway"/>, the ramp reached): with nothing watching they get aboard unannounced (no spotted message, no
        /// message of the attempt, nothing on the desk board), and the count tells the shuttle; with a guard on the pad they are stopped and told as ever; one for whom boarding closed is no stowaway to the count.
        /// </summary>
        private static void CheckCountStowawayAtTheRamp(Map map)
        {
            Guarded("count: a stowaway nobody saw at the ramp is not announced", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "count stowaway")) return;
                if (!ShuttleSystem.HasPad(map))
                {
                    RPLog.Message("[RimPrison:test] count: no shuttle pad on this map, the stowaway at the ramp is not judged");
                    return;
                }
                g.shuttle.padGuards = 0;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (v == null || g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    RPLog.Message("[RimPrison:test] count: the shuttle did not land, the stowaway at the ramp is not judged");
                    return;
                }
                // (The shuttle's own roll is spent: the runners are the check's own.)
                v.stowawayRolled = true;
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonMap pm = t.pm;
                // (The live prison ran while the shuttle landed: a letter of its own then is not the runner's.)
                int letters0 = r.NewLetters.Count();
                void LongStay() => (v.departTick, v.landedTick) = (ShNow + 2400, ShNow);
                IntVec3 ramp = v.shuttle.InteractionCell;
                Pawn Runner()
                {
                    Pawn p = t.Inmate(ramp);
                    p.Inmate().RefreshActiveFlag();
                    return p;
                }
                string shortLabel = "RP_Count_ShortLabel".Translate().Resolve();
                string StowawayWay() => "RP_Count_WayStowaway".Translate().Resolve();
                string WalkedWay() => "RP_Count_WayWalked".Translate().Resolve();
                // (The name is matched without case: the game capitalises the first letter of a sentence, and a made-up inmate's name may be lower case: "fwine" in the log, "Fwine" in the letter. And as a word: a runner called "Ri"
                // was found in the letter of the one before, "was not in the prison" - census-c73-ev1.)
                string LetterOf(Pawn p) => t.LettersTold().FirstOrDefault(l => l.StartsWith(shortLabel) && NamesPawn(l, p, ignoreCase: true)) ?? "";
                Pawn guard = t.Guard();

                // Nobody on the pad, nobody watching: aboard, and not announced.
                LongStay();
                v.lastGuardTick = -999999;
                Pawn first = Runner();
                SecurityLevelDef level = first.Inmate().security;
                int away0 = g.stats.stowawaysAway, board0 = g.today.misconducts, onLevel0 = g.today.MisconductsOn(level);
                if (g.shuttle.TryStowaway(g, map, v, first) != first || !CountSystem.IsHidden(first))
                {
                    RPLog.Message($"[RimPrison:test] count: the runner at the ramp was not picked as a hidden stowaway (hidden {CountSystem.IsHidden(first)}), the stowaway at the ramp is not judged");
                    return;
                }
                if (CountSaid(first)) CountFail("stowaway ramp", "a stowaway nobody saw was spotted by message when they began to run");
                ShuttleSystem.Board(first, v.shuttle);
                if (first.Spawned || pm.count.HiddenOf(first) is not { Exited: true, way: EscapeWay.Stowaway })
                    CountFail("stowaway ramp", $"a stowaway nobody saw did not get aboard as a held escape (spawned {first.Spawned}, record {pm.count.HiddenOf(first)?.way.ToString() ?? "none"})");
                if (CountSaid(first) || g.today.misconducts != board0 || g.today.MisconductsOn(level) != onLevel0 || r.NewLetters.Count() != letters0)
                    CountFail("stowaway ramp", $"a stowaway nobody saw was announced at the ramp (message {CountSaid(first)}, desk board {board0} -> {g.today.misconducts}, letters {string.Join(" // ", r.NewLetters.Skip(letters0).Select(l => l.Label.Resolve()))})");
                if (g.stats.stowawaysAway != away0 + 1) CountFail("stowaway ramp", $"the shuttle's own statistic of a stowaway away is not counted ({away0} -> {g.stats.stowawaysAway})");
                if (t.CountEveryWing(guard))
                {
                    string letter = LetterOf(first);
                    if (letter == "" || !letter.Contains(StowawayWay())) CountFail("stowaway ramp", $"the count did not tell that the runner stowed away: {letter}");
                }

                // A guard on the pad: stopped at the ramp, told as ever, and no longer held.
                LongStay();
                v.lastGuardTick = ShNow;
                Pawn second = Runner();
                int caught0 = g.stats.stowawaysCaught;
                if (g.shuttle.TryStowaway(g, map, v, second) == second && CountSystem.IsHidden(second))
                {
                    ShuttleSystem.Board(second, v.shuttle);
                    if (g.stats.stowawaysCaught != caught0 + 1 || pm.count.HiddenOf(second) != null || !second.Spawned || !CountSaid(second))
                        CountFail("stowaway ramp", $"a stowaway a guard on the pad stopped was not told as ever (caught {caught0} -> {g.stats.stowawaysCaught}, still held {pm.count.HiddenOf(second) != null}, message {CountSaid(second)})");
                }
                else RPLog.Message("[RimPrison:test] count: the second runner was not picked as a hidden stowaway, the stop at the ramp is not judged");

                // Boarding closed before they got there: they run the ordinary way, and are no stowaway to the count.
                LongStay();
                v.lastGuardTick = -999999;
                Pawn third = Runner();
                if (g.shuttle.TryStowaway(g, map, v, third) != third || !CountSystem.IsHidden(third))
                {
                    RPLog.Message("[RimPrison:test] count: the third runner was not picked as a hidden stowaway, the missed shuttle is not judged");
                    return;
                }
                ShuttleDepartIn(v, ShuttleSystem.BoardingClosesTicks - 50);
                if (ShuttleSystem.StowawayGoal(third) != null || g.shuttle.stowaway != null)
                {
                    RPLog.Message("[RimPrison:test] count: boarding did not close, the missed shuttle is not judged");
                    return;
                }
                int away1 = g.stats.stowawaysAway;
                third.ExitMap(false, Rot4.Invalid);
                HiddenEscape h = pm.count.HiddenOf(third);
                if (h == null || !h.Exited || h.way != EscapeWay.Walked || g.stats.stowawaysAway != away1)
                    CountFail("stowaway missed", $"a stowaway who missed the shuttle is held as {h?.way.ToString() ?? "nothing"} (wanted Walked; stowaways away {away1} -> {g.stats.stowawaysAway})");
                if (t.CountEveryWing(guard))
                {
                    string letter = LetterOf(third);
                    if (letter == "" || !letter.Contains(WalkedWay()) || letter.Contains(StowawayWay())) CountFail("stowaway missed", $"the count tells a runner who missed the shuttle as a stowaway: {letter}");
                }
                RPLog.Message("[RimPrison:test] count: a stowaway nobody saw was aboard unannounced and found by the count; one a guard stopped was told; one who missed the shuttle is no stowaway");
            });
        }

        // ---- The guard's walk ---------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The count job on a real guard: ended before its time it leaves the wing for the next, finished it counts it; an alarm takes the guard from it within the check's interval; and a guard on a post or a tower, with an
        /// alarm to answer, deployed to another wing, or kept off this one is not sent (each against its own control: the same guard with the hold let go is).
        /// </summary>
        private static void CheckCountGuardJobRuns(Map map)
        {
            Guarded("count: the guard's walk gives way to an alarm, a post is never left for it", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                t.GoneUnseen(EscapeWay.Walked);
                Pawn guard = t.Guard();
                if (GuardUtility.AlarmFor(guard) || map.mapPawns.PrisonersOfColonySpawned.Any(x => x.InMentalState))
                {
                    RPLog.Message("[RimPrison:test] count: an alarm is already on in the prison, the guard's walk is not judged");
                    return;
                }
                CountSystem.Begin(map, pm, g, Find.TickManager.TicksGame);
                if (!pm.count.Running)
                {
                    CountFail("guard walk", "the check's own premise: a count called with a runner of the wing hidden has no wing to walk");
                    return;
                }
                pm.count.dueTick = Find.TickManager.TicksGame;
                CountSystem.lastTry.Clear();
                Job job = CountSystem.JobFor(guard);
                if (job == null)
                {
                    RPLog.Message($"[RimPrison:test] count: the guard (on duty {GuardPosts.OnDuty(guard)}) is given no wing from {guard.Position}, the guard's walk is not judged");
                    return;
                }
                // (The one wing the check is about: the live prison's other wings are not on the list.)
                int wing = job.count;
                PrisonZoneDef wingZone = PrisonZoneDef.FromGridIndex((byte)wing);
                // (The wing stays held for every call: the runner of the walk before was found by it, and a wing nobody is on is no count.)
                void Call()
                {
                    if (!pm.count.hidden.Any(h => h.security == wingZone.security)) t.GoneUnseen(EscapeWay.Walked);
                    CountSystem.Begin(map, pm, g, Find.TickManager.TicksGame);
                    pm.count.wingsLeft.RemoveAll(i => i != wing);
                    pm.count.dueTick = Find.TickManager.TicksGame;
                }
                Job Fresh()
                {
                    CountSystem.lastTry.Clear();
                    return CountSystem.JobFor(guard);
                }
                void Stand() => guard.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                Call();

                // Ended before its time: the wing is left for the next. Finished: counted.
                guard.jobs.StartJob(Fresh() ?? job, JobCondition.InterruptForced);
                if (guard.CurJobDef != RPCountDefOf.RP_Count)
                {
                    RPLog.Message($"[RimPrison:test] count: the guard did not take the count job ({guard.CurJobDef?.defName ?? "none"}), the guard's walk is not judged");
                    return;
                }
                Stand();
                if (!pm.count.wingsLeft.Contains(wing) || pm.count.walked != 0) CountFail("guard walk", $"a walk that was broken off counted its wing (left {string.Join(",", pm.count.wingsLeft)}, walked {pm.count.walked})");
                guard.jobs.StartJob(Fresh() ?? job, JobCondition.InterruptForced);
                guard.jobs.EndCurrentJob(JobCondition.Succeeded, false);
                if (pm.count.wingsLeft.Contains(wing) || pm.count.walked < 1) CountFail("guard walk", $"a walk that was finished did not count its wing (left {string.Join(",", pm.count.wingsLeft)}, walked {pm.count.walked})");

                // An inmate running amok some cells from the guard, reserved by the guard first (no other is sent to them: the check is about this guard); null when there is no cell or the state would not start.
                Pawn MakeRowdy()
                {
                    IntVec3 cell = GenRadial.RadialCellsAround(guard.Position, 8f, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && c.DistanceTo(guard.Position) >= 3f
                        && guard.CanReach(c, PathEndMode.OnCell, Danger.Some));
                    if (!cell.IsValid || cell == default) return null;
                    Pawn rowdy = Throwaway(map, r);
                    r.Add(() => rowdy.mindState.mentalStateHandler.Reset());
                    CountScene.Place(rowdy, cell);
                    if (guard.CurJob != null) guard.Reserve(rowdy, guard.CurJob, 1, -1, null, false);
                    rowdy.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, transitionSilently: true);
                    return rowdy.InMentalState ? rowdy : null;
                }

                // An alarm: the walk goes on while all is quiet (the control), and gives way to an inmate running amok.
                Call();
                guard.jobs.StartJob(Fresh() ?? job, JobCondition.InterruptForced);
                int quiet = ShuttleTicks(() => guard.CurJobDef != RPCountDefOf.RP_Count, 2 * JobDriver_Count.AlarmCheckTicks + 60);
                if (guard.CurJobDef != RPCountDefOf.RP_Count)
                {
                    RPLog.Message($"[RimPrison:test] count: the walk ended by itself after {quiet} ticks ({guard.CurJobDef?.defName ?? "none"}), the alarm is not judged");
                }
                else
                {
                    Pawn rowdy = MakeRowdy();
                    if (rowdy == null || !GuardUtility.AlarmFor(guard))
                        RPLog.Message("[RimPrison:test] count: no inmate could be set running amok within the guard's reach, the alarm is not judged");
                    else
                    {
                        int waited = ShuttleTicks(() => guard.CurJobDef != RPCountDefOf.RP_Count, 2 * JobDriver_Count.AlarmCheckTicks + 60);
                        if (guard.CurJobDef == RPCountDefOf.RP_Count) CountFail("guard walk", $"an inmate running amok did not take the guard from the count within {waited} ticks");
                        else if (!pm.count.wingsLeft.Contains(wing) || pm.count.walked != 0) CountFail("guard walk", $"a walk an alarm broke off counted its wing (left {string.Join(",", pm.count.wingsLeft)}, walked {pm.count.walked})");
                        rowdy.mindState.mentalStateHandler.Reset();
                    }
                }
                Stand();

                // Not sent: a post, a tower, an alarm, a deployment elsewhere, kin on the wing. Each against its control.
                void Excluded(string what, Func<bool> hold, Action release)
                {
                    Call();
                    Stand();
                    if (!hold())
                    {
                        RPLog.Message($"[RimPrison:test] count: could not set up a guard {what}, not judged");
                        release();
                        return;
                    }
                    CountSystem.lastTry.Clear();
                    bool offered = CountSystem.JobFor(guard) != null, wants = CountSystem.WantsGuard(guard);
                    release();
                    if (Fresh() == null) RPLog.Message($"[RimPrison:test] count: the control guard is given no wing once {what} is let go, not judged");
                    else if (offered || wants) CountFail("guard walk", $"a guard {what} is sent to count a wing (job {offered}, wanted by the patrol {wants})");
                }

                // (A post and a tower, put up where the guard stands, and the guard put on them by their jobs, which are what the guard's current job is about.)
                var posts = new PostScene(map, r);
                IntVec3 site = posts.Site(guard);
                CompGuardPost post = site.IsValid ? posts.Post(site) : null;
                Excluded("on a post", () =>
                {
                    if (post == null) return false;
                    guard.jobs.StartJob(JobMaker.MakeJob(RPPostDefOf.RP_ManPost, post.parent), JobCondition.InterruptForced);
                    return guard.CurJobDef == RPPostDefOf.RP_ManPost;
                }, () =>
                {
                    Stand();
                    // (The post is shut for the cases after: an open one is a post this guard is due on.)
                    if (post != null) posts.Closed(post);
                });
                ThingDef towerDef = RPArmedDefOf.RP_GuardTower;
                IntVec3 at = GenRadial.RadialCellsAround(guard.Position, 12f, true).FirstOrDefault(c => c.InBounds(map) && GenAdj.OccupiedRect(c, Rot4.North, towerDef.size).Cells.All(x => x.InBounds(map) && x.Standable(map)
                    && x.GetEdifice(map) == null && x.GetFirstItem(map) == null && x.GetFirstPawn(map) == null));
                Thing tower = null;
                if (at.IsValid && at != default)
                {
                    tower = GenSpawn.Spawn(ThingMaker.MakeThing(towerDef, towerDef.MadeFromStuff ? GenStuff.DefaultStuffFor(towerDef) : null), at, map);
                    tower.SetFaction(Faction.OfPlayer);
                    Thing built = tower;
                    r.Add(() =>
                    {
                        if (!built.Destroyed) built.Destroy();
                    });
                }
                Excluded("on a tower", () =>
                {
                    if (tower == null) return false;
                    guard.jobs.StartJob(JobMaker.MakeJob(RPArmedDefOf.RP_ManTower, tower), JobCondition.InterruptForced);
                    return guard.CurJobDef == RPArmedDefOf.RP_ManTower;
                }, () =>
                {
                    Stand();
                    // (Manned for the cases after: an empty tower is one this guard may be due on.)
                    if (tower != null) tower.TryGetComp<CompGuardTower>().mannedUntil = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                });
                // Due on a post or a tower and not on it yet (just in for the shift, back from a meal): not taken for the count either.
                Excluded("due on a post, not on it yet", () =>
                {
                    if (post == null) return false;
                    posts.Open(post);
                    return GuardPosts.PostFor(guard) == post;
                }, () => posts.Closed(post));
                Excluded("due on a tower, not on it yet", () =>
                {
                    if (tower == null) return false;
                    tower.TryGetComp<CompGuardTower>().mannedUntil = -1;
                    return CountSystem.DueOnAPost(guard);
                }, () => tower.TryGetComp<CompGuardTower>().mannedUntil = Find.TickManager.TicksGame + GenDate.TicksPerDay);
                Pawn amok = null;
                Excluded("with an alarm to answer", () =>
                {
                    amok = MakeRowdy();
                    return amok != null && GuardUtility.AlarmFor(guard);
                }, () => amok?.mindState.mentalStateHandler.Reset());
                CompStaff sc = guard.GetComp<CompStaff>();
                PrisonZoneDef elsewhere = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.security != null && z != wingZone);
                PrisonZoneDef patrol0 = sc?.patrolZone;
                r.Add(() =>
                {
                    if (sc != null) sc.patrolZone = patrol0;
                });
                Excluded("deployed to another wing", () =>
                {
                    if (sc == null || elsewhere == null) return false;
                    sc.patrolZone = elsewhere;
                    return SpecialMeasures.DeployedZone(guard) == elsewhere;
                }, () =>
                {
                    if (sc != null) sc.patrolZone = patrol0;
                });
                Pawn kin = t.Inmate(t.s.cell);
                Excluded("kept off the wing their kin is in", () =>
                {
                    if (sc == null) return false;
                    sc.kinKeepOff = true;
                    kin.relations.AddDirectRelation(PawnRelationDefOf.Parent, guard);
                    return Kin.KeepsOffZone(guard, wingZone);
                }, () =>
                {
                    if (sc != null) sc.kinKeepOff = false;
                    kin.relations.RemoveDirectRelation(PawnRelationDefOf.Parent, guard);
                });
                RPLog.Message("[RimPrison:test] count: a broken walk left its wing, a finished one counted it, an alarm ended it, and a post, a tower, an alarm, a deployment and kin each kept a guard from being sent");
            });
        }

    }
}
