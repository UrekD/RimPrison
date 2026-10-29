using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPWatchDefOf
    {
        public static MentalStateDef RP_Crisis;
        public static JobDef RP_WatchCheck, RP_WatchCounselling, RP_CrisisResponse;
        public static WorkGiverDef RP_WatchCheckGiver, RP_CrisisResponseGiver;
        public static ThoughtDef RP_OnWatch, RP_Counselled;
        /// <summary>The reservation of a guard going to an inmate on the list (a watch's check, a crisis): one guard at a time, and nobody else's work on the inmate is blocked by it.</summary>
        public static ReservationLayerDef RP_WatchLayer;

        static RPWatchDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPWatchDefOf));
    }

    /// <summary>What happened to an inmate in the last days that can put them at risk (with mood under <see cref="WatchList.LowMood"/> for a day). Saved as a byte: new ones go at the end.</summary>
    public enum RiskTrigger : byte
    {
        None,
        DearJohn,
        FamilyDeath,
        ParoleRefused,
        GreenLight,
        Supermax,
        DebtBeating,
        LifeTerm,
    }

    /// <summary>The warden's answer to the letter (saved as a byte: new ones go at the end).</summary>
    public enum WatchAnswer : byte
    {
        Pending,
        Watch,
        Padded,
        Counselling,
        Nothing,
    }

    /// <summary>Who flagged an inmate as at risk.</summary>
    public enum RiskFlagger : byte
    {
        Psychologist,
        UnitOfficer,
        Guard,
    }

    /// <summary>
    /// Inmates at risk: the watch list (gap analysis 7, item 6). An inmate with a trigger in the last <see cref="TriggerDays"/> days (a Dear John letter, a death in the family, parole refused, a green light, a supermax stay of
    /// more than <see cref="SupermaxDays"/> days, a beating over a debt, a life term begun) whose mood has been under <see cref="LowMood"/> for more than <see cref="LowHours"/> hours is flagged at risk: by the psychologist's
    /// assessment when the prison has one (the session is due: <see cref="AssessmentDue"/>), else by the unit officer of their wing, else by any guard on duty, at the hourly look (<see cref="Hourly"/>; also when the
    /// psychologist has not seen them within <see cref="PsychGraceHours"/> hours). One decision letter a flag (budgeted as any other) offers a watch (a guard checks on them every half hour as care work, their belongings are
    /// taken - the <see cref="RPWatchDefOf.RP_OnWatch"/> mood penalty - and they are out of the yard), a padded cell (only when the psych wing has a free one), counselling sessions (an hour a day of the counsellor's time) or
    /// nothing. The period lasts <see cref="WatchDays"/> days or until the mood recovers above <see cref="RecoveredMood"/>, whichever is first; the warden can end the measure early (Intel: <see cref="EndByWarden"/>), which
    /// leaves them on the list with nothing done.
    /// <para>
    /// A crisis is rare: only for one flagged, answered (or the letter lapsed) and not watched - no watch, or one whose checks are overdue (<see cref="Watched"/>) - whose mood has stayed under <see cref="LowMood"/> for
    /// <see cref="CrisisHours"/> more hours, with a daily chance of <see cref="CrisisDailyChance"/>, and only with the mod setting on (<see cref="CrisesOn"/>). It is a mental state (<see cref="MentalState_Crisis"/>): any member of staff who reaches them ends it (a guard who sees it, or the watch, is sent: <see cref="WorkGiver_CrisisResponse"/>);
    /// one nobody reaches in <see cref="HarmTicks"/> ticks ends in a self-inflicted cut (vanilla injuries, blood loss), harmless in a padded cell of their own. A death of it is the existing neglect inquiry and the family letter.
    /// </para>
    /// Saved on <see cref="CompInmate"/>: <see cref="CompInmate.riskTick"/>, <see cref="CompInmate.watchUntilTick"/>, <see cref="CompInmate.crisisCount"/> and what the watch needs (the last trigger event, the cause, when the
    /// mood went low, the answer, the last check, the last session, when the last period ended).
    /// </summary>
    public static class WatchList
    {
        public const string DecisionDef = "RP_Decision_WatchList";

        /// <summary>A trigger counts for this many days; a supermax stay counts once it is longer than this many.</summary>
        public const int TriggerDays = 3, SupermaxDays = 3;

        /// <summary>Mood under this (0-1) is very low; above <see cref="RecoveredMood"/> the period ends.</summary>
        public const float LowMood = 0.10f, RecoveredMood = 0.30f;

        /// <summary>Hours of very low mood before the flag, and more hours of it after the flag before a crisis can come.</summary>
        public const int LowHours = 24, CrisisHours = 24;

        /// <summary>How long the psychologist's session is waited for before the unit officer or a guard flags them.</summary>
        public const int PsychGraceHours = 12;

        /// <summary>The days a period lasts, and the days after one ends before the same inmate can be flagged again.</summary>
        public const int WatchDays = 3, ReflagDays = 2;

        /// <summary>The watch: a check is due this long after the last one (so that, with the walk there, one comes about every half hour), and never more than <see cref="CheckMostTicks"/> between two; a check is a look this long.</summary>
        public const int CheckEveryTicks = 1000, CheckMostTicks = 1800, LookTicks = 120;

        /// <summary>
        /// How often a due check nobody is on calls a free guard (<see cref="CallFreeGuards"/>, on the map's beat). The rule the two numbers above keep: due, a call within this, the walk, the look, all within
        /// <see cref="CheckMostTicks"/> - so a guard on duty within 430 ticks' walk of the inmate (about 30 cells at a walk) keeps the watch; one further off, busy or off shift lets it lapse.
        /// </summary>
        public const int CallEveryTicks = 250;

        /// <summary>Counselling: one session of an hour a day.</summary>
        public const int SessionTicks = GenDate.TicksPerHour, SessionEveryTicks = GenDate.TicksPerDay;

        /// <summary>The daily chance of a crisis for one who may have one.</summary>
        public const float CrisisDailyChance = 0.10f;

        /// <summary>A crisis nobody reaches ends in harm after this many ticks; a member of staff within this many cells (in the room, or beside them) reaches them.</summary>
        public const int HarmTicks = 2000;
        public const float ReachRadius = 2.9f;

        /// <summary>The cuts (severity each, how many): about 1.4 of blood a day, a death in some sixteen hours if nobody tends them.</summary>
        public const float CutSeverity = 12f;
        public const int Cuts = 2;

        /// <summary>The mood the counselling memory gives, and the tension a session takes off.</summary>
        public const float SessionTension = 10f;

        /// <summary>At most this many letters in one hourly pass (the rest wait for the next hour).</summary>
        public const int MaxLettersPerPass = 2;

        public static float HourlyCrisisChance => 1f - Mathf.Pow(1f - CrisisDailyChance, 1f / 24f);

        // ---- for the checks ----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>For a check: whether a psychologist decides (null: whether the prison has one).</summary>
        internal static bool? PsychOverride;

        /// <summary>For a check: the mod setting as it should read (null: the setting).</summary>
        internal static bool? SettingOverride;

        /// <summary>For a check: whether the hourly crisis roll comes up (null: the dice).</summary>
        internal static bool? CrisisRoll;

        /// <summary>For a check: the inmates the hourly pass looks at, and the guards that may flag (null: all).</summary>
        internal static Func<Pawn, bool> Pool, GuardPool;

        private static int Now => Find.TickManager.TicksGame;

        private const int Hour = GenDate.TicksPerHour, Day = GenDate.TicksPerDay;

        /// <summary>The mod setting "Self-harm crises": off, the flag, the letter and the watch still happen, and no crisis ever starts.</summary>
        public static bool CrisesOn => SettingOverride ?? RimPrisonMod.Settings?.selfHarmCrises ?? true;

        // ---- the state -----------------------------------------------------------------------------------------------------------------------------------------------

        public static bool OnList(CompInmate c) => c != null && c.riskTick >= 0;

        /// <summary>On the list and answered with a watch, the period not over.</summary>
        public static bool OnWatch(CompInmate c) => c != null && c.riskTick >= 0 && c.watchAnswer == WatchAnswer.Watch && Now < c.watchUntilTick;

        /// <summary>
        /// On watch and the watch kept: the last check, or the start of the watch, no more than <see cref="CheckMostTicks"/> ago. A watch the guards do not get to (nobody on duty, nobody who can reach the cell) is no
        /// watch: the crisis rules are those of one left alone until a check comes.
        /// </summary>
        public static bool Watched(CompInmate c, int now) => OnWatch(c) && Mathf.Max(c.watchCheckTick, c.measureTick) >= 0 && now - Mathf.Max(c.watchCheckTick, c.measureTick) <= CheckMostTicks;

        public static bool InCrisis(Pawn p) => p != null && p.MentalStateDef != null && p.MentalStateDef == RPWatchDefOf.RP_Crisis;

        public static float Mood(Pawn p) => p.needs?.mood?.CurLevel ?? 1f;

        /// <summary>A check of the watch is due: none yet (-1), half an hour since the last, or at once when they are in a crisis.</summary>
        public static bool CheckDue(Pawn p) => p.Spawned && p.Inmate() is { activeFlag: true } c && OnWatch(c) && (c.watchCheckTick < 0 || Now - c.watchCheckTick >= CheckEveryTicks || InCrisis(p));

        /// <summary>
        /// The most recent trigger within <see cref="TriggerDays"/> days (and when), or none: the last event the hooks noted (a letter, a death in the family, a green light, a beating), a parole refusal, a life term begun (their
        /// admission as a lifer), a supermax stay of more than <see cref="SupermaxDays"/> days (now, while it lasts).
        /// </summary>
        public static RiskTrigger Trigger(Pawn p, CompInmate c, int now, out int tick)
        {
            RiskTrigger best = RiskTrigger.None;
            int at = int.MinValue, window = TriggerDays * Day;
            bool Fresh(int t) => t >= 0 && now >= t && now - t <= window && t > at;
            if (c.riskEvent != RiskTrigger.None && Fresh(c.riskEventTick)) (best, at) = (c.riskEvent, c.riskEventTick);
            if (Fresh(c.paroleDenialTick)) (best, at) = (RiskTrigger.ParoleRefused, c.paroleDenialTick);
            if (c.IsLifer && DeathRowSystem.Ext(c) == null && Fresh(c.admittedTick)) (best, at) = (RiskTrigger.LifeTerm, c.admittedTick);
            // (A supermax stay counts while it lasts, from the day it passed the days: a fresher event is named before it.)
            int longStay = c.lastReclassTick + SupermaxDays * Day;
            if (SupermaxSystem.IsIn(c) && now - c.lastReclassTick > SupermaxDays * Day && (best == RiskTrigger.None || longStay > at)) (best, at) = (RiskTrigger.Supermax, longStay);
            tick = at;
            return best;
        }

        /// <summary>Not on the list (nor just off it), mood under <see cref="LowMood"/> for more than <see cref="LowHours"/> hours, and a trigger: the flag is due.</summary>
        public static bool Qualifies(Pawn p, CompInmate c, int now) =>
            c.riskTick < 0 && c.lowMoodTick >= 0 && now - c.lowMoodTick > LowHours * Hour && (c.riskEndTick < 0 || now - c.riskEndTick >= ReflagDays * Day) && Mood(p) < LowMood
            && Trigger(p, c, now, out _) != RiskTrigger.None;

        /// <summary>The psychologist's session is due to flag them (<see cref="PsychologyUtility.WantsSession"/>).</summary>
        public static bool AssessmentDue(CompInmate c) => c.riskTick < 0 && c.lowMoodTick >= 0 && c.Pawn is { } p && Qualifies(p, c, Now);

        /// <summary>A psychologist on the staff (not away) flags inmates at their assessment.</summary>
        public static bool PsychDecides(Map map)
        {
            if (PsychOverride is { } forced) return forced;
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>() is { } sc && sc.role == RPStaffRoleDefOf.RP_Psychologist && StaffCareers.Available(sc)) return true;
            return false;
        }

        // ---- the triggers --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>An event that can put an inmate at risk happened to them now (the hooks: a Dear John letter, a death in the family, a green light, a beating over a debt).</summary>
        public static void Notify_Trigger(Pawn p, RiskTrigger trigger)
        {
            if (p?.Inmate() is not { } c || trigger == RiskTrigger.None) return;
            (c.riskEvent, c.riskEventTick) = (trigger, Now);
        }

        // ---- the hourly look -----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Hourly (one pass over the inmates, a few field reads each): the clock of very low mood; for those on the list, the end of the period (time, or the mood above <see cref="RecoveredMood"/>) and the crisis roll; for the
        /// others, the flag when it is due and no psychologist will make it.
        /// </summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            if (map == null) return;
            int now = Now, letters = 0;
            bool? psych = null;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.ToList();
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (Pool != null && !Pool(p) || p.Dead || p.Inmate() is not { activeFlag: true } c || p.needs?.mood == null) continue;
                float mood = p.needs.mood.CurLevel;
                if (mood < LowMood)
                {
                    if (c.lowMoodTick < 0) c.lowMoodTick = now;
                }
                else c.lowMoodTick = -1;
                if (c.riskTick >= 0)
                {
                    Look(p, c, now, mood);
                    continue;
                }
                if (letters >= MaxLettersPerPass || ShuttleSystem.OnTheWayOut(p) || !Qualifies(p, c, now)) continue;
                psych ??= PsychDecides(map);
                // (The psychologist's assessment flags them: the session is due. Not seen in the grace hours, the officer or a guard does.)
                if (psych.Value && now - c.lowMoodTick <= (LowHours + PsychGraceHours) * Hour) continue;
                if (FlaggerFor(map, c, out RiskFlagger kind) is { } by && Flag(p, c, map, by, kind) != null) letters++;
            }
        }

        private static void Look(Pawn p, CompInmate c, int now, float mood)
        {
            if (ShuttleSystem.OnTheWayOut(p)) End(p, c, "RP_Watch_EndGone");
            else if (now >= c.watchUntilTick) End(p, c, "RP_Watch_EndTime");
            else if (mood > RecoveredMood && !InCrisis(p)) End(p, c, "RP_Watch_EndMood");
            else if (CrisisDue(p, c, now) && (CrisisRoll ?? Rand.Chance(HourlyCrisisChance))) TryStartCrisis(p, c);
        }

        /// <summary>Who flags them now (null: nobody can): the unit officer of their wing, serving and on the map, else any guard on duty on site.</summary>
        public static Pawn FlaggerFor(Map map, CompInmate c, out RiskFlagger kind)
        {
            kind = RiskFlagger.UnitOfficer;
            if (UnitOfficers.HolderOf(map, c.security) is { Spawned: true, Downed: false, Dead: false } officer && officer.Map == map && officer.GetComp<CompStaff>() is { } sc && UnitOfficers.Serving(sc)
                && (GuardPool == null || GuardPool(officer)) && officer.Awake() && !officer.InMentalState && !HostageCrisis.IsHeld(officer)) return officer;
            kind = RiskFlagger.Guard;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn g = colonists[i];
                if ((GuardPool == null || GuardPool(g)) && CareDetail.Why(g) == null && !g.Drafted && !g.InMentalState) return g;
            }
            return null;
        }

        // ---- the flag and the letter ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The psychologist's session (<see cref="PsychologyUtility.Complete"/>): an inmate due the flag is flagged by the psychologist.</summary>
        public static void Notify_Session(Pawn psychologist, Pawn p, CompInmate c)
        {
            if (c == null || p.MapHeld is not { } map || !Qualifies(p, c, Now)) return;
            Flag(p, c, map, psychologist, RiskFlagger.Psychologist);
        }

        /// <summary>Puts them on the list (the period starts now) and writes the one letter of the flag.</summary>
        public static ChoiceLetter_PrisonDecision Flag(Pawn p, CompInmate c, Map map, Pawn by, RiskFlagger kind)
        {
            int now = Now;
            RiskTrigger cause = Trigger(p, c, now, out _);
            (c.riskTick, c.watchUntilTick, c.riskCause, c.watchAnswer, c.watchCheckTick, c.counselTick) = (now, now + WatchDays * Day, cause, WatchAnswer.Pending, -1, -1);
            string byText = kind switch
            {
                RiskFlagger.Psychologist => "RP_Watch_ByPsych".Translate(by.LabelShort, p.LabelShort).Resolve(),
                RiskFlagger.UnitOfficer => "RP_Watch_ByOfficer".Translate(by.LabelShort, p.LabelShort, UnitOfficers.WingLabel(c.security)).Resolve(),
                _ => "RP_Watch_ByGuard".Translate(by.LabelShort, p.LabelShort).Resolve(),
            };
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(DecisionDef, "RP_WatchLabel".Translate(p.LabelShort), LetterText(p, c, map, byText, cause), map, p);
            letter.pawns.Add(p);
            letter.subjectId = p.thingIDNumber;
            // (The flag the letter is about: a letter from an earlier period decides nothing about this one.)
            letter.amount = now;
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_AtRisk".Translate(p.LabelShort, CauseText(cause), by.LabelShort), p);
            return letter;
        }

        public static string CauseText(RiskTrigger cause) =>
            cause == RiskTrigger.Supermax ? "RP_WatchCause_Supermax".Translate(SupermaxDays).Resolve() : ("RP_WatchCause_" + cause).Translate().Resolve();

        /// <summary>The mood penalty of the watch as the letter states it (the thought's own number).</summary>
        public static float WatchPenalty => RPWatchDefOf.RP_OnWatch?.stages is { Count: > 0 } stages ? stages[0].baseMoodEffect : 0f;

        public static string LetterText(Pawn p, CompInmate c, Map map, string byText, RiskTrigger cause)
        {
            // (The reason a padded cell cannot be had, its first sentence: the rest is the psych wing's own advice.)
            // (Free: for the period only, unless the psych wing has a case of its own for them, see LeavePadded.)
            string padded = PsychWing.CannotMove(p, c) is { } no ? "RP_Watch_PaddedNo".Translate(no.Split(new[] { ". " }, StringSplitOptions.None)[0].TrimEnd('.').UncapitalizeFirst()).Resolve()
                : PsychWing.CaseFor(p, c) != null ? "RP_Watch_PaddedCase".Translate().Resolve() : "RP_Watch_PaddedYes".Translate().Resolve();
            Pawn counsellor = Counsellor(map);
            string counsel = counsellor != null ? "RP_Watch_CounselYes".Translate(counsellor.LabelShort, WatchDays * SessionTicks / Hour).Resolve() : "RP_Watch_CounselNo".Translate().Resolve();
            string crisis = CrisesOn ? "\n\n" + "RP_Watch_CrisisRisk".Translate(CrisisDailyChance.ToStringPercent()).Resolve() : "";
            return "RP_WatchText".Translate(p.LabelShort, byText, CauseText(cause), Mood(p).ToStringPercent(), WatchDays, WatchPenalty.ToString("0"), padded, counsel, crisis, RecoveredMood.ToStringPercent()).Resolve();
        }

        /// <summary>Who gives the counselling: a counsellor on the staff, else the psychologist (null: nobody can).</summary>
        public static Pawn Counsellor(Map map)
        {
            if (map == null) return null;
            Pawn psych = null;
            foreach (Pawn s in StaffCommute.AllStaff(map))
            {
                if (s.GetComp<CompStaff>() is not { } sc || !StaffCareers.Available(sc)) continue;
                if (sc.role == RPProgramDefOf.RP_Counsellor) return s;
                if (sc.role == RPStaffRoleDefOf.RP_Psychologist) psych ??= s;
            }
            return psych;
        }

        // ---- the answers ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The warden's answer; false when it cannot be done (nothing changes, the caller records nothing instead).</summary>
        public static bool Answer(Pawn p, CompInmate c, WatchAnswer answer, Map map)
        {
            if (c.riskTick < 0) return false;
            int now = Now;
            switch (answer)
            {
                case WatchAnswer.Watch:
                    // (The first check is due at once, -1 being none yet, and the watch is kept from now: overdue once CheckMostTicks pass with no check. Out of the yard from now.)
                    (c.watchAnswer, c.watchCheckTick, c.measureTick) = (WatchAnswer.Watch, -1, now);
                    InmateUtility.InterruptForRegimeChange(p);
                    break;
                case WatchAnswer.Padded:
                    if (PsychWing.Level == null || PsychWing.CannotMove(p, c) != null) return false;
                    SecurityLevelDef from = c.security;
                    // (A case of their own for the psych wing - two breaks, a trait - and the move is the wing's: it lasts. Else it is the period's, given up at its end.)
                    bool ownCase = PsychWing.CaseFor(p, c) != null;
                    if (!InmateUtility.ChangeSecurity(p, c, PsychWing.Level, why: "RP_Watch_PaddedWhy".Translate()) || c.security != PsychWing.Level) return false;
                    c.psychFrom = from;
                    (c.watchAnswer, c.measureTick) = (WatchAnswer.Padded, ownCase ? -1 : c.lastReclassTick);
                    break;
                case WatchAnswer.Counselling:
                    if (Counsellor(map) == null) return false;
                    (c.watchAnswer, c.counselTick, c.measureTick) = (WatchAnswer.Counselling, -1, now);
                    break;
                default:
                    (c.watchAnswer, c.measureTick) = (WatchAnswer.Nothing, -1);
                    break;
            }
            return true;
        }

        /// <summary>
        /// Off the list: the period is over (time, the mood recovered, or they are gone). A padded cell given for the period is given up (<see cref="LeavePadded"/>). <paramref name="whyKey"/>: a chronicle line ({0} the
        /// inmate), or null for none.
        /// </summary>
        public static void End(Pawn p, CompInmate c, string whyKey)
        {
            if (c.riskTick < 0) return;
            bool watched = OnWatch(c);
            if (whyKey != "RP_Watch_EndGone") LeavePadded(p, c);
            (c.riskTick, c.watchUntilTick, c.watchAnswer, c.watchCheckTick, c.counselTick, c.riskCause, c.riskEndTick, c.measureTick) = (-1, -1, WatchAnswer.Pending, -1, -1, RiskTrigger.None, Now, -1);
            CloseLetters(p);
            if (whyKey != null && whyKey != "RP_Watch_EndGone") Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, whyKey.Translate(p.LabelShort), p);
            // (Back to the yard with the others.)
            if (watched && p.Spawned) InmateUtility.InterruptForRegimeChange(p);
        }

        /// <summary>Whether the warden has a measure to end (Intel's "End"): a watch, counselling, or a padded cell given for the period.</summary>
        public static bool CanEndMeasure(CompInmate c) => c != null && c.riskTick >= 0 && c.watchAnswer is WatchAnswer.Watch or WatchAnswer.Counselling or WatchAnswer.Padded;

        /// <summary>
        /// The warden ends the measure (Intel): the watch or the counselling stop, a padded cell given for the period is given up, and the yard and their things come back. They stay on the list, at risk, until the
        /// period ends (the time, or the mood back above <see cref="RecoveredMood"/>), as one nothing was done for: the crisis rules apply. False when there is no measure to end.
        /// </summary>
        public static bool EndByWarden(Pawn p)
        {
            if (p?.Inmate() is not { } c || !CanEndMeasure(c)) return false;
            string was = AnswerText(c);
            bool watched = OnWatch(c);
            LeavePadded(p, c);
            (c.watchAnswer, c.watchCheckTick, c.counselTick, c.measureTick) = (WatchAnswer.Nothing, -1, -1, -1);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Watch_EndWarden".Translate(p.LabelShort, was), p);
            Messages.Message("RP_Watch_Ended".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
            // (Back to the yard with the others.)
            if (watched && p.Spawned) InmateUtility.InterruptForRegimeChange(p);
            return true;
        }

        /// <summary>
        /// A padded cell given for the period (the move of <see cref="CompInmate.measureTick"/>, nobody has moved them since, and the psych wing has found no case of its own in the meantime): back to the level they came
        /// from. With no room there they stay, told, and the psych wing's own way out applies.
        /// </summary>
        private static void LeavePadded(Pawn p, CompInmate c)
        {
            if (c.watchAnswer != WatchAnswer.Padded || c.measureTick < 0 || !PsychWing.IsIn(c) || c.lastReclassTick != c.measureTick || !p.Spawned || PsychWing.CaseFor(p, c) != null) return;
            if (PsychWing.StepOutLevel(c) is not { } back || back == PsychWing.Level) return;
            if (!InmateUtility.HasRoomIn(p, back))
            {
                Messages.Message("RP_Watch_PaddedStays".Translate(p.LabelShort, back.label), p, MessageTypeDefOf.CautionInput, historical: false);
                return;
            }
            InmateUtility.ChangeSecurity(p, c, back, why: "RP_Watch_PaddedBackWhy".Translate());
        }

        /// <summary>They left for good (released, transferred, dead): the letters about them still open are closed.</summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p?.Inmate() is not { } c) return;
            if (c.riskTick >= 0) (c.riskTick, c.watchUntilTick, c.watchAnswer, c.watchCheckTick, c.counselTick, c.riskCause, c.measureTick) = (-1, -1, WatchAnswer.Pending, -1, -1, RiskTrigger.None, -1);
            CloseLetters(p);
        }

        private static void CloseLetters(Pawn p)
        {
            bool Open(ChoiceLetter_PrisonDecision l) => l is { answered: false } && l.Is(DecisionDef) && (l.subjectId == p.thingIDNumber || l.pawns.Contains(p));
            foreach (ChoiceLetter_PrisonDecision open in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(Open).ToList())
            {
                open.answered = true;
                Find.LetterStack.RemoveLetter(open);
            }
            if (PrisonGame.Instance?.story.deferredLetters is { } waiting)
                foreach (ChoiceLetter_PrisonDecision held in waiting.Where(Open).ToList())
                {
                    held.answered = true;
                    waiting.Remove(held);
                }
        }

        // ---- the watch and the counselling ----------------------------------------------------------------------------------------------------------------------------

        /// <summary>A guard looked in on them (the watch's check): the clock starts again, and a crisis ends.</summary>
        public static void Notify_Checked(Pawn guard, Pawn p)
        {
            if (p.Inmate() is not { } c) return;
            c.watchCheckTick = Now;
            if (InCrisis(p)) Interrupt(p, guard);
        }

        public static bool CounselDue(Pawn p) =>
            p.Inmate() is { activeFlag: true } c && c.riskTick >= 0 && c.watchAnswer == WatchAnswer.Counselling && (c.counselTick < 0 || Now - c.counselTick >= SessionEveryTicks) && PsychologyUtility.CanTalk(p);

        /// <summary>A counselling session done: the day's is had, a better mood for a day, a little calmer.</summary>
        public static void Notify_Counselled(Pawn counsellor, Pawn p)
        {
            if (p.Inmate() is not { } c) return;
            c.counselTick = Now;
            c.tension = Mathf.Max(0f, c.tension - SessionTension);
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPWatchDefOf.RP_Counselled);
            counsellor?.skills?.Learn(SkillDefOf.Social, 200f);
        }

        // ---- the crisis ----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A crisis may come now: flagged, the letter answered (or lapsed: none while it waits on the desk or is held back by the day's budget), not watched (no watch, or one whose checks are overdue: <see cref="Watched"/>), mood
        /// under <see cref="LowMood"/> for <see cref="CrisisHours"/> hours since the flag (or since it last went low), the setting on, and able to have one.
        /// </summary>
        public static bool CrisisDue(Pawn p, CompInmate c, int now) =>
            CrisesOn && c.riskTick >= 0 && c.watchAnswer != WatchAnswer.Pending && !Watched(c, now) && c.lowMoodTick >= 0 && Mood(p) < LowMood && now - Mathf.Max(c.riskTick, c.lowMoodTick) >= CrisisHours * Hour
            && CanHaveCrisis(p);

        public static bool CanHaveCrisis(Pawn p) =>
            p.Spawned && !p.Dead && !p.Downed && p.Awake() && !p.InMentalState && !HostageCrisis.IsHeld(p) && !ShuttleSystem.OnTheWayOut(p) && !EvacuationSystem.Keeps(p);

        /// <summary>Starts a crisis (never with the setting off). <paramref name="forced"/>: whatever else they are doing (a check's own).</summary>
        public static bool TryStartCrisis(Pawn p, CompInmate c, bool forced = false)
        {
            if (!CrisesOn || p == null || c == null || RPWatchDefOf.RP_Crisis == null) return false;
            if (!forced && !CanHaveCrisis(p)) return false;
            if (!p.mindState.mentalStateHandler.TryStartMentalState(RPWatchDefOf.RP_Crisis, null, forced: true, forceWake: forced)) return false;
            c.crisisCount++;
            return true;
        }

        /// <summary>A member of staff who has reached them (within <see cref="ReachRadius"/> in their room, or in its doorway beside them), awake and able; null when nobody has.</summary>
        public static Pawn Reacher(Pawn p)
        {
            if (!p.Spawned) return null;
            Room room = p.GetRoom();
            IReadOnlyList<Pawn> colonists = p.Map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn s = colonists[i];
                if (s == p || !s.Position.InHorDistOf(p.Position, ReachRadius) || s.GetComp<CompStaff>()?.role == null || s.Downed || !s.Awake() || s.InMentalState || HostageCrisis.IsHeld(s)) continue;
                // (In their room, or in its doorway beside them: not through a wall.)
                if (s.GetRoom() == room || s.Position.GetDoor(p.Map) != null && s.Position.AdjacentTo8WayOrInside(p.Position)) return s;
            }
            return null;
        }

        /// <summary>Somebody reached them in time: the crisis is over, nobody is hurt.</summary>
        public static void Interrupt(Pawn p, Pawn by)
        {
            if (!InCrisis(p)) return;
            p.MentalState.RecoverFromState();
            if (p.Inmate() is { } c && OnWatch(c)) c.watchCheckTick = Now;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_CrisisInterrupted".Translate(by.LabelShort, p.LabelShort), by, p);
            Messages.Message("RP_Watch_Interrupted".Translate(by.LabelShort, p.LabelShort), p, MessageTypeDefOf.PositiveEvent, historical: false);
        }

        /// <summary>Nobody came: they harmed themself (vanilla cuts on the arms, bleeding). In the padded cell of their own bed nothing happens. Returns whether they were hurt.</summary>
        public static bool Harm(Pawn p)
        {
            if (!p.Spawned || p.Dead || !CrisesOn) return false;
            if (PaddedCells.InOwnCell(p, out _))
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_CrisisPadded".Translate(p.LabelShort), p);
                Messages.Message("RP_Watch_Padded".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
                return false;
            }
            List<BodyPartRecord> parts = p.health.hediffSet.GetNotMissingParts().Where(x => x.def == BodyPartDefOf.Arm).ToList();
            if (parts.Count == 0) parts = p.health.hediffSet.GetNotMissingParts().Where(x => x.def == BodyPartDefOf.Leg).ToList();
            if (parts.Count == 0) return false;
            bool hurt = false;
            for (int i = 0; i < Cuts; i++)
            {
                BodyPartRecord part = parts[i % parts.Count];
                float severity = Mathf.Min(CutSeverity, p.health.hediffSet.GetPartHealth(part) - 1f);
                if (severity <= 0f) continue;
                var cut = (Hediff_Injury)HediffMaker.MakeHediff(HediffDefOf.Cut, p, part);
                cut.Severity = severity;
                p.health.AddHediff(cut, part);
                hurt = true;
            }
            if (!hurt) return false;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_CrisisHarmed".Translate(p.LabelShort), p);
            Find.LetterStack.ReceiveLetter("RP_Watch_HarmedLabel".Translate(p.LabelShort), "RP_Watch_HarmedText".Translate(p.LabelShort), LetterDefOf.NegativeEvent, p);
            return true;
        }

        // ---- the guards called from their leisure --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The watch's checks and the crisis calls are emergency work: a guard takes them before recreation and whatever the timetable says, but only when they next choose what to do, and a guard at leisure does not choose again
        /// until the leisure is over. A meditation lasts 5000 ticks (vanilla's other recreation a few thousand, a staff-room break 2000, and the guard kept on site off shift has all their sleeping hours for it), while a check is
        /// overdue <see cref="CheckMostTicks"/> - <see cref="CheckEveryTicks"/> ticks after it falls due and a crisis harms in <see cref="HarmTicks"/>: the guard did one check and meditated through the next ones.
        /// At the map's beat, for each due check or crisis call nobody is on, the guard at leisure (<see cref="LeisureRank"/>) who would be given it - the idle and the patrolling first, then recreation, a staff-room break
        /// last, the nearest of each - is made to choose again, and the think tree's emergency work gives it to them.
        /// </summary>
        public static void CallFreeGuards(Map map)
        {
            List<Thing> crises = InmateCandidates.Shared.Crisis(map), checks = InmateCandidates.Shared.WatchCheck(map);
            if (crises.Count == 0 && checks.Count == 0) return;
            // (Copies: a guard choosing again asks the givers, which may build the lists again.)
            foreach (Thing t in crises.ToArray()) CallFreeGuard(map, (Pawn)t, RPWatchDefOf.RP_CrisisResponseGiver);
            foreach (Thing t in checks.ToArray()) CallFreeGuard(map, (Pawn)t, RPWatchDefOf.RP_WatchCheckGiver);
        }

        /// <summary>The guard at leisure called to this inmate for this giver's job (see <see cref="CallFreeGuards"/>), or null when nobody at leisure would be given it (somebody already on it holds the watch's reservation).</summary>
        public static Pawn CallFreeGuard(Map map, Pawn inmate, WorkGiverDef def)
        {
            if (def?.Worker is not WorkGiver_Scanner giver || !inmate.Spawned) return null;
            Pawn best = null;
            int bestRank = int.MaxValue;
            float bestDist = float.MaxValue;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn g = colonists[i];
                int rank = LeisureRank(g);
                if (rank < 0 || rank > bestRank) continue;
                float dist = g.Position.DistanceToSquared(inmate.Position);
                if (rank == bestRank && dist >= bestDist) continue;
                if (!MayTake(g, def, giver, inmate)) continue;
                (best, bestRank, bestDist) = (g, rank, dist);
            }
            best?.jobs.CheckForJobOverride();
            return best;
        }

        /// <summary>
        /// How free a guard is to be called off what they are doing (lower first), or -1 when they are not: idle or on patrol (0), recreation (1: a joy job, a meditation), a break in the staff room (2). Never one who is
        /// asleep, down, drafted, in a mental state, or on a job the warden ordered.
        /// </summary>
        public static int LeisureRank(Pawn g)
        {
            if (!g.Spawned || g.Downed || g.Drafted || g.InMentalState || !g.Awake() || !GuardUtility.IsGuard(g)) return -1;
            Job job = g.CurJob;
            if (job == null || job.playerForced) return -1;
            if (job.def.isIdle || job.def == RPSecurityDefOf.RP_Patrol) return 0;
            if (job.def == RPStaffRoomDefOf.RP_StaffBreak) return 2;
            return job.def.joyKind != null ? 1 : -1;
        }

        /// <summary>The guard would be given this giver's job on the inmate by the work node: the work on and able to do it, not on strike, and the giver's own tests (the reservation and the way there among them).</summary>
        private static bool MayTake(Pawn g, WorkGiverDef def, WorkGiver_Scanner giver, Pawn inmate) =>
            !IncidentWorker_StaffStrike.Striking(g) && g.workSettings?.WorkIsActive(def.workType) == true && !g.WorkTypeIsDisabled(def.workType) && !g.WorkTagIsDisabled(def.workTags)
            && giver.MissingRequiredCapacity(g) == null && !giver.ShouldSkip(g) && giver.HasJobOnThing(g, inmate);

        // ---- the screens ---------------------------------------------------------------------------------------------------------------------------------------------

        public static string AnswerText(CompInmate c) => ("RP_WatchAnswer_" + c.watchAnswer).Translate().Resolve();

        public static float DaysLeft(CompInmate c) => Mathf.Max(0f, (c.watchUntilTick - Now) / (float)Day);

        /// <summary>The file's status line (null: not on the list).</summary>
        public static string FileLine(CompInmate c) => c.riskTick < 0 ? null : "RP_FileStatusWatch".Translate(AnswerText(c), DaysLeft(c).ToString("0.0")).Resolve();

        /// <summary>The inmates on the list on the map, the most recently flagged first.</summary>
        public static List<Pawn> Listed(Map map) =>
            map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { riskTick: >= 0 }).OrderByDescending(p => p.Inmate().riskTick).ToList();

        /// <summary>The saved fields on one line (the saveload snapshot).</summary>
        public static string Describe(CompInmate c) =>
            $"{c.riskTick}:{c.watchUntilTick}:{c.crisisCount}:{c.riskEvent}@{c.riskEventTick}:{c.riskCause}:{c.lowMoodTick}:{c.watchAnswer}:{c.watchCheckTick}:{c.counselTick}:{c.riskEndTick}:{c.measureTick}";
    }

    /// <summary>The warden's answer to a flag: a watch, a padded cell, counselling, or nothing (an unanswered letter is nothing).</summary>
    public class WatchListDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Watch_OptWatch", "RP_Watch_OptPadded", "RP_Watch_OptCounsel", "RP_Watch_OptNothing" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 3;

        /// <summary>The inmate and their record, or nothing when they are gone or the letter is from a period that is over.</summary>
        private static (Pawn p, CompInmate c) Subject(ChoiceLetter_PrisonDecision l)
        {
            Pawn p = l.pawns.FirstOrDefault(x => x != null);
            if (p is not { Dead: false, Spawned: true } || p.Inmate() is not { activeFlag: true } c || c.riskTick < 0 || c.riskTick != l.amount || ShuttleSystem.OnTheWayOut(p)) return default;
            return (p, c);
        }

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            (Pawn p, CompInmate c) = Subject(l);
            if (c == null) return option == 3 ? null : "RP_Watch_Gone".Translate().ToString();
            return option switch
            {
                1 => PsychWing.CannotMove(p, c),
                2 => WatchList.Counsellor(l.map ?? p.MapHeld) == null ? "RP_Watch_NoCounsellor".Translate().ToString() : null,
                _ => null,
            };
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            (Pawn p, CompInmate c) = Subject(l);
            if (c == null || c.watchAnswer != WatchAnswer.Pending) return;
            WatchAnswer answer = option switch { 0 => WatchAnswer.Watch, 1 => WatchAnswer.Padded, 2 => WatchAnswer.Counselling, _ => WatchAnswer.Nothing };
            // (What was chosen could not be done - the padded cell was taken, the counsellor gone - and it is nothing: said, since the chronicle has already the answer chosen.)
            if (WatchList.Answer(p, c, answer, map)) return;
            WatchList.Answer(p, c, WatchAnswer.Nothing, map);
            string chosen = Keys[option].Translate().Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_WatchFallback".Translate(p.LabelShort, chosen), p);
            Messages.Message("RP_Watch_Fallback".Translate(p.LabelShort, chosen), p, MessageTypeDefOf.CautionInput, historical: false);
        }
    }

    /// <summary>The belongings taken on a watch: a mood penalty (the number the letter states) while the watch lasts.</summary>
    public class ThoughtWorker_OnWatch : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p) => p.IsPrisonerOfColony && WatchList.OnWatch(p.Inmate()) ? ThoughtState.ActiveAtStage(0) : ThoughtState.Inactive;
    }

    /// <summary>
    /// A crisis: they keep to the room they are in. Any member of staff who reaches them ends it (<see cref="WatchList.Reacher"/>); seen by a guard (or a camera, a tower) it is noticed, and a guard is sent
    /// (<see cref="WorkGiver_CrisisResponse"/>). Nobody in <see cref="WatchList.HarmTicks"/> ticks: they harm themself and it ends.
    /// </summary>
    public class MentalState_Crisis : MentalState
    {
        /// <summary>The staff have seen it (a guard, a tower or a camera): a guard is sent.</summary>
        public bool noticed;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref noticed, "noticed");
        }

        protected override bool CanEndBeforeMaxDurationNow => false;

        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;

        public override void MentalStateTick(int delta)
        {
            // (The setting turned off during a crisis: it ends at once, with no harm.)
            if (!WatchList.CrisesOn)
            {
                RecoverFromState();
                return;
            }
            if (pawn.Spawned && pawn.IsHashIntervalTick(60, delta))
            {
                if (WatchList.Reacher(pawn) is { } staff)
                {
                    WatchList.Interrupt(pawn, staff);
                    return;
                }
                if (!noticed && PrisonMap.Of(pawn.Map) is { } pm && TensionUtility.Watched(pawn, pm)) noticed = true;
                if (age >= WatchList.HarmTicks)
                {
                    WatchList.Harm(pawn);
                    if (pawn.MentalState == this) RecoverFromState();
                    return;
                }
            }
            base.MentalStateTick(delta);
        }
    }

    // ---- the guards' and the counsellors' work ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The watch's check: a guard goes to the inmate and looks in (care work: the care reserve takes it first, see <see cref="CareDetail"/>). One guard a check, by a reservation of its own layer
    /// (<see cref="RPWatchDefOf.RP_WatchLayer"/>): nobody else's work on the inmate (a tend, a rescue, therapy) is blocked by it.
    /// </summary>
    public class WorkGiver_WatchCheck : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.WatchCheck(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || !forced && InmateCandidates.Shared.WatchCheck(pawn.Map).Count == 0;

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the guard.</summary>
        public static bool Candidate(Pawn inmate) => WatchList.CheckDue(inmate);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate) && pawn.CanReserve(inmate, 1, -1, RPWatchDefOf.RP_WatchLayer, forced) && pawn.CanReach(inmate, PathEndMode.Touch, Danger.Deadly);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPWatchDefOf.RP_WatchCheck, t);
    }

    public class JobDriver_WatchCheck : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, RPWatchDefOf.RP_WatchLayer, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !WatchList.OnWatch(Inmate.Inmate()));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil look = Toils_General.Wait(WatchList.LookTicks);
            look.handlingFacing = true;
            look.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            look.WithProgressBarToilDelay(TargetIndex.A);
            look.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, 3f));
            yield return look;
            Toil done = ToilMaker.MakeToil("WatchChecked");
            done.initAction = () => WatchList.Notify_Checked(pawn, Inmate);
            yield return done;
        }
    }

    /// <summary>A crisis the staff have seen (or of one on watch): a guard goes to them, as to the downed, one at a time (the watch's reservation layer: nobody else's work is blocked). Reaching them ends it.</summary>
    public class WorkGiver_CrisisResponse : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Crisis(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || !forced && InmateCandidates.Shared.Crisis(pawn.Map).Count == 0;

        public static bool Candidate(Pawn inmate) =>
            inmate.Spawned && inmate.MentalState is MentalState_Crisis state && inmate.Inmate() is { activeFlag: true } c && (state.noticed || WatchList.OnWatch(c));

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate) && pawn.CanReserve(inmate, 1, -1, RPWatchDefOf.RP_WatchLayer, forced) && pawn.CanReach(inmate, PathEndMode.Touch, Danger.Deadly);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Job job = JobMaker.MakeJob(RPWatchDefOf.RP_CrisisResponse, t);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            return job;
        }
    }

    public class JobDriver_CrisisResponse : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, RPWatchDefOf.RP_WatchLayer, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !WatchList.InCrisis(Inmate));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil reached = ToilMaker.MakeToil("CrisisReached");
            reached.initAction = () => WatchList.Interrupt(Inmate, pawn);
            yield return reached;
        }
    }

    /// <summary>Counselling for an inmate on the list the warden answered with it: an hour a day, by the counsellor (or the psychologist).</summary>
    public class WorkGiver_WatchCounselling : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Counselling(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !forced && InmateCandidates.Shared.Counselling(pawn.Map).Count == 0;

        public static bool Candidate(Pawn inmate) => WatchList.CounselDue(inmate);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate) && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPWatchDefOf.RP_WatchCounselling, t);
    }

    public class JobDriver_WatchCounselling : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !WatchList.CounselDue(Inmate));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil talk = Toils_General.Wait(WatchList.SessionTicks);
            talk.handlingFacing = true;
            talk.initAction = () =>
            {
                pawn.pather.StopDead();
                if (Inmate.CurJob is not { } cur || cur.def.casualInterruptible)
                    Inmate.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, WatchList.SessionTicks + 50), JobCondition.InterruptForced);
            };
            talk.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            talk.WithProgressBarToilDelay(TargetIndex.A);
            talk.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, 3f));
            yield return talk;
            Toil done = ToilMaker.MakeToil("WatchCounsellingDone");
            done.initAction = () => WatchList.Notify_Counselled(pawn, Inmate);
            yield return done;
        }
    }
}
