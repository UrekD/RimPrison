using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Food poisoning from the prison kitchen: a few inmates go down with it after a meal. Scrub the kitchen (paid, the
    /// filth goes, no more cases), stand the inmate kitchen crew down for a day (no inmate cooks, the staff cook; a second
    /// wave is less likely), or carry on (a second wave half a day later is likely, more so in a dirty kitchen). The sick
    /// are angry either way.
    /// </summary>
    public class IncidentWorker_FoodScare : IncidentWorker_Prison
    {
        public const int ScrubCost = 400, SecondWaveHours = 12, StandDownHours = 24;
        public const float SickTension = 10f;

        protected override int MinInmates => 6;

        /// <summary>Kitchen safety (see <see cref="FoodScare.SafetyFactor"/>): a trained crew makes the scare less likely.</summary>
        public override float ChanceFactorNow(IIncidentTarget target) =>
            base.ChanceFactorNow(target) * (target is Map map ? FoodScare.SafetyFactor(map) : 1f);

        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && PrisonGame.Instance.story.foodScareTick < 0 && FoodScare.Kitchen((Map)parms.target).Count > 0
            && !StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_FoodScare"));

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            if (FoodScare.Kitchen(map).Count == 0) return false;
            List<Pawn> sick = FoodScare.Poison(map, Rand.RangeInclusive(2, 4));
            if (sick.Count == 0) return false;
            int filth = FoodScare.KitchenFilth(map);
            PrisonGame.Instance.stats.foodScares++;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_FoodScare", "RP_FoodScareLabel".Translate(),
                "RP_FoodScareText".Translate(string.Join(", ", sick.Select(p => p.LabelShort)), sick.Count,
                    (FoodScare.Dirty(map) ? "RP_FoodScareDirty" : "RP_FoodScareClean").Translate(filth), Money.Format(ScrubCost), StandDownHours), map, sick);
            letter.pawns.AddRange(sick);
            letter.StartTimeout(GenDate.TicksPerHour * 6);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_FoodScare".Translate(sick.Count), sick.ToArray());
            // (The wings of the sick talk of it: the rumour mill.)
            Rumours.Notify_FoodScare(map, sick);
            return true;
        }
    }

    public static class FoodScare
    {
        /// <summary>Dirty: at least this much filth, and at least one spot per ten kitchen cells.</summary>
        public const int DirtyKitchen = 3, CellsPerFilth = 10;

        /// <summary>Each inmate on the kitchen crew who passed kitchen safety takes this much off the chance of a scare, up to <see cref="MaxTrainedCrew"/> of them.</summary>
        public const float PerTrainedCrew = 0.15f;
        public const int MaxTrainedCrew = 4;

        /// <summary>Inmates working the kitchen who passed kitchen safety (the kitchen job asks for it, so this is the crew, bar a job given by hand).</summary>
        public static int TrainedCrew(Map map)
        {
            int n = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { job: { } job } c && job.zone == RPLaborDefOf.RP_Kitchen && c.passedPrograms.Contains(RPVocationDefOf.RP_KitchenSafety)) n++;
            return n;
        }

        /// <summary>What the incident's chance is multiplied by: 1 with no trained crew, 0.4 with four or more.</summary>
        public static float SafetyFactor(Map map) => 1f - PerTrainedCrew * Mathf.Min(TrainedCrew(map), MaxTrainedCrew);

        public static List<IntVec3> Kitchen(Map map) => PrisonMap.Of(map)?.zones.CellsOf(RPLaborDefOf.RP_Kitchen) ?? new List<IntVec3>();

        public static int KitchenFilth(Map map)
        {
            int n = 0;
            foreach (IntVec3 c in Kitchen(map))
                foreach (Thing t in c.GetThingList(map))
                    if (t is Filth) n++;
            return n;
        }

        public static bool Dirty(Map map)
        {
            int filth = KitchenFilth(map);
            return filth >= DirtyKitchen && filth * CellsPerFilth >= Kitchen(map).Count;
        }

        /// <summary>Inmates stood down from the kitchen after a scare cannot take kitchen jobs until it is over.</summary>
        public static bool StoodDown(InmateJobDef job) =>
            job.zone == RPLaborDefOf.RP_Kitchen && PrisonGame.Instance is { } g && g.story.kitchenStandDownUntilTick > Find.TickManager.TicksGame;

        /// <summary>Second wave chance: carrying on in a dirty kitchen is the worst bet, standing the crew down halves it.</summary>
        public static float SecondWaveChance(Map map, bool crewStoodDown) =>
            (Dirty(map) ? 0.6f : 0.3f) * (crewStoodDown ? 0.5f : 1f);

        /// <summary>Food poisoning (vanilla) for up to n inmates who ate the kitchen's food and are not already sick (not
        /// the downed or those in the hole); they blame the prison.</summary>
        public static List<Pawn> Poison(Map map, int n)
        {
            List<Pawn> sick = map.mapPawns.PrisonersOfColonySpawned
                .Where(p => InmateUtility.IsActiveInmate(p) && !p.Downed && p.Inmate().forcedActivity == null
                            && !p.health.hediffSet.HasHediff(HediffDefOf.FoodPoisoning))
                .InRandomOrder().Take(n).ToList();
            foreach (Pawn p in sick)
            {
                p.health.AddHediff(HediffDefOf.FoodPoisoning);
                CompInmate c = p.Inmate();
                c.tension = Mathf.Min(100f, c.tension + IncidentWorker_FoodScare.SickTension);
            }
            return sick;
        }

        /// <summary>Hourly: a second wave when it is due and the dice say so.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            StorySystem s = game.story;
            if (s.foodScareTick < 0 || Find.TickManager.TicksGame < s.foodScareTick) return;
            s.foodScareTick = -1;
            if (Kitchen(map).Count == 0 || !Rand.Chance(s.foodScareChance)) return;
            List<Pawn> sick = Poison(map, Rand.RangeInclusive(1, 3));
            if (sick.Count == 0) return;
            game.stats.foodScares++;
            Messages.Message("RP_FoodScareSecondWave".Translate(sick.Count), sick[0], MessageTypeDefOf.NegativeEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FoodScareAgain".Translate(sick.Count), sick.ToArray());
        }
    }

    public class FoodScareDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_FoodScare_Scrub", "RP_FoodScare_StandDown", "RP_FoodScare_CarryOn" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        private static IEnumerable<CompInmate> Crew(Map map) =>
            map.mapPawns.PrisonersOfColonySpawned.Select(p => p.Inmate()).Where(c => c?.job != null && c.job.zone == RPLaborDefOf.RP_Kitchen);

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option == 0 && PrisonGame.Instance.ledger.funds < IncidentWorker_FoodScare.ScrubCost)
                return "RP_CannotAfford".Translate(Money.Format(IncidentWorker_FoodScare.ScrubCost));
            if (option == 1 && !Crew(l.map ?? Find.AnyPlayerHomeMap).Any()) return "RP_FoodScareNoCrew".Translate();
            return null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (option == 0 && !game.ledger.TrySpend(RPDefOf.RP_Supplies, IncidentWorker_FoodScare.ScrubCost, "RP_FoodScareScrubNote".Translate()))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(IncidentWorker_FoodScare.ScrubCost)), MessageTypeDefOf.RejectInput, historical: false);
                option = 2;
            }
            if (option == 0)
            {
                foreach (IntVec3 c in FoodScare.Kitchen(map))
                    foreach (Thing t in c.GetThingList(map).OfType<Filth>().ToList())
                        t.Destroy();
                game.story.foodScareTick = -1;
                return;
            }
            if (option == 1)
            {
                List<CompInmate> crew = Crew(map).ToList();
                game.story.kitchenStandDownUntilTick = Find.TickManager.TicksGame + IncidentWorker_FoodScare.StandDownHours * GenDate.TicksPerHour;
                foreach (CompInmate c in crew)
                {
                    c.job = null;
                    c.jobLocked = false;
                }
                if (crew.Count > 0)
                    Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_CrewStoodDown".Translate(crew.Count), crew.Select(c => (Pawn)c.parent).ToArray());
            }
            game.story.foodScareTick = Find.TickManager.TicksGame + IncidentWorker_FoodScare.SecondWaveHours * GenDate.TicksPerHour;
            game.story.foodScareChance = FoodScare.SecondWaveChance(map, option == 1);
        }
    }
}
