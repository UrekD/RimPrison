using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPWelfareDefOf
    {
        public static ThoughtDef RP_FamilyDay;
        public static ThoughtDef RP_FamilyDayCancelled;
        public static ThoughtDef RP_SpecialAttention;

        static RPWelfareDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPWelfareDefOf));
    }

    /// <summary>
    /// An inmate's heart gives out (vanilla heart attack). Call the paramedics (they stabilise it) or leave it to the
    /// prison's own doctor; if the inmate dies after the paramedics were refused, it is a death by neglect (inquiry).
    /// </summary>
    public class IncidentWorker_MedicalEmergency : IncidentWorker_Prison
    {
        public const int ParamedicCost = 600;
        public static HediffDef HeartAttack => RPVanillaDefOf.HeartAttack;

        private static BodyPartRecord Heart(Pawn p) => p.RaceProps.body.AllParts.FirstOrDefault(part => part.def == BodyPartDefOf.Heart);

        /// <summary>A natural heart (not artificial), not already failing.</summary>
        private static bool CanHaveOne(Pawn p) =>
            !p.health.hediffSet.HasHediff(HeartAttack) && Heart(p) is { } heart && !p.health.hediffSet.PartIsMissing(heart)
            && !p.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(heart);

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            // Older hearts first.
            Pawn p = RandomInmate(map, x => x.ageTracker.AgeBiologicalYears >= 45 && CanHaveOne(x)) ?? RandomInmate(map, CanHaveOne);
            BodyPartRecord heart = p == null ? null : Heart(p);
            if (heart == null) return false;
            p.health.AddHediff(HeartAttack, heart);
            PrisonGame.Instance.stats.medicalEmergencies++;
            bool doctor = StaffCommute.AllStaff(map).Any(s => s.workSettings?.WorkIsActive(WorkTypeDefOf.Doctor) == true);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Medical", "RP_MedicalLabel".Translate(p.LabelShort),
                "RP_MedicalText".Translate(p.LabelShort, Money.Format(ParamedicCost), (doctor ? "RP_MedicalHaveDoctor" : "RP_MedicalNoDoctor").Translate()), map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerHour * 2);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_HeartAttack".Translate(p.LabelShort), p);
            return true;
        }
    }

    public class MedicalDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Medical_Paramedics", "RP_Medical_OwnDoctor" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < IncidentWorker_MedicalEmergency.ParamedicCost
                ? "RP_CannotAfford".Translate(Money.Format(IncidentWorker_MedicalEmergency.ParamedicCost)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.FirstOrDefault(x => x != null);
            if (p == null || p.Dead || p.Inmate() is not { } c) return;
            if (option == 0)
            {
                PrisonGame.Instance.ledger.Add(RPDefOf.RP_Supplies, -IncidentWorker_MedicalEmergency.ParamedicCost, "RP_ParamedicNote".Translate());
                if (p.health.hediffSet.GetFirstHediffOfDef(IncidentWorker_MedicalEmergency.HeartAttack) is { } h) p.health.RemoveHediff(h);
                c.careRefusedTick = -1;
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_Paramedics".Translate(p.LabelShort), p);
            }
            else c.careRefusedTick = Find.TickManager.TicksGame;
        }
    }

    /// <summary>
    /// A family day: the visiting room opens to families for an afternoon. Everyone with family outside has a good
    /// few days; more visitors means more chances to pass something across (fewer with thorough visitor searches).
    /// </summary>
    public class IncidentWorker_FamilyDay : IncidentWorker_Prison
    {
        public const int Cost = 300;

        protected override int MinInmates => 4;

        private static int Families(Map map) => map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate() is { activeFlag: true, familyCount: > 0 });

        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && PrisonMap.Of((Map)parms.target)?.zones.Any(RPVisitDefOf.RP_Visitation) == true
            && Families((Map)parms.target) >= 2;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            int families = Families(map);
            if (families < 2) return false;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_FamilyDay", "RP_FamilyDayLabel".Translate(),
                "RP_FamilyDayText".Translate(families, Money.Format(Cost), FamilyDays.SmuggleChance(PrisonGame.Instance.visitorSearch).ToStringPercent()), map);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public static class FamilyDays
    {
        public static float SmuggleChance(VisitorSearchPolicy search) => search switch
        {
            VisitorSearchPolicy.Thorough => 0.04f,
            VisitorSearchPolicy.PatDown => 0.1f,
            _ => 0.2f,
        };
    }

    public class FamilyDayDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_FamilyDay_Host", "RP_FamilyDay_Decline" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < IncidentWorker_FamilyDay.Cost ? "RP_CannotAfford".Translate(Money.Format(IncidentWorker_FamilyDay.Cost)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (option == 0) game.ledger.Add(RPDefOf.RP_Wages, -IncidentWorker_FamilyDay.Cost, "RP_FamilyDayNote".Translate());
            float smuggle = FamilyDays.SmuggleChance(game.visitorSearch);
            int visited = 0, smuggled = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (p.Inmate() is not { activeFlag: true, familyCount: > 0 } c) continue;
                if (option == 1)
                {
                    p.needs.mood?.thoughts.memories.TryGainMemory(RPWelfareDefOf.RP_FamilyDayCancelled);
                    continue;
                }
                visited++;
                p.needs.mood?.thoughts.memories.TryGainMemory(RPWelfareDefOf.RP_FamilyDay);
                c.tension = Mathf.Max(0f, c.tension - 10f);
                if (Rand.Chance(smuggle) && ContrabandUtility.GiveSmuggled(p) != null) smuggled++;
            }
            if (option == 0)
            {
                game.stats.familyDays++;
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_FamilyDay".Translate(visited));
                if (AutoTest.Active) RPLog.Message($"[RimPrison:test] family day: {visited} visited, {smuggled} smuggled");
            }
        }
    }

    /// <summary>
    /// An officer and an inmate have grown too close. Move the officer on (a resignation), give a formal warning, or
    /// look the other way: then they are very likely to start carrying for that inmate (corruption, the inmate as
    /// patron).
    /// </summary>
    public class IncidentWorker_TooClose : IncidentWorker_Prison
    {
        protected override int MinInmates => 2;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            List<Pawn> staff = StaffLives.Storied(map).Where(s => GuardUtility.IsGuard(s) && s.GetComp<CompStaff>() is { corrupt: false, exposedCorrupt: false }).ToList();
            if (staff.Count < 2) return false; // never the only guard
            Pawn officer = staff.RandomElement();
            Pawn inmate = RandomInmate(map, x => x.relations.OpinionOf(officer) >= 0);
            if (inmate == null) return false;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_TooClose", "RP_TooCloseLabel".Translate(officer.LabelShort),
                "RP_TooCloseText".Translate(officer.LabelShort, inmate.LabelShort), map, officer);
            letter.pawns.Add(officer);
            letter.pawns.Add(inmate);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class TooCloseDecision : PrisonDecisionWorker
    {
        public const float StartsCarrying = 0.6f;
        private static readonly string[] Keys = { "RP_TooClose_MoveOn", "RP_TooClose_Warn", "RP_TooClose_Ignore" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn officer = l.pawns.FirstOrDefault(x => x?.GetComp<CompStaff>()?.role != null);
            Pawn inmate = l.pawns.FirstOrDefault(x => x != null && x.IsPrisonerOfColony);
            // Off shift (at home) counts too: the decision only touches their record, and Fire handles them at home.
            if (officer == null || officer.Dead || officer.GetComp<CompStaff>() is not { } sc) return;
            switch (option)
            {
                case 0:
                    Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_TooCloseMovedOn".Translate(officer.LabelShort), officer);
                    StaffUtility.Fire(officer, record: false);
                    break;
                case 1:
                    if (sc.integrity >= 0f) sc.integrity = Mathf.Min(100f, sc.integrity + 5f);
                    officer.needs?.mood?.thoughts.memories.TryGainMemory(RPEpisodeDefOf.RP_WrittenUp);
                    break;
                default:
                    if (inmate == null || !InmateUtility.IsActiveInmate(inmate)) return;
                    inmate.needs.mood?.thoughts.memories.TryGainMemory(RPWelfareDefOf.RP_SpecialAttention);
                    if (!Rand.Chance(StartsCarrying)) return;
                    sc.corrupt = true;
                    sc.patron = inmate;
                    sc.patronGang = inmate.Inmate().gang;
                    break;
            }
        }
    }
}
