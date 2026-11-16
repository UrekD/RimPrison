using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPHurtDefOf
    {
        public static ThoughtDef RP_RushedBack;
        public static ThoughtDef RP_ColleagueRushedBack;
        public static ThoughtDef RP_LookedAfter;
        public static ThoughtDef RP_FiredWhileHurt;
        public static ThoughtDef RP_PensionSendOff;

        static RPHurtDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPHurtDefOf));
    }

    /// <summary>One time a member of staff was hurt by an inmate: when, by whom (the name as it was), how badly, and what the warden answered. The staff record keeps the latest five.</summary>
    public class InjuryCase : IExposable
    {
        /// <summary>From <see cref="StorySystem.nextInjuryCaseId"/>: on the case and on its letter, so that an answer pays once whatever sends it.</summary>
        public int id;
        public int tick;
        public int attackerId = -1;
        public string attackerName;
        /// <summary>One of <see cref="HurtOnDuty.Minor"/>, <see cref="HurtOnDuty.Serious"/>, <see cref="HurtOnDuty.Lasting"/>.</summary>
        public int level;
        /// <summary>0 not answered yet, else one of the <c>HurtOnDuty.Answer</c> constants.</summary>
        public int answer;
        /// <summary>A decision letter was sent for it (a minor case has none).</summary>
        public bool lettered;
        /// <summary>The money booked for the answer.</summary>
        public int paid;
        /// <summary>The other leave (a burnout leave, a course) that runs beside the injury leave and its reason - the one running when the case opened, or granted since (the longer): put back when the officer goes back on the next shift.</summary>
        public int priorLeaveUntil = -1;
        public int priorLeaveReason;
        /// <summary>When the injury's own entitlement to leave ends (the longest the case has set: never shortened): what "back on the next shift" is judged by, as the leave itself may run longer for another reason.</summary>
        public int injuryUntil = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref attackerId, "attackerId", -1);
            Scribe_Values.Look(ref attackerName, "attackerName");
            Scribe_Values.Look(ref level, "level");
            Scribe_Values.Look(ref answer, "answer");
            Scribe_Values.Look(ref lettered, "lettered");
            Scribe_Values.Look(ref paid, "paid");
            Scribe_Values.Look(ref priorLeaveUntil, "priorLeaveUntil", -1);
            Scribe_Values.Look(ref priorLeaveReason, "priorLeaveReason");
            Scribe_Values.Look(ref injuryUntil, "injuryUntil", -1);
        }

        public string Describe() => $"{id}@{tick}/{attackerId}:{attackerName}/L{level}/A{answer}/{lettered}/{paid}/{priorLeaveUntil}:{priorLeaveReason}/{injuryUntil}";
    }

    /// <summary>
    /// Hurt on duty: a member of staff put down by an inmate (or freed from a hostage crisis hurt) becomes a case. A minor one is a paid day off and a line in the chronicle; a serious or lasting one is
    /// one letter (shared by everyone hurt in the same hour) that asks what the prison does for them: injury leave on full pay, compensation with the leave, back on the next shift, and for a lasting injury
    /// a pension or a desk post. The leave starts when the case opens (the letter changes it), they heal at home (see <see cref="HomeHealing"/>), and on their return the fear of the one who did it is
    /// armed again for a week (<see cref="StaffWariness.Rearm"/>: the one effect; no new avoidance). The case is closed by the officer leaving the staff or dying (<see cref="Notify_Left"/>).
    /// </summary>
    public static class HurtOnDuty
    {
        public const string DecisionDef = "RP_Decision_HurtOnDuty";

        public const int Minor = 1, Serious = 2, Lasting = 3;
        public const int AnswerNone = 0, AnswerLeave = 1, AnswerCompensation = 2, AnswerBack = 3, AnswerPension = 4, AnswerDesk = 5;
        /// <summary>The reasons a leave is for (<see cref="CompStaff.leaveReason"/>).</summary>
        public const int ReasonNone = 0, ReasonTraining = 1, ReasonBurnout = 2, ReasonInjury = 3;

        public const string CauseAssault = "assault", CauseHostage = "hostage";

        public const int PensionPay = 1200, DeskSkill = 6, RecordKept = 5, FiredWhileHurtDays = 14, RushedBackDays = 6;
        /// <summary>A wound older than this is not the fight's: a permanent injury or a lost part only counts as gained in the incident when it is as young as the incident (an hour before the downing).</summary>
        public const int AssaultWindow = GenDate.TicksPerHour;

        public static int Days(int level) => level >= Lasting ? 5 : level >= Serious ? 3 : 1;

        public static int Compensation(int level) => level >= Lasting ? 800 : level >= Serious ? 400 : 150;

        public static InjuryCase CaseById(CompStaff sc, int id) => id < 0 ? null : sc.injuries.Find(c => c.id == id);

        /// <summary>The case the officer is in the middle of (none: -1 on the record).</summary>
        public static InjuryCase OpenCase(CompStaff sc) => sc == null ? null : CaseById(sc, sc.injuryCaseId);

        /// <summary>Whom a case can be for: permanent staff of the prison's own, not the warden or the state's chief (no payroll of theirs), not a callout, not one whose dismissal is pending.</summary>
        public static bool IsCandidate(Pawn p, out CompStaff sc)
        {
            sc = p?.GetComp<CompStaff>();
            return sc is { role: not null, Protected: false, pendingFire: 0, leaveAtTick: < 0 } && p.Faction == Faction.OfPlayer && !p.Dead && !p.Destroyed;
        }

        // ---- Severity -----------------------------------------------------------------------------------------------------

        /// <summary>
        /// How badly they are hurt, by what is visible now that is as young as the incident (<paramref name="sinceTick"/>: when it started; nothing is stored beforehand, and no hidden scar threshold is
        /// read): lasting - a missing part or a permanent visible injury; serious - a wound of 10 HP or more, or one still bleeding; else minor. A scar that forms later does not reopen the case.
        /// </summary>
        public static int Severity(Pawn p, int sinceTick)
        {
            int span = Math.Max(0, Find.TickManager.TicksGame - sinceTick), level = Minor;
            if (p?.health?.hediffSet == null) return level;
            foreach (Hediff h in p.health.hediffSet.hediffs)
            {
                if (h.ageTicks > span) continue;
                if (h is Hediff_MissingPart && h.Visible) return Lasting;
                if (h is not Hediff_Injury injury) continue;
                if (injury.IsPermanent() && injury.Visible) return Lasting;
                if (injury.Severity >= 10f || injury.Bleeding) level = Serious;
            }
            return level;
        }

        // ---- Opening a case -----------------------------------------------------------------------------------------------

        /// <summary>
        /// A member of staff was hurt by an inmate. Opens a case, or joins the one open for the same officer and attacker within the day (a worse severity upgrades it; no second payout, answered or not;
        /// an attacker that is not known joins any). Null: not a case (see <see cref="IsCandidate"/>).
        /// </summary>
        /// <param name="cause"><see cref="CauseAssault"/> or <see cref="CauseHostage"/>.</param>
        /// <param name="sinceTick">When the incident started (the hostage crisis's start); -1: an hour before now.</param>
        public static InjuryCase Open(Pawn staff, Pawn attacker, string cause, int sinceTick = -1)
        {
            if (PrisonGame.Instance == null || !IsCandidate(staff, out CompStaff sc)) return null;
            int now = Find.TickManager.TicksGame, attackerId = attacker?.thingIDNumber ?? -1;
            if (sinceTick < 0) sinceTick = now - AssaultWindow;
            InjuryCase joined = sc.injuries.FindLast(c => now - c.tick < GenDate.TicksPerDay && (attackerId < 0 || c.attackerId < 0 || c.attackerId == attackerId));
            if (joined != null)
            {
                if (joined.attackerId < 0 && attackerId >= 0) (joined.attackerId, joined.attackerName) = (attackerId, attacker.LabelShort);
                int worse = Severity(staff, Math.Min(sinceTick, joined.tick));
                if (worse > joined.level) Upgrade(staff, sc, joined, worse);
                return joined;
            }
            InjuryCase old = OpenCase(sc);
            // (The leave beside the injury's: what runs now, or - when that is the injury leave of the case this one follows - what that case kept beside it.)
            (int priorUntil, int priorReason) = !StaffLives.OnLeave(sc) ? (-1, ReasonNone) : sc.leaveReason != ReasonInjury ? (sc.leaveUntilTick, sc.leaveReason) : old != null ? (old.priorLeaveUntil, old.priorLeaveReason) : (-1, ReasonNone);
            var c = new InjuryCase
            {
                id = PrisonGame.Instance.story.nextInjuryCaseId++, tick = now, attackerId = attackerId, attackerName = attacker?.LabelShort,
                level = Severity(staff, sinceTick), priorLeaveUntil = priorUntil, priorLeaveReason = priorReason,
            };
            // (A case still open for another attacker is over as far as the letter goes: what ran for it was the leave.)
            if (old is { answer: AnswerNone }) old.answer = AnswerLeave;
            sc.injuries.Add(c);
            while (sc.injuries.Count > RecordKept) sc.injuries.RemoveAt(0);
            sc.injuryCaseId = c.id;
            WithdrawLetters(staff);
            // The default answer runs from the start: an officer whose letter is held back for the next day (or never read) is already on leave.
            StartLeave(sc, c, now + Days(c.level) * GenDate.TicksPerDay);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:hurt] case {c.id} for {staff.LabelShort}: level {c.level} ({cause}), attacker {c.attackerName ?? "?"}");
            if (c.level <= Minor)
            {
                c.answer = AnswerLeave;
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_HodMinor".Translate(staff.LabelShort, Days(c.level)), staff);
            }
            else SendLetter(staff, sc, c);
            return c;
        }

        /// <summary>
        /// A second hook for the same case found it worse: the level is raised and the leave made the new level's long (counted from the case's start while it runs, from now when they were hurt again after
        /// it; never shortened), unless they were sent back or pensioned; a case that had no letter gets its one (and is open again for it, ahead of another case of theirs that was open and not answered: that
        /// one is over as far as the letter goes, as in <see cref="Open"/>). No second payout: an answered case stays answered.
        /// </summary>
        private static void Upgrade(Pawn staff, CompStaff sc, InjuryCase c, int level)
        {
            c.level = level;
            bool needsLetter = !c.lettered && level > Minor;
            if (needsLetter)
            {
                if (OpenCase(sc) is { answer: AnswerNone } other && other != c) other.answer = AnswerLeave;
                c.answer = AnswerNone;
                sc.injuryCaseId = c.id;
                WithdrawLetters(staff);
            }
            if (c.answer is not (AnswerBack or AnswerPension))
                StartLeave(sc, c, (StaffLives.OnLeave(sc) ? c.tick : Find.TickManager.TicksGame) + Days(level) * GenDate.TicksPerDay);
            if (needsLetter) SendLetter(staff, sc, c);
            else RefreshLetters(staff);
        }

        /// <summary>
        /// Starts or lengthens an injury leave: a leave already running is never shortened (the longer of the two stands), and an injury leave stays the injury's even when another leave (burnout) was running.
        /// Nothing is said and nothing is paid here (wages go on as for any leave).
        /// </summary>
        public static void StartLeave(CompStaff sc, InjuryCase c, int until)
        {
            if (until > c.injuryUntil) c.injuryUntil = until;
            WriteLeave(sc, until, ReasonInjury);
        }

        /// <summary>The leave is the case's own: an injury leave, or - an old save's case has no entitlement stored, and its leave no reason - the leave the case started.</summary>
        private static bool IsCaseLeave(CompStaff sc, InjuryCase c) => sc.leaveReason == ReasonInjury || (c.injuryUntil < 0 && sc.leaveReason == ReasonNone);

        /// <summary>
        /// "Back on the next shift" has something to end: the injury's own entitlement still runs, and it runs past any other leave kept beside it (a burnout leave that outlasts it is theirs whatever is answered;
        /// the entitlement is the case's, not the leave's, which may run on for another reason). An old save's case has no entitlement stored: the leave's own end stands for it (see <see cref="IsCaseLeave"/>).
        /// </summary>
        public static bool InjuryRuns(CompStaff sc, InjuryCase c) =>
            StaffLives.OnLeave(sc) && IsCaseLeave(sc, c) && (c.injuryUntil >= 0 ? c.injuryUntil : sc.leaveUntilTick) > Math.Max(Find.TickManager.TicksGame, c.priorLeaveUntil);

        /// <summary>Every leave write goes through here (burnout, a course, an injury): the longer leave stands, and an injury leave is kept as the reason of one that runs.</summary>
        public static void WriteLeave(CompStaff sc, int until, int reason)
        {
            if (StaffLives.OnLeave(sc))
            {
                // (Another leave granted over an injury leave is kept beside it on the case: sent back on the next shift, that is what is left.)
                if (reason != ReasonInjury && sc.leaveReason == ReasonInjury && OpenCase(sc) is { } open && until > open.priorLeaveUntil) (open.priorLeaveUntil, open.priorLeaveReason) = (until, reason);
                if (until > sc.leaveUntilTick) sc.leaveUntilTick = until;
                if (reason == ReasonInjury || sc.leaveReason != ReasonInjury) sc.leaveReason = reason;
            }
            else
            {
                sc.leaveUntilTick = until;
                sc.leaveReason = reason;
            }
            Roster.Invalidate();
            StaffCareers.InvalidateLeads();
        }

        // ---- The letter ---------------------------------------------------------------------------------------------------

        private static string Row(Pawn p, CompStaff sc, InjuryCase c) =>
            "RP_HodRow".Translate(p.LabelShort, StaffCareers.Title(sc), c.attackerName ?? "RP_HodUnknownAttacker".Translate().ToString(), ("RP_HodLevel_" + c.level).Translate().ToString(), Days(c.level), Money.Format(Compensation(c.level))).Resolve();

        /// <summary>
        /// The letter's label and text from its live rows (an officer's case still open and not answered): a row each with the level, the days and the money as they are now, and the notes on what only some
        /// answers can give - so that an upgrade, a moot row or a late officer is as true on the letter as in the case. No live row: left as it was.
        /// </summary>
        private static void Refresh(ChoiceLetter_PrisonDecision l)
        {
            List<(Pawn pawn, CompStaff sc, InjuryCase c)> rows = LiveRows(l);
            if (rows.Count == 0) return;
            string notes = "";
            if (rows.Any(r => r.c.level >= Lasting)) notes += "\n\n" + "RP_HodNotePension".Translate().Resolve();
            if (rows.Any(r => r.c.level >= Lasting && DeskOk(r.pawn, r.sc))) notes += "\n\n" + "RP_HodNoteDesk".Translate(DeskSkill).Resolve();
            l.Text = "RP_HodText".Translate(string.Join("\n", rows.Select(r => Row(r.pawn, r.sc, r.c))), notes);
            l.Label = rows.Count == 1 ? "RP_HodLabel".Translate(rows[0].pawn.LabelShort) : "RP_HodLabelMany".Translate(rows.Count);
        }

        /// <summary>The letters still to be answered that name this officer (on the stack, or held for the next day) are written again.</summary>
        private static void RefreshLetters(Pawn p)
        {
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is(DecisionDef) && d.pawns.Contains(p)) Refresh(d);
        }

        /// <summary>One letter for everyone hurt in the same hour (a row for each); a serious or lasting case only: a minor one has none.</summary>
        private static void SendLetter(Pawn staff, CompStaff sc, InjuryCase c)
        {
            c.lettered = true;
            int now = Find.TickManager.TicksGame;
            if (SharedLetter(now) is { } shared)
            {
                if (!shared.pawns.Contains(staff)) shared.pawns.Add(staff);
                shared.note += "," + c.id;
                Refresh(shared);
                return;
            }
            Map map = staff.MapHeld ?? Find.AnyPlayerHomeMap;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(DecisionDef, "RP_HodLabel".Translate(staff.LabelShort), "RP_HodText".Translate(Row(staff, sc, c), ""), map, staff);
            letter.pawns.Add(staff);
            letter.note = c.id.ToString();
            letter.amount = now;
            Refresh(letter);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>The letter still to be answered that was written in the last hour (on the stack, or held for the next day): the next officer hurt in that hour is a row on it.</summary>
        private static ChoiceLetter_PrisonDecision SharedLetter(int now)
        {
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is(DecisionDef) && d.amount >= 0 && now - d.amount < GenDate.TicksPerHour) return d;
            return null;
        }

        private static HashSet<int> Ids(string note)
        {
            var ids = new HashSet<int>();
            if (note.NullOrEmpty()) return ids;
            foreach (string part in note.Split(','))
                if (int.TryParse(part, out int id)) ids.Add(id);
            return ids;
        }

        /// <summary>The officers a letter is still about: still a case (<see cref="IsCandidate"/>), their case open and not answered yet (a row of someone let go, dead, queued for dismissal or already answered is moot).</summary>
        public static List<(Pawn pawn, CompStaff sc, InjuryCase c)> LiveRows(ChoiceLetter_PrisonDecision l)
        {
            var rows = new List<(Pawn, CompStaff, InjuryCase)>();
            if (l?.pawns == null || l.note.NullOrEmpty()) return rows;
            HashSet<int> ids = Ids(l.note);
            foreach (Pawn p in l.pawns)
            {
                // (Still a case at all: let go, dead, pensioned, dismissal queued, state-posted or a callout make the row moot.)
                if (!IsCandidate(p, out CompStaff sc)) continue;
                if (OpenCase(sc) is { answer: AnswerNone } c && ids.Contains(c.id)) rows.Add((p, sc, c));
            }
            return rows;
        }

        // ---- The answers --------------------------------------------------------------------------------------------------

        /// <summary>The desk post: an administrator, for a lasting injury - Intellectual 6 or more and able to do the administrator's work.</summary>
        public static bool DeskOk(Pawn p, CompStaff sc)
        {
            if (RPParoleDefOf.RP_Administrator is not { } admin || sc.role == admin || !Unlocks.IsUnlocked(admin)) return false;
            if (p.skills?.GetSkill(SkillDefOf.Intellectual) is not { TotallyDisabled: false, Level: >= DeskSkill }) return false;
            foreach (WorkTypeDef w in admin.workTypes)
                if (p.WorkTypeIsDisabled(w)) return false;
            return true;
        }

        /// <summary>A memory for every colleague, on site and at home. A plain memory that names nobody: vanilla discards a social memory with no opinion offset at once, and every memory that names a pawn when that pawn is discarded (an officer let go at home is on the spot).</summary>
        private static void Colleagues(Pawn officer, Map map, ThoughtDef thought)
        {
            foreach (Pawn o in StaffCommute.AllStaff(map ?? Find.AnyPlayerHomeMap))
                if (o != officer && o is { Dead: false } && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(thought);
        }

        /// <summary>
        /// The answer for one officer. Re-checks everything the letter was written under (they are still a case - employed, not let go or queued for it, not state-posted, not a callout; the case is theirs and
        /// open and not answered), so that a timeout, a standing order, a letter held and then lapsed or answered twice pays at most once. What the prison cannot give turns into the leave: compensation or a pension
        /// it cannot pay, a pension for one who is down, a desk post for one who cannot do the work, and being sent back when the injury leave is already over.
        /// </summary>
        public static void Apply(Pawn p, CompStaff sc, InjuryCase c, string key, Map map)
        {
            if (!IsCandidate(p, out CompStaff current) || current != sc || OpenCase(sc) != c || c.answer != AnswerNone) return;
            PrisonGame game = PrisonGame.Instance;
            int now = Find.TickManager.TicksGame;
            switch (key)
            {
                case "RP_Hod_Compensation":
                case "RP_Hod_Pension" when c.level < Lasting:
                    int amount = Compensation(c.level);
                    if (!game.ledger.TrySpend(RPDefOf.RP_Wages, amount, "RP_HodCompNote".Translate(p.LabelShort))) goto default;
                    c.answer = AnswerCompensation;
                    c.paid = amount;
                    Colleagues(p, map, RPHurtDefOf.RP_LookedAfter);
                    Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_HodCompensated".Translate(p.LabelShort, Money.Format(amount)), p);
                    break;
                case "RP_Hod_Back" when InjuryRuns(sc, c):
                    c.answer = AnswerBack;
                    EndInjuryLeave(p, sc, c);
                    p.needs?.mood?.thoughts.memories.TryGainMemory(RPHurtDefOf.RP_RushedBack);
                    sc.rushedBackUntilTick = now + RushedBackDays * GenDate.TicksPerDay;
                    Colleagues(p, map, RPHurtDefOf.RP_ColleagueRushedBack);
                    StaffWariness.Rearm(p, sc, c.attackerId);
                    Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_HodRushedBack".Translate(p.LabelShort), p);
                    break;
                case "RP_Hod_Pension" when !p.Downed:
                    if (!game.ledger.TrySpend(RPDefOf.RP_Wages, PensionPay, "RP_HodPensionNote".Translate(p.LabelShort))) goto default;
                    c.answer = AnswerPension;
                    c.paid = PensionPay;
                    Colleagues(p, map, RPHurtDefOf.RP_PensionSendOff);
                    game.stats.staffRetired++;
                    Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_HodPensioned".Translate(p.LabelShort, StaffCareers.Title(sc), Money.Format(PensionPay)), p);
                    // (Out through the retirement path: no "resigned" letter, and the case closes with them.)
                    StaffUtility.Fire(p, record: false);
                    break;
                case "RP_Hod_Desk" when c.level >= Lasting && DeskOk(p, sc):
                    StaffRoleDef admin = RPParoleDefOf.RP_Administrator;
                    c.answer = AnswerDesk;
                    Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_HodDesk".Translate(p.LabelShort, StaffCareers.Title(sc), admin.label), p);
                    StaffUtility.ApplyRole(p, admin);
                    break;
                default:
                    // (The leave: already running since the case opened; also what an answer that could not be given falls back to.)
                    c.answer = AnswerLeave;
                    if (key == "RP_Hod_Pension" && p.Downed && c.level >= Lasting) Messages.Message("RP_HodPensionDown".Translate(), p, MessageTypeDefOf.RejectInput, historical: false);
                    else if (key is "RP_Hod_Compensation" or "RP_Hod_Pension") Messages.Message("RP_HodNoFunds".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput, historical: false);
                    break;
            }
            if (sc.role != null) TryClose(sc, c);
        }

        /// <summary>
        /// Back on the next shift: the injury leave ends. The other leave that ran beside it (see <see cref="InjuryCase.priorLeaveUntil"/>) is put back if it still runs; if it is over, nothing is owed for it
        /// (a rest it was to be is dropped) and a course booked in it is done now that they are back.
        /// </summary>
        private static void EndInjuryLeave(Pawn p, CompStaff sc, InjuryCase c)
        {
            if (!IsCaseLeave(sc, c)) return;
            if (c.priorLeaveUntil > Find.TickManager.TicksGame) (sc.leaveUntilTick, sc.leaveReason) = (c.priorLeaveUntil, c.priorLeaveReason);
            else
            {
                (sc.leaveUntilTick, sc.leaveReason) = (-1, ReasonNone);
                sc.restPending = false;
                if (sc.trainingPending) StaffTraining.Complete(p, sc);
            }
            Roster.Invalidate();
            StaffCareers.InvalidateLeads();
        }

        /// <summary>An answered case is closed once the leave is over, whatever its reason was (an old save's leave has none); before then it is what the leave is about.</summary>
        private static void TryClose(CompStaff sc, InjuryCase c)
        {
            if (c.answer != AnswerNone && sc.injuryCaseId == c.id && !StaffLives.OnLeave(sc) && sc.leaveReason != ReasonInjury) sc.injuryCaseId = -1;
        }

        // ---- The return, and the end of the case --------------------------------------------------------------------------

        /// <summary>
        /// A leave is over and they are back (through the gate for the shift, or in-house when they were kept on site): the reason is cleared, once, and an answered case closes. After an injury leave the fear of
        /// the one who did it is armed again for a week (see <see cref="StaffWariness.Rearm"/>).
        /// </summary>
        public static void Notify_Returned(Pawn p, CompStaff sc)
        {
            if (StaffLives.OnLeave(sc)) return;
            int reason = sc.leaveReason;
            sc.leaveReason = ReasonNone;
            InjuryCase c = OpenCase(sc);
            if (reason == ReasonInjury && (c ?? sc.injuries.LastOrDefault()) is { } back)
            {
                StaffWariness.Rearm(p, sc, back.attackerId);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_HodBack".Translate(p.LabelShort), p);
            }
            if (c != null) TryClose(sc, c);
        }

        /// <summary>Hourly, for an officer kept on site whose injury leave ran out (downed, in a medical bed: the commute never sent them home): the leave ends here, as a course's does (see <see cref="StaffTraining.CheckInHouse"/>).</summary>
        public static void CheckInHouse(Pawn p, CompStaff sc)
        {
            if (sc.leaveReason != ReasonInjury || sc.role == null || p?.Spawned != true || sc.trainingPending || StaffLives.OnLeave(sc)) return;
            if (sc.leaveUntilTick >= 0)
            {
                sc.leaveUntilTick = -1;
                Roster.Invalidate();
            }
            GrantRest(p, sc);
            Notify_Returned(p, sc);
        }

        /// <summary>
        /// Whether the leave that is over gives a rest (<see cref="RPStaffLifeDefOf.RP_Rested"/>): one a burnout leave asked for (pending), or a plain leave of no course and no injury. An injury leave is no rest (it has its
        /// own reason), but a burnout leave granted over it still is one, and so is one beside a course. Read before the course is done and the reason cleared.
        /// </summary>
        public static bool RestDue(CompStaff sc) => sc.restPending || (!sc.trainingPending && sc.leaveReason != ReasonInjury);

        /// <summary>The rest of a leave that is over, given in-house as at the gate (<see cref="StaffCommute"/>'s arrival): the pending one is cleared whichever share of the leave (a course, the injury) ended it.</summary>
        public static void GrantRest(Pawn p, CompStaff sc)
        {
            if (RestDue(sc)) p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_Rested);
            sc.restPending = false;
        }

        /// <summary>
        /// The officer is leaving the staff (dismissed, resigned, retired, pensioned) or has died: the case closes, an open letter's row for them is moot (a letter with no row left is withdrawn), and
        /// colleagues who see a hurt officer dismissed by the warden (within a fortnight of the case) hold it against the prison.
        /// </summary>
        /// <param name="dismissed">The warden's own dismissal (see <see cref="StaffUtility.Fire"/>): not a corruption dismissal, welfare's move, a resignation or a retirement.</param>
        public static void Notify_Left(Pawn p, CompStaff sc, bool dismissed)
        {
            if (sc == null || (sc.injuryCaseId < 0 && sc.injuries.Count == 0 && sc.leaveReason == ReasonNone)) return;
            int now = Find.TickManager.TicksGame;
            bool recent = sc.injuries.Exists(c => now - c.tick < FiredWhileHurtDays * GenDate.TicksPerDay);
            sc.injuryCaseId = -1;
            sc.leaveReason = ReasonNone;
            sc.rushedBackUntilTick = -1;
            WithdrawLetters(p);
            if (dismissed && recent) Colleagues(p, p.MapHeld ?? Find.AnyPlayerHomeMap, RPHurtDefOf.RP_FiredWhileHurt);
        }

        /// <summary>A dismissal is queued for them (held hostage, carried, in a pod): it will go through, so the case is moot now - an open letter's row for them is not payable meanwhile.</summary>
        public static void Notify_DismissalQueued(Pawn p) => WithdrawLetters(p);

        /// <summary>A letter whose every row is moot is closed (answered, off the stack and the held ones); one with rows left is written again without the moot ones.</summary>
        private static void WithdrawLetters(Pawn p)
        {
            foreach (Letter l in StoryLetters.Open().ToList())
            {
                if (l is not ChoiceLetter_PrisonDecision { answered: false } d || !d.Is(DecisionDef) || !d.pawns.Contains(p)) continue;
                if (LiveRows(d).Count > 0)
                {
                    Refresh(d);
                    continue;
                }
                d.answered = true;
                PrisonGame.Instance?.story.deferredLetters.Remove(d);
                Find.LetterStack.RemoveLetter(d);
            }
        }

        // ---- The record ---------------------------------------------------------------------------------------------------

        /// <summary>The "hurt on duty" lines of the staff record, newest first (the latest five cases).</summary>
        public static List<string> RecordLines(CompStaff sc)
        {
            var lines = new List<string>();
            for (int i = sc.injuries.Count - 1; i >= 0; i--)
            {
                InjuryCase c = sc.injuries[i];
                lines.Add("RP_HodRecordLine".Translate(Story.When(c.tick), ("RP_HodLevel_" + c.level).Translate().ToString(), c.attackerName ?? "RP_HodUnknownAttacker".Translate().ToString(), ("RP_HodAnswer_" + c.answer).Translate().ToString()).Resolve());
            }
            return lines;
        }
    }

    /// <summary>The letter: leave, compensation with the leave, or back on the next shift; for a lasting injury a pension or a desk post. An answer applies to every officer on the letter (each at their own level); what cannot be given to one of them (a pension for an injury that is not lasting) is the compensation for them.</summary>
    public class HurtOnDutyDecision : PrisonDecisionWorker
    {
        private const string Leave = "RP_Hod_Leave", Compensation = "RP_Hod_Compensation", Back = "RP_Hod_Back", Pension = "RP_Hod_Pension", Desk = "RP_Hod_Desk";
        private static readonly string[] Plain = { Leave, Compensation, Back }, WithPension = { Leave, Compensation, Back, Pension }, WithDesk = { Leave, Compensation, Back, Pension, Desk };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l)
        {
            List<(Pawn pawn, CompStaff sc, InjuryCase c)> rows = HurtOnDuty.LiveRows(l);
            var lasting = rows.Where(r => r.c.level >= HurtOnDuty.Lasting).ToList();
            if (lasting.Count == 0) return Plain;
            return lasting.Any(r => HurtOnDuty.DeskOk(r.pawn, r.sc)) ? WithDesk : WithPension;
        }

        /// <summary>No answer is the leave (already running).</summary>
        public override int TimeoutChoice => 0;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            IReadOnlyList<string> options = Options(l);
            if (option < 0 || option >= options.Count || options[option] == Leave) return null;
            List<(Pawn pawn, CompStaff sc, InjuryCase c)> rows = HurtOnDuty.LiveRows(l);
            if (rows.Count == 0) return "RP_OfficerGone".Translate(l.pawns.FirstOrDefault(x => x != null)?.LabelShort ?? "?").ToString();
            int funds = PrisonGame.Instance.ledger.funds;
            switch (options[option])
            {
                case Compensation:
                    int need = rows.Sum(r => HurtOnDuty.Compensation(r.c.level));
                    return funds < need ? "RP_CannotAfford".Translate(Money.Format(need)).ToString() : null;
                case Back:
                    // (Sent back when the injury's days have run out - a letter held, or left a day - or when another leave runs past them is no story: nothing is ended.)
                    return rows.Any(r => HurtOnDuty.InjuryRuns(r.sc, r.c)) ? null : "RP_HodBackOver".Translate().ToString();
                case Pension:
                    var lasting = rows.Where(r => r.c.level >= HurtOnDuty.Lasting).ToList();
                    if (lasting.Any(r => r.pawn.Downed)) return "RP_HodPensionDown".Translate().ToString();
                    int pension = lasting.Count * HurtOnDuty.PensionPay + rows.Where(r => r.c.level < HurtOnDuty.Lasting).Sum(r => HurtOnDuty.Compensation(r.c.level));
                    return funds < pension ? "RP_CannotAfford".Translate(Money.Format(pension)).ToString() : null;
                default:
                    return null;
            }
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            IReadOnlyList<string> options = Options(l);
            string key = option >= 0 && option < options.Count ? options[option] : Leave;
            foreach ((Pawn pawn, CompStaff sc, InjuryCase c) in HurtOnDuty.LiveRows(l))
                HurtOnDuty.Apply(pawn, sc, c, key, map);
        }
    }
}
