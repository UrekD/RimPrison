using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for the watch list (Programs/WatchList.cs; gap analysis 7, item 6), called from one line of the events plan, last of its list: the triggers and their three days (the real post, a green light, a beating over a
    /// debt, a death in the family), the flag after a day of very low mood (by a guard, by the unit officer of the wing, by the psychologist's assessment, and by a guard when the psychologist has not come) with its one letter,
    /// the end of the period (time, mood, they leave), the answers (a watch with its yard and mood and care queue, counselling, a padded cell only when one is free and given up at the period's end, nothing), the warden's
    /// End (the measure stops, they stay on the list at risk), no crisis while the letter waits and one for a watch whose checks are overdue, the reservations that block nobody else, the watch's checks every half hour
    /// and a crisis on watch stopped in time (real ticks), a watch no guard keeps (real ticks), a crisis nobody reaches ending in blood loss, a death and one neglect inquiry (real ticks), a padded cell that keeps them
    /// from harm, and the setting that turns crises off (one already running among them).
    /// Each is self-contained: throwaway pawns, the clock moved on (never before 0) and back, the test switches put back by the rollback.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckWatchList(Map map)
        {
            Rand.PushState(20261028);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            try
            {
                CheckWatchDefs(map);
                CheckWatchTriggers(map);
                CheckWatchTriggerHooks(map);
                CheckWatchFlag(map);
                CheckWatchOfficer(map);
                CheckWatchPsychologist(map);
                CheckWatchLettersPerPass(map);
                CheckWatchPeriodEnds(map);
                CheckWatchAnswers(map);
                CheckWatchWardenEnd(map);
                CheckWatchPendingAndOverdue(map);
                CheckWatchReservations(map);
                CheckWatchPaddedAnswer(map);
                CheckWatchChecksAndCrisis(map);
                CheckWatchUnkeptWatch(map);
                CheckWatchUnwatchedCrisisDeath(map);
                CheckWatchPaddedCrisis(map);
                CheckWatchSettingOff(map);
            }
            finally
            {
                Rand.PopState();
                WatchSwitchesOff();
            }
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Zw")).ToArray();
            if (left.Length > 0) problems0.Add($"watch list checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void WatchFail(string check, string what) => problems0.Add($"watch list ({check}): {what}");

        private const int WlHour = GenDate.TicksPerHour, WlDay = GenDate.TicksPerDay;

        private static void WatchSwitchesOff()
        {
            WatchList.PsychOverride = null;
            WatchList.SettingOverride = null;
            WatchList.CrisisRoll = null;
            WatchList.Pool = null;
            WatchList.GuardPool = null;
        }

        /// <summary>The test switches off again when the check is over (whatever it set).</summary>
        private static void WatchSwitches(Rollback r) => r.Add(WatchSwitchesOff);

        /// <summary>
        /// The clock at least at <paramref name="atLeast"/> (put back by the rollback): the checks need a day or more of very low mood behind them, and a tick before 0 reads as "never". No tick runs while it is moved. Call it
        /// first in a check, so that it is put back last.
        /// </summary>
        private static int WatchClock(Rollback r, int atLeast)
        {
            int clock0 = Find.TickManager.TicksGame;
            if (clock0 >= atLeast) return clock0;
            Find.TickManager.DebugSetTicksGame(atLeast);
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            return atLeast;
        }

        /// <summary>A throwaway's watch-list fields as a new inmate's, with a term (not a life one), a clean file and the mood given.</summary>
        private static void WatchReset(Pawn p, float mood = 0.05f)
        {
            CompInmate c = p.Inmate();
            int now = Find.TickManager.TicksGame;
            (c.sentenceDays, c.daysAdded, c.processed, c.awaitingSearch, c.assessed, c.lastTherapyTick, c.tension, c.assailantId) = (200, 0, true, false, true, now, 10f, -1);
            (c.riskTick, c.watchUntilTick, c.crisisCount, c.riskEvent, c.riskEventTick, c.riskCause, c.lowMoodTick) = (-1, -1, 0, RiskTrigger.None, -1, RiskTrigger.None, -1);
            (c.watchAnswer, c.watchCheckTick, c.counselTick, c.riskEndTick, c.paroleDenialTick, c.measureTick) = (WatchAnswer.Pending, -1, -1, -1, -1, -1);
            c.relatives.Clear();
            c.familyCount = 0;
            if (p.needs?.mood != null) p.needs.mood.CurLevel = mood;
            c.RefreshActiveFlag();
        }

        /// <summary>A throwaway inmate at the intake (medium security) with nothing behind them and a very low mood.</summary>
        private static Pawn WatchInmate(Map map, Rollback r, string name, float mood = 0.05f)
        {
            Pawn p = Throwaway(map, r);
            p.Name = new NameSingle("Zw" + name);
            p.Inmate().security = InmateUtility.DefaultSecurity;
            p.Inmate().lastReclassTick = Math.Max(0, Find.TickManager.TicksGame - WlDay);
            WatchReset(p, mood);
            r.Add(() =>
            {
                if (!p.Dead && p.mindState?.mentalStateHandler != null) p.mindState.mentalStateHandler.Reset();
            });
            return p;
        }

        /// <summary>A throwaway guard on duty at the intake, every hour a working one.</summary>
        private static Pawn WatchGuard(Map map, Rollback r, string name)
        {
            Pawn g = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
            g.Name = new NameSingle("Zw" + name);
            if (g.timetable != null)
                for (int hour = 0; hour < 24; hour++) g.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
            if (g.needs?.rest != null) g.needs.rest.CurLevel = 1f;
            return g;
        }

        private static ChoiceLetter_PrisonDecision WatchLetter(Rollback r, Pawn p) =>
            r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is(WatchList.DecisionDef) && l.subjectId == p.thingIDNumber);

        private static int WatchLetters(Rollback r) => r.NewLetters.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is(WatchList.DecisionDef));

        private static string WatchText(Letter l) => l is ChoiceLetter cl ? PsychPlain(cl.Text.Resolve()) : "";

        private static bool HasCut(Pawn p) => p.health.hediffSet.hediffs.Any(h => h.def == HediffDefOf.Cut);

        // ---- the defs ----------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckWatchDefs(Map map)
        {
            Guarded("watch list: the defs", map, r =>
            {
                const string check = "defs";
                MentalStateDef crisis = RPWatchDefOf.RP_Crisis;
                if (crisis == null || crisis.IsAggro || crisis.stateClass != typeof(MentalState_Crisis) || crisis.maxTicksBeforeRecovery <= WatchList.HarmTicks)
                    WatchFail(check, $"the crisis state: aggro {crisis?.IsAggro}, class {crisis?.stateClass?.Name}, longest {crisis?.maxTicksBeforeRecovery} ticks (harm comes at {WatchList.HarmTicks}: it must last longer)");
                // (Only the watch list starts it: no mood break does.)
                if (DefDatabase<MentalBreakDef>.AllDefsListForReading.FirstOrDefault(b => b.mentalState == crisis) is { } breakDef) WatchFail(check, $"the mood break {breakDef.defName} starts the crisis");
                PrisonDecisionDef decision = PrisonDecisionDef.Named(WatchList.DecisionDef);
                if (decision.urgent || decision.Worker is not WatchListDecision) WatchFail(check, $"the letter is not a budgeted decision of its own (urgent {decision.urgent}, worker {decision.Worker?.GetType().Name})");
                WorkGiverDef giver = RPWatchDefOf.RP_WatchCheckGiver;
                if (giver?.workType != WorkTypeDefOf.Warden || !giver.emergency || giver.Worker is not WorkGiver_WatchCheck) WatchFail(check, $"the watch's check is not the guards' emergency work ({giver?.workType?.defName}, emergency {giver?.emergency})");
                if (WatchList.WatchPenalty >= 0f) WatchFail(check, $"the watch's mood penalty is {WatchList.WatchPenalty}");
                float daily = 1f - Mathf.Pow(1f - WatchList.HourlyCrisisChance, 24f);
                if (Mathf.Abs(daily - WatchList.CrisisDailyChance) > 0.001f) WatchFail(check, $"the hourly chance adds up to {daily:P2} a day, the letter states {WatchList.CrisisDailyChance:P0}");
                if (CareDetail.KindCount != Enum.GetValues(typeof(CareKind)).Length) WatchFail(check, $"the care detail counts {CareDetail.KindCount} kinds, there are {Enum.GetValues(typeof(CareKind)).Length}");
            });
        }

        // ---- the triggers --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Each trigger counts for three days (a supermax stay once it is longer than three): the real post's Dear John and death in the family, a parole refusal, a life term begun, a supermax stay.</summary>
        private static void CheckWatchTriggers(Map map)
        {
            Guarded("watch list: the triggers and their three days", map, r =>
            {
                const string check = "triggers";
                int now = WatchClock(r, 10 * WlDay);
                Pawn p = WatchInmate(map, r, "t1");
                CompInmate c = p.Inmate();
                string T() => WatchList.Trigger(p, c, Find.TickManager.TicksGame, out _).ToString();
                if (T() != "None") WatchFail(check, $"a new inmate with nothing behind them has a trigger ({T()})");
                // The post: a partner who writes to end it (the real letter's worker), then a death in the family.
                c.relatives.Add(new Relative { name = "Ana", kind = "RP_Kin_Partner" });
                c.relatives.Add(new Relative { name = "Bea", kind = "RP_Kin_Mother" });
                Relatives.Recount(c);
                if (!RPMailDefOf.RP_Mail_DearJohn.Worker.Deliver(p, c, null)) WatchFail(check, "premise: the Dear John letter was not delivered");
                else if (c.riskEvent != RiskTrigger.DearJohn || c.riskEventTick != now || T() != "DearJohn") WatchFail(check, $"after a Dear John letter: {WatchList.Describe(c)}, trigger {T()}");
                c.riskEventTick = now - 3 * WlDay - 1;
                if (T() != "None") WatchFail(check, $"a letter three days and a tick old still counts ({T()})");
                if (!RPMailDefOf.RP_Mail_Death.Worker.Deliver(p, c, null)) WatchFail(check, "premise: the letter of a death in the family was not delivered");
                else if (T() != "FamilyDeath") WatchFail(check, $"after a death in the family: {WatchList.Describe(c)}, trigger {T()}");
                // Parole refused (the file's day of the refusal).
                (c.riskEvent, c.riskEventTick) = (RiskTrigger.None, -1);
                c.paroleDenialTick = now - WlDay;
                if (T() != "ParoleRefused") WatchFail(check, $"a refusal yesterday: trigger {T()}");
                c.paroleDenialTick = now - 4 * WlDay;
                if (T() != "None") WatchFail(check, $"a refusal four days ago: trigger {T()}");
                // A life term begun: a lifer admitted in the last three days.
                (c.sentenceDays, c.admittedTick) = (-1, now - WlDay);
                if (T() != "LifeTerm") WatchFail(check, $"a lifer admitted yesterday: trigger {T()}");
                c.admittedTick = now - 4 * WlDay;
                if (T() != "None") WatchFail(check, $"a lifer admitted four days ago: trigger {T()}");
                c.sentenceDays = 200;
                // A supermax stay longer than three days, while it lasts.
                if (SupermaxSystem.Level is { } supermax)
                {
                    c.security = supermax;
                    c.lastReclassTick = now - 2 * WlDay;
                    if (T() != "None") WatchFail(check, $"two days in supermax: trigger {T()}");
                    c.lastReclassTick = now - 4 * WlDay;
                    if (T() != "Supermax") WatchFail(check, $"four days in supermax: trigger {T()}");
                    // (The stay passed its three days yesterday: a letter since is the one named, one before it is not.)
                    (c.riskEvent, c.riskEventTick) = (RiskTrigger.DearJohn, now - WlDay / 2);
                    if (T() != "DearJohn") WatchFail(check, $"four days in supermax and a Dear John letter half a day ago: trigger {T()} (the fresher, the letter, expected)");
                    c.riskEventTick = now - 2 * WlDay;
                    if (T() != "Supermax") WatchFail(check, $"four days in supermax and a Dear John letter two days ago: trigger {T()} (the stay, past its days since yesterday, expected)");
                    (c.riskEvent, c.riskEventTick) = (RiskTrigger.None, -1);
                    c.security = InmateUtility.DefaultSecurity;
                }
                // Of two, the later is named.
                c.paroleDenialTick = now - 2 * WlDay;
                (c.riskEvent, c.riskEventTick) = (RiskTrigger.DebtBeating, now - WlDay);
                if (T() != "DebtBeating") WatchFail(check, $"a beating yesterday after a refusal two days ago: trigger {T()}");
            });
        }

        /// <summary>The hooks: a renouncement's green light, a beating over a debt, and the death of kin inside are what can put an inmate at risk.</summary>
        private static void CheckWatchTriggerHooks(Map map)
        {
            Guarded("watch list: a green light is a trigger", map, r =>
            {
                const string check = "green light";
                var s = new GangScene(map, r);
                Pawn boss = s.Inmate(s.ravens, GangRank.Leader, known: false), mate = s.Inmate(s.ravens, known: true), leaver = s.Inmate(s.ravens, known: false);
                CompInmate c = leaver.Inmate();
                (c.riskEvent, c.riskEventTick) = (RiskTrigger.None, -1);
                GangExit.Renounce(leaver, c);
                if (!c.exMarked) RPLog.Message($"[RimPrison:test] watch list: the renouncement put no green light on {leaver.LabelShort} (nobody of the gang could get at them): the hook was not looked at");
                else if (c.riskEvent != RiskTrigger.GreenLight || c.riskEventTick != Find.TickManager.TicksGame) WatchFail(check, $"the green light is no trigger: {WatchList.Describe(c)} (boss {boss.LabelShort}, mate {mate.LabelShort})");
            });
            Guarded("watch list: a beating over a debt is a trigger", map, r =>
            {
                const string check = "debt";
                var s = new EconomyScene(map, r);
                Pawn creditor = s.Inmate(fighter: true), debtor = s.Inmate();
                CompInmate dc = debtor.Inmate();
                (dc.riskEvent, dc.riskEventTick) = (RiskTrigger.None, -1);
                InmateDebt d = s.Overdue(creditor, debtor);
                d.stage = DebtStage.Threatened;
                d.lastBeatingTick = -999999;
                creditor.Inmate().forcedActivity = null;
                s.g.story.lastArcStart.Remove(RPDebtDefOf.RP_Arc_Debt);
                bool beaten = DebtSystem.TryBeat(d, debtor, creditor, map, s.pm);
                creditor.mindState.mentalStateHandler.Reset();
                if (!beaten) RPLog.Message("[RimPrison:test] watch list: the debt's beating did not happen here: the hook was not looked at");
                else if (dc.riskEvent != RiskTrigger.DebtBeating) WatchFail(check, $"the beating is no trigger: {WatchList.Describe(dc)}");
            });
            Guarded("watch list: the death of kin inside is a trigger", map, r =>
            {
                const string check = "kin";
                Pawn parent = Throwaway(map, r, spawn: false);
                Pawn dead = WatchInmate(map, r, "k1"), brother = WatchInmate(map, r, "k2");
                dead.relations.AddDirectRelation(PawnRelationDefOf.Parent, parent);
                brother.relations.AddDirectRelation(PawnRelationDefOf.Parent, parent);
                if (!Kin.Relatives(dead).Any(k => k.other == brother))
                {
                    WatchFail(check, "premise: the two throwaways with one parent are not kin");
                    return;
                }
                Kin.Notify_Died(dead);
                CompInmate bc = brother.Inmate();
                if (bc.riskEvent != RiskTrigger.FamilyDeath || bc.riskEventTick != Find.TickManager.TicksGame) WatchFail(check, $"a brother inside died and it is no trigger: {WatchList.Describe(bc)}");
            });
        }

        // ---- the flag ------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A Dear John letter and a day of very low mood: a guard flags them at the hourly look, with one letter that names who flagged them and why and states the watch's cost.</summary>
        private static void CheckWatchFlag(Map map)
        {
            Guarded("watch list: a Dear John and a day of very low mood, flagged by a guard, one letter", map, r =>
            {
                const string check = "flag";
                int now = WatchClock(r, 3 * WlDay);
                WatchSwitches(r);
                WatchList.PsychOverride = false;
                Pawn g = WatchGuard(map, r, "g1");
                Pawn p = WatchInmate(map, r, "f1");
                CompInmate c = p.Inmate();
                WatchList.Pool = x => x == p;
                WatchList.GuardPool = x => x == g;
                if (CareDetail.Why(g) is { } off)
                {
                    WatchFail(check, $"premise: the check's guard is not on duty ({off})");
                    return;
                }
                PrisonGame game = PrisonGame.Instance;
                // A day of very low mood and nothing behind it: no flag.
                c.lowMoodTick = now - 25 * WlHour;
                WatchList.Hourly(game, map);
                if (c.riskTick >= 0) WatchFail(check, $"flagged with no trigger: {WatchList.Describe(c)}");
                // The letter, and 23 hours of it: no flag yet.
                c.relatives.Add(new Relative { name = "Ana", kind = "RP_Kin_Partner" });
                Relatives.Recount(c);
                RPMailDefOf.RP_Mail_DearJohn.Worker.Deliver(p, c, null);
                c.lowMoodTick = now - 23 * WlHour;
                WatchList.Hourly(game, map);
                if (c.riskTick >= 0) WatchFail(check, $"flagged after 23 hours of very low mood: {WatchList.Describe(c)}");
                // Low but not very low (15%): the clock is not running, and no flag.
                p.needs.mood.CurLevel = 0.15f;
                c.lowMoodTick = now - 25 * WlHour;
                WatchList.Hourly(game, map);
                if (c.riskTick >= 0 || c.lowMoodTick >= 0) WatchFail(check, $"a mood of 15% kept the clock or made the flag: {WatchList.Describe(c)}");
                // A day and an hour under 10%: flagged, one letter.
                p.needs.mood.CurLevel = 0.05f;
                c.lowMoodTick = now - 25 * WlHour;
                WatchList.Hourly(game, map);
                ChoiceLetter_PrisonDecision letter = WatchLetter(r, p);
                if (c.riskTick != now || c.watchUntilTick != now + WatchList.WatchDays * WlDay || c.riskCause != RiskTrigger.DearJohn || c.watchAnswer != WatchAnswer.Pending)
                    WatchFail(check, $"after a day and an hour: {WatchList.Describe(c)} (flagged now, {WatchList.WatchDays} days, Dear John, no answer yet expected)");
                if (letter == null) WatchFail(check, "flagged and no letter");
                else
                {
                    string text = WatchText(letter);
                    string penalty = WatchList.WatchPenalty.ToString("0");
                    if (!text.Contains(g.LabelShort) || !text.Contains(WatchList.CauseText(RiskTrigger.DearJohn)) || !text.Contains(penalty))
                        WatchFail(check, $"the letter does not name the guard {g.LabelShort}, the cause or the watch's cost {penalty}: {text}");
                    if (letter.amount != c.riskTick || !letter.TimeoutActive || letter.decision.Worker.Options(letter).Count != 4 || letter.decision.Worker.TimeoutChoice != 3)
                        WatchFail(check, $"the letter: flag {letter.amount} (the flag's tick {c.riskTick}), timeout {letter.TimeoutActive}, {letter.decision.Worker.Options(letter).Count} options, the timeout picks {letter.decision.Worker.TimeoutChoice} (nothing, 3)");
                }
                // One letter a flag: the next hours write none.
                int letters = WatchLetters(r);
                WatchList.Hourly(game, map);
                WatchList.Hourly(game, map);
                if (WatchLetters(r) != letters) WatchFail(check, $"{WatchLetters(r) - letters} more letters in the next two hours");
                if (!r.NewEntries.Any(e => e.text.Contains(p.LabelShort) && e.text.Contains(g.LabelShort))) WatchFail(check, "the chronicle has no line naming who flagged them");
                if (WatchList.FileLine(c) == null) WatchFail(check, "the file shows nothing");
            });
        }

        /// <summary>The unit officer of their wing flags them before any guard; with no officer on the map, a guard does.</summary>
        private static void CheckWatchOfficer(Map map)
        {
            Guarded("watch list: the unit officer of their wing flags them before any guard", map, r =>
            {
                const string check = "officer";
                int now = WatchClock(r, 3 * WlDay);
                WatchSwitches(r);
                WatchList.PsychOverride = false;
                UnitOfficers.ChiefOverride = false;
                r.Add(() => UnitOfficers.ChiefOverride = null);
                Pawn officer = WatchGuard(map, r, "o1"), g = WatchGuard(map, r, "g2");
                Pawn p = WatchInmate(map, r, "o1");
                CompInmate c = p.Inmate();
                SecurityLevelDef wing = c.security;
                // (A real officer of the wing is off it for the check, and back after.)
                foreach (Pawn s in StaffCommute.AllStaff(map))
                    if (s != officer && s.GetComp<CompStaff>() is { } sc && sc.unitWing == wing)
                    {
                        (SecurityLevelDef was, int since) = (sc.unitWing, sc.unitSinceTick);
                        r.Add(() => (sc.unitWing, sc.unitSinceTick) = (was, since));
                        sc.unitWing = null;
                    }
                CompStaff oc = officer.GetComp<CompStaff>();
                (oc.unitWing, oc.unitSinceTick) = (wing, now - WlDay);
                WatchList.Pool = x => x == p;
                WatchList.GuardPool = x => x == officer || x == g;
                Pawn by = WatchList.FlaggerFor(map, c, out RiskFlagger kind);
                if (by != officer || kind != RiskFlagger.UnitOfficer) WatchFail(check, $"with the wing's officer on duty, {by?.LabelShort ?? "nobody"} ({kind}) flags them (serving {UnitOfficers.Serving(oc)})");
                WatchList.Notify_Trigger(p, RiskTrigger.GreenLight);
                c.lowMoodTick = now - 25 * WlHour;
                WatchList.Hourly(PrisonGame.Instance, map);
                ChoiceLetter_PrisonDecision letter = WatchLetter(r, p);
                if (letter == null) WatchFail(check, $"no letter: {WatchList.Describe(c)}");
                else if (!WatchText(letter).Contains(officer.LabelShort) || !WatchText(letter).Contains(UnitOfficers.WingLabel(wing))) WatchFail(check, $"the letter does not name the officer {officer.LabelShort} and the wing: {WatchText(letter)}");
                // The officer off the map: a guard.
                WatchList.GuardPool = x => x == g;
                if (WatchList.FlaggerFor(map, c, out kind) != g || kind != RiskFlagger.Guard) WatchFail(check, $"with no officer, {kind} flags them");
            });
        }

        /// <summary>A psychologist on the staff: the hourly look waits, the session is due, and the assessment flags them; not seen within the grace hours, a guard does.</summary>
        private static void CheckWatchPsychologist(Map map)
        {
            Guarded("watch list: the psychologist's assessment flags them; not seen in time, a guard does", map, r =>
            {
                const string check = "psychologist";
                int now = WatchClock(r, 3 * WlDay);
                WatchSwitches(r);
                WatchList.PsychOverride = true;
                Pawn g = WatchGuard(map, r, "g3");
                Pawn psych = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Psychologist);
                psych.Name = new NameSingle("Zwpsy");
                Pawn p = WatchInmate(map, r, "p1"), late = WatchInmate(map, r, "p2");
                CompInmate c = p.Inmate(), lc = late.Inmate();
                WatchList.Pool = x => x == p || x == late;
                WatchList.GuardPool = x => x == g;
                (c.paroleDenialTick, c.lowMoodTick) = (now - WlDay, now - 25 * WlHour);
                WatchList.Hourly(PrisonGame.Instance, map);
                if (c.riskTick >= 0) WatchFail(check, "the hourly look flagged them with a psychologist to assess them");
                if (!PsychologyUtility.WantsSession(c)) WatchFail(check, "the psychologist's session is not due");
                PsychologyUtility.Complete(psych, p);
                ChoiceLetter_PrisonDecision letter = WatchLetter(r, p);
                if (c.riskTick != now || letter == null) WatchFail(check, $"the assessment did not flag them: {WatchList.Describe(c)}, letter {letter != null}");
                else if (!WatchText(letter).Contains(psych.LabelShort)) WatchFail(check, $"the letter does not name the psychologist: {WatchText(letter)}");
                if (WatchList.AssessmentDue(c)) WatchFail(check, "still due the assessment once flagged");
                // Not seen in the grace hours: the guard flags them.
                (lc.paroleDenialTick, lc.lowMoodTick) = (now - WlDay, now - (WatchList.LowHours + WatchList.PsychGraceHours + 1) * WlHour);
                WatchList.Hourly(PrisonGame.Instance, map);
                ChoiceLetter_PrisonDecision lateLetter = WatchLetter(r, late);
                if (lc.riskTick != now || lateLetter == null || !WatchText(lateLetter).Contains(g.LabelShort))
                    WatchFail(check, $"{WatchList.LowHours + WatchList.PsychGraceHours + 1} hours and no session: {WatchList.Describe(lc)}, letter {(lateLetter == null ? "none" : WatchText(lateLetter))}");
            });
        }

        /// <summary>Three inmates due the flag in one hour: two letters that hour, the third the next.</summary>
        private static void CheckWatchLettersPerPass(Map map)
        {
            Guarded("watch list: at most two letters in one hourly pass", map, r =>
            {
                const string check = "letters a pass";
                int now = WatchClock(r, 3 * WlDay);
                WatchSwitches(r);
                WatchList.PsychOverride = false;
                Pawn g = WatchGuard(map, r, "g16");
                var three = new List<Pawn> { WatchInmate(map, r, "lp1"), WatchInmate(map, r, "lp2"), WatchInmate(map, r, "lp3") };
                WatchList.Pool = three.Contains;
                WatchList.GuardPool = x => x == g;
                if (CareDetail.Why(g) is { } off)
                {
                    WatchFail(check, $"premise: the check's guard is not on duty ({off})");
                    return;
                }
                foreach (Pawn x in three)
                {
                    WatchList.Notify_Trigger(x, RiskTrigger.DebtBeating);
                    x.Inmate().lowMoodTick = now - 25 * WlHour;
                }
                int letters0 = WatchLetters(r);
                WatchList.Hourly(PrisonGame.Instance, map);
                int first = WatchLetters(r) - letters0, flagged = three.Count(x => x.Inmate().riskTick >= 0);
                WatchList.Hourly(PrisonGame.Instance, map);
                int both = WatchLetters(r) - letters0;
                if (first != WatchList.MaxLettersPerPass || flagged != WatchList.MaxLettersPerPass || both != 3)
                    WatchFail(check, $"three due the flag: {first} letters ({flagged} flagged) in the first pass, {both} after the second ({WatchList.MaxLettersPerPass}, then 3, expected)");
            });
        }

        // ---- the period ----------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckWatchPeriodEnds(Map map)
        {
            Guarded("watch list: the period ends when the mood recovers, after three days, or by the warden", map, r =>
            {
                const string check = "end";
                int now = WatchClock(r, 6 * WlDay);
                WatchSwitches(r);
                Pawn g = WatchGuard(map, r, "g4");
                Pawn p = WatchInmate(map, r, "e1");
                CompInmate c = p.Inmate();
                WatchList.Pool = x => x == p;
                WatchList.GuardPool = _ => false;
                PrisonGame game = PrisonGame.Instance;
                ChoiceLetter_PrisonDecision FlagNow()
                {
                    WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
                    return WatchList.Flag(p, c, map, g, RiskFlagger.Guard);
                }
                bool Open(Letter l) => l != null && Find.LetterStack.LettersListForReading.Contains(l);
                // The mood back above 30%: off the list, the letter closed.
                ChoiceLetter_PrisonDecision first = FlagNow();
                p.needs.mood.CurLevel = 0.35f;
                WatchList.Hourly(game, map);
                if (c.riskTick >= 0 || c.riskEndTick != now) WatchFail(check, $"a mood of 35%: {WatchList.Describe(c)} (off the list now expected)");
                if (Open(first)) WatchFail(check, "the letter stayed open after the period ended");
                // Not flagged again within two days of the end.
                p.needs.mood.CurLevel = 0.05f;
                c.lowMoodTick = now - 25 * WlHour;
                WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
                if (WatchList.Qualifies(p, c, now)) WatchFail(check, "flagged again the hour the last period ended");
                c.riskEndTick = now - WatchList.ReflagDays * WlDay;
                if (!WatchList.Qualifies(p, c, now)) WatchFail(check, $"not flagged again {WatchList.ReflagDays} days after the end: {WatchList.Describe(c)}");
                // 29% is not above 30%; three days later the period runs out (the clock moved on to its end, and back).
                FlagNow();
                p.needs.mood.CurLevel = 0.29f;
                WatchList.Hourly(game, map);
                if (c.riskTick < 0) WatchFail(check, "the period ended at a mood of 29%");
                int end = c.watchUntilTick, clock = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock));
                Find.TickManager.DebugSetTicksGame(end);
                WatchList.Hourly(game, map);
                if (c.riskTick >= 0 || c.riskEndTick != end) WatchFail(check, $"{WatchList.WatchDays} days on, still on the list: {WatchList.Describe(c)}");
                Find.TickManager.DebugSetTicksGame(clock);
                // The warden has nothing to end while the letter waits: the period and its letter stay (the warden's End is CheckWatchWardenEnd's).
                c.riskEndTick = -1;
                ChoiceLetter_PrisonDecision byWarden = FlagNow();
                if (WatchList.EndByWarden(p) || c.riskTick != now || c.watchAnswer != WatchAnswer.Pending || !Open(byWarden))
                    WatchFail(check, $"the warden's End on a letter not yet answered: {WatchList.Describe(c)}, letter open {Open(byWarden)} (nothing to end expected)");
                WatchList.End(p, c, null);
                // Gone (released, transferred, dead): the letter is closed.
                c.riskEndTick = -1;
                ChoiceLetter_PrisonDecision gone = FlagNow();
                WatchList.Notify_Leaving(p);
                if (c.riskTick >= 0 || Open(gone)) WatchFail(check, $"gone: {WatchList.Describe(c)}, letter open {Open(gone)}");
            });
        }

        // ---- the answers ---------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckWatchAnswers(Map map)
        {
            Guarded("watch list: the letter's answers - a watch, counselling, nothing", map, r =>
            {
                const string check = "answers";
                int now = WatchClock(r, 4 * WlDay);
                WatchSwitches(r);
                WatchList.Pool = _ => false;
                Pawn g = WatchGuard(map, r, "g5");
                ChoiceLetter_PrisonDecision FlagNew(Pawn p)
                {
                    WatchList.Notify_Trigger(p, RiskTrigger.DebtBeating);
                    return WatchList.Flag(p, p.Inmate(), map, g, RiskFlagger.Guard);
                }
                // A watch: out of the yard, the stated mood penalty, a check due at once and on the care detail's queue.
                Pawn w = WatchInmate(map, r, "a1");
                CompInmate wc = w.Inmate();
                PrisonDecisions.Choose(FlagNew(w), 0);
                if (wc.watchAnswer != WatchAnswer.Watch || !WatchList.OnWatch(wc)) WatchFail(check, $"a watch was chosen: {WatchList.Describe(wc)}");
                if (!Sanctions.Banned(wc, Sanction.LoseYard)) WatchFail(check, "on watch and still in the yard");
                if (!RPWatchDefOf.RP_OnWatch.Worker.CurrentState(w).Active) WatchFail(check, "on watch and no mood penalty");
                if (!WatchList.CheckDue(w)) WatchFail(check, "the first check is not due at once");
                InmateCandidates.Shared.Invalidate();
                if (!InmateCandidates.Shared.WatchCheck(map).Contains(w)) WatchFail(check, "the guards' list of checks does not have them");
                if (CareDetail.Of(map) is { } care)
                {
                    care.ForceRebuild();
                    if (!care.QueueFor(map).Any(q => q.inmate == w && q.kind == CareKind.Watch)) WatchFail(check, "the care detail's queue has no check for them");
                }
                if (CareDetail.TryGive(g, CareKind.Watch, w) is not { } job || job.def != RPWatchDefOf.RP_WatchCheck)
                    WatchFail(check, $"the care detail gives the guard no check (warden work {g.workSettings?.WorkIsActive(WorkTypeDefOf.Warden)}, reach {g.CanReach(w, PathEndMode.Touch, Danger.Deadly)})");
                WatchList.Notify_Checked(g, w);
                if (WatchList.CheckDue(w)) WatchFail(check, "a check is due right after one");
                wc.watchCheckTick = now - WatchList.CheckEveryTicks;
                if (!WatchList.CheckDue(w)) WatchFail(check, $"no check due {WatchList.CheckEveryTicks} ticks after the last");
                // Watched: no crisis is due, however long the mood stays very low.
                (wc.riskTick, wc.lowMoodTick, wc.watchUntilTick) = (now - 2 * WlDay, now - 3 * WlDay, now + WlDay);
                if (WatchList.CrisisDue(w, wc, now)) WatchFail(check, "a crisis is due on watch");
                // Counselling: the counsellor's (or the psychologist's) hour a day.
                Pawn cs = WatchInmate(map, r, "a2");
                CompInmate cc = cs.Inmate();
                ChoiceLetter_PrisonDecision counselLetter = FlagNew(cs);
                Pawn counsellor = WatchList.Counsellor(map);
                string refused = counselLetter.decision.Worker.Disabled(counselLetter, 2);
                if ((counsellor == null) != (refused != null)) WatchFail(check, $"counselling with {counsellor?.LabelShort ?? "nobody"} to give it is refused \"{refused}\"");
                if (counsellor == null)
                {
                    counsellor = ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
                    counsellor.Name = new NameSingle("Zwcou");
                }
                if (counselLetter.decision.Worker.Disabled(counselLetter, 2) is { } still) WatchFail(check, $"counselling refused with a counsellor: {still}");
                PrisonDecisions.Choose(counselLetter, 2);
                if (cc.watchAnswer != WatchAnswer.Counselling || !WatchList.CounselDue(cs)) WatchFail(check, $"counselling was chosen: {WatchList.Describe(cc)}, due {WatchList.CounselDue(cs)}");
                InmateCandidates.Shared.Invalidate();
                if (!InmateCandidates.Shared.Counselling(map).Contains(cs)) WatchFail(check, "the counsellors' list does not have them");
                WatchList.Notify_Counselled(counsellor, cs);
                if (cc.counselTick != now || WatchList.CounselDue(cs) || cs.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPWatchDefOf.RP_Counselled) == null)
                    WatchFail(check, $"after the session: {WatchList.Describe(cc)}, due {WatchList.CounselDue(cs)}, memory {cs.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPWatchDefOf.RP_Counselled) != null}");
                // Unanswered: nothing.
                Pawn n = WatchInmate(map, r, "a3");
                FlagNew(n).Lapse();
                if (n.Inmate().watchAnswer != WatchAnswer.Nothing || WatchList.OnWatch(n.Inmate())) WatchFail(check, $"a letter left to run out: {WatchList.Describe(n.Inmate())}");
            });
        }

        /// <summary>The warden's End stops the measure (a watch: the yard and their things back; counselling) and keeps them on the list, at risk, with the crisis rules of one nothing was done for.</summary>
        private static void CheckWatchWardenEnd(Map map)
        {
            Guarded("watch list: the warden's End stops the measure, and they stay on the list at risk", map, r =>
            {
                const string check = "warden's end";
                int now = WatchClock(r, 4 * WlDay);
                WatchSwitches(r);
                Pawn g = WatchGuard(map, r, "g10");
                Pawn w = WatchInmate(map, r, "we1"), n = WatchInmate(map, r, "we2");
                CompInmate c = w.Inmate(), nc = n.Inmate();
                WatchList.Pool = x => x == w;
                WatchList.GuardPool = _ => false;
                PrisonGame game = PrisonGame.Instance;
                WatchList.Notify_Trigger(w, RiskTrigger.DearJohn);
                ChoiceLetter_PrisonDecision letter = WatchList.Flag(w, c, map, g, RiskFlagger.Guard);
                if (WatchList.CanEndMeasure(c)) WatchFail(check, "a measure to end before the letter is answered");
                PrisonDecisions.Choose(letter, 0);
                if (!WatchList.OnWatch(c) || !WatchList.CanEndMeasure(c))
                {
                    WatchFail(check, $"premise: a watch was chosen and they are not on watch: {WatchList.Describe(c)}");
                    return;
                }
                int until = c.watchUntilTick;
                bool ended = WatchList.EndByWarden(w);
                if (!ended || c.riskTick != now || c.watchUntilTick != until || c.watchAnswer != WatchAnswer.Nothing || WatchList.OnWatch(c))
                    WatchFail(check, $"after the warden's End of a watch: ended {ended}, {WatchList.Describe(c)} (still flagged at {now} until {until}, nothing done, expected)");
                if (Sanctions.Banned(c, Sanction.LoseYard)) WatchFail(check, "the watch ended and still out of the yard");
                if (RPWatchDefOf.RP_OnWatch.Worker.CurrentState(w).Active) WatchFail(check, "the watch ended and still the mood penalty of the belongings taken");
                if (WatchList.CheckDue(w)) WatchFail(check, "the watch ended and a check is still due");
                if (WatchList.FileLine(c) == null || !WatchList.Listed(map).Contains(w)) WatchFail(check, "the watch ended and they are off the screens' list");
                if (WatchList.CanEndMeasure(c) || WatchList.EndByWarden(w)) WatchFail(check, "something to end again with nothing done");
                // A day more of very low mood, the dice up: the crisis of one nothing was done for.
                (c.riskTick, c.lowMoodTick) = (now - 25 * WlHour, now - 50 * WlHour);
                (WatchList.SettingOverride, WatchList.CrisisRoll) = (true, true);
                bool due = WatchList.CrisisDue(w, c, now);
                WatchList.Hourly(game, map);
                if (!due || !WatchList.InCrisis(w) || c.crisisCount != 1)
                    WatchFail(check, $"after the warden's End no crisis can come: due {due}, in one {WatchList.InCrisis(w)}, count {c.crisisCount}, {WatchList.Describe(c)}");
                w.mindState.mentalStateHandler.Reset();
                // Counselling: the sessions stop the same way.
                if (WatchList.Counsellor(map) == null)
                {
                    Pawn counsellor = ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
                    counsellor.Name = new NameSingle("Zwcou2");
                }
                WatchList.Notify_Trigger(n, RiskTrigger.FamilyDeath);
                PrisonDecisions.Choose(WatchList.Flag(n, nc, map, g, RiskFlagger.Guard), 2);
                if (nc.watchAnswer != WatchAnswer.Counselling) WatchFail(check, $"premise: counselling was chosen: {WatchList.Describe(nc)}");
                else if (!WatchList.EndByWarden(n) || nc.riskTick < 0 || nc.watchAnswer != WatchAnswer.Nothing || WatchList.CounselDue(n))
                    WatchFail(check, $"after the warden's End of counselling: {WatchList.Describe(nc)}, a session due {WatchList.CounselDue(n)} (still flagged, nothing done, no session, expected)");
            });
        }

        /// <summary>
        /// No crisis while the letter waits, on the desk or held back by the day's budget (Intel offers the letter, or says it comes tomorrow); after it lapses, the crisis rules apply. A watch counts only while it is
        /// kept: a check (or the start of the watch) no more than <see cref="WatchList.CheckMostTicks"/> ago.
        /// </summary>
        private static void CheckWatchPendingAndOverdue(Map map)
        {
            Guarded("watch list: no crisis while the letter waits, and a watch whose checks are overdue is no watch", map, r =>
            {
                const string check = "pending and overdue";
                int now = WatchClock(r, 4 * WlDay);
                WatchSwitches(r);
                (WatchList.SettingOverride, WatchList.CrisisRoll) = (true, true);
                Pawn g = WatchGuard(map, r, "g11");
                Pawn p = WatchInmate(map, r, "ov1");
                CompInmate c = p.Inmate();
                WatchList.Pool = x => x == p;
                WatchList.GuardPool = _ => false;
                PrisonGame game = PrisonGame.Instance;
                WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
                ChoiceLetter_PrisonDecision letter = WatchList.Flag(p, c, map, g, RiskFlagger.Guard);
                // (A day more of very low mood since the flag: every rule but the answer's would let a crisis come. The letter is the flag's still.)
                (c.riskTick, c.lowMoodTick) = (now - 25 * WlHour, now - 50 * WlHour);
                letter.amount = c.riskTick;
                bool Rolls()
                {
                    WatchList.Hourly(game, map);
                    bool started = WatchList.InCrisis(p);
                    if (started)
                    {
                        p.mindState.mentalStateHandler.Reset();
                        c.crisisCount--;
                    }
                    return started;
                }
                if (Rolls()) WatchFail(check, "a crisis came while the letter waited on the desk");
                if (IntelTab.WatchRowButton(p, c, out ChoiceLetter_PrisonDecision desk) != IntelTab.WatchButton.Reopen || desk != letter) WatchFail(check, $"Intel does not offer the letter on the desk ({IntelTab.WatchRowButton(p, c, out _)})");
                // Held back by the day's budget (as StoryLetters.Defer keeps it): the same, and Intel says it comes tomorrow.
                List<ChoiceLetter_PrisonDecision> held = game.story.deferredLetters;
                Find.LetterStack.RemoveLetter(letter);
                held.Add(letter);
                r.Add(() => held.Remove(letter));
                if (Rolls()) WatchFail(check, "a crisis came while the letter was held back by the day's budget");
                IntelTab.WatchButton heldButton = IntelTab.WatchRowButton(p, c, out ChoiceLetter_PrisonDecision heldDesk);
                if (heldButton != IntelTab.WatchButton.Held || heldDesk != null) WatchFail(check, $"Intel offers {heldButton} for a letter held back (the letter tomorrow expected)");
                // Lapsed: nothing done, the crisis rules apply, and Intel has nothing to end.
                held.Remove(letter);
                letter.Lapse();
                if (c.watchAnswer != WatchAnswer.Nothing) WatchFail(check, $"premise: the lapse did not answer nothing: {WatchList.Describe(c)}");
                if (IntelTab.WatchRowButton(p, c, out _) != IntelTab.WatchButton.None) WatchFail(check, $"Intel offers {IntelTab.WatchRowButton(p, c, out _)} with nothing done");
                if (!Rolls()) WatchFail(check, $"the letter lapsed, a day more of very low mood, the dice up, and no crisis: {WatchList.Describe(c)}");
                // A watch just begun: kept, no crisis; Intel offers to end it.
                WatchList.Answer(p, c, WatchAnswer.Watch, map);
                if (!WatchList.Watched(c, now) || Rolls()) WatchFail(check, $"a watch just begun: kept {WatchList.Watched(c, now)}, a crisis came {WatchList.InCrisis(p)}: {WatchList.Describe(c)}");
                if (IntelTab.WatchRowButton(p, c, out _) != IntelTab.WatchButton.End) WatchFail(check, $"Intel offers {IntelTab.WatchRowButton(p, c, out _)} for a watch (End expected)");
                // Begun longer ago than the longest gap and never checked: no watch.
                c.measureTick = now - WatchList.CheckMostTicks - 1;
                if (WatchList.Watched(c, now) || !Rolls()) WatchFail(check, $"a watch begun {WatchList.CheckMostTicks + 1} ticks ago and never checked: kept {WatchList.Watched(c, now)}, and no crisis came: {WatchList.Describe(c)}");
                // Checked in time: kept.
                c.watchCheckTick = now - WatchList.CheckEveryTicks;
                if (!WatchList.Watched(c, now) || Rolls()) WatchFail(check, $"a watch checked {WatchList.CheckEveryTicks} ticks ago: kept {WatchList.Watched(c, now)}, a crisis came: {WatchList.Describe(c)}");
                // The last check too long ago: no watch.
                c.watchCheckTick = now - WatchList.CheckMostTicks - 1;
                if (WatchList.Watched(c, now) || !Rolls()) WatchFail(check, $"the last check {WatchList.CheckMostTicks + 1} ticks ago: kept {WatchList.Watched(c, now)}, and no crisis came: {WatchList.Describe(c)}");
            });
        }

        /// <summary>A guard on a watch's check, or sent to a crisis, blocks nobody else's work on the inmate (a tend, a rescue, therapy reserve them on the default layer), and a second guard is not sent to the same one.</summary>
        private static void CheckWatchReservations(Map map)
        {
            Guarded("watch list: a check or a crisis call blocks no one else's work on the inmate, and one guard goes", map, r =>
            {
                const string check = "reservations";
                WatchSwitches(r);
                WatchList.Pool = _ => false;
                Pawn g1 = WatchGuard(map, r, "g13"), g2 = WatchGuard(map, r, "g14"), other = WatchGuard(map, r, "g15");
                Pawn p = WatchInmate(map, r, "rv1");
                CompInmate c = p.Inmate();
                WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
                WatchList.Flag(p, c, map, g1, RiskFlagger.Guard);
                WatchList.Answer(p, c, WatchAnswer.Watch, map);
                var checker = (WorkGiver_WatchCheck)RPWatchDefOf.RP_WatchCheckGiver.Worker;
                var responder = (WorkGiver_CrisisResponse)DefDatabase<WorkGiverDef>.GetNamed("RP_CrisisResponseGiver").Worker;
                void Stop()
                {
                    if (!g1.Destroyed && g1.jobs?.curJob != null) g1.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                r.Add(Stop);
                if (!checker.HasJobOnThing(g1, p))
                {
                    RPLog.Message($"[RimPrison:test] watch list: the throwaway guard is given no check (reach {g1.CanReach(p, PathEndMode.Touch, Danger.Deadly)}): the reservations were not looked at");
                    return;
                }
                Job look = checker.JobOnThing(g1, p);
                g1.jobs.StartJob(look, JobCondition.InterruptForced);
                if (g1.CurJob != look)
                {
                    WatchFail(check, $"premise: the guard did not start the check ({g1.CurJobDef?.defName})");
                    return;
                }
                if (!other.CanReserve(p)) WatchFail(check, $"a guard on the watch's check blocks the inmate for any other work (a tend, a rescue, therapy): reserved by {map.reservationManager.FirstRespectedReserver(p, other)?.LabelShort}");
                if (checker.HasJobOnThing(g2, p)) WatchFail(check, "a second guard is given the same check");
                Stop();
                // A crisis the staff have seen: one guard is sent, not two, and nobody else's work is blocked. Not seen and not on watch, nobody is sent.
                if (!WatchList.TryStartCrisis(p, c, forced: true) || p.MentalState is not MentalState_Crisis state)
                {
                    WatchFail(check, $"premise: no crisis started ({p.MentalStateDef?.defName})");
                    return;
                }
                c.watchAnswer = WatchAnswer.Nothing;
                state.noticed = false;
                if (WorkGiver_CrisisResponse.Candidate(p)) WatchFail(check, "a guard is sent to a crisis nobody has seen, of one not on watch");
                state.noticed = true;
                if (!responder.HasJobOnThing(g1, p))
                {
                    WatchFail(check, $"premise: no guard is sent to a crisis the staff have seen (candidate {WorkGiver_CrisisResponse.Candidate(p)})");
                    return;
                }
                Job go = responder.JobOnThing(g1, p);
                g1.jobs.StartJob(go, JobCondition.InterruptForced);
                if (responder.HasJobOnThing(g2, p)) WatchFail(check, "a second guard is sent to the same crisis");
                if (!other.CanReserve(p)) WatchFail(check, $"a guard sent to a crisis blocks the inmate for any other work: reserved by {map.reservationManager.FirstRespectedReserver(p, other)?.LabelShort}");
                Stop();
                p.mindState.mentalStateHandler.Reset();
            });
        }

        /// <summary>
        /// The padded cell is offered only with the psych wing's project done and a padded cell free (chosen anyway, nothing is done and it is said); chosen, they move to it, and go back to their level when the period
        /// ends or the warden ends it; one with a case of their own for the psych wing stays.
        /// </summary>
        private static void CheckWatchPaddedAnswer(Map map)
        {
            Guarded("watch list: a padded cell only when the psych wing has a free one, for the period only", map, r =>
            {
                const string check = "padded answer";
                // (A day on the clock at least: the breaks of a case of their own are hours before now.)
                WatchClock(r, WlDay);
                WatchSwitches(r);
                WatchList.Pool = _ => false;
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = InmateUtility.DefaultSecurity;
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                s.RegimeAllDay(s.level, RPDefOf.RP_Lockup);
                Pawn g = s.Staff("Zwg6", "RP_Guard", s.Corridor);
                Pawn p = s.Inmate("Zwpa", medium, s.plain, s.CellQ);
                WatchReset(p);
                CompInmate c = p.Inmate();
                s.Project(false);
                WatchList.Notify_Trigger(p, RiskTrigger.FamilyDeath);
                ChoiceLetter_PrisonDecision letter = WatchList.Flag(p, c, map, g, RiskFlagger.Guard);
                if (letter.decision.Worker.Disabled(letter, 1) == null) WatchFail(check, "a padded cell is offered without the psych wing's project");
                if (WatchText(letter).Contains("RP_Watch_PaddedYes".Translate().Resolve())) WatchFail(check, $"the letter says a padded cell is free without the project: {WatchText(letter)}");
                // Chosen all the same (the letter was open when the cell went): nothing is done, and the chronicle says so beside the answer it recorded.
                letter.decision.Worker.Choose(letter, map, 1);
                string fallback = PsychPlain("RP_Chr_WatchFallback".Translate(p.LabelShort, "RP_Watch_OptPadded".Translate().Resolve()).Resolve());
                if (c.watchAnswer != WatchAnswer.Nothing || c.security != medium || !r.NewEntries.Any(e => PsychPlain(e.text).Contains(fallback)))
                    WatchFail(check, $"a padded cell chosen that cannot be had: {WatchList.Describe(c)}, level {c.security?.defName}, the line that nothing was done {r.NewEntries.Any(e => PsychPlain(e.text).Contains(fallback))}");
                c.watchAnswer = WatchAnswer.Pending;
                s.Project(true);
                if (letter.decision.Worker.Disabled(letter, 1) is { } refused) WatchFail(check, $"the project done and a padded cell free, still refused: {refused}");
                PrisonDecisions.Choose(letter, 1);
                if (c.security != s.level || p.ownership?.OwnedBed != s.padded || c.watchAnswer != WatchAnswer.Padded || c.psychFrom != medium || c.measureTick != c.lastReclassTick)
                    WatchFail(check, $"moved to a padded cell: level {c.security?.defName}, bed {p.ownership?.OwnedBed?.def.defName}, {WatchList.Describe(c)}, from {c.psychFrom?.defName}, moved at {c.lastReclassTick}");
                // The only padded cell is taken: refused for the next.
                Pawn q = s.Inmate("Zwpb", medium, null, s.Corridor);
                WatchReset(q);
                WatchList.Notify_Trigger(q, RiskTrigger.FamilyDeath);
                ChoiceLetter_PrisonDecision second = WatchList.Flag(q, q.Inmate(), map, g, RiskFlagger.Guard);
                if (second.decision.Worker.Disabled(second, 1) == null) WatchFail(check, "a padded cell is offered when the only one is taken");
                WatchList.End(q, q.Inmate(), null);
                if (!InmateUtility.HasRoomIn(p, medium))
                {
                    RPLog.Message($"[RimPrison:test] watch list: no free bed in {medium.label} for the way back from the padded cell: the period's end was not looked at");
                    return;
                }
                // The period runs out: back to the level they came from.
                c.watchUntilTick = Find.TickManager.TicksGame;
                WatchList.Pool = x => x == p;
                WatchList.Hourly(s.g, map);
                WatchList.Pool = _ => false;
                if (c.riskTick >= 0 || c.security != medium || p.ownership?.OwnedBed == s.padded)
                    WatchFail(check, $"the period over: {WatchList.Describe(c)}, level {c.security?.defName}, bed {p.ownership?.OwnedBed?.def.defName} (back to {medium.defName} expected)");
                // Flagged again and moved: the warden's End sends them back as well, and they stay on the list with nothing done.
                c.riskEndTick = -1;
                WatchList.Notify_Trigger(p, RiskTrigger.FamilyDeath);
                ChoiceLetter_PrisonDecision third = WatchList.Flag(p, c, map, g, RiskFlagger.Guard);
                PrisonDecisions.Choose(third, 1);
                if (c.security != s.level) WatchFail(check, $"premise: flagged again, not moved to the padded cell ({c.security?.defName}, {WatchList.Describe(c)})");
                else if (!WatchList.EndByWarden(p) || c.security != medium || c.riskTick < 0 || c.watchAnswer != WatchAnswer.Nothing)
                    WatchFail(check, $"the warden's End of a padded cell: level {c.security?.defName}, {WatchList.Describe(c)} (back to {medium.defName}, still on the list with nothing done, expected)");
                WatchList.End(p, c, null);
                // A case of their own (two breaks since they last moved): the letter says they would stay, and they do.
                c.riskEndTick = -1;
                int now = Find.TickManager.TicksGame;
                c.lastReclassTick = Math.Min(c.lastReclassTick, now - 3 * WlHour);
                c.breakTicks.Clear();
                c.breakTicks.AddRange(new[] { now - 2 * WlHour, now - WlHour });
                WatchList.Notify_Trigger(p, RiskTrigger.FamilyDeath);
                ChoiceLetter_PrisonDecision fourth = WatchList.Flag(p, c, map, g, RiskFlagger.Guard);
                if (!WatchText(fourth).Contains(PsychPlain("RP_Watch_PaddedCase".Translate().Resolve()))) WatchFail(check, $"with a case of their own the letter does not say they would stay: {WatchText(fourth)}");
                PrisonDecisions.Choose(fourth, 1);
                if (c.security != s.level || c.measureTick != -1) WatchFail(check, $"with a case of their own: level {c.security?.defName}, {WatchList.Describe(c)} (in the padded cell for good expected)");
                c.watchUntilTick = Find.TickManager.TicksGame;
                WatchList.Pool = x => x == p;
                WatchList.Hourly(s.g, map);
                WatchList.Pool = _ => false;
                if (c.riskTick >= 0 || c.security != s.level) WatchFail(check, $"with a case of their own, the period over: {WatchList.Describe(c)}, level {c.security?.defName} (still in the psych wing expected)");
            });
        }

        // ---- the watch in real ticks -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>On watch in a cell, with a guard in the corridor: the real work givers check on them at least every <see cref="WatchList.CheckMostTicks"/> ticks, and a crisis is stopped before any harm.</summary>
        private static void CheckWatchChecksAndCrisis(Map map)
        {
            Guarded("watch list: a watch is checked every half hour, and a crisis on watch is stopped in time (real ticks)", map, r =>
            {
                const string check = "watch ticks";
                WatchSwitches(r);
                // (The hour's pass leaves the scene's inmate alone: the period is the check's own.)
                WatchList.Pool = _ => false;
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = InmateUtility.DefaultSecurity;
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                Pawn guard = s.Staff("Zwg7", "RP_Guard", s.Corridor);
                guard.workSettings?.SetPriority(WorkTypeDefOf.Warden, 1);
                // (On shift for the whole check, as the supermax scenes' guards are: the prison's hourly pass writes every member of staff's shift into their timetable, a day guard's from 22:00 is sleep, and
                // an off-shift guard takes leisure and rest before work. In census-c84-ev1 the pass put Zwg7 off shift inside the first window (the commute tried to send them home: "Zwg7 could not leave",
                // the unit has no way out), and the check after 298 came at 2215.)
                supermaxGuards.Add(guard);
                r.Add(() => supermaxGuards.Remove(guard));
                Pawn p = s.Inmate("Zww1", medium, s.plain, s.CellQ);
                WatchReset(p);
                CompInmate c = p.Inmate();
                r.Add(() =>
                {
                    if (!p.Dead) p.mindState.mentalStateHandler.Reset();
                });
                WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
                WatchList.Flag(p, c, map, guard, RiskFlagger.Guard);
                WatchList.Answer(p, c, WatchAnswer.Watch, map);
                // (The mood up while the ticks run: the checks no longer depend on it, and a vanilla break would upset their timing.)
                p.needs.mood.CurLevel = 0.5f;
                // The rule the limit rests on: a check falls due CheckEveryTicks after the last, the beat calls a guard within CallEveryTicks, and the walk and the look must fit in what is left of
                // CheckMostTicks (DESIGN, the watch list). The scene's walk is a few steps; a scene where it did not fit would test the walk, not the call.
                int walkSteps = JobDriver_EscortTransfer.StepsOfWalking(map, guard.Position, p.Position, guard);
                int walkTicks = walkSteps < 0 ? -1 : Mathf.CeilToInt(walkSteps * guard.TicksPerMoveDiagonal);
                if (walkTicks < 0 || WatchList.CheckEveryTicks + WatchList.CallEveryTicks + walkTicks + WatchList.LookTicks > WatchList.CheckMostTicks)
                {
                    WatchFail(check, $"premise: the guard's walk to the inmate ({walkSteps} steps, {walkTicks} ticks) does not fit the rule: {WatchList.CheckEveryTicks} + a beat of {WatchList.CallEveryTicks} + the walk + a look of "
                                     + $"{WatchList.LookTicks} within {WatchList.CheckMostTicks}");
                    return;
                }
                int start = ShNow, last = c.watchCheckTick;
                var checks = new List<int>();
                // (What the guard did between the checks, and their hour: the line says whether a wait was the beat and the walk, or something that took the guard away.)
                var guardJobs = new List<string>();
                string lastGuardJob = null;
                ShuttleTicks(() =>
                {
                    if (c.watchCheckTick != last) checks.Add(last = c.watchCheckTick);
                    string jobNow = $"{guard.CurJobDef?.defName ?? "none"} ({guard.timetable?.CurrentAssignment?.defName})";
                    if (jobNow != lastGuardJob && guardJobs.Count < 40) guardJobs.Add($"{ShNow - start}: {lastGuardJob = jobNow}");
                    return false;
                }, 4500);
                int worst = 0, prev = start;
                foreach (int t in checks)
                {
                    worst = Math.Max(worst, t - prev);
                    prev = t;
                }
                worst = Math.Max(worst, ShNow - prev);
                if (checks.Count < 2 || worst > WatchList.CheckMostTicks)
                    WatchFail(check, $"in {ShNow - start} ticks the checks came at [{string.Join(", ", checks.Select(t => t - start))}] (the longest wait {worst}, at most {WatchList.CheckMostTicks}); the guard is {guard.CurJobDef?.defName} at "
                                     + $"{guard.Position - s.origin}, the inmate at {p.Position - s.origin} doing {p.CurJobDef?.defName}, due {WatchList.CheckDue(p)}, {s.ReachDiagnostics(guard, p)}; the walk {walkSteps} steps ({walkTicks} ticks); "
                                     + $"the guard's jobs (tick: job (hour)) [{string.Join(", ", guardJobs)}]");
                // The guard at leisure: a long leisure job begun just after a check, outlasting the next check's last moment. The beat calls them off it in time (round 2's runs: one check, then the guard
                // meditated through the rest).
                // (The leisure must outlast the window: a wander or an idle moment ends inside it, and the think tree gives the guard the check without the beat. Round 3's w3d: the meditation ended as it began
                // and the guard wandered off. The unit is new and unheated, so its rooms are at the outdoor temperature (-10C that day), and vanilla's meditation fails at once in a room outside the pawn's safe
                // range. So the scene keeps its rooms warm through the window and has the guard rested and fed. Without Royalty the work hours end a meditation, so a staff break (2000 ticks, with none of those
                // conditions) is used instead, as it is when the meditation will not start.)
                FloatRange safeTemp = guard.SafeTemperatureRange();
                float warmTo = Mathf.Clamp(21f, safeTemp.min + 2f, safeTemp.max - 2f);
                bool KeepWarm()
                {
                    foreach (IntVec3 at in new CellRect(s.origin.x, s.origin.z, PsychScene.Width, PsychScene.Height))
                        if (at.GetRoom(map) is { UsesOutdoorTemperature: false } warmed) warmed.Temperature = warmTo;
                    return false;
                }
                // (One tick in the warm, so that no danger a region cached in the cold on this tick is read.)
                KeepWarm();
                ShuttleTicks(KeepWarm, 1);
                KeepWarm();
                c.watchCheckTick = ShNow;
                if (guard.needs?.rest != null) guard.needs.rest.CurLevel = 1f;
                if (guard.needs?.food != null) guard.needs.food.CurLevel = 1f;
                if (guard.needs?.joy != null) guard.needs.joy.CurLevel = 0.2f;
                string noMeditation = null;
                if (guard.psychicEntropy == null) noMeditation = "no psychic tracker (Royalty off), so the work hours would end it";
                else
                {
                    Job meditate = JobMaker.MakeJob(JobDefOf.Meditate, guard.Position);
                    // (As a meditation for psyfocus: the timetable's work hours do not end it. Vanilla reads the psychic tracker for those, which only Royalty gives.)
                    meditate.ignoreJoyTimeAssignment = true;
                    guard.jobs.StartJob(meditate, JobCondition.InterruptForced);
                    if (guard.CurJobDef != JobDefOf.Meditate)
                        noMeditation = $"it ended as it began ({guard.CurJobDef?.defName}): can meditate now {MeditationUtility.CanMeditateNow(guard)}, safe conditions {MeditationUtility.SafeEnvironmentalConditions(guard, guard.Position, map)}, "
                                       + WatchLeisureWhyNot(guard);
                }
                if (noMeditation != null)
                {
                    RPLog.Message($"[RimPrison:test] watch list: the guard at leisure takes a staff break, not a meditation - {noMeditation}");
                    guard.jobs.StartJob(JobMaker.MakeJob(RPStaffRoomDefOf.RP_StaffBreak, guard.Position), JobCondition.InterruptForced);
                }
                JobDef leisure = guard.CurJobDef;
                if (leisure != JobDefOf.Meditate && leisure != RPStaffRoomDefOf.RP_StaffBreak)
                    WatchFail(check, $"premise: the guard could not be put to leisure - no meditation ({noMeditation}), and no staff break either (the job is {leisure?.defName}; {WatchLeisureWhyNot(guard)})");
                else
                {
                    int sat = ShNow, checkedAt = c.watchCheckTick, leftAt = -1;
                    JobDef lastJob = null, leftFor = null;
                    bool leftDue = false;
                    ShuttleTicks(() =>
                    {
                        KeepWarm();
                        if (leftAt < 0 && guard.CurJobDef != leisure) (leftAt, leftFor, leftDue) = (ShNow - sat, guard.CurJobDef, WatchList.CheckDue(p));
                        if (c.watchCheckTick != checkedAt) return true;
                        if (guard.CurJobDef != RPWatchDefOf.RP_WatchCheck) lastJob = guard.CurJobDef;
                        return false;
                    }, WatchList.CheckMostTicks + 200);
                    // (The beat calls a guard only to a check that is due: leisure that ended before then ended by itself, and what came after proves nothing about the beat.)
                    if (leftAt >= 0 && !leftDue)
                        WatchFail(check, $"premise: the guard's {leisure.defName} ended {leftAt} ticks in, before the check was due ({WatchList.CheckEveryTicks}), for {leftFor?.defName ?? "no job"}; {WatchLeisureWhyNot(guard)}");
                    else if (c.watchCheckTick == checkedAt || c.watchCheckTick - sat > WatchList.CheckMostTicks)
                        WatchFail(check, $"the guard began {leisure.defName} just after a check, and {(c.watchCheckTick == checkedAt ? "no check came" : "the next check came")} in {ShNow - sat} ticks (at most {WatchList.CheckMostTicks}); "
                                         + $"the guard is {guard.CurJobDef?.defName} at {guard.Position - s.origin}, their last job other than a check {lastJob?.defName}, leisure rank {WatchList.LeisureRank(guard)}, due {WatchList.CheckDue(p)}");
                }
                // A crisis on watch: somebody reaches them before any harm.
                if (!WatchList.TryStartCrisis(p, c, forced: true))
                {
                    WatchFail(check, $"premise: no crisis started (mental {p.MentalStateDef?.defName}, downed {p.Downed})");
                    return;
                }
                int t0 = ShNow;
                ShuttleTicks(() => !WatchList.InCrisis(p), WatchList.HarmTicks + 600);
                string tail = "RP_Chr_CrisisInterrupted".Translate("", p.LabelShort).Resolve().Trim();
                if (WatchList.InCrisis(p) || HasCut(p) || p.health.hediffSet.BleedRateTotal > 0f || !r.NewEntries.Any(e => e.text.Trim().EndsWith(tail)))
                    WatchFail(check, $"after {ShNow - t0} ticks of a crisis on watch: still in it {WatchList.InCrisis(p)}, cut {HasCut(p)}, bleeding {p.health.hediffSet.BleedRateTotal:F2}, a line that somebody reached them "
                                     + $"{r.NewEntries.Any(e => e.text.Trim().EndsWith(tail))}; the guard is {guard.CurJobDef?.defName} at {guard.Position - s.origin}");
                if (c.crisisCount != 1) WatchFail(check, $"the crisis count is {c.crisisCount}");
            });
        }

        /// <summary>What a meditation or a staff break reads before it lets a guard sit (the watch ticks check's premise, said when it cannot be had).</summary>
        private static string WatchLeisureWhyNot(Pawn guard)
        {
            Room room = guard.GetRoom();
            FloatRange safe = guard.SafeTemperatureRange();
            return $"room {room?.ID} at {room?.Temperature:F0}C (outdoor temperature {room?.UsesOutdoorTemperature}), safe {safe.min:F0}..{safe.max:F0}C, danger {guard.Position.GetDangerFor(guard, guard.Map)}, "
                   + $"rest {guard.needs?.rest?.CurCategory}, starving {guard.needs?.food?.Starving}, bleeding {guard.health.hediffSet.BleedRateTotal:F2}, medical rest {HealthAIUtility.ShouldSeekMedicalRest(guard)}, "
                   + $"hour {guard.GetTimeAssignment()?.defName} (joy {guard.GetTimeAssignment()?.allowJoy}), awake {guard.Awake()}, drafted {guard.Drafted}, mental {guard.MentalStateDef?.defName}, alarm {GuardUtility.AlarmFor(guard)}, "
                   + $"the spot held by {guard.Map?.reservationManager.FirstRespectedReserver(guard.Position, guard)?.LabelShort ?? "nobody"}";
        }

        /// <summary>
        /// On watch with no guard to keep it (the real staff's warden work off, the one guard at home): in real ticks no check comes, the watch is overdue after <see cref="WatchList.CheckMostTicks"/>, and the hourly roll that
        /// a fresh watch keeps off starts a crisis (the roll is made a day on, the watch as old as the ticks made it, the clock put back).
        /// </summary>
        private static void CheckWatchUnkeptWatch(Map map)
        {
            Guarded("watch list: a watch no guard keeps is no watch (real ticks, no guard)", map, r =>
            {
                const string check = "unkept watch";
                WatchSwitches(r);
                WatchList.Pool = _ => false;
                PrisonGame game = PrisonGame.Instance;
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = InmateUtility.DefaultSecurity;
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                QuietTheRealStaff(map, r, "Warden");
                Pawn away = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                away.Name = new NameSingle("Zwg12");
                Pawn p = s.Inmate("Zwu1", medium, s.plain, s.CellQ);
                WatchReset(p);
                CompInmate c = p.Inmate();
                r.Add(() =>
                {
                    if (!p.Dead) p.mindState.mentalStateHandler.Reset();
                });
                int restoreTo = -1;
                r.Add(() =>
                {
                    if (restoreTo >= 0) Find.TickManager.DebugSetTicksGame(restoreTo);
                });
                WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
                WatchList.Flag(p, c, map, away, RiskFlagger.Guard);
                WatchList.Answer(p, c, WatchAnswer.Watch, map);
                // (The mood up while the ticks run, so that no vanilla break comes; the roll puts it back down.)
                p.needs.mood.CurLevel = 0.5f;
                bool RollsADayOn()
                {
                    int clock = ShNow, k = (WatchList.CrisisHours + 1) * WlHour;
                    (int risk, int low, int since, int checkedAt, int until, int crises) = (c.riskTick, c.lowMoodTick, c.measureTick, c.watchCheckTick, c.watchUntilTick, c.crisisCount);
                    float mood = p.needs.mood.CurLevel;
                    restoreTo = clock;
                    Find.TickManager.DebugSetTicksGame(clock + k);
                    // (The watch as old as it was; flagged, and very low, a day before.)
                    (c.riskTick, c.lowMoodTick, c.measureTick, c.watchUntilTick) = (clock, clock, since < 0 ? -1 : since + k, until + k);
                    if (checkedAt >= 0) c.watchCheckTick = checkedAt + k;
                    p.needs.mood.CurLevel = 0.05f;
                    if (!p.Awake()) RestUtility.WakeUp(p);
                    (WatchList.Pool, WatchList.CrisisRoll, WatchList.SettingOverride) = (x => x == p, true, true);
                    WatchList.Hourly(game, map);
                    bool started = WatchList.InCrisis(p);
                    (WatchList.Pool, WatchList.CrisisRoll) = (_ => false, null);
                    if (started) p.mindState.mentalStateHandler.Reset();
                    (c.riskTick, c.lowMoodTick, c.measureTick, c.watchCheckTick, c.watchUntilTick, c.crisisCount) = (risk, low, since, checkedAt, until, crises);
                    p.needs.mood.CurLevel = mood;
                    Find.TickManager.DebugSetTicksGame(clock);
                    restoreTo = -1;
                    return started;
                }
                bool fresh = RollsADayOn();
                int t0 = ShNow;
                ShuttleTicks(() => c.watchCheckTick >= 0, WatchList.CheckMostTicks + 120);
                if (c.watchCheckTick >= 0)
                {
                    RPLog.Message($"[RimPrison:test] watch list: somebody checked the watch with no guard on duty, {c.watchCheckTick - t0} ticks in: the unkept watch was not looked at");
                    return;
                }
                if (p.InMentalState)
                {
                    RPLog.Message($"[RimPrison:test] watch list: the inmate of the unkept watch is in {p.MentalStateDef?.defName}: the roll was not looked at");
                    return;
                }
                bool kept = WatchList.Watched(c, ShNow), rolls = RollsADayOn();
                if (fresh || kept || !rolls)
                    WatchFail(check, $"a watch {ShNow - t0} ticks old that nobody checked: a crisis while it was fresh {fresh}, still counted as kept {kept}, a crisis now {rolls} (only the last expected): {WatchList.Describe(c)}");
            });
        }

        /// <summary>"Nothing", a crisis and nobody to reach them (the real staff's warden and doctor work off for the check): the real cuts, blood loss, a death, exactly one inquiry in the neglect words, and the bitter family letter.</summary>
        private static void CheckWatchUnwatchedCrisisDeath(Map map)
        {
            Guarded("watch list: nothing done, a crisis nobody reaches - blood loss, a death, one neglect inquiry (real ticks)", map, r =>
            {
                const string check = "nobody came";
                WatchSwitches(r);
                WatchList.Pool = _ => false;
                PrisonGame g = PrisonGame.Instance;
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = InmateUtility.DefaultSecurity;
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                QuietTheRealStaff(map, r, "Warden");
                QuietTheRealStaff(map, r, "Doctor");
                var condolences0 = g.story.condolences.ToList();
                var faults0 = g.story.bitterDeaths.ToList();
                var incidents0 = s.pm.incidents.ToList();
                (int lastDeath0, int drama0) = (g.story.lastDeathTick, g.story.dramaToday);
                r.Add(() =>
                {
                    g.story.condolences = condolences0;
                    g.story.bitterDeaths = faults0;
                    s.pm.incidents.Clear();
                    s.pm.incidents.AddRange(incidents0);
                    (g.story.lastDeathTick, g.story.dramaToday) = (lastDeath0, drama0);
                });
                Pawn away = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                away.Name = new NameSingle("Zwg8");
                Pawn p = s.Inmate("Zwn1", medium, s.plain, s.CellQ);
                WatchReset(p);
                CompInmate c = p.Inmate();
                c.relatives.Add(new Relative { name = "Ines", kind = "RP_Kin_Mother" });
                c.familyCount = 1;
                WatchList.Notify_Trigger(p, RiskTrigger.FamilyDeath);
                ChoiceLetter_PrisonDecision letter = WatchList.Flag(p, c, map, away, RiskFlagger.Guard);
                PrisonDecisions.Choose(letter, 3);
                if (c.watchAnswer != WatchAnswer.Nothing) WatchFail(check, $"premise: \"nothing\" was not recorded: {WatchList.Describe(c)}");
                // (The mood up while the ticks run: the crisis is forced, and a vanilla break would get in its way.)
                p.needs.mood.CurLevel = 0.5f;
                if (WatchList.Reacher(p) is { } near) WatchFail(check, $"premise: {near.LabelShort} is already beside them");
                if (!WatchList.TryStartCrisis(p, c, forced: true))
                {
                    WatchFail(check, "premise: no crisis started");
                    return;
                }
                int t0 = ShNow;
                ShuttleTicks(() => !WatchList.InCrisis(p), WatchList.HarmTicks + 300);
                int cuts = p.health.hediffSet.hediffs.Count(h => h.def == HediffDefOf.Cut);
                if (WatchList.InCrisis(p) || cuts != WatchList.Cuts || p.health.hediffSet.BleedRateTotal <= 0f)
                {
                    WatchFail(check, $"after {ShNow - t0} ticks with nobody there: still in the crisis {WatchList.InCrisis(p)}, {cuts} cuts, bleeding {p.health.hediffSet.BleedRateTotal:F2}; reacher {WatchList.Reacher(p)?.LabelShort}");
                    return;
                }
                string harmedLabel = "RP_Watch_HarmedLabel".Translate(p.LabelShort).Resolve();
                if (!r.NewLetters.Any(l => l.Label.Resolve() == harmedLabel)) WatchFail(check, "no letter that they harmed themself");
                // The blood loss runs its course (brought near its end: the sixteen hours it takes are not ticked).
                Hediff loss = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss) ?? p.health.AddHediff(HediffDefOf.BloodLoss);
                loss.Severity = 0.97f;
                int t1 = ShNow;
                ShuttleTicks(() => p.Dead, 3000);
                if (!p.Dead)
                {
                    WatchFail(check, $"not dead {ShNow - t1} ticks after blood loss of 97% with {p.health.hediffSet.BleedRateTotal:F2} a day still bleeding (downed {p.Downed}, in bed {p.InBed()}, loss now {loss.Severity:F3})");
                    return;
                }
                Corpse body = p.Corpse;
                if (body != null)
                    r.Add(() =>
                    {
                        if (!body.Destroyed) body.Destroy();
                        if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    });
                List<ChoiceLetter_PrisonDecision> inquiries = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Inquiry")).ToList();
                string neglect = PsychPlain("RP_InquiryTextNeglect".Translate(p.LabelShort, "", Money.Format(DeathAftermath.InquiryFine)).Resolve());
                if (inquiries.Count != 1 || inquiries[0].note != p.LabelShort || WatchText(inquiries[0]) != neglect)
                    WatchFail(check, $"{inquiries.Count} inquiries ({string.Join(" | ", inquiries.Select(l => l.note + ": " + WatchText(l)))}), one in the neglect words expected (\"{neglect}\")");
                if (!CondolenceSystem.AtFault(p)) WatchFail(check, "the death is not the prison's fault for the family's letter");
            });
        }

        /// <summary>The harm itself: in the padded cell of their own bed nothing happens; elsewhere it is vanilla cuts on the arms, bleeding.</summary>
        private static void CheckWatchPaddedCrisis(Map map)
        {
            Guarded("watch list: a crisis in a padded cell harms nothing; elsewhere it is cuts that bleed", map, r =>
            {
                const string check = "padded crisis";
                WatchSwitches(r);
                WatchList.Pool = _ => false;
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                s.Project(true);
                Pawn a = s.Inmate("Zwc1", s.level, s.padded, s.CellP), b = s.Inmate("Zwc2", InmateUtility.DefaultSecurity, s.plain, s.CellQ);
                if (!PaddedCells.InOwnCell(a, out _))
                {
                    WatchFail(check, "premise: the inmate is not in the padded cell of their own bed");
                    return;
                }
                bool hurtA = WatchList.Harm(a), hurtB = WatchList.Harm(b);
                if (hurtA || HasCut(a)) WatchFail(check, "a crisis in a padded cell hurt them");
                List<Hediff> cuts = b.health.hediffSet.hediffs.Where(h => h.def == HediffDefOf.Cut).ToList();
                if (!hurtB || cuts.Count != WatchList.Cuts || b.health.hediffSet.BleedRateTotal <= 0f || cuts.Any(h => h.Part?.def != BodyPartDefOf.Arm && h.Part?.def != BodyPartDefOf.Leg))
                    WatchFail(check, $"in a plain cell: hurt {hurtB}, cuts [{string.Join(", ", cuts.Select(h => h.Part?.def.defName + " " + h.Severity.ToString("F0")))}], bleeding {b.health.hediffSet.BleedRateTotal:F2}");
            });
        }

        // ---- the setting -------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckWatchSettingOff(Map map)
        {
            Guarded("watch list: with self-harm crises off the flag, the letter and the watch still happen and no crisis starts", map, r =>
            {
                const string check = "setting";
                int now = WatchClock(r, 4 * WlDay);
                WatchSwitches(r);
                (WatchList.SettingOverride, WatchList.PsychOverride, WatchList.CrisisRoll) = (false, false, true);
                Pawn g = WatchGuard(map, r, "g9");
                Pawn p = WatchInmate(map, r, "s1");
                CompInmate c = p.Inmate();
                WatchList.Pool = x => x == p;
                WatchList.GuardPool = x => x == g;
                PrisonGame game = PrisonGame.Instance;
                WatchList.Notify_Trigger(p, RiskTrigger.GreenLight);
                c.lowMoodTick = now - 25 * WlHour;
                WatchList.Hourly(game, map);
                ChoiceLetter_PrisonDecision letter = WatchLetter(r, p);
                string crisisLine = "RP_Watch_CrisisRisk".Translate(WatchList.CrisisDailyChance.ToStringPercent()).Resolve();
                if (c.riskTick != now || letter == null) WatchFail(check, $"with the setting off nobody was flagged: {WatchList.Describe(c)}, letter {letter != null}");
                else
                {
                    if (WatchText(letter).Contains(PsychPlain(crisisLine))) WatchFail(check, "with the setting off the letter speaks of a crisis");
                    PrisonDecisions.Choose(letter, 3);
                }
                // A day more of very low mood, nothing done, the dice always up: no crisis.
                (c.riskTick, c.lowMoodTick, c.watchUntilTick) = (now - 25 * WlHour, now - 50 * WlHour, now + 2 * WlDay);
                for (int i = 0; i < 5; i++) WatchList.Hourly(game, map);
                if (WatchList.InCrisis(p) || c.crisisCount != 0) WatchFail(check, $"with the setting off a crisis started ({p.MentalStateDef?.defName}, {c.crisisCount})");
                if (WatchList.TryStartCrisis(p, c, forced: true)) WatchFail(check, "with the setting off a forced crisis started");
                // The same with the setting on: one starts (the check measures something).
                WatchList.SettingOverride = true;
                WatchList.Hourly(game, map);
                if (!WatchList.InCrisis(p) || c.crisisCount != 1) WatchFail(check, $"with the setting on no crisis started: {WatchList.Describe(c)}, mental {p.MentalStateDef?.defName}, due {WatchList.CrisisDue(p, c, Find.TickManager.TicksGame)}");
                // The setting turned off during the crisis: it ends at once, and nobody is hurt.
                else
                {
                    WatchList.SettingOverride = false;
                    p.MentalState.MentalStateTick(1);
                    if (WatchList.InCrisis(p) || HasCut(p)) WatchFail(check, $"the setting turned off during a crisis: still in it {WatchList.InCrisis(p)}, cut {HasCut(p)}");
                    if (WatchList.Harm(p) || HasCut(p)) WatchFail(check, "with the setting off the harm still comes");
                    WatchList.SettingOverride = true;
                }
                p.mindState.mentalStateHandler.Reset();
                // On watch (just begun, so kept): none, with the setting on.
                (c.watchAnswer, c.measureTick, c.watchCheckTick) = (WatchAnswer.Watch, Find.TickManager.TicksGame, -1);
                WatchList.Hourly(game, map);
                if (WatchList.InCrisis(p) || c.crisisCount != 1) WatchFail(check, "a crisis started on watch");
            });
        }

        // ---- the saveload plan ---------------------------------------------------------------------------------------------------------------------------------------

        private static int watchSeedId = -1;
        private static string watchSeedState;

        /// <summary>An inmate on the list mid-period (every field off its default), for the round trip.</summary>
        private static void SeedWatchListForReload(Map map)
        {
            watchSeedId = -1;
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber != psychSeedId && x.Inmate() is { activeFlag: true, riskTick: < 0 } ic && ic.security is { escortOnly: false });
            if (p == null)
            {
                reloadProblems.Add("saveload: no inmate to seed the watch list's fields on");
                return;
            }
            int now = Find.TickManager.TicksGame;
            int At(int back) => Math.Max(1, now - back);
            CompInmate c = p.Inmate();
            (c.riskTick, c.watchUntilTick, c.crisisCount, c.riskEvent, c.riskEventTick, c.riskCause) = (At(1000), now + 2 * WlDay, 2, RiskTrigger.DearJohn, At(2000), RiskTrigger.GreenLight);
            (c.lowMoodTick, c.watchAnswer, c.watchCheckTick, c.counselTick, c.riskEndTick, c.measureTick) = (At(3000), WatchAnswer.Counselling, At(400), At(500), At(9000), At(600));
            watchSeedId = p.thingIDNumber;
            RPLog.Message($"[RimPrison:test] saveload watch list: {p.LabelShort} has {WatchList.Describe(c)}");
        }

        /// <summary>The seeded fields as they are at the snapshot (nothing ticks between this and the save).</summary>
        private static void RecordWatchListForReload(Map map)
        {
            watchSeedState = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == watchSeedId)?.Inmate() is { } c ? WatchList.Describe(c) : null;
        }

        private static void CheckWatchListAfterReload(Map map)
        {
            if (watchSeedId < 0) return;
            int id = watchSeedId;
            watchSeedId = -1;
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);
            if (p?.Inmate() is not { } c)
            {
                reloadProblems.Add("saveload: the inmate with the watch list's fields did not survive the load");
                return;
            }
            if (WatchList.Describe(c) != watchSeedState) reloadProblems.Add($"saveload: the watch list's fields changed across the load ({watchSeedState} -> {WatchList.Describe(c)})");
            else RPLog.Message("[RimPrison:test] saveload watch list: intact");
            (c.riskTick, c.watchUntilTick, c.crisisCount, c.riskEvent, c.riskEventTick, c.riskCause) = (-1, -1, 0, RiskTrigger.None, -1, RiskTrigger.None);
            (c.lowMoodTick, c.watchAnswer, c.watchCheckTick, c.counselTick, c.riskEndTick, c.measureTick) = (-1, WatchAnswer.Pending, -1, -1, -1, -1);
        }
    }
}
