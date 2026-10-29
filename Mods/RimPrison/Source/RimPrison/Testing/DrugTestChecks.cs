using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Drug testing (gap analysis 6, item 4), in the events plan: the policy decides who is due and a reason tests once in two days; a user tests positive within the day and the record, the file, the referral and the parole odds
    /// follow; a clean inmate tests negative and a random test is a tension cause; a refusal is recorded; a positive on a ring's customer traces the dealer by the ring's own rule and line, and heats nobody; the kits are ordered
    /// when they run out and no kit is no test; the night doctor is never sent, nor a doctor off duty by the timetable, nor the only doctor on site, and two tests an hour are the most; and the round walked in game ticks
    /// (<c>DrugTestRoundChecks.cs</c>: a whole round, the reasons and the cap across an hour and a load, the ways a round is cut short). Each check works in the ring checks' quiet yard (<see cref="RingScene"/>: the real
    /// inmates are set aside), and puts the policy, the counts, the due list, the rounds under way and the supply orders back.
    /// </summary>
    public static partial class AutoTest
    {
        private static void DtFail(string what) => problems0.Add("drug testing: " + what);

        /// <summary>The parole term lasts five days: a failed test just past it counts for nothing.</summary>
        private const int ParoleWindowDaysPlusOne = DrugTesting.ParoleWindowTicks / GenDate.TicksPerDay + 1;

        /// <summary>
        /// The policy, the day's counts (with the rounds under way and the hour's cap), the due list and what a check decides (doctors, hour, the refusal roll) as they were, and the supply orders (the real ones are set
        /// aside, the check works on its own list, and what the check ordered is not left among them), put back by the rollback; the check starts with a clean log.
        /// </summary>
        private static void DrugTestSetup(PrisonGame g, Rollback r)
        {
            (DrugTestPolicy policy0, int perDay0, DrugTestLog log0) = (g.drugTests, g.drugTestsPerDay, g.drugTestLog);
            DrugTesting.StateForTests state0 = DrugTesting.SnapshotForTests();
            List<SupplyOrder> real0 = g.supply.pending;
            List<SupplyOrder> mine = real0.Where(o => o.thing != DrugTesting.Kit).ToList();
            r.Add(() =>
            {
                (g.drugTests, g.drugTestsPerDay, g.drugTestLog) = (policy0, perDay0, log0);
                // (The real list is back, less the real orders that left the check's list meanwhile - loaded on a shuttle, cancelled and refunded: they are gone, not to be delivered or refunded twice - and with an order of
                // another kind that arrived on it, which is kept; the check's own kit orders are not.)
                var left = new HashSet<SupplyOrder>(mine.Where(o => !g.supply.pending.Contains(o)));
                real0.RemoveAll(left.Contains);
                real0.AddRange(g.supply.pending.Where(o => o.thing != DrugTesting.Kit && !real0.Contains(o)));
                g.supply.pending = real0;
                DrugTesting.RestoreForTests(state0);
            });
            g.supply.pending = mine;
            (g.drugTests, g.drugTestsPerDay, g.drugTestLog) = (DrugTestPolicy.Targeted, DrugTesting.DefaultPerDay, new DrugTestLog());
            DrugTesting.ClearForTests();
            // (The cap's hour is held: a check that runs ticks is not caught by the real hour turning.)
            DrugTesting.HourKeyForTests = Find.TickManager.TicksGame / GenDate.TicksPerHour;
        }

        /// <summary>A doctor on duty round the clock: work in every hour of the timetable, rested, on the day shift.</summary>
        private static void DtWorkAllDay(Pawn doctor)
        {
            if (doctor.timetable != null)
                for (int hour = 0; hour < 24; hour++) doctor.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
            if (doctor.needs?.rest != null) doctor.needs.rest.CurLevel = 1f;
            if (doctor.GetComp<CompStaff>() is { } sc) sc.nightShift = false;
        }

        private static void CheckDrugTest(Map map)
        {
            if (DrugTesting.Kit == null || RPDrugTestDefOf.RP_DrugUse == null)
            {
                DtFail("the kit or the misconducts are not defined");
                return;
            }
            CheckDrugTestPolicyAndReasons(map);
            CheckDrugTestPositiveFollows(map);
            CheckDrugTestCleanAndRefused(map);
            CheckDrugTestRingCustomer(map);
            CheckDrugTestFileLines(map);
            CheckDrugTestKitsAndDoctors(map);
            CheckDrugTestRound(map);
            CheckDrugTestKitsNotHauled(map);
            CheckDrugTestRoundAcrossAnHourAndALoad(map);
            CheckDrugTestCapAcrossALoad(map);
            CheckDrugTestDoctorsHeldThroughThePass(map);
            CheckDrugTestRoundCutShort(map);
        }

        // ---- who is due ---------------------------------------------------------------------------------------------------------------------

        private static void CheckDrugTestPolicyAndReasons(Map map)
        {
            Guarded("drug testing: the policy decides who is due, and a reason tests once in two days", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DrugTestSetup(g, r);
                var s = new RingScene(map, r);
                int now = Find.TickManager.TicksGame, gap = DrugTesting.ReasonGapTicks;
                Pawn quiet = s.Inmate("Zdt1a"), reasoned = s.Inmate("Zdt1b"), informer = s.Inmate("Zdt1c"), stale = s.Inmate("Zdt1d");
                CompInmate rc = reasoned.Inmate();
                // All four reasons: a find (a seen contraband record), a tip, an overdose survived, a hearing within the two days before it.
                rc.records.Add(new MisconductRecord { def = RPContrabandDefOf.RP_ContrabandPossession, tick = now - 50, seen = true });
                rc.informedBy = informer;
                rc.lastOverdoseTick = now - 100;
                // (Half of a three-day sentence is a day and a half: the first hearing is inside the two days.)
                (rc.sentenceDays, rc.admittedTick, rc.lastHearingTick) = (3, now, DrugTesting.Never);
                int all = DrugTesting.ReasonFind | DrugTesting.ReasonTip | DrugTesting.ReasonOverdose | DrugTesting.ReasonHearing;
                // A find three days ago is no reason now.
                stale.Inmate().records.Add(new MisconductRecord { def = RPContrabandDefOf.RP_ContrabandPossession, tick = now - 3 * GenDate.TicksPerDay, seen = true });
                // (A life sentence has no hearing coming: the others have no reason but the one given, and a generated sentence of a few days has its hearing inside the week.)
                quiet.Inmate().sentenceDays = informer.Inmate().sentenceDays = stale.Inmate().sentenceDays = -1;

                g.drugTests = DrugTestPolicy.Off;
                DrugTesting.Hourly(map, g);
                if (DrugTesting.DueCount != 0 || DrugTesting.IsDue(reasoned, out _)) DtFail($"with the policy off {DrugTesting.DueCount} inmates are due, among them the one with four reasons ({DrugTesting.IsDue(reasoned, out _)})");
                g.drugTests = DrugTestPolicy.Targeted;
                DrugTesting.Hourly(map, g);
                if (!DrugTesting.IsDue(reasoned, out int mask) || mask != all) DtFail($"targeted: the inmate with a find, a tip, an overdose and a hearing coming is due for {mask} (expected {all})");
                if (DrugTesting.IsDue(quiet, out _) || DrugTesting.IsDue(informer, out _) || DrugTesting.IsDue(stale, out _))
                    DtFail($"targeted: somebody with no reason (or only a find from three days ago) is due (quiet {DrugTesting.IsDue(quiet, out _)}, informer {DrugTesting.IsDue(informer, out _)}, stale {DrugTesting.IsDue(stale, out _)})");

                // Each reason tests at most once in two days: after a test none of the four is due, and as each ages out of the gap it is due again alone.
                DrugTestResult done = DrugTesting.Conduct(null, reasoned, refuse: false);
                if (done is not (DrugTestResult.Negative or DrugTestResult.Positive)) DtFail($"the test of the inmate with four reasons came out {done}");
                if (DrugTesting.DueReasons(g, reasoned, rc, now) != 0) DtFail($"after a test the inmate is still due for {DrugTesting.DueReasons(g, reasoned, rc, now)}");
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(reasoned, out _)) DtFail("an inmate tested a moment ago is on the next hour's due list");
                for (int i = 0; i < DrugTesting.TargetedReasons; i++)
                {
                    rc.testedFor[i] = now - gap - 1;
                    int due = DrugTesting.DueReasons(g, reasoned, rc, now);
                    if (due != 1 << i) DtFail($"reason {i} tested more than two days ago is due as {due} (expected {1 << i})");
                    rc.testedFor[i] = now;
                }

                // A hearing is a reason in the two days before it, once for each hearing (not "the week before": with a hearing every four days that made every eligible inmate due every two days).
                Pawn far = s.Inmate("Zdt1e"), overdue = s.Inmate("Zdt1f"), heard = s.Inmate("Zdt1g");
                CompInmate fc = far.Inmate(), oc = overdue.Inmate(), hc = heard.Inmate();
                // (The events plan runs in the first days of a game: ten days served by an admission ten days back would be a negative tick, which counts as no time served. The clock is moved on instead, and put back after.)
                int clock0 = now;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + 10 * GenDate.TicksPerDay);
                now = Find.TickManager.TicksGame;
                (fc.sentenceDays, fc.admittedTick, fc.lastHearingTick) = (14, now, DrugTesting.Never);
                (oc.sentenceDays, oc.admittedTick, oc.lastHearingTick) = (14, clock0, DrugTesting.Never);
                (hc.sentenceDays, hc.admittedTick, hc.lastHearingTick) = (14, clock0, now);
                if ((DrugTesting.ActiveReasons(g, far, fc, now) & DrugTesting.ReasonHearing) != 0) DtFail("a hearing seven days away is a reason (it is the two days before it)");
                if ((DrugTesting.ActiveReasons(g, heard, hc, now) & DrugTesting.ReasonHearing) != 0) DtFail("an inmate heard just now, whose next hearing is four days off, has a hearing reason");
                if ((DrugTesting.ActiveReasons(g, overdue, oc, now) & DrugTesting.ReasonHearing) == 0) DtFail("a hearing that is due and not yet held is no reason");
                foreach ((long ago, bool expectDue, string why) in new[] { (-1L, true, "never tested"), (6L * GenDate.TicksPerDay, true, "tested before the two days that lead to it"), (5L * GenDate.TicksPerDay / 2, false, "tested for this hearing two and a half days ago (past the two-day gap, still for this hearing)") })
                {
                    oc.testedFor[3] = ago < 0 ? DrugTesting.Never : now - (int)ago;
                    bool due = (DrugTesting.DueReasons(g, overdue, oc, now) & DrugTesting.ReasonHearing) != 0;
                    if (due != expectDue) DtFail($"the hearing reason of an inmate {why} is {(due ? "due" : "not due")} (expected {(expectDue ? "due" : "not due")})");
                }
                Find.TickManager.DebugSetTicksGame(clock0);
                now = clock0;

                // Random: only in the third mode, only for those not tested in the last day, as many as the day still needs (the last hour of the round gives all of them).
                (rc.lastTestedTick, stale.Inmate().lastTestedTick) = (now, now);
                DrugTesting.HourForTests = DrugTesting.RoundToHour - 1;
                g.drugTestsPerDay = DrugTesting.MaxPerDay;
                g.drugTests = DrugTestPolicy.Targeted;
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(quiet, out _)) DtFail("targeted only: an inmate with no reason is due");
                g.drugTests = DrugTestPolicy.TargetedAndRandom;
                DrugTesting.Hourly(map, g);
                if (!DrugTesting.IsDue(quiet, out int pick) || pick != DrugTesting.ReasonRandom || !DrugTesting.IsDue(informer, out _)) DtFail($"random: with six a day owed and one hour left, the untested are not all picked (quiet {pick})");
                if (DrugTesting.IsDue(stale, out _)) DtFail("random: an inmate tested today is picked again");
                g.drugTestLog.randoms = g.drugTestsPerDay;
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(quiet, out _)) DtFail("random: an inmate is picked after the day's random tests are done");
                DrugTesting.HourForTests = DrugTesting.RoundFromHour - 1;
                g.drugTestLog.randoms = 0;
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(quiet, out _)) DtFail("random: an inmate is picked before the doctors' day shift");
            });
        }

        // ---- a positive ---------------------------------------------------------------------------------------------------------------------

        private static void CheckDrugTestPositiveFollows(Map map)
        {
            Guarded("drug testing: a user tests positive within the day, and the record, the file and the referral follow", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DrugTestSetup(g, r);
                var s = new RingScene(map, r);
                int now = Find.TickManager.TicksGame;
                Pawn user = s.Inmate("Zdt2a"), addict = s.Inmate("Zdt2b"), lapsed = s.Inmate("Zdt2c");
                CompInmate uc = user.Inmate(), ac = addict.Inmate(), lc = lapsed.Inmate();
                (uc.lastDrugUseTick, uc.lastOverdoseTick) = (now - GenDate.TicksPerDay, now - 10);
                (lc.lastDrugUseTick, lc.lastOverdoseTick) = (now - 3 * GenDate.TicksPerDay, now - 10);
                addict.health.AddHediff(ChemicalDefOf.Alcohol.addictionHediff).Severity = 0.5f;
                ac.lastOverdoseTick = now - 10;
                int tests0 = g.stats.drugTests, positives0 = g.stats.drugPositives;
                (int punishments0, int light0) = (g.stats.punishments, g.stats.lightSanctions);
                int hole0 = ac.pendingPunishmentHours;
                var confined0 = ac.forcedActivity;
                // The default sanctions: for a drug use the lightest that is more than a word on the record, confinement to the cell (four hours); for a refusal four hours in the hole.
                if (g.punishments.KindFor(RPDrugTestDefOf.RP_DrugUse) != Sanction.CellConfinement || g.punishments.HoursFor(RPDrugTestDefOf.RP_DrugUse) != 4) DtFail($"the default sanction of a drug use is {g.punishments.KindFor(RPDrugTestDefOf.RP_DrugUse)} for {g.punishments.HoursFor(RPDrugTestDefOf.RP_DrugUse)} hours (cell confinement, 4)");
                if (g.punishments.KindFor(RPDrugTestDefOf.RP_RefusedTest) != Sanction.Solitary || g.punishments.HoursFor(RPDrugTestDefOf.RP_RefusedTest) != 4) DtFail($"the default sanction of a refusal is {g.punishments.KindFor(RPDrugTestDefOf.RP_RefusedTest)} for {g.punishments.HoursFor(RPDrugTestDefOf.RP_RefusedTest)} hours (solitary, 4)");
                // The day's hours: the pass makes the list, a doctor takes whoever is on it (the round itself is walked in the last check).
                for (int hour = 0; hour < 24 && (uc.lastTestResult == DrugTestResult.None || ac.lastTestResult == DrugTestResult.None || lc.lastTestResult == DrugTestResult.None); hour++)
                {
                    DrugTesting.Hourly(map, g);
                    foreach (Pawn p in new[] { user, addict, lapsed })
                        if (DrugTesting.IsDue(p, out _))
                        {
                            // (The user's punishment is looked at on its own: the addict's must not have changed the counts of one.)
                            if (p == user) (punishments0, light0) = (g.stats.punishments, g.stats.lightSanctions);
                            DrugTesting.Conduct(null, p, refuse: false);
                        }
                }
                if (uc.lastTestResult != DrugTestResult.Positive) DtFail($"a user (a drug taken a day ago) tested {uc.lastTestResult} in the policy's day");
                if (ac.lastTestResult != DrugTestResult.Dependence) DtFail($"an addict who used no drug lately tested {ac.lastTestResult} (positive for dependence expected)");
                // Use is punished, dependence is treated: the addict has the file line, the referral and the parole term, but no misconduct and no punishment.
                if (ac.records.Any(x => x.def == RPDrugTestDefOf.RP_DrugUse || x.def == RPDrugTestDefOf.RP_RefusedTest)) DtFail("a positive for dependence put a misconduct on the record");
                if (ac.pendingPunishmentHours != hole0 || ac.forcedActivity != confined0) DtFail($"a positive for dependence was punished (hole {hole0} -> {ac.pendingPunishmentHours}, confinement {confined0?.defName} -> {ac.forcedActivity?.defName})");
                if (!DrugTesting.FailedRecently(ac) || ac.lastFailedTestTick == DrugTesting.Never) DtFail("a positive for dependence does not count against them with the parole board");
                if (uc.pendingPunishmentHours <= 0 && uc.forcedActivity == null) DtFail("a positive for drug use was not punished (no cell confinement, no hole)");
                if (g.stats.punishments <= punishments0 && g.stats.lightSanctions <= light0 && uc.pendingPunishmentHours <= 0) DtFail("the sanction of a drug use was not counted");
                if (lc.lastTestResult != DrugTestResult.Negative) DtFail($"somebody who last used a drug three days ago and has no addiction tested {lc.lastTestResult} (the window is two days)");
                if (g.stats.drugTests - tests0 != 3 || g.stats.drugPositives - positives0 != 2 || g.drugTestLog.tests != 3 || g.drugTestLog.positives != 2)
                    DtFail($"the counters: tests {g.stats.drugTests - tests0} (3), positives {g.stats.drugPositives - positives0} (2), today {g.drugTestLog.tests}/{g.drugTestLog.positives}");
                // The record: a seen drug use, once.
                if (uc.records.Count(x => x.def == RPDrugTestDefOf.RP_DrugUse && x.seen) != 1) DtFail("a positive left no seen drug use on the record, or more than one");
                if (lc.records.Any(x => x.def == RPDrugTestDefOf.RP_DrugUse)) DtFail("a negative test left a drug use on the record");
                // The referral.
                ProgramDef rehab = RPProgramDefOf.RP_SubstanceAbuse;
                if (rehab != null && (uc.referral != rehab || ac.referral != rehab)) DtFail($"a positive did not refer to substance abuse treatment (user {uc.referral?.defName}, addict {ac.referral?.defName})");
                if (lc.referral != null) DtFail("a negative test referred somebody to treatment");
                // The file, with the addiction a positive found.
                string line = DrugTesting.FileLine(user, uc, now), addictLine = DrugTesting.FileLine(addict, ac, now);
                if (line == null || !line.Contains("RP_DrugTestResult_Positive".Translate().Resolve())) DtFail($"the file's line for a positive reads \"{line}\"");
                if (addictLine == null || !addictLine.Contains(ChemicalDefOf.Alcohol.addictionHediff.label)) DtFail($"the file does not name the addiction a positive found (\"{addictLine}\")");
                if (DrugTesting.FileLine(addict, ac, now) is not { } dependenceLine || !dependenceLine.Contains("RP_DrugTestResult_Dependence".Translate().Resolve())) DtFail("the file's line for a dependence does not say so");
                if (DrugTesting.FileLine(lapsed, lc, now) is not { } negativeLine || !negativeLine.Contains("RP_DrugTestResult_Negative".Translate().Resolve())) DtFail("the file has no line for a negative test");
                if (!ITab_InmateFile.StatusLines(user, uc, now).Any(l => l.Item1.Contains("RP_DrugTestResult_Positive".Translate().Resolve()))) DtFail("the inmate file's status lines do not show the test");
                // Not again for the same reason within two days.
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(user, out _)) DtFail("an inmate tested for an overdose is due for it again within two days");
                // The parole board counts a positive or a refusal in the last five days, and a negative after it does not clear it (what a goal measures is left out, as the incidents are).
                (uc.lastTestedTick, uc.lastTestResult, uc.lastFailedTestTick) = (now, DrugTestResult.Positive, now - GenDate.TicksPerDay);
                float failed = ParoleSystem.ParoleChance(uc), standing = ParoleSystem.ParoleChance(uc, recentIncidents: false);
                (uc.lastTestResult, uc.lastTestedTick) = (DrugTestResult.Negative, now);
                float failedThenNegative = ParoleSystem.ParoleChance(uc);
                uc.lastFailedTestTick = now - ParoleWindowDaysPlusOne * GenDate.TicksPerDay;
                float clean = ParoleSystem.ParoleChance(uc), cleanStanding = ParoleSystem.ParoleChance(uc, recentIncidents: false);
                uc.lastFailedTestTick = DrugTesting.Never;
                if (!(failed < clean - 0.05f)) DtFail($"a positive test a day ago does not lower the parole odds ({failed:P0} against {clean:P0} with none in five days)");
                if (Math.Abs(failedThenNegative - failed) > 0.001f) DtFail($"a negative test after the positive cleared it with the board ({failed:P0} -> {failedThenNegative:P0}): the term holds for the last positive or refusal in five days");
                if (Math.Abs(standing - cleanStanding) > 0.001f) DtFail($"what a goal measures changed with the test ({standing:P0} against {cleanStanding:P0})");
                // Every place a drug is taken sets the last use: the Ingest of one in the pocket (hooch and the garden's joints are drugs of the same kind), and not a cigarette.
                // (A generated inmate can have a low Consciousness of their own: the joint's high takes 0.3 off it, and one under 0.3 is downed and cannot take up the next dose - s8b's "could not take up RP_Hooch" in the
                // vanilla error "Dead/downed pawn tried to start carry". The highs are taken off after each dose, and a smoker who is down to begin with is made again.)
                Pawn smoker = s.Inmate("Zdt2d");
                for (int again = 0; smoker.Downed && again < 5; again++) smoker = s.Inmate("Zdt2d");
                if (smoker.Downed) DtFail($"set-up: the inmate is down before taking anything (Consciousness {smoker.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness):F2}, Moving {smoker.health.capacities.GetLevel(PawnCapacityDefOf.Moving):F2})");
                CompInmate sc = smoker.Inmate();
                Thing cig = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RP_Cigarettes"));
                if (smoker.carryTracker.TryStartCarry(cig, 1) != 1) DtFail("set-up: the inmate could not take up a cigarette");
                cig.Ingested(smoker, 1f);
                if (sc.lastDrugUseTick != DrugTesting.Never) DtFail("smoking a commissary cigarette set a last drug use");
                foreach (ThingDef drug in new[] { RPContrabandDefOf.SmokeleafJoint, RPContrabandDefOf.RP_Hooch })
                {
                    sc.lastDrugUseTick = DrugTesting.Never;
                    Thing dose = ThingMaker.MakeThing(drug);
                    smoker.carryTracker.innerContainer.ClearAndDestroyContents();
                    if (smoker.carryTracker.TryStartCarry(dose, 1) != 1) DtFail($"set-up: the inmate could not take up {drug.defName} (down {smoker.Downed}, Consciousness {smoker.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness):F2}, hediffs {string.Join(", ", smoker.health.hediffSet.hediffs.Select(h => h.def.defName))})");
                    dose.Ingested(smoker, 1f);
                    foreach (Hediff high in smoker.health.hediffSet.hediffs.Where(h => h is Hediff_High).ToList()) smoker.health.RemoveHediff(high);
                    if (sc.lastDrugUseTick < now) DtFail($"taking {drug.defName} did not set a last drug use ({sc.lastDrugUseTick})");
                }
                smoker.carryTracker.innerContainer.ClearAndDestroyContents();
            });
        }

        // ---- a negative, a refusal ----------------------------------------------------------------------------------------------------------

        private static void CheckDrugTestCleanAndRefused(Map map)
        {
            Guarded("drug testing: a clean inmate tests negative and is searched again, a refusal is recorded", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DrugTestSetup(g, r);
                var s = new RingScene(map, r);
                PrisonMap pm = PrisonMap.Of(map);
                int now = Find.TickManager.TicksGame;
                Pawn clean = s.Inmate("Zdt3a"), refuser = s.Inmate("Zdt3b"), user = s.Inmate("Zdt3c");
                CompInmate cc = clean.Inmate(), rc = refuser.Inmate(), uc = user.Inmate();
                uc.lastDrugUseTick = now - 100;
                // A random test of a clean inmate.
                DrugTesting.SetDueForTests(g, (clean, DrugTesting.ReasonRandom));
                DrugTestResult result = DrugTesting.Conduct(null, clean, refuse: false);
                if (result != DrugTestResult.Negative || cc.lastTestResult != DrugTestResult.Negative || !cc.lastTestRandom) DtFail($"a clean inmate picked at random tested {result} (file {cc.lastTestResult}, random {cc.lastTestRandom})");
                if (cc.records.Any(x => x.def == RPDrugTestDefOf.RP_DrugUse || x.def == RPDrugTestDefOf.RP_RefusedTest) || cc.referral != null) DtFail("a negative test wrote a record or a referral");
                if (g.drugTestLog.randoms != 1) DtFail($"a random test did not count against the day's random tests ({g.drugTestLog.randoms})");
                // The tension cause: the same hour with and without the mark.
                if (!DrugTesting.SearchedAgain(cc)) DtFail("an inmate just tested at random is not searched again");
                // (On the inmate tested, not on the wing: the others are not searched again for it.)
                if (DrugTesting.SearchedAgain(uc) || DrugTesting.SearchedAgain(rc)) DtFail("a random test of one inmate is a tension cause for the others");
                cc.lastTestRandom = false;
                (cc.tension, cc.suppressed) = (20f, 0f);
                TensionUtility.HourlyUpdate(clean, cc, pm);
                float without = cc.tension;
                cc.lastTestRandom = true;
                (cc.tension, cc.suppressed) = (20f, 0f);
                TensionUtility.HourlyUpdate(clean, cc, pm);
                if (!(cc.tension > without + 0.1f)) DtFail($"a random test is no tension cause (tension {without:F2} without it, {cc.tension:F2} with it)");
                if (!"RP_Cause_SearchedAgain".CanTranslate()) DtFail("the tension cause has no words");
                cc.lastTestedTick = now - (DrugTesting.SearchedHours + 1) * GenDate.TicksPerHour;
                if (DrugTesting.SearchedAgain(cc)) DtFail("being searched again lasts longer than the day");

                // A refusal: a seen misconduct of its own, no test and no kit counted, no positive, and a count against them with the board.
                int tests0 = g.stats.drugTests, positives0 = g.stats.drugPositives;
                (rc.lastTestedTick, rc.lastOverdoseTick) = (DrugTesting.Never, now - 10);
                DrugTesting.Hourly(map, g);
                DrugTestResult refused = DrugTesting.Conduct(null, refuser, refuse: true);
                if (refused != DrugTestResult.Refused || rc.lastTestResult != DrugTestResult.Refused) DtFail($"a refusal came out {refused} (file {rc.lastTestResult})");
                if (rc.records.Count(x => x.def == RPDrugTestDefOf.RP_RefusedTest && x.seen) != 1) DtFail("a refusal is not a seen misconduct on the record");
                if (g.stats.drugTests != tests0 || g.stats.drugPositives != positives0) DtFail("a refusal was counted as a test or a positive");
                if (!DrugTesting.FailedRecently(rc)) DtFail("a refusal does not count as a positive for the parole board");
                if (rc.pendingPunishmentHours <= 0) DtFail("a refusal was not punished by the default sanction (the hole)");
                float refusedOdds = ParoleSystem.ParoleChance(rc);
                int failedAt = rc.lastFailedTestTick;
                rc.lastFailedTestTick = DrugTesting.Never;
                if (!(refusedOdds < ParoleSystem.ParoleChance(rc) - 0.05f)) DtFail("a refusal does not lower the parole odds");
                rc.lastFailedTestTick = failedAt;
                if (DrugTesting.FileLine(refuser, rc, now) is not { } refusedLine || !refusedLine.Contains("RP_DrugTestResult_Refused".Translate().Resolve())) DtFail("the file has no line for a refusal");
                // Likelier with a drug used lately and with tension, less so when beaten down.
                (cc.lastDrugUseTick, cc.tension, cc.suppressed) = (DrugTesting.Never, 10f, 0f);
                float calm = DrugTesting.RefuseChance(clean, cc, now);
                (cc.lastDrugUseTick, cc.tension) = (now - 100, 10f);
                float used = DrugTesting.RefuseChance(clean, cc, now);
                (cc.lastDrugUseTick, cc.tension) = (DrugTesting.Never, 90f);
                float tense = DrugTesting.RefuseChance(clean, cc, now);
                cc.suppressed = 100f;
                float beaten = DrugTesting.RefuseChance(clean, cc, now);
                if (!(used > calm) || !(tense > calm) || !(beaten < tense)) DtFail($"the refusal chance: calm {calm:P0}, used {used:P0}, tense {tense:P0}, tense and beaten down {beaten:P0}");
            });
        }

        // ---- a customer of a ring -----------------------------------------------------------------------------------------------------------

        private static void CheckDrugTestRingCustomer(Map map)
        {
            Guarded("drug testing: a positive on a ring's customer may trace the dealer by the ring's own rule", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DrugTestSetup(g, r);
                var s = new RingScene(map, r);
                int now = Find.TickManager.TicksGame;
                Pawn dealer = s.Inmate("Zdt4dealer", dealer: true), customer = s.Inmate("Zdt4customer");
                CompInmate cc = customer.Inmate();
                cc.lastDealerId = dealer.thingIDNumber;
                StoryArc ring = s.Form(dealer);
                RingLink link = s.Link(ring, RingRole.Dealer);
                if (link == null)
                {
                    DtFail("the check's set-up failed: the ring has no dealer link");
                    return;
                }
                // The customer tests positive again and again: the dealer is found with the ring's own chance, and the ring is heated only when the trace held (as for a find).
                int unlucky = 0, tries = 0;
                for (; tries < 60 && !link.known; tries++)
                {
                    ring.ring.heat = 0f;
                    cc.lastDrugUseTick = now;
                    DrugTesting.SetDueForTests(g, (customer, DrugTesting.ReasonRandom));
                    if (DrugTesting.Conduct(null, customer, refuse: false) != DrugTestResult.Positive)
                    {
                        DtFail("the dealer's customer, who used a drug a moment ago, did not test positive");
                        return;
                    }
                    if (!link.known && ring.ring.heat != 0f) unlucky++;
                }
                if (!link.known) DtFail($"{tries} positive tests on the dealer's customer never traced the dealer (the ring's own chance is {RingSystem.TraceDealerChance:P0})");
                if (unlucky > 0) DtFail($"a positive that traced nothing heated the ring {unlucky} times (a test is no find on the ring)");
                if (!s.Told().Any(t => t.Contains("RP_RingHow_trace".Translate().Resolve()))) DtFail("the dealer was found without the ring's own line (\"traced from what was found\")");
                // A positive on a member of the ring is not a find on it: no heat from a test.
                Pawn courier = s.Inmate("Zdt4courier");
                courier.Inmate().lastDealerId = dealer.thingIDNumber;
                RingLink member = s.Recruit(ring);
                if (member == null) RPLog.Message("[RimPrison:test] drug testing: no courier could be recruited for the ring, the member's test was not checked");
                else
                {
                    Pawn who = map.mapPawns.AllPawnsSpawned.First(p => p.thingIDNumber == member.pawnId);
                    ring.ring.heat = 0f;
                    who.Inmate().lastDrugUseTick = now;
                    DrugTesting.SetDueForTests(g, (who, DrugTesting.ReasonRandom));
                    if (DrugTesting.Conduct(null, who, refuse: false) != DrugTestResult.Positive) DtFail("a courier who used a drug did not test positive");
                    if (ring.ring.heat != 0f) DtFail($"a positive on a member of the ring heated it ({ring.ring.heat})");
                }
            });
        }

        // ---- the file's lines ---------------------------------------------------------------------------------------------------------------

        /// <summary>The file's status lines: the test line is its own, and the detox/withdrawal pair is as it was (supervised detox, else withdrawal), whether or not they were ever tested.</summary>
        private static void CheckDrugTestFileLines(Map map)
        {
            Guarded("drug testing: the inmate file shows the test on its own line, and the withdrawal or detox line as before", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DrugTestSetup(g, r);
                var s = new RingScene(map, r);
                int now = Find.TickManager.TicksGame;
                Pawn tested = s.Inmate("Zdt7a"), untestedWithdrawing = s.Inmate("Zdt7b"), untestedSupervised = s.Inmate("Zdt7c");
                CompInmate tc = tested.Inmate();
                (tc.lastTestResult, tc.lastTestedTick) = (DrugTestResult.Negative, now - 30 * GenDate.TicksPerDay);
                HediffDef addiction = ChemicalDefOf.Alcohol.addictionHediff;
                foreach (Pawn p in new[] { tested, untestedWithdrawing, untestedSupervised })
                {
                    p.health.AddHediff(addiction).Severity = 0.5f;
                    p.needs.AddOrRemoveNeedsAsAppropriate();
                    ((Need_Chemical)p.needs.TryGetNeed(addiction.chemicalNeed)).CurLevel = 0f;
                    if (!Detox.InWithdrawal(p)) DtFail($"set-up: {p.LabelShort} is not in withdrawal with their craving at nothing");
                }
                untestedSupervised.health.AddHediff(RPDetoxDefOf.RP_SupervisedDetox);
                string withdrawal = "RP_FileStatusWithdrawal".Translate().Resolve(), detox = "RP_FileStatusDetox".Translate().Resolve(), testLine = "RP_DrugTestResult_Negative".Translate().Resolve();
                bool Shows(Pawn p, string text) => ITab_InmateFile.StatusLines(p, p.Inmate(), now).Any(l => l.Item1.Contains(text));
                if (!Shows(tested, withdrawal)) DtFail("an inmate tested once, long ago, who is in withdrawal has no withdrawal line on the file");
                if (!Shows(tested, testLine)) DtFail("the test line is not on the file of an inmate in withdrawal");
                if (!Shows(untestedWithdrawing, withdrawal) || Shows(untestedWithdrawing, testLine)) DtFail("an untested inmate in withdrawal: the withdrawal line is missing or a test line is shown");
                if (!Shows(untestedSupervised, detox) || Shows(untestedSupervised, withdrawal)) DtFail($"an untested inmate under supervised detox shows the detox line {Shows(untestedSupervised, detox)} and the withdrawal line {Shows(untestedSupervised, withdrawal)} (the detox line alone expected)");
            });
        }

        // ---- the kits, the doctors, the cap -------------------------------------------------------------------------------------------------

        private static void CheckDrugTestKitsAndDoctors(Map map)
        {
            Guarded("drug testing: no kit no test, the night doctor and the only doctor are never sent, two tests an hour, policy off tests nobody", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DrugTestSetup(g, r);
                if (DrugTesting.KitCount(map) != 0)
                {
                    RPLog.Message($"[RimPrison:test] drug testing: {DrugTesting.KitCount(map)} kits are lying about, the kit checks were not made");
                    return;
                }
                var s = new RingScene(map, r);
                int now = Find.TickManager.TicksGame;
                Pawn a = s.Inmate("Zdt5a"), b = s.Inmate("Zdt5b"), c = s.Inmate("Zdt5c");
                foreach (Pawn p in new[] { a, b, c }) p.Inmate().lastOverdoseTick = now - 10;
                Pawn day = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor), second = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor), guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                foreach (Pawn doctor in new[] { day, second }) DtWorkAllDay(doctor);
                foreach ((Pawn p, int at) in new[] { (day, 0), (second, 1), (guard, 2), (a, 3), (b, 4), (c, 5) }) s.Place(p, at);
                var giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("RP_DrugTest").Worker;
                g.drugTests = DrugTestPolicy.Targeted;
                DrugTesting.Hourly(map, g);
                if (DrugTesting.DueCount < 3) DtFail($"the set-up has {DrugTesting.DueCount} inmates due (3 expected)");

                // No kit, no test: the doctor is offered nothing, and the Policy row says so.
                if (giver.HasJobOnThing(day, a) || !giver.ShouldSkip(day)) DtFail("a doctor is offered a test with no kit on the map");
                if (DrugTesting.Blocker(map) != "RP_DrugTestNoKits") DtFail($"the Policy row does not say there are no kits ({DrugTesting.Blocker(map)})");
                Thing kit = ThingMaker.MakeThing(DrugTesting.Kit);
                kit.stackCount = 3;
                GenPlace.TryPlaceThing(kit, day.Position, map, ThingPlaceMode.Near, out Thing placed);
                r.Add(() =>
                {
                    if (placed is { Destroyed: false }) placed.Destroy(DestroyMode.Vanish);
                });
                if (DrugTesting.KitCount(map) != 3) DtFail($"the kit stack is {DrugTesting.KitCount(map)} (3 placed)");
                if (!giver.HasJobOnThing(day, a) || giver.ShouldSkip(day)) DtFail($"a day doctor with a kit and two doctors on site is offered no test (job {giver.HasJobOnThing(day, a)}, skip {giver.ShouldSkip(day)}, {DrugTesting.DoctorsOnSite(map)} doctors, takeable {DrugTesting.Takeable(a, a.Inmate())})");
                if (giver.HasJobOnThing(guard, a)) DtFail("a guard is offered a drug test (the doctors' work)");
                if (DrugTesting.Blocker(map) != null) DtFail($"the Policy row blocks a round that can run ({DrugTesting.Blocker(map)})");

                // The night doctor is never sent.
                CompStaff dc = day.GetComp<CompStaff>();
                dc.nightShift = true;
                if (giver.HasJobOnThing(day, a) || DrugTesting.IsTestDoctor(day)) DtFail("the night doctor is offered a drug test");
                dc.nightShift = false;
                // On duty by the timetable, not only by the night flag: the day shift's work hours, and not the commute hour before it, a break or the hours of sleep.
                ShiftSystem.Apply(day, night: false);
                int joy = Enumerable.Range(0, 24).First(h => day.timetable.GetAssignment(h) == TimeAssignmentDefOf.Joy);
                foreach ((int hour, bool offered, string when) in new[] { (5, false, "in the commute hour before the shift"), (6, true, "in the first hour of the shift"), (joy, false, "in a break"), (21, true, "in the last hour of the shift"), (22, false, "asleep, the shift being over") })
                {
                    DrugTesting.HourForTests = hour;
                    if (giver.HasJobOnThing(day, a) != offered || DrugTesting.IsTestDoctor(day) != offered) DtFail($"a day doctor {when} (hour {hour}) is {(offered ? "not offered" : "offered")} a drug test");
                }
                DrugTesting.HourForTests = null;
                DtWorkAllDay(day);
                // Never the only doctor on site.
                DrugTesting.DoctorsForTests = 1;
                if (giver.HasJobOnThing(day, a) || DrugTesting.Blocker(map) != "RP_DrugTestNoDoctor") DtFail($"the only doctor on site is offered a drug test (or the row does not say so: {DrugTesting.Blocker(map)})");
                DrugTesting.DoctorsForTests = 2;
                if (!giver.HasJobOnThing(day, a)) DtFail("with a second doctor on site the day doctor is not offered a test");
                DrugTesting.DoctorsForTests = null;

                // Those who cannot be taken now: held hostage, in supermax, in a break. (Not the whole list: a riot, below.)
                Hediff hostage = a.health.AddHediff(RPHostageDefOf.RP_Hostage);
                if (giver.HasJobOnThing(day, a)) DtFail("a hostage is offered for a drug test");
                a.health.RemoveHediff(hostage);
                SecurityLevelDef level0 = b.Inmate().security;
                b.Inmate().security = SupermaxSystem.Level;
                if (giver.HasJobOnThing(day, b)) DtFail("an inmate of supermax is offered for a drug test");
                b.Inmate().security = level0;
                c.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Psychotic, forced: true);
                if (giver.HasJobOnThing(day, c)) DtFail("an inmate in a mental break is offered for a drug test");
                c.mindState.mentalStateHandler.Reset();
                if (!giver.HasJobOnThing(day, a) || !giver.HasJobOnThing(day, b) || !giver.HasJobOnThing(day, c)) DtFail("the inmates are not offered again when what held them is over");
                // A riot anywhere: nobody is taken aside, and the next hour looks again.
                Pawn rioter = s.Inmate("Zdt5d");
                rioter.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                DrugTesting.Hourly(map, g);
                bool riotListed = DrugTesting.DueCount != 0;
                rioter.mindState.mentalStateHandler.Reset();
                if (riotListed) DtFail("the hour's list is not empty while a riot is on");
                DrugTesting.Hourly(map, g);
                if (DrugTesting.DueCount < 3) DtFail("the list is not built again the hour after the riot");

                // A hostage crisis running: nobody is taken aside and the list is empty; a riot's flag (set by the rioters' ticks, see the rounds cut short) stops the giver at once too.
                var crisis = new StoryArc { def = RPHostageDefOf.RP_Arc_Hostage, pawns = new List<Pawn> { a } };
                g.story.arcs.Add(crisis);
                bool crisisOffered = giver.HasJobOnThing(day, b) || !giver.ShouldSkip(day);
                string crisisBlocker = DrugTesting.Blocker(map);
                DrugTesting.Hourly(map, g);
                bool crisisListed = DrugTesting.DueCount != 0;
                g.story.arcs.Remove(crisis);
                if (crisisOffered || crisisListed || crisisBlocker != "RP_DrugTestCrisis") DtFail($"a hostage crisis does not stop the round (offered {crisisOffered}, listed {crisisListed}, the row says {crisisBlocker})");
                DrugTesting.Hourly(map, g);
                if (!giver.HasJobOnThing(day, b)) DtFail("the round does not go on when the hostage crisis is over");
                RiotPolice.Notify_Rioting(map);
                bool riotOffered = giver.HasJobOnThing(day, b) || !giver.ShouldSkip(day);
                RiotPolice.ForgetRiotForTests(map);
                if (riotOffered) DtFail("a riot's flag does not stop the work giver");
                if (!giver.HasJobOnThing(day, b) || giver.ShouldSkip(day)) DtFail("the work giver does not offer a test when the riot flag is gone");
                // The flag is a mark of a tick of this game: one from later than now (a load of an earlier save, another game's clock) is no riot, one from a game that was left is gone, and the mark holds its few ticks and no more.
                int clock = Find.TickManager.TicksGame;
                RiotPolice.MarkForTests(map, clock + GenDate.TicksPerDay / 2);
                bool futureOn = RiotPolice.RiotActive(map) || DrugTesting.CrisisOn(map) || !giver.HasJobOnThing(day, b);
                RiotPolice.MarkForTests(map, clock - RiotPolice.RiotMarkTicks);
                bool edgeOn = RiotPolice.RiotActive(map);
                RiotPolice.MarkForTests(map, clock - RiotPolice.RiotMarkTicks - 1);
                bool staleOn = RiotPolice.RiotActive(map);
                RiotPolice.MarkForTests(map, clock);
                bool nowOn = RiotPolice.RiotActive(map);
                RiotPolice.Reset();
                bool resetOn = RiotPolice.RiotActive(map);
                RiotPolice.ForgetRiotForTests(map);
                if (futureOn) DtFail("a riot mark from a later tick than now stops the round (or the work giver is not offering a test with it)");
                if (!edgeOn || staleOn || !nowOn) DtFail($"the riot mark's window: at its edge {edgeOn} (true), one tick past {staleOn} (false), now {nowOn} (true)");
                if (resetOn) DtFail("a new game or a load leaves the riot marks of the one before");

                // Two tests an hour in the whole prison, however many doctors.
                if (!DrugTesting.TryBegin(day, a) || !DrugTesting.TryBegin(second, b)) DtFail("the first two tests of an hour did not start");
                if (DrugTesting.TryBegin(day, c)) DtFail("a third test started in one hour");
                if (!DrugTesting.HourFull || giver.HasJobOnThing(second, c) || !giver.ShouldSkip(second)) DtFail("with two tests started in the hour a doctor is still offered a third");
                if (DrugTesting.StartedThisHour != DrugTesting.MaxPerHour) DtFail($"{DrugTesting.StartedThisHour} tests are counted in the hour (2 started)");
                DrugTesting.Release(day);
                DrugTesting.Release(second);

                // Policy off tests nobody: no job, no start, and the hour's pass lists nobody.
                g.drugTests = DrugTestPolicy.Off;
                DrugTesting.ClearForTests();
                DrugTesting.SetDueForTests(g, (a, DrugTesting.ReasonOverdose));
                if (giver.HasJobOnThing(day, a) || !giver.ShouldSkip(day)) DtFail("a doctor is offered a drug test with the policy off");
                if (DrugTesting.TryBegin(day, a)) DtFail("a test started with the policy off");
                DrugTesting.Hourly(map, g);
                if (DrugTesting.DueCount != 0) DtFail("the hour's pass lists inmates with the policy off");

                // The kits are kept in stock as the antipsychotic is: nothing ordered with the policy off or with enough, an order in packs of ten when they run low.
                g.ledger.funds = Math.Max(g.ledger.funds, 20000);
                if (g.supply.PendingCount(DrugTesting.Kit) != 0)
                {
                    RPLog.Message($"[RimPrison:test] drug testing: a shuttle carries {g.supply.PendingCount(DrugTesting.Kit)} kits already, the stock checks were not made");
                    return;
                }
                placed.Destroy(DestroyMode.Vanish);
                DrugTesting.Daily(map, g);
                if (g.supply.PendingCount(DrugTesting.Kit) != 0) DtFail("kits were ordered with the policy off");
                g.drugTests = DrugTestPolicy.Targeted;
                DrugTesting.Daily(map, g);
                int ordered = g.supply.PendingCount(DrugTesting.Kit);
                if (ordered <= 0 || ordered % 10 != 0) DtFail($"with the kits run out the daily pass ordered {ordered} (a number of packs of ten, more than none)");
                DrugTesting.Daily(map, g);
                if (g.supply.PendingCount(DrugTesting.Kit) != ordered) DtFail($"the daily pass ordered kits again while an order was on its way ({ordered} -> {g.supply.PendingCount(DrugTesting.Kit)})");
                g.supply.pending.RemoveAll(o => o.thing == DrugTesting.Kit);
                Thing stock = ThingMaker.MakeThing(DrugTesting.Kit);
                stock.stackCount = 25;
                GenPlace.TryPlaceThing(stock, day.Position, map, ThingPlaceMode.Near, out Thing stocked);
                r.Add(() =>
                {
                    if (stocked is { Destroyed: false }) stocked.Destroy(DestroyMode.Vanish);
                });
                DrugTesting.Daily(map, g);
                if (g.supply.PendingCount(DrugTesting.Kit) != 0) DtFail($"kits were ordered with 25 on the shelf ({g.supply.PendingCount(DrugTesting.Kit)})");
            });
        }
    }
}
