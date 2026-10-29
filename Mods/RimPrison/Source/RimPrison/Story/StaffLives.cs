using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPStaffLifeDefOf
    {
        public static ThoughtDef RP_Rested;
        public static ThoughtDef RP_ManagementListened;
        public static ThoughtDef RP_Valued;
        public static ThoughtDef RP_SendOff;
        public static ThoughtDef RP_LeftToSink;

        static RPStaffLifeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPStaffLifeDefOf));
    }

    /// <summary>The people who work here: burning out, money trouble at home, a veteran ready to hang up the keys.</summary>
    public static class StaffLives
    {
        public const int LeaveDays = 3, DebtAdvance = 400, RetentionBonus = 600, SendOffCost = 250, VeteranDays = 12;

        /// <summary>Permanent staff on site (no riot police, no one already leaving or away).</summary>
        public static IEnumerable<Pawn> OnSite(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => p.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 } sc && !OnLeave(sc) && !CorruptionSystem.Suspended(sc));

        /// <summary>Whom life happens to: permanent staff on site, but not the state's own chief (not the warden's to retire, burn out or lead astray) and not the warden on the floor.</summary>
        public static IEnumerable<Pawn> Storied(Map map) => OnSite(map).Where(p => !p.GetComp<CompStaff>().Protected);

        public static bool OnLeave(CompStaff sc) => sc.leaveUntilTick > Find.TickManager.TicksGame;

        /// <summary>Days in the present role (a retraining starts them again; promotion and the wait for it count these, not the years of service).</summary>
        public static float DaysInRole(CompStaff sc) =>
            sc.hiredTick == CompStaff.NotHired ? 0f : (Find.TickManager.TicksGame - (sc.roleSinceTick >= 0 ? System.Math.Max(sc.hiredTick, sc.roleSinceTick) : sc.hiredTick)) / (float)GenDate.TicksPerDay;

        public static float DaysEmployed(CompStaff sc) => sc.hiredTick == CompStaff.NotHired ? 0f : (Find.TickManager.TicksGame - sc.hiredTick) / (float)GenDate.TicksPerDay;

        public static ChoiceLetter_PrisonDecision Ask(string decision, Pawn staff, TaggedString label, TaggedString text, int hours)
        {
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(decision, label, text, staff.Map, staff);
            letter.pawns.Add(staff);
            letter.StartTimeout(GenDate.TicksPerHour * hours);
            Find.LetterStack.ReceiveLetter(letter);
            return letter;
        }

        /// <summary>Daily: someone left to sink after a burnout warning who is still miserable hands in their notice.</summary>
        public static void Daily(Map map)
        {
            int now = Find.TickManager.TicksGame;
            // Everyone employed, on site or at home: this runs at midnight, when the day shift is at home.
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                if (sc.burnoutIgnoredTick < 0 || sc.Protected || !StaffCareers.Available(sc) || HostageCrisis.IsHeld(p)) continue;
                if (now - sc.burnoutIgnoredTick > 4 * GenDate.TicksPerDay)
                {
                    sc.burnoutIgnoredTick = -1;
                    continue;
                }
                if ((p.needs.mood?.CurLevelPercentage ?? 1f) >= 0.25f) continue;
                sc.burnoutIgnoredTick = -1;
                StaffCareers.Resign(p, sc);
                StaffUtility.Fire(p, record: false);
            }
        }
    }

    /// <summary>Someone on staff is running on empty: send them home for a few days, talk to them, or let them sink.</summary>
    public class IncidentWorker_StaffBurnout : IncidentWorker_Prison
    {
        /// <summary>A lead guard on site keeps the staff going: burnout is a quarter less likely.</summary>
        public override float ChanceFactorNow(IIncidentTarget target) =>
            base.ChanceFactorNow(target) * (target is Map map && StaffCareers.LeadOnSite(map) ? StaffCareers.LeadBurnoutFactor : 1f);

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            if (StaffLives.OnSite(map).Count(x => !WardenSystem.IsWarden(x)) < 3) return false; // (the warden is no one of the three)
            Pawn p = StaffLives.Storied(map).Where(x => x.needs.mood != null && x.needs.mood.CurLevelPercentage < 0.4f && x.GetComp<CompStaff>().burnoutIgnoredTick < 0)
                .OrderBy(x => x.needs.mood.CurLevelPercentage).FirstOrDefault();
            if (p == null) return false;
            CompStaff sc = p.GetComp<CompStaff>();
            StaffLives.Ask("RP_Decision_Burnout", p, "RP_BurnoutLabel".Translate(p.LabelShort), "RP_BurnoutText".Translate(p.LabelShort, StaffCareers.Title(sc), StaffLives.LeaveDays), 12);
            return true;
        }
    }

    public class BurnoutDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Burnout_Leave", "RP_Burnout_Talk", "RP_Burnout_Ignore" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p is not { Dead: false } || p.GetComp<CompStaff>() is not { role: not null } sc) return;
            switch (option)
            {
                case 0:
                    sc.leaveUntilTick = Find.TickManager.TicksGame + StaffLives.LeaveDays * GenDate.TicksPerDay;
                    sc.restPending = true;
                    StaffCareers.InvalidateLeads();
                    Roster.Invalidate();
                    Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_StaffLeave".Translate(p.LabelShort, StaffLives.LeaveDays), p);
                    break;
                case 1:
                    p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_ManagementListened);
                    break;
                default:
                    sc.burnoutIgnoredTick = Find.TickManager.TicksGame;
                    p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_LeftToSink);
                    break;
            }
        }
    }

    /// <summary>
    /// Money trouble at home: a guard is behind on rent. An advance on wages keeps them honest; refusing makes them
    /// easier to buy, and the fixers on the wing notice.
    /// </summary>
    public class IncidentWorker_StaffDebt : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Pawn p = StaffLives.Storied(map).Where(x => GuardUtility.IsGuard(x) && x.GetComp<CompStaff>() is { corrupt: false, debtAsked: false, integrity: >= 0f }).RandomElementWithFallback();
            if (p == null) return false;
            p.GetComp<CompStaff>().debtAsked = true;
            StaffLives.Ask("RP_Decision_StaffDebt", p, "RP_StaffDebtLabel".Translate(p.LabelShort), "RP_StaffDebtText".Translate(p.LabelShort, Money.Format(StaffLives.DebtAdvance)), 24);
            return true;
        }
    }

    public class StaffDebtDecision : PrisonDecisionWorker
    {
        /// <summary>Integrity is 0..100 (fixers approach under 65).</summary>
        public const float AdvanceIntegrity = 15f, RefusedIntegrity = 20f;
        private static readonly string[] Keys = { "RP_StaffDebt_Advance", "RP_StaffDebt_Refuse" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < StaffLives.DebtAdvance ? "RP_CannotAfford".Translate(Money.Format(StaffLives.DebtAdvance)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p is not { Dead: false } || p.GetComp<CompStaff>() is not { role: not null } sc) return;
            if (option == 0)
            {
                PrisonGame.Instance.ledger.Add(RPDefOf.RP_Wages, -StaffLives.DebtAdvance, "RP_StaffDebtNote".Translate(p.LabelShort));
                if (sc.integrity >= 0f) sc.integrity = UnityEngine.Mathf.Min(100f, sc.integrity + AdvanceIntegrity);
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_ManagementListened);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_StaffAdvance".Translate(p.LabelShort), p);
                return;
            }
            if (sc.integrity >= 0f) sc.integrity = UnityEngine.Mathf.Max(0f, sc.integrity - RefusedIntegrity);
            sc.lastApproachTick = -999999; // the fixers hear about it
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_LeftToSink);
        }
    }

    /// <summary>A long-serving officer is thinking of retiring: a send-off, a retention bonus, or let them go quietly.</summary>
    public class IncidentWorker_VeteranRetiring : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            List<Pawn> onSite = StaffLives.Storied(map).ToList();
            if (onSite.Count < 4) return false;
            Pawn p = onSite.Where(x => x.GetComp<CompStaff>() is { retireAsked: false } sc && StaffLives.DaysEmployed(sc) >= StaffLives.VeteranDays)
                .OrderByDescending(x => x.ageTracker.AgeBiologicalYears).FirstOrDefault();
            if (p == null) return false;
            CompStaff sc = p.GetComp<CompStaff>();
            sc.retireAsked = true;
            StaffLives.Ask("RP_Decision_Retire", p, "RP_RetireLabel".Translate(p.LabelShort),
                "RP_RetireText".Translate(p.LabelShort, StaffCareers.Title(sc), StaffLives.DaysEmployed(sc).ToString("0"), Money.Format(StaffLives.SendOffCost), Money.Format(StaffLives.RetentionBonus)), 24);
            return true;
        }
    }

    public class RetireDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Retire_Stay", "RP_Retire_SendOff", "RP_Retire_Quiet" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            int cost = option == 0 ? StaffLives.RetentionBonus : option == 1 ? StaffLives.SendOffCost : 0;
            return cost > 0 && PrisonGame.Instance.ledger.funds < cost ? "RP_CannotAfford".Translate(Money.Format(cost)) : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            // (An off-shift veteran is at home, off the map: the letter still counts. A dead one has nothing to retire from.)
            if (p is not { Dead: false } || p.GetComp<CompStaff>() is not { role: not null } sc) return;
            PrisonGame game = PrisonGame.Instance;
            if (option == 0)
            {
                game.ledger.Add(RPDefOf.RP_Wages, -StaffLives.RetentionBonus, "RP_RetentionNote".Translate(p.LabelShort));
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_Valued);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_StaffStayed".Translate(p.LabelShort), p);
                return;
            }
            if (option == 1)
            {
                game.ledger.Add(RPDefOf.RP_Wages, -StaffLives.SendOffCost, "RP_SendOffNote".Translate(p.LabelShort));
                foreach (Pawn o in StaffLives.OnSite(map))
                    if (o != p) o.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_SendOff, p);
            }
            game.stats.staffRetired++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, (option == 1 ? "RP_Chr_StaffRetiredParty" : "RP_Chr_StaffRetired").Translate(p.LabelShort, StaffCareers.Title(sc), StaffLives.DaysEmployed(sc).ToString("0")), p);
            StaffUtility.Fire(p, record: false);
        }
    }
}
