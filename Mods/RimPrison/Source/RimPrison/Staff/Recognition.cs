using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPRecognitionDefOf
    {
        public static ThoughtDef RP_OfficerOfTheMonth;
        public static ThoughtDef RP_ColleagueHonoured;

        static RPRecognitionDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRecognitionDefOf));
    }

    /// <summary>
    /// Every quadrum (15 days), the staff room puts forward an officer of the month: commendations first, then the
    /// one who has been here longest. A bonus and a photo on the wall (and a lift for everyone), or let it pass.
    /// The staff room does not know who is on someone's payroll: neither do you.
    /// </summary>
    public static class OfficerOfTheMonth
    {
        public const int Days = 15, Bonus = 200;

        public static void Daily(PrisonGame game, Map map)
        {
            // Fifteen days after the last one (a missed settlement only delays it a day).
            int day = GenDate.DaysPassed;
            if (game.story.lastOfficerOfMonthDay < 0)
            {
                game.story.lastOfficerOfMonthDay = day;
                return;
            }
            if (day - game.story.lastOfficerOfMonthDay < Days) return;
            game.story.lastOfficerOfMonthDay = day;
            Pawn best = Candidate(map);
            if (best == null) return;
            CompStaff sc = best.GetComp<CompStaff>();
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_OfficerOfMonth", "RP_OfficerOfMonthLabel".Translate(best.LabelShort),
                "RP_OfficerOfMonthText".Translate(best.LabelShort, StaffCareers.Title(sc), sc.commendations, StaffLives.DaysEmployed(sc).ToString("0"), Money.Format(Bonus)), map, best);
            letter.pawns.Add(best);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        public static Pawn Candidate(Map map) =>
            StaffCommute.AllStaff(map).Where(p => p.GetComp<CompStaff>() is { role: not null, Protected: false, leaveAtTick: < 0, exposedCorrupt: false, iaOpen: false } sc && sc.suspicion < 20f)
                .OrderByDescending(p => p.GetComp<CompStaff>().commendations).ThenByDescending(p => StaffLives.DaysEmployed(p.GetComp<CompStaff>())).FirstOrDefault();
    }

    public class OfficerOfMonthDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_OfficerOfMonth_Award", "RP_OfficerOfMonth_Pass" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        /// <summary>Still on the staff and staying (not dead, fired or working out their notice).</summary>
        private static Pawn Officer(ChoiceLetter_PrisonDecision l) =>
            l.pawns.FirstOrDefault(x => x is { Dead: false } && x.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 });

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 0) return null;
            if (Officer(l) == null) return "RP_OfficerGone".Translate(l.pawns.FirstOrDefault()?.LabelShort ?? "?").ToString();
            return PrisonGame.Instance.ledger.funds < OfficerOfTheMonth.Bonus ? "RP_CannotAfford".Translate(Money.Format(OfficerOfTheMonth.Bonus)).ToString() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn officer = Officer(l);
            if (option != 0 || officer == null) return;
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Wages, OfficerOfTheMonth.Bonus, "RP_OfficerOfMonthNote".Translate(officer.LabelShort))) return;
            CompStaff sc = officer.GetComp<CompStaff>();
            if (sc.integrity >= 0f) sc.integrity = Mathf.Min(100f, sc.integrity + 10f);
            officer.needs?.mood?.thoughts.memories.TryGainMemory(RPRecognitionDefOf.RP_OfficerOfTheMonth);
            foreach (Pawn o in StaffCommute.AllStaff(map))
                if (o != officer && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPRecognitionDefOf.RP_ColleagueHonoured);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_OfficerOfMonth".Translate(officer.LabelShort, StaffCareers.Title(sc)), officer);
        }
    }
}
