using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Hunger strike: miserable inmates refuse meals until things improve (mood back above half) or hunger breaks
    /// them. Bad press for the prison.
    /// </summary>
    public static class HungerStrike
    {
        public const float JoinBelowMood = 0.35f;
        public const float EndAboveMood = 0.45f;
        public const float BreakBelowFood = 0.06f;

        public static bool Fasting(CompInmate c) => c != null && c.hungerStrikeUntilTick > Find.TickManager.TicksGame;

        /// <summary>They refuse their meals until <paramref name="until"/> (the strike's start, for each striker), and ask to see the warden about it (<see cref="WardenAsks"/>).</summary>
        public static void Join(Pawn p, int until)
        {
            if (p.Inmate() is not { } c) return;
            c.hungerStrikeUntilTick = until;
            WardenAsks.Raise(p, WardenAskReason.HungerStrike);
        }

        /// <summary>Hourly per inmate: demands met, or hunger wins.</summary>
        public static void Hourly(Pawn p, CompInmate c)
        {
            if (!Fasting(c)) return;
            float mood = p.needs.mood?.CurLevelPercentage ?? 1f;
            float food = p.needs.food?.CurLevelPercentage ?? 1f;
            if (mood < EndAboveMood && food >= BreakBelowFood) return;
            c.hungerStrikeUntilTick = -1;
            Messages.Message((food < BreakBelowFood ? "RP_HungerStrikeBroken" : "RP_HungerStrikeEnded").Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        public static string WorstComplaint(Pawn p)
        {
            if (p.needs.mood == null) return "?";
            var groups = new List<Thought>();
            p.needs.mood.thoughts.GetDistinctMoodThoughtGroups(groups);
            Thought worst = null;
            float worstOffset = 0f;
            foreach (Thought t in groups)
            {
                float o = p.needs.mood.thoughts.MoodOffsetOfGroup(t);
                if (o < worstOffset)
                {
                    worstOffset = o;
                    worst = t;
                }
            }
            return worst?.LabelCap ?? "?";
        }
    }

    public class IncidentWorker_HungerStrike : IncidentWorker_Prison
    {
        protected override int MinInmates => 3;

        private static List<Pawn> Strikers(Map map) =>
            map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && !HungerStrike.Fasting(p.Inmate())
                                                             && p.needs.mood != null && p.needs.mood.CurLevelPercentage < HungerStrike.JoinBelowMood).Take(6).ToList();

        protected override bool CanFireNowSub(IncidentParms parms) => base.CanFireNowSub(parms) && Strikers((Map)parms.target).Count >= 3;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            List<Pawn> strikers = Strikers(map);
            if (strikers.Count < 3) return false;
            int until = Find.TickManager.TicksGame + GenDate.TicksPerDay * 2;
            foreach (Pawn p in strikers) HungerStrike.Join(p, until);
            PrisonGame game = PrisonGame.Instance;
            game.reputation = Mathf.Max(0f, game.reputation - 3f);
            game.stats.hungerStrikes++;
            string names = string.Join("\n", strikers.Select(p => "  - " + p.LabelShort + ": " + HungerStrike.WorstComplaint(p)));
            SendStandardLetter(def.letterLabel, def.letterText.Formatted(strikers.Count.Named("COUNT"), names.Named("NAMES")), def.letterDef, parms, new LookTargets(strikers));
            Story.Record(RPChronicleDefOf.RP_Chr_Violence, 2, "RP_Chr_HungerStrike".Translate(strikers.Count), strikers.ToArray());
            return true;
        }
    }

    /// <summary>
    /// Unhappy staff demand a bonus (one day's wages). Refuse and they down tools for half a day; or agree to fix what they are angry about (see <see cref="StaffAgreements"/>). No demand comes over a grievance
    /// the staff are at peace over (a promise kept) or are waiting on (a promise running).
    /// </summary>
    public class IncidentWorker_StaffStrike : IncidentWorker_Prison
    {
        public const int StrikeHours = 12;

        protected override int MinInmates => 1;

        internal static List<Pawn> Staff(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(Employed).ToList();

        private static bool Employed(Pawn p) => p.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0, Protected: false };

        /// <summary>The staff on the payroll for this purpose wherever they are, on the map or at home between shifts (<see cref="Staff"/> is those on the map): what a statement about the schedule is made over.</summary>
        internal static List<Pawn> EmployedStaff(Map map) => StaffCommute.AllStaff(map).Where(Employed).ToList();

        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!base.CanFireNowSub(parms)) return false;
            // One demand at a time (an unanswered letter is still pending).
            if (StoryLetters.Open().Any(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_StaffStrike"))) return false;
            var map = (Map)parms.target;
            List<Pawn> staff = Staff(map);
            return staff.Count >= 3 && staff.Average(p => p.needs.mood?.CurLevelPercentage ?? 0.5f) < 0.5f && !StaffAgreements.Blocked(Grievance(map, staff));
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            List<Pawn> staff = Staff(map);
            if (staff.Count < 3) return false;
            int bonus = StaffCareers.Payroll(staff);
            StaffGrievance grievance = Grievance(map, staff);
            if (StaffAgreements.Blocked(grievance)) return false;
            // (The demand names no one of its own: the representative is the most senior of the grieving role, and it is they who would be named in the promise.)
            Pawn rep = StaffAgreements.Representative(map, grievance);
            bool bonusOffered = !StaffAgreements.BrokenBefore(grievance);
            string ask = bonusOffered ? "RP_StaffAsk".Translate(Money.Format(bonus)).Resolve() : "RP_StaffAskNoBonus".Translate().Resolve();
            // (The baseline an understaffing promise is measured against is taken here and travels with the letter: what the warden agrees to is what the letter says, however long they take.)
            int[] baselines = grievance == StaffGrievance.Understaffed ? StaffAgreements.RosterBaseline(map) : null;
            string term = rep == null ? "" : StaffAgreements.TermText(grievance, rep.GetComp<CompStaff>().role);
            string because = rep == null ? StaffAgreements.NoRepBecause(map, grievance) : "";
            string offer = rep == null
                ? (bonusOffered ? "RP_StaffOfferNoRep" : "RP_StaffOfferNoRepNoBonus").Translate(bonusOffered ? because.CapitalizeFirst() : because).Resolve()
                : (bonusOffered ? "RP_StaffOffer" : "RP_StaffOfferNoBonus").Translate(rep.LabelShort, term).Resolve();
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_StaffStrike", def.letterLabel,
                def.letterText.Formatted(ask.Named("ASK"), StrikeHours.Named("HOURS"), CauseKey(grievance).Translate().Named("CAUSE"), offer.Named("OFFER")), map, rep != null ? new LookTargets(rep) : null);
            letter.money = bonus;
            letter.note = StaffAgreements.LetterNote(grievance, baselines);
            if (rep != null) letter.pawns.Add(rep);
            letter.StartTimeout(GenDate.TicksPerHour * 6); // no answer is an answer: they walk out
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }

        /// <summary>What the union is actually angry about: too few guards for the inmates, a colleague hurt, nights, or pay.</summary>
        public static StaffGrievance Grievance(Map map, List<Pawn> staff)
        {
            int guards = staff.Count(GuardUtility.IsGuard);
            if (guards == 0 || map.mapPawns.PrisonersOfColonySpawnedCount / (float)guards > 6f) return StaffGrievance.Understaffed;
            if (Find.TickManager.TicksGame - PrisonGame.Instance.story.lastHazardDemandTick < 3 * GenDate.TicksPerDay) return StaffGrievance.Safety;
            if (TooManyOnNights(map)) return StaffGrievance.Nights;
            return StaffGrievance.Pay;
        }

        /// <summary>
        /// The nights grievance: more than half of the staff employed are on the night shift (a promise over it is kept in an hour when this no longer holds). It is the roster's shift (<c>nightShift</c>) of everyone on the payroll,
        /// at home or on the map: who happens to be on the map is the hour's doing (the day staff are home at night), not the schedule's.
        /// </summary>
        public static bool TooManyOnNights(Map map)
        {
            List<Pawn> staff = EmployedStaff(map);
            return staff.Count(p => p.GetComp<CompStaff>().nightShift) * 2 > staff.Count;
        }

        public static string CauseKey(StaffGrievance g) => "RP_StrikeCause_" + g;

        public static string Cause(Map map, List<Pawn> staff) => CauseKey(Grievance(map, staff));

        public static bool Striking(Pawn p) => p.GetComp<CompStaff>() is { } sc && sc.strikeUntilTick > Find.TickManager.TicksGame;

        public static void Strike(Map map)
        {
            int until = Find.TickManager.TicksGame + StrikeHours * GenDate.TicksPerHour;
            foreach (Pawn p in Staff(map))
            {
                p.GetComp<CompStaff>().strikeUntilTick = until;
                // Let a carry or other critical job finish; no new work is taken (see Patch_JobGiver_Work_Strike).
                if (p.CurJob is { } cur && cur.def.casualInterruptible) p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptOptional);
            }
            PrisonGame.Instance.stats.staffStrikes++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_StaffStrike".Translate(StrikeHours));
            Messages.Message("RP_StaffStrikeStarted".Translate(StrikeHours), MessageTypeDefOf.ThreatBig);
            // (Every wing sees the staff down tools: the rumour mill.)
            Rumours.Notify_Strike(map);
        }

        public static void PayBonus(Map map, int bonus)
        {
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Wages, bonus, "RP_StaffBonusNote".Translate()))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(bonus)), MessageTypeDefOf.RejectInput, historical: false);
                Strike(map);
                return;
            }
            foreach (Pawn p in Staff(map)) p.needs.mood?.thoughts.memories.TryGainMemory(RPUnrestDefOf.RP_GotBonus);
        }
    }

    /// <summary>Striking staff take no work at all (enforced at job-giving time, so neither the Work tab nor a role
    /// change ends a strike early), but the fire, the injured and a supermax inmate left outside their cell (see <see cref="SupermaxSystem.IsFetch"/>).</summary>
    [HarmonyLib.HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    public static class Patch_JobGiver_Work_Strike
    {
        /// <summary>
        /// Strikers still put out fires and tend the injured, and see a supermax inmate who is out of their cell with nobody back into it (they stand where they are until a guard comes, for the
        /// twelve hours of the strike otherwise, with the meal in the cell). A guard who is suspended does none of it.
        /// </summary>
        public static void Postfix(JobGiver_Work __instance, Pawn pawn, ref Verse.AI.ThinkResult __result)
        {
            bool suspended = CorruptionSystem.Suspended(pawn), striking = IncidentWorker_StaffStrike.Striking(pawn);
            if (!striking && !suspended) return;
            if (__result.Job != null)
            {
                WorkTypeDef type = __result.Job.workGiverDef?.workType;
                if (type == WorkTypeDefOf.Firefighter || type == WorkTypeDefOf.Doctor) return;
                if (!suspended && SupermaxSystem.IsFetch(__result.Job)) return;
                __result = Verse.AI.ThinkResult.NoJob;
            }
            // (Asked for apart from the scan: whatever the scan found first, a frisk or a restraint that is thrown away above, is not what stood between the inmate and their cell. Only from the
            // ordinary work node: the emergency one comes before eating, resting and the timetable, and a striker is not keener than a guard at work.)
            if (striking && !suspended && !__instance.emergency && SupermaxSystem.FetchJobFor(pawn) is { } fetch) __result = new Verse.AI.ThinkResult(fetch, __instance, Verse.AI.JobTag.MiscWork);
        }
    }

    /// <summary>
    /// The mains go down for a few hours: cameras, metal detectors, lights and machines off, and misconduct twice as
    /// likely in the dark. Lock down until it is back, pay an emergency electrician to cut it short, or ride it out.
    /// </summary>
    public class IncidentWorker_Blackout : IncidentWorker_Prison
    {
        public const int ElectricianCost = 400, ElectricianHours = 1;
        public const float DarkMisconductFactor = 2f;

        protected override bool CanFireNowSub(IncidentParms parms) => base.CanFireNowSub(parms) && UtilitySystem.Active && !PrisonGame.Instance.BlackoutActive;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            int hours = Rand.RangeInclusive(2, 6);
            PrisonGame.Instance.blackoutUntilTick = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            PrisonGame.Instance.stats.blackouts++;
            UtilitySystem.Tick(map);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Blackout", def.letterLabel,
                def.letterText.Formatted(hours.Named("HOURS"), Money.Format(ElectricianCost).Named("COST"), ElectricianHours.Named("FIX")), map);
            letter.StartTimeout(GenDate.TicksPerHour);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_Blackout".Translate(hours));
            return true;
        }
    }

    public class BlackoutDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Blackout_Lockdown", "RP_Blackout_Electrician", "RP_Blackout_RideOut" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 1) return null;
            if (PrisonGame.Instance.blackoutUntilTick - Find.TickManager.TicksGame <= IncidentWorker_Blackout.ElectricianHours * GenDate.TicksPerHour)
                return "RP_ElectricianPointless".Translate();
            return PrisonGame.Instance.ledger.funds < IncidentWorker_Blackout.ElectricianCost
                ? "RP_CannotAfford".Translate(Money.Format(IncidentWorker_Blackout.ElectricianCost)).ToString() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            int left = game.blackoutUntilTick - Find.TickManager.TicksGame;
            if (left <= 0) return; // the power is already back
            if (option == 0)
            {
                // Never shorten a lockdown already running (a riot, a shakedown); end with the power, not an hour later.
                int before = game.lockdownUntilTick;
                game.StartLockdown(left / GenDate.TicksPerHour + 1);
                game.lockdownUntilTick = System.Math.Max(before, game.blackoutUntilTick);
            }
            else if (option == 1)
            {
                game.ledger.Add(RPDefOf.RP_Supplies, -IncidentWorker_Blackout.ElectricianCost, "RP_ElectricianNote".Translate());
                game.blackoutUntilTick = Find.TickManager.TicksGame + System.Math.Min(left, IncidentWorker_Blackout.ElectricianHours * GenDate.TicksPerHour);
            }
        }
    }

    [DefOf]
    public static class RPUnrestDefOf
    {
        public static ThoughtDef RP_GotBonus;

        static RPUnrestDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPUnrestDefOf));
    }
}
