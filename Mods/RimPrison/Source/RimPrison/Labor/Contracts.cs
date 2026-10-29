using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Export contracts: a buyer offers a bonus on top of the sale price for a number of license plates through the
    /// Exports zone over the next five morning export runs (a third more than the prison has been selling; only offered
    /// once it sells plates). Met, the bonus is paid; missed, a penalty and a dent in the prison's name. One at a time.
    /// </summary>
    public class IncidentWorker_ExportContract : IncidentWorker_Prison
    {
        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && PrisonGame.Instance.story.contractTarget <= 0 && PrisonGame.Instance.story.platesPerDay >= ExportContracts.MinPlatesPerDay
            && PrisonMap.Of((Map)parms.target)?.zones.Any(RPDefOf.RP_Exports) == true
            && !StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_ExportContract"));

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            float rate = PrisonGame.Instance.story.platesPerDay;
            if (rate < ExportContracts.MinPlatesPerDay) return false;
            int target = ExportContracts.TargetFor(rate);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_ExportContract", "RP_ContractLabel".Translate(),
                "RP_ContractText".Translate(ExportContracts.Buyers().RandomElement(), target, ExportContracts.Days, Money.Format(ExportContracts.Bonus(target)),
                    Money.Format(ExportContracts.Penalty(target)), Mathf.RoundToInt(rate)), map);
            letter.amount = target;
            letter.StartTimeout(GenDate.TicksPerHour * 24);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public static class ExportContracts
    {
        /// <summary>Days: export runs to deliver in; a backstop two days later ends a contract with no runs (no Exports zone).</summary>
        public const int Days = 5, BackstopDays = 2, BonusPerPlate = 5;
        public const float PenaltyShare = 0.5f, ReputationCost = 3f, MinPlatesPerDay = 10f, Stretch = 1.3f, RateWeight = 0.3f;
        public static ThingDef Plates => RPLaborDefOf.RP_LicensePlates;

        public static IEnumerable<string> Buyers() => "RP_ContractBuyers".Translate().ToString().Split(';').Select(s => s.Trim());

        /// <summary>The recent rate over the contract's days, stretched by a third, rounded up to the ten.</summary>
        public static int TargetFor(float platesPerDay) => Mathf.CeilToInt(platesPerDay * Days * Stretch / 10f) * 10;

        public static int Bonus(int target) => target * BonusPerPlate;

        public static int Penalty(int target) => Mathf.RoundToInt(Bonus(target) * PenaltyShare);

        public static void Sign(StorySystem s, int target)
        {
            s.contractTarget = target;
            s.contractDelivered = 0;
            s.contractRunsLeft = Days;
            s.contractDueTick = Find.TickManager.TicksGame + (Days + BackstopDays) * GenDate.TicksPerDay;
        }

        /// <summary>
        /// A map's morning export run sold these plates (maybe none). The running rate moves once a day (a second map's run
        /// the same day adds to it), and one morning counts for at most twice the rate (a stockpile sold off at once does
        /// not size the next offer). A signed contract counts the plates and loses a run.
        /// </summary>
        public static void Notify_ExportRun(PrisonGame game, int count)
        {
            StorySystem s = game.story;
            int day = GenDate.DaysPassed;
            bool newDay = day != s.lastExportRunDay;
            s.lastExportRunDay = day;
            float counted = Mathf.Min(count, 2f * Mathf.Max(s.platesPerDay, MinPlatesPerDay));
            s.platesPerDay = newDay ? s.platesPerDay + (counted - s.platesPerDay) * RateWeight : s.platesPerDay + counted * RateWeight;
            if (s.contractTarget <= 0) return;
            s.contractDelivered += count;
            if (s.contractDelivered < s.contractTarget)
            {
                if (newDay && --s.contractRunsLeft <= 0) Miss(game);
                return;
            }
            int bonus = Bonus(s.contractTarget);
            game.ledger.Add(RPDefOf.RP_ExportSales, bonus, "RP_ContractBonusNote".Translate());
            game.stats.contractsMet++;
            Messages.Message("RP_ContractMet".Translate(s.contractTarget, Money.Format(bonus)), MessageTypeDefOf.PositiveEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_ContractMet".Translate(s.contractTarget, Money.Format(bonus)));
            Clear(s);
        }

        /// <summary>Hourly: the backstop for a contract whose export runs stopped (the Exports zone went).</summary>
        public static void Hourly(PrisonGame game)
        {
            StorySystem s = game.story;
            if (s.contractTarget > 0 && Find.TickManager.TicksGame >= s.contractDueTick) Miss(game);
        }

        private static void Miss(PrisonGame game)
        {
            StorySystem s = game.story;
            int penalty = Penalty(s.contractTarget);
            game.ledger.Add(RPDefOf.RP_Fines, -penalty, "RP_ContractPenaltyNote".Translate());
            game.reputation = Mathf.Max(0f, game.reputation - ReputationCost);
            game.stats.contractsMissed++;
            Messages.Message("RP_ContractMissed".Translate(s.contractDelivered, s.contractTarget, Money.Format(penalty)), MessageTypeDefOf.NegativeEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_ContractMissed".Translate(s.contractDelivered, s.contractTarget));
            Clear(s);
        }

        private static void Clear(StorySystem s)
        {
            s.contractTarget = 0;
            s.contractDelivered = 0;
            s.contractRunsLeft = 0;
            s.contractDueTick = -1;
        }
    }

    public class ExportContractDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Contract_Sign", "RP_Contract_Decline" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.story.contractTarget > 0 ? "RP_ContractAlready".Translate().ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            StorySystem s = PrisonGame.Instance.story;
            if (option != 0 || s.contractTarget > 0 || l.amount <= 0) return;
            ExportContracts.Sign(s, l.amount);
        }
    }
}
