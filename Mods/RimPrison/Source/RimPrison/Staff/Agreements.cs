using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>What the staff are angry about (the letter's cause, see <see cref="IncidentWorker_StaffStrike.Grievance"/>). The member names are a serialized format (the letter's note, the book's saved fields): do not rename them.</summary>
    public enum StaffGrievance : byte { Understaffed, Safety, Nights, Pay }

    public enum AgreementOutcome : byte { Fulfilled, Broken }

    [DefOf]
    public static class RPAgreementDefOf
    {
        public static ThoughtDef RP_KeptTheirWord;
        public static ThoughtDef RP_BrokenPromise;

        static RPAgreementDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPAgreementDefOf));
    }

    /// <summary>The promise to the staff that is running: what was promised, to whom, and how the prison has kept it so far.</summary>
    public class StaffAgreement : IExposable
    {
        public StaffGrievance grievance;
        /// <summary>The representative (thing ID, -1 none; the name outlives them) and their role: the one whose wage rises for pay, the one named in the panel for the others.</summary>
        public int repId = -1;
        public string repName;
        public StaffRoleDef role;
        public int agreedTick, deadlineTick;
        /// <summary>Understaffing only: the guards the roster had on duty in each hour of the day when the letter was made (24 of them, taken once and carried by the letter): the term is one more than this, at every hour.</summary>
        public List<int> baselines;
        /// <summary>Hours measured so far, those that met the term, and how many have to (counted at each hourly pass).</summary>
        public int hoursSeen, compliantHours, hoursNeeded;
        /// <summary>A member of staff was hurt by an inmate at this tick (-1: not since it was agreed).</summary>
        public int hurtTick = -1;
        /// <summary>Midnight settlements paid with the rise in them.</summary>
        public int paidSettlements;

        /// <summary>Guards on duty the term wants in this hour of the day (understaffing: one more than the roster had).</summary>
        public int TargetAt(int hour) => baselines is { Count: 24 } ? baselines[hour] + 1 : 1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref grievance, "grievance");
            Scribe_Values.Look(ref repId, "repId", -1);
            Scribe_Values.Look(ref repName, "repName");
            Scribe_Defs.Look(ref role, "role");
            Scribe_Values.Look(ref agreedTick, "agreedTick");
            Scribe_Values.Look(ref deadlineTick, "deadlineTick");
            Scribe_Collections.Look(ref baselines, "baselines", LookMode.Value);
            Scribe_Values.Look(ref hoursSeen, "hoursSeen");
            Scribe_Values.Look(ref compliantHours, "compliantHours");
            Scribe_Values.Look(ref hoursNeeded, "hoursNeeded");
            Scribe_Values.Look(ref hurtTick, "hurtTick", -1);
            Scribe_Values.Look(ref paidSettlements, "paidSettlements");
        }
    }

    /// <summary>How an agreement ended, for the panel and for the "broken before" rule.</summary>
    public class AgreementRecord : IExposable
    {
        public StaffGrievance grievance;
        public AgreementOutcome outcome;
        public int day;

        public void ExposeData()
        {
            Scribe_Values.Look(ref grievance, "grievance");
            Scribe_Values.Look(ref outcome, "outcome");
            Scribe_Values.Look(ref day, "day");
        }
    }

    /// <summary>A wage rise the staff were promised and have: it stays until the warden takes it back (within <see cref="StaffAgreements.TakeBackDays"/> of agreeing - the term and the peace after it - taking it back breaks the promise).</summary>
    public class PayRise : IExposable
    {
        public StaffRoleDef role;
        public int agreedTick;
        /// <summary>The day the promise was kept (-1: still being kept), to find its record if the rise is taken back in time.</summary>
        public int keptDay = -1;
        /// <summary>The panel's line for it, kept with what it was made from (not saved: see <see cref="StaffAgreements.RiseLine"/>).</summary>
        internal string lineText, lineShort;
        internal int lineTenths = -1;
        internal LoadedLanguage lineLanguage;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref role, "role");
            Scribe_Values.Look(ref agreedTick, "agreedTick");
            Scribe_Values.Look(ref keptDay, "keptDay", -1);
        }
    }

    /// <summary>Everything saved about staff agreements (old saves have none: all of it is at its default).</summary>
    public class StaffAgreementBook : IExposable
    {
        public StaffAgreement active;
        public List<AgreementRecord> history = new List<AgreementRecord>();
        /// <summary>No demand over a grievance until this tick (a kept promise buys a stretch of peace).</summary>
        public Dictionary<StaffGrievance, int> peaceUntil = new Dictionary<StaffGrievance, int>();
        public List<PayRise> rises = new List<PayRise>();
        /// <summary>The hour (ticks / an hour) last measured: an hour is counted once, however often the pass is asked for it.</summary>
        public int lastHourStamp = -1;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref active, "active");
            Scribe_Collections.Look(ref history, "history", LookMode.Deep);
            Scribe_Collections.Look(ref peaceUntil, "peaceUntil", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref rises, "rises", LookMode.Deep);
            Scribe_Values.Look(ref lastHourStamp, "lastHourStamp", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                history ??= new List<AgreementRecord>();
                peaceUntil ??= new Dictionary<StaffGrievance, int>();
                rises ??= new List<PayRise>();
                history.RemoveAll(h => h == null);
                rises.RemoveAll(x => x?.role == null);
                if (active is { grievance: StaffGrievance.Pay, role: null }) active = null; // (its role is gone: the rise has nothing to be paid to)
            }
        }
    }

    /// <summary>
    /// Staff agreements (gap analysis 6, item 6): the staff's demand letter has a third answer besides the bonus and the strike - agree to fix what they are angry about. The promise has a term per grievance
    /// (see <see cref="StaffGrievance"/>), measured at each hourly pass over the next <see cref="TermDays"/> days and judged at the deadline, the way the inmates' promises are (<see cref="DemandSystem"/>). Kept:
    /// the staff remember it and no demand over that grievance for <see cref="PeaceDays"/> days; broken: they remember that more, there is no peace, and the next demand over it offers no bonus.
    /// One agreement runs at a time; the last <see cref="HistoryMax"/> are kept. The cover it measures is the commute's own count of guards on site and on shift (<see cref="StaffCommute.GuardsOnDuty"/>), taken at each
    /// hour, never a roster headcount that could be filled up for the last hour; for nights it is the grievance's own test, looked at each hour.
    /// </summary>
    public static class StaffAgreements
    {
        public const int TermDays = 5, PeaceDays = 15, TakeBackDays = TermDays + PeaceDays, HistoryMax = 10;
        public const float ComplianceShare = 0.8f, PayRiseShare = 0.1f;

        public static StaffAgreementBook Book => PrisonGame.Instance?.agreements;

        /// <summary>Hourly looks the grievance is measured in: every hour of the term for understaffing and nights.</summary>
        public static int TermHours(StaffGrievance g) => g is StaffGrievance.Understaffed or StaffGrievance.Nights ? TermDays * 24 : 0;

        /// <summary>The hours that must meet the term (80%, rounded up).</summary>
        public static int HoursNeeded(StaffGrievance g) => Mathf.CeilToInt(TermHours(g) * ComplianceShare - 0.001f);

        // ---- who speaks, and for whom ---------------------------------------------------------------------------------

        /// <summary>Whether this member of staff is party to the grievance (the state's appointees and the warden never are; temporary staff are not on the payroll).</summary>
        public static bool Grieves(CompStaff sc, StaffGrievance g, StaffRoleDef role) =>
            sc.role != null && !sc.Protected && sc.leaveAtTick < 0 && (g == StaffGrievance.Pay ? sc.role == role : sc.role.guardDuties);

        /// <summary>
        /// The role in dispute: for pay the role with the lowest mood on average (the one most likely to be angry about its wage); for the others the guards, whose role is the representative's.
        /// </summary>
        public static StaffRoleDef PayRole(List<Pawn> staff)
        {
            StaffRoleDef worst = null;
            float worstMood = float.MaxValue;
            foreach (IGrouping<StaffRoleDef, Pawn> group in staff.Where(p => p.GetComp<CompStaff>() is { role: not null, Protected: false, leaveAtTick: < 0 }).GroupBy(p => p.GetComp<CompStaff>().role))
            {
                float mood = group.Average(p => p.needs?.mood?.CurLevelPercentage ?? 0.5f);
                if (mood < worstMood)
                {
                    worstMood = mood;
                    worst = group.Key;
                }
            }
            return worst;
        }

        /// <summary>
        /// The representative: the most senior of the grieving role (rank, then service), never a state appointee. The demand letter names no one today, so this is who it names. Null when nobody who is not an
        /// appointee has the grievance.
        /// </summary>
        public static Pawn Representative(Map map, StaffGrievance g)
        {
            List<Pawn> staff = StaffCommute.AllStaff(map);
            StaffRoleDef role = g == StaffGrievance.Pay ? PayRole(staff) : null;
            Pawn best = null;
            foreach (Pawn p in staff)
            {
                CompStaff sc = p.GetComp<CompStaff>();
                if (!Grieves(sc, g, role) || p.Dead) continue;
                if (best == null || MoreSenior(p, sc, best, best.GetComp<CompStaff>())) best = p;
            }
            return best;
        }

        private static bool MoreSenior(Pawn p, CompStaff sc, Pawn other, CompStaff osc)
        {
            if (sc.rank != osc.rank) return sc.rank > osc.rank;
            float days = StaffLives.DaysEmployed(sc), odays = StaffLives.DaysEmployed(osc);
            if (!Mathf.Approximately(days, odays)) return days > odays;
            return p.thingIDNumber < other.thingIDNumber;
        }

        // ---- the rules around a demand --------------------------------------------------------------------------------

        public static bool InPeace(StaffGrievance g) => Book is { } b && b.peaceUntil.TryGetValue(g, out int until) && Find.TickManager.TicksGame < until;

        /// <summary>No demand over this grievance now: the staff are at peace over it, or the promise to fix it is still being kept.</summary>
        public static bool Blocked(StaffGrievance g) => InPeace(g) || Book?.active?.grievance == g;

        /// <summary>The last promise over this grievance was broken: no bonus is offered until one is kept (the staff want it fixed, not paid for).</summary>
        public static bool BrokenBefore(StaffGrievance g)
        {
            if (Book is not { } b) return false;
            for (int i = b.history.Count - 1; i >= 0; i--)
                if (b.history[i].grievance == g) return b.history[i].outcome == AgreementOutcome.Broken;
            return false;
        }

        public static bool RiseStands(StaffRoleDef role)
        {
            if (role == null || Book is not { } b) return false;
            for (int i = 0; i < b.rises.Count; i++)
                if (b.rises[i].role == role) return true;
            return false;
        }

        /// <summary>The role's name in the plural, as the texts say it ("the guards have a rise").</summary>
        public static string Plural(StaffRoleDef role) => Find.ActiveLanguageWorker.Pluralize(role.label);

        /// <summary>The wage rise the role has (0.1: a tenth more).</summary>
        public static float RiseFor(StaffRoleDef role) => RiseStands(role) ? PayRiseShare : 0f;

        /// <summary>Why nobody can speak for the staff over this: there are no guards to speak for the guards' grievances, or only the state's own appointees are left (a clause, lower case).</summary>
        public static string NoRepBecause(Map map, StaffGrievance g) =>
            g != StaffGrievance.Pay && !IncidentWorker_StaffStrike.Staff(map).Any(GuardUtility.IsGuard) ? "RP_NoGuardsBecause".Translate().Resolve() : "RP_NoRepBecause".Translate().Resolve();

        /// <summary>Why the warden cannot agree to this just now, with <paramref name="rep"/> speaking for the staff (null: they can).</summary>
        public static string CannotAgree(Map map, StaffGrievance g, Pawn rep)
        {
            if (Book?.active != null) return "RP_AgreementOneAtATime".Translate();
            if (rep?.GetComp<CompStaff>() is not { role: not null } sc) return NoRepBecause(map, g).CapitalizeFirst() + ".";
            if (g == StaffGrievance.Pay && RiseStands(sc.role)) return "RP_AgreementRiseStands".Translate(Plural(sc.role));
            return null;
        }

        // ---- agreeing -------------------------------------------------------------------------------------------------

        /// <summary>The term in words, as the letter and the panel give it.</summary>
        public static string TermText(StaffGrievance g, StaffRoleDef role) => g switch
        {
            StaffGrievance.Understaffed => "RP_AgreementTerm_Understaffed".Translate(HoursNeeded(g), TermHours(g), TermDays).Resolve(),
            StaffGrievance.Nights => "RP_AgreementTerm_Nights".Translate(HoursNeeded(g), TermHours(g), TermDays).Resolve(),
            StaffGrievance.Safety => "RP_AgreementTerm_Safety".Translate(TermDays).Resolve(),
            _ => "RP_AgreementTerm_Pay".Translate(PayRiseShare.ToStringPercent(), role == null ? "-" : Plural(role), TermDays, TakeBackDays).Resolve(),
        };

        /// <summary>
        /// The cover the understaffing term is measured against: the guards the roster has on duty in each hour of the day as it is now (a copy: the strip is the roster's own). Taken once, when the demand letter is made, and
        /// carried by the letter, so that what the warden agrees to is what the letter said whenever they answer.
        /// </summary>
        public static int[] RosterBaseline(Map map) => (int[])Roster.Strip(map).Clone();

        /// <summary>The letter's note: the grievance, and for understaffing the baseline (24 counts) after a colon. The member names of <see cref="StaffGrievance"/> are in it.</summary>
        public static string LetterNote(StaffGrievance g, int[] baselines) => baselines == null ? g.ToString() : g + ":" + string.Join(",", baselines);

        /// <summary>Reads a letter's note back (false: no note, or not one of ours - a letter from before agreements).</summary>
        public static bool ParseLetterNote(string note, out StaffGrievance g, out int[] baselines)
        {
            g = default;
            baselines = null;
            if (note == null) return false;
            string[] parts = note.Split(':');
            if (!System.Enum.TryParse(parts[0], out g) || !System.Enum.IsDefined(typeof(StaffGrievance), g)) return false;
            if (parts.Length > 1)
            {
                string[] counts = parts[1].Split(',');
                var read = new int[24];
                bool whole = counts.Length == 24;
                for (int i = 0; whole && i < 24; i++) whole = int.TryParse(counts[i], out read[i]);
                if (whole) baselines = read;
            }
            return true;
        }

        /// <summary>Promises are made: the term starts now. <paramref name="baselines"/> is the letter's (understaffing; null: the roster's now). False when one cannot be (another is running, nobody to speak for the staff, the role has a rise).</summary>
        public static bool Agree(Map map, PrisonGame game, StaffGrievance g, Pawn rep, int[] baselines = null)
        {
            StaffAgreementBook book = game.agreements;
            if (book.active != null || rep?.GetComp<CompStaff>() is not { role: not null } sc) return false;
            if (g == StaffGrievance.Pay && RiseStands(sc.role)) return false;
            int now = Find.TickManager.TicksGame;
            var a = new StaffAgreement
            {
                grievance = g,
                role = sc.role,
                repId = rep.thingIDNumber,
                repName = rep.LabelShort,
                agreedTick = now,
                deadlineTick = now + TermDays * GenDate.TicksPerDay,
                baselines = g == StaffGrievance.Understaffed ? (baselines ?? RosterBaseline(map)).ToList() : null,
                hoursNeeded = HoursNeeded(g),
            };
            book.active = a;
            if (g == StaffGrievance.Pay) book.rises.Add(new PayRise { role = sc.role, agreedTick = now });
            string term = TermText(g, sc.role);
            Messages.Message("RP_AgreementMade".Translate(a.repName, term), rep, MessageTypeDefOf.PositiveEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_AgreementMade".Translate(GrievanceLabel(g), a.repName, term), rep);
            return true;
        }

        public static string GrievanceLabel(StaffGrievance g) => ("RP_Grievance_" + g).Translate().Resolve();

        // ---- measuring and judging ------------------------------------------------------------------------------------

        /// <summary>
        /// Hourly (after the commute's pass, so that those who came in for this hour are on site and those sent home are not): the hour is measured against the term, and the promise is judged when it is due. An hour
        /// is counted once however often this is called for it (a load puts the clock back on the hour it saved in).
        /// </summary>
        public static void Hourly(Map map, PrisonGame game, int hour)
        {
            StaffAgreementBook book = game.agreements;
            StaffAgreement a = book.active;
            if (a == null) return;
            int stamp = Find.TickManager.TicksGame / GenDate.TicksPerHour;
            if (stamp == book.lastHourStamp) return;
            book.lastHourStamp = stamp;
            int now = Find.TickManager.TicksGame;
            switch (a.grievance)
            {
                case StaffGrievance.Understaffed:
                    a.hoursSeen++;
                    if (StaffCommute.GuardsOnDuty(map, hour) >= a.TargetAt(hour)) a.compliantHours++;
                    break;
                case StaffGrievance.Nights:
                    // The grievance itself, looked at every hour over everyone employed (at home or not): met when too many of the staff are not on nights any more.
                    a.hoursSeen++;
                    if (!IncidentWorker_StaffStrike.TooManyOnNights(map)) a.compliantHours++;
                    break;
                case StaffGrievance.Safety:
                    // Broken the hour it is, not at the deadline: nothing the prison does in the days left makes up for it (a blow after the deadline is nothing).
                    if (a.hurtTick >= 0 && a.hurtTick < a.deadlineTick)
                    {
                        Judge(map, game, AgreementOutcome.Broken);
                        return;
                    }
                    break;
                case StaffGrievance.Pay:
                    // Kept once the fifth midnight's pay has gone out with the rise in it; the settlement can come a few minutes after the hour's pass, so one more hour is given before it counts as unpaid.
                    if (a.paidSettlements >= TermDays) Judge(map, game, AgreementOutcome.Fulfilled);
                    else if (now >= a.deadlineTick + GenDate.TicksPerHour) Judge(map, game, AgreementOutcome.Broken);
                    return;
            }
            if (now < a.deadlineTick) return;
            Judge(map, game, a.grievance == StaffGrievance.Safety || a.compliantHours >= a.hoursNeeded ? AgreementOutcome.Fulfilled : AgreementOutcome.Broken);
        }

        /// <summary>Midnight, with the wages paid: a settlement that had the rise in it counts towards the promise.</summary>
        public static void Notify_WagesPaid(PrisonGame game)
        {
            if (game.agreements.active is { grievance: StaffGrievance.Pay } a && RiseStands(a.role)) a.paidSettlements++;
        }

        /// <summary>An inmate's blow landed on a member of staff.</summary>
        public static void Notify_StaffHurt()
        {
            // (The first blow is the one that counts: a later one must not hide it.)
            if (Book?.active is { grievance: StaffGrievance.Safety, hurtTick: < 0 } a) a.hurtTick = Find.TickManager.TicksGame;
        }

        /// <summary>The promise is over: kept or broken. Whatever calls this, it is judged once (the promise is gone from the book before anything is said or remembered).</summary>
        public static void Judge(Map map, PrisonGame game, AgreementOutcome outcome)
        {
            StaffAgreementBook book = game.agreements;
            StaffAgreement a = book.active;
            if (a == null) return;
            book.active = null;
            bool kept = outcome == AgreementOutcome.Fulfilled;
            int now = Find.TickManager.TicksGame, day = Ledger.CurrentDay;
            Record(book, a.grievance, outcome, day);
            if (kept) book.peaceUntil[a.grievance] = now + PeaceDays * GenDate.TicksPerDay;
            else book.peaceUntil.Remove(a.grievance);
            if (kept && a.grievance == StaffGrievance.Pay && book.rises.FirstOrDefault(x => x.role == a.role && x.agreedTick == a.agreedTick) is { } rise) rise.keptDay = day;
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>() is { } sc && Grieves(sc, a.grievance, a.role))
                {
                    if (kept) StaffMorale.KeptTheirWord(p);
                    else StaffMorale.BrokenPromise(p);
                }
            string label = GrievanceLabel(a.grievance);
            Pawn rep = StaffCommute.AllStaff(map).FirstOrDefault(p => p.thingIDNumber == a.repId);
            Messages.Message((kept ? "RP_AgreementKept" : "RP_AgreementBroken").Translate(label, a.repName), kept ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, historical: true);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, (kept ? "RP_Chr_AgreementKept" : "RP_Chr_AgreementBroken").Translate(label, a.repName), rep != null ? new[] { rep } : new Pawn[0]);
        }

        private static void Record(StaffAgreementBook book, StaffGrievance g, AgreementOutcome outcome, int day)
        {
            book.history.Add(new AgreementRecord { grievance = g, outcome = outcome, day = day });
            while (book.history.Count > HistoryMax) book.history.RemoveAt(0);
        }

        // ---- the rise, and taking it back -----------------------------------------------------------------------------

        /// <summary>Whether taking the rise back now would break the promise that gave it (it is still being kept, or was kept less than <see cref="TakeBackDays"/> after it was agreed).</summary>
        public static bool TakingBackBreaks(PayRise rise) =>
            (Book?.active is { grievance: StaffGrievance.Pay } a && a.role == rise.role) || Find.TickManager.TicksGame < rise.agreedTick + TakeBackDays * GenDate.TicksPerDay;

        /// <summary>The warden takes the rise back: wages are what they were at the next settlement, and the peace over pay is over whenever it is. Inside the term and the peace after it (<see cref="TakeBackDays"/> of agreeing) that is breaking the promise.</summary>
        public static void TakeBack(Map map, PrisonGame game, PayRise rise)
        {
            StaffAgreementBook book = game.agreements;
            if (!book.rises.Remove(rise)) return;
            book.peaceUntil.Remove(StaffGrievance.Pay);
            if (book.active is { grievance: StaffGrievance.Pay } a && a.role == rise.role)
            {
                Judge(map, game, AgreementOutcome.Broken);
                return;
            }
            // (A promise judged broken at its deadline left the rise standing, and it has its record already: not broken twice.)
            if (Find.TickManager.TicksGame >= rise.agreedTick + TakeBackDays * GenDate.TicksPerDay || rise.keptDay < 0) return;
            // Kept, and taken back inside the term and the peace: the record says broken, as the staff remember it.
            AgreementRecord kept = book.history.LastOrDefault(h => h.grievance == StaffGrievance.Pay && h.outcome == AgreementOutcome.Fulfilled && h.day == rise.keptDay);
            if (kept != null) kept.outcome = AgreementOutcome.Broken;
            else Record(book, StaffGrievance.Pay, AgreementOutcome.Broken, Ledger.CurrentDay);
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>() is { } sc && Grieves(sc, StaffGrievance.Pay, rise.role)) StaffMorale.BrokenPromise(p);
            Messages.Message("RP_AgreementRiseTakenBack".Translate(Plural(rise.role)), MessageTypeDefOf.NegativeEvent, historical: true);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_RiseTakenBack".Translate(Plural(rise.role)));
        }

        // ---- the panel ------------------------------------------------------------------------------------------------

        public const float PanelLineH = 24f;

        /// <summary>The rise a running pay promise is being kept with is on the promise's own line (with the button), not a line of its own.</summary>
        public static bool OnTheActiveLine(StaffAgreementBook book, PayRise rise) => book.active is { grievance: StaffGrievance.Pay } a && a.role == rise.role;

        /// <summary>Lines the Staff tab's panel needs (the running promise and each rise that stands).</summary>
        public static int PanelLines(PrisonGame game)
        {
            StaffAgreementBook book = game.agreements;
            int lines = book.active != null ? 1 : 0;
            for (int i = 0; i < book.rises.Count; i++)
                if (!OnTheActiveLine(book, book.rises[i])) lines++;
            return lines;
        }

        // The panel's texts are kept with what they were made from (and the language), so that drawing the tab formats nothing: made again when a count, the days left (in tenths) or the language changes.
        private static (StaffAgreement a, int compliant, int seen, int hurt, int paid, int tenths, LoadedLanguage language) activeKey;
        private static string activeText, activeShort;

        /// <summary>The panel's tooltip for the running promise: the grievance, the term in full, the hours that met it so far out of those needed, and the days left.</summary>
        public static string ActiveLine(StaffAgreement a)
        {
            RefreshActive(a);
            return activeText;
        }

        /// <summary>The panel's row for the running promise: a short line (the grievance, who speaks, the progress, the days left), the term being in the tooltip.</summary>
        public static string ActiveShort(StaffAgreement a)
        {
            RefreshActive(a);
            return activeShort;
        }

        private static void RefreshActive(StaffAgreement a)
        {
            float daysLeft = Mathf.Max(0f, (a.deadlineTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay);
            var key = (a, a.compliantHours, a.hoursSeen, a.hurtTick, a.paidSettlements, Mathf.FloorToInt(daysLeft * 10f), LanguageDatabase.activeLanguage);
            if (activeText != null && key == activeKey) return;
            activeKey = key;
            string progress = a.grievance switch
            {
                StaffGrievance.Understaffed or StaffGrievance.Nights => "RP_AgreementProgress_Hours".Translate(a.compliantHours, a.hoursNeeded, a.hoursSeen).Resolve(),
                StaffGrievance.Safety => (a.hurtTick >= 0 ? "RP_AgreementProgress_Hurt" : "RP_AgreementProgress_Safe").Translate().Resolve(),
                _ => "RP_AgreementProgress_Paid".Translate(a.paidSettlements, TermDays).Resolve(),
            };
            string progressShort = a.grievance switch
            {
                StaffGrievance.Understaffed or StaffGrievance.Nights => "RP_AgreementShort_Hours".Translate(a.compliantHours, a.hoursNeeded).Resolve(),
                StaffGrievance.Safety => (a.hurtTick >= 0 ? "RP_AgreementShort_Hurt" : "RP_AgreementShort_Safe").Translate().Resolve(),
                _ => "RP_AgreementShort_Paid".Translate(a.paidSettlements, TermDays).Resolve(),
            };
            string days = daysLeft.ToString("0.0");
            activeText = "RP_AgreementLine".Translate(GrievanceLabel(a.grievance), a.repName ?? "-", TermText(a.grievance, a.role), progress, days).Resolve();
            activeShort = "RP_AgreementLineShort".Translate(GrievanceLabel(a.grievance), a.repName ?? "-", progressShort, days).Resolve();
        }

        public static string RiseLine(PayRise rise)
        {
            float daysLeft = (rise.agreedTick + TakeBackDays * GenDate.TicksPerDay - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay;
            int tenths = daysLeft > 0f ? Mathf.FloorToInt(daysLeft * 10f) : -1; // (-1: past; under a tenth left is 0, still binding)
            if (rise.lineText != null && rise.lineTenths == tenths && rise.lineLanguage == LanguageDatabase.activeLanguage) return rise.lineText;
            (rise.lineTenths, rise.lineLanguage) = (tenths, LanguageDatabase.activeLanguage);
            string days = Mathf.Max(0f, daysLeft).ToString("0.0");
            rise.lineShort = (daysLeft > 0f ? "RP_PayRiseShortBinding" : "RP_PayRiseShort").Translate(Plural(rise.role), PayRiseShare.ToStringPercent(), days).Resolve();
            return rise.lineText = (daysLeft > 0f ? "RP_PayRiseLineBinding" : "RP_PayRiseLine").Translate(Plural(rise.role), PayRiseShare.ToStringPercent(), days).Resolve();
        }

        /// <summary>The panel's row for a rise that stands (the tooltip is <see cref="RiseLine"/>).</summary>
        public static string RiseShort(PayRise rise)
        {
            RiseLine(rise);
            return rise.lineShort;
        }

        /// <summary>Everything saved, as one comparable string (the saveload plan).</summary>
        public static string Describe(StaffAgreementBook b)
        {
            StaffAgreement a = b.active;
            string active = a == null ? "-" : $"{a.grievance}:{a.role?.defName}:{a.repId}:{a.repName}:{a.agreedTick}:{a.deadlineTick}:{(a.baselines == null ? "-" : string.Join(",", a.baselines))}:{a.hoursSeen}:{a.compliantHours}:{a.hoursNeeded}:{a.hurtTick}:{a.paidSettlements}";
            string history = string.Join(",", b.history.Select(h => $"{h.grievance}={h.outcome}@{h.day}"));
            string peace = string.Join(",", b.peaceUntil.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            string rises = string.Join(",", b.rises.OrderBy(r => r.role.defName).Select(r => $"{r.role.defName}:{r.agreedTick}:{r.keptDay}"));
            return $"{active}/{history}/{peace}/{rises}/{b.lastHourStamp}";
        }
    }
}
