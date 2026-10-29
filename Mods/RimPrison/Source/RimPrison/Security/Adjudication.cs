using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>What the warden decided for a case on the sheet.</summary>
    public enum AdjudicationAnswer : byte
    {
        /// <summary>Not decided yet.</summary>
        Pending,
        /// <summary>The policy's sanction for the offence (also what a case nobody answered takes).</summary>
        Policy,
        /// <summary>The clean days they earned are wiped.</summary>
        LoseRemission,
        /// <summary>Days added to the sentence (struck out on appeal when nobody saw it and a jailhouse lawyer is in the prison).</summary>
        DaysAdded,
        /// <summary>The state's court sits for it (see <see cref="Trials"/>).</summary>
        Charge,
        /// <summary>The inmate was gone before the sheet came to it (released, dead, shipped out, a new stay): nothing to decide.</summary>
        Lapsed,
    }

    /// <summary>
    /// One serious offence waiting for the warden: who, what, when, and what the evidence is (the readings of <see cref="CourtUtil"/>: an officer in sight, a camera over the spot, the weapon in hand, the record).
    /// </summary>
    public class AdjudicationCase : IExposable
    {
        public int id;
        public int inmateId = -1;
        public string inmateName;
        public MisconductDef offence;
        public int tick;
        /// <summary>An officer saw it (a guard in sight, a manned tower, or staff who found it out directly) and, when a guard did, who.</summary>
        public bool officer;
        public string officerName;
        public bool camera, weapon;
        /// <summary>Serious offences seen on their file before this one.</summary>
        public int record;
        /// <summary>The sheet that listed it (-1: not listed yet).</summary>
        public int sheet = -1;
        public AdjudicationAnswer answer;
        public int answerTick = -1;
        /// <summary>Days added by the answer (struck out or not), or the days of earned remission it gave back.</summary>
        public int days;
        /// <summary>The days added were appealed at once and struck out (see <see cref="Adjudication.Lawyer"/>).</summary>
        public bool struckOut;
        /// <summary>For an assault on staff with a weapon, the one hit and the prosecution's file as the scene was (what a charge opens the court case with); null for what cannot be charged.</summary>
        public int victimId = -1;
        public string victimName;
        public MurderTrial scene;
        /// <summary>The court case a charge opened (-1 none).</summary>
        public int trialId = -1;

        /// <summary>An officer or a camera saw it: days can be added and stand.</summary>
        public bool Evidenced => officer || camera;

        public bool Chargeable => scene != null;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref inmateId, "inmateId", -1);
            Scribe_Values.Look(ref inmateName, "inmateName");
            Scribe_Defs.Look(ref offence, "offence");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref officer, "officer");
            Scribe_Values.Look(ref officerName, "officerName");
            Scribe_Values.Look(ref camera, "camera");
            Scribe_Values.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref record, "record");
            Scribe_Values.Look(ref sheet, "sheet", -1);
            Scribe_Values.Look(ref answer, "answer");
            Scribe_Values.Look(ref answerTick, "answerTick", -1);
            Scribe_Values.Look(ref days, "days");
            Scribe_Values.Look(ref struckOut, "struckOut");
            Scribe_Values.Look(ref victimId, "victimId", -1);
            Scribe_Values.Look(ref victimName, "victimName");
            Scribe_Deep.Look(ref scene, "scene");
            Scribe_Values.Look(ref trialId, "trialId", -1);
        }

        /// <summary>Everything saved about the case in one string (the saveload plan compares it).</summary>
        public string Describe() =>
            $"{id}:{inmateId}:{inmateName}:{offence?.defName}:{tick}:{officer}{camera}{weapon}:{officerName}:{record}:{sheet}:{answer}:{answerTick}:{days}:{struckOut}:{victimId}:{victimName}:{trialId}:{(scene != null ? Trials.Describe(scene) : "-")}";
    }

    /// <summary>The sheet's cases (answered ones kept for a month, for the file and the inspector), owned by <see cref="PrisonGame"/>.</summary>
    public class AdjudicationBook : IExposable
    {
        public List<AdjudicationCase> cases = new List<AdjudicationCase>();
        public int nextId = 1, nextSheet = 1;
        /// <summary>The day (tick / ticks per day) the last sheet went out: one sheet a day.</summary>
        public int lastSheetDay = -1;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref cases, "cases", LookMode.Deep);
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Values.Look(ref nextSheet, "nextSheet", 1);
            Scribe_Values.Look(ref lastSheetDay, "lastSheetDay", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                cases ??= new List<AdjudicationCase>();
                cases.RemoveAll(k => k == null || k.offence == null);
            }
        }

        public string Describe() => $"{nextId}/{nextSheet}/{lastSheetDay}:" + string.Join("+", cases.Select(k => k.Describe()));
    }

    /// <summary>
    /// The adjudication sheet (a policy, off by default): with "adjudicate the serious" on, a seen offence of severity <see cref="MinSeverity"/> and over (an assault on staff, an escape attempt, a riot's ringleader)
    /// takes no sanction from the table. It is queued as a case, with what the evidence is when it happened (<see cref="Queue"/>), and one letter a day at <see cref="SheetHour"/> lists yesterday's cases (at most
    /// <see cref="MaxPerSheet"/>; more wait for the next day). Each has four answers (<see cref="Answer"/>): the policy's sanction; loss of remission (the earned days given back to the sentence, the clean streak
    /// wiped); days added, up to <see cref="MaxDays"/> (the sentence everywhere reads <see cref="CompInmate.TermDays"/>; only with an officer's or a camera's evidence, else a jailhouse lawyer in the prison appeals
    /// and the days are struck out, at the prison's cost); or, for an assault on staff with a weapon, a charge: the state's court sits for it as for a murder (see <see cref="Trials"/>), with the witnesses the scene gave and
    /// a shorter sentence table. A case nobody answers (the letter timed out, the window was left) takes the policy's sanction. Lifers and death row cannot have days added. The days added in a week are
    /// what the inspector counts (<see cref="InspectionReport.AddedKey"/>), and are days of pay from the state.
    /// </summary>
    public static class Adjudication
    {
        public const int MinSeverity = 3, MaxPerSheet = 6, MaxDays = 30, SheetHour = 6;
        /// <summary>
        /// The hours a sheet's letter waits for an answer. Under a day, so that it has timed out (and taken the policy's sanction) before the next morning's pass: a letter that ended on that very tick would still count as
        /// out, and the day's sheet would not go.
        /// </summary>
        public const int SheetAnswerHours = 22;
        /// <summary>The days added in <see cref="WeekDays"/> days that the inspector writes down (more than this).</summary>
        public const int WeekLimit = 60, WeekDays = 7;
        /// <summary>How long an answered case is kept.</summary>
        public const int KeepDays = 30;
        public const float DaysTension = 8f, UnfairTension = 15f, RemissionTension = 5f;

        public const string DecisionDef = "RP_Decision_Adjudication";

        private static int Now => Find.TickManager.TicksGame;

        /// <summary>The offence is one the sheet decides (the warden adjudicates the serious, and this is serious): murder has its trial already, and a mark on the file is no offence.</summary>
        public static bool Takes(MisconductDef def) =>
            PrisonGame.Instance is { adjudicate: true } && def != null && def.severity >= MinSeverity && !def.mark && def != RPDeathDefOf.RP_Murder;

        /// <summary>The days the sheet starts at for this offence (what a stabbing is worth is the warden's to change).</summary>
        public static int SuggestedDays(MisconductDef def) => Mathf.Clamp(def.severity * 5 - 10, 5, MaxDays);

        // ---- queueing ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A seen offence the sheet takes becomes a case, with the evidence read off the spot now (an assault on staff with a weapon also keeps the prosecution's file as the scene is, for a charge). False when it
        /// cannot be queued (not an inmate here): the policy applies.
        /// </summary>
        public static bool Queue(Pawn p, CompInmate c, MisconductDef def, bool evidence)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = p.MapHeld;
            if (game == null || map == null || c == null || !p.IsPrisonerOfColony) return false;
            PrisonMap pm = PrisonMap.Of(map);
            var k = new AdjudicationCase
            {
                id = game.adjudication.nextId++, inmateId = p.thingIDNumber, inmateName = p.LabelShort, offence = def, tick = Now, officer = evidence,
                weapon = p.equipment?.Primary is { def.IsWeapon: true },
            };
            // (Seen offences have been counted and this one is on the file already: the record is what came before it.)
            k.record = Mathf.Max(0, c.records.Count(r => r.seen && r.def.severity >= 2) - 1);
            if (p.Spawned && pm != null)
            {
                IntVec3 at = p.Position;
                if (TensionUtility.GuardWhoSees(at, map, pm) is { } guard)
                    (k.officer, k.officerName) = (true, guard.LabelShort);
                else if (!k.officer)
                    for (int i = 0; i < pm.Towers.Count && !k.officer; i++)
                        k.officer = TensionUtility.TowerSees(pm.Towers[i], at);
                k.camera = CourtUtil.CameraOver(map, at);
            }
            // An assault on staff with a weapon can go to court: the scene as it is now is the prosecution's (the officers in sight, the camera, the weapon), as for a killing.
            if (def.chargeDays.max > 0 && k.weapon && p.MentalState is MentalState_Assaulting { Target: { } target } && target.GetComp<CompStaff>()?.role != null)
            {
                k.victimId = target.thingIDNumber;
                k.victimName = target.LabelShort;
                k.scene = Trials.ReadScene(map, target, p, c, staffVictim: true, null, offence: def);
                k.scene.priorRecord = k.record;
            }
            game.adjudication.cases.Add(k);
            return true;
        }

        // ---- the sheet -----------------------------------------------------------------------------------------------------------

        /// <summary>The cases listed on a sheet that nobody has answered yet.</summary>
        public static List<AdjudicationCase> Waiting(PrisonGame game, int sheet = -1) =>
            game.adjudication.cases.Where(k => k.answer == AdjudicationAnswer.Pending && k.sheet >= 0 && (sheet < 0 || k.sheet == sheet)).ToList();

        /// <summary>The cases that happened and are not on a sheet yet.</summary>
        public static List<AdjudicationCase> Queued(PrisonGame game) => game.adjudication.cases.Where(k => k.answer == AdjudicationAnswer.Pending && k.sheet < 0).OrderBy(k => k.tick).ToList();

        /// <summary>Whether the sheet's letter is still to be answered (on the stack, or held back for the day's budget).</summary>
        public static bool SheetLetterOpen(int sheet)
        {
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision d && !d.answered && d.Is(DecisionDef) && (sheet < 0 || d.amount == sheet)) return true;
            return false;
        }

        /// <summary>Whether the sheet's letter is still to be answered, or its window is open (the window is the warden at work on it: a sheet is not settled under them).</summary>
        public static bool SheetOpen(int sheet) => SheetLetterOpen(sheet) || Find.WindowStack?.Windows.OfType<Dialog_Adjudication>().Any() == true;

        /// <summary>
        /// The player's home map the case's inmate is on: <paramref name="prefer"/> if they are there, else another (the book is the game's, and each map's pass reaches it), null when they are on none (gone).
        /// </summary>
        public static Map HomeOf(AdjudicationCase k, Map prefer)
        {
            if (prefer != null && CourtUtil.FindInmate(prefer, k.inmateId) != null) return prefer;
            foreach (Map m in Find.Maps)
                if (m != prefer && m.IsPlayerHome && CourtUtil.FindInmate(m, k.inmateId) != null) return m;
            return null;
        }

        /// <summary>The inmate is not here to be decided on (gone from every home map, no longer active, or it was another stay).</summary>
        private static bool Gone(Map map, AdjudicationCase k)
        {
            Map home = HomeOf(k, map);
            Pawn p = home != null ? CourtUtil.FindInmate(home, k.inmateId) : null;
            return p == null || p.Inmate() is not { } c || !InmateUtility.IsActiveInmate(p) || k.tick < c.admittedTick;
        }

        /// <summary>
        /// The first morning hour: what was listed and never answered (no letter, no window) takes the policy's sanction; then, unless a sheet is out or one went today, yesterday's cases (the oldest
        /// <see cref="MaxPerSheet"/>) go on one letter. Cases whose inmate is gone are closed. The pass runs once per home map, but the book is the game's: whichever map's pass comes first writes the
        /// day's one sheet for every map (a case is answered on the map its inmate is on, see <see cref="Answer"/>). An open window does not hold the sheet back (it may have been left open overnight).
        /// </summary>
        public static void Morning(PrisonGame game, Map map)
        {
            AdjudicationBook book = game.adjudication;
            if (book.cases.Count == 0) return;
            foreach (AdjudicationCase stale in Waiting(game).Where(k => !SheetOpen(k.sheet)).ToList()) Answer(game, map, stale, AdjudicationAnswer.Policy);
            int today = Now / GenDate.TicksPerDay;
            Trim(book);
            if (book.lastSheetDay == today || SheetLetterOpen(-1)) return;
            int dayStart = Now - GenLocalDate.DayTick(map);
            var listed = new List<AdjudicationCase>();
            foreach (AdjudicationCase k in Queued(game))
            {
                if (k.tick >= dayStart) continue;
                if (Gone(map, k))
                {
                    (k.answer, k.answerTick) = (AdjudicationAnswer.Lapsed, Now);
                    continue;
                }
                if (listed.Count < MaxPerSheet) listed.Add(k);
            }
            if (listed.Count == 0) return;
            SendSheet(game, map, listed);
            book.lastSheetDay = today;
        }

        /// <summary>One letter for the cases: each with what the evidence is, the answers in the text, and a way into the window that answers them. Left alone it takes the policy's sanction for all.</summary>
        public static ChoiceLetter_PrisonDecision SendSheet(PrisonGame game, Map map, List<AdjudicationCase> listed)
        {
            int sheet = game.adjudication.nextSheet++;
            var pawns = new List<Pawn>();
            foreach (AdjudicationCase k in listed)
            {
                k.sheet = sheet;
                if (HomeOf(k, map) is { } home && CourtUtil.FindInmate(home, k.inmateId) is { } p) pawns.Add(p);
            }
            string lines = CourtUtil.Bullets(listed.Select(k => "RP_AdjudicationLine".Translate(k.inmateName, k.offence.label, EvidenceText(k)).Resolve()));
            string text = "RP_AdjudicationText".Translate(listed.Count, lines, MaxDays, Money.Format(ReputationBehavior_Lawyer.CourtCosts), WeekLimit, WeekDays).Resolve();
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(DecisionDef, "RP_AdjudicationLabel".Translate(listed.Count), text, map, new LookTargets(pawns));
            letter.amount = sheet;
            letter.pawns.AddRange(pawns);
            letter.StartTimeout(SheetAnswerHours * GenDate.TicksPerHour);
            Find.LetterStack.ReceiveLetter(letter);
            return letter;
        }

        /// <summary>Answered cases a month old are forgotten.</summary>
        private static void Trim(AdjudicationBook book) => book.cases.RemoveAll(k => k.answer != AdjudicationAnswer.Pending && k.answerTick >= 0 && Now - k.answerTick > KeepDays * GenDate.TicksPerDay);

        /// <summary>The evidence in words, for the sheet: who saw it, the footage, the weapon, the record (or that nobody can swear to it).</summary>
        public static string EvidenceText(AdjudicationCase k)
        {
            var parts = new List<string>();
            if (k.officer) parts.Add(k.officerName != null ? "RP_AdjEvidence_Officer".Translate(k.officerName).Resolve() : "RP_AdjEvidence_OfficerUnnamed".Translate().Resolve());
            if (k.camera) parts.Add("RP_AdjEvidence_Camera".Translate().Resolve());
            if (k.weapon) parts.Add("RP_AdjEvidence_Weapon".Translate().Resolve());
            if (k.record > 0) parts.Add("RP_AdjEvidence_Record".Translate(k.record).Resolve());
            return parts.Count > 0 ? string.Join(", ", parts) : "RP_AdjEvidence_None".Translate().Resolve();
        }

        // ---- the answers ------------------------------------------------------------------------------------------------------------

        /// <summary>The jailhouse lawyer in the prison (an active inmate with the reputation), if there is one: they file the appeal that strikes out days nobody saw.</summary>
        public static Pawn Lawyer(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c && InmateUtility.IsActiveInmate(p) && c.HasReputation(RPReputationDefOf.RP_JailhouseLawyer)) return p;
            return null;
        }

        /// <summary>Why this answer cannot be given to this case now (a keyed string), or null.</summary>
        public static string Refused(PrisonGame game, Map map, AdjudicationCase k, CompInmate c, AdjudicationAnswer a)
        {
            switch (a)
            {
                case AdjudicationAnswer.LoseRemission:
                    // (Only the days earned come back: a clean run towards the next is wiped with them, but is nothing to take on its own, whether or not the policy is on now.)
                    return !Remission.Eligible(c) || c.remissionEarned <= 0 ? "RP_AdjRefused_Remission" : null;
                case AdjudicationAnswer.DaysAdded:
                    return c.IsLifer || DeathRowSystem.Ext(c) != null ? "RP_AdjRefused_Days" : null;
                case AdjudicationAnswer.Charge:
                    if (!k.Chargeable) return "RP_AdjRefused_Charge";
                    if (c.IsLifer || DeathRowSystem.Ext(c) != null) return "RP_AdjRefused_ChargeLife";
                    return CourtUtil.FindStaff(map, k.victimId) == null ? "RP_AdjRefused_Victim" : null;
            }
            return null;
        }

        /// <summary>
        /// Decides a case. An answer that cannot be given (nothing earned to wipe, a lifer, nothing to charge) is the policy's sanction instead; returns the keyed reason in that case, else null. <paramref name="days"/>: the days to add
        /// (1 to <see cref="MaxDays"/>). The case is decided on the map its inmate is on, whichever map's sheet or window it was asked from.
        /// </summary>
        public static string Answer(PrisonGame game, Map map, AdjudicationCase k, AdjudicationAnswer a, int days = 0)
        {
            if (k.answer != AdjudicationAnswer.Pending) return null;
            map = HomeOf(k, map) ?? map;
            Pawn p = CourtUtil.FindInmate(map, k.inmateId);
            if (p == null || p.Inmate() is not { } c || !InmateUtility.IsActiveInmate(p) || k.tick < c.admittedTick)
            {
                (k.answer, k.answerTick) = (AdjudicationAnswer.Lapsed, Now);
                return null;
            }
            string why = a == AdjudicationAnswer.Policy ? null : Refused(game, map, k, c, a);
            if (why != null) a = AdjudicationAnswer.Policy;
            (k.answer, k.answerTick) = (a, Now);
            k.scene = a == AdjudicationAnswer.Charge ? k.scene : null;
            game.stats.adjudicated++;
            switch (a)
            {
                case AdjudicationAnswer.Policy:
                    // (A light sanction marks this case's own record, not the newest other one the policy would find; solitary marks it when the guard takes them, as the table's does.)
                    int light0 = game.stats.lightSanctions;
                    Sanctions.ApplyPolicy(p, c, k.offence, markLast: false);
                    if (game.stats.lightSanctions > light0) MarkPunished(c, k);
                    break;
                case AdjudicationAnswer.LoseRemission:
                    MarkPunished(c, k);
                    LoseRemission(game, p, c, k);
                    break;
                case AdjudicationAnswer.DaysAdded:
                    AddDays(game, map, p, c, k, Mathf.Clamp(days > 0 ? days : SuggestedDays(k.offence), 1, MaxDays));
                    if (!k.struckOut) MarkPunished(c, k);
                    break;
                case AdjudicationAnswer.Charge:
                    MarkPunished(c, k);
                    Charge(game, map, p, c, k);
                    break;
            }
            return why;
        }

        /// <summary>Every unanswered case of a sheet (or of all, <paramref name="sheet"/> -1) takes the answer (the policy's, when the letter timed out).</summary>
        public static void SettleSheet(PrisonGame game, Map map, int sheet, AdjudicationAnswer a)
        {
            foreach (AdjudicationCase k in Waiting(game, sheet)) Answer(game, map, k, a);
        }

        /// <summary>Their record of this offence is punished (the file shows it), as a light sanction marks one.</summary>
        private static void MarkPunished(CompInmate c, AdjudicationCase k)
        {
            int i = c.records.FindLastIndex(r => r.def == k.offence && r.tick == k.tick);
            if (i >= 0) c.records[i].punished = true;
        }

        private static void LoseRemission(PrisonGame game, Pawn p, CompInmate c, AdjudicationCase k)
        {
            // (The days they earned come back onto the sentence; the clean run starts again.)
            k.days = c.remissionEarned;
            c.sentenceDays += c.remissionEarned;
            c.remissionEarned = 0;
            c.cleanDays = 0;
            game.stats.remissionWiped++;
            Sanctioned(p, c, RemissionTension);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_AdjRemission".Translate(p.LabelShort, k.offence.label, k.days), p);
            Messages.Message("RP_AdjRemissionMessage".Translate(p.LabelShort, k.offence.label, k.days), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        private static void AddDays(PrisonGame game, Map map, Pawn p, CompInmate c, AdjudicationCase k, int days)
        {
            k.days = days;
            // Nobody who can swear to it: a jailhouse lawyer files the appeal (the prison pays the costs, as for any appeal they file) and the days are struck out.
            if (!k.Evidenced && Lawyer(map) is { } lawyer)
            {
                k.struckOut = true;
                game.ledger.Add(RPDefOf.RP_Fines, -ReputationBehavior_Lawyer.CourtCosts, "RP_AppealCostsNote".Translate(p.LabelShort));
                game.stats.appealsFiled++;
                game.stats.daysStruckOut += days;
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_AdjStruck".Translate(p.LabelShort, k.offence.label, days, lawyer.LabelShort), p, lawyer);
                Messages.Message("RP_AdjStruckMessage".Translate(lawyer.LabelShort, p.LabelShort, days, k.offence.label, Money.Format(ReputationBehavior_Lawyer.CourtCosts)), p, MessageTypeDefOf.NegativeEvent, historical: false);
                return;
            }
            c.daysAdded += days;
            game.stats.daysAddedGiven += days;
            // (Days nobody saw earned stand, and are called unfair: the cause the wing's tension names.)
            Sanctioned(p, c, k.Evidenced ? DaysTension : UnfairTension);
            if (!k.Evidenced) c.mainCause = "RP_Cause_Unfair";
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_AdjDays".Translate(p.LabelShort, k.offence.label, days, c.TermDays), p);
            Messages.Message("RP_AdjDaysMessage".Translate(p.LabelShort, days, c.TermDays, k.offence.label), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The state's court sits for the offence: the case the scene gave is opened, as for a killing (remand, the witnesses, the same odds), and a conviction adds the offence's days.</summary>
        private static void Charge(PrisonGame game, Map map, Pawn p, CompInmate c, AdjudicationCase k)
        {
            MurderTrial t = Trials.Register(game, map, k.scene, p);
            k.scene = null;
            k.trialId = t.id;
            game.stats.chargesBrought++;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_AdjCharge".Translate(p.LabelShort, k.offence.label, t.victimName, Trials.DateText(t)), p);
            Find.LetterStack.ReceiveLetter("RP_AdjChargeLabel".Translate(p.LabelShort), "RP_AdjChargeText".Translate(p.LabelShort, k.offence.label, t.victimName, Trials.DateText(t), Trials.KnownLinesText(t)),
                LetterDefOf.ThreatSmall, p);
        }

        /// <summary>The sanction is felt as one: tension up, the memory of being sanctioned.</summary>
        private static void Sanctioned(Pawn p, CompInmate c, float tension)
        {
            c.tension = Mathf.Min(100f, c.tension + tension);
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPSecurityDefOf.RP_Sanctioned);
        }

        // ---- what the file and the inspector read -------------------------------------------------------------------------------------

        /// <summary>The days added to sentences in the last <see cref="WeekDays"/> days (those that were not struck out).</summary>
        public static int DaysAddedInWeek(PrisonGame game)
        {
            int total = 0;
            foreach (AdjudicationCase k in game.adjudication.cases)
                if (k.answer == AdjudicationAnswer.DaysAdded && !k.struckOut && k.answerTick >= 0 && Now - k.answerTick <= WeekDays * GenDate.TicksPerDay) total += k.days;
            return total;
        }

        /// <summary>The answered cases of one inmate this stay, newest first (the file's lines).</summary>
        public static IEnumerable<AdjudicationCase> DecidedFor(PrisonGame game, Pawn p)
        {
            for (int i = game.adjudication.cases.Count - 1; i >= 0; i--)
            {
                AdjudicationCase k = game.adjudication.cases[i];
                if (k.inmateId == p.thingIDNumber && k.answer is not (AdjudicationAnswer.Pending or AdjudicationAnswer.Lapsed)) yield return k;
            }
        }

        /// <summary>The answer in words, not resolved yet (for a line that embeds it: a text is resolved once, or the days in it are coloured twice).</summary>
        private static TaggedString AnswerTagged(AdjudicationCase k)
        {
            switch (k.answer)
            {
                case AdjudicationAnswer.LoseRemission: return "RP_AdjAnswered_Remission".Translate(k.days);
                case AdjudicationAnswer.DaysAdded: return (k.struckOut ? "RP_AdjAnswered_Struck" : "RP_AdjAnswered_Days").Translate(k.days);
                case AdjudicationAnswer.Charge: return "RP_AdjAnswered_Charge".Translate();
                case AdjudicationAnswer.Lapsed: return "RP_AdjAnswered_Lapsed".Translate();
                default: return "RP_AdjAnswered_Policy".Translate();
            }
        }

        /// <summary>The answer in words, for the file and the window.</summary>
        public static string AnswerText(AdjudicationCase k) => AnswerTagged(k).Resolve();

        private const float ScreenCacheSeconds = 1f;
        private static readonly Dictionary<int, (float at, List<string> lines)> fileCache = new Dictionary<int, (float, List<string>)>();

        /// <summary>The file's lines for the screen (kept for a second per inmate, as <see cref="Trials.FileLinesCached"/>: the file asks on every frame).</summary>
        public static List<string> FileLinesCached(Pawn p)
        {
            float now = Time.realtimeSinceStartup;
            if (fileCache.TryGetValue(p.thingIDNumber, out (float at, List<string> lines) hit) && now - hit.at < ScreenCacheSeconds && now >= hit.at) return hit.lines;
            var lines = FileLines(p).ToList();
            fileCache[p.thingIDNumber] = (now, lines);
            return lines;
        }

        /// <summary>For the file: what the sheet did with this inmate's cases (one line each, the latest three).</summary>
        public static IEnumerable<string> FileLines(Pawn p)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || game.adjudication.cases.Count == 0 || p.Inmate() is not { } c) yield break;
            int shown = 0;
            foreach (AdjudicationCase k in DecidedFor(game, p))
            {
                if (k.tick < c.admittedTick || shown++ >= 3) continue;
                yield return "RP_FileAdjudication".Translate(k.offence.label, AnswerTagged(k)).Resolve();
            }
        }
    }

    /// <summary>
    /// The sheet's letter: open the window that decides each case, or leave it all to the policy. Unanswered (timed out, or held past its days) it is the policy for every case on it.
    /// </summary>
    public class PrisonDecisionWorker_Adjudication : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Adjudication_Open", "RP_Adjudication_Policy" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (PrisonGame.Instance is not { } game) return;
            if (option == 0)
            {
                if (Adjudication.Waiting(game, l.amount).Count > 0) Find.WindowStack.Add(new Dialog_Adjudication(map, l.amount));
                return;
            }
            Adjudication.SettleSheet(game, map, l.amount, AdjudicationAnswer.Policy);
        }
    }
}
