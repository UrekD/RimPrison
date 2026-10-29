using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// A heat wave or a cold snap (vanilla's conditions). Cells that run hot (over 30 °C) or cold (under 10 °C) wear
    /// tempers down every hour. Pay for relief (water and fans, or blankets and extra heating: a third of the strain,
    /// tempers cool now) or let them sweat it out; heatstroke and hypothermia are vanilla's, and a death from either
    /// already opens a neglect inquiry.
    /// </summary>
    public class IncidentWorker_Weather : IncidentWorker_Prison
    {
        public const int ReliefPerInmate = 8;

        protected override int MinInmates => 4;

        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && WeatherStrain.Active((Map)parms.target) == null && WeatherStrain.Fitting((Map)parms.target) != null;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            // Like vanilla: a heat wave only when it is warm already, a cold snap only when it is cool but not freezing.
            GameConditionDef def = WeatherStrain.Fitting(map);
            if (def == null) return false;
            bool heat = def == GameConditionDefOf.HeatWave;
            int days = Rand.RangeInclusive(2, 4);
            map.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(def, days * GenDate.TicksPerDay));
            PrisonGame.Instance.stats.weatherEvents++;
            int cost = ReliefPerInmate * map.mapPawns.PrisonersOfColonySpawnedCount;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Weather", (heat ? "RP_HeatWaveLabel" : "RP_ColdSnapLabel").Translate(),
                (heat ? "RP_HeatWaveText" : "RP_ColdSnapText").Translate(days, Money.Format(cost)), map);
            letter.money = cost;
            letter.amount = heat ? 1 : 0;
            letter.StartTimeout(GenDate.TicksPerHour * 6);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 1, (heat ? "RP_Chr_HeatWave" : "RP_Chr_ColdSnap").Translate(days));
            return true;
        }
    }

    public class WeatherDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Weather_Relief", "RP_Weather_Nothing" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < l.money ? "RP_CannotAfford".Translate(Money.Format(l.money)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option != 0 || WeatherStrain.Active(map) is not { } cond) return;
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Supplies, l.money, (l.amount == 1 ? "RP_HeatReliefNote" : "RP_ColdReliefNote").Translate())) return;
            PrisonGame.Instance.story.weatherReliefMapId = map.uniqueID;
            PrisonGame.Instance.story.weatherReliefStartTick = cond.startTick;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c) c.tension = Mathf.Max(0f, c.tension - 5f);
        }
    }

    /// <summary>
    /// The heat or the cold as a tension cause (fed into TensionUtility.HourlyUpdate, so personality and danger scale it
    /// and it shows as the cause). Relief counts for the map and the very condition it was bought for.
    /// </summary>
    public static class WeatherStrain
    {
        public const float HotAbove = 30f, ColdBelow = 10f, StrainPerHour = 0.8f, ReliefFactor = 1f / 3f;

        public static GameCondition Active(Map map) =>
            map?.gameConditionManager.GetActiveCondition(GameConditionDefOf.HeatWave) ?? map?.gameConditionManager.GetActiveCondition(GameConditionDefOf.ColdSnap);

        public static bool Relieved(Map map, GameCondition cond)
        {
            StorySystem s = PrisonGame.Instance?.story;
            return s != null && cond != null && s.weatherReliefMapId == map.uniqueID && s.weatherReliefStartTick == cond.startTick;
        }

        /// <summary>This hour's strain on an inmate (0 when the weather is ordinary or they are comfortable), and its cause.</summary>
        public static float For(Pawn p, out string cause)
        {
            cause = null;
            GameCondition cond = Active(p.Map);
            if (cond == null) return 0f;
            float t = p.AmbientTemperature;
            if (t <= HotAbove && t >= ColdBelow) return 0f;
            cause = t > HotAbove ? "RP_Cause_Heat" : "RP_Cause_Cold";
            return StrainPerHour * (Relieved(p.Map, cond) ? ReliefFactor : 1f);
        }

        /// <summary>The condition vanilla would allow at this seasonal temperature (heat 20+, cold 0-15), or none.</summary>
        public static GameConditionDef Fitting(Map map)
        {
            float t = map.mapTemperature.SeasonalTemp;
            if (t >= 20f) return GameConditionDefOf.HeatWave;
            if (t > 0f && t < 15f) return GameConditionDefOf.ColdSnap;
            return null;
        }
    }
}
