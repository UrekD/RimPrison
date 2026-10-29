using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The state pays by the grade (gap analysis 7, W1): each inmate's per-diem (<see cref="SupermaxSystem.DailyPayment"/>) is multiplied, on top of reputation's factor
    /// (<see cref="PrisonGame.BasePaymentMultiplier"/>), by a factor from the mean of the last <see cref="Days"/> grades of <see cref="PrisonGame.gradeHistory"/> (fewer when there are fewer; none, 1):
    /// A +15%, B +5%, C the plain rate, D -10%, F -25%. Not before <see cref="FirstDay"/> of the game, as the screens count it (<see cref="Applies"/>). Nothing is saved for it: the grade
    /// history and the clock hold all it reads, so an old save pays by its grades from its next midnight (one still in its first week waits for day 8, like a new game).
    /// </summary>
    public static class PerDiem
    {
        public const int Days = 7, FirstDay = 8;

        /// <summary>The factor for a mean grade, by its letter (<see cref="PrisonGrading.Letter"/>).</summary>
        public static float FactorOf(float meanGrade) => PrisonGrading.Letter(meanGrade) switch
        {
            "A" => 1.15f,
            "B" => 1.05f,
            "C" => 1f,
            "D" => 0.9f,
            _ => 0.75f,
        };

        /// <summary>The mean of the last <see cref="Days"/> grades of the history and how many there were (false, and 0 days, with no grade yet).</summary>
        public static bool MeanGrade(PrisonGame game, out float mean, out int days)
        {
            var h = game.gradeHistory;
            days = Mathf.Min(Days, h.Count);
            mean = 0f;
            if (days <= 0) return false;
            for (int i = h.Count - days; i < h.Count; i++) mean += h[i];
            mean /= days;
            return true;
        }

        /// <summary>The game tick day <see cref="FirstDay"/> begins at, as every screen counts the days (<c>TicksGame / TicksPerDay + 1</c>: the Overview's title, the ledger's When).</summary>
        public const int FirstTick = (FirstDay - 1) * GenDate.TicksPerDay;

        /// <summary>
        /// The grades count from <see cref="FirstDay"/> as the screens number it: the first midnight that pays by them is the one the Overview's title calls day 8 (the settlement runs at
        /// local midnight, the screens' day turns at the hour the game began), and the seven midnights before it pay the plain rate.
        /// </summary>
        public static bool Applies => Find.TickManager.TicksGame >= FirstTick;

        /// <summary>What the grades multiply the per-diem by now (1 in the first week and with no grade yet).</summary>
        public static float GradeFactor(PrisonGame game) => Applies && MeanGrade(game, out float mean, out _) ? FactorOf(mean) : 1f;

        /// <summary>What the grades add to (or take from) a day's payment for the inmates the state pays for now: the payment with the factor less the payment without it.</summary>
        public static int GradeShare(PrisonGame game, Map map)
        {
            float factor = GradeFactor(game);
            if (factor == 1f) return 0;
            float base0 = game.BasePaymentMultiplier;
            return DailySettlement.DailyPayments(game, map, out _, base0 * factor) - DailySettlement.DailyPayments(game, map, out _, base0);
        }

        /// <summary>"+15%", "-25%", "0%".</summary>
        public static string PercentText(float factor)
        {
            int pct = Mathf.RoundToInt((factor - 1f) * 100f);
            return pct > 0 ? "+" + pct + "%" : pct + "%";
        }

        /// <summary>The mean as the lines show it: rounded down, so it never reads as a letter's edge it is short of (84.6 is "84", a B).</summary>
        public static string MeanText(float mean) => Mathf.FloorToInt(mean).ToString();

        /// <summary>The note on the day's per-diem line of the ledger: the inmates, and what the week's grade did to it (from day 8, with grades).</summary>
        public static string LedgerNote(PrisonGame game, int inmates, int share)
        {
            if (!Applies || !MeanGrade(game, out float mean, out _)) return "RP_PaymentsNote".Translate(inmates);
            string letter = PrisonGrading.Letter(mean);
            float factor = FactorOf(mean);
            return factor != 1f ? "RP_PaymentsGradeNote".Translate(inmates, Money.FormatSigned(share), letter, PercentText(factor))
                : "RP_PaymentsGradePlainNote".Translate(inmates, letter);
        }

        /// <summary>The desk's line under the grade's tooltip: what the week's grade is worth a day now, or when it starts counting, and the scale.</summary>
        public static string DeskLine(PrisonGame game, Map map)
        {
            string scale = "RP_PerDiemScale".Translate(Days);
            if (!Applies) return "RP_PerDiemDeskSoon".Translate(FirstDay) + "\n" + scale;
            if (!MeanGrade(game, out float mean, out int days)) return "RP_PerDiemDeskNone".Translate() + "\n" + scale;
            string letter = PrisonGrading.Letter(mean);
            int share = GradeShare(game, map);
            string line = FactorOf(mean) != 1f ? "RP_PerDiemDesk".Translate(Money.FormatSigned(share), letter, days, MeanText(mean), PercentText(FactorOf(mean)))
                : "RP_PerDiemDeskPlain".Translate(letter, days, MeanText(mean));
            return line + "\n" + scale;
        }

        /// <summary>The weekly report's line: what the grades are worth, the plain rate for a C, or when they start counting (null with no grade yet).</summary>
        public static string WeekLine(PrisonGame game, Map map)
        {
            if (!Applies) return "RP_Week_PerDiemSoon".Translate(FirstDay);
            if (!MeanGrade(game, out float mean, out int days)) return null;
            string letter = PrisonGrading.Letter(mean);
            return FactorOf(mean) != 1f ? "RP_Week_PerDiem".Translate(days, letter, MeanText(mean), PercentText(FactorOf(mean)), Money.FormatSigned(GradeShare(game, map)))
                : "RP_Week_PerDiemPlain".Translate(days, letter, MeanText(mean));
        }
    }
}
