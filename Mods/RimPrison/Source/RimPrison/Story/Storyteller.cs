using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Numbers about the prison right now, recomputed hourly; event weights read these, never pawns.</summary>
    public enum PulseMetric : byte
    {
        Inmates,
        AvgTension,
        TenseShare,
        AvgMood,
        RivalGangs,
        DaysSinceRiot,
        DaysSinceDeath,
        StaffAvgMood,
        InmatesPerGuard,
        Reputation,
    }

    public class PrisonPulse
    {
        public int inmates;
        public float avgTension, tenseShare, avgMood = 0.5f, staffAvgMood = 0.5f, inmatesPerGuard;
        public bool rivalGangs;

        public float Get(PulseMetric m)
        {
            PrisonGame game = PrisonGame.Instance;
            switch (m)
            {
                case PulseMetric.Inmates: return inmates;
                case PulseMetric.AvgTension: return avgTension;
                case PulseMetric.TenseShare: return tenseShare;
                case PulseMetric.AvgMood: return avgMood;
                case PulseMetric.RivalGangs: return rivalGangs ? 1f : 0f;
                case PulseMetric.DaysSinceRiot: return DaysSince(game.story.lastRiotTick);
                case PulseMetric.DaysSinceDeath: return DaysSince(game.story.lastDeathTick);
                case PulseMetric.StaffAvgMood: return staffAvgMood;
                case PulseMetric.InmatesPerGuard: return inmatesPerGuard;
                case PulseMetric.Reputation: return game.reputation;
                default: return 0f;
            }
        }

        private static float DaysSince(int tick) => tick < 0 ? 999f : (Find.TickManager.TicksGame - tick) / (float)GenDate.TicksPerDay;

        private readonly List<GangDef> gangsSeen = new List<GangDef>();

        /// <summary>Two of these gangs are rivals not in a truce (every pair, not each against the first one seen).</summary>
        public static bool AnyRivals(List<GangDef> gangs)
        {
            for (int i = 0; i < gangs.Count; i++)
                for (int j = i + 1; j < gangs.Count; j++)
                    if (GangUtility.AreRivals(gangs[i], gangs[j])) return true;
            return false;
        }

        /// <summary>One pass over inmates and staff (hourly, alongside the compliance sample).</summary>
        public void Update(Map map)
        {
            IReadOnlyList<Pawn> list = map.mapPawns.PrisonersOfColonySpawned;
            int n = 0, tense = 0;
            float tension = 0f, mood = 0f;
            gangsSeen.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Inmate() is not { activeFlag: true } c) continue;
                n++;
                tension += c.tension;
                if (c.tension >= 60f) tense++;
                mood += list[i].needs.mood?.CurLevelPercentage ?? 0.5f;
                if (c.gang != null && !gangsSeen.Contains(c.gang)) gangsSeen.Add(c.gang);
            }
            rivalGangs = AnyRivals(gangsSeen);
            inmates = n;
            avgTension = n > 0 ? tension / n : 0f;
            tenseShare = n > 0 ? tense / (float)n : 0f;
            avgMood = n > 0 ? mood / n : 0.5f;
            int staff = 0, guards = 0;
            float sm = 0f;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.GetComp<CompStaff>()?.role is not { warden: false }) continue; // (the warden's mood is not the staff's: they never go home, and at night they are most of those on site)
                staff++;
                sm += p.needs?.mood?.CurLevelPercentage ?? 0.5f;
                if (GuardUtility.IsGuard(p)) guards++;
            }
            staffAvgMood = staff > 0 ? sm / staff : 0.5f;
            inmatesPerGuard = n / (float)Mathf.Max(1, guards);
        }
    }

    /// <summary>A weight curve over one pulse number.</summary>
    public class StoryWeight
    {
        public PulseMetric metric;
        public SimpleCurve curve;

        public float Factor(PrisonPulse p) => curve?.Evaluate(p.Get(metric)) ?? 1f;
    }

    /// <summary>
    /// Story settings on an incident: drama cost (spent from the day's budget for beats about people inside), how
    /// long a cast member rests before starring again, and weights from the prison's state.
    /// </summary>
    public class StoryExt : DefModExtension
    {
        public int drama = 1;
        public float perPawnCooldownDays = 3f;
        public List<StoryWeight> weights = new List<StoryWeight>();

        public float Weight(PrisonPulse p)
        {
            float w = 1f;
            for (int i = 0; i < weights.Count; i++) w *= weights[i].Factor(p);
            return w;
        }
    }

    /// <summary>
    /// The storyteller for life inside: beats about the people in the prison come more often in a bigger prison (but
    /// not proportionally: 100 inmates get about three times the beats of 20), at most a few a day, and not when the
    /// day's drama budget is spent (a riot or a death makes the next day quieter).
    /// </summary>
    public class StorytellerCompProperties_PrisonLife : StorytellerCompProperties
    {
        public IncidentCategoryDef category;
        public float mtbDaysAt20Inmates = 0.6f;
        public float populationExponent = 0.7f;
        public int minInmates = 3;

        public StorytellerCompProperties_PrisonLife() => compClass = typeof(StorytellerComp_PrisonLife);
    }

    public class StorytellerComp_PrisonLife : StorytellerComp
    {
        private StorytellerCompProperties_PrisonLife Props => (StorytellerCompProperties_PrisonLife)props;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (target is not Map map || PrisonGame.Instance == null) yield break;
            int n = map.mapPawns.PrisonersOfColonySpawnedCount;
            if (n < Props.minInmates) yield break;
            float factor = Mathf.Clamp(Mathf.Pow(n / 20f, Props.populationExponent), 0.5f, 4f);
            if (!Rand.MTBEventOccurs(Props.mtbDaysAt20Inmates / factor / PrisonDifficulty.EventFactor, GenDate.TicksPerDay, 1000f)) yield break;
            if (PrisonGame.Instance.story.beatsToday >= StoryPacing.MaxBeatsPerDay(n)) yield break;
            IncidentParms parms = GenerateParms(Props.category, target);
            if (TrySelectRandomIncident(UsableIncidentsInCategory(Props.category, parms), out IncidentDef def, target))
                yield return new FiringIncident(def, this, parms);
        }
    }

    /// <summary>
    /// The board's incidents inside the prison (vanilla's mean-time-between-events comp for a category) at the pace the difficulty sets: <see cref="PrisonDifficulty"/> scales the
    /// mean time, one at the neutral level.
    /// </summary>
    public class StorytellerComp_PrisonEvents : StorytellerComp
    {
        private StorytellerCompProperties_CategoryMTB Props => (StorytellerCompProperties_CategoryMTB)props;

        /// <summary>The mean days between incidents of the category at the game's difficulty.</summary>
        public static float MeanDays(StorytellerCompProperties_CategoryMTB props)
        {
            float mtb = props.mtbDays / PrisonDifficulty.EventFactor;
            if (props.mtbDaysFactorByDaysPassedCurve != null) mtb *= props.mtbDaysFactorByDaysPassedCurve.Evaluate(GenDate.DaysPassedSinceSettleFloat);
            return mtb;
        }

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!Rand.MTBEventOccurs(MeanDays(Props), 60000f, 1000f)) yield break;
            IncidentParms parms = GenerateParms(Props.category, target);
            if (TrySelectRandomIncident(UsableIncidentsInCategory(Props.category, parms), out IncidentDef def, target))
                yield return new FiringIncident(def, this, parms);
        }

        public override string ToString() => $"{base.ToString()} {Props.category}";
    }

    public static class StoryPacing
    {
        public static int MaxBeatsPerDay(int inmates) => 2 + inmates / 25;

        public static int DramaBudget(int inmates) => 3 + inmates / 20;

        /// <summary>A beat about people inside may fire: within the day's drama budget.</summary>
        public static bool BudgetAllows(IncidentDef def, Map map)
        {
            if (def.GetModExtension<StoryExt>() is not { } ext || PrisonGame.Instance == null) return true;
            return PrisonGame.Instance.story.dramaToday + ext.drama <= DramaBudget(map.mapPawns.PrisonersOfColonySpawnedCount);
        }

        /// <summary>A beat fired: spend its drama, count it.</summary>
        public static void Notify_Fired(IncidentDef def)
        {
            if (def.GetModExtension<StoryExt>() is not { } ext || PrisonGame.Instance == null) return;
            PrisonGame.Instance.story.dramaToday += ext.drama;
            PrisonGame.Instance.story.beatsToday++;
            PrisonGame.Instance.stats.storyBeats++;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class Patch_IncidentWorker_TryExecute_Story
    {
        public static void Postfix(IncidentWorker __instance, bool __result)
        {
            if (__result && __instance is IncidentWorker_Prison) StoryPacing.Notify_Fired(__instance.def);
        }
    }
}
