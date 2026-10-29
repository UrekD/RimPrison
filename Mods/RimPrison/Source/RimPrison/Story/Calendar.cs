using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPCalendarDefOf
    {
        public static ThoughtDef RP_HolidayMeal;
        public static ThoughtDef RP_HolidayInside;
        public static IncidentDef RP_Inspection;

        static RPCalendarDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCalendarDefOf));
    }

    /// <summary>
    /// A state inspection, announced a day ahead and graded on the real report card. Before it: pay for a deep clean
    /// (the filth really goes, and the inspector sees the effort), grease the inspector (a pass, unless it comes out
    /// later), or let them see it as it is.
    /// </summary>
    public static class Inspections
    {
        public const int BribeCost = 800, BribeFine = 3000;
        public const float BribeExposeChance = 0.3f;

        public static int CleanCost(Map map) => 200 + 10 * map.mapPawns.PrisonersOfColonySpawnedCount;

        public static void Hourly(PrisonGame game, Map map)
        {
            StorySystem s = game.story;
            int now = Find.TickManager.TicksGame;
            if (s.bribeExposeTick >= 0 && now >= s.bribeExposeTick)
            {
                s.bribeExposeTick = -1;
                game.reputation = Mathf.Max(0f, game.reputation - 10f);
                game.ledger.Add(RPDefOf.RP_Fines, -BribeFine, "RP_InspectionBribeFineNote".Translate());
                Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_InspectorBribeExposed".Translate());
                // (What the bribed inspector left out of the report, named now.)
                string hidden = s.inspection.hidden.Count == 0 ? "RP_InspectorBribeHiddenNone".Translate().Resolve()
                    : "RP_InspectorBribeHidden".Translate(string.Join("\n", s.inspection.hidden.Select(f => "RP_InspectorBribeHiddenLine".Translate(InspectionReport.Describe(f, map)).Resolve()))).Resolve();
                s.inspection.hidden.Clear();
                Find.LetterStack.ReceiveLetter("RP_InspectorBribeExposedLabel".Translate(), "RP_InspectorBribeExposedText".Translate(Money.Format(BribeFine)) + "\n\n" + hidden, LetterDefOf.NegativeEvent);
            }
            InspectionReport.Hourly(game, map);
            if (s.inspectionTick < 0 || now < s.inspectionTick) return;
            s.inspectionTick = -1;
            Inspect(game, map);
        }

        /// <summary>What the inspector makes of the prison as it stands (no deep clean, no bribe): yesterday's grade weighs in for the part of the day not yet in today's card, less what a deal with a gang's leader costs.</summary>
        public static int Score(PrisonGame game, Map map, ReportCard card)
        {
            float lastGrade = game.lastGrade;
            // (A deal with a gang's leader is a mark against the prison for as long as it runs: see GangDeals.InspectionPenalty.)
            return Mathf.RoundToInt(lastGrade >= 0f ? Mathf.Lerp(lastGrade, card.Overall, GenLocalDate.DayPercent(map)) : card.Overall) - GangDeals.InspectionPenalty;
        }

        /// <summary>
        /// The tour: today's card only covers the hours so far, so yesterday's grade weighs in for the rest of the
        /// last 24 hours. A deep clean shows (+6); a bribed inspector passes anything (and may talk later). The inspector walks a wing, the canteen,
        /// the kitchen and the yard, and writes down what only the warden can put right (<see cref="InspectionReport"/>): the report is in the letter, the findings are lines of the to-do band, and the re-inspection five days on pays for
        /// each one put right and kept so through the last day and fines for each one not. A bribed inspector writes nothing down; what they left out is kept for the exposure letter.
        /// </summary>
        public static void Inspect(PrisonGame game, Map map)
        {
            StorySystem s = game.story;
            ReportCard card = PrisonGrading.Compute(map, game);
            int score = Score(game, map, card);
            if (s.inspectionPrep == 1) score += 6;
            bool bribed = s.inspectionPrep == 2;
            s.inspectionPrep = 0;
            InspectionState book = s.inspection;
            // (What an earlier bribe left out is kept only while its exposure is still to come.)
            if (s.bribeExposeTick < 0) book.hidden.Clear();
            // (A visit that comes with a re-inspection still to come - a dev incident - settles that one first, so its findings are not dropped unpaid.)
            if (book.reinspectTick >= 0) InspectionReport.Settle(game, map);
            List<InspectionFinding> read = InspectionReport.Read(game, map);
            book.noticeKeys = null;
            if (bribed)
            {
                book.hidden = read;
                book.findings = new List<InspectionFinding>();
                book.reinspectTick = -1;
            }
            else
            {
                book.findings = read;
                book.reinspectTick = read.Count > 0 ? Find.TickManager.TicksGame + InspectionReport.ReinspectDays * GenDate.TicksPerDay : -1;
            }
            book.inspector = SpecialMeasures.Visit(map, book.inspector, RPInspectorDefOf.RP_Inspector, InspectionReport.VisitHours, InspectionReport.Stops(map)) ?? book.inspector;
            if (bribed)
            {
                score = Mathf.Max(score, 60);
                if (Rand.Chance(BribeExposeChance)) s.bribeExposeTick = Find.TickManager.TicksGame + Rand.RangeInclusive(2, 5) * GenDate.TicksPerDay;
            }
            game.stats.inspections++;
            int delta = score >= 70 ? 1500 + (score - 70) * 100 : score >= 45 ? 0 : -(1500 + (45 - score) * 80);
            if (delta != 0) game.ledger.Add(delta > 0 ? RPDefOf.RP_Grants : RPDefOf.RP_Fines, delta, "RP_InspectionNote".Translate());
            game.reputation = Mathf.Clamp(game.reputation + (score - 50) / 5f, 0f, 100f);
            string verdict = delta > 0 ? "RP_InspectionGood".Translate(Money.Format(delta)) : delta < 0 ? "RP_InspectionBad".Translate(Money.Format(-delta)) : "RP_InspectionOk".Translate();
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_Inspection".Translate(PrisonGrading.Letter(score), verdict));
            IncidentDef def = RPCalendarDefOf.RP_Inspection;
            Find.LetterStack.ReceiveLetter(def.letterLabel, def.letterText.Formatted(PrisonGrading.Letter(score).Named("GRADE"), card.Describe().Named("CARD"), verdict.Named("VERDICT"),
                    InspectionReport.ReportText(read, bribed, map).Named("REPORT")),
                delta < 0 ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent, book.inspector);
        }

        /// <summary>The overtime crew: every bit of filth in the home area goes.</summary>
        public static int DeepClean(Map map)
        {
            List<Thing> filth = map.listerFilthInHomeArea.FilthInHomeArea.ToList();
            foreach (Thing f in filth) f.Destroy();
            return filth.Count;
        }
    }

    public class IncidentWorker_Inspection : IncidentWorker_Prison
    {
        public const int NoticeHours = 20;

        protected override int MinInmates => 4;

        protected override bool CanFireNowSub(IncidentParms parms) => base.CanFireNowSub(parms) && PrisonGame.Instance.story.inspectionTick < 0 && PrisonGame.Instance.story.inspection.reinspectTick < 0;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            StorySystem s = PrisonGame.Instance.story;
            s.inspectionTick = Find.TickManager.TicksGame + NoticeHours * GenDate.TicksPerHour;
            s.inspectionPrep = 0;
            InspectionReport.Notice(PrisonGame.Instance, map);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Inspection", "RP_InspectionComingLabel".Translate(),
                "RP_InspectionComingText".Translate(NoticeHours, Money.Format(Inspections.CleanCost(map)), Money.Format(Inspections.BribeCost), InspectionReport.ReinspectDays,
                    Money.Format(InspectionReport.GrantPerFixed), Money.Format(InspectionReport.FinePerOpen)), map);
            letter.money = Inspections.CleanCost(map);
            letter.StartTimeout(GenDate.TicksPerHour * (NoticeHours - 2));
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class InspectionDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Inspection_Clean", "RP_Inspection_Bribe", "RP_Inspection_AsIs" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            int cost = option == 0 ? l.money : option == 1 ? Inspections.BribeCost : 0;
            return cost > 0 && PrisonGame.Instance.ledger.funds < cost ? "RP_CannotAfford".Translate(Money.Format(cost)).ToString() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game.story.inspectionTick < 0) return; // already held
            if (option == 0)
            {
                game.ledger.Add(RPDefOf.RP_Wages, -l.money, "RP_InspectionCleanNote".Translate());
                game.story.inspectionPrep = 1;
                Messages.Message("RP_InspectionCleaned".Translate(Inspections.DeepClean(map)), MessageTypeDefOf.PositiveEvent, historical: false);
            }
            else if (option == 1)
            {
                game.ledger.Add(RPDefOf.RP_Fines, -Inspections.BribeCost, "RP_InspectionBribeNote".Translate());
                game.story.inspectionPrep = 2;
            }
        }
    }

    /// <summary>
    /// The winter holiday: a special meal for everyone (a good day, tension down) or an ordinary day (those with
    /// family outside feel it).
    /// </summary>
    public static class Holidays
    {
        /// <summary>Decembary 10th.</summary>
        public const int DayOfYear = 54;
        public const int MealPerHead = 15;

        public static void Daily(PrisonGame game, Map map)
        {
            if (GenLocalDate.DayOfYear(map) != DayOfYear || game.story.lastHolidayYear == GenLocalDate.Year(map)) return;
            game.story.lastHolidayYear = GenLocalDate.Year(map);
            int cost = MealPerHead * map.mapPawns.PrisonersOfColonySpawnedCount;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Holiday", "RP_HolidayLabel".Translate(),
                "RP_HolidayText".Translate(Money.Format(cost)), map);
            letter.money = cost;
            letter.StartTimeout(GenDate.TicksPerHour * 10);
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    public class HolidayDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Holiday_Meal", "RP_Holiday_Ordinary" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < l.money ? "RP_CannotAfford".Translate(Money.Format(l.money)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (option == 0) game.ledger.Add(RPDefOf.RP_Supplies, -l.money, "RP_HolidayMealNote".Translate());
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c) continue;
                if (option == 0)
                {
                    p.needs.mood?.thoughts.memories.TryGainMemory(RPCalendarDefOf.RP_HolidayMeal);
                    c.tension = Mathf.Max(0f, c.tension - 10f);
                }
                else if (c.familyCount > 0) p.needs.mood?.thoughts.memories.TryGainMemory(RPCalendarDefOf.RP_HolidayInside);
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, (option == 0 ? "RP_Chr_HolidayMeal" : "RP_Chr_HolidayOrdinary").Translate());
        }
    }
}
