using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPRiotReviewDefOf
    {
        public static LedgerCategoryDef RP_Restitution;

        static RPRiotReviewDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRiotReviewDefOf));
    }

    /// <summary>What the warden answered a riot with (up to <see cref="RiotReview.MaxAnswers"/>; amnesty is the only answer when it is given).</summary>
    public enum RiotAnswer : byte
    {
        /// <summary>The wing's yard off and an early lockup for 1-3 days (<see cref="RiotRecord.restrictDays"/>).</summary>
        Restrict,
        ShipOut,
        Charge,
        Restitution,
        Grievance,
        Amnesty,
    }

    /// <summary>One inmate in the riot, by id and name (the record outlives them), and whether staff or a camera saw them in it.</summary>
    public class RiotParticipant : IExposable
    {
        public int id = -1;
        public string name;
        public bool seen;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref seen, "seen");
        }

        public string Describe() => $"{id}:{name}:{seen}";
    }

    /// <summary>
    /// One riot, from the ringleader's first move to the warden's answers: who (by the seen rule), who was hurt and who died, what was broken, the grievance and whether it held up, what the prison lacked at
    /// the start, and what was answered. Kept by <see cref="RiotBook"/> (the last <see cref="RiotReview.MaxRecords"/>).
    /// </summary>
    public class RiotRecord : IExposable
    {
        public int id;
        public int mapId = -1;
        public SecurityLevelDef level;
        public int startTick;
        /// <summary>The tick the record was closed at (-1: the riot is still on, as far as the record knows).</summary>
        public int endTick = -1;
        /// <summary>The last tick a participant was seen rioting (their own state's ticks, see <see cref="RiotReview.Notify_Rioting"/>).</summary>
        public int lastRiotTick;
        public int ringleaderId = -1;
        public string ringleaderName;
        /// <summary>Everyone in it, the ringleader first (at most <see cref="RiotReview.MaxParticipants"/>).</summary>
        public List<RiotParticipant> participants = new List<RiotParticipant>();
        /// <summary>The staff and the inmates hurt in it (thing ids, each once); the staff and inmates it killed (thing ids, each once), and the names of those of them the report may name (not a rioter nobody saw).</summary>
        public List<int> staffHurt = new List<int>(), inmatesHurt = new List<int>(), deadIds = new List<int>();
        public List<string> deaths = new List<string>();
        /// <summary>The value of what the rioters broke.</summary>
        public float damage;
        /// <summary>What the ringleader shouted for, and whether it held up by the numbers read at the start (the need's average over the inmates, and yesterday's strip of the wing: missed meals, hours locked in).</summary>
        public NeedDef grievance;
        public bool grievanceReal;
        public int needPercent = -1, missedMeals, lockedHours;
        /// <summary>What the prison lacked at the start: the nearest guard to the ringleader in cells (-1: none on the map), whether the spot was dark, the hour's guards on the roster, the inmates on the wing.</summary>
        public int nearestGuard = -1;
        public bool dark;
        public int guardsRostered, wingInmates;
        /// <summary>The riot ended in a deal (a negotiation, or a hostage crisis conceded): the grievance was met already.</summary>
        public bool negotiated;
        /// <summary>The tick the morning letter went (-1: not yet).</summary>
        public int letterTick = -1;
        public List<RiotAnswer> answers = new List<RiotAnswer>();
        /// <summary>The warden has finished answering (two answers, amnesty, "that is all", or the letter timed out).</summary>
        public bool done;
        /// <summary>A restriction: its days, the last day it is in force, whether its tension cost has landed.</summary>
        public int restrictDays, restrictUntilDay = -1;
        public bool restrictBilled;
        /// <summary>An amnesty: the wing riots more readily until this tick (-1 none).</summary>
        public int amnestyUntilTick = -1;
        /// <summary>The cases a charge put on the sheet, and the cash restitution took.</summary>
        public int charged, restitution;
        /// <summary>The rumour the wing was telling when the riot began (a copy, as it stood then: the rumour itself may be over by the morning), or null.</summary>
        public Rumour rumour;

        public bool Open => endTick < 0;

        public RiotParticipant Ringleader => participants.Find(x => x.id == ringleaderId);

        public bool RingleaderSeen => Ringleader?.seen ?? false;

        public RiotParticipant Of(int thingId)
        {
            for (int i = 0; i < participants.Count; i++)
                if (participants[i].id == thingId) return participants[i];
            return null;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref mapId, "mapId", -1);
            Scribe_Defs.Look(ref level, "level");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref endTick, "endTick", -1);
            Scribe_Values.Look(ref lastRiotTick, "lastRiotTick");
            Scribe_Values.Look(ref ringleaderId, "ringleaderId", -1);
            Scribe_Values.Look(ref ringleaderName, "ringleaderName");
            Scribe_Collections.Look(ref participants, "participants", LookMode.Deep);
            Scribe_Collections.Look(ref staffHurt, "staffHurt", LookMode.Value);
            Scribe_Collections.Look(ref inmatesHurt, "inmatesHurt", LookMode.Value);
            Scribe_Collections.Look(ref deaths, "deaths", LookMode.Value);
            Scribe_Collections.Look(ref deadIds, "deadIds", LookMode.Value);
            Scribe_Values.Look(ref damage, "damage");
            Scribe_Defs.Look(ref grievance, "grievance");
            Scribe_Values.Look(ref grievanceReal, "grievanceReal");
            Scribe_Values.Look(ref needPercent, "needPercent", -1);
            Scribe_Values.Look(ref missedMeals, "missedMeals");
            Scribe_Values.Look(ref lockedHours, "lockedHours");
            Scribe_Values.Look(ref nearestGuard, "nearestGuard", -1);
            Scribe_Values.Look(ref dark, "dark");
            Scribe_Values.Look(ref guardsRostered, "guardsRostered");
            Scribe_Values.Look(ref wingInmates, "wingInmates");
            Scribe_Values.Look(ref negotiated, "negotiated");
            Scribe_Values.Look(ref letterTick, "letterTick", -1);
            Scribe_Collections.Look(ref answers, "answers", LookMode.Value);
            Scribe_Values.Look(ref done, "done");
            Scribe_Values.Look(ref restrictDays, "restrictDays");
            Scribe_Values.Look(ref restrictUntilDay, "restrictUntilDay", -1);
            Scribe_Values.Look(ref restrictBilled, "restrictBilled");
            Scribe_Values.Look(ref amnestyUntilTick, "amnestyUntilTick", -1);
            Scribe_Values.Look(ref charged, "charged");
            Scribe_Values.Look(ref restitution, "restitution");
            Scribe_Deep.Look(ref rumour, "rumour");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                participants ??= new List<RiotParticipant>();
                participants.RemoveAll(x => x == null);
                staffHurt ??= new List<int>();
                inmatesHurt ??= new List<int>();
                deaths ??= new List<string>();
                deadIds ??= new List<int>();
                answers ??= new List<RiotAnswer>();
                // (A save from before the rumour mill has none; one whose level is gone is dropped, as the map's are.)
                if (rumour is { level: null }) rumour = null;
            }
        }

        /// <summary>Everything saved about the record in one string (the saveload plan compares it).</summary>
        public string Describe() =>
            $"{id}:{mapId}:{level?.defName}:{startTick}:{endTick}:{lastRiotTick}:{ringleaderId}:{ringleaderName}:[{string.Join("+", participants.Select(x => x.Describe()))}]:{string.Join("/", staffHurt)}:{string.Join("/", inmatesHurt)}:{string.Join("/", deaths)}:{string.Join("/", deadIds)}:"
            + $"{damage:F1}:{grievance?.defName}:{grievanceReal}:{needPercent}:{missedMeals}:{lockedHours}:{nearestGuard}:{dark}:{guardsRostered}:{wingInmates}:{negotiated}:{letterTick}:{string.Join("/", answers)}:{done}:"
            + $"{restrictDays}:{restrictUntilDay}:{restrictBilled}:{amnestyUntilTick}:{charged}:{restitution}:{rumour?.Describe() ?? "-"}";
    }

    /// <summary>The riots the prison has had, the last <see cref="RiotReview.MaxRecords"/> (owned by <see cref="PrisonGame"/>; an old save has an empty one).</summary>
    public class RiotBook : IExposable
    {
        public List<RiotRecord> records = new List<RiotRecord>();
        public int nextId = 1;

        public RiotRecord ById(int id) => records.Find(x => x.id == id);

        public void ExposeData()
        {
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            Scribe_Values.Look(ref nextId, "nextId", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                records ??= new List<RiotRecord>();
                records.RemoveAll(x => x == null);
            }
        }

        public string Describe() => $"{nextId}:" + string.Join("|", records.Select(x => x.Describe()));
    }

    /// <summary>
    /// The morning after: a riot that ends in a reckoning. The record opens when the riot starts (<see cref="Open"/>, from <see cref="MisconductWorker_Riot.TryExecute"/>), the rioters' own state notes who is in
    /// it and whether anything that watches sees them (<see cref="Notify_Rioting"/>, every 250 ticks of each), and what they break and who is hurt is counted as the blows land (<see cref="Notify_Damage"/>). The
    /// record closes when no participant has been rioting for an hour, and the next 06:00 brings one budgeted letter per riot with the incident report: the ringleader and those who joined, named only where
    /// staff or a camera saw them (the rest a count); the hurt and the dead; the damage bill; the grievance and whether it held up; the rumour the wing was telling (see <see cref="Rumours"/>); what the prison lacked at
    /// the start. The warden picks up to two answers
    /// (<see cref="Apply"/>): restrict the wing for 1-3 days (the yard off and an early lockup each day: the wing riots less meanwhile, each day served holds it down more, and the tension it costs lands when it ends), ship the
    /// ringleader out, charge the ones seen whom the riot's own records did not deal with already (cases on the adjudication sheet), make restitution out of their cash, meet the grievance (a promise), or amnesty alone (tension down, reputation down, and the wing
    /// riots more readily for a while). Everything is event-driven: no pass scans the prison for it.
    /// </summary>
    public static class RiotReview
    {
        public const int MaxRecords = 10, MaxParticipants = 30, MaxAnswers = 2;
        public const int CloseAfterTicks = GenDate.TicksPerHour;
        public const int LetterHour = 6;
        /// <summary>The morning letter's answer time (more than <see cref="StoryLetters.UrgentHours"/>: a budgeted letter), and the second answer's.</summary>
        public const int AnswerHours = 24, MoreAnswerHours = 12;
        /// <summary>A restriction: what each day served adds to the wing's suppression (at its end, pro rata for part of one), what each day costs in tension when it ends, and the wing's riot odds while it runs.</summary>
        public const float RestrictSuppressionPerDay = 15f, RestrictTensionPerDay = 5f, RestrictRiotFactor = 0.5f;
        /// <summary>An amnesty: tension off each inmate of the wing, the reputation it costs, and the wing's riot odds for <see cref="AmnestyDays"/> days.</summary>
        public const float AmnestyTension = 10f, AmnestyReputation = 2f, AmnestyRiotFactor = 1.5f;
        public const int AmnestyDays = 5;
        /// <summary>What restitution costs each payer in tension.</summary>
        public const float RestitutionTension = 4f;
        /// <summary>A grievance is real when the inmates' average of the need is under this (or, for food, the wing missed meals yesterday).</summary>
        public const float RealBelow = DemandSystem.MetAverage;

        public const string DecisionDef = "RP_Decision_RiotReview", MoreDecisionDef = "RP_Decision_RiotReviewMore";

        private static int Now => Find.TickManager.TicksGame;

        private static RiotBook Book => PrisonGame.Instance?.riots;

        public static Map MapOf(RiotRecord r)
        {
            foreach (Map m in Find.Maps)
                if (m.uniqueID == r.mapId) return m;
            return null;
        }

        // ---- the record opens -------------------------------------------------------------------------------------------------------

        /// <summary>A riot has started: its record, with the ringleader and the first rioters (each with whether they are seen now), the grievance, and what the prison lacked at the start.</summary>
        public static RiotRecord Open(Pawn ringleader, CompInmate c, List<Pawn> rioting, NeedDef grievance)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = ringleader?.Map;
            if (game == null || map == null || c == null) return null;
            PrisonMap pm = PrisonMap.Of(map);
            int now = Now;
            var r = new RiotRecord
            {
                id = game.riots.nextId++, mapId = map.uniqueID, level = c.security ?? InmateUtility.DefaultSecurity, startTick = now, lastRiotTick = now, ringleaderId = ringleader.thingIDNumber,
                ringleaderName = ringleader.LabelShort, grievance = grievance,
            };
            // (What the wing was saying as it began, kept as it stood: by the morning's report it may be calmed, overtaken or burnt out.)
            r.rumour = Rumours.LiveOn(pm, r.level)?.Copy();
            Add(r, ringleader, pm);
            foreach (Pawn p in rioting)
                if (p != ringleader) Add(r, p, pm);
            if (pm != null)
            {
                IntVec3 at = ringleader.Position;
                float best = float.MaxValue;
                foreach (Pawn g in pm.Guards)
                    if (g.Spawned && !g.Downed) best = Mathf.Min(best, g.Position.DistanceTo(at));
                r.nearestGuard = best < float.MaxValue ? Mathf.RoundToInt(best) : -1;
                r.dark = TensionUtility.Dark(at, map, pm);
                if (pm.regime.Yesterday.TryGetValue(r.level, out RegimeDay day))
                    for (int h = 0; h < 24; h++)
                    {
                        r.missedMeals += day.missed[h];
                        if (day.Has(h) && day.ran[h] is { lockup: true }) r.lockedHours++;
                    }
            }
            r.guardsRostered = Roster.Strip(map)[GenLocalDate.HourOfDay(map)];
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate()?.security == r.level && InmateUtility.IsActiveInmate(p)) r.wingInmates++;
            if (grievance != null)
            {
                (float avg, _, int n) = DemandSystem.Survey(map, grievance);
                r.needPercent = n > 0 ? Mathf.RoundToInt(avg * 100f) : -1;
                r.grievanceReal = n > 0 && avg < RealBelow || grievance == NeedDefOf.Food && r.missedMeals > 0;
            }
            game.riots.records.Add(r);
            Trim(game.riots);
            return r;
        }

        /// <summary>
        /// Keeps the last <see cref="MaxRecords"/>: the oldest that is answered (or the oldest of all) goes first. Not one whose restriction is still to be billed while another can go (its tension lands from the record when
        /// it ends: dropped, the restriction would be free).
        /// </summary>
        internal static void Trim(RiotBook book)
        {
            while (book.records.Count > MaxRecords)
            {
                int i = book.records.FindIndex(x => x.done && !RestrictionUnbilled(x));
                if (i < 0) i = book.records.FindIndex(x => !RestrictionUnbilled(x));
                book.records.RemoveAt(i >= 0 ? i : 0);
            }
        }

        private static bool RestrictionUnbilled(RiotRecord r) => r.restrictUntilDay >= 0 && !r.restrictBilled;

        private static void Add(RiotRecord r, Pawn p, PrisonMap pm)
        {
            if (r.Of(p.thingIDNumber) is { } known)
            {
                if (!known.seen && pm != null && TensionUtility.Watched(p, pm)) known.seen = true;
                return;
            }
            if (r.participants.Count >= MaxParticipants) return;
            r.participants.Add(new RiotParticipant { id = p.thingIDNumber, name = p.LabelShort, seen = pm != null && TensionUtility.Watched(p, pm) });
        }

        /// <summary>The open record this rioter is in, else null (one in the rioting state for another reason - a hostage-taker of an assault - is in no riot's record).</summary>
        private static RiotRecord RecordFor(Pawn p)
        {
            RiotBook book = Book;
            if (book == null || book.records.Count == 0) return null;
            for (int i = book.records.Count - 1; i >= 0; i--)
            {
                RiotRecord r = book.records[i];
                if (r.Open && r.Of(p.thingIDNumber) != null) return r;
            }
            return null;
        }

        /// <summary>
        /// A rioter's own state, every 250 of its ticks (<see cref="MentalState_Rioting"/>): the riot is still on (the record's clock), and the rioter is in it, seen if anything that watches sees them now. A rioter who
        /// joined from another (<paramref name="from"/>) goes into the record that one is in; one who is in no record and joined nobody's riot is not the riot review's.
        /// </summary>
        public static void Notify_Rioting(Pawn p, Pawn from = null)
        {
            RiotRecord r = (from != null ? RecordFor(from) : null) ?? RecordFor(p);
            if (r == null || p.Map == null) return;
            r.lastRiotTick = Now;
            Add(r, p, PrisonMap.Of(p.Map));
        }

        // ---- the damage bill and the hurt -----------------------------------------------------------------------------------------------

        /// <summary>Whether a blow is the riot's to count: a rioter dealt it, or a rioter took it (cheap: two state reads before anything else).</summary>
        public static bool Counts(Thing victim, DamageInfo dinfo) =>
            dinfo.Instigator is Pawn { MentalStateDef: { } a } && a == RPMentalStateDefOf.RP_Rioting
            || victim is Pawn { MentalStateDef: { } v } && v == RPMentalStateDefOf.RP_Rioting;

        /// <summary>
        /// A blow the riot counts has landed: what a rioter broke goes on the bill at its value (the hit points it lost against all it had), a person a rioter hurt is staff or an inmate hurt, a rioter hurt by
        /// anyone is an inmate hurt, and a member of staff or an inmate it killed is one of the dead. A blow between a rioter and staff is a sighting: the staff member saw who they hit or who hit them (a rioter a guard
        /// puts down between two of the rioter's own samples is seen all the same).
        /// </summary>
        public static void Notify_Damage(Thing victim, DamageInfo dinfo, bool victimRioting, int hitPointsBefore, float valuePerHitPoint)
        {
            Pawn rioter = dinfo.Instigator is Pawn { MentalStateDef: { } a } i && a == RPMentalStateDefOf.RP_Rioting ? i : null;
            RiotRecord r = rioter != null ? RecordFor(rioter) : null;
            if (victim is Pawn hurt)
            {
                // (Read before the blow: one that downs a rioter ends their state with it.)
                RiotRecord own = victimRioting ? RecordFor(hurt) : null;
                r ??= own;
                if (r == null) return;
                bool staff = Funerals.IsStaff(hurt);
                if (staff && rioter != null && r.Of(rioter.thingIDNumber) is { seen: false } struck) struck.seen = true;
                if (own != null && dinfo.Instigator is Pawn by && Funerals.IsStaff(by) && own.Of(hurt.thingIDNumber) is { seen: false } downed) downed.seen = true;
                List<int> hurtList = staff ? r.staffHurt : hurt.IsPrisonerOfColony || hurt.Inmate() != null ? r.inmatesHurt : null;
                if (hurtList == null) return;
                if (!hurtList.Contains(hurt.thingIDNumber)) hurtList.Add(hurt.thingIDNumber);
                if (hurt.Dead && !r.deadIds.Contains(hurt.thingIDNumber))
                {
                    r.deadIds.Add(hurt.thingIDNumber);
                    // (A rioter nobody saw is not named by dying: the report counts them.)
                    if (r.Of(hurt.thingIDNumber) is not { seen: false }) r.deaths.Add(hurt.LabelShort);
                }
                return;
            }
            if (r == null || !victim.def.useHitPoints || hitPointsBefore <= 0) return;
            int lost = hitPointsBefore - (victim.Destroyed ? 0 : victim.HitPoints);
            if (lost > 0) r.damage += lost * valuePerHitPoint;
        }

        // ---- the riot ends ----------------------------------------------------------------------------------------------------------------

        /// <summary>The riot ended in a deal with its ringleader (a negotiation, a hostage crisis conceded): the record says so, and the grievance is met already.</summary>
        public static void Notify_Negotiated(int ringleaderId)
        {
            RiotBook book = Book;
            if (book == null || ringleaderId < 0) return;
            for (int i = book.records.Count - 1; i >= 0; i--)
                if (book.records[i].ringleaderId == ringleaderId && book.records[i].letterTick < 0)
                {
                    book.records[i].negotiated = true;
                    return;
                }
        }

        /// <summary>The hour pass of a home map: its records whose riot has been quiet for an hour close; at <see cref="LetterHour"/> each closed one that has had no letter gets one.</summary>
        public static void Hourly(PrisonGame game, Map map, int hour) => Hourly(game, map, hour, Now);

        /// <summary>The same at a given tick (a check asks about a riot an hour after it ended without waiting for the hour).</summary>
        public static void Hourly(PrisonGame game, Map map, int hour, int now)
        {
            List<RiotRecord> records = game.riots.records;
            if (records.Count == 0) return;
            for (int i = 0; i < records.Count; i++)
            {
                RiotRecord r = records[i];
                if (r.done && !r.Open) continue;
                // (A record of a map that is gone has nobody to answer for: it is closed and done.)
                if (r.mapId != map.uniqueID)
                {
                    if (MapOf(r) == null) (r.endTick, r.done) = (r.Open ? now : r.endTick, true);
                    continue;
                }
                if (r.Open && now - r.lastRiotTick >= CloseAfterTicks) Close(r, now);
            }
            if (hour != LetterHour) return;
            for (int i = 0; i < records.Count; i++)
                if (records[i] is { Open: false, done: false, letterTick: < 0 } r && r.mapId == map.uniqueID) SendLetter(game, map, r);
        }

        private static void Close(RiotRecord r, int now)
        {
            r.endTick = now;
            Story.Record(RPChronicleDefOf.RP_Chr_Violence, 1, "RP_Chr_RiotOver".Translate(r.level?.label ?? "?", r.participants.Count, GenDate.ToStringTicksToPeriod(Mathf.Max(GenDate.TicksPerHour / 4, r.lastRiotTick - r.startTick))));
        }

        // ---- the morning letter -------------------------------------------------------------------------------------------------------------

        public static TaggedString Label(RiotRecord r) => "RP_RiotReviewLabel".Translate(r.level?.label ?? "?");

        /// <summary>The incident report's lines (the seen rule: only who staff or a camera saw is named).</summary>
        public static List<string> ReportLines(RiotRecord r)
        {
            var lines = new List<string>();
            RiotParticipant lead = r.Ringleader;
            lines.Add(lead is { seen: true } ? "RP_RiotReport_Ringleader".Translate(lead.name).Resolve() : "RP_RiotReport_RingleaderUnseen".Translate().Resolve());
            List<string> seen = r.participants.Where(x => x.seen && x.id != r.ringleaderId).Select(x => x.name).ToList();
            int others = r.participants.Count(x => !x.seen && x.id != r.ringleaderId);
            if (seen.Count > 0) lines.Add("RP_RiotReport_Joined".Translate(string.Join(", ", seen)).Resolve());
            if (others > 0) lines.Add((seen.Count > 0 ? "RP_RiotReport_Others" : "RP_RiotReport_OthersOnly").Translate(others).Resolve());
            else if (seen.Count == 0) lines.Add("RP_RiotReport_NobodyElse".Translate().Resolve());
            lines.Add("RP_RiotReport_Hurt".Translate(r.staffHurt.Count, r.inmatesHurt.Count).Resolve());
            int unseenDead = Mathf.Max(0, r.deadIds.Count - r.deaths.Count);
            if (r.deaths.Count > 0)
                lines.Add((unseenDead > 0 ? "RP_RiotReport_DeadAndUnseen".Translate(string.Join(", ", r.deaths), unseenDead) : "RP_RiotReport_Dead".Translate(string.Join(", ", r.deaths))).Resolve());
            else if (unseenDead > 0) lines.Add("RP_RiotReport_DeadUnseen".Translate(unseenDead).Resolve());
            int bill = Bill(r);
            lines.Add(bill > 0 ? "RP_RiotReport_Damage".Translate(Money.Format(bill)).Resolve() : "RP_RiotReport_NoDamage".Translate().Resolve());
            if (r.grievance != null)
            {
                string yesterday = "RP_RiotReport_Yesterday".Translate(r.missedMeals, r.lockedHours).Resolve();
                lines.Add((r.grievanceReal ? "RP_RiotReport_GrievanceReal" : "RP_RiotReport_GrievanceNot").Translate(r.grievance.label, r.needPercent >= 0 ? r.needPercent : 100, yesterday).Resolve());
            }
            if (r.negotiated) lines.Add("RP_RiotReport_Negotiated".Translate(r.grievance?.label ?? "?").Resolve());
            // (What the wing was saying when it began, true or false as the warden knew it then: the chain from cause to rumour to riot.)
            if (r.rumour != null) lines.Add(RumourLine(r.rumour));
            var lacked = new List<string>();
            if (r.nearestGuard < 0) lacked.Add("RP_RiotLacked_NoGuard".Translate().Resolve());
            else if (r.nearestGuard > TensionUtility.GuardRadius) lacked.Add("RP_RiotLacked_GuardFar".Translate(Mathf.RoundToInt(TensionUtility.GuardRadius), r.nearestGuard).Resolve());
            if (r.dark) lacked.Add("RP_RiotLacked_Dark".Translate().Resolve());
            lacked.Add("RP_RiotLacked_Roster".Translate(r.guardsRostered, r.wingInmates).Resolve());
            lines.Add("RP_RiotReport_Lacked".Translate(string.Join(", ", lacked)).Resolve());
            return lines;
        }

        /// <summary>The report's line of the rumour the wing was telling when the riot began.</summary>
        public static string RumourLine(Rumour rumour) => "RP_RiotReport_Rumour".Translate(Rumours.WingName(rumour.level), Rumours.Text(rumour), Rumours.TruthKey(rumour).Translate()).Resolve();

        /// <summary>The damage bill in whole money.</summary>
        public static int Bill(RiotRecord r) => Mathf.RoundToInt(r.damage);

        public static string ReportText(RiotRecord r) => CourtUtil.Bullets(ReportLines(r));

        public static string LetterText(RiotRecord r, Map map)
        {
            string when = map != null ? GenDate.DateFullStringAt(GenDate.TickGameToAbs(r.startTick), Find.WorldGrid.LongLatOf(map.Tile)) : "?";
            string lasted = GenDate.ToStringTicksToPeriod(Mathf.Max(GenDate.TicksPerHour / 4, r.lastRiotTick - r.startTick));
            string answers = "RP_RiotReviewAnswers".Translate(RestrictTensionPerDay.ToString("0"), Money.Format(InmateUtility.TransferCost), Money.Format(Bill(r)), r.grievance?.label ?? "?", DemandSystem.Hours, AmnestyDays,
                RestrictSuppressionPerDay.ToString("0")).Resolve();
            // (Meeting the grievance sweeps an open promise aside, as a negotiation does: the letter says so.)
            if (PrisonGame.Instance?.demand is { Active: true, need: { } open } && !r.negotiated && r.grievance != null)
                answers += "\n" + "RP_RiotReviewPromiseOpen".Translate(open.label).Resolve();
            return "RP_RiotReviewText".Translate(r.level?.label ?? "?", when, lasted, ReportText(r), answers, MaxAnswers).Resolve();
        }

        /// <summary>The pawns the letter may point at: the seen participants who are still here.</summary>
        private static List<Pawn> SeenPawns(RiotRecord r, Map map)
        {
            var found = new List<Pawn>();
            if (map == null) return found;
            foreach (RiotParticipant x in r.participants)
                if (x.seen && CourtUtil.FindInmate(map, x.id) is { } p) found.Add(p);
            return found;
        }

        /// <summary>The morning letter (budgeted, one per riot): the report and the answers. Left unanswered it is "no reckoning".</summary>
        public static ChoiceLetter_PrisonDecision SendLetter(PrisonGame game, Map map, RiotRecord r)
        {
            r.letterTick = Now;
            List<Pawn> seen = SeenPawns(r, map);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(DecisionDef, Label(r), LetterText(r, map), map, new LookTargets(seen));
            letter.amount = r.id;
            letter.pawns.AddRange(seen);
            letter.StartTimeout(AnswerHours * GenDate.TicksPerHour);
            Find.LetterStack.ReceiveLetter(letter);
            return letter;
        }

        /// <summary>
        /// After one answer, the second (not budgeted: it is the same morning's letter, answered in two steps). Left alone it is "that is all". <paramref name="refused"/>: the answer just chosen could not be given (things
        /// changed since the letter was drawn): the same step again, saying why, and with nothing given yet it asks for the answer, not a second one.
        /// </summary>
        public static ChoiceLetter_PrisonDecision SendMore(Map map, RiotRecord r, string refused = null)
        {
            List<Pawn> seen = SeenPawns(r, map);
            string level = r.level?.label ?? "?";
            bool none = r.answers.Count == 0;
            string text = (none ? "RP_RiotReviewAgainText" : "RP_RiotReviewMoreText").Translate(AnswersText(r)).Resolve();
            if (refused != null) text = "RP_RiotAnswerNotGiven".Translate(refused).Resolve() + "\n" + text;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(MoreDecisionDef, (none ? "RP_RiotReviewAgainLabel" : "RP_RiotReviewMoreLabel").Translate(level),
                text + "\n\n" + LetterText(r, map), map, new LookTargets(seen));
            letter.amount = r.id;
            letter.pawns.AddRange(seen);
            letter.StartTimeout(MoreAnswerHours * GenDate.TicksPerHour);
            Find.LetterStack.ReceiveLetter(letter);
            return letter;
        }

        /// <summary>Whether a letter of the record is still to be answered (on the stack, or held back for the day's budget).</summary>
        public static bool LetterOpen(RiotRecord r) =>
            StoryLetters.Open().Any(l => l is ChoiceLetter_PrisonDecision { answered: false } d && (d.Is(DecisionDef) || d.Is(MoreDecisionDef)) && d.amount == r.id);

        // ---- the answers -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The seen participants still here and active (the ones a charge or the bill can name). Still here is the same stay: admitted by the riot's last rioting (a joiner may have arrived after it began); one
        /// admitted after it left and came back.
        /// </summary>
        public static List<Pawn> SeenHere(RiotRecord r, Map map)
        {
            var found = new List<Pawn>();
            if (map == null) return found;
            foreach (RiotParticipant x in r.participants)
                if (x.seen && CourtUtil.FindInmate(map, x.id) is { } p && InmateUtility.IsActiveInmate(p) && p.Inmate() is { } c && c.admittedTick <= r.lastRiotTick) found.Add(p);
            return found;
        }

        /// <summary>The ringleader, still here on the same stay (admitted by the riot's start, which was theirs).</summary>
        public static Pawn RingleaderHere(RiotRecord r, Map map) =>
            map != null && CourtUtil.FindInmate(map, r.ringleaderId) is { } p && InmateUtility.IsActiveInmate(p) && p.Inmate().admittedTick <= r.startTick ? p : null;

        /// <summary>Why the answer cannot be given to the record now (a translated reason), or null.</summary>
        public static string Refused(PrisonGame game, Map map, RiotRecord r, RiotAnswer a)
        {
            if (r.done || r.answers.Count >= MaxAnswers) return "RP_RiotRefused_Done".Translate();
            if (r.answers.Contains(a)) return "RP_RiotRefused_Given".Translate();
            if (a == RiotAnswer.Amnesty && r.answers.Count > 0) return "RP_RiotRefused_AmnestyAlone".Translate();
            switch (a)
            {
                case RiotAnswer.Restrict:
                    if (r.level == null) return "RP_RiotRefused_NoWing".Translate();
                    if (RegimeOrders.Restricted(game, r.level)) return "RP_RiotRefused_Restricted".Translate(r.level.label);
                    return null;
                case RiotAnswer.ShipOut:
                    if (!r.RingleaderSeen) return "RP_RiotRefused_RingleaderUnseen".Translate();
                    Pawn lead = RingleaderHere(r, map);
                    if (lead == null || ShuttleSystem.OnTheWayOut(lead)) return "RP_RiotRefused_RingleaderGone".Translate(r.ringleaderName);
                    return game.ledger.funds < InmateUtility.TransferCost ? "RP_CannotAfford".Translate(Money.Format(InmateUtility.TransferCost)).ToString() : null;
                case RiotAnswer.Charge:
                    if (SeenHere(r, map).Count == 0) return "RP_RiotRefused_NobodySeen".Translate();
                    return Chargeable(game, map, r).Count == 0 ? "RP_RiotRefused_DealtWith".Translate() : null;
                case RiotAnswer.Restitution:
                    if (Bill(r) <= 0) return "RP_RiotRefused_NothingBroken".Translate();
                    return SeenHere(r, map).Count == 0 ? "RP_RiotRefused_NobodySeen".Translate() : null;
                case RiotAnswer.Grievance:
                    if (r.negotiated) return "RP_RiotRefused_Negotiated".Translate();
                    return r.grievance == null ? "RP_RiotRefused_NoGrievance".Translate() : null;
            }
            return null;
        }

        /// <summary>
        /// Gives the answer (once: an answer already given, a third one, or any after amnesty is refused). <paramref name="days"/>: a restriction's days (1-3). Returns whether it was given. The record is done after the
        /// second answer or amnesty.
        /// </summary>
        public static bool Apply(PrisonGame game, Map map, RiotRecord r, RiotAnswer a, int days = 1)
        {
            if (game == null || r == null || Refused(game, map, r, a) != null) return false;
            bool given = a switch
            {
                RiotAnswer.Restrict => Restrict(game, map, r, Mathf.Clamp(days, 1, 3)),
                RiotAnswer.ShipOut => ShipOut(game, map, r),
                RiotAnswer.Charge => Charge(game, map, r),
                RiotAnswer.Restitution => Restitution(game, map, r),
                RiotAnswer.Grievance => MeetGrievance(game, map, r),
                _ => Amnesty(game, map, r),
            };
            if (!given) return false;
            r.answers.Add(a);
            if (a == RiotAnswer.Amnesty || r.answers.Count >= MaxAnswers) r.done = true;
            return true;
        }

        private static bool Restrict(PrisonGame game, Map map, RiotRecord r, int days)
        {
            if (!RegimeOrders.Restrict(game, map, r.level, days)) return false;
            r.restrictDays = days;
            r.restrictUntilDay = Ledger.CurrentDay + days - 1;
            r.restrictBilled = false;
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_RiotRestrict".Translate(r.level.label, days));
            return true;
        }

        /// <summary>A restriction of the level is over (its last day passed, or the warden lifted it): the tension it held down lands on the wing, a day's worth for each day it ran.</summary>
        public static void Notify_RestrictionEnded(PrisonGame game, SecurityLevelDef level, bool early)
        {
            if (game?.riots == null || level == null) return;
            foreach (RiotRecord r in game.riots.records)
            {
                if (r.level != level || r.restrictUntilDay < 0 || r.restrictBilled) continue;
                r.restrictBilled = true;
                int from = r.restrictUntilDay - r.restrictDays + 1;
                int served = early ? Mathf.Clamp(Ledger.CurrentDay - from + 1, 1, r.restrictDays) : r.restrictDays;
                float tension = RestrictTensionPerDay * served;
                int n = 0;
                foreach (Pawn p in WingInmates(level))
                {
                    CompInmate c = p.Inmate();
                    c.tension = Mathf.Min(100f, c.tension + tension);
                    n++;
                }
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_RiotRestrictOver".Translate(level.label, served, tension.ToString("0")));
                Messages.Message("RP_RiotRestrictOver".Translate(level.label, served, tension.ToString("0"), n), MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        /// <summary>A stretch of a restriction was served (<paramref name="days"/>: a whole day at midnight, a part of one when it began late or was lifted): the wing is held down that much more, after the fact.</summary>
        public static void Notify_RestrictionServed(SecurityLevelDef level, float days)
        {
            float add = RestrictSuppressionPerDay * days;
            foreach (Pawn p in WingInmates(level))
            {
                CompInmate c = p.Inmate();
                c.suppressed = Mathf.Min(100f, c.suppressed + add);
            }
        }

        private static IEnumerable<Pawn> WingInmates(SecurityLevelDef level)
        {
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                foreach (Pawn p in m.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate()?.security == level && InmateUtility.IsActiveInmate(p)) yield return p;
            }
        }

        private static bool ShipOut(PrisonGame game, Map map, RiotRecord r)
        {
            Pawn lead = RingleaderHere(r, map);
            if (lead == null) return false;
            InmateUtility.TransferOut(lead, lead.Inmate(), "RP_Cause_RiotRingleader");
            // (TransferOut refuses quietly when it cannot be paid for: then the answer was not given.)
            return ShuttleSystem.OnTheWayOut(lead) || !lead.Spawned;
        }

        private static bool RiotOffence(MisconductDef def) => def == RPRiotDefOf.RP_Riot || def == RPRiotDefOf.RP_RiotJoined;

        /// <summary>
        /// The inmate was dealt with for this riot already: a case for it is on the adjudication book (waiting or answered: the sheet took it, or a charge put it there), or their riot record on the file was seen and so
        /// punished at the time (riots are loud: <see cref="MisconductUtility.Record"/> applies the sanction policy when they start) - unless the policy gives nothing for it. Within the riot's window: its start to the
        /// last tick anyone was seen rioting.
        /// </summary>
        public static bool DealtWith(PrisonGame game, RiotRecord r, Pawn p)
        {
            int id = p.thingIDNumber;
            List<AdjudicationCase> cases = game.adjudication.cases;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].inmateId == id && RiotOffence(cases[i].offence) && cases[i].tick >= r.startTick && cases[i].tick <= r.lastRiotTick) return true;
            if (p.Inmate() is not { } c) return false;
            for (int i = c.records.Count - 1; i >= 0; i--)
            {
                MisconductRecord rec = c.records[i];
                if (rec.seen && RiotOffence(rec.def) && rec.tick >= r.startTick && rec.tick <= r.lastRiotTick && (rec.punished || Sanctions.PolicyImposes(rec.def))) return true;
            }
            return false;
        }

        /// <summary>The ones a charge puts on the sheet: the seen still here who were not dealt with for this riot already (<see cref="DealtWith"/>): nobody is charged twice for one riot.</summary>
        public static List<Pawn> Chargeable(PrisonGame game, Map map, RiotRecord r)
        {
            List<Pawn> seen = SeenHere(r, map);
            seen.RemoveAll(p => DealtWith(game, r, p));
            return seen;
        }

        private static bool Charge(PrisonGame game, Map map, RiotRecord r)
        {
            int n = 0;
            foreach (Pawn p in Chargeable(game, map, r))
            {
                CompInmate c = p.Inmate();
                MisconductDef def = p.thingIDNumber == r.ringleaderId ? RPRiotDefOf.RP_Riot : RPRiotDefOf.RP_RiotJoined;
                if (!Adjudication.Queue(p, c, def, evidence: true)) continue;
                // (The case is for the riot, when it happened: its record is the one the sheet's answer marks. One with no record on the file is dated to the riot's end. Either way it is inside the riot's window,
                // which is what tells it is this riot's.)
                AdjudicationCase k0 = game.adjudication.cases[game.adjudication.cases.Count - 1];
                MisconductRecord rec = c.records.FindLast(x => RiotOffence(x.def) && x.tick >= r.startTick && x.tick <= r.lastRiotTick);
                k0.tick = rec?.tick ?? r.lastRiotTick;
                n++;
            }
            if (n == 0) return false;
            r.charged = n;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_RiotCharged".Translate(n, r.level?.label ?? "?"));
            Messages.Message("RP_RiotCharged".Translate(n), MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        /// <summary>The bill is split between the inmates seen in the riot, each paying their share out of their cash as far as it goes (the debts' way: never more than they have).</summary>
        private static bool Restitution(PrisonGame game, Map map, RiotRecord r)
        {
            List<Pawn> payers = SeenHere(r, map);
            int bill = Bill(r);
            if (payers.Count == 0 || bill <= 0) return false;
            int share = Mathf.CeilToInt(bill / (float)payers.Count), taken = 0;
            foreach (Pawn p in payers)
            {
                CompInmate c = p.Inmate();
                int pay = Mathf.Min(c.cash, share, bill - taken);
                if (pay <= 0) continue;
                c.cash -= pay;
                taken += pay;
                c.tension = Mathf.Min(100f, c.tension + RestitutionTension);
            }
            r.restitution = taken;
            if (taken > 0) game.ledger.Add(RPRiotReviewDefOf.RP_Restitution, taken, "RP_RiotRestitutionNote".Translate(r.level?.label ?? "?"));
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_RiotRestitution".Translate(Money.Format(taken), Money.Format(bill), payers.Count));
            Messages.Message("RP_RiotRestitution".Translate(Money.Format(taken), Money.Format(bill), payers.Count), MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        private static bool MeetGrievance(PrisonGame game, Map map, RiotRecord r)
        {
            if (r.grievance == null) return false;
            // (The promise is made to the ringleader only when the staff saw who that was: else it has no spokesperson, and the chronicle names nobody when it is kept or broken.)
            DemandSystem.Accept(game, r.grievance, map != null && r.RingleaderSeen ? DemandSystem.InmateById(map, r.ringleaderId) : null);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_RiotGrievance".Translate(r.grievance.label, DemandSystem.Hours));
            return true;
        }

        private static bool Amnesty(PrisonGame game, Map map, RiotRecord r)
        {
            if (r.level != null)
                foreach (Pawn p in WingInmates(r.level))
                {
                    CompInmate c = p.Inmate();
                    c.tension = Mathf.Max(0f, c.tension - AmnestyTension);
                }
            game.reputation = Mathf.Max(0f, game.reputation - AmnestyReputation);
            r.amnestyUntilTick = Now + AmnestyDays * GenDate.TicksPerDay;
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_RiotAmnesty".Translate(r.level?.label ?? "?"));
            return true;
        }

        /// <summary>
        /// What the wing's riot odds are multiplied by now (one lookup when no riot was answered): <see cref="RestrictRiotFactor"/> while a restriction holds it down, <see cref="AmnestyRiotFactor"/> while an amnesty
        /// says they got away with it.
        /// </summary>
        public static float WingFactor(PrisonGame game, SecurityLevelDef level)
        {
            if (level == null || game.riots.records.Count == 0) return 1f;
            float f = RegimeOrders.Restricted(game, level) ? RestrictRiotFactor : 1f;
            int now = Now;
            List<RiotRecord> records = game.riots.records;
            for (int i = 0; i < records.Count; i++)
                if (records[i].level == level && records[i].amnestyUntilTick > now)
                {
                    f *= AmnestyRiotFactor;
                    break;
                }
            return f;
        }

        // ---- the words ---------------------------------------------------------------------------------------------------------------------------

        public static string AnswerText(RiotRecord r, RiotAnswer a) => a switch
        {
            RiotAnswer.Restrict => "RP_RiotAnswered_Restrict".Translate(r.restrictDays).Resolve(),
            RiotAnswer.ShipOut => "RP_RiotAnswered_ShipOut".Translate(r.ringleaderName ?? "?").Resolve(),
            RiotAnswer.Charge => "RP_RiotAnswered_Charge".Translate(r.charged).Resolve(),
            RiotAnswer.Restitution => "RP_RiotAnswered_Restitution".Translate(Money.Format(r.restitution)).Resolve(),
            RiotAnswer.Grievance => "RP_RiotAnswered_Grievance".Translate(r.grievance?.label ?? "?").Resolve(),
            _ => "RP_RiotAnswered_Amnesty".Translate().Resolve(),
        };

        /// <summary>The answers given in words ("none yet").</summary>
        public static string AnswersText(RiotRecord r) =>
            r.answers.Count == 0 ? "RP_RiotAnswered_None".Translate().Resolve() : string.Join(", ", r.answers.Select(a => AnswerText(r, a)));

        /// <summary>Where the record stands, for the Intel list: still going, the report due, waiting for an answer, or what was answered.</summary>
        public static string StateText(RiotRecord r)
        {
            string text;
            if (r.Open) text = "RP_RiotState_Open".Translate().Resolve();
            else if (r.letterTick < 0 && !r.done) text = "RP_RiotState_Due".Translate(LetterHour).Resolve();
            else if (r.answers.Count == 0) text = (r.done ? "RP_RiotState_NoReckoning" : "RP_RiotState_Waiting").Translate().Resolve();
            else text = AnswersText(r) + (r.done ? "" : " " + "RP_RiotState_MoreToCome".Translate().Resolve());
            return r.negotiated ? "RP_RiotState_Negotiated".Translate(text).Resolve() : text;
        }

        /// <summary>The list line: when, which wing, who (by the seen rule).</summary>
        public static string ListLine(RiotRecord r, Map map)
        {
            string when = map != null ? GenDate.DateShortStringAt(GenDate.TickGameToAbs(r.startTick), Find.WorldGrid.LongLatOf(map.Tile)) : "?";
            string who = r.RingleaderSeen ? "RP_IntelRiotWho".Translate(r.ringleaderName, r.participants.Count - 1).Resolve() : "RP_IntelRiotWhoUnseen".Translate(r.participants.Count).Resolve();
            return "RP_IntelRiotLine".Translate(when, r.level != null ? r.level.LabelCap.Resolve() : "?", who).Resolve();
        }
    }

    /// <summary>
    /// The morning letter of a riot and its second step: the answers (a restriction of 1, 2 or 3 days, ship out, charge, restitution, the grievance, amnesty) and "no reckoning" / "that is all". Each answer that
    /// cannot be given says why on its button.
    /// </summary>
    public class PrisonDecisionWorker_RiotReview : PrisonDecisionWorker
    {
        private static readonly string[] First =
        {
            "RP_RiotReview_Restrict1", "RP_RiotReview_Restrict2", "RP_RiotReview_Restrict3", "RP_RiotReview_ShipOut", "RP_RiotReview_Charge", "RP_RiotReview_Restitution", "RP_RiotReview_Grievance",
            "RP_RiotReview_Amnesty", "RP_RiotReview_None",
        };

        private static readonly string[] Second =
        {
            "RP_RiotReview_Restrict1", "RP_RiotReview_Restrict2", "RP_RiotReview_Restrict3", "RP_RiotReview_ShipOut", "RP_RiotReview_Charge", "RP_RiotReview_Restitution", "RP_RiotReview_Grievance",
            "RP_RiotReview_Amnesty", "RP_RiotReview_Enough",
        };

        /// <summary>The option that answers nothing (and what an unanswered letter takes).</summary>
        public const int NoneOption = 8;

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => l.Is(RiotReview.MoreDecisionDef) ? Second : First;

        public override int TimeoutChoice => NoneOption;

        /// <summary>The answer an option stands for, and a restriction's days.</summary>
        public static (RiotAnswer answer, int days) AnswerOf(int option) => option switch
        {
            0 => (RiotAnswer.Restrict, 1),
            1 => (RiotAnswer.Restrict, 2),
            2 => (RiotAnswer.Restrict, 3),
            3 => (RiotAnswer.ShipOut, 0),
            4 => (RiotAnswer.Charge, 0),
            5 => (RiotAnswer.Restitution, 0),
            6 => (RiotAnswer.Grievance, 0),
            _ => (RiotAnswer.Amnesty, 0),
        };

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option == NoneOption || PrisonGame.Instance is not { } game) return null;
            RiotRecord r = game.riots.ById(l.amount);
            if (r == null) return "RP_RiotRefused_Done".Translate();
            return RiotReview.Refused(game, l.map ?? RiotReview.MapOf(r), r, AnswerOf(option).answer);
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (PrisonGame.Instance is not { } game || game.riots.ById(l.amount) is not { } r || r.done) return;
            if (option == NoneOption)
            {
                r.done = true;
                return;
            }
            (RiotAnswer answer, int days) = AnswerOf(option);
            if (!RiotReview.Apply(game, map, r, answer, days))
            {
                // (Things changed since the letter was drawn: the step is not used up. The same step comes again with the reason, and no "second answer" is asked for when none was given.)
                string why = RiotReview.Refused(game, map, r, answer) ?? "RP_RiotRefused_Changed".Translate().Resolve();
                Messages.Message("RP_RiotAnswerNotGiven".Translate(why), MessageTypeDefOf.RejectInput, historical: false);
                if (!r.done) RiotReview.SendMore(map, r, why);
                return;
            }
            // (Room for one more, and no amnesty: the second step.)
            if (!r.done && r.answers.Count < RiotReview.MaxAnswers) RiotReview.SendMore(map, r);
        }
    }

    /// <summary>The damage bill and the hurt: a blow a rioter deals, or one a rioter takes, is counted on their riot's record (see <see cref="RiotReview.Notify_Damage"/>).</summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    public static class Patch_Thing_TakeDamage_RiotBill
    {
        public struct Before
        {
            public bool counting, victimRioting;
            public int hitPoints;
            public float valuePerHitPoint;
        }

        public static void Prefix(Thing __instance, DamageInfo dinfo, out Before __state)
        {
            __state = default;
            if (!RiotReview.Counts(__instance, dinfo) || __instance.Destroyed) return;
            __state.counting = true;
            if (__instance is Pawn victim)
            {
                __state.victimRioting = victim.MentalStateDef == RPMentalStateDefOf.RP_Rioting;
                return;
            }
            if (!__instance.def.useHitPoints || __instance.MaxHitPoints <= 0) return;
            __state.hitPoints = __instance.HitPoints;
            __state.valuePerHitPoint = __instance.MarketValue / __instance.MaxHitPoints;
        }

        public static void Postfix(Thing __instance, DamageInfo dinfo, Before __state, DamageWorker.DamageResult __result)
        {
            if (!__state.counting) return;
            if (__instance is Pawn && (__result == null || __result.totalDamageDealt <= 0f)) return;
            RiotReview.Notify_Damage(__instance, dinfo, __state.victimRioting, __state.hitPoints, __state.valuePerHitPoint);
        }
    }
}
