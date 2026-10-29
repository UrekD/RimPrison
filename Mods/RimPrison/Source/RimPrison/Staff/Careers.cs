using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPCareerDefOf
    {
        public static ThoughtDef RP_Promoted;
        public static ThoughtDef RP_PassedOver;

        static RPCareerDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCareerDefOf));
    }

    /// <summary>Why a member of staff walked out (the order is the order of precedence).</summary>
    public enum ResignReason { Pay, Danger, HatedInmate, Burnout }

    /// <summary>
    /// Careers: every member of staff has a rank (0 officer, 1 senior, 2 lead) that comes with service. A daily check
    /// offers the longest-serving eligible one a promotion (one letter open at a time): +15% of the role's wage per rank
    /// from then on. Being passed over stings and is not offered again for a while. A lead guard on site steadies the
    /// others: guards restrain a little more surely and the staff burn out less. Whoever quits says why.
    /// </summary>
    public static class StaffCareers
    {
        public const int MaxRank = 2, SeniorDays = 10, LeadDays = 25, PassedOverDays = 10, StagnantDays = 25;
        public const float PayPerRank = 0.15f, LeadSubdueBonus = 0.03f, LeadBurnoutFactor = 0.75f;
        /// <summary>Under this mood a member of staff is burnt out right now (the level at which a warned one quits).</summary>
        public const float BurntOutBelow = 0.25f;

        /// <summary>Days of service for a rank.</summary>
        public static int DaysFor(int nextRank) => nextRank <= 1 ? SeniorDays : LeadDays;

        public static string RankLabel(int rank) => ("RP_Rank_" + Mathf.Clamp(rank, 0, MaxRank)).Translate();

        /// <summary>The role label with the rank before it (an officer is just the role).</summary>
        public static string Title(CompStaff sc) =>
            sc.rank > 0 ? "RP_RankedRole".Translate(RankLabel(sc.rank), sc.role.label).Resolve() : sc.role.label;

        public static string TitleCap(CompStaff sc) => Title(sc).CapitalizeFirst();

        /// <summary>The daily wage: the role's wage x (1 + 15% per rank).</summary>
        public static int Wage(CompStaff sc) => WageAt(sc.role, sc.rank);

        /// <summary>The wage of a role at a rank, rounded up so a raise is never under 15%, and a tenth more where the warden agreed to a rise (see <see cref="StaffAgreements"/>; the sum is rounded off first, a float's 44.000004 is not 45).</summary>
        public static int WageAt(StaffRoleDef role, int rank)
        {
            if (role == null) return 0;
            float wage = role.dailyWage * (1f + PayPerRank * rank), rise = StaffAgreements.RiseFor(role);
            return rise <= 0f ? Mathf.CeilToInt(wage) : Mathf.CeilToInt(Mathf.Round(wage * (1f + rise) * 1000f) / 1000f);
        }

        /// <summary>What the handover as it is set now would cost a day for a day guard kept past the end of the shift (a forecast: the settlement pays what was worked, see <see cref="ShiftSystem.AccrueOvertime"/>); none for one who is not working (suspended, on leave).</summary>
        public static int Overtime(CompStaff sc) =>
            ShiftSystem.HandoverHours is > 0 and var hours && ShiftSystem.StaysForHandover(sc) && Available(sc) ? OvertimeFor(sc, hours) : 0;

        public static int OvertimeFor(CompStaff sc, int hours) => hours * OvertimeHour(sc);

        /// <summary>An hour of the handover: 1.5x an hour (a sixteenth of the day's wage), rounded up - what the hourly pass accrues for a guard working it.</summary>
        public static int OvertimeHour(CompStaff sc) => Mathf.CeilToInt(Wage(sc) * Roster.OvertimeRate / Roster.DayShiftHours);

        /// <summary>What the staff cost a day, the handover's overtime as it is set now in (a forecast for the tabs and alerts; the settlement pays the overtime worked).</summary>
        public static int Payroll(List<Pawn> staff) => BasePay(staff) + OvertimePay(staff);

        /// <summary>The wages alone, without the handover's overtime.</summary>
        public static int BasePay(List<Pawn> staff)
        {
            int wages = 0;
            for (int i = 0; i < staff.Count; i++)
                if (staff[i].GetComp<CompStaff>() is { } sc) wages += Wage(sc);
            return wages;
        }

        /// <summary>The overtime the handover as it is set now would cost a day.</summary>
        public static int OvertimePay(List<Pawn> staff)
        {
            int pay = 0;
            for (int i = 0; i < staff.Count; i++)
                if (staff[i].GetComp<CompStaff>() is { } sc) pay += Overtime(sc);
            return pay;
        }

        // ---- Promotion ----------------------------------------------------------------------------------------

        public static bool BurntOut(Pawn p, CompStaff sc) =>
            sc.burnoutIgnoredTick >= 0 || (sc.restPending && StaffLives.OnLeave(sc)) || (p.needs?.mood?.CurLevelPercentage ?? 1f) < BurntOutBelow;

        /// <summary>Employed here and available: not working out their notice (callout staff), not on leave, not suspended.</summary>
        public static bool Available(CompStaff sc) => sc.role != null && sc.leaveAtTick < 0 && !StaffLives.OnLeave(sc) && !CorruptionSystem.Suspended(sc);

        /// <summary>Enough service for the next rank and a clean record (not known corrupt, suspended, burnt out or demoralised).</summary>
        public static bool Eligible(Pawn p, CompStaff sc)
        {
            // (The state's chief is not promoted: they are the state's to post and to recall. Nor is the warden: there is no rank above the post.)
            if (sc.role == null || sc.rank >= MaxRank || sc.leaveAtTick >= 0 || sc.Protected) return false;
            int now = Find.TickManager.TicksGame;
            if (sc.passedOverTick != CompStaff.NotPassedOver && now - sc.passedOverTick < PassedOverDays * GenDate.TicksPerDay) return false;
            // (An upheld complaint puts the next rank off: a week more for each.)
            if (StaffLives.DaysInRole(sc) < DaysFor(sc.rank + 1) + ComplaintSystem.PromotionDelayDays(sc)) return false;
            return !sc.exposedCorrupt && !CorruptionSystem.Suspended(sc) && !BurntOut(p, sc) && StaffMorale.Band(p) != MoraleBand.Demoralised;
        }

        public static bool PromotionOpen()
        {
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is("RP_Decision_Promotion")) return true;
            return false;
        }

        /// <summary>Daily: the eligible member of staff with the longest service gets a promotion letter, if none is open.</summary>
        public static void Daily(Map map)
        {
            if (PromotionOpen()) return;
            Pawn best = null;
            float bestDays = 0f;
            // Everyone employed, on site or at home: the check runs at midnight, when the day shift is at home.
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                if (!Available(sc) || !Eligible(p, sc) || StaffLives.DaysEmployed(sc) <= bestDays) continue;
                best = p;
                bestDays = StaffLives.DaysEmployed(sc);
            }
            if (best == null) return;
            CompStaff c = best.GetComp<CompStaff>();
            int raise = WageAt(c.role, c.rank + 1) - Wage(c);
            ChoiceLetter_PrisonDecision letter = StaffLives.Ask("RP_Decision_Promotion", best, "RP_PromotionLabel".Translate(best.LabelShort),
                "RP_PromotionText".Translate(best.LabelShort, Title(c), StaffLives.DaysEmployed(c).ToString("0"), RankLabel(c.rank + 1), Money.Format(raise)), 24);
            letter.amount = c.rank + 1;
            letter.map = map; // (an officer at home has no map of their own)
        }

        // ---- A lead on site -----------------------------------------------------------------------------------

        private static Map leadMap;
        private static int leadTick, leadCount;

        /// <summary>Forget the cached count (a lead was promoted, fired, killed, sent away or suspended; a game was started or loaded).</summary>
        public static void InvalidateLeads() => leadMap = null; // (not leadTick = int.MinValue: now - leadTick would overflow)

        /// <summary>Lead guards on site (not away, suspended, downed, in a mental state or on strike): recounted once an hour.</summary>
        private static int LeadsOnSite(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (map == leadMap && now >= leadTick && now - leadTick < GenDate.TicksPerHour) return leadCount;
            leadMap = map;
            leadTick = now;
            leadCount = 0;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (IsLeadOnSite(colonists[i])) leadCount++;
            return leadCount;
        }

        private static bool IsLeadOnSite(Pawn p) => p.GetComp<CompStaff>() is { rank: >= MaxRank } && CouldLead(p);

        /// <summary>A guard who would count as a lead on site at the lead's rank: available, not downed, in a mental state or on strike.</summary>
        public static bool CouldLead(Pawn p) =>
            p.GetComp<CompStaff>() is { } sc && Available(sc) && GuardUtility.IsGuard(p) && !p.Downed && !p.InMentalState && !IncidentWorker_StaffStrike.Striking(p);

        public static bool LeadOnSite(Map map) => LeadsOnSite(map) > 0;

        /// <summary>Added to a guard's restraint chance while another lead guard is on site (a lead gets no help from themselves).</summary>
        public static float SubdueBonus(Pawn guard) => LeadsOnSite(guard.Map) - (IsLeadOnSite(guard) ? 1 : 0) > 0 ? LeadSubdueBonus : 0f;

        // ---- Resignation --------------------------------------------------------------------------------------

        /// <summary>A recent injury or death among the colleagues (their own injury shows as being wary of the one who did it).</summary>
        private static bool Frightened(Pawn p) =>
            p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLifeDefOf.RP_ColleagueHurt) != null
            || p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPStaffDeathDefOf.RP_ColleagueKilled) != null;

        public static ResignReason ReasonFor(Pawn p, CompStaff sc)
        {
            // Whoever was taken hostage quits over the danger, whatever else they had against the job.
            if (p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPHostageDefOf.RP_TakenHostage) != null) return ResignReason.Danger;
            if (sc.rank == 0 && StaffLives.DaysInRole(sc) >= StagnantDays) return ResignReason.Pay;
            if (Frightened(p)) return ResignReason.Danger;
            if (sc.waryOfId >= 0 && sc.waryUntilTick > Find.TickManager.TicksGame) return ResignReason.HatedInmate;
            return ResignReason.Burnout;
        }

        /// <summary>
        /// A burnt-out member of staff who was left to sink hands in their notice: the counts, the chronicle and a letter
        /// that says why (the caller sends them off).
        /// </summary>
        public static ResignReason Resign(Pawn p, CompStaff sc)
        {
            PrisonGame game = PrisonGame.Instance;
            ResignReason reason = ReasonFor(p, sc);
            if (reason == ResignReason.Burnout) game.stats.staffBurnedOut++;
            game.stats.resignations++;
            string why = ("RP_ResignReason_" + reason).Translate(sc.waryOfName ?? "?").Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_StaffResigned".Translate(p.LabelShort, Title(sc), why), p);
            Find.LetterStack.ReceiveLetter("RP_ResignLabel".Translate(p.LabelShort), "RP_ResignText".Translate(p.LabelShort, Title(sc), why), LetterDefOf.NegativeEvent, p);
            return reason;
        }
    }

    public class PromotionDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Promotion_Promote", "RP_Promotion_NotNow" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        /// <summary>Still on the staff and staying (not dead, fired or working out their notice).</summary>
        private static Pawn Candidate(ChoiceLetter_PrisonDecision l) =>
            l.pawns.FirstOrDefault(x => x is { Dead: false } && x.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 });

        /// <summary>The record when the letter is answered (it may have been open a day): exposed as corrupt or suspended since.</summary>
        private static bool RecordTainted(Pawn p) => p.GetComp<CompStaff>() is { } sc && (sc.exposedCorrupt || CorruptionSystem.Suspended(sc));

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 0) return null;
            Pawn p = Candidate(l);
            if (p == null) return "RP_OfficerGone".Translate(l.pawns.FirstOrDefault()?.LabelShort ?? "?").ToString();
            return RecordTainted(p) ? "RP_PromotionRecord".Translate(p.LabelShort).ToString() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = Candidate(l);
            // The rank moved while the letter waited (retraining into another role resets it to 0, or a test promoted them): ask again tomorrow.
            if (p == null || p.GetComp<CompStaff>() is not { } sc || sc.rank + 1 != l.amount) return;
            PrisonGame game = PrisonGame.Instance;
            if (option == 0)
            {
                if (RecordTainted(p))
                {
                    Messages.Message("RP_PromotionRecord".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput);
                    return;
                }
                sc.rank++;
                StaffCareers.InvalidateLeads();
                game.stats.promotions++;
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPCareerDefOf.RP_Promoted);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_StaffPromoted".Translate(p.LabelShort, StaffCareers.Title(sc), Money.Format(StaffCareers.Wage(sc))), p);
                return;
            }
            // A letter nobody answered is not a "not now": the officer is not passed over (no block, no thought, no count), and the daily pass offers it again.
            if (l.TimeoutActive && l.TimeoutPassed) return;
            game.stats.promotionsDeclined++;
            sc.passedOverTick = Find.TickManager.TicksGame;
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPCareerDefOf.RP_PassedOver);
        }
    }
}
