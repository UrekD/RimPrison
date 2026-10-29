using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPStaffDeathDefOf
    {
        public static ThoughtDef RP_ColleagueKilled;
        public static ThoughtDef RP_FamilyLookedAfter;
        public static ThoughtDef RP_PrisonDidNotCare;

        static RPStaffDeathDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPStaffDeathDefOf));
    }

    /// <summary>
    /// A member of staff dies: a milestone in the chronicle, every colleague mourns, an inmate who killed them is charged
    /// with murder like any killer inside, and the warden decides whether the prison looks after the family (paid; the
    /// staff see it) or not (they see that too). Callout staff (riot police) are recorded, with no letter.
    /// </summary>
    public static class StaffDeaths
    {
        public const int Compensation = 1500;

        public static void Notify_Died(Pawn staff, CompStaff sc, DamageInfo? dinfo, Hediff culprit)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = staff.MapHeld ?? Find.AnyPlayerHomeMap;
            if (game == null || map == null || sc.role == null) return;
            game.stats.staffDeaths++;
            DangerMap.NoteDeath(staff, dinfo, culprit);
            Kin.Notify_Died(staff);
            UnitOfficers.Unassign(staff, sc);
            StaffCareers.InvalidateLeads();
            Pawn killer = dinfo?.Instigator as Pawn;
            TaggedString how = killer != null && killer != staff
                ? "RP_Chr_StaffKilled".Translate(staff.LabelShort, StaffCareers.Title(sc), killer.LabelShort)
                : "RP_Chr_StaffDied".Translate(staff.LabelShort, StaffCareers.Title(sc), culprit?.def?.label ?? dinfo?.Def?.label ?? "RP_Chr_UnknownCause".Translate().ToString());
            // A callout officer was here a few hours: a lesser line than one of the prison's own.
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, sc.leaveAtTick >= 0 ? 2 : 3, how, staff, killer);
            // A death of neglect (no hand in it: starved, bled out, frozen) is the prison's fault, and their family will say so; one at an inmate's hands is the inmate's, also when the wound bleeds out or festers
            // later and has no damage info (an inmate's blow within the day: see CompStaff.assaultedTick).
            // (The warden has no family to answer to: no bitter letter after a death of neglect either.)
            if (killer == null && culprit != null && !sc.role.warden && DeathAftermath.IsNeglect(culprit.def) && !DeathAftermath.RecentlyAssaulted(sc)) CondolenceSystem.Notify_Fault(staff);
            foreach (Pawn o in StaffCommute.AllStaff(map))
                if (o != staff && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffDeathDefOf.RP_ColleagueKilled, staff);
            // The charge against the inmate who killed them and the question of the family are one decision moment: one letter (a callout officer has no family letter, so the charge goes alone).
            string charge = null;
            if (killer != null && killer.IsPrisonerOfColony && killer.Inmate() is { } kc)
            {
                MurderTrial trial = DeathAftermath.ChargeWithMurder(game, staff, killer, kc, map, staffVictim: true, dinfo: dinfo);
                charge = "RP_MurderStaffText".Translate(killer.LabelShort, staff.LabelShort, Trials.DateText(trial), Trials.KnownLinesText(trial)).Resolve();
            }
            // The warden's death is no family's to compensate: the post is empty, and the state names another.
            if (sc.role.warden)
            {
                WardenSystem.Notify_Died(staff);
                string lost = "RP_WardenDiedText".Translate(staff.LabelShort, how, WardenSystem.SuccessionDays).Resolve();
                Find.LetterStack.ReceiveLetter("RP_WardenDiedLabel".Translate(staff.LabelShort), charge == null ? lost : charge + "\n\n" + lost, LetterDefOf.NegativeEvent, killer != null && killer.Spawned ? new LookTargets(killer) : LookTargets.Invalid);
                return;
            }
            if (sc.leaveAtTick >= 0)
            {
                if (charge != null) Find.LetterStack.ReceiveLetter("RP_MurderLabel".Translate(killer.LabelShort), charge, LetterDefOf.ThreatSmall, killer);
                return; // callout staff: no family letter from this prison
            }
            string text = "RP_StaffDeathText".Translate(staff.LabelShort, StaffCareers.Title(sc), how, Money.Format(Compensation)).Resolve();
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_StaffDeath", "RP_StaffDeathLabel".Translate(staff.LabelShort),
                charge == null ? text : charge + "\n\n" + text, map, charge == null ? LookTargets.Invalid : new LookTargets(killer));
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    public class StaffDeathDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_StaffDeath_Compensate", "RP_StaffDeath_Nothing" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < StaffDeaths.Compensation ? "RP_CannotAfford".Translate(Money.Format(StaffDeaths.Compensation)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            bool paid = option == 0 && PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Wages, StaffDeaths.Compensation, "RP_StaffDeathNote".Translate());
            ThoughtDef felt = paid ? RPStaffDeathDefOf.RP_FamilyLookedAfter : RPStaffDeathDefOf.RP_PrisonDidNotCare;
            foreach (Pawn o in StaffCommute.AllStaff(map ?? Find.AnyPlayerHomeMap))
                if (o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(felt);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, (paid ? "RP_Chr_StaffFamilyPaid" : "RP_Chr_StaffFamilyNothing").Translate());
        }
    }
}
