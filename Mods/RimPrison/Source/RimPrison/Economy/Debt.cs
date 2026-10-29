using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Debt crisis: three settlements in a row in the red and the state offers a loan (debt cleared plus a cushion, 10%
    /// interest), repaid from a quarter of each day's inmate payments. Turned down, the offer comes back after five more
    /// days in debt. One loan at a time.
    /// </summary>
    public static class DebtCrisis
    {
        public const int DaysInDebt = 3, ReofferDays = 5, Cushion = 10000, MinRepayment = 200;
        public const float Interest = 0.1f, RepaymentShare = 0.25f;
        public const float ReputationCost = 3f;

        /// <summary>The loan on offer: the debt rounded up to the thousand, plus the cushion.</summary>
        public static int LoanFor(int funds) => (funds < 0 ? (-funds + 999) / 1000 * 1000 : 0) + Cushion;

        /// <summary>Midnight, after the day's money: repay the loan from today's payments, then count the days in debt.</summary>
        public static void Daily(PrisonGame game, Map map, int payments)
        {
            StorySystem s = game.story;
            if (s.loanOwed > 0)
            {
                int repay = System.Math.Min(s.loanOwed, System.Math.Max(MinRepayment, (int)(payments * RepaymentShare)));
                game.ledger.Add(RPDefOf.RP_LoanRepayment, -repay, "RP_LoanRepaymentNote".Translate(Money.Format(s.loanOwed - repay)));
                s.loanOwed -= repay;
                if (s.loanOwed <= 0)
                {
                    s.loanOwed = 0;
                    s.debtDays = 0; // the next crisis counts its own nights from here
                    Messages.Message("RP_LoanRepaid".Translate(), MessageTypeDefOf.PositiveEvent);
                    Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_LoanRepaid".Translate());
                }
            }
            s.debtDays = game.ledger.funds < 0 ? s.debtDays + 1 : 0;
            if (s.loanOwed > 0 || s.debtDays < DaysInDebt || (s.debtDays - DaysInDebt) % ReofferDays != 0) return;
            int loan = LoanFor(game.ledger.funds);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_DebtCrisis", "RP_DebtCrisisLabel".Translate(),
                "RP_DebtCrisisText".Translate(s.debtDays, Money.Format(-game.ledger.funds), Money.Format(loan), Money.Format(Owed(loan)),
                    RepaymentShare.ToStringPercent(), ReofferDays), map);
            letter.StartTimeout(GenDate.TicksPerHour * 20);
            Find.LetterStack.ReceiveLetter(letter);
        }

        public static int Owed(int loan) => UnityEngine.Mathf.RoundToInt(loan * (1f + Interest));
    }

    public class DebtCrisisDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_DebtCrisis_Loan", "RP_DebtCrisis_Refuse" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (option != 0 || game.story.loanOwed > 0) return;
            // Sized when answered: funds move in the hours before the warden decides.
            int loan = DebtCrisis.LoanFor(game.ledger.funds);
            game.ledger.Add(RPDefOf.RP_StateLoan, loan, "RP_StateLoanNote".Translate());
            game.story.loanOwed = DebtCrisis.Owed(loan);
            game.story.debtDays = 0;
            game.reputation = UnityEngine.Mathf.Max(0f, game.reputation - DebtCrisis.ReputationCost);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_StateLoan".Translate(Money.Format(loan)));
        }
    }
}
