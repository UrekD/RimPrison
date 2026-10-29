using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    public enum MeasuresCause : byte
    {
        /// <summary>Three days in a row graded below the difficulty's line (<see cref="SpecialMeasures.BadGradeText"/>).</summary>
        Grades,
        /// <summary>Deaths and escapes over a week's limit.</summary>
        Incidents,
        /// <summary>The state loan in default: still in the red nights after taking it.</summary>
        Loan,
    }

    /// <summary>
    /// What the state makes of the prison: special measures while it fails, a security chief of its own when it fails them,
    /// flagship status while it excels. One scribed block, owned by <see cref="StorySystem"/>; the rules are in <see cref="SpecialMeasures"/>.
    /// Days are <see cref="Ledger.CurrentDay"/>s, so the arc is counted in midnights whatever the clock does between them.
    /// </summary>
    public class StateOversight : IExposable
    {
        public bool active;
        public MeasuresCause cause;
        public int startDay = -1, deadlineDay = -1;
        /// <summary>Terms already extended (0: the first term).</summary>
        public int extensions;
        /// <summary><see cref="PrisonStats.escaped"/> when the term began: an escape since is a missed target.</summary>
        public int baselineEscapes;
        /// <summary>Midnights in a row with the safety grade at C or better.</summary>
        public int safetyDays;
        public bool midVisitDone;
        /// <summary>The visiting overseer (leaves after a few hours; null between visits).</summary>
        public Pawn overseer;
        /// <summary>The state's security chief: on the staff, cannot be let go, until the prison earns the state's trust back.</summary>
        public Pawn chief;
        /// <summary>The measures were failed and the chief has not been recalled: the restrictions stay, the chief is replaced if they are lost, a bad record costs another project.</summary>
        public bool underChief;
        /// <summary>Midnights in a row graded B or better, counted while the chief is in post.</summary>
        public int goodDays;
        /// <summary>No new special measures before this day (after they were lifted, or the chief left).</summary>
        public int graceUntilDay = -1;
        /// <summary>Deaths plus escapes of each of the last seven days, oldest first.</summary>
        public List<int> weekLog = new List<int>();
        /// <summary>Midnights in a row graded A, and in a row below B (flagship status is earned by the first, lost by the second).</summary>
        public int aDays, slumpDays;
        public bool flagship;
        public int nextTransferDay = -1;

        public bool ChiefInPost => chief is { Dead: false, Destroyed: false } && chief.GetComp<CompStaff>() is { statePosted: true, role: not null };

        public void ExposeData()
        {
            Scribe_Values.Look(ref active, "active");
            Scribe_Values.Look(ref cause, "cause");
            Scribe_Values.Look(ref startDay, "startDay", -1);
            Scribe_Values.Look(ref deadlineDay, "deadlineDay", -1);
            Scribe_Values.Look(ref extensions, "extensions");
            Scribe_Values.Look(ref baselineEscapes, "baselineEscapes");
            Scribe_Values.Look(ref safetyDays, "safetyDays");
            Scribe_Values.Look(ref midVisitDone, "midVisitDone");
            Scribe_References.Look(ref overseer, "overseer");
            Scribe_References.Look(ref chief, "chief");
            Scribe_Values.Look(ref underChief, "underChief");
            Scribe_Values.Look(ref goodDays, "goodDays");
            Scribe_Values.Look(ref graceUntilDay, "graceUntilDay", -1);
            Scribe_Collections.Look(ref weekLog, "weekLog", LookMode.Value);
            Scribe_Values.Look(ref aDays, "aDays");
            Scribe_Values.Look(ref slumpDays, "slumpDays");
            Scribe_Values.Look(ref flagship, "flagship");
            Scribe_Values.Look(ref nextTransferDay, "nextTransferDay", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) weekLog ??= new List<int>();
        }
    }

    [DefOf]
    public static class RPMeasuresDefOf
    {
        public static StaffRoleDef RP_Overseer;
        public static StaffRoleDef RP_SecurityChief;
        public static StoryArcDef RP_Arc_Legend;

        static RPMeasuresDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPMeasuresDefOf));
    }

    /// <summary>
    /// Special measures: the state steps in. Three days in a row graded below the difficulty's line (a D or F at the neutral level, see <see cref="BadGradeText"/>), too many deaths and escapes in a week, or a state loan in default, and an
    /// overseer arrives with a term of seven days: safety at C or better on the last two reports and no escapes. Meanwhile the intake is capped
    /// (frozen once the term is extended), the state pays 10% less per inmate, armed response may not fire on rioters and solitary is
    /// capped at a day. Meeting the targets (safety, no escapes, and the trouble that brought the state put right) lifts the measures with praise and a
    /// grant; missing them extends the term once; missing it again, the state installs a security chief of its own (on the staff, cannot be let go, sets
    /// the patrols) and the bureaucracy loses its latest project. Failing is worse than being under measures: the restrictions stay (no new inmates sent),
    /// a chief who is lost is replaced, and a bad record while the chief is here costs another project. They all end when the prison has been graded B
    /// for a week and the chief is recalled. The mirror: five days graded A make the prison the state's flagship, with better grants and the occasional
    /// legendary transfer.
    /// </summary>
    public static class SpecialMeasures
    {
        public const int TermDays = 7, MaxExtensions = 1, GraceDays = 5, BadDays = 3, LoanDefaultDays = 5, WeekLength = 7;
        public const float SafetyGrade = 55f, GoodGrade = 70f, FlagshipGrade = 85f;
        public const int SafetyDaysNeeded = 2, ChiefRecallDays = 7, FlagshipDays = 5, FlagshipSlumpDays = 2, TransferEveryDays = 8;
        public const int IntakeCapPerDay = 2, SolitaryCapHours = 24, LiftGrant = 3000, LegendPremiumFactor = 6, LegendMediaFee = 1500;
        public const float PaymentCut = 0.1f, FlagshipGrantBonus = 0.25f, LiftReputation = 5f;
        public const int OverseerVisitHours = 8, WatchMargin = 2;

        /// <summary>A day graded below this counts as bad towards the state stepping in (a C at the neutral difficulty: see <see cref="PrisonDifficulty"/>).</summary>
        public static float BadGrade => PrisonDifficulty.BadGrade;

        /// <summary>The line for a bad day in words, for the letters and the pane: "below 55 points (a D or an F)" (the letter only where the line is the edge of one).</summary>
        public static string BadGradeText => "RP_BadGradeBelow".Translate(Mathf.RoundToInt(BadGrade)).Resolve() + GradeNote("RP_BadGradeNote_");

        /// <summary>The line for a good day: "55 points or more (a C or better)".</summary>
        public static string GoodGradeText => "RP_GoodGradeFrom".Translate(Mathf.RoundToInt(BadGrade)).Resolve() + GradeNote("RP_GoodGradeNote_");

        private static string GradeNote(string keyStart) => (keyStart + Mathf.RoundToInt(BadGrade)).TryTranslate(out TaggedString note) ? note.Resolve() : "";

        public static StateOversight State => PrisonGame.Instance?.story.oversight;

        public static bool Active => State is { active: true };

        public static bool FlagshipActive => State is { flagship: true };

        public static bool ChiefInPost => State is { } o && o.ChiefInPost;

        /// <summary>The restrictions are in force: the measures run, or they were failed and the state's chief has not been recalled yet.</summary>
        public static bool Restricted => State is { } o && (o.active || o.underChief);

        /// <summary>The measures themselves are running (not only the state's chief in post after they failed).</summary>
        public static bool Running => State is { active: true };

        /// <summary>Where a guard patrols: the zone the warden set, unless the state's chief is in post (then wherever the inmates are: null).</summary>
        public static PrisonZoneDef DeployedZone(Pawn guard) => ChiefInPost ? null : guard.GetComp<CompStaff>()?.patrolZone;

        /// <summary>The oversight on one line (the saveload plan compares it across a round trip).</summary>
        public static string Describe(StateOversight o) =>
            $"{o.active}/{o.underChief}/{o.cause}/{o.startDay}/{o.deadlineDay}/{o.extensions}/{o.baselineEscapes}/{o.safetyDays}/{o.midVisitDone}/ov={o.overseer?.thingIDNumber}/chief={o.chief?.thingIDNumber}:{o.chief?.GetComp<CompStaff>()?.statePosted}"
            + $"/{o.goodDays}/{o.graceUntilDay}/[{string.Join(",", o.weekLog)}]/{o.aDays}/{o.slumpDays}/{o.flagship}/{o.nextTransferDay}";

        /// <summary>Set while the state recalls its own chief: the only way the chief is let go.</summary>
        public static bool Recalling;

        /// <summary>A member of staff the state posted: not to be let go, promoted or turned.</summary>
        public static bool Posted(Pawn p) => p?.GetComp<CompStaff>() is { statePosted: true };

        // ---- the restrictions ---------------------------------------------------------------------------------------------------

        /// <summary>Arrivals the shuttle may bring today (int.MaxValue: no limit): a few in the first term, none once it is extended or the state's chief is here.</summary>
        public static int IntakeLimit => State is { } o && (o.active || o.underChief) ? (o.underChief || o.extensions > 0 ? 0 : IntakeCapPerDay) : int.MaxValue;

        public static float PaymentFactor => Restricted ? 1f - PaymentCut : 1f;

        /// <summary>The overseer will not have rioters shot at: armed response is held at escapes only.</summary>
        public static ArmedResponsePolicy LimitArmed(ArmedResponsePolicy policy) => Restricted && policy == ArmedResponsePolicy.EscapesAndViolence ? ArmedResponsePolicy.Escapes : policy;

        public static int LimitSolitary(int hours) => Restricted ? Mathf.Min(hours, SolitaryCapHours) : hours;

        public static int GrantReward(int reward) => FlagshipActive ? Mathf.RoundToInt(reward * (1f + FlagshipGrantBonus)) : reward;

        public static int DaysLeft(StateOversight o) => Mathf.Max(0, o.deadlineDay - Ledger.CurrentDay);

        public static bool SafetyMet(StateOversight o) => o.safetyDays >= SafetyDaysNeeded;

        public static bool NoEscapes(PrisonGame game, StateOversight o) => game.stats.escaped == o.baselineEscapes;

        public static bool LoanInDefault(PrisonGame game) => game.story.loanOwed > 0 && game.story.debtDays >= LoanDefaultDays;

        /// <summary>Midnights in a row, up to <see cref="SafetyDaysNeeded"/>, the overall grade was not a bad one (see <see cref="BadGradeText"/>).</summary>
        public static int GradeRun(PrisonGame game)
        {
            int run = 0;
            for (int i = game.gradeHistory.Count - 1; i >= 0 && run < SafetyDaysNeeded; i--)
            {
                if (game.BadDay(i)) break;
                run++;
            }
            return run;
        }

        /// <summary>The third target, the one that matches what brought the state in: the grade back at C, the week's deaths and escapes under the limit, the loan out of default.</summary>
        public static bool CauseMet(PrisonGame game, Map map, StateOversight o) => o.cause switch
        {
            MeasuresCause.Grades => GradeRun(game) >= SafetyDaysNeeded,
            MeasuresCause.Incidents => o.weekLog.Sum() < WeeklyLimit(map),
            _ => !LoanInDefault(game),
        };

        /// <summary>The week's limit of deaths and escapes: a tenth of the inmates, at least five (times the difficulty's factor, see <see cref="PrisonDifficulty"/>).</summary>
        public static int WeeklyLimit(Map map) =>
            Mathf.Max(3, Mathf.CeilToInt(Mathf.Max(5, Mathf.CeilToInt(map.mapPawns.PrisonersOfColonySpawnedCount * 0.1f)) * PrisonDifficulty.Current.weeklyLimit));

        // ---- the midnight pass --------------------------------------------------------------------------------------------------

        /// <summary>Midnight, after the day's grade: the week's tally, the streaks, then whatever is due (the trigger, a visit, the review, the chief's recall, the flagship).</summary>
        public static void Daily(PrisonGame game, Map map, ReportCard card, DailyIncidents day)
        {
            StateOversight o = game.story.oversight;
            if (o == null) return;
            o.weekLog.Add(day.deaths + day.escapes);
            while (o.weekLog.Count > WeekLength) o.weekLog.RemoveAt(0);
            // (An empty prison has no grade: its day counts for none of these.)
            if (card.Graded)
            {
                o.safetyDays = card.safety >= SafetyGrade ? o.safetyDays + 1 : 0;
                float grade = card.Overall;
                // (No A days count while the state is overseeing the prison: lifting the measures is not a flagship's first day.)
                o.aDays = grade >= FlagshipGrade && !(o.active || o.underChief) ? o.aDays + 1 : 0;
                o.slumpDays = grade < GoodGrade ? o.slumpDays + 1 : 0;
                o.goodDays = grade >= GoodGrade ? o.goodDays + 1 : 0;
            }
            int today = Ledger.CurrentDay;
            // (A chief in post means the state's term: it holds for a state that was saved before the flag existed.)
            o.underChief |= o.ChiefInPost;
            if (o.active)
            {
                if (today >= o.deadlineDay) Review(game, map, o, card);
                else if (!o.midVisitDone && today >= o.startDay + TermDays / 2) MidVisit(game, map, o);
            }
            else if (o.underChief) ChiefDaily(game, map, o, today);
            else if (Trigger(game, map, o) is { } cause) Start(game, map, o, cause);
            Flagship(game, map, o, today);
        }

        /// <summary>Why the state would step in (or step in harder, with its chief here) today, or null (not while measures run, or in the grace after they were lifted, the chief came or tightened).</summary>
        public static MeasuresCause? Trigger(PrisonGame game, Map map, StateOversight o)
        {
            if (o.active || Ledger.CurrentDay < o.graceUntilDay) return null;
            List<float> h = game.gradeHistory;
            if (h.Count >= BadDays)
            {
                bool bad = true;
                for (int i = h.Count - BadDays; i < h.Count; i++)
                    if (!game.BadDay(i)) bad = false;
                if (bad) return MeasuresCause.Grades;
            }
            if (o.weekLog.Sum() >= WeeklyLimit(map)) return MeasuresCause.Incidents;
            if (LoanInDefault(game)) return MeasuresCause.Loan;
            return null;
        }

        /// <summary>What the state is about to step in over, one line each: the grades two bad days in, the week's count within two of the limit, the loan two nights short of default. Empty: nothing to warn of.</summary>
        public static List<string> Watching(PrisonGame game, Map map, StateOversight o)
        {
            var lines = new List<string>();
            if (o.active || o.underChief || Ledger.CurrentDay < o.graceUntilDay) return lines;
            List<float> h = game.gradeHistory;
            int bad = 0;
            for (int i = h.Count - 1; i >= 0 && game.BadDay(i); i--) bad++;
            if (bad > 0 && bad < BadDays) lines.Add("RP_MeasuresWatch_Grades".Translate(bad, BadDays, BadGradeText).ToString());
            int week = o.weekLog.Sum(), limit = WeeklyLimit(map);
            if (week > 0 && week >= limit - WatchMargin && week < limit) lines.Add("RP_MeasuresWatch_Incidents".Translate(week, limit).ToString());
            if (game.story.loanOwed > 0 && game.story.debtDays >= LoanDefaultDays - WatchMargin && game.story.debtDays < LoanDefaultDays)
                lines.Add("RP_MeasuresWatch_Loan".Translate(game.story.debtDays, LoanDefaultDays).ToString());
            return lines;
        }

        public static string CauseText(PrisonGame game, Map map, StateOversight o) => o.cause switch
        {
            MeasuresCause.Grades => "RP_MeasuresCause_Grades".Translate(BadDays, BadGradeText).ToString(),
            MeasuresCause.Incidents => "RP_MeasuresCause_Incidents".Translate(o.weekLog.Sum(), WeeklyLimit(map)).ToString(),
            _ => "RP_MeasuresCause_Loan".Translate(game.story.debtDays, Money.Format(game.story.loanOwed)).ToString(),
        };

        public static void Start(PrisonGame game, Map map, StateOversight o, MeasuresCause cause)
        {
            int today = Ledger.CurrentDay;
            (o.active, o.cause, o.startDay, o.deadlineDay, o.extensions) = (true, cause, today, today + TermDays, 0);
            Concepts.Teach(Concepts.Measures);
            (o.baselineEscapes, o.safetyDays, o.midVisitDone) = (game.stats.escaped, 0, false);
            if (o.flagship) LoseFlagship(game, o, "RP_Chr_FlagshipLostToMeasures");
            o.aDays = 0;
            game.stats.measuresStarted++;
            // (The state does not send a cohort on contract while it is overseeing the prison: the offer is withdrawn and the cohort not yet admitted called off.)
            CustodyContracts.Notify_MeasuresBegin(game);
            Pawn overseer = OverseerVisit(map, o, OverseerVisitHours);
            string who = overseer?.LabelShort ?? "RP_MeasuresAnOverseer".Translate().ToString();
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_MeasuresStart".Translate(CauseText(game, map, o), who), overseer);
            Find.LetterStack.ReceiveLetter("RP_MeasuresLabel".Translate(), "RP_MeasuresText".Translate(CauseText(game, map, o), TermDays, who, IntakeCapPerDay,
                PaymentCut.ToStringPercent(), SolitaryCapHours, Targets(game, map, o)), LetterDefOf.NegativeEvent, overseer);
        }

        /// <summary>The overseer's tour half way through the term: where the targets stand, and the inspector's score for the tour (<see cref="Inspections.Score"/>).</summary>
        private static void MidVisit(PrisonGame game, Map map, StateOversight o)
        {
            o.midVisitDone = true;
            Pawn overseer = OverseerVisit(map, o, OverseerVisitHours);
            int score = Inspections.Score(game, map, PrisonGrading.Compute(map, game));
            Find.LetterStack.ReceiveLetter("RP_MeasuresProgressLabel".Translate(), "RP_MeasuresProgressText".Translate(overseer?.LabelShort ?? "RP_MeasuresAnOverseer".Translate().ToString(),
                DaysLeft(o), o.safetyDays, SafetyDaysNeeded, Targets(game, map, o), PrisonGrading.Letter(score)), LetterDefOf.NeutralEvent, overseer);
        }

        /// <summary>The three targets and how each stands, one line.</summary>
        public static string Targets(PrisonGame game, Map map, StateOversight o) =>
            "RP_MeasuresTargets".Translate(SafetyMet(o) ? "RP_MeasuresMet".Translate() : "RP_MeasuresNotYet".Translate(o.safetyDays, SafetyDaysNeeded),
                NoEscapes(game, o) ? "RP_MeasuresMet".Translate() : "RP_MeasuresMissed".Translate(), CauseTarget(game, map, o)).ToString();

        /// <summary>The target that matches the cause, and how it stands.</summary>
        public static string CauseTarget(PrisonGame game, Map map, StateOversight o)
        {
            TaggedString met = "RP_MeasuresMet".Translate();
            bool ok = CauseMet(game, map, o);
            return o.cause switch
            {
                MeasuresCause.Grades => "RP_MeasuresCauseTarget_Grades".Translate(SafetyDaysNeeded, ok ? met : "RP_MeasuresNotYet".Translate(GradeRun(game), SafetyDaysNeeded), GoodGradeText).ToString(),
                MeasuresCause.Incidents => "RP_MeasuresCauseTarget_Incidents".Translate(WeeklyLimit(map), ok ? met : "RP_MeasuresSoFar".Translate(o.weekLog.Sum())).ToString(),
                _ => "RP_MeasuresCauseTarget_Loan".Translate(ok ? met : "RP_MeasuresNightsInDefault".Translate(game.story.debtDays)).ToString(),
            };
        }

        /// <summary>The review at the end of a term: lifted (all three targets met), extended once, or the state's own chief.</summary>
        public static void Review(PrisonGame game, Map map, StateOversight o, ReportCard card)
        {
            Pawn overseer = OverseerVisit(map, o, OverseerVisitHours);
            string who = overseer?.LabelShort ?? "RP_MeasuresAnOverseer".Translate().ToString();
            if (SafetyMet(o) && NoEscapes(game, o) && CauseMet(game, map, o)) Lift(game, o, who, overseer);
            else if (o.extensions < MaxExtensions) Extend(game, map, o, who, overseer);
            else Fail(game, map, o, who, overseer);
        }

        private static void Lift(PrisonGame game, StateOversight o, string who, Pawn overseer)
        {
            o.active = false;
            o.graceUntilDay = Ledger.CurrentDay + GraceDays;
            game.stats.measuresLifted++;
            game.ledger.Add(RPDefOf.RP_Grants, LiftGrant, "RP_MeasuresGrantNote".Translate());
            game.reputation = Mathf.Clamp(game.reputation + LiftReputation, 0f, 100f);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_MeasuresLifted".Translate(who, Money.Format(LiftGrant)), overseer);
            Find.LetterStack.ReceiveLetter("RP_MeasuresLiftedLabel".Translate(), "RP_MeasuresLiftedText".Translate(who, Money.Format(LiftGrant), GraceDays), LetterDefOf.PositiveEvent, overseer);
        }

        private static void Extend(PrisonGame game, Map map, StateOversight o, string who, Pawn overseer)
        {
            o.extensions++;
            // (A new term: its half-way visit is counted from here, not from the first term's start.)
            o.startDay = Ledger.CurrentDay;
            o.deadlineDay = Ledger.CurrentDay + TermDays;
            o.baselineEscapes = game.stats.escaped;
            o.safetyDays = 0;
            o.midVisitDone = false;
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_MeasuresExtended".Translate(who, TermDays), overseer);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_MeasuresExtended", "RP_MeasuresExtendedLabel".Translate(),
                "RP_MeasuresExtendedText".Translate(who, TermDays, Targets(game, map, o), BureaucracyProjectName(game)), map, overseer);
            letter.StartTimeout(GenDate.TicksPerHour * 20);
            Find.LetterStack.ReceiveLetter(letter);
        }

        private static string BureaucracyProjectName(PrisonGame game)
        {
            BureaucracyDef next = game.bureaucracy.NextToLose();
            return next != null ? next.label : "RP_MeasuresNoProject".Translate().ToString();
        }

        /// <summary>
        /// The term ran out for the second time (or the warden asked for it): the state's security chief, and the bureaucracy loses a project. The term
        /// ends but the restrictions do not: failing is worse than being under measures, and the chief is the state's hold on the prison until it earns
        /// the recall.
        /// </summary>
        public static void Fail(PrisonGame game, Map map, StateOversight o, string who, Pawn overseer)
        {
            o.active = false;
            o.underChief = true;
            o.goodDays = 0;
            o.graceUntilDay = Ledger.CurrentDay + GraceDays;
            game.stats.measuresFailed++;
            BureaucracyDef lost = game.bureaucracy.LoseLatest();
            Pawn chief = InstallChief(map, o);
            string chiefName = chief?.LabelShort ?? "RP_MeasuresAChief".Translate().ToString();
            string project = lost != null ? lost.label : "RP_MeasuresNoProject".Translate().ToString();
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, (lost != null ? "RP_Chr_MeasuresFailed" : "RP_Chr_MeasuresFailedNoProject").Translate(chiefName, project, ChiefRecallDays), chief);
            Find.LetterStack.ReceiveLetter("RP_MeasuresFailedLabel".Translate(), (lost != null ? "RP_MeasuresFailedText" : "RP_MeasuresFailedTextNoProject").Translate(who, chiefName, project,
                ChiefRecallDays, PrisonGrading.Letter(GoodGrade)), LetterDefOf.NegativeEvent, chief);
        }

        // ---- the state's security chief -----------------------------------------------------------------------------------------

        /// <summary>A chief arrives, on the staff for good (paid like anyone) and out of the warden's hands.</summary>
        public static Pawn InstallChief(Map map, StateOversight o)
        {
            StaffRoleDef role = RPMeasuresDefOf.RP_SecurityChief;
            Pawn p = StaffUtility.GenerateCandidate(role);
            if (p == null) return null;
            IntVec3 spot = IntakeSystem.IntakeSpot(map);
            if (!CellFinder.TryFindRandomCellNear(spot, map, 5, c => c.Standable(map), out IntVec3 cell)) cell = spot;
            GenSpawn.Spawn(p, cell, map);
            StaffUtility.ApplyRole(p, role, record: false);
            p.GetComp<CompStaff>().statePosted = true;
            RiotPolice.Dress(p, armoured: false);
            o.chief = p;
            o.underChief = true;
            o.goodDays = 0;
            return p;
        }

        /// <summary>The midnight with the state's chief here: a lost chief is replaced, a week graded B takes them back, a fresh bad record costs another project.</summary>
        private static void ChiefDaily(PrisonGame game, Map map, StateOversight o, int today)
        {
            if (!o.ChiefInPost) ReplaceChief(game, map, o);
            else if (o.goodDays >= ChiefRecallDays) RecallChief(game, map, o);
            else if (Trigger(game, map, o) is { } cause) Tighten(game, map, o, cause);
        }

        /// <summary>
        /// The chief died, quit or vanished (not recalled): the restrictions held all along, and the state sends another, with the week of B grades counted
        /// afresh. Losing the chief is no way out.
        /// </summary>
        private static void ReplaceChief(PrisonGame game, Map map, StateOversight o)
        {
            string gone = o.chief?.LabelShort ?? "RP_MeasuresAChief".Translate().ToString();
            Pawn chief = InstallChief(map, o);
            if (chief == null) return; // (nobody to send tonight: the state tries again at the next midnight)
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_ChiefReplaced".Translate(gone, chief.LabelShort, ChiefRecallDays), chief);
            Find.LetterStack.ReceiveLetter("RP_ChiefReplacedLabel".Translate(), "RP_ChiefReplacedText".Translate(gone, chief.LabelShort, ChiefRecallDays), LetterDefOf.NegativeEvent, chief);
        }

        /// <summary>The prison is failing again with the state's chief in post: the bureaucracy loses another project, and the week of B grades starts over.</summary>
        private static void Tighten(PrisonGame game, Map map, StateOversight o, MeasuresCause cause)
        {
            o.cause = cause;
            o.goodDays = 0;
            o.graceUntilDay = Ledger.CurrentDay + GraceDays;
            BureaucracyDef lost = game.bureaucracy.LoseLatest();
            string project = lost != null ? lost.label : "RP_MeasuresNoProject".Translate().ToString();
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, (lost != null ? "RP_Chr_MeasuresTightened" : "RP_Chr_MeasuresTightenedNoProject").Translate(CauseText(game, map, o), project, ChiefRecallDays), o.chief);
            Find.LetterStack.ReceiveLetter("RP_MeasuresTightenedLabel".Translate(), (lost != null ? "RP_MeasuresTightenedText" : "RP_MeasuresTightenedTextNoProject").Translate(CauseText(game, map, o), project,
                ChiefRecallDays, GraceDays), LetterDefOf.NegativeEvent, o.chief);
        }

        /// <summary>
        /// Whether anyone but <paramref name="chief"/> is still among the prison's colonists, on site or at home (<paramref name="colonists"/>: who counts, the real colonists
        /// when null). Recalling the last one would start vanilla's game-over countdown.
        /// </summary>
        public static bool OthersRemain(Pawn chief, IEnumerable<Pawn> colonists = null)
        {
            if (colonists == null)
            {
                PrisonGame game = PrisonGame.Instance;
                colonists = PawnsFinder.AllMaps_FreeColonistsSpawned.Concat(game != null ? game.OffSite : Enumerable.Empty<Pawn>());
            }
            return colonists.Any(p => p != chief && p is { Dead: false, Destroyed: false });
        }

        /// <summary>The prison has been graded B for a week: the state takes its chief back, and the restrictions end. Not while they are the last colonist (the recall would end the game).</summary>
        public static void RecallChief(PrisonGame game, Map map, StateOversight o, IEnumerable<Pawn> colonists = null)
        {
            Pawn chief = o.chief;
            if (chief != null && !OthersRemain(chief, colonists)) return;
            string name = chief?.LabelShort ?? "RP_MeasuresAChief".Translate().ToString();
            if (chief != null && chief.GetComp<CompStaff>() is { } sc)
            {
                sc.statePosted = false;
                Recalling = true;
                try { StaffUtility.Fire(chief, record: false); }
                finally { Recalling = false; }
            }
            o.chief = null;
            o.underChief = false;
            o.graceUntilDay = Ledger.CurrentDay + GraceDays;
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_ChiefRecalled".Translate(name), chief);
            Find.LetterStack.ReceiveLetter("RP_ChiefRecalledLabel".Translate(), "RP_ChiefRecalledText".Translate(name, GraceDays), LetterDefOf.PositiveEvent);
        }

        // ---- the overseer ---------------------------------------------------------------------------------------------------------

        /// <summary>The overseer arrives (or, still here from a visit, stays on) and tours the yard and the canteen; they leave after <paramref name="hours"/>.</summary>
        public static Pawn OverseerVisit(Map map, StateOversight o, int hours)
        {
            PrisonMap pm = PrisonMap.Of(map);
            Pawn p = Visit(map, o.overseer, RPMeasuresDefOf.RP_Overseer, hours, pm == null ? new List<IntVec3>() : ZoneStops(pm, RPDefOf.RP_Yard, RPDefOf.RP_Canteen, RPDefOf.RP_CommonRoom));
            if (p != null) o.overseer = p;
            return p;
        }

        /// <summary>
        /// A visiting official of the state (the overseer, the inspector): <paramref name="here"/>, if they are still on the map from a visit, stays on until the time is up; otherwise one arrives at the intake spot, walks through
        /// <paramref name="stops"/> in turn and leaves after <paramref name="hours"/>. Null when nobody could be made for the role.
        /// </summary>
        public static Pawn Visit(Map map, Pawn here, StaffRoleDef role, int hours, List<IntVec3> stops)
        {
            int leave = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            if (here is { Dead: false, Spawned: true } && here.GetComp<CompStaff>() is { role: not null } hc)
            {
                hc.leaveAtTick = leave;
                return here;
            }
            Pawn p = StaffUtility.GenerateCandidate(role);
            if (p == null) return null;
            IntVec3 spot = IntakeSystem.IntakeSpot(map);
            if (!CellFinder.TryFindRandomCellNear(spot, map, 5, c => c.Standable(map), out IntVec3 cell)) cell = spot;
            GenSpawn.Spawn(p, cell, map);
            StaffUtility.ApplyRole(p, role, record: false);
            p.GetComp<CompStaff>().leaveAtTick = leave;
            // A visit of a few hours is all work: arriving at night, they would otherwise lie down where they stand.
            if (p.timetable != null) for (int hr = 0; hr < 24; hr++) p.timetable.SetAssignment(hr, TimeAssignmentDefOf.Work);
            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
            RiotPolice.Dress(p, armoured: false);
            Tour(p, map, stops);
            return p;
        }

        /// <summary>One random cell of each of the zones that are painted (the stops of a walk).</summary>
        public static List<IntVec3> ZoneStops(PrisonMap pm, params PrisonZoneDef[] zones)
        {
            var stops = new List<IntVec3>();
            foreach (PrisonZoneDef zone in zones)
                if (pm.zones.CellsOf(zone) is { Count: > 0 } cells) stops.Add(cells.RandomElement());
            return stops;
        }

        /// <summary>A walk through the stops, so the official is seen to inspect.</summary>
        private static void Tour(Pawn p, Map map, List<IntVec3> stops)
        {
            if (p.jobs == null) return;
            bool first = true;
            foreach (IntVec3 dest in stops)
            {
                if (!p.CanReach(dest, PathEndMode.OnCell, Danger.Some)) continue;
                Job go = JobMaker.MakeJob(JobDefOf.Goto, dest);
                go.locomotionUrgency = LocomotionUrgency.Walk;
                if (first) p.jobs.StartJob(go, JobCondition.InterruptForced);
                else p.jobs.jobQueue.EnqueueLast(go);
                first = false;
            }
        }

        // ---- flagship ---------------------------------------------------------------------------------------------------------------

        private static void Flagship(PrisonGame game, Map map, StateOversight o, int today)
        {
            if (o.flagship)
            {
                if (o.slumpDays >= FlagshipSlumpDays)
                {
                    LoseFlagship(game, o, "RP_Chr_FlagshipLost");
                    return;
                }
                if (today >= o.nextTransferDay && OfferTransfer(game, map)) o.nextTransferDay = today + TransferEveryDays;
                return;
            }
            if (o.aDays < FlagshipDays || o.active || o.underChief) return;
            o.flagship = true;
            Concepts.Teach(Concepts.Flagship);
            o.nextTransferDay = today + 2;
            game.stats.flagshipsEarned++;
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_FlagshipEarned".Translate(FlagshipDays));
            Find.LetterStack.ReceiveLetter("RP_FlagshipLabel".Translate(), "RP_FlagshipText".Translate(FlagshipDays, FlagshipGrantBonus.ToStringPercent(), TransferEveryDays), LetterDefOf.PositiveEvent);
        }

        private static void LoseFlagship(PrisonGame game, StateOversight o, string chronicleKey)
        {
            o.flagship = false;
            o.aDays = 0;
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, chronicleKey.Translate());
            Find.LetterStack.ReceiveLetter("RP_FlagshipLostLabel".Translate(), "RP_FlagshipLostText".Translate(), LetterDefOf.NegativeEvent);
        }

        private static SecurityLevelDef Maximum => RPSecurityDefOf.RP_Maximum;

        public static bool CanReceiveLegend(Map map) => Unlocks.IsUnlocked(Maximum) && IntakeSystem.FreeBedCount(map, Maximum) > 0;

        /// <summary>The state offers a legendary prisoner (false when the prison has no maximum-security bed to give them).</summary>
        public static bool OfferTransfer(PrisonGame game, Map map)
        {
            if (!CanReceiveLegend(map)) return false;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_FlagshipTransfer", "RP_FlagshipTransferLabel".Translate(),
                "RP_FlagshipTransferText".Translate(Money.Format(Maximum.intakeGrant * LegendPremiumFactor)), map);
            letter.StartTimeout(GenDate.TicksPerHour * 16);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }

        /// <summary>The legend arrives: a celebrity arrival with a bigger premium, and a story of their own.</summary>
        public static Pawn ReceiveLegend(Map map)
        {
            Pawn p = Celebrities.Admit(map, "RP_Chr_AdmittedLegend", LegendPremiumFactor);
            if (p == null) return null;
            PrisonGame.Instance.stats.legendsArrived++;
            if (PrisonGame.Instance.story.TryStartArc(RPMeasuresDefOf.RP_Arc_Legend, p) is { } arc) LegendArc.Begin(arc);
            return p;
        }
    }

    /// <summary>The overseer's second term: the warden may ask the state for its chief at once instead (the restrictions end, the chief and the lost project come).</summary>
    public class MeasuresExtendedDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_MeasuresExtended_Carry", "RP_MeasuresExtended_Chief" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 0;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            StateOversight o = game.story.oversight;
            // (The letter may have sat open while the term ran out: nothing to hand over then.)
            if (option != 1 || o is not { active: true, extensions: > 0 }) return;
            Pawn overseer = o.overseer is { Dead: false, Spawned: true } ov && ov.GetComp<CompStaff>() is { role: not null } ? ov : null;
            SpecialMeasures.Fail(game, map, o, overseer?.LabelShort ?? "RP_MeasuresAnOverseer".Translate().ToString(), overseer);
        }
    }

    /// <summary>A legendary prisoner on offer to the flagship: take them (a big premium, and a prisoner everyone wants to test) or decline.</summary>
    public class FlagshipTransferDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_FlagshipTransfer_Accept", "RP_FlagshipTransfer_Decline" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && l.map is { } m && !SpecialMeasures.CanReceiveLegend(m) ? "RP_FlagshipTransferNoBed".Translate().ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option != 0) return;
            if (SpecialMeasures.ReceiveLegend(map) is not { } legend) Messages.Message("RP_FlagshipTransferNoBed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            else Find.LetterStack.ReceiveLetter("RP_LegendArrivedLabel".Translate(legend.LabelShort), "RP_LegendArrivedText".Translate(legend.LabelShort, Money.Format(Celebrities.Premium(Celebrities.Maximum, SpecialMeasures.LegendPremiumFactor))),
                LetterDefOf.NeutralEvent, legend);
        }
    }

    /// <summary>
    /// A legend's story: two days after they arrive, a film crew and the state want to show the prison off through them. Let the cameras in (a fee,
    /// reputation, and a prisoner who feeds on the attention) or keep them out of sight. Unanswered, the legend is kept out of sight.
    /// </summary>
    public class LegendArc : StoryArcWorker
    {
        public const int ArrivalDays = 2, Tension = 20;
        public const float ReputationGain = 3f;
        private static readonly string[] Keys = { "RP_Legend_Cameras", "RP_Legend_Quiet" };

        public static void Begin(StoryArc arc) => arc.nextTick = Find.TickManager.TicksGame + ArrivalDays * GenDate.TicksPerDay;

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override int TimeoutChoice(StoryArc arc) => 1;

        public override void Hourly(StoryArc arc, Map map)
        {
            if (arc.stage != 0 || Find.TickManager.TicksGame < arc.nextTick || arc.A == null) return;
            arc.stage = 1;
            AskWarden(arc, map, "RP_LegendCamerasLabel".Translate(arc.A.LabelShort), "RP_LegendCamerasText".Translate(arc.A.LabelShort, Money.Format(SpecialMeasures.LegendMediaFee)));
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            Pawn legend = arc.A;
            arc.done = true;
            if (option != 0 || legend == null || legend.Dead || legend.Inmate() is not { } c) return;
            game.ledger.Add(RPDefOf.RP_Grants, SpecialMeasures.LegendMediaFee, "RP_LegendFeeNote".Translate(legend.LabelShort));
            game.reputation = Mathf.Clamp(game.reputation + ReputationGain, 0f, 100f);
            c.tension = Mathf.Min(100f, c.tension + Tension);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_LegendCameras".Translate(legend.LabelShort, Money.Format(SpecialMeasures.LegendMediaFee)), legend);
        }
    }
}
