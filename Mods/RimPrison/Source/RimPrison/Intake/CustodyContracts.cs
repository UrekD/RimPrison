using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPCustodyDefOf
    {
        public static CustodyContractDef RP_Custody_Rehabilitation;
        public static LedgerCategoryDef RP_Custody;

        static RPCustodyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCustodyDefOf));
    }

    /// <summary>
    /// One kind of custody contract (the rehabilitation contract is the first): a small cohort of inmates of the listed levels who come by the daily shuttle for a payment each on admission, and a bonus for each who
    /// attends the stated number of class sessions within a service window that starts at that inmate's own admission. The numbers are the offer's; an accepted contract keeps the ones it was signed with.
    /// </summary>
    public class CustodyContractDef : Def
    {
        public IntRange cohort = new IntRange(3, 5);
        public List<SecurityLevelDef> levels = new List<SecurityLevelDef>();
        /// <summary>The course the offer measures the prison's rooms and teachers against (any credited class session counts towards the bonus, whichever course it is).</summary>
        public ProgramDef demandProgram;
        public int serviceDays = 7, requiredSessions = 4;
        /// <summary>The shortest term the dossier gives: an inmate released before the end of their window forfeits their bonus, so the state sends none who would be.</summary>
        public int minTermDays = 10;
        public int offerHours = 48;
        /// <summary>Days from signing to be admitted by (the cohort is on a daily shuttle), and how many days at most are added for a shuttle called off or filled by the state's marshals.</summary>
        public int arrivalDays = 4, maxArrivalExtensionDays = 3;
        /// <summary>The payment on admission and the bonus, as shares of the level's intake grant.</summary>
        public float admissionShare = 0.5f, bonusShare = 1.5f;
        public IntRange offerEveryDays = new IntRange(7, 10);
    }

    public enum CustodyState : byte
    {
        /// <summary>Not admitted yet: waiting to be sent, aboard or carried over.</summary>
        Pending,
        /// <summary>Admitted: the service window runs.</summary>
        Serving,
        /// <summary>Judged at the end of the window: the sessions were attended and the bonus paid.</summary>
        Met,
        /// <summary>Judged at the end of the window: too few sessions, no bonus.</summary>
        Missed,
        /// <summary>Left before the end (released, paroled, transferred, escaped, dead): no bonus.</summary>
        Forfeited,
        /// <summary>Never admitted: the arrival window ran out, the state's measures began, or they were lost on the way. Nothing was paid.</summary>
        Dropped,
    }

    public enum CustodyOutcome : byte
    {
        Running,
        Completed,
        /// <summary>Nobody was ever admitted.</summary>
        Cancelled,
    }

    /// <summary>
    /// One place in the cohort: the dossier row (what the offer said: level, crime, term, the fee and the bonus) and, once admitted, where the inmate stands. Judged exactly once: the state leaves <see cref="CustodyState.Serving"/> once and never comes back.
    /// </summary>
    public class CustodyMember : IExposable
    {
        public SecurityLevelDef level;
        public CrimeDef crime;
        public int termDays, fee, bonus;
        public CustodyState state;
        public Pawn pawn;
        /// <summary>The thing id of the inmate once generated (-1: not yet): it outlives the pawn, so a member whose pawn is gone is told from one not made yet.</summary>
        public int pawnId = -1;
        /// <summary>The stay this is for: the inmate's count of admissions (<see cref="CompInmate.timesAdmitted"/>) when they were admitted. A return after leaving is another stay, and no part of this contract.</summary>
        public int stay;
        public int admittedTick = -1, dueTick = -1, endTick = -1;
        /// <summary>Class sessions credited inside the window (see <see cref="ProgramSystem.Complete"/>).</summary>
        public int sessions;
        /// <summary>What was paid on admission, and what was paid as the bonus (the checkpoints: each is paid once).</summary>
        public int paid, bonusPaid;
        public string name;
        /// <summary>Why they left or were dropped (a keyed string's name).</summary>
        public string why;

        public bool Open => state is CustodyState.Pending or CustodyState.Serving;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref level, "level");
            Scribe_Defs.Look(ref crime, "crime");
            Scribe_Values.Look(ref termDays, "termDays");
            Scribe_Values.Look(ref fee, "fee");
            Scribe_Values.Look(ref bonus, "bonus");
            Scribe_Values.Look(ref state, "state");
            // (A finished member keeps no pawn: nothing of theirs is saved with the contract any more.)
            if (Scribe.mode == LoadSaveMode.Saving && !Open) pawn = null;
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref pawnId, "pawnId", -1);
            Scribe_Values.Look(ref stay, "stay");
            Scribe_Values.Look(ref admittedTick, "admittedTick", -1);
            Scribe_Values.Look(ref dueTick, "dueTick", -1);
            Scribe_Values.Look(ref endTick, "endTick", -1);
            Scribe_Values.Look(ref sessions, "sessions");
            Scribe_Values.Look(ref paid, "paid");
            Scribe_Values.Look(ref bonusPaid, "bonusPaid");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref why, "why");
        }

        public string Describe() => $"{level?.defName}/{crime?.defName}/{termDays}/{fee}/{bonus}/{state}/{pawn?.thingIDNumber}:{pawnId}/{stay}/{admittedTick}/{dueTick}/{endTick}/{sessions}/{paid}/{bonusPaid}/{name}/{why}";
    }

    /// <summary>An offer the warden has not answered: the cohort as the dossier lists it. It lives on the book and the letter points at it (so a lapsed letter takes it with it).</summary>
    public class CustodyOffer : IExposable
    {
        public int id;
        public CustodyContractDef def;
        public int mapId = -1;
        public int createdTick, expiresTick;
        public List<CustodyMember> rows = new List<CustodyMember>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref mapId, "mapId", -1);
            Scribe_Values.Look(ref createdTick, "createdTick");
            Scribe_Values.Look(ref expiresTick, "expiresTick");
            Scribe_Collections.Look(ref rows, "rows", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) rows ??= new List<CustodyMember>();
        }

        public string Describe() => $"{id}/{def?.defName}/{mapId}/{createdTick}/{expiresTick}/[{string.Join(";", rows.Select(r => r.Describe()))}]";
    }

    /// <summary>A signed contract: the terms it was signed with, the cohort, and where it stands. Saved with the game.</summary>
    public class CustodyContract : IExposable
    {
        public int id;
        public CustodyContractDef def;
        /// <summary>The map the cohort comes to.</summary>
        public int mapId = -1;
        public int acceptedTick;
        /// <summary>By when the cohort must be admitted, and the days added to it (a shuttle called off, a roster the state's marshals filled).</summary>
        public int arrivalDeadlineTick, extensionDays;
        /// <summary>The terms as signed.</summary>
        public int serviceDays, requiredSessions;
        public List<CustodyMember> members = new List<CustodyMember>();
        /// <summary>The money paid so far (the admission payments, the bonuses).</summary>
        public int paidAdmission, paidBonus;
        public CustodyOutcome outcome;
        public int endTick = -1;

        public CustodyMember MemberOf(Pawn p)
        {
            if (p == null) return null;
            for (int i = 0; i < members.Count; i++)
                if (members[i].pawn == p) return members[i];
            return null;
        }

        public int Count(CustodyState s)
        {
            int n = 0;
            for (int i = 0; i < members.Count; i++)
                if (members[i].state == s) n++;
            return n;
        }

        public bool AnyOpen
        {
            get
            {
                for (int i = 0; i < members.Count; i++)
                    if (members[i].Open) return true;
                return false;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref mapId, "mapId", -1);
            Scribe_Values.Look(ref acceptedTick, "acceptedTick");
            Scribe_Values.Look(ref arrivalDeadlineTick, "arrivalDeadlineTick");
            Scribe_Values.Look(ref extensionDays, "extensionDays");
            Scribe_Values.Look(ref serviceDays, "serviceDays");
            Scribe_Values.Look(ref requiredSessions, "requiredSessions");
            Scribe_Collections.Look(ref members, "members", LookMode.Deep);
            Scribe_Values.Look(ref paidAdmission, "paidAdmission");
            Scribe_Values.Look(ref paidBonus, "paidBonus");
            Scribe_Values.Look(ref outcome, "outcome");
            Scribe_Values.Look(ref endTick, "endTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) members ??= new List<CustodyMember>();
        }

        public string Describe() =>
            $"{id}/{def?.defName}/{mapId}/{acceptedTick}/{arrivalDeadlineTick}/{extensionDays}/{serviceDays}/{requiredSessions}/{paidAdmission}/{paidBonus}/{outcome}/{endTick}/[{string.Join(";", members.Select(m => m.Describe()))}]";
    }

    /// <summary>The prison's custody contracts: the open offer, the running contract (one of each at most), the last one finished, and when the next offer is due.</summary>
    public class CustodyBook : IExposable
    {
        public int nextId = 1;
        /// <summary>The day the next offer is made (-1: not scheduled yet, the prison has had no classroom and counsellor).</summary>
        public int nextOfferDay = -1;
        public CustodyOffer offer;
        public CustodyContract contract;
        public CustodyContract last;

        public void ExposeData()
        {
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Values.Look(ref nextOfferDay, "nextOfferDay", -1);
            Scribe_Deep.Look(ref offer, "offer");
            Scribe_Deep.Look(ref contract, "contract");
            Scribe_Deep.Look(ref last, "last");
        }

        public string Describe() => $"{nextId}/{nextOfferDay}/offer {offer?.Describe()}/contract {contract?.Describe()}/last {last?.Describe()}";
    }

    /// <summary>The prison's room and teachers against what a cohort asks of them (for the offer and the Intake tab).</summary>
    public struct CustodySupply
    {
        public int rooms, seatsAtOnce, bestRoom, teachers, classHours;
        public bool Enough;
    }

    /// <summary>
    /// Custody contracts: the state offers a small cohort of minimum and medium security inmates for the prison to rehabilitate (see DESIGN.md, "Custody contracts"). They come by the daily shuttle (first on its roster after the state's
    /// own marshals' prisoners), the state pays for each on admission, and a bonus at the end of that inmate's own service window (<see cref="CustodyContractDef.serviceDays"/> from admission) for each who attended
    /// <see cref="CustodyContractDef.requiredSessions"/> class sessions in it. Each is judged exactly once; one who leaves before the end (released, paroled, transferred, escaped, dead) forfeits the bonus and nothing more.
    /// Offers are made every <see cref="CustodyContractDef.offerEveryDays"/> days once the prison has a classroom and a counsellor, one open offer and one running contract at a time.
    /// <para>
    /// Pauses and cancellations, stated: a daily shuttle called off (weather, a threat) or filled by the state's marshals' prisoners before the cohort adds a day to the arrival window (at most
    /// <see cref="CustodyContractDef.maxArrivalExtensionDays"/>); a member waits for a free bed of their level (never overcrowding) and is dropped, unpaid, when the window ends; the state's special measures cancel the cohort not yet admitted and
    /// the open offer, and admitted members' windows run on. A cohort member who was in the shuttle's hold but not unloaded stays pending and comes first on the next roster (after the marshals' prisoners).
    /// </para>
    /// </summary>
    public static class CustodyContracts
    {
        private static int Now => Find.TickManager.TicksGame;

        public static CustodyBook Book => PrisonGame.Instance?.custody;

        public static CustodyContractDef Def => RPCustodyDefOf.RP_Custody_Rehabilitation;

        // ---- the offer -----------------------------------------------------------------------------------------------

        /// <summary>The prison has a classroom (a room of the classroom zone with seats) and a counsellor on staff: what the state asks before it makes offers.</summary>
        /// <param name="fresh">Counted now, not read from the screen's half-second cache (see <see cref="Supply"/>): the midnight pass's question.</param>
        public static bool CanTeachHere(Map map, CustodyContractDef def, bool fresh = false)
        {
            CustodySupply s = Supply(map, def, 1, fresh);
            return s.rooms > 0 && s.teachers > 0;
        }

        private static readonly List<(IntVec3 anchor, int places)> venueScratch = new List<(IntVec3, int)>();
        private static readonly List<int> placesScratch = new List<int>();
        private static int supplyMap = -1, supplyStamp = -1;
        private static ProgramDef supplyProgram;
        private static CustodySupply supplyCache;

        /// <summary>
        /// How much the prison could teach at once: the classrooms (rooms of the course's zone with seats) and the teachers who can teach it, against a cohort of this size sitting <see cref="CustodyContractDef.requiredSessions"/> sessions each.
        /// The count of rooms and teachers is kept for half a second (the screen asks every frame, and the venue search walks the map's seats). Game logic asks with <paramref name="fresh"/>: a cache keyed by
        /// wall-clock time would let a counsellor fired in the last half second before midnight still count (hundreds of ticks in a fast headless run), and two runs of one seed differ by the machine's speed.
        /// A fresh count is kept for the screen as well.
        /// </summary>
        public static CustodySupply Supply(Map map, CustodyContractDef def, int cohort, bool fresh = false)
        {
            // (Real time, not game ticks: the screen is asked while the game is paused, which is when the warden builds the classroom the letter asked for.)
            int stamp = (int)(Time.realtimeSinceStartup * 2f);
            if (fresh || supplyMap != map.uniqueID || supplyStamp != stamp || supplyProgram != def.demandProgram)
            {
                var s = new CustodySupply();
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef prog = def.demandProgram;
                if (pm != null && prog != null)
                {
                    ProgramSystem.ListVenues(map, pm, prog, venueScratch);
                    placesScratch.Clear();
                    foreach ((IntVec3 anchor, int places) v in venueScratch)
                        if (v.places > 0) placesScratch.Add(v.places);
                    placesScratch.Sort((x, y) => y.CompareTo(x));
                    s.rooms = placesScratch.Count;
                    s.bestRoom = s.rooms > 0 ? placesScratch[0] : 0;
                    // (The staff on the books, at home between shifts too: a counsellor who is off duty is still the prison's counsellor.)
                    foreach (Pawn staff in StaffCommute.AllStaff(map))
                        if (staff.GetComp<CompStaff>()?.role is { } role && prog.TeacherRoles.Contains(role) && ProgramSystem.CanTeach(staff, role)) s.teachers++;
                    int classes = Mathf.Min(s.rooms, s.teachers);
                    for (int i = 0; i < classes; i++) s.seatsAtOnce += placesScratch[i];
                }
                (supplyMap, supplyStamp, supplyProgram, supplyCache) = (map.uniqueID, stamp, def.demandProgram, s);
            }
            CustodySupply result = supplyCache;
            result.Enough = result.seatsAtOnce >= cohort;
            result.classHours = result.seatsAtOnce > 0 ? def.requiredSessions * (def.demandProgram?.sessionHours ?? 1) * Mathf.CeilToInt(cohort / (float)result.seatsAtOnce) : 0;
            return result;
        }

        /// <summary>Why no offer is made now (a keyed string's name), or null: not while the state's measures run, with no classroom or counsellor, or while an offer or a contract stands.</summary>
        /// <param name="fresh">The classroom and the counsellors counted now (the midnight pass), not the screen's cached count.</param>
        public static string NoOfferBecause(PrisonGame game, Map map, bool fresh = false)
        {
            CustodyBook b = game.custody;
            if (b.contract != null) return "RP_CustodyNoOfferContract";
            if (b.offer != null) return "RP_CustodyNoOfferOpen";
            if (SpecialMeasures.Restricted) return "RP_CustodyNoOfferMeasures";
            if (!Def.levels.Any(Unlocks.IsUnlocked)) return "RP_CustodyNoOfferLevels";
            if (!CanTeachHere(map, Def, fresh)) return "RP_CustodyNoOfferClassroom";
            return null;
        }

        private static int RoundToTen(float amount) => Mathf.Max(10, Mathf.RoundToInt(amount / 10f) * 10);

        /// <summary>The cohort a new offer lists: three to five places, minimum and medium security, each with a crime the level sentences for and a term long enough to see the window through. Nothing hidden is rolled here (a gang, a claim of innocence come with the person).</summary>
        public static List<CustodyMember> RollRows(CustodyContractDef def)
        {
            var rows = new List<CustodyMember>();
            List<SecurityLevelDef> levels = def.levels.Where(Unlocks.IsUnlocked).ToList();
            if (levels.Count == 0) return rows;
            int n = def.cohort.RandomInRange;
            for (int i = 0; i < n; i++)
            {
                SecurityLevelDef level = levels.RandomElement();
                CrimeDef crime = level.crimes.Where(c => c.lifeChance <= 0f).TryRandomElement(out CrimeDef picked) ? picked : level.crimes.RandomElementWithFallback();
                rows.Add(new CustodyMember
                {
                    level = level,
                    crime = crime,
                    termDays = Mathf.Max(def.minTermDays, crime?.sentenceDays.RandomInRange ?? 0),
                    fee = RoundToTen(level.intakeGrant * def.admissionShare),
                    bonus = RoundToTen(level.intakeGrant * def.bonusShare),
                });
            }
            rows.Sort((a, b) => a.level.uiOrder != b.level.uiOrder ? a.level.uiOrder.CompareTo(b.level.uiOrder) : string.CompareOrdinal(a.crime?.defName, b.crime?.defName));
            return rows;
        }

        /// <summary>Midnight: the offer is made when it is due and nothing stands in its way (see <see cref="NoOfferBecause"/>); a prison with a classroom and a counsellor for the first time starts the clock.</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            CustodyBook b = game.custody;
            if (b == null) return;
            int today = Ledger.CurrentDay;
            // An offer whose letter is gone (answered by time, dismissed in some way) does not stand for ever.
            if (b.offer != null && Now > b.offer.expiresTick + (StoryLetters.MaxHeldDays + 1) * GenDate.TicksPerDay) b.offer = null;
            if (SpecialMeasures.Restricted && b.contract != null) CancelPending(game, "RP_CustodyEnd_Measures");
            if (NoOfferBecause(game, map, fresh: true) != null) return;
            CustodyContractDef def = Def;
            if (b.nextOfferDay < 0)
            {
                b.nextOfferDay = today + def.offerEveryDays.RandomInRange;
                return;
            }
            if (today < b.nextOfferDay) return;
            Offer(game, map);
            b.nextOfferDay = today + def.offerEveryDays.RandomInRange;
        }

        /// <summary>Makes the offer and sends its letter (a decision like any other: it counts against the day's budget of them). Null when no cohort could be listed.</summary>
        public static CustodyOffer Offer(PrisonGame game, Map map)
        {
            CustodyContractDef def = Def;
            List<CustodyMember> rows = RollRows(def);
            if (rows.Count == 0) return null;
            CustodyBook b = game.custody;
            var offer = new CustodyOffer { id = b.nextId++, def = def, mapId = map.uniqueID, createdTick = Now, expiresTick = Now + def.offerHours * GenDate.TicksPerHour, rows = rows };
            b.offer = offer;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_CustodyContract", "RP_CustodyLabel".Translate(), OfferText(map, offer), map);
            letter.amount = offer.id;
            letter.StartTimeout(def.offerHours * GenDate.TicksPerHour);
            Find.LetterStack.ReceiveLetter(letter);
            game.stats.custodyOffers++;
            return offer;
        }

        /// <summary>The dossier, one line a place: the official file only (level, crime, term, the fee and the bonus). Never who is in a gang or who claims to be innocent.</summary>
        public static string Dossier(List<CustodyMember> rows)
        {
            var lines = new List<string>();
            for (int i = 0; i < rows.Count; i++)
                lines.Add("RP_CustodyDossierLine".Translate(i + 1, rows[i].level?.label ?? "?", rows[i].crime?.label ?? "?", rows[i].termDays, Money.Format(rows[i].fee), Money.Format(rows[i].bonus)).ToString());
            return string.Join("\n", lines);
        }

        /// <summary>The beds the cohort needs, by level, against the free ones: "2 of 3 minimum security, 1 of 0 medium security".</summary>
        public static string BedsLine(Map map, List<CustodyMember> rows)
        {
            var parts = new List<string>();
            foreach (IGrouping<SecurityLevelDef, CustodyMember> g in rows.Where(r => r.level != null && r.Open).GroupBy(r => r.level).OrderBy(g => g.Key.uiOrder))
                parts.Add("RP_CustodyBedsPart".Translate(g.Count(), IntakeSystem.FreeBedCount(map, g.Key), g.Key.label).ToString());
            return string.Join(", ", parts);
        }

        /// <summary>What the cohort asks of the classroom and the counsellors, against what the prison has (see <see cref="CustodySupply"/>).</summary>
        public static string DemandLine(Map map, CustodyContractDef def, int cohort)
        {
            CustodySupply s = Supply(map, def, cohort);
            return "RP_CustodyDemand".Translate(cohort, def.requiredSessions, def.serviceDays, s.classHours, s.rooms, s.bestRoom, s.teachers, s.seatsAtOnce,
                (s.Enough ? "RP_CustodyDemandEnough" : "RP_CustodyDemandShort").Translate()).ToString();
        }

        public static TaggedString OfferText(Map map, CustodyOffer offer)
        {
            CustodyContractDef def = offer.def;
            int fees = offer.rows.Sum(r => r.fee), bonuses = offer.rows.Sum(r => r.bonus);
            return "RP_CustodyText".Translate(offer.rows.Count, def.serviceDays, def.requiredSessions, Dossier(offer.rows), Money.Format(fees), Money.Format(bonuses),
                DemandLine(map, def, offer.rows.Count), BedsLine(map, offer.rows), def.arrivalDays, def.offerHours / 24, "RP_CustodyRules".Translate(def.serviceDays).ToString());
        }

        /// <summary>
        /// The hours the open offer can still be answered in, as its letter has them (the offer's own expiry is that of the day it was made: a letter the day's budget held back gets its full answer time when it is delivered).
        /// </summary>
        public static int OfferHoursLeft(PrisonGame game, CustodyOffer offer)
        {
            int ticks = offer.expiresTick - Now;
            foreach (Letter let in Find.LetterStack.LettersListForReading)
                if (let is ChoiceLetter_PrisonDecision d && d.amount == offer.id && d.decision?.Worker is CustodyContractDecision)
                    return Mathf.Max(0, (d.TimeoutActive ? d.disappearAtTick - Now : ticks) / GenDate.TicksPerHour);
            foreach (ChoiceLetter_PrisonDecision held in game.story.deferredLetters)
                if (held.amount == offer.id && held.decision?.Worker is CustodyContractDecision)
                    return held.deferredTimeout > 0 ? held.deferredTimeout / GenDate.TicksPerHour : Mathf.Max(0, ticks / GenDate.TicksPerHour);
            return Mathf.Max(0, ticks / GenDate.TicksPerHour);
        }

        /// <summary>The offer is turned down, or left to lapse: nothing is owed and nobody comes.</summary>
        public static void Decline(PrisonGame game, int offerId)
        {
            CustodyBook b = game.custody;
            if (b.offer != null && b.offer.id == offerId) b.offer = null;
        }

        /// <summary>The offer is signed: the cohort comes by the next daily shuttles (see <see cref="IntakeSystem.GenerateRoster"/>), within the arrival window.</summary>
        public static CustodyContract Accept(PrisonGame game, Map map, int offerId)
        {
            CustodyBook b = game.custody;
            CustodyOffer offer = b.offer;
            if (offer == null || offer.id != offerId || b.contract != null || SpecialMeasures.Restricted) return null;
            CustodyContractDef def = offer.def;
            var c = new CustodyContract
            {
                id = offer.id,
                def = def,
                mapId = map.uniqueID,
                acceptedTick = Now,
                arrivalDeadlineTick = Now + def.arrivalDays * GenDate.TicksPerDay,
                serviceDays = def.serviceDays,
                requiredSessions = def.requiredSessions,
                members = offer.rows,
            };
            b.contract = c;
            b.offer = null;
            game.stats.custodyAccepted++;
            Messages.Message("RP_CustodyAccepted".Translate(c.members.Count, def.arrivalDays), MessageTypeDefOf.PositiveEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_CustodyAccepted".Translate(c.members.Count, def.serviceDays, def.requiredSessions));
            return c;
        }

        // ---- the cohort comes ----------------------------------------------------------------------------------------

        /// <summary>The pawn is a cohort member still to be admitted (aboard a shuttle or waiting in the world for one): the shuttle keeps them for the contract and not on the list of those carried over.</summary>
        public static bool Holds(Pawn p) => Book?.contract is { } c && c.MemberOf(p) is { state: CustodyState.Pending };

        /// <summary>A member's pawn has been made for the shuttle's roster (see <see cref="IntakeSystem.GenerateRoster"/>).</summary>
        public static void Notify_Generated(CustodyMember m, Pawn p)
        {
            m.pawn = p;
            m.pawnId = p.thingIDNumber;
            m.name = p.LabelShort;
        }

        /// <summary>
        /// The inmate steps into the prison (see <see cref="IntakeSystem.AdmitArrival"/>): the payment on admission is paid for them, and their window starts now. A pawn who is not a pending member of the running contract (the state's
        /// own prisoners come back through the marshals, and the same person never counts twice) is none of this.
        /// </summary>
        public static void Notify_Admitted(PrisonGame game, Pawn p)
        {
            CustodyContract c = game.custody?.contract;
            if (c == null || c.MemberOf(p) is not { state: CustodyState.Pending } m) return;
            m.state = CustodyState.Serving;
            // (The inmate was made when the roster was, and may have waited days for a shuttle to take them: their sentence runs from the day they step in, as their window does.)
            if (p.Inmate() is { } comp) comp.admittedTick = Now;
            m.stay = p.Inmate()?.timesAdmitted ?? 1;
            m.admittedTick = Now;
            m.dueTick = Now + c.serviceDays * GenDate.TicksPerDay;
            m.name = p.LabelShort;
            m.paid = m.fee;
            c.paidAdmission += m.fee;
            game.ledger.Add(RPCustodyDefOf.RP_Custody, m.fee, "RP_CustodyFeeNote".Translate(p.LabelShort));
            game.stats.custodyAdmitted++;
            Messages.Message("RP_CustodyAdmitted".Translate(p.LabelShort, Money.Format(m.fee), c.serviceDays, c.requiredSessions), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The daily shuttle was called off: the cohort waits for the next, and a day is added to the arrival window.</summary>
        public static void Notify_ShuttleCalledOff(PrisonGame game) => Extend(game.custody?.contract);

        /// <summary>The roster was full (the state's marshals' prisoners come first) and a cohort member who could have come was left off: a day is added to the arrival window.</summary>
        public static void Notify_RosterFull(PrisonGame game) => Extend(game.custody?.contract);

        private static void Extend(CustodyContract c)
        {
            if (c == null || c.Count(CustodyState.Pending) == 0 || c.extensionDays >= c.def.maxArrivalExtensionDays) return;
            c.extensionDays++;
            c.arrivalDeadlineTick += GenDate.TicksPerDay;
        }

        // ---- the service ---------------------------------------------------------------------------------------------

        /// <summary>A class session credited this inmate (see <see cref="ProgramSystem.Complete"/>: the teacher taught and they sat through it): it counts if their window is running.</summary>
        public static void Notify_Attended(Pawn p)
        {
            CustodyContract c = Book?.contract;
            if (c == null || c.MemberOf(p) is not { state: CustodyState.Serving } m || Now >= m.dueTick) return;
            m.sessions++;
        }

        /// <summary>The inmate leaves the prison's custody before their window ends (released, paroled, transferred, escaped, dead): the bonus is forfeit, nothing else is asked of anyone.</summary>
        public static void Notify_Departed(Pawn p, string why)
        {
            PrisonGame game = PrisonGame.Instance;
            CustodyContract c = game?.custody?.contract;
            if (c == null || c.MemberOf(p) is not { state: CustodyState.Serving } m) return;
            // (A window that has ended is judged on what was attended, even when the hourly pass has not come to it yet: leaving after the end forfeits nothing.)
            if (Now >= m.dueTick) Judge(game, c, m);
            else Forfeit(game, c, m, why);
        }

        private static void Forfeit(PrisonGame game, CustodyContract c, CustodyMember m, string why)
        {
            if (m.state != CustodyState.Serving) return;
            m.state = CustodyState.Forfeited;
            m.endTick = Now;
            m.why = why;
            string name = m.name ?? m.pawn?.LabelShort ?? "?";
            m.pawn = null;
            game.stats.custodyForfeited++;
            Messages.Message("RP_CustodyForfeit".Translate(name, Money.Format(m.bonus), why.Translate()), MessageTypeDefOf.NeutralEvent, historical: false);
            TryClose(game, c);
        }

        private static void Judge(PrisonGame game, CustodyContract c, CustodyMember m)
        {
            if (m.state != CustodyState.Serving) return;
            m.endTick = Now;
            string name = m.name ?? m.pawn?.LabelShort ?? "?";
            Pawn p = m.pawn;
            if (m.sessions >= c.requiredSessions)
            {
                m.state = CustodyState.Met;
                m.bonusPaid = m.bonus;
                c.paidBonus += m.bonus;
                game.ledger.Add(RPCustodyDefOf.RP_Custody, m.bonus, "RP_CustodyBonusNote".Translate(name));
                game.stats.custodyMet++;
                game.stats.custodyEarned += m.bonus;
                Messages.Message("RP_CustodyMet".Translate(name, m.sessions, c.requiredSessions, Money.Format(m.bonus)), p != null ? new LookTargets(p) : LookTargets.Invalid, MessageTypeDefOf.PositiveEvent, historical: false);
            }
            else
            {
                m.state = CustodyState.Missed;
                game.stats.custodyMissed++;
                Messages.Message("RP_CustodyMissed".Translate(name, m.sessions, c.requiredSessions), p != null ? new LookTargets(p) : LookTargets.Invalid, MessageTypeDefOf.NegativeEvent, historical: false);
            }
            m.pawn = null;
            TryClose(game, c);
        }

        /// <summary>A member who never came: nothing was paid and nothing is owed.</summary>
        private static void Drop(PrisonGame game, CustodyMember m, string why)
        {
            if (m.state != CustodyState.Pending) return;
            m.state = CustodyState.Dropped;
            m.endTick = Now;
            m.why = why;
            Pawn p = m.pawn;
            m.pawn = null;
            // (A pawn made for the roster and never admitted goes back to the state: nobody on the books, nobody left in the world.)
            if (p != null && !p.Spawned && !p.Dead && Find.WorldPawns.Contains(p) && !Aboard(game, p)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(p);
            Messages.Message("RP_CustodyDropped".Translate(m.name ?? ("RP_CustodyAPlace".Translate().ToString() + " (" + (m.level?.label ?? "?") + ")"), why.Translate()), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The pawn is aboard the shuttle that is here now (a roster made meanwhile must not bring them twice).</summary>
        public static bool Aboard(PrisonGame game, Pawn p) => p != null && game.shuttle.visit?.shuttle?.passengers != null && game.shuttle.visit.shuttle.passengers.Contains(p);

        /// <summary>
        /// The state's special measures begin (see <see cref="SpecialMeasures.Start"/>): the open offer is withdrawn, and so is the cohort not yet admitted (nobody was paid for them). The admitted go on as they were.
        /// </summary>
        public static void Notify_MeasuresBegin(PrisonGame game)
        {
            if (game.custody == null) return;
            game.custody.offer = null;
            CancelPending(game, "RP_CustodyEnd_Measures");
        }

        private static void CancelPending(PrisonGame game, string why)
        {
            CustodyContract c = game.custody?.contract;
            if (c == null) return;
            // (One aboard a shuttle that is on the ground now is unloaded and admitted as the state has already sent them; the next pass calls off whoever is left.)
            foreach (CustodyMember m in c.members)
                if (m.state == CustodyState.Pending && !Aboard(game, m.pawn)) Drop(game, m, why);
            TryClose(game, c);
        }

        /// <summary>Hourly: the arrival window, the members who left by some road the events did not tell, and the windows that have ended (judged here, once).</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            CustodyContract c = game.custody?.contract;
            if (c == null) return;
            int now = Now;
            for (int i = 0; i < c.members.Count; i++)
            {
                CustodyMember m = c.members[i];
                if (m.state == CustodyState.Pending)
                {
                    if (m.pawnId >= 0 && (m.pawn == null || m.pawn.Dead || m.pawn.Destroyed)) Drop(game, m, "RP_CustodyEnd_Lost");
                    else if (now >= c.arrivalDeadlineTick && !Aboard(game, m.pawn)) Drop(game, m, "RP_CustodyEnd_Late");
                }
                else if (m.state == CustodyState.Serving)
                {
                    if (Gone(m)) Forfeit(game, c, m, "RP_CustodyEnd_Gone");
                    else if (now >= m.dueTick) Judge(game, c, m);
                }
            }
            TryClose(game, c);
        }

        /// <summary>A serving member who is not in the prison's custody any more: dead, off the map, on the way out, or back on a stay of their own (not this contract's).</summary>
        private static bool Gone(CustodyMember m)
        {
            Pawn p = m.pawn;
            return p == null || p.Dead || p.Destroyed || p.MapHeld == null || ShuttleSystem.OnTheWayOut(p) || p.Inmate()?.timesAdmitted != m.stay;
        }

        /// <summary>When nobody is left to wait for or to judge, the contract is over: it is told, kept as the last one, and the next offer is a week or so off.</summary>
        private static void TryClose(PrisonGame game, CustodyContract c)
        {
            CustodyBook b = game.custody;
            if (b == null || b.contract != c || c.AnyOpen) return;
            bool anyAdmitted = c.members.Any(m => m.state is CustodyState.Met or CustodyState.Missed or CustodyState.Forfeited);
            c.outcome = anyAdmitted ? CustodyOutcome.Completed : CustodyOutcome.Cancelled;
            c.endTick = Now;
            b.last = c;
            b.contract = null;
            b.nextOfferDay = System.Math.Max(b.nextOfferDay, Ledger.CurrentDay + c.def.offerEveryDays.RandomInRange);
            if (anyAdmitted)
            {
                int met = c.Count(CustodyState.Met);
                Messages.Message("RP_CustodyClosed".Translate(met, c.members.Count(m => m.state != CustodyState.Dropped), Money.Format(c.paidAdmission), Money.Format(c.paidBonus)), MessageTypeDefOf.NeutralEvent, historical: true);
                Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_CustodyClosed".Translate(met, c.members.Count(m => m.state != CustodyState.Dropped), Money.Format(c.paidAdmission + c.paidBonus)));
            }
            else
            {
                Messages.Message("RP_CustodyCancelled".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
                Story.Record(RPChronicleDefOf.RP_Chr_Outside, 1, "RP_Chr_CustodyCancelled".Translate());
            }
        }

        // ---- for the screens -----------------------------------------------------------------------------------------

        /// <summary>The member's line on the Intake tab.</summary>
        public static string MemberLine(CustodyContract c, CustodyMember m)
        {
            string who = (m.name ?? "RP_CustodyAPlace".Translate().ToString()) + " - " + (m.level?.label ?? "?") + ", " + (m.crime?.label ?? "?");
            switch (m.state)
            {
                case CustodyState.Pending:
                    return "RP_CustodyRowPending".Translate(who, Mathf.Max(0f, (c.arrivalDeadlineTick - Now) / (float)GenDate.TicksPerDay).ToString("0.0")).ToString();
                case CustodyState.Serving:
                    int day = Mathf.Clamp((Now - m.admittedTick) / GenDate.TicksPerDay + 1, 1, c.serviceDays);
                    return "RP_CustodyRowServing".Translate(who, day, c.serviceDays, m.sessions, c.requiredSessions, Money.Format(m.bonus), Mathf.Max(0f, (m.dueTick - Now) / (float)GenDate.TicksPerDay).ToString("0.0")).ToString();
                case CustodyState.Met:
                    return "RP_CustodyRowMet".Translate(who, m.sessions, c.requiredSessions, Money.Format(m.bonusPaid)).ToString();
                case CustodyState.Missed:
                    return "RP_CustodyRowMissed".Translate(who, m.sessions, c.requiredSessions).ToString();
                case CustodyState.Forfeited:
                    return "RP_CustodyRowForfeited".Translate(who, (m.why ?? "RP_CustodyEnd_Gone").Translate(), Money.Format(m.bonus)).ToString();
                default:
                    return "RP_CustodyRowDropped".Translate(who, (m.why ?? "RP_CustodyEnd_Late").Translate()).ToString();
            }
        }

        public static string SummaryLine(CustodyContract c) =>
            "RP_CustodySummary".Translate(c.members.Count, c.Count(CustodyState.Serving), c.Count(CustodyState.Pending), Money.Format(c.paidAdmission), Money.Format(c.paidBonus),
                Money.Format(c.members.Where(m => m.state == CustodyState.Serving).Sum(m => m.bonus))).ToString();
    }

    /// <summary>The offer's letter: sign (the cohort comes by the shuttle) or decline; no answer is a no. "Prepare capacity first" is what the letter's postpone is for, within the offer's time.</summary>
    public class CustodyContractDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Custody_Accept", "RP_Custody_Decline" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 0 || PrisonGame.Instance?.custody is not { } b) return null;
            if (b.contract != null) return "RP_CustodyAlready".Translate().ToString();
            if (b.offer == null || b.offer.id != l.amount) return "RP_CustodyOfferGone".Translate().ToString();
            return SpecialMeasures.Restricted ? "RP_CustodyNoOfferMeasures".Translate().CapitalizeFirst().ToString() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game?.custody == null) return;
            if (option == 0 && CustodyContracts.Accept(game, map, l.amount) != null) return;
            CustodyContracts.Decline(game, l.amount);
        }
    }
}
