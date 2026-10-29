using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Game-wide prison state: money, regime schedules, intake policy and daily statistics.
    /// Systems hang off it as sub-objects so each owns its own state and save block.
    /// </summary>
    public class PrisonGame : GameComponent, IThingHolder
    {
        public static PrisonGame Instance { get; private set; }

        public Ledger ledger = new Ledger();
        public Dictionary<SecurityLevelDef, RegimeSchedule> regimes = new Dictionary<SecurityLevelDef, RegimeSchedule>();
        /// <summary>Today's orders per level (see <see cref="RegimeOrders"/>): one-day overrides of the timetable, gone at midnight.</summary>
        public Dictionary<SecurityLevelDef, DayOrders> dayOrders = new Dictionary<SecurityLevelDef, DayOrders>();
        /// <summary>The days each level was given an order that keeps it in (see <see cref="RegimeOrders.HabitLevels"/>).</summary>
        public Dictionary<SecurityLevelDef, OrderDays> orderDays = new Dictionary<SecurityLevelDef, OrderDays>();
        public IntakePolicy intake = new IntakePolicy();
        public SupplySystem supply = new SupplySystem();
        /// <summary>The state shuttle: the daily one and the cargo runs, the arrivals walking in and the released waiting to leave.</summary>
        public ShuttleSystem shuttle = new ShuttleSystem();
        public PrisonStats stats = new PrisonStats();
        public PunishmentPolicy punishments = new PunishmentPolicy();
        public ProgramPolicy programs = new ProgramPolicy();
        public GrantSystem grants = new GrantSystem();
        public BureaucracySystem bureaucracy = new BureaucracySystem();
        public LaundrySystem laundry = new LaundrySystem();
        /// <summary>The tool supplies of the workshop and the kitchen and the stolen things traced to them (see <see cref="ToolCount"/>).</summary>
        public ToolLedgers tools = new ToolLedgers();
        public StorySystem story = new StorySystem();
        public MailSystem mail = new MailSystem();
        /// <summary>The warden's keep-apart orders (see <see cref="KeepApart"/>): inmates who are not to be put together.</summary>
        public KeepApartBook keepApart = new KeepApartBook();
        /// <summary>The people who applied for the posts the warden opened (see <see cref="ApplicantPool"/>): their pawns are held there, deep-saved.</summary>
        public ApplicantPool applicants = new ApplicantPool();
        /// <summary>
        /// Staff at home (off shift or suspended) and their dogs, held here until they come back. Not world pawns:
        /// the world would hand a player pawn outside a caravan to another faction.
        /// </summary>
        public ThingOwner<Pawn> home;

        /// <summary>Members of staff at home (not their dogs).</summary>
        public IEnumerable<Pawn> OffSite
        {
            get
            {
                foreach (Pawn p in home)
                    if (p.RaceProps.Humanlike) yield return p;
            }
        }

        public IThingHolder ParentHolder => null;

        public ThingOwner GetDirectlyHeldThings() => home;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
            if (applicants != null) outChildren.Add(applicants);
        }
        public int blackoutUntilTick = -1;
        /// <summary>The prison was handed over with a past (see <see cref="Handover"/>): its secrets were sown at the start and the old warden's note was the welcome letter. Set once, at the start; an old save has none.</summary>
        public bool handover;
        public bool winterJackets = true;
        public bool guardNightShift = true;
        /// <summary>The handover (Staff, the roster): the day guards stay 0-3 hours past 22:00 at overtime pay (see <see cref="ShiftSystem.InHandover"/>).</summary>
        public int handoverHours;
        /// <summary>The handover's overtime worked and not yet paid, in money and in guard-hours (accrued by the hourly pass, paid and cleared by the midnight settlement: see <see cref="ShiftSystem.AccrueOvertime"/>).</summary>
        public int overtimeOwed, overtimeHours;
        /// <summary>The colonist bar shows only the warden, the pinned staff and those who need the player now (see <see cref="StaffBar"/>).</summary>
        public bool staffBarCompact;
        /// <summary>The staff pinned to the compact colonist bar.</summary>
        public List<Pawn> staffBarPins = new List<Pawn>();
        /// <summary>The colonist bar's staff groups the player has collapsed to their pinned and needy members (see <see cref="StaffBar"/>).</summary>
        public List<StaffBarGroup> staffBarCollapsed = new List<StaffBarGroup>();
        /// <summary>The day the guards' shifts were last balanced (once a day at most).</summary>
        public int lastShiftBalanceDay = -1;
        /// <summary>When the warden's post was first found empty (-1: it is filled): the state names a successor after <see cref="WardenSystem.SuccessionDays"/> days.</summary>
        public int wardenVacantTick = -1;
        public bool autoReclassify = true;
        /// <summary>The count (Policy): how many times a day the guards count heads, 0 to <see cref="CountSystem.MaxPerDay"/>. At nought an escape is known the moment it happens, as it was before the count.</summary>
        public int countsPerDay = CountSystem.DefaultPerDay;
        /// <summary>Cells by conduct (Policy): the cell grade counts in where an inmate is housed and the daily review moves them by their entitlement (see <see cref="CellQuality"/>).</summary>
        public bool cellsByConduct = true;
        public VisitorSearchPolicy visitorSearch = VisitorSearchPolicy.PatDown;
        /// <summary>How closely the mailroom reads the post (Policy): spot checks, or every letter.</summary>
        public MailPolicy mailPolicy = MailPolicy.SpotChecks;
        /// <summary>What the staff cooks make (Policy): simple or fine meals.</summary>
        public KitchenMenu kitchenMenu = KitchenMenu.Simple;
        /// <summary>Good behaviour remission policy (off by default).</summary>
        public bool remission;
        /// <summary>Adjudicate the serious (Policy, off by default): a seen offence of severity <see cref="Adjudication.MinSeverity"/> and over is a case for the warden's sheet, not the table's sanction (see <see cref="Adjudication"/>).</summary>
        public bool adjudicate;
        /// <summary>The cases the sheet has had, answered or not (see <see cref="Adjudication"/>).</summary>
        public AdjudicationBook adjudication = new AdjudicationBook();
        /// <summary>The state's custody contracts: the open offer, the running contract, the last one finished (see <see cref="CustodyContracts"/>).</summary>
        public CustodyBook custody = new CustodyBook();
        /// <summary>The last riots, what they cost and what the warden answered (see <see cref="RiotReview"/>).</summary>
        public RiotBook riots = new RiotBook();
        /// <summary>The warden's standing orders (Policy): a decision's def name to the key of the answer every letter of it is given at once (see <see cref="StandingOrders"/>).</summary>
        public Dictionary<string, string> standingOrders = new Dictionary<string, string>();
        /// <summary>The letters standing orders answered lately, for the desk's review links (not saved: the letters are in the archive).</summary>
        public List<ChoiceLetter_PrisonDecision> standingAnswered = new List<ChoiceLetter_PrisonDecision>();
        /// <summary>A contraband amnesty: until the tick (-1 none), declared last at, items handed in this time.</summary>
        public int amnestyUntilTick = -1, lastAmnestyTick = -999999, amnestyHandedIn;
        /// <summary>Monitor payphone calls (needs the phone-tap project; costs per phone per day).</summary>
        public bool phoneTaps = true;
        public StaffSearchPolicy staffSearch = StaffSearchPolicy.None;
        /// <summary>Drug testing (Policy): off, targeted, or targeted and random (<see cref="drugTestsPerDay"/> a day), with what the round did today and yesterday.</summary>
        public DrugTestPolicy drugTests = DrugTestPolicy.Off;
        public int drugTestsPerDay = DrugTesting.DefaultPerDay;
        public DrugTestLog drugTestLog = new DrugTestLog();
        public Dictionary<SecurityLevelDef, VisitPolicy> visitPolicies = new Dictionary<SecurityLevelDef, VisitPolicy>();
        /// <summary>The tick each wing was last written to that its lid was coming off (see <see cref="Suppression"/>): the five-day gap, and the date the riot-odds spike runs from.</summary>
        public Dictionary<SecurityLevelDef, int> lidTicks = new Dictionary<SecurityLevelDef, int>();
        /// <summary>Who may live in each security level's wing (Intake tab): everyone unless the warden says men or women only.</summary>
        public Dictionary<SecurityLevelDef, WingGender> wingGenders = new Dictionary<SecurityLevelDef, WingGender>();

        public WingGender WingGenderFor(SecurityLevelDef sec) => sec != null && wingGenders.TryGetValue(sec, out WingGender g) ? g : WingGender.All;

        /// <summary>Whether any wing is kept for one sex.</summary>
        public bool AnyWingRestricted()
        {
            foreach (WingGender g in wingGenders.Values) if (g != WingGender.All) return true;
            return false;
        }

        /// <summary>Contact visits by default; maximum security and death row through glass.</summary>
        public VisitPolicy VisitPolicyFor(SecurityLevelDef sec)
        {
            if (sec == null) return VisitPolicy.Contact;
            if (visitPolicies.TryGetValue(sec, out VisitPolicy p)) return p;
            return sec.dangerFactor >= 2f ? VisitPolicy.Booth : VisitPolicy.Contact;
        }
        public InmateDemand demand = new InmateDemand();
        /// <summary>The promise to the staff that is running, how the last ones ended and the rises that stand (see <see cref="StaffAgreements"/>).</summary>
        public StaffAgreementBook agreements = new StaffAgreementBook();
        public List<Fugitive> fugitives = new List<Fugitive>();
        public List<Rearrest> rearrests = new List<Rearrest>();
        /// <summary>Rearrested ex-inmates on their way back here.</summary>
        public List<Rearrest> returning = new List<Rearrest>();
        /// <summary>The releases still under review and those settled lately (see <see cref="Supervision"/>); empty in an old save.</summary>
        public SupervisionBook supervision = new SupervisionBook();
        public int jacketStock;

        public bool BlackoutActive => blackoutUntilTick > Find.TickManager.TicksGame;
        public ArmedResponsePolicy armedResponse = ArmedResponsePolicy.EscapesAndViolence;

        private int lastDay = -1;
        /// <summary>While running, guards search every inmate (shakedown).</summary>
        public int shakedownUntilTick = -1;
        /// <summary>The prison's name (the player faction and home settlement carry it too).</summary>
        public string prisonName;
        /// <summary>Where the current week began (weekly report), and how many weeks have been reported.</summary>
        public WeekSnapshot week;
        public int weeksReported;
        /// <summary>While running, every inmate is confined to their cell.</summary>
        public int lockdownUntilTick = -1;
        public DailyIncidents today = new DailyIncidents();
        /// <summary>0–100; drives payment rates. Moves toward the daily report card.</summary>
        public float reputation = 50f;
        public float lastGrade = -1f;
        /// <summary>The daily grade, oldest first (the last <see cref="GradeHistoryDays"/> days).</summary>
        public List<float> gradeHistory = new List<float>();
        /// <summary>
        /// The grade below which a day counted as bad when each day of <see cref="gradeHistory"/> was graded, oldest first, kept in step with it (the difficulty sets the line and can change in a
        /// running game: a day keeps the line it was graded under). A save from before the line moved with the difficulty has the neutral one for all its days.
        /// </summary>
        public List<float> gradeRules = new List<float>();
        public const int GradeHistoryDays = 30;
        /// <summary>The card of each day of <see cref="gradeHistory"/>, oldest first, kept in step with it: the numbers the day's grade was worked out from. A day from before the cards were kept has an empty one ("no detail").</summary>
        public List<ReportCard> gradeCards = new List<ReportCard>();

        /// <summary>Gives every day of the history a line for a bad day: the neutral one for days that have none on record (a save from before the line moved with the difficulty).</summary>
        public void AlignGradeRules()
        {
            while (gradeRules.Count < gradeHistory.Count) gradeRules.Insert(0, PrisonDifficulty.LeversOf(PrisonDifficulty.NeutralLevel).badGrade);
            while (gradeRules.Count > gradeHistory.Count) gradeRules.RemoveAt(0);
            while (gradeCards.Count < gradeHistory.Count) gradeCards.Insert(0, new ReportCard());
            while (gradeCards.Count > gradeHistory.Count) gradeCards.RemoveAt(0);
        }

        /// <summary>Adds a day's grade to the history, with the line for a bad day that is in force now and the card it came from (none: a card with no detail).</summary>
        public void RecordGrade(float grade, ReportCard card = null)
        {
            gradeHistory.Add(grade);
            gradeRules.Add(PrisonDifficulty.BadGrade);
            gradeCards.Add(card ?? new ReportCard());
            if (gradeHistory.Count > GradeHistoryDays) gradeHistory.RemoveAt(0);
            while (gradeRules.Count > gradeHistory.Count) gradeRules.RemoveAt(0);
            while (gradeCards.Count > gradeHistory.Count) gradeCards.RemoveAt(0);
        }

        /// <summary>The card of the day at this place of <see cref="gradeHistory"/>, or null when none is on record (a card with no detail is returned as it is: its <see cref="ReportCard.detailed"/> is false).</summary>
        public ReportCard CardAt(int index)
        {
            int at = gradeCards.Count - (gradeHistory.Count - index);
            return at >= 0 && at < gradeCards.Count ? gradeCards[at] : null;
        }

        /// <summary>Whether the day at this place of <see cref="gradeHistory"/> was a bad one under the line it was graded under (the current line for a day with none on record).</summary>
        public bool BadDay(int index)
        {
            int at = gradeRules.Count - (gradeHistory.Count - index);
            return gradeHistory[index] < (at >= 0 && at < gradeRules.Count ? gradeRules[at] : PrisonDifficulty.BadGrade);
        }
        public float paymentModifier = 1f;
        public int paymentModifierUntilTick = -1;

        public bool LockdownActive => lockdownUntilTick > Find.TickManager.TicksGame;

        /// <summary>Per security level (wing) lockdowns, alongside the prison-wide one.</summary>
        public Dictionary<SecurityLevelDef, int> wingLockdowns = new Dictionary<SecurityLevelDef, int>();

        /// <summary>Is this inmate under a lockdown (prison-wide or their level's)?</summary>
        public bool LockedDown(CompInmate c) =>
            LockdownActive || (c?.security != null && wingLockdowns.TryGetValue(c.security, out int until) && until > Find.TickManager.TicksGame);

        /// <summary>Is the level under a lockdown (prison-wide or its own)?</summary>
        public bool LevelLockedDown(SecurityLevelDef sec) =>
            LockdownActive || (sec != null && wingLockdowns.TryGetValue(sec, out int until) && until > Find.TickManager.TicksGame);

        public void StartWingLockdown(SecurityLevelDef sec, int hours)
        {
            wingLockdowns[sec] = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome)
                {
                    foreach (Pawn p in m.mapPawns.PrisonersOfColonySpawned)
                        if (p.Inmate()?.security == sec) InmateUtility.InterruptForRegimeChange(p, emergency: true);
                    PrisonMap.Of(m)?.Notify_DoorsChanged();
                }
        }

        /// <summary>The per-diem's multiplier before the grades: reputation, an event's modifier while it runs, the state's special measures.</summary>
        public float BasePaymentMultiplier => (0.85f + 0.3f * reputation / 100f) * (paymentModifierUntilTick > Find.TickManager.TicksGame ? paymentModifier : 1f) * SpecialMeasures.PaymentFactor;

        /// <summary>The per-diem's whole multiplier: <see cref="BasePaymentMultiplier"/> times the last week's grades (<see cref="PerDiem.GradeFactor"/>).</summary>
        public float PaymentMultiplier => BasePaymentMultiplier * PerDiem.GradeFactor(this);

        public void StartLockdown(int hours)
        {
            lockdownUntilTick = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome)
                {
                    foreach (Pawn p in m.mapPawns.PrisonersOfColonySpawned) InmateUtility.InterruptForRegimeChange(p, emergency: true);
                    PrisonMap.Of(m)?.Notify_DoorsChanged();
                }
        }

        /// <summary>A shakedown turns every cell over: not something to do every day.</summary>
        public const int ShakedownCooldownTicks = GenDate.TicksPerDay * 2;

        /// <summary>How long a shakedown lasts (every inmate frisked and every cell turned over once, in that time).</summary>
        public const int ShakedownHours = 4;

        public int lastShakedownTick = -999999;

        public bool ShakedownReady => Find.TickManager.TicksGame - lastShakedownTick >= ShakedownCooldownTicks;

        public void StartShakedown()
        {
            if (!ShakedownReady) return;
            lastShakedownTick = Find.TickManager.TicksGame;
            StartLockdown(4);
            shakedownUntilTick = Find.TickManager.TicksGame + ShakedownHours * GenDate.TicksPerHour;
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome)
                    foreach (Pawn p in m.mapPawns.PrisonersOfColonySpawned)
                        if (p.Inmate() is { } c)
                        {
                            c.tension = UnityEngine.Mathf.Min(100f, c.tension + 10f);
                            p.needs.mood?.thoughts.memories.TryGainMemory(RPDefOf.RP_CellTurnedOver);
                        }
        }

        public PrisonGame(Game game)
        {
            home = new ThingOwner<Pawn>(this);
            Instance = this;
            // Set here, not in StartedNewGame: the scenario's start (PostGameStart) already spends money.
            ledger.funds = PrisonConstants.StartingFunds;
        }

        /// <summary>Inmates break things (vandalism, riots): rebuild what they destroy by default (the player can turn it
        /// off in the play settings; rebuilding is paid like any blueprint).</summary>
        public override void StartedNewGame()
        {
            Find.PlaySettings.autoRebuild = true;
            // The learning helper's box shows when it has a note to show (not an empty box in a corner from the first day).
            Find.PlaySettings.showLearningHelper = false;
            // A short guide to running the prison (and, as things first happen, the learning helper's notes, see Concepts): the next steps are those of the start chosen.
            ScenPart_PrisonStart start = Find.Scenario?.AllParts.OfType<ScenPart_PrisonStart>().FirstOrDefault();
            // (A prison that was handed over with a past is welcomed by the old warden's note, which is only sent with the start that sowed it: the flag is saved, but the letter is not sent twice.)
            string note = handover ? Handover.NoteText(prisonName ?? "", Handover.Last) : null;
            Find.LetterStack.ReceiveLetter("RP_IntroLabel".Translate(prisonName ?? ""), IntroText(prisonName ?? "", intake.hour, start is { starterFacility: true } ? start.facility : null, note), LetterDefOf.NeutralEvent);
        }

        /// <summary>
        /// The welcome letter's text. A prison that is handed over built (the inherited facility, the state penitentiary: <paramref name="facility"/>) has its pad and its rooms and is told what to
        /// look at, each in its own words; an empty plot (null) is told what to build, in the order the first shuttle needs it.
        /// </summary>
        public static string IntroText(string prison, int shuttleHour, Facility? facility, string handoverNote = null) =>
            // (A prison handed over with a past is welcomed by the old warden's note, which takes the lead's place; the lead's practical part, what the state's shuttle brings and pays, follows it as a paragraph of its own.)
            (handoverNote.NullOrEmpty() ? "RP_IntroLead".Translate(prison, shuttleHour).Resolve() : handoverNote + "\n\n" + "RP_HandoverPractical".Translate(shuttleHour).Resolve()) + "\n\n"
            + (facility switch
            {
                Facility.Inherited => "RP_IntroSteps_Inherited".Translate(),
                Facility.Penitentiary => "RP_IntroSteps_Penitentiary".Translate(),
                _ => "RP_IntroSteps_New".Translate(shuttleHour),
            }).Resolve() + "\n\n" + "RP_IntroTail".Translate(SpecialMeasures.BadGradeText).Resolve();

        public override void FinalizeInit()
        {
            Instance = this;
            RiotPolice.Reset();
            StaffCareers.InvalidateLeads(); // (the cached lead count holds the last game's map)
            StandingOrders.Reset();
            Amends.Reset();
            Concepts.Reset();
            Concepts.DropVanillaNotes(Find.Tutor?.learningReadout);
            EnsureRegimes();
            intake.EnsureDefaults();
            PrisonNaming.Apply(this);
        }

        private void EnsureRegimes()
        {
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
            {
                if (!regimes.TryGetValue(sec, out RegimeSchedule s) || s == null || !s.IsValid)
                    regimes[sec] = RegimeSchedule.FromDefault(sec);
            }
        }

        public RegimeSchedule RegimeFor(SecurityLevelDef sec)
        {
            if (!regimes.TryGetValue(sec, out RegimeSchedule s))
                regimes[sec] = s = RegimeSchedule.FromDefault(sec);
            return s;
        }

        /// <summary>
        /// The slot the level's timetable has at the hour, with today's orders on it (<see cref="RegimeOrders"/>). Cheap: with no orders it is the timetable's slot. There is no day in it: an hour asked for that is past
        /// midnight (the next hour at 23:00, a scan that wraps round) reads today's orders too, which are gone by then; the effects are small and the readers (the HUD's next slot, a funeral's hour, a visit's next hour) leave it be.
        /// </summary>
        public RegimeActivityDef SlotAt(SecurityLevelDef sec, int hour)
        {
            sec ??= InmateUtility.DefaultSecurity;
            RegimeActivityDef scheduled = RegimeFor(sec).At(hour);
            return dayOrders.Count == 0 ? scheduled : RegimeOrders.Apply(scheduled, hour, RegimeOrders.Of(this, sec));
        }

        public RegimeActivityDef ActivityAt(CompInmate inmate, int hour)
        {
            if (inmate.forcedActivity != null)
            {
                // Confined to the cell as a punishment (not the hole): they are still taken to the showers.
                if (inmate.forcedActivity == RPDefOf.RP_Lockup && !LockedDown(inmate) && inmate.parent.MapHeld is { } m && PrisonMap.Of(m)?.zones.Any(RPDefOf.RP_Shower) == true
                    && SlotAt(inmate.security, hour) == RPDefOf.RP_ShowerTime)
                    return RPDefOf.RP_ShowerTime;
                return inmate.forcedActivity;
            }
            if (LockedDown(inmate)) return RPDefOf.RP_Lockup;
            RegimeActivityDef act = SlotAt(inmate.security, hour);
            // Banned from the yard: free time in the cell.
            return act == RPDefOf.RP_FreeTime && Sanctions.Banned(inmate, Sanction.LoseYard) ? RPDefOf.RP_Lockup : act;
        }

        public RegimeActivityDef CurrentActivity(Pawn inmate)
        {
            CompInmate comp = inmate.Inmate();
            if (comp == null) return RPDefOf.RP_Lockup;
            RegimeActivityDef act = ActivityAt(comp, GenLocalDate.HourOfDay(inmate.MapHeld ?? Find.AnyPlayerHomeMap));
            // Too frightened to go out: free time in their cell (the door locks only if their cellmates are in too).
            if (Hiding(comp, act)) return RPDefOf.RP_Lockup;
            return act;
        }

        /// <summary>Kept in their cell on their own (a sanction, a punishment in the cell) while the regime does not lock
        /// the cell up: their cellmates are not locked in with them.</summary>
        public bool ConfinedAlone(CompInmate c, int hour) =>
            !LockedDown(c) && !SlotAt(c.security, hour).lockup
            && (c.forcedActivity == RPDefOf.RP_Lockup || Sanctions.Banned(c, Sanction.LoseYard));

        /// <summary>Spending scheduled free time in the cell out of fear (not the schedule's doing).</summary>
        public bool Hiding(CompInmate comp, RegimeActivityDef scheduled) =>
            scheduled == RPDefOf.RP_FreeTime && comp.fear >= FearSystem.HideAbove && comp.forcedActivity == null;

        public override void GameComponentTick()
        {
            int ticks = Find.TickManager.TicksGame;
            shuttle.Tick();
            if (ticks % 250 != 0) return;
            Map home = Find.AnyPlayerHomeMap;
            if (home == null) return;
            ParoleScene.Rare(this, home);
            StaffBar.Rare(this, home);
            Funerals.Rare(this, home);
            int day = Ledger.CurrentDay;
            if (lastDay < 0) lastDay = day;
            if (day != lastDay)
            {
                lastDay = day;
                DailySettlement.Run(this, home);
            }
        }

        public void Notify_HourChanged(Map map, int hour)
        {
            if (IntakeSystem.ShuttleDue(this, hour)) ShuttleSystem.CallDaily(this, map);
            ShuttleSystem.Hourly(this, map, hour);
            if (hour == 6 || hour == intake.hour) WinterClothing.Morning(map, this);
            if (hour == Adjudication.SheetHour) Adjudication.Morning(this, map);
            RiotReview.Hourly(this, map, hour);
            if (hour == 18) DemandSystem.Evening(map, this);
            DemandSystem.Hourly(map, this);
            if (UtilitySystem.Active) UtilitySystem.SampleHourly(map, this);
            FugitiveSystem.Hourly(map, this);
            ReleaseSystem.CheckRearrests(this);
            Reoffenders.Hourly(this, map);
            Supervision.Hourly(this, map);
            shuttle.LivingMarshals();
            DeathAftermath.Hourly(this);
            CondolenceSystem.Hourly(this);
            Funerals.Hourly(this, map, hour);
            ToolCount.Hourly(this, map);
            Trials.Hourly(this, map, hour);
            ComplaintSystem.HourlyGuards(this, map);
            ComplaintSystem.Hourly(this, map);
            Overdoses.Hourly(map);
            DrugTesting.Hourly(map, this);
            DebtSystem.Hourly(map, this);
            GangExit.Hourly(map);
            GangTruces.Hourly(this);
            Outbreaks.Hourly(map);
            FoodScare.Hourly(this, map);
            ExportContracts.Hourly(this);
            CustodyContracts.Hourly(this, map);
            FireBrigade.Hourly(this, map);
            Amnesty.Hourly(this, map);
            Detox.Hourly(this, map);
            FormerInmates.Hourly(this, map);
            OldAge.Hourly(this, map);
            WatchList.Hourly(this, map);
            ParoleScene.Hourly(this, map, hour);
            Amends.Hourly(this, map, hour);
            Innocence.Hourly(this, map);
            Inspections.Hourly(this, map);
            ShiftSystem.Balance(map, this);
            StaffCommute.Hourly(map, this, hour);
            StaffAgreements.Hourly(map, this, hour);
            ShiftSystem.AccrueOvertime(map, this, hour);
            StaffBreaks.Hourly(map, hour);
            WardenSystem.Hourly(map, hour);
            supply.Notify_HourChanged(map, hour);
            KitchenBills.Ensure(map);
            KeepApart.Hourly(map);
            InformantTask.Hourly(map);
            UnitOfficers.Hourly(map);
            LaborUtility.AssignJobs(map);
            VisitationSystem.HourlyTick(map, this, hour);
            PrisonGrading.SampleCompliance(map, this);
            story.pulse.Update(map);
            grants.Check(map);
            story.HourlyArcs(map);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref prisonName, "prisonName");
            Scribe_Deep.Look(ref week, "week");
            Scribe_Values.Look(ref weeksReported, "weeksReported");
            Scribe_Deep.Look(ref ledger, "ledger");
            Scribe_Collections.Look(ref regimes, "regimes", LookMode.Def, LookMode.Deep);
            Scribe_Collections.Look(ref dayOrders, "dayOrders", LookMode.Def, LookMode.Deep);
            Scribe_Collections.Look(ref orderDays, "orderDays", LookMode.Def, LookMode.Deep);
            Scribe_Deep.Look(ref intake, "intake");
            Scribe_Deep.Look(ref supply, "supply");
            Scribe_Deep.Look(ref shuttle, "shuttle");
            Scribe_Deep.Look(ref stats, "stats");
            Scribe_Deep.Look(ref punishments, "punishments");
            Scribe_Deep.Look(ref programs, "programs");
            Scribe_Deep.Look(ref grants, "grants");
            Scribe_Deep.Look(ref bureaucracy, "bureaucracy");
            Scribe_Deep.Look(ref laundry, "laundry");
            Scribe_Deep.Look(ref tools, "tools");
            Scribe_Deep.Look(ref story, "story");
            Scribe_Deep.Look(ref mail, "mail");
            Scribe_Deep.Look(ref keepApart, "keepApart");
            Scribe_Deep.Look(ref applicants, "applicants");
            Scribe_Deep.Look(ref home, "home", this);
            Scribe_Values.Look(ref blackoutUntilTick, "blackoutUntilTick", -1);
            Scribe_Values.Look(ref handover, "handover");
            Scribe_Values.Look(ref lastShakedownTick, "lastShakedownTick", -999999);
            Scribe_Values.Look(ref winterJackets, "winterJackets", true);
            Scribe_Values.Look(ref guardNightShift, "guardNightShift", true);
            Scribe_Values.Look(ref handoverHours, "handoverHours");
            Scribe_Values.Look(ref overtimeOwed, "overtimeOwed");
            Scribe_Values.Look(ref overtimeHours, "overtimeHours");
            Scribe_Values.Look(ref staffBarCompact, "staffBarCompact");
            Scribe_Collections.Look(ref staffBarPins, "staffBarPins", LookMode.Reference);
            Scribe_Collections.Look(ref staffBarCollapsed, "staffBarCollapsed", LookMode.Value);
            Scribe_Values.Look(ref lastShiftBalanceDay, "lastShiftBalanceDay", -1);
            Scribe_Values.Look(ref wardenVacantTick, "wardenVacantTick", -1);
            Scribe_Values.Look(ref visitorSearch, "visitorSearch", VisitorSearchPolicy.PatDown);
            Scribe_Values.Look(ref mailPolicy, "mailPolicy", MailPolicy.SpotChecks);
            Scribe_Values.Look(ref kitchenMenu, "kitchenMenu", KitchenMenu.Simple);
            Scribe_Values.Look(ref remission, "remission");
            Scribe_Values.Look(ref adjudicate, "adjudicate");
            Scribe_Deep.Look(ref adjudication, "adjudication");
            Scribe_Deep.Look(ref custody, "custody");
            Scribe_Deep.Look(ref riots, "riots");
            Scribe_Collections.Look(ref standingOrders, "standingOrders", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref amnestyUntilTick, "amnestyUntilTick", -1);
            Scribe_Values.Look(ref lastAmnestyTick, "lastAmnestyTick", -999999);
            Scribe_Values.Look(ref amnestyHandedIn, "amnestyHandedIn");
            Scribe_Values.Look(ref phoneTaps, "phoneTaps", true);
            Scribe_Values.Look(ref staffSearch, "staffSearch", StaffSearchPolicy.None);
            Scribe_Values.Look(ref drugTests, "drugTests", DrugTestPolicy.Off);
            Scribe_Values.Look(ref drugTestsPerDay, "drugTestsPerDay", DrugTesting.DefaultPerDay);
            Scribe_Deep.Look(ref drugTestLog, "drugTestLog");
            Scribe_Values.Look(ref autoReclassify, "autoReclassify", true);
            Scribe_Values.Look(ref countsPerDay, "countsPerDay", CountSystem.DefaultPerDay);
            Scribe_Values.Look(ref cellsByConduct, "cellsByConduct", true);
            Scribe_Collections.Look(ref visitPolicies, "visitPolicies", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref lidTicks, "lidTicks", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref wingGenders, "wingGenders", LookMode.Def, LookMode.Value);
            Scribe_Deep.Look(ref demand, "demand");
            Scribe_Deep.Look(ref agreements, "agreements");
            Scribe_Collections.Look(ref fugitives, "fugitives", LookMode.Deep);
            Scribe_Collections.Look(ref rearrests, "rearrests", LookMode.Deep);
            Scribe_Collections.Look(ref returning, "returning", LookMode.Deep);
            Scribe_Deep.Look(ref supervision, "supervision");
            Scribe_Values.Look(ref jacketStock, "jacketStock");
            Scribe_Values.Look(ref armedResponse, "armedResponse", ArmedResponsePolicy.EscapesAndViolence);
            Scribe_Values.Look(ref lastDay, "lastDay", -1);
            Scribe_Values.Look(ref shakedownUntilTick, "shakedownUntilTick", -1);
            Scribe_Values.Look(ref lockdownUntilTick, "lockdownUntilTick", -1);
            Scribe_Collections.Look(ref wingLockdowns, "wingLockdowns", LookMode.Def, LookMode.Value);
            Scribe_Deep.Look(ref today, "today");
            Scribe_Values.Look(ref reputation, "reputation", 50f);
            Scribe_Values.Look(ref lastGrade, "lastGrade", -1f);
            Scribe_Collections.Look(ref gradeHistory, "gradeHistory", LookMode.Value);
            Scribe_Collections.Look(ref gradeRules, "gradeRules", LookMode.Value);
            Scribe_Collections.Look(ref gradeCards, "gradeCards", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                gradeHistory ??= new List<float>();
                gradeRules ??= new List<float>();
                gradeCards ??= new List<ReportCard>();
                countsPerDay = Mathf.Clamp(countsPerDay, 0, CountSystem.MaxPerDay);
                // (A card that could not be read keeps its day's place, so the older days keep theirs.)
                for (int i = 0; i < gradeCards.Count; i++) gradeCards[i] ??= new ReportCard();
                AlignGradeRules();
            }
            Scribe_Values.Look(ref paymentModifier, "paymentModifier", 1f);
            Scribe_Values.Look(ref paymentModifierUntilTick, "paymentModifierUntilTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ledger ??= new Ledger();
                regimes ??= new Dictionary<SecurityLevelDef, RegimeSchedule>();
                regimes.RemoveAll(kv => kv.Key == null || kv.Value == null);
                dayOrders ??= new Dictionary<SecurityLevelDef, DayOrders>();
                dayOrders.RemoveAll(kv => kv.Key == null || kv.Value == null);
                orderDays ??= new Dictionary<SecurityLevelDef, OrderDays>();
                orderDays.RemoveAll(kv => kv.Key == null || kv.Value == null);
                intake ??= new IntakePolicy();
                supply ??= new SupplySystem();
                shuttle ??= new ShuttleSystem();
                stats ??= new PrisonStats();
                punishments ??= new PunishmentPolicy();
                today ??= new DailyIncidents();
                programs ??= new ProgramPolicy();
                grants ??= new GrantSystem();
                laundry ??= new LaundrySystem();
                // (A save from before the tool count has no supplies: they are made at full, once, when the rooms are first seen. Old contraband stays untraced.)
                tools ??= new ToolLedgers();
                story ??= new StorySystem();
                mail ??= new MailSystem();
                keepApart ??= new KeepApartBook();
                adjudication ??= new AdjudicationBook();
                custody ??= new CustodyBook();
                // (A save from before the riot review: an empty book.)
                riots ??= new RiotBook();
                // (An order for a decision that is gone or no longer allows one, or for an answer the decision no longer has or no longer takes for good, is dropped: it could never answer a letter,
                // and the Policy tab would list it for nothing.)
                standingOrders ??= new Dictionary<string, string>();
                standingOrders.RemoveAll(kv => kv.Key == null || !StandingOrders.Keeps(DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(kv.Key), kv.Value));
                applicants ??= new ApplicantPool();
                drugTestLog ??= new DrugTestLog();
                home ??= new ThingOwner<Pawn>(this);
                staffBarPins ??= new List<Pawn>();
                staffBarPins.RemoveAll(p => p == null);
                staffBarCollapsed ??= new List<StaffBarGroup>();
                // A save from before the chronicle: its past is not written down, but its first inmate came long ago.
                if (story.admissions == 0 && stats.admitted > 0)
                {
                    story.admissions = stats.admitted;
                    story.firsts.Add("admit1");
                }
                demand ??= new InmateDemand();
                agreements ??= new StaffAgreementBook();
                visitPolicies ??= new Dictionary<SecurityLevelDef, VisitPolicy>();
                visitPolicies.RemoveAll(kv => kv.Key == null);
                lidTicks ??= new Dictionary<SecurityLevelDef, int>();
                lidTicks.RemoveAll(kv => kv.Key == null);
                wingGenders ??= new Dictionary<SecurityLevelDef, WingGender>();
                wingGenders.RemoveAll(kv => kv.Key == null);
                fugitives ??= new List<Fugitive>();
                rearrests ??= new List<Rearrest>();
                returning ??= new List<Rearrest>();
                supervision ??= new SupervisionBook();
                wingLockdowns ??= new Dictionary<SecurityLevelDef, int>();
                wingLockdowns.RemoveAll(kv => kv.Key == null);
                fugitives.RemoveAll(f => f?.pawn == null);
                foreach (Fugitive f in fugitives) Find.WorldPawns.ForcefullyKeptPawns.Add(f.pawn);
                if (bureaucracy == null)
                {
                    // Saves from before the bureaucracy tree: keep everything they could already build.
                    bureaucracy = new BureaucracySystem();
                    bureaucracy.UnlockAll();
                }
            }
        }
    }

    public static class PrisonConstants
    {
        public const int StartingFunds = 25000;
    }
}
