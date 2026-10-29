using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Tasking an informant (<see cref="InformantTask"/>, gap analysis 7, item 3). A made-up scene (<see cref="TaskScene"/>): a known snitch, a target with a hidden debt and a tunnel at 0.2, a decoy within 30 cells with
    /// contraband, and the target's creditor, the tip confined to them (<see cref="InformantSystem.TipPool"/>). The tasked tip names the target's tunnel every time and never the decoy's contraband, then the target's debt;
    /// untasked, the decoy can be named (the control). A wrong lead is fed only by an informant who no longer trusts the warden, and reads as an ordinary tip. The exposure chance on a tip acted on is greater at day 6 than at
    /// day 1 (one forced roll exposes at day 6 and not at day 1), the gang on the wing makes it faster, the daily pass burns in place on the same term. The move near the target goes through the cells of a
    /// <see cref="CellRig"/> (into the cell, onto the wing, or left where they are; a keep-apart order refuses both with its reason). The task ends with the target's release, with a target gone without a word (the daily
    /// pass), with an informant who quit, and re-tasking starts the days again. The saveload plan's seed is at the end. Written without a game run: none of it has been run yet.
    /// </summary>
    public static partial class AutoTest
    {
        private static void TaskFail(string check, string what) => problems0.Add($"informant task ({check}): {what}");

        private static void CheckInformantTask(Map map)
        {
            CheckTaskTipTargetFirst(map);
            CheckTaskWrongLead(map);
            CheckTaskExposure(map);
            CheckTaskInPlace(map);
            CheckTaskPullOutMemory(map);
            CheckTaskMenu(map);
            CheckTaskMove(map);
            CheckTaskEnds(map);
        }

        /// <summary>The made-up inmates of a tip scene and the target's hidden debt.</summary>
        private sealed class TaskScene
        {
            public Pawn snitch, target, decoy, creditor;
            public CompInmate sc, tc, dc;
            public InmateDebt debt;
            public readonly List<Pawn> cast = new List<Pawn>();

            /// <summary>As before a tip: nothing told of the target or the decoy, the debt hidden.</summary>
            public void Reset()
            {
                (tc.tunnelKnown, tc.cellSearchPending, tc.suspected, tc.informedBy) = (false, false, false, null);
                (dc.suspected, dc.informedBy) = (false, null);
                debt.known = false;
            }
        }

        /// <summary>A throwaway with nothing on them: no gang, no reputation, nothing suspected, no tunnel, no feelings about the staff, empty pockets.</summary>
        private static Pawn TaskClean(Pawn p)
        {
            CompInmate c = p.Inmate();
            (c.gang, c.gangKnown, c.gangLeader) = (null, false, false);
            c.reputations = new List<ReputationDef>();
            c.revealed = new List<ReputationDef>();
            (c.suspected, c.cellSearchPending, c.tunnelProgress, c.tunnelKnown, c.informedBy) = (false, false, 0f, false, null);
            (c.snitchExposed, c.informantQuit, c.informantPaid, c.taskTargetId, c.taskSinceTick) = (false, false, false, -1, -1);
            c.staffMemories.Clear();
            p.inventory?.innerContainer.ClearAndDestroyContents();
            c.RefreshActiveFlag();
            return p;
        }

        /// <summary>A known informant (the reputation and the warden knowing it).</summary>
        private static void TaskMakeInformant(CompInmate c)
        {
            if (!c.reputations.Contains(ReputationDefOf.RP_Snitch)) c.reputations.Add(ReputationDefOf.RP_Snitch);
            if (!c.revealed.Contains(ReputationDefOf.RP_Snitch)) c.revealed.Add(ReputationDefOf.RP_Snitch);
        }

        /// <summary>The scene, the tip and the rolls confined to it and put back by the rollback; null (a problem noted) when a premise did not take.</summary>
        private static TaskScene MakeTaskScene(Map map, Rollback r, string check)
        {
            var s = new TaskScene();
            s.snitch = TaskClean(Throwaway(map, r));
            s.target = TaskClean(Throwaway(map, r));
            s.decoy = TaskClean(Throwaway(map, r));
            s.creditor = TaskClean(Throwaway(map, r));
            (s.sc, s.tc, s.dc) = (s.snitch.Inmate(), s.target.Inmate(), s.decoy.Inmate());
            s.cast.AddRange(new[] { s.snitch, s.target, s.decoy, s.creditor });
            TaskMakeInformant(s.sc);
            System.Func<Pawn, bool> pool0 = InformantSystem.TipPool, gangPool0 = InformantTask.GangPool;
            InformantSystem.TipPool = p => s.cast.Contains(p);
            // (The gang on the wing counts only the scene's inmates: a real member of the gang the check borrows, on the default level, would be "on the wing" of a target with no bed.)
            InformantTask.GangPool = p => s.cast.Contains(p);
            r.Add(() =>
            {
                InformantSystem.TipPool = pool0;
                InformantTask.GangPool = gangPool0;
                InformantTask.ExposureRoll = null;
                InformantTask.WrongLeadRoll = null;
            });
            s.tc.tunnelProgress = 0.2f;
            PrisonGame g = PrisonGame.Instance;
            s.debt = DebtSystem.Open(s.creditor, s.target, DebtReason.Cards, 40, 5);
            r.Add(() => g.story.debts.Remove(s.debt));
            ThingDef drug = ContrabandUtility.PickSmuggled(s.decoy, ContrabandKind.Drug);
            ContrabandUtility.Give(s.decoy, drug);
            if (!s.snitch.Position.InHorDistOf(s.decoy.Position, 30f) || !ContrabandUtility.CarriesContraband(s.decoy) || ContrabandUtility.CarriesContraband(s.target) || s.debt.known
                || !InformantTask.CanBeTasked(s.snitch, s.sc) || !InformantTask.Targetable(s.snitch, s.target) || !InformantTask.InReach(s.snitch, s.target))
            {
                TaskFail(check, $"premise: the scene did not take (decoy within 30 cells {s.snitch.Position.InHorDistOf(s.decoy.Position, 30f)}, decoy carries {ContrabandUtility.CarriesContraband(s.decoy)} ({drug?.defName}), target carries {ContrabandUtility.CarriesContraband(s.target)}, debt known {s.debt.known}, snitch can be tasked {InformantTask.CanBeTasked(s.snitch, s.sc)}, target targetable {InformantTask.Targetable(s.snitch, s.target)}, target in reach {InformantTask.InReach(s.snitch, s.target)})");
                return null;
            }
            return s;
        }

        /// <summary>
        /// The clock a week on, put back by the rollback, and the new now: a check that sets days in place ("6 days ago") needs a clock past them (the events plan runs at day 0-3, and a tick before 0 reads as "not in
        /// place"). No tick runs while it is moved.
        /// </summary>
        private static int TaskClockForward(Rollback r)
        {
            int clock0 = Find.TickManager.TicksGame;
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            Find.TickManager.DebugSetTicksGame(clock0 + 7 * GenDate.TicksPerDay);
            return Find.TickManager.TicksGame;
        }

        /// <summary>Puts a pawn somewhere else at once (no tick runs).</summary>
        private static void TaskTeleport(Pawn p, IntVec3 to)
        {
            p.pather?.StopDead();
            p.Position = to;
            p.Notify_Teleported();
        }

        // ---- the tasked tip looks at the target first --------------------------------------------------------------------------------------------------------

        /// <summary>Tasked: the target's tunnel every time (never the decoy's contraband), and once the tunnel is told, the target's debt. Untasked: the decoy's contraband comes up (the control).</summary>
        private static void CheckTaskTipTargetFirst(Map map)
        {
            const string check = "the tasked tip";
            Guarded("informant task: the tasked tip looks at the target first", map, r =>
            {
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                InformantTask.Assign(s.snitch, s.sc, s.target);
                if (InformantTask.TargetOf(s.snitch, s.sc) != s.target)
                {
                    TaskFail(check, $"premise: the task was not set (target id {s.sc.taskTargetId}, the target's {s.target.thingIDNumber})");
                    return;
                }
                string tunnelTip = "RP_TipTunnel".Translate(s.target.LabelShort), decoyTip = "RP_TipContraband".Translate(s.decoy.LabelShort);
                var seen = new List<string>();
                for (int i = 0; i < 10; i++)
                {
                    s.Reset();
                    seen.Add(InformantSystem.TryTip(s.snitch, s.sc) ?? "(nothing)");
                }
                if (seen.Any(t => t != tunnelTip)) TaskFail(check, $"of 10 tasked tips {seen.Count(t => t == tunnelTip)} named the target's tunnel and {seen.Count(t => t == decoyTip)} the decoy's contraband (saw: {string.Join(" | ", seen.Distinct())})");
                // The tunnel told already: the debt is next (the target has no contraband and no gang), before anything of the others.
                s.Reset();
                s.tc.tunnelKnown = true;
                string debtTip = InformantSystem.TryTip(s.snitch, s.sc);
                string expected = "RP_TipDebt".Translate(s.debt.debtorName, s.debt.creditorName, Money.Format(s.debt.amount), DebtSystem.ReasonText(s.debt));
                if (debtTip != expected || !s.debt.known) TaskFail(check, $"with the tunnel told, the tasked tip said \"{debtTip}\" (debt known {s.debt.known}), not the target's debt \"{expected}\"");
                // The control: untasked, the decoy within 30 cells is as likely as the target.
                InformantTask.End(s.snitch, s.sc);
                int decoys = 0, tunnels = 0;
                for (int i = 0; i < 40; i++)
                {
                    s.Reset();
                    string tip = InformantSystem.TryTip(s.snitch, s.sc);
                    if (tip == decoyTip) decoys++;
                    else if (tip == tunnelTip) tunnels++;
                }
                if (decoys == 0) TaskFail(check, $"untasked, 40 tips never named the decoy's contraband (the target's tunnel {tunnels} times): the control does not show the decoy is possible");
                RPLog.Message($"[RimPrison:test] informant task: tasked tips {seen.Count(t => t == tunnelTip)}/10 the target's tunnel, then \"{debtTip}\"; untasked the decoy {decoys}/40, the tunnel {tunnels}/40");
            });
        }

        /// <summary>A wrong lead: never from an informant who trusts the warden or when the dice say no; from one at or under the mark of hatred, the target flagged for contraband they do not carry, in the words of a true tip.</summary>
        private static void CheckTaskWrongLead(Map map)
        {
            const string check = "a wrong lead";
            Guarded("informant task: a wrong lead from an informant who distrusts the warden", map, r =>
            {
                if (WardenSystem.InPost(map) is not { } warden)
                {
                    RPLog.Message("[RimPrison:test] informant task: the wrong lead not run, no warden in post");
                    return;
                }
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                InformantTask.Assign(s.snitch, s.sc, s.target);
                string tunnelTip = "RP_TipTunnel".Translate(s.target.LabelShort), lie = "RP_TipContraband".Translate(s.target.LabelShort);
                InformantTask.WrongLeadRoll = true;
                s.Reset();
                string trusted = InformantSystem.TryTip(s.snitch, s.sc);
                if (trusted != tunnelTip || s.tc.suspected) TaskFail(check, $"an informant with no grudge against the warden fed \"{trusted}\" (target flagged {s.tc.suspected}), not the truth \"{tunnelTip}\"");
                // (-50: under the mark of hatred, -40, and above a grudge's -60, which would give the made-up snitch a goal.)
                StaffMemories.Note(s.sc, warden.thingIDNumber, warden.LabelShort, -50f);
                if (!InformantTask.Distrusts(s.snitch, s.sc))
                {
                    TaskFail(check, $"premise: a trust of {StaffMemories.ScoreFor(s.sc, warden):0} in the warden is not read as distrust");
                    return;
                }
                s.Reset();
                string fed = InformantSystem.TryTip(s.snitch, s.sc);
                if (fed != lie || !s.tc.suspected || s.tc.informedBy != s.snitch || ContrabandUtility.CarriesContraband(s.target) || s.tc.tunnelKnown)
                    TaskFail(check, $"an informant who distrusts the warden, the dice saying yes, said \"{fed}\" (want \"{lie}\"), target flagged {s.tc.suspected}, informed by the snitch {s.tc.informedBy == s.snitch}, carries {ContrabandUtility.CarriesContraband(s.target)}, tunnel told {s.tc.tunnelKnown}");
                InformantTask.WrongLeadRoll = false;
                s.Reset();
                string honest = InformantSystem.TryTip(s.snitch, s.sc);
                if (honest != tunnelTip) TaskFail(check, $"the dice saying no, a distrustful informant said \"{honest}\", not the truth \"{tunnelTip}\"");
                RPLog.Message($"[RimPrison:test] informant task: trusted \"{trusted}\", distrustful \"{fed}\", the dice saying no \"{honest}\"");
            });
        }

        // ---- exposure ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The term by days and with the gang on the wing, the bands; a tip acted on with one forced roll (0.4) gives the informant away at day 6 and not at day 1 (the old 30% and 0.33 against 0.48), which ends the task; the
        /// daily pass burns nobody at day 0 and burns at day 6 with a roll of 0.
        /// </summary>
        private static void CheckTaskExposure(Map map)
        {
            const string check = "exposure";
            Guarded("informant task: the exposure grows with the days in place", map, r =>
            {
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                int now = TaskClockForward(r);
                float untasked = InformantTask.ActedOnChance(s.snitch, s.sc, s.target);
                InformantTask.Assign(s.snitch, s.sc, s.target);
                if (s.sc.taskSinceTick != now) TaskFail(check, $"premise: tasked in reach of the target, the days in place did not start at once (from {s.sc.taskSinceTick}, now {now})");
                s.sc.taskSinceTick = now - GenDate.TicksPerDay;
                float day1 = InformantTask.ActedOnChance(s.snitch, s.sc, s.target), other = InformantTask.ActedOnChance(s.snitch, s.sc, s.decoy);
                s.sc.taskSinceTick = now - 6 * GenDate.TicksPerDay;
                float day6 = InformantTask.ActedOnChance(s.snitch, s.sc, s.target);
                if (!(day6 > day1 && day1 > untasked) || untasked != InformantTask.ActedOnBase || other != InformantTask.ActedOnBase)
                    TaskFail(check, $"a tip acted on gives the informant away at {untasked:0.00} untasked, {day1:0.00} at day 1, {day6:0.00} at day 6 and {other:0.00} for a tip about someone else (want the base {InformantTask.ActedOnBase} untasked and for others, and rising with the days)");
                string bands = $"{InformantTask.BandKey(InformantTask.Term(1f, false))}/{InformantTask.BandKey(InformantTask.Term(6f, false))}/{InformantTask.BandKey(InformantTask.Term(13f, false))}/{InformantTask.BandKey(InformantTask.Term(6f, true))}";
                if (bands != "RP_InformantBand_Low/RP_InformantBand_Rising/RP_InformantBand_High/RP_InformantBand_High") TaskFail(check, $"the bands for day 1, 6, 13 and 6 with the gang are {bands}");
                // The target's gang on the wing (the decoy in it, of the same level, at the intake; only the scene's inmates counted): faster. The screens' band counts it only when the desk knows both gangs.
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                if (gang != null)
                {
                    float alone = InformantTask.Term(s.snitch, s.sc);
                    (s.tc.gang, s.dc.gang, s.tc.gangKnown, s.dc.gangKnown) = (gang, gang, false, false);
                    bool on = InformantTask.GangOnWing(s.target, s.snitch), shownHidden = InformantTask.GangOnWing(s.target, s.snitch, known: true);
                    float withGang = InformantTask.Term(s.snitch, s.sc);
                    s.tc.gangKnown = true;
                    bool shownHalf = InformantTask.GangOnWing(s.target, s.snitch, known: true);
                    s.dc.gangKnown = true;
                    bool shownKnown = InformantTask.GangOnWing(s.target, s.snitch, known: true);
                    s.dc.gang = null;
                    bool off = InformantTask.GangOnWing(s.target, s.snitch);
                    (s.tc.gang, s.tc.gangKnown, s.dc.gangKnown) = (null, false, false);
                    if (!on || off || withGang <= alone) TaskFail(check, $"the target's gang on the wing: seen {on} with the decoy in it and {off} without, the term {withGang:0.00} against {alone:0.00} alone");
                    if (shownHidden || shownHalf || !shownKnown)
                        TaskFail(check, $"the band as the desk sees it counts the gang with both gangs hidden {shownHidden}, the other member's hidden {shownHalf}, both known {shownKnown} (want false, false, true)");
                }
                else RPLog.Message("[RimPrison:test] informant task: no gang in this game, the gang on the wing not checked");
                // One forced roll: 0.4 is over day 1's chance and under day 6's.
                InformantTask.ExposureRoll = 0.4f;
                s.sc.taskSinceTick = now - GenDate.TicksPerDay;
                s.tc.informedBy = s.snitch;
                InformantSystem.Notify_TipActedOn(s.target, s.tc);
                if (s.sc.snitchExposed || !InformantTask.Tasked(s.sc)) TaskFail(check, $"a roll of 0.4 on day 1 (chance {day1:0.00}) exposed the informant ({s.sc.snitchExposed}) or ended the task ({!InformantTask.Tasked(s.sc)})");
                s.sc.taskSinceTick = now - 6 * GenDate.TicksPerDay;
                s.tc.informedBy = s.snitch;
                InformantSystem.Notify_TipActedOn(s.target, s.tc);
                bool letter = r.NewLetters.Any(l => l.Label.Resolve() == "RP_SnitchExposedLabel".Translate().Resolve());
                bool hates = s.target.needs?.mood?.thoughts.memories.Memories.Any(m => m.def == RPTrialDefOf.RP_HatesSnitch && m.otherPawn == s.snitch) ?? true;
                if (!s.sc.snitchExposed || InformantTask.Tasked(s.sc) || !letter || !hates)
                    TaskFail(check, $"the same roll on day 6 (chance {day6:0.00}): exposed {s.sc.snitchExposed}, task ended {!InformantTask.Tasked(s.sc)}, a letter {letter}, the target hates the snitch {hates}");
                // The daily pass: nothing at day 0 with a roll of 0, burned at day 6 (the burned letter).
                s.sc.snitchExposed = false;
                InformantTask.ExposureRoll = 0f;
                InformantTask.Assign(s.snitch, s.sc, s.target);
                InformantTask.Daily(map, only: s.snitch);
                if (s.sc.snitchExposed || !InformantTask.Tasked(s.sc)) TaskFail(check, $"the daily pass at day 0 burned the informant ({s.sc.snitchExposed}) or ended the task ({!InformantTask.Tasked(s.sc)}) with no term");
                s.sc.taskSinceTick = now - 6 * GenDate.TicksPerDay;
                int letters = r.NewLetters.Count();
                InformantTask.Daily(map, only: s.snitch);
                string burned = "RP_InformantTask_BurnedText".Translate(s.snitch.LabelShort, s.target.LabelShort).Resolve();
                bool burnedLetter = r.NewLetters.Skip(letters).Any(l => l is ChoiceLetter cl && cl.Text.Resolve() == burned);
                if (!s.sc.snitchExposed || InformantTask.Tasked(s.sc) || !burnedLetter) TaskFail(check, $"the daily pass at day 6 with a roll of 0: burned {s.sc.snitchExposed}, task ended {!InformantTask.Tasked(s.sc)}, the burned letter {burnedLetter}");
                RPLog.Message($"[RimPrison:test] informant task: exposure {untasked:0.00} untasked, {day1:0.00} day 1, {day6:0.00} day 6; bands {bands}");
            });
        }

        /// <summary>
        /// The days in place count only in reach of the target: tasked far from them, not in place (the line says so, the term is 0); the hourly pass starts the days once they are in reach, and takes an hour off them for
        /// an hour out of reach; out of reach the daily pass risks nothing even with a roll of 0 and six days in place.
        /// </summary>
        private static void CheckTaskInPlace(Map map)
        {
            const string check = "in place";
            Guarded("informant task: the days in place count only in reach of the target", map, r =>
            {
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                IntVec3 home = s.snitch.Position;
                if (!CellFinder.TryFindRandomCell(map, c => c.Standable(map) && !c.Fogged(map) && !c.InHorDistOf(s.target.Position, InformantTask.ReachCells + 10f), out IntVec3 far))
                {
                    RPLog.Message("[RimPrison:test] informant task: no cell out of the target's reach on this map, the days in place not checked");
                    return;
                }
                int now = TaskClockForward(r);
                TaskTeleport(s.snitch, far);
                if (InformantTask.InReach(s.snitch, s.target))
                {
                    TaskFail(check, $"premise: the snitch at {far}, the target at {s.target.Position}, read as in reach");
                    return;
                }
                InformantTask.Assign(s.snitch, s.sc, s.target);
                string line = InformantTask.Line(s.snitch, s.sc, now), notYet = "RP_InformantTask_LineNotYet".Translate(s.target.LabelShort).Resolve();
                InformantTask.Hourly(map, only: s.snitch);
                if (InformantTask.InPlace(s.sc) || InformantTask.Term(s.snitch, s.sc) != 0f || line != notYet)
                    TaskFail(check, $"tasked out of reach: in place {InformantTask.InPlace(s.sc)} (from {s.sc.taskSinceTick}), term {InformantTask.Term(s.snitch, s.sc):0.00}, the line \"{line}\" (want \"{notYet}\")");
                TaskTeleport(s.snitch, home);
                InformantTask.Hourly(map, only: s.snitch);
                if (s.sc.taskSinceTick != now) TaskFail(check, $"in reach, the hourly pass did not start the days now ({s.sc.taskSinceTick}, now {now})");
                s.sc.taskSinceTick = now - 2 * GenDate.TicksPerDay;
                TaskTeleport(s.snitch, far);
                InformantTask.Hourly(map, only: s.snitch);
                int want = now - 2 * GenDate.TicksPerDay + GenDate.TicksPerHour;
                if (s.sc.taskSinceTick != want) TaskFail(check, $"an hour out of reach did not come off the days in place (from {s.sc.taskSinceTick}, want {want})");
                s.sc.taskSinceTick = now - 6 * GenDate.TicksPerDay;
                InformantTask.ExposureRoll = 0f;
                InformantTask.Daily(map, only: s.snitch);
                if (s.sc.snitchExposed || !InformantTask.Tasked(s.sc)) TaskFail(check, $"out of reach, the daily pass at day 6 with a roll of 0 burned the informant ({s.sc.snitchExposed}) or ended the task ({!InformantTask.Tasked(s.sc)})");
                RPLog.Message($"[RimPrison:test] informant task: out of reach \"{line}\", the days start in reach and pause out of it");
            });
        }

        /// <summary>
        /// Pulled out, the target stays wary as long as the days were: pointed at them again, the informant picks up what is left (the days less the time since); once it is gone, the daily pass forgets it, and a new task
        /// on the same target starts at 0. The target's leaving forgets it too.
        /// </summary>
        private static void CheckTaskPullOutMemory(Map map)
        {
            const string check = "pull out";
            Guarded("informant task: pulling out and re-tasking the same target keeps the days", map, r =>
            {
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                int now = TaskClockForward(r), id = s.target.thingIDNumber;
                InformantTask.Assign(s.snitch, s.sc, s.target);
                s.sc.taskSinceTick = now - 4 * GenDate.TicksPerDay;
                InformantTask.PullOut(s.snitch, s.sc);
                string pulled = InformantTask.Describe(s.sc), wantPulled = $"{-2 - id}:{now + 4 * GenDate.TicksPerDay}";
                if (InformantTask.Tasked(s.sc) || pulled != wantPulled) TaskFail(check, $"pulled out after 4 days, the file holds {pulled} (want {wantPulled}: the target coded, the wariness to the tick), tasked {InformantTask.Tasked(s.sc)}");
                InformantTask.Assign(s.snitch, s.sc, s.target);
                if (s.sc.taskTargetId != id || s.sc.taskSinceTick != now - 4 * GenDate.TicksPerDay)
                    TaskFail(check, $"re-tasked on the same target at once, the days are from {s.sc.taskSinceTick} (want {now - 4 * GenDate.TicksPerDay}: all 4 kept), target {s.sc.taskTargetId}");
                // Three days after the pull-out (one day of the wariness left): one day is picked up.
                InformantTask.PullOut(s.snitch, s.sc);
                s.sc.taskSinceTick = now + GenDate.TicksPerDay;
                InformantTask.Assign(s.snitch, s.sc, s.target);
                if (s.sc.taskSinceTick != now - GenDate.TicksPerDay) TaskFail(check, $"re-tasked with a day of the wariness left, the days are from {s.sc.taskSinceTick} (want {now - GenDate.TicksPerDay})");
                // The wariness gone: the daily pass forgets it, and the same target is a new task.
                InformantTask.PullOut(s.snitch, s.sc);
                s.sc.taskSinceTick = now;
                InformantTask.Daily(map, only: s.snitch);
                string forgotten = InformantTask.Describe(s.sc);
                InformantTask.Assign(s.snitch, s.sc, s.target);
                if (forgotten != "-1:-1" || s.sc.taskSinceTick != now) TaskFail(check, $"the wariness gone, the daily pass left {forgotten} (want -1:-1) and a new task starts from {s.sc.taskSinceTick} (want {now})");
                // The target leaves: a wariness of them goes with them.
                s.sc.taskSinceTick = now - GenDate.TicksPerDay;
                InformantTask.PullOut(s.snitch, s.sc);
                InformantTask.Notify_Leaving(s.target);
                if (InformantTask.Describe(s.sc) != "-1:-1") TaskFail(check, $"the target left, the informant's file still holds {InformantTask.Describe(s.sc)}");
                RPLog.Message($"[RimPrison:test] informant task: pulled out {pulled}, picked up again, forgotten when gone");
            });
        }

        // ---- the menu ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The target menu: suspects before the rest, never the informant or the present target; the first option pulls out a tasked informant; choosing a target sets it.</summary>
        private static void CheckTaskMenu(Map map)
        {
            const string check = "the menu";
            Guarded("informant task: the target menu lists suspects first", map, r =>
            {
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                s.dc.suspected = true;
                List<(Pawn pawn, List<string> why)> targets = InformantTask.Targets(s.snitch, s.sc);
                int lastSuspect = targets.FindLastIndex(x => x.why.Count > 0), firstPlain = targets.FindIndex(x => x.why.Count == 0);
                int decoyAt = targets.FindIndex(x => x.pawn == s.decoy);
                if (firstPlain >= 0 && lastSuspect > firstPlain) TaskFail(check, $"a suspect is listed after someone with nothing against them (last suspect {lastSuspect}, first plain {firstPlain})");
                // (A prison with a menu's worth of suspects of more reasons than one may leave the decoy off the list: told, not failed.)
                if (decoyAt < 0 && targets.Count(x => x.why.Count > 1) >= InformantTask.MenuCap) RPLog.Message($"[RimPrison:test] informant task: the flagged decoy is not on a menu of {targets.Count} suspects with more against them");
                else if (decoyAt < 0 || !targets[decoyAt].why.Contains("RP_InformantTask_WhyFlagged".Translate().Resolve())) TaskFail(check, $"the flagged decoy is not listed as flagged (at {decoyAt}: {(decoyAt >= 0 ? string.Join(", ", targets[decoyAt].why) : "-")})");
                if (targets.Any(x => x.pawn == s.snitch) || targets.Count > InformantTask.MenuCap) TaskFail(check, $"the menu lists the informant themselves or more than {InformantTask.MenuCap} ({targets.Count})");
                List<FloatMenuOption> options = InformantTask.TaskOptions(s.snitch, s.sc);
                if (options.Count != targets.Count || options.Any(o => o.Disabled)) TaskFail(check, $"an untasked informant's menu has {options.Count} options for {targets.Count} targets, disabled {options.Count(o => o.Disabled)}");
                InformantTask.Assign(s.snitch, s.sc, s.target);
                options = InformantTask.TaskOptions(s.snitch, s.sc);
                targets = InformantTask.Targets(s.snitch, s.sc);
                if (options.Count == 0 || options[0].Label != "RP_InformantTask_PullOut".Translate(s.target.LabelShort).Resolve() || targets.Any(x => x.pawn == s.target))
                    TaskFail(check, $"a tasked informant's menu starts with \"{options.FirstOrDefault()?.Label}\" and lists the present target {targets.Any(x => x.pawn == s.target)}");
                else
                {
                    options[0].action();
                    if (InformantTask.Tasked(s.sc)) TaskFail(check, "pulling out from the menu left the task");
                }
            });
        }

        // ---- the move ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// In a <see cref="CellRig"/>: three choices (the cell, the wing, where they are), leaving them moves no one; a keep-apart order refuses both moves with its reason and <see cref="InformantTask.MoveNear"/> does
        /// nothing; into the target's cell (the warden's placement, then "a cell shared already"); from a cell in no wing onto the target's wing.
        /// </summary>
        private static void CheckTaskMove(Map map)
        {
            const string check = "the move";
            Guarded("informant task: the move near the target", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "informant move", 3, 1, 1);
                if (s == null) return;
                s.Grades(1, 1, 1);
                bool wings = !s.level.wingOnly;
                // (The third cell in no wing, its paint taken off, for the move onto the wing; a level that keeps to its wing has no such bed.)
                if (wings)
                    foreach (IntVec3 c in s.RoomOf(2).Cells) s.pm.zones.Set(c, null);
                Pawn target = s.Inmate(0, 0, 1), cellmate = s.Inmate(0, 1, 1), snitch = s.Inmate(wings ? 2 : 1, 0, 1);
                CompInmate sc = snitch.Inmate();
                TaskMakeInformant(sc);
                int start = s.CellOf(snitch);
                InformantTask.Assign(snitch, sc, target);
                List<FloatMenuOption> options = InformantTask.MoveOptions(snitch, sc, target);
                if (options.Count != 3 || options[0].Disabled || options[2].Disabled)
                {
                    TaskFail(check, $"the move menu has {options.Count} options ({string.Join(" | ", options.Select(o => o.Label + (o.Disabled ? " [off]" : "")))}), want the cell, the wing and where they are, the cell on offer");
                    return;
                }
                options[2].action();
                if (s.CellOf(snitch) != start) TaskFail(check, $"leaving them where they are moved them to cell {s.CellOf(snitch)}");
                // Kept apart: both moves refused, with the order as the reason.
                if (KeepApart.Add(snitch, target, "warden", silent: true) != KeepApartResult.Added)
                {
                    TaskFail(check, "premise: the keep-apart order was not written");
                    return;
                }
                string reason = "RP_InformantTask_WhyKeptApart".Translate(target.LabelShort).Resolve();
                string cellWhy = InformantTask.CellMoveRefusal(snitch, sc, target, out _), wingWhy = InformantTask.WingMoveRefusal(snitch, sc, target, out _);
                bool moved = InformantTask.MoveNear(snitch, sc, target, cell: true);
                FloatMenuOption keptOption = InformantTask.MoveOptions(snitch, sc, target)[0];
                if (cellWhy != reason || wingWhy != reason || moved || s.CellOf(snitch) != start || !keptOption.Disabled || !keptOption.Label.Contains(reason))
                    TaskFail(check, $"kept apart: the cell move's refusal \"{cellWhy}\", the wing's \"{wingWhy}\" (want \"{reason}\"), moved {moved} (now in cell {s.CellOf(snitch)}), the option \"{keptOption.Label}\" off {keptOption.Disabled}");
                KeepApart.Lift(KeepApart.OrderBetween(snitch, target), silent: true);
                // Kept apart from the target's cellmate: the cell move is refused with that order (Cellmates.Move would refuse it), the wing move is not.
                if (KeepApart.Add(snitch, cellmate, "warden", silent: true) != KeepApartResult.Added)
                {
                    TaskFail(check, "premise: the keep-apart order with the cellmate was not written");
                    return;
                }
                string mateReason = "RP_InformantTask_WhyKeptApart".Translate(cellmate.LabelShort).Resolve();
                string mateWhy = InformantTask.CellMoveRefusal(snitch, sc, target, out _);
                FloatMenuOption mateOption = InformantTask.MoveOptions(snitch, sc, target)[0];
                if (mateWhy != mateReason || !mateOption.Disabled || !mateOption.Label.Contains(mateReason))
                    TaskFail(check, $"kept apart from the target's cellmate: the cell move's refusal \"{mateWhy}\" (want \"{mateReason}\"), the option \"{mateOption.Label}\" off {mateOption.Disabled}");
                KeepApart.Lift(KeepApart.OrderBetween(snitch, cellmate), silent: true);
                // The move on offer names the cell and how they would get on there.
                FloatMenuOption cellOption = InformantTask.MoveOptions(snitch, sc, target)[0];
                string fit = "RP_InformantTask_MoveFit".Translate(Cellmates.CellAt(map, s.Bed(0, 2), fresh: true)?.number ?? 0, Cellmates.Verdict(s.Bed(0, 2), sc).Key().Translate()).Resolve();
                if (cellOption.Disabled || !cellOption.Label.Contains(fit)) TaskFail(check, $"the cell move on offer reads \"{cellOption.Label}\" (off {cellOption.Disabled}), want it to show \"{fit}\"");
                // Into the target's cell.
                int now = Find.TickManager.TicksGame;
                if (!InformantTask.MoveNear(snitch, sc, target, cell: true) || s.CellOf(snitch) != 0 || !Cellmates.Share(snitch, target) || !Cellmates.WardenHold(sc, now))
                    TaskFail(check, $"the move into the target's cell left the informant in cell {s.CellOf(snitch)} (sharing {Cellmates.Share(snitch, target)}, held as the warden's placement {Cellmates.WardenHold(sc, now)})");
                string same = InformantTask.CellMoveRefusal(snitch, sc, target, out _);
                if (same != "RP_InformantTask_WhySameCell".Translate().Resolve()) TaskFail(check, $"sharing the cell, the cell move's refusal is \"{same}\"");
                if (!wings)
                {
                    RPLog.Message($"[RimPrison:test] informant task: the move onto the wing not run, {s.level.defName} keeps to its wing");
                    return;
                }
                // From a cell in no wing onto the target's wing.
                s.PutBack(snitch, s.Bed(2, 0));
                bool onto = InformantTask.MoveNear(snitch, sc, target, cell: false);
                SecurityLevelDef wing = IntakeSystem.WingOf(snitch.ownership.OwnedBed);
                string again = InformantTask.WingMoveRefusal(snitch, sc, target, out _);
                if (!onto || wing != s.level || again != "RP_InformantTask_WhySameWing".Translate().Resolve())
                    TaskFail(check, $"the move onto the wing: moved {onto}, now in cell {s.CellOf(snitch)} of the {wing?.defName ?? "no"} wing (want {s.level.defName}), then the refusal \"{again}\"");
                RPLog.Message($"[RimPrison:test] informant task: kept apart refused (\"{cellWhy}\"), the cell move and the wing move done");
            });
        }

        // ---- the end of a task -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Re-tasking onto someone else starts the days again; a target carried out of sight keeps the task; the target's release drops it; a target gone without a word and an informant who quit are dropped by the daily
        /// pass; the informant's own release ends theirs.
        /// </summary>
        private static void CheckTaskEnds(Map map)
        {
            const string check = "the end";
            Guarded("informant task: the task ends when either leaves", map, r =>
            {
                TaskScene s = MakeTaskScene(map, r, check);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame;
                InformantTask.Assign(s.snitch, s.sc, s.target);
                s.sc.taskSinceTick = System.Math.Max(0, now - 5 * GenDate.TicksPerDay);
                InformantTask.Assign(s.snitch, s.sc, s.decoy);
                if (s.sc.taskTargetId != s.decoy.thingIDNumber || s.sc.taskSinceTick != now) TaskFail(check, $"re-tasking left the target {s.sc.taskTargetId} (want {s.decoy.thingIDNumber}) and the days from {s.sc.taskSinceTick} (want {now})");
                // A target out of sight for a while (carried by a guard) is still in the prison: the daily pass keeps the task and risks nothing.
                Pawn carrier = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                InformantTask.Assign(s.snitch, s.sc, s.target);
                s.sc.taskSinceTick = System.Math.Max(0, now - GenDate.TicksPerDay);
                InformantTask.ExposureRoll = 0f;
                if (carrier.carryTracker.TryStartCarry(s.target, 1, reserve: false) == 1)
                {
                    InformantTask.Daily(map, only: s.snitch);
                    bool kept = InformantTask.Tasked(s.sc) && s.sc.taskTargetId == s.target.thingIDNumber && !s.sc.snitchExposed;
                    carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out _);
                    if (!kept) TaskFail(check, $"the target carried by a guard: the daily pass ended the task or burned the informant (target {s.sc.taskTargetId}, exposed {s.sc.snitchExposed})");
                }
                else RPLog.Message($"[RimPrison:test] informant task: the guard could not pick the target up (spawned {s.target.Spawned}), the target out of sight not checked");
                InformantTask.ExposureRoll = null;
                // The target released: the task is dropped at once.
                InformantTask.Assign(s.snitch, s.sc, s.target);
                s.tc.innocent = true;
                r.Add(() =>
                {
                    g.shuttle.Forget(s.target);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(s.target);
                });
                ReleaseSystem.Release(g, s.target, record: false);
                if (InformantTask.Tasked(s.sc)) TaskFail(check, $"the target's release left the task on {s.sc.taskTargetId}");
                // A target gone without a word (destroyed): the daily pass drops it.
                InformantTask.Assign(s.snitch, s.sc, s.decoy);
                s.decoy.Destroy(DestroyMode.Vanish);
                InformantTask.Daily(map, only: s.snitch);
                if (InformantTask.Tasked(s.sc)) TaskFail(check, $"the daily pass kept a task on a target who is gone ({s.sc.taskTargetId})");
                // An informant who quit: the daily pass drops it.
                InformantTask.Assign(s.snitch, s.sc, s.creditor);
                s.sc.informantQuit = true;
                InformantTask.Daily(map, only: s.snitch);
                if (InformantTask.Tasked(s.sc)) TaskFail(check, "the daily pass kept the task of an informant who quit");
                s.sc.informantQuit = false;
                // The informant released: their own task ends.
                InformantTask.Assign(s.snitch, s.sc, s.creditor);
                s.sc.innocent = true;
                r.Add(() =>
                {
                    g.shuttle.Forget(s.snitch);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(s.snitch);
                });
                ReleaseSystem.Release(g, s.snitch, record: false);
                if (InformantTask.Tasked(s.sc) || s.sc.taskSinceTick != -1) TaskFail(check, $"the informant's release left their task ({s.sc.taskTargetId}:{s.sc.taskSinceTick})");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------------------------

        private static int taskSeedId = -1;
        private static string taskSeed;
        private static (int target, int since) taskSeedWas;

        /// <summary>A real inmate tasked on another since three days and a bit: both fields off their defaults (put back after the reload).</summary>
        private static void SeedInformantTaskForReload(Map map)
        {
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { security: not null }).OrderBy(p => p.thingIDNumber).ToList();
            if (inmates.Count < 2)
            {
                problems0.Add("saveload: no two inmates to seed an informant's task with");
                return;
            }
            // (A pair in reach of each other if there is one: the hourly pass leaves the days of an informant in reach alone, should the few ticks of the later seeds cross an hour. The start is never before tick 0, which
            // would read as "not in place" and be started by that pass.)
            (Pawn watcher, Pawn watched) = (inmates[0], inmates[1]);
            foreach (Pawn a in inmates)
            {
                if (inmates.FirstOrDefault(o => o != a && InformantTask.InReach(a, o)) is not { } near) continue;
                (watcher, watched) = (a, near);
                break;
            }
            CompInmate c = watcher.Inmate();
            taskSeedWas = (c.taskTargetId, c.taskSinceTick);
            (c.taskTargetId, c.taskSinceTick) = (watched.thingIDNumber, System.Math.Max(0, Find.TickManager.TicksGame - 3 * GenDate.TicksPerDay - 77));
            (taskSeedId, taskSeed) = (watcher.thingIDNumber, InformantTask.Describe(c));
            RPLog.Message($"[RimPrison:test] saveload informant task: {watcher.LabelShort} watches {watched.LabelShort} ({taskSeed}, in reach {InformantTask.InReach(watcher, watched)})");
        }

        /// <summary>The seeded task came back whole; then it goes.</summary>
        private static void CheckInformantTaskAfterReload(Map map)
        {
            if (taskSeedId < 0) return;
            CompInmate c = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == taskSeedId)?.Inmate();
            string after = c == null ? "(inmate gone)" : InformantTask.Describe(c);
            RPLog.Message($"[RimPrison:test] saveload informant task: {(after == taskSeed ? "intact" : "changed")} ({after})");
            if (after != taskSeed) reloadProblems.Add($"an informant's task changed in the round trip: {taskSeed} -> {after}");
            if (c != null) (c.taskTargetId, c.taskSinceTick) = taskSeedWas;
            taskSeedId = -1;
            taskSeed = null;
        }
    }
}
