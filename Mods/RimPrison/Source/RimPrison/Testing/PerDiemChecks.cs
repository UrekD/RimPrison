using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Pay by the grade (<see cref="PerDiem"/>, gap analysis 7, W1). Forced grade histories give the stated factors (the letters' edges, a mixed week, older days left out, fewer days, none); the day's
    /// payment for a throwaway inmate moves by the factor, the ledger's per-diem line says what the week's grade added; reputation still multiplies under the grade; a new game's first week pays 1.0
    /// to the last tick of the day the screens call 7 and the factor from their day 8; the midnight's settlement writes the graded line. The grade history is put back, and so is the clock: checks that need day 8 move it forward (never below tick 0) and the rollback moves it back.
    /// Self-contained, run last in the events plan. Written without a game run.
    /// </summary>
    public static partial class AutoTest
    {
        private static void PerDiemFail(string check, string what) => problems0.Add($"per-diem ({check}): {what}");

        private static void CheckPerDiem(Map map)
        {
            CheckPerDiemFactors(map);
            CheckPerDiemPayment(map);
            CheckPerDiemReputation(map);
            CheckPerDiemFirstWeek(map);
            CheckPerDiemSettlement(map);
            CheckPerDiemScreens(map);
        }

        /// <summary>The grade history (and the clock, when <paramref name="atTick"/> is given) put back by the rollback; the clock then moved to that tick.</summary>
        private static PrisonGame PerDiemScene(Rollback r, int atTick = -1)
        {
            PrisonGame g = PrisonGame.Instance;
            (List<float> history0, List<float> rules0, List<ReportCard> cards0, float lastGrade0) = (g.gradeHistory.ToList(), g.gradeRules.ToList(), g.gradeCards.ToList(), g.lastGrade);
            r.Add(() =>
            {
                g.gradeHistory.Clear();
                g.gradeHistory.AddRange(history0);
                g.gradeRules.Clear();
                g.gradeRules.AddRange(rules0);
                g.gradeCards.Clear();
                g.gradeCards.AddRange(cards0);
                g.lastGrade = lastGrade0;
            });
            if (atTick >= 0)
            {
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(System.Math.Max(0, atTick));
            }
            return g;
        }

        /// <summary>A tick an hour into day 8 of the game, or now when the game is past it already (the clock only goes forward).</summary>
        private static int PerDiemDay8() => System.Math.Max(Find.TickManager.TicksGame, PerDiem.FirstTick + GenDate.TicksPerHour);

        /// <summary>The day the screens show (the Overview's title: <c>GenDate.DaysPassed + 1</c>).</summary>
        private static int PerDiemScreenDay() => GenDate.DaysPassed + 1;

        private static void ForceGrades(PrisonGame g, params float[] grades)
        {
            g.gradeHistory.Clear();
            g.gradeHistory.AddRange(grades);
            g.AlignGradeRules();
        }

        private static float[] Week(float grade) => Enumerable.Repeat(grade, PerDiem.Days).ToArray();

        private static void CheckPerDiemFactors(Map map)
        {
            Guarded("per-diem: the grades give the stated factors", map, r =>
            {
                PrisonGame g = PerDiemScene(r, PerDiemDay8());
                if (!PerDiem.Applies || PerDiemScreenDay() < PerDiem.FirstDay) { PerDiemFail("factors", $"the clock was moved to day {PerDiem.FirstDay} and reads day {PerDiemScreenDay()} (tick {Find.TickManager.TicksGame}, the grades count: {PerDiem.Applies})"); return; }
                var cases = new List<(string name, float[] grades, float factor, int days)>
                {
                    ("an A week", Week(90f), 1.15f, 7), ("a B week", Week(75f), 1.05f, 7), ("a C week", Week(60f), 1f, 7), ("a D week", Week(45f), 0.9f, 7), ("an F week", Week(20f), 0.75f, 7),
                    ("A's edge (85)", Week(85f), 1.15f, 7), ("B's edge (70)", Week(70f), 1.05f, 7), ("just under C (54.9)", Week(54.9f), 0.9f, 7), ("just under D (39.9)", Week(39.9f), 0.75f, 7),
                    ("a mixed week averaging 74", new[] { 100f, 100f, 100f, 100f, 40f, 40f, 40f }, 1.05f, 7),
                    ("older days left out", new[] { 0f, 0f, 0f, 0f, 0f }.Concat(Week(90f)).ToArray(), 1.15f, 7),
                    ("two days of grades", new[] { 90f, 90f }, 1.15f, 2), ("one F day", new[] { 10f }, 0.75f, 1), ("no grades", new float[0], 1f, 0),
                };
                var seen = new List<string>();
                foreach ((string name, float[] grades, float want, int wantDays) in cases)
                {
                    ForceGrades(g, grades);
                    PerDiem.MeanGrade(g, out float mean, out int days);
                    float factor = PerDiem.GradeFactor(g), ratio = g.PaymentMultiplier / g.BasePaymentMultiplier;
                    if (Mathf.Abs(factor - want) > 0.0001f || days != wantDays || Mathf.Abs(ratio - want) > 0.0001f)
                        PerDiemFail("factors", $"{name} [{string.Join(", ", grades.Select(x => x.ToString("F1")))}]: factor {factor:F3} (expected {want:F2}), {days} days counted (expected {wantDays}), mean {mean:F1}, the payment multiplier {g.PaymentMultiplier:F4} against {g.BasePaymentMultiplier:F4} before the grades ({ratio:F3})");
                    seen.Add($"{name}={factor:F2}");
                }
                RPLog.Message($"[RimPrison:test] per-diem: factors at day {PerDiemScreenDay()}: {string.Join(", ", seen)}");
            });
        }

        private static void CheckPerDiemPayment(Map map)
        {
            Guarded("per-diem: the payment line moves by the factor", map, r =>
            {
                PrisonGame g = PerDiemScene(r, PerDiemDay8());
                var weeks = new (string letter, float grade, float factor)[] { ("A", 90f, 1.15f), ("C", 60f, 1f), ("F", 20f, 0.75f) };
                var live = new Dictionary<string, int>();
                foreach ((string letter, float grade, _) in weeks)
                {
                    ForceGrades(g, Week(grade));
                    live[letter] = DailySettlement.DailyPayments(g, map, out _);
                }
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                if (c?.security == null || ShuttleSystem.OnTheWayOut(p) || DeathRowSystem.ExecutionOverdue(c)) { PerDiemFail("payment", $"premise: the throwaway inmate is not one the state pays for (level {c?.security?.defName})"); return; }
                int rate = SupermaxSystem.DailyPayment(c);
                var log = new List<string>();
                var with = new Dictionary<string, int>();
                foreach ((string letter, float grade, float factor) in weeks)
                {
                    ForceGrades(g, Week(grade));
                    int pay = DailySettlement.DailyPayments(g, map, out _);
                    with[letter] = pay;
                    int delta = pay - live[letter], want = Mathf.RoundToInt(rate * (g.BasePaymentMultiplier * factor));
                    if (delta != want) PerDiemFail("payment", $"an {letter} week: the throwaway inmate (rate {rate}, multiplier {g.BasePaymentMultiplier:F4} before the grades) adds {delta} to the day's payment (expected {want} at x{factor:F2})");
                    log.Add($"{letter} {pay} (+{delta})");
                }
                if (!(with["A"] > with["C"] && with["C"] > with["F"])) PerDiemFail("payment", $"the day's payment does not fall with the grade: A {with["A"]}, C {with["C"]}, F {with["F"]}");
                // The ledger's line: the payment, and a note with what the week's grade added (or took), or that it is the plain rate.
                foreach ((string letter, float grade, float factor) in weeks)
                {
                    ForceGrades(g, Week(grade));
                    int share = PerDiem.GradeShare(g, map), plain = DailySettlement.DailyPayments(g, map, out int inmates, g.BasePaymentMultiplier);
                    var before = new HashSet<LedgerEntry>(g.ledger.recent);
                    int paid = DailySettlement.PayPerDiem(g, map);
                    LedgerEntry line = g.ledger.recent.FirstOrDefault(e => !before.Contains(e) && e.category == RPDefOf.RP_InmatePayments);
                    string wantNote = factor != 1f ? "RP_PaymentsGradeNote".Translate(inmates, Money.FormatSigned(share), letter, PerDiem.PercentText(factor)) : "RP_PaymentsGradePlainNote".Translate(inmates, letter);
                    if (line == null || line.amount != paid || paid != with[letter] || share != paid - plain || line.note != wantNote || (factor != 1f) != (share != 0) || (factor > 1f) != (share > 0))
                        PerDiemFail("payment", $"an {letter} week's ledger line: {(line == null ? "none" : $"{line.amount} \"{line.note}\"")}, paid {paid} (the day's payment {with[letter]}), the grade's share {share} (with less without: {paid - plain}), expected the note \"{wantNote}\"");
                    log.Add($"{letter} line \"{line?.note}\"");
                }
                RPLog.Message($"[RimPrison:test] per-diem: payments with the throwaway inmate (rate {rate}) {string.Join(", ", log)}");
            });
        }

        private static void CheckPerDiemReputation(Map map)
        {
            Guarded("per-diem: reputation still multiplies", map, r =>
            {
                PrisonGame g = PerDiemScene(r, PerDiemDay8());
                ForceGrades(g, Week(90f));
                // (The live prison's payment at both reputations first, then the throwaway inmate's part of it.)
                g.reputation = 0f;
                int live0 = DailySettlement.DailyPayments(g, map, out _);
                g.reputation = 100f;
                int live100 = DailySettlement.DailyPayments(g, map, out _);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                if (c?.security == null) { PerDiemFail("reputation", "premise: the throwaway inmate has no level"); return; }
                int rate = SupermaxSystem.DailyPayment(c);
                (float m, float b, int delta) At(float rep, int live)
                {
                    g.reputation = rep;
                    return (g.PaymentMultiplier, g.BasePaymentMultiplier, DailySettlement.DailyPayments(g, map, out _) - live);
                }
                (float m0, float b0, int d0) = At(0f, live0);
                (float m100, float b100, int d100) = At(100f, live100);
                bool ok = Mathf.Abs(m0 / b0 - 1.15f) < 0.0001f && Mathf.Abs(m100 / b100 - 1.15f) < 0.0001f && Mathf.Abs(m100 / m0 - 1.15f / 0.85f) < 0.0001f
                    && d0 == Mathf.RoundToInt(rate * m0) && d100 == Mathf.RoundToInt(rate * m100) && d100 > d0;
                if (!ok) PerDiemFail("reputation", $"an A week at reputation 0: multiplier {m0:F4} ({b0:F4} before the grades), the throwaway inmate paid {d0}; at 100: {m100:F4} ({b100:F4}), paid {d100} (rate {rate}; expected x1.15 on both and reputation's 1.15/0.85 between them)");
                RPLog.Message($"[RimPrison:test] per-diem: an A week pays the throwaway inmate {d0} at reputation 0 and {d100} at 100 (x{m0:F3} / x{m100:F3})");
            });
        }

        private static void CheckPerDiemFirstWeek(Map map)
        {
            Guarded("per-diem: a new game's first week pays the plain rate", map, r =>
            {
                int lastOfWeek = PerDiem.FirstTick - 1;
                PrisonGame g = PerDiemScene(r, lastOfWeek);
                ForceGrades(g, Week(95f));
                Pawn p = Throwaway(map, r);
                int day7 = PerDiemScreenDay(), share7 = PerDiem.GradeShare(g, map);
                float factor7 = PerDiem.GradeFactor(g), ratio7 = g.PaymentMultiplier / g.BasePaymentMultiplier;
                string note7 = PerDiem.LedgerNote(g, 3, share7), plainNote = "RP_PaymentsNote".Translate(3);
                if (day7 != PerDiem.FirstDay - 1 || factor7 != 1f || Mathf.Abs(ratio7 - 1f) > 0.0001f || share7 != 0 || note7 != plainNote)
                    PerDiemFail("first week", $"at tick {Find.TickManager.TicksGame} (the screens' day {day7}, expected {PerDiem.FirstDay - 1}) with an A week: factor {factor7:F3}, multiplier ratio {ratio7:F3}, the grade's share {share7}, the note \"{note7}\" (expected 1, 1, 0 and \"{plainNote}\")");
                // The day's first tick: the grades count.
                Find.TickManager.DebugSetTicksGame(PerDiem.FirstTick);
                int day8 = PerDiemScreenDay(), share8 = PerDiem.GradeShare(g, map);
                float factor8 = PerDiem.GradeFactor(g);
                if (day8 != PerDiem.FirstDay || factor8 != 1.15f || share8 <= 0)
                    PerDiemFail("first week", $"at tick {Find.TickManager.TicksGame} (the screens' day {day8}, expected {PerDiem.FirstDay}) with an A week: factor {factor8:F3} (expected 1.15), the grade's share {share8} (expected more than 0)");
                // The grades count exactly on the days the screens number 8 and on: at the game's start, the 7th and 8th midnights of a game begun at 06:00 (a quarter of a day before a
                // screen day turns), and around day 8's first tick.
                var seen = new List<string>();
                foreach (int tick in new[] { 0, GenDate.TicksPerDay, PerDiem.FirstTick - GenDate.TicksPerDay / 4, lastOfWeek, PerDiem.FirstTick, PerDiem.FirstTick + 3 * GenDate.TicksPerDay / 4 })
                {
                    Find.TickManager.DebugSetTicksGame(tick);
                    bool counts = PerDiem.Applies, want = PerDiemScreenDay() >= PerDiem.FirstDay;
                    if (counts != want || (counts ? 1.15f : 1f) != PerDiem.GradeFactor(g))
                        PerDiemFail("first week", $"at tick {tick} the screens say day {PerDiemScreenDay()} and the grades count: {counts}, factor {PerDiem.GradeFactor(g):F3} (expected {want})");
                    seen.Add($"{tick}=day {PerDiemScreenDay()} x{PerDiem.GradeFactor(g):F2}");
                }
                RPLog.Message($"[RimPrison:test] per-diem: day 8 begins at tick {PerDiem.FirstTick}; day {day7} pays x{factor7:F2}, day {day8} x{factor8:F2} (+{share8}); {string.Join(", ", seen)}; the inmate {p.LabelShort}");
            });
        }

        /// <summary>
        /// The midnight's settlement pays by the grade: <see cref="DailySettlement.Run"/> writes its per-diem line through <see cref="DailySettlement.PayPerDiem"/> (whose line and note the payment check
        /// reads) and not a payment of its own. Read from the method's body: the settlement cannot be run in a check (it is the whole midnight), and the events plan's real midnights are before day 8.
        /// </summary>
        private static void CheckPerDiemSettlement(Map map)
        {
            Guarded("per-diem: the midnight's settlement pays through the grades' line", map, r =>
            {
                var called = new HashSet<System.Reflection.MethodBase>();
                foreach (KeyValuePair<System.Reflection.Emit.OpCode, object> op in HarmonyLib.PatchProcessor.ReadMethodBody(HarmonyLib.AccessTools.Method(typeof(DailySettlement), nameof(DailySettlement.Run))))
                    if (op.Value is System.Reflection.MethodBase m) called.Add(m);
                bool pays = called.Contains(HarmonyLib.AccessTools.Method(typeof(DailySettlement), nameof(DailySettlement.PayPerDiem)));
                bool inline = called.Any(m => m.DeclaringType == typeof(DailySettlement) && m.Name == nameof(DailySettlement.DailyPayments));
                if (!pays || inline)
                    PerDiemFail("settlement", $"DailySettlement.Run calls PayPerDiem: {pays}, works out the payment itself (DailyPayments): {inline} (expected the per-diem line written by PayPerDiem alone, {called.Count} methods called)");
                RPLog.Message($"[RimPrison:test] per-diem: the settlement pays through PayPerDiem={pays}, inline={inline}");
            });
        }

        private static void CheckPerDiemScreens(Map map)
        {
            Guarded("per-diem: the desk and the weekly report say it", map, r =>
            {
                PrisonGame g = PerDiemScene(r, PerDiemDay8());
                Throwaway(map, r);
                var lines = new List<string>();
                foreach ((string letter, float grade) in new[] { ("B", 75f), ("F", 20f) })
                {
                    ForceGrades(g, Week(grade));
                    int share = PerDiem.GradeShare(g, map);
                    string desk = PerDiem.DeskLine(g, map), week = PerDiem.WeekLine(g, map), tip = OverviewTab.GradeTip(true, desk);
                    string money = Money.FormatSigned(share);
                    if (share == 0 || !desk.Contains(money + " a day from your " + letter) || week == null || !week.Contains(money) || !week.Contains(letter) || !tip.Contains(desk))
                        PerDiemFail("screens", $"a {letter} week, the grade's share {share}: the desk says \"{desk}\", the week's report \"{week}\" (expected \"{money} a day from your {letter}\" and the money in both)");
                    lines.Add(desk.Split('\n')[0]);
                }
                ForceGrades(g, Week(60f));
                string plain = PerDiem.DeskLine(g, map), plainWeek = PerDiem.WeekLine(g, map);
                string plainWant = "RP_PerDiemDeskPlain".Translate("C", PerDiem.Days, "60"), plainWeekWant = "RP_Week_PerDiemPlain".Translate(PerDiem.Days, "C", "60");
                if (!plain.StartsWith(plainWant) || plainWeek != plainWeekWant)
                    PerDiemFail("screens", $"a C week: the desk says \"{plain}\", the week's report \"{plainWeek}\" (expected the desk to begin \"{plainWant}\" and the report \"{plainWeekWant}\")");
                // A mean just short of A's edge reads as its own number rounded down, not as the edge.
                ForceGrades(g, Week(84.9f));
                string edge = PerDiem.DeskLine(g, map), edgeWeek = PerDiem.WeekLine(g, map);
                if (!edge.Contains("(84, so +5%") || !edge.Contains("from your B") || edgeWeek == null || !edgeWeek.Contains("B (84)"))
                    PerDiemFail("screens", $"a week averaging 84.9: the desk says \"{edge}\", the week's report \"{edgeWeek}\" (expected a B shown as 84)");
                ForceGrades(g);
                string none = PerDiem.DeskLine(g, map);
                string noneWant = "RP_PerDiemDeskNone".Translate();
                if (!none.StartsWith(noneWant) || PerDiem.WeekLine(g, map) != null) PerDiemFail("screens", $"no grades: the desk says \"{none}\", the week's report \"{PerDiem.WeekLine(g, map)}\" (expected the no-grade line and no week line)");
                lines.Add(plain.Split('\n')[0]);
                RPLog.Message($"[RimPrison:test] per-diem: the desk says {string.Join(" | ", lines)}");
            });
        }
    }
}
