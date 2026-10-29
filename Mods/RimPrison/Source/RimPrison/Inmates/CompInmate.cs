using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The inmate record, attached to every humanlike and inert until the pawn becomes a prisoner of the player.
    /// </summary>
    public class CompInmate : ThingComp, IThingHolder
    {
        /// <summary>Contraband hidden in the cell (see <see cref="StashUtility"/>).</summary>
        public ThingOwner<Thing> stash;

        public CompInmate()
        {
            stash = new ThingOwner<Thing>(this);
        }


        public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public ThingOwner GetDirectlyHeldThings() => stash;

        public const int MaxRecords = 20;

        public SecurityLevelDef security;
        public CrimeDef crime;
        /// <summary>Sentence length in days; -1 for life.</summary>
        public int sentenceDays = -1;
        /// <summary>Days the warden added to the sentence by an adjudication this stay (see <see cref="Adjudication"/>; never for a lifer or death row). The term they serve is <see cref="TermDays"/>.</summary>
        public int daysAdded;
        public int admittedTick = -1;
        public int inmateNumber;
        /// <summary>Has reached their cell at least once since intake.</summary>
        public bool processed;
        /// <summary>Overrides the regime (solitary, lockdown, ...) until <see cref="forcedUntilTick"/>.</summary>
        public RegimeActivityDef forcedActivity;
        public int forcedUntilTick = -1;
        /// <summary>Death row: appeals left (-1 = not yet set up), next hearing, execution date (-1 = none).</summary>
        public int appealsLeft = -1;
        public int nextAppealTick = -1;
        public int executionTick = -1;
        public int lastOverdueTick = -999999;
        /// <summary>Seen by the psychologist (hidden reputations and gang revealed).</summary>
        public bool assessed;
        public int lastTherapyTick = -999999;
        /// <summary>Psychologist sessions so far (the first is the assessment).</summary>
        public int psychSessions;
        /// <summary>Computed on the main thread: the staff work zone this inmate may enter right now (kitchen job at
        /// work time). Read by door checks on pathfinding threads.</summary>
        public volatile PrisonZoneDef workPass;
        /// <summary>How unsafe they feel (0-100), and when they last asked for protective custody.</summary>
        public float fear;
        /// <summary>Persistent suppression (0-100) from punishment: fewer incidents, but no learning and slow recovery.</summary>
        public float suppressed;
        public int lastPcRequestTick = -999999;
        /// <summary>When they last asked the warden for a different cell (see CellRequests).</summary>
        public int lastCellRequestTick = -999999;
        /// <summary>When the warden last chose where they live (a move, a granted request, a feud left to settle): the daily
        /// cell review leaves them and the cellmates they clash with alone for <see cref="Cellmates.WardenHoldDays"/> days.</summary>
        public int wardenPlacedTick = -999999;
        /// <summary>Last change of security level: incidents before it are already acted on.</summary>
        public int lastReclassTick = -999999;
        /// <summary>The grade of cell (0-5) they are entitled to (see <see cref="CellQuality"/>), and when the clean days toward the next step started (<see cref="CellQuality.Unset"/>: not started yet).</summary>
        public int entitlement = CellQuality.PlainCell;
        public int entitlementTick = CellQuality.Unset;
        /// <summary>When they were last moved for their cell's grade (no second grade move for <see cref="CellQuality.MoveCooldownDays"/> days), and when the daily review last saw their grade mismatch (a grade move needs it at two reviews in a row).</summary>
        /// <summary>When they were last moved for their cell's grade, and when the review first saw their grade mismatch (<see cref="CellQuality.NoTick"/>: never; a tick early in a game can be below 0 in the checks'
        /// arithmetic, so the mark is not -1).</summary>
        public int gradeMovedTick = CellQuality.NoTick, gradeMismatchTick = CellQuality.NoTick;
        /// <summary>Supermax: when the warden was last asked about stepping them down (see <see cref="SupermaxSystem.Daily"/>), and the slot of the hour of the regime they last had their yard in.</summary>
        public int supermaxAskTick = -999999, yardSlot = -1;
        /// <summary>Supermax: why they were sent (a <see cref="SupermaxReason"/>, -1 not known: sent before this was kept), and the level they came from (see <see cref="SupermaxSystem.DailyPayment"/>, <see cref="SupermaxSystem.StepDownAcquitted"/>).</summary>
        public int supermaxWhy = -1;
        public SecurityLevelDef levelBeforeSupermax;
        /// <summary>Psychiatric care (see <see cref="PsychWing"/>): the ticks of their mental breaks in the last ten days, when the psychologist last looked at their case, how long since the antipsychotic ran out (in hours), and when they last had a dose. The last break (kept past the ten days: the clean days that move a patient out count from it),
        /// whether the first assessment found a trait the psych level names (a case until they change level), and the level they were moved from (where conduct moves them back to).</summary>
        public List<int> breakTicks = new List<int>();
        public int psychCheckTick = -999999, unmedicatedHours, lastDoseTick = -999999, lastBreakTick = -999999;
        public bool psychTraitCase;
        public SecurityLevelDef psychFrom;
        /// <summary>
        /// The watch list (see <see cref="WatchList"/>): when they were flagged at risk (-1: not on the list), when the period ends, the crises they have had; the last event that can put them at risk and when, the cause the flag
        /// named, since when their mood has been very low (-1: it is not), the warden's answer, the watch's last check, the last counselling session, when the last period ended (-1: never), and when the answer's
        /// measure began (-1: none; the watch's first check is counted from it, and a padded cell given for the period is given up at its end only when it is the move of that tick).
        /// </summary>
        public int riskTick = -1, watchUntilTick = -1, crisisCount;
        public RiskTrigger riskEvent, riskCause;
        public int riskEventTick = -1, lowMoodTick = -1, watchCheckTick = -1, counselTick = -1, riskEndTick = -1, measureTick = -1;
        public WatchAnswer watchAnswer;
        /// <summary>
        /// Not saved: a guard has them by the arm and is walking them somewhere (see <see cref="EscortWalk"/>). While it is set the doors on the path of the walk open to them
        /// (<see cref="EscortLeg.doors"/>), which is why it is read on pathfinding threads. The guard's job makes it again after a load (<see cref="JobDriver_EscortInmate"/>).
        /// </summary>
        [Unsaved] public volatile EscortLeg leg;
        /// <summary>
        /// Not saved: the doors on the route an evacuating inmate walks (see <see cref="EvacuationSystem"/>), the pass of a walker who goes by themselves. A set made on the main thread and never changed (a new one replaces it), read on
        /// pathfinding threads; null when no evacuation has them. Made again after a load.
        /// </summary>
        [Unsaved] public volatile HashSet<Building_Door> evacDoors;
        /// <summary>Not saved: no walk is offered to a guard for them before this tick (the last one found no way).</summary>
        [Unsaved] public int escortRetryTick;
        /// <summary>The day the shuttle's roster first found them (carried over from a landing that could not unload them) of a sex no bed of their level will take, so that they are sent elsewhere on the next
        /// day's (see <see cref="IntakeSystem.GenerateRoster"/>); -1 when nothing waits on that.</summary>
        public int carriedSkipDay = -1;
        /// <summary>Named family outside (see Relatives); familyCount counts those still in touch.</summary>
        public List<Relative> relatives = new List<Relative>();
        /// <summary>A job is waiting for them outside (news from home): less likely to reoffend.</summary>
        public bool jobWaiting;
        /// <summary>Who the current visitor is ("his daughter Ines"), for the history.</summary>
        public string visitorRelative;
        /// <summary>The current visitor is a former inmate (the kept world pawn, see <see cref="ExInmates"/>), a friend from inside, not a relative: <see cref="visitorRelative"/> then holds their name.</summary>
        public bool visitorFormer;
        /// <summary>Last time they were cast in a story event (per-pawn cooldown).</summary>
        public int lastStoryTick = -999999;
        /// <summary>How many times they have been admitted here (reoffenders come back).</summary>
        public int timesAdmitted = 1;
        /// <summary>A recruited informant on the payroll ($10 a day into their commissary account).</summary>
        public bool informantPaid;
        /// <summary>Taken off the payroll: the arrangement is over and they stop talking.</summary>
        public bool informantQuit;
        /// <summary>The level they were on before the warden moved them to protective custody to keep them safe as a witness: they go back once no open case needs them there (unless they are vulnerable).</summary>
        public SecurityLevelDef protectedFrom;
        /// <summary>A talent was noticed already (no second letter).</summary>
        public bool talentNoticed;
        /// <summary>An innocence claim was made (once per inmate); the appeal's outcome is due at the tick (-1 none).</summary>
        public bool innocenceClaimed, appealBacked;
        public int appealResolveTick = -1;
        /// <summary>When the warden declined to call paramedics (a death soon after is neglect), or -1.</summary>
        public int careRefusedTick = -1;
        /// <summary>The warden's decisions about their chronic conditions (see <see cref="OldAge"/>), one per condition; and that they died of age or illness after years inside (the family's letter says so).</summary>
        public List<CareDecision> care = new List<CareDecision>();
        public bool diedOfAge;
        /// <summary>Death row: a last meal was granted; the governor answers a clemency plea at the tick (-1 none).</summary>
        public bool lastMealGranted;
        /// <summary>A program the warden referred them to (takes the first seat; eligible even if not otherwise).</summary>
        public ProgramDef referral;
        /// <summary>An older inmate guiding this one (thing ID, -1 none; the name outlives them) until the tick.</summary>
        public int mentorId = -1, mentorUntilTick = -1;
        /// <summary>Good behaviour remission: clean days in a row, and days taken off the sentence this stay.</summary>
        public int cleanDays, remissionEarned, lastCleanCheckTick = -1;
        /// <summary>Last put a demand to the warden for the inmates.</summary>
        public int lastSpokeTick = -999999;
        /// <summary>Handed contraband in during an amnesty at this tick (once per amnesty).</summary>
        public int amnestyHandedTick = -999999;
        /// <summary>Privileges lost as a sanction, each until its tick.</summary>
        public int yardBanUntilTick = -1, shopBanUntilTick = -1;
        /// <summary>The staff they have history with (see StaffMemories).</summary>
        public List<StaffMemory> staffMemories = new List<StaffMemory>();
        /// <summary>The warden on the floor (see <see cref="WardenSystem"/>): when they last had a word with this inmate (one a day), when the warden last told them where they stand before a hearing (-1: never), and until when the warden's firm word in the canteen keeps their tension creeping up (-1: no).</summary>
        public int wardenWordTick = -999999, headsUpTick = -1, wardenFirmUntil = -1;
        public string mentorName;
        public int clemencyResolveTick = -1;
        /// <summary>Who last sold them drugs (thing id, -1 none) and when they last overdosed.</summary>
        public int lastDealerId = -1, lastOverdoseTick = -999999;
        /// <summary>Drug testing (see <see cref="DrugTesting"/>): when they last used a drug, when they were last tested and how it came out (and whether that was a random test), when they last tested positive or refused, and when each targeted reason last tested them.</summary>
        public int lastDrugUseTick = DrugTesting.Never, lastTestedTick = DrugTesting.Never, lastFailedTestTick = DrugTesting.Never;
        public DrugTestResult lastTestResult;
        public bool lastTestRandom;
        public int[] testedFor = DrugTesting.FreshTestedFor();
        /// <summary>When the warden last took them out of a smuggling ring as its dealer (see <see cref="RingSystem.Bust"/>); <see cref="RingSystem.NeverBusted"/> if they never have.</summary>
        public int ringBustedTick = RingSystem.NeverBusted;
        /// <summary>Their own history (see Story).</summary>
        public List<LifeEntry> life = new List<LifeEntry>();
        /// <summary>New arrival waiting in holding for the intake search.</summary>
        public bool awaitingSearch;
        public int holdingSinceTick = -1;
        /// <summary>Refusing meals until this tick (hunger strike).</summary>
        public int hungerStrikeUntilTick = -1;
        /// <summary>Has the faith need (prays in the chapel).</summary>
        public bool religious;
        /// <summary>Hidden: wrongly convicted.</summary>
        public bool innocent;
        /// <summary>Transient: the death in progress is a lawful execution.</summary>
        [Unsaved] public bool executing;
        /// <summary>
        /// <see cref="InmateUtility.IsActiveInmate"/>, refreshed on the main thread every 60 ticks for readers that
        /// must not touch game state (door checks on pathfinding threads).
        /// </summary>
        public volatile bool activeFlag;
        /// <summary>
        /// Not saved (rebuilt from the shuttle's lists on load): a new arrival walking from the shuttle to holding (those on their way out are released pawns, and
        /// pass the doors as the released do). Every gate that is not locked, not staff's and not held by rioters is open to them (an escort or the state's clerks let
        /// them through), so the way from a pad outside the fence is not blocked by the gate's inmate setting. Read on pathfinding threads.
        /// </summary>
        public volatile bool inTransit;

        // Security
        public float tension;
        public float suppression;
        /// <summary>Translation key of the strongest tension contributor in the last update.</summary>
        public string mainCause;
        public List<ReputationDef> reputations = new List<ReputationDef>();
        /// <summary>Hidden reputations the warden has found out about.</summary>
        public List<ReputationDef> revealed = new List<ReputationDef>();
        public List<MisconductRecord> records = new List<MisconductRecord>();
        /// <summary>Hours of punishment owed; staff restrain and take them to solitary while above zero.</summary>
        public int pendingPunishmentHours;
        /// <summary>Cell bed to return to after solitary.</summary>
        public Building_Bed homeBed;

        // Labor
        public InmateJobDef job;
        /// <summary>Set by the warden: the auto-assigner leaves this inmate's job alone.</summary>
        public bool jobLocked;
        public int hoursWorked;
        /// <summary>The day (<see cref="Ledger.CurrentDay"/>) of the last hour they were seen at work; -1 never. Grades count who works, not who holds a job.</summary>
        public int lastWorkDay = -1;

        // Contraband & visits
        public int lastSearchedTick = -999999;
        /// <summary>Flagged (metal detector, informant, spotted handover): guards will frisk them.</summary>
        public bool suspected;
        public int familyCount;
        /// <summary>The last few letters they were handed (see MailSystem), oldest first.</summary>
        public List<MailRecord> mail = new List<MailRecord>();
        public Pawn visitor;
        /// <summary>What the current visitor is bringing in (null: nothing).</summary>
        public ThingDef visitorContraband;
        public bool boothVisit;
        /// <summary>The visitor waits in the visitation room for a guard's search.</summary>
        public bool visitorSearchPending;
        /// <summary>When the visitor reached the visitation room (-1: still on the way).</summary>
        public int visitorArrivedTick = -1;
        /// <summary>This visit followed a thorough search: families find it humiliating.</summary>
        public bool visitorStripSearched;
        /// <summary>The current visit is flagged (phone tap): the visitor is searched thoroughly.</summary>
        public bool visitFlagged;
        /// <summary>A phone tap overheard a handover being arranged: the next visit will be flagged.</summary>
        public bool nextVisitFlagged;
        /// <summary>Something was arranged over the phone: the next visitor smuggles more likely.</summary>
        public bool smuggleArranged;
        /// <summary>No visits until this tick (a visitor was caught smuggling).</summary>
        public int visitBanUntilTick = -1;
        /// <summary>When the last visit of a relative ended (-1 none yet; a former inmate's visit is not one): a shot-caller's deal that promises the gang's members visits counts from it (see <see cref="GangDeals.VisitOwed"/>).</summary>
        public int lastVisitTick = -1;
        /// <summary>The last inmate who hurt this one (their thing id, when, and where the victim stood): a death from the wounds later is the assailant's murder (see <see cref="DeathAftermath"/>).</summary>
        public int assailantId = -1, assaultTick = -1;
        /// <summary>Cut off from every way in (visits, post, guards, cargo) until this tick: they were taken out of a smuggling ring. Kept here, not read off the ring, which may end before the cut does.</summary>
        public int supplyCutUntilTick = -1;
        public IntVec3 assaultCell = IntVec3.Invalid;
        [Unsaved] public int nextDrugCheckTick;
        public int lastHearingTick = -999999;
        /// <summary>Why the board refused them at the last hearing it held in the hearing room (reasons the warden could see, joined with commas), and when (-1 none).</summary>
        public string paroleDenial;
        public int paroleDenialTick = -1;
        /// <summary>A restorative meeting with the victim's family (see <see cref="Amends"/>): where it stands, and the tick of the last change (-1 none). Cleared with the stay.</summary>
        public AmendsState amendsState;
        public int amendsTick = -1;
        /// <summary>What the warden has promised to arrange for their release (see <see cref="ReleasePrep"/>): nothing, lodging, or a job interview. Cleared with the stay.</summary>
        public ReleaseSupport releasePlan;
        public int programsPassed;
        /// <summary>Their goal, or the last one they had (see <see cref="Wants"/>).</summary>
        public InmateWant want;
        /// <summary>Grudges that ended lately (see <see cref="Wants.RecentGrudge"/>).</summary>
        public List<GrudgeRecord> pastGrudges = new List<GrudgeRecord>();

        // Gangs and intel
        public GangDef gang;
        public bool gangLeader;
        /// <summary>0 member, 1 lieutenant, 2 leader (see <see cref="GangRanks"/>; the leader is also <see cref="gangLeader"/>, which the rest of the code reads).</summary>
        public byte gangRank;
        /// <summary>The warden knows this inmate's gang.</summary>
        public bool gangKnown;
        /// <summary>Carries their gang's mark (rolled at intake): a search or a frisk shows it and gives the gang away.</summary>
        public bool gangTattoo;
        /// <summary>Doubts about the gang since this tick (a birth, a mentor, a program passed, a friend killed in a war): the daily pass may turn them into the goal to leave (-1 none).</summary>
        public int gangDoubtTick = -1;
        /// <summary>The gang they renounced and when (see <see cref="GangExit"/>): hated by it, and left out of turf presence, for <see cref="GangExit.HatedDays"/>.</summary>
        public GangDef exGang;
        public int exGangTick = -1;
        /// <summary>The old gang's green light is on (a threat made, the beating due at <see cref="exNextTick"/>), and the beatings so far.</summary>
        public bool exMarked;
        public int exNextTick;
        public byte exBeatings;
        /// <summary>0..1 hidden dig progress of a tunnel from this inmate's cell.</summary>
        public float tunnelProgress;
        public bool tunnelKnown;
        /// <summary>Staff intend to search this inmate's cell (tip, shakedown); cleared only by a cell search.</summary>
        public bool cellSearchPending;
        /// <summary>Snitch whose tip made staff suspect this inmate (exposure risk when acted on).</summary>
        public Pawn informedBy;
        public bool snitchExposed;
        /// <summary>A known informant's task (see <see cref="InformantTask"/>): the inmate the warden set them to watch (thingIDNumber, -1: none), and since when (the days in place).</summary>
        public int taskTargetId = -1, taskSinceTick = -1;
        public int lastCellSearchTick = -999999;
        /// <summary>When a count last had this inmate on its wing (-1: never): see <see cref="CountSystem"/>.</summary>
        public int lastCountedTick = -1;
        /// <summary>Inmate dollars: wages and family deposits, spent at the commissary (or on bribes).</summary>
        public int cash;
        /// <summary>Staff member who smuggled their latest contraband in (traceable when found).</summary>
        public Pawn smuggledBy;
        public List<ProgramDef> passedPrograms = new List<ProgramDef>();
        public Dictionary<ProgramDef, int> programProgress = new Dictionary<ProgramDef, int>();
        /// <summary>Library books in the cell waiting to be read, the ticks read of the one they are on, and the workouts they have done (see <see cref="LibrarySystem"/>, <see cref="ExerciseUtility"/>).</summary>
        public int books, bookTicks, workouts;

        public Pawn Pawn => parent as Pawn;

        public bool IsLifer => sentenceDays < 0;

        /// <summary>The sentence as it stands: the court's days and the days added since (-1 for life). Every reading of the sentence (release, parole, the file) goes by this.</summary>
        public int TermDays => sentenceDays < 0 ? sentenceDays : sentenceDays + daysAdded;

        /// <summary>Takes up to <paramref name="days"/> off the term, the days added first, and never below the days served (rounded up). Returns the days taken.</summary>
        public int CutTerm(int days)
        {
            if (IsLifer || days <= 0) return 0;
            int floor = UnityEngine.Mathf.CeilToInt(DaysServed), room = UnityEngine.Mathf.Max(0, TermDays - floor), cut = UnityEngine.Mathf.Min(days, room);
            int fromAdded = UnityEngine.Mathf.Min(cut, daysAdded);
            daysAdded -= fromAdded;
            sentenceDays -= cut - fromAdded;
            return cut;
        }

        public float DaysServed => admittedTick < 0 ? 0f : (Find.TickManager.TicksGame - admittedTick) / (float)GenDate.TicksPerDay;

        public bool HasReputation(ReputationDef def) => def != null && reputations.Contains(def);

        public bool KnownReputation(ReputationDef def) => HasReputation(def) && (!def.hidden || revealed.Contains(def));

        public float MisconductFactor(MisconductDef def)
        {
            float f = 1f;
            for (int i = 0; i < reputations.Count; i++)
            {
                List<MisconductFactor> factors = reputations[i].misconductFactors;
                for (int j = 0; j < factors.Count; j++)
                    if (factors[j].misconduct == def) f *= factors[j].factor;
            }
            return f;
        }

        public void AddRecord(MisconductRecord rec)
        {
            // (A full file gives up its oldest mark first, whichever kind of record comes in; with no mark to give way a mark is not written and a record takes the place of the oldest, the file's history is worth more than the marks.)
            if (records.Count >= MaxRecords)
            {
                int oldest = records.FindIndex(r => r.def.mark);
                if (oldest < 0 && rec.def.mark) return;
                records.RemoveAt(oldest < 0 ? 0 : oldest);
            }
            records.Add(rec);
        }

        public const int ResistingHours = 6;

        /// <summary>
        /// Hours in the hole for a restraint: the policy's for the latest offence in the last hour; none if its sanction is
        /// a lighter one (already applied: the restraint only separates them) unless they were acting out beyond it
        /// (attacking staff, berserk, rioting, an escape with no record of its own); with no recent offence, the hours for
        /// resisting staff.
        /// </summary>
        public int PolicyHoursForLatest(bool actingOut = false)
        {
            int now = Find.TickManager.TicksGame;
            // The latest offence staff saw: one nobody saw cannot be what they restrained them for.
            MisconductRecord last = null;
            for (int i = records.Count - 1; i >= 0; i--)
                if (records[i].seen && !records[i].def.mark)
                {
                    last = records[i];
                    break;
                }
            if (last != null && now - last.tick <= GenDate.TicksPerHour)
                // (An offence the warden's sheet decides is no table's hole: it waits for the sheet, as a light sanction does.)
                return PrisonGame.Instance.punishments.KindFor(last.def) == Sanction.Solitary && !Adjudication.Takes(last.def) ? PrisonGame.Instance.punishments.SolitaryHoursFor(last.def) : actingOut ? ResistingHours : 0;
            return ResistingHours;
        }

        /// <summary>A mental state that is an act of its own, beyond the latest recorded offence.</summary>
        public bool ActingOut(Pawn p)
        {
            MentalStateDef s = p.MentalStateDef;
            if (s == null) return false;
            if (s == RPMentalStateDefOf.RP_Escaping) return records.Count == 0 || records[records.Count - 1].def != RPShuttleDefOf.RP_EscapeAttempt;
            return s == RPMentalStateDefOf.RP_Assaulting || s == RPMentalStateDefOf.RP_Rioting || s == MentalStateDefOf.Berserk;
        }

        public bool MisbehavedWithin(int ticks)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = records.Count - 1; i >= 0; i--)
                if (now - records[i].tick <= ticks && !records[i].def.mark) return true;
            return false;
        }

        public void MarkLastRecordPunished()
        {
            // (Only a record the staff saw: nothing is punished for what nobody saw, and the file shows no line for it.)
            for (int i = records.Count - 1; i >= 0; i--)
                if (!records[i].punished && records[i].seen)
                {
                    records[i].punished = true;
                    return;
                }
        }

        public override void PostExposeData()
        {
            Scribe_Defs.Look(ref security, "rpSecurity");
            Scribe_Defs.Look(ref crime, "rpCrime");
            Scribe_Values.Look(ref sentenceDays, "rpSentence", -1);
            Scribe_Values.Look(ref daysAdded, "rpDaysAdded");
            Scribe_Values.Look(ref admittedTick, "rpAdmitted", -1);
            Scribe_Values.Look(ref inmateNumber, "rpNumber");
            Scribe_Values.Look(ref processed, "rpProcessed");
            Scribe_Defs.Look(ref forcedActivity, "rpForced");
            Scribe_Values.Look(ref forcedUntilTick, "rpForcedUntil", -1);
            Scribe_Values.Look(ref appealsLeft, "rpAppealsLeft", -1);
            Scribe_Values.Look(ref nextAppealTick, "rpNextAppeal", -1);
            Scribe_Values.Look(ref executionTick, "rpExecution", -1);
            Scribe_Values.Look(ref lastOverdueTick, "rpLastOverdue", -999999);
            Scribe_Values.Look(ref innocent, "rpInnocent");
            Scribe_Values.Look(ref religious, "rpReligious");
            Scribe_Values.Look(ref assessed, "rpAssessed");
            Scribe_Values.Look(ref lastTherapyTick, "rpLastTherapy", -999999);
            Scribe_Values.Look(ref psychSessions, "rpSessions");
            Scribe_Values.Look(ref hungerStrikeUntilTick, "rpHungerStrike", -1);
            Scribe_Values.Look(ref awaitingSearch, "rpAwaitingSearch");
            Scribe_Values.Look(ref books, "rpBooks");
            Scribe_Values.Look(ref bookTicks, "rpBookTicks");
            Scribe_Values.Look(ref workouts, "rpWorkouts");
            Scribe_Values.Look(ref fear, "rpFear");
            Scribe_Deep.Look(ref stash, "rpStash", this);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) stash ??= new ThingOwner<Thing>(this);
            Scribe_Values.Look(ref suppressed, "rpSuppressed");
            Scribe_Values.Look(ref lastPcRequestTick, "rpLastPcRequest", -999999);
            Scribe_Values.Look(ref lastCellRequestTick, "rpLastCellRequest", -999999);
            Scribe_Values.Look(ref wardenPlacedTick, "rpWardenPlaced", -999999);
            Scribe_Values.Look(ref lastReclassTick, "rpLastReclass", -999999);
            Scribe_Values.Look(ref entitlement, "rpEntitlement", CellQuality.PlainCell);
            Scribe_Values.Look(ref entitlementTick, "rpEntitlementTick", CellQuality.Unset);
            Scribe_Values.Look(ref gradeMovedTick, "rpGradeMovedTick", CellQuality.NoTick);
            Scribe_Values.Look(ref gradeMismatchTick, "rpGradeMismatchTick", CellQuality.NoTick);
            Scribe_Values.Look(ref supermaxAskTick, "rpSupermaxAsk", -999999);
            Scribe_Values.Look(ref yardSlot, "rpYardSlot", -1);
            Scribe_Values.Look(ref supermaxWhy, "rpSupermaxWhy", -1);
            Scribe_Defs.Look(ref levelBeforeSupermax, "rpBeforeSupermax");
            Scribe_Collections.Look(ref breakTicks, "rpBreakTicks", LookMode.Value);
            Scribe_Values.Look(ref psychCheckTick, "rpPsychCheck", -999999);
            Scribe_Values.Look(ref unmedicatedHours, "rpUnmedicatedHours");
            Scribe_Values.Look(ref lastDoseTick, "rpLastDose", -999999);
            Scribe_Values.Look(ref lastBreakTick, "rpLastBreak", -999999);
            Scribe_Values.Look(ref psychTraitCase, "rpPsychTraitCase");
            Scribe_Defs.Look(ref psychFrom, "rpPsychFrom");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) breakTicks ??= new List<int>();
            Scribe_Values.Look(ref riskTick, "rpRiskTick", -1);
            Scribe_Values.Look(ref watchUntilTick, "rpWatchUntil", -1);
            Scribe_Values.Look(ref crisisCount, "rpCrisisCount");
            Scribe_Values.Look(ref riskEvent, "rpRiskEvent");
            Scribe_Values.Look(ref riskEventTick, "rpRiskEventTick", -1);
            Scribe_Values.Look(ref riskCause, "rpRiskCause");
            Scribe_Values.Look(ref lowMoodTick, "rpLowMoodTick", -1);
            Scribe_Values.Look(ref watchAnswer, "rpWatchAnswer");
            Scribe_Values.Look(ref watchCheckTick, "rpWatchCheck", -1);
            Scribe_Values.Look(ref counselTick, "rpCounselTick", -1);
            Scribe_Values.Look(ref riskEndTick, "rpRiskEnd", -1);
            Scribe_Values.Look(ref measureTick, "rpMeasureTick", -1);
            Scribe_Values.Look(ref carriedSkipDay, "rpCarriedSkipDay", -1);
            Scribe_Collections.Look(ref life, "rpLife", LookMode.Deep);
            Scribe_Values.Look(ref lastStoryTick, "rpLastStory", -999999);
            Scribe_Values.Look(ref timesAdmitted, "rpTimesAdmitted", 1);
            Scribe_Values.Look(ref informantPaid, "rpInformantPaid");
            Scribe_Values.Look(ref informantQuit, "rpInformantQuit");
            Scribe_Values.Look(ref wardenWordTick, "rpWardenWord", -999999);
            Scribe_Values.Look(ref headsUpTick, "rpHeadsUp", -1);
            Scribe_Values.Look(ref wardenFirmUntil, "rpWardenFirm", -1);
            Scribe_Defs.Look(ref protectedFrom, "rpProtectedFrom");
            Scribe_Values.Look(ref talentNoticed, "rpTalentNoticed");
            Scribe_Values.Look(ref innocenceClaimed, "rpInnocenceClaimed");
            Scribe_Values.Look(ref appealBacked, "rpAppealBacked");
            Scribe_Values.Look(ref appealResolveTick, "rpAppealResolve", -1);
            Scribe_Values.Look(ref careRefusedTick, "rpCareRefused", -1);
            Scribe_Collections.Look(ref care, "rpCare", LookMode.Deep);
            Scribe_Values.Look(ref diedOfAge, "rpDiedOfAge");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                care ??= new List<CareDecision>();
                care.RemoveAll(d => d?.condition == null);
            }
            Scribe_Values.Look(ref lastMealGranted, "rpLastMeal");
            Scribe_Defs.Look(ref referral, "rpReferral");
            Scribe_Values.Look(ref mentorId, "rpMentor", -1);
            Scribe_Values.Look(ref mentorUntilTick, "rpMentorUntil", -1);
            Scribe_Values.Look(ref mentorName, "rpMentorName");
            Scribe_Values.Look(ref cleanDays, "rpCleanDays");
            Scribe_Values.Look(ref remissionEarned, "rpRemission");
            Scribe_Values.Look(ref lastCleanCheckTick, "rpCleanCheck", -1);
            Scribe_Values.Look(ref lastSpokeTick, "rpLastSpoke", -999999);
            Scribe_Values.Look(ref amnestyHandedTick, "rpAmnestyHanded", -999999);
            Scribe_Values.Look(ref yardBanUntilTick, "rpYardBan", -1);
            Scribe_Values.Look(ref shopBanUntilTick, "rpShopBan", -1);
            Scribe_Values.Look(ref clemencyResolveTick, "rpClemencyResolve", -1);
            Scribe_Values.Look(ref lastDealerId, "rpLastDealer", -1);
            Scribe_Values.Look(ref lastOverdoseTick, "rpLastOverdose", -999999);
            Scribe_Values.Look(ref lastDrugUseTick, "rpLastDrugUse", DrugTesting.Never);
            Scribe_Values.Look(ref lastTestedTick, "rpLastTested", DrugTesting.Never);
            Scribe_Values.Look(ref lastFailedTestTick, "rpLastTestFailed", DrugTesting.Never);
            Scribe_Values.Look(ref lastTestResult, "rpLastTestResult");
            Scribe_Values.Look(ref lastTestRandom, "rpLastTestRandom");
            for (int i = 0; i < testedFor.Length; i++) Scribe_Values.Look(ref testedFor[i], "rpTestedFor" + i, DrugTesting.Never);
            Scribe_Values.Look(ref ringBustedTick, "rpRingBusted", RingSystem.NeverBusted);
            Scribe_Collections.Look(ref relatives, "rpRelatives", LookMode.Deep);
            Scribe_Values.Look(ref jobWaiting, "rpJobWaiting");
            Scribe_Values.Look(ref visitorRelative, "rpVisitorRelative");
            Scribe_Values.Look(ref visitorFormer, "rpVisitorFormer");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) relatives ??= new List<Relative>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) life ??= new List<LifeEntry>();
            Scribe_Collections.Look(ref staffMemories, "rpStaffMemories", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) staffMemories ??= new List<StaffMemory>();
            Scribe_Values.Look(ref holdingSinceTick, "rpHoldingSince", -1);
            Scribe_Values.Look(ref tension, "rpTension");
            Scribe_Values.Look(ref mainCause, "rpCause");
            Scribe_Collections.Look(ref reputations, "rpReputations", LookMode.Def);
            Scribe_Collections.Look(ref revealed, "rpRevealed", LookMode.Def);
            Scribe_Collections.Look(ref records, "rpRecords", LookMode.Deep);
            Scribe_Values.Look(ref pendingPunishmentHours, "rpPendingPunishment");
            Scribe_References.Look(ref homeBed, "rpHomeBed");
            Scribe_Defs.Look(ref job, "rpJob");
            Scribe_Values.Look(ref jobLocked, "rpJobLocked");
            Scribe_Values.Look(ref hoursWorked, "rpHoursWorked");
            Scribe_Values.Look(ref lastWorkDay, "rpLastWorkDay", -1);
            Scribe_Values.Look(ref lastSearchedTick, "rpLastSearched", -999999);
            Scribe_Values.Look(ref suspected, "rpSuspected");
            Scribe_Values.Look(ref familyCount, "rpFamily");
            Scribe_Collections.Look(ref mail, "rpMail", LookMode.Deep);
            Scribe_References.Look(ref visitor, "rpVisitor");
            Scribe_Defs.Look(ref visitorContraband, "rpVisitorContraband");
            Scribe_Values.Look(ref boothVisit, "rpBoothVisit");
            Scribe_Values.Look(ref visitorSearchPending, "rpVisitorSearchPending");
            Scribe_Values.Look(ref visitorArrivedTick, "rpVisitorArrived", -1);
            Scribe_Values.Look(ref visitorStripSearched, "rpVisitorStripSearched");
            Scribe_Values.Look(ref visitFlagged, "rpVisitFlagged");
            Scribe_Values.Look(ref nextVisitFlagged, "rpNextVisitFlagged");
            Scribe_Values.Look(ref smuggleArranged, "rpSmuggleArranged");
            Scribe_Values.Look(ref visitBanUntilTick, "rpVisitBan", -1);
            Scribe_Values.Look(ref lastVisitTick, "rpLastVisit", -1);
            Scribe_Values.Look(ref supplyCutUntilTick, "rpSupplyCut", -1);
            Scribe_Values.Look(ref assailantId, "rpAssailant", -1);
            Scribe_Values.Look(ref assaultTick, "rpAssaultTick", -1);
            Scribe_Values.Look(ref assaultCell, "rpAssaultCell", IntVec3.Invalid);
            Scribe_Values.Look(ref lastHearingTick, "rpLastHearing", -999999);
            Scribe_Values.Look(ref paroleDenial, "rpParoleDenial");
            Scribe_Values.Look(ref paroleDenialTick, "rpParoleDenialTick", -1);
            Scribe_Values.Look(ref amendsState, "rpAmendsState");
            Scribe_Values.Look(ref amendsTick, "rpAmendsTick", -1);
            Scribe_Values.Look(ref releasePlan, "rpReleasePlan");
            Scribe_Values.Look(ref programsPassed, "rpProgramsPassed");
            Scribe_Deep.Look(ref want, "rpWant");
            Scribe_Collections.Look(ref pastGrudges, "rpPastGrudges", LookMode.Deep);
            Scribe_Defs.Look(ref gang, "rpGang");
            Scribe_Values.Look(ref gangLeader, "rpGangLeader");
            Scribe_Values.Look(ref gangKnown, "rpGangKnown");
            Scribe_Values.Look(ref gangRank, "rpGangRank");
            Scribe_Values.Look(ref gangTattoo, "rpGangTattoo");
            Scribe_Values.Look(ref gangDoubtTick, "rpGangDoubt", -1);
            Scribe_Defs.Look(ref exGang, "rpExGang");
            Scribe_Values.Look(ref exGangTick, "rpExGangTick", -1);
            Scribe_Values.Look(ref exMarked, "rpExMarked");
            Scribe_Values.Look(ref exNextTick, "rpExNext");
            Scribe_Values.Look(ref exBeatings, "rpExBeatings");
            Scribe_Values.Look(ref tunnelProgress, "rpTunnel");
            Scribe_Values.Look(ref tunnelKnown, "rpTunnelKnown");
            Scribe_Values.Look(ref cellSearchPending, "rpCellSearchPending");
            Scribe_References.Look(ref informedBy, "rpInformedBy");
            Scribe_Values.Look(ref snitchExposed, "rpSnitchExposed");
            Scribe_Values.Look(ref taskTargetId, "rpTaskTarget", -1);
            Scribe_Values.Look(ref taskSinceTick, "rpTaskSince", -1);
            Scribe_Values.Look(ref lastCellSearchTick, "rpLastCellSearch", -999999);
            Scribe_Values.Look(ref lastCountedTick, "rpLastCounted", -1);
            Scribe_Values.Look(ref cash, "rpCash");
            Scribe_References.Look(ref smuggledBy, "rpSmuggledBy");
            Scribe_Collections.Look(ref passedPrograms, "rpPassedPrograms", LookMode.Def);
            Scribe_Collections.Look(ref programProgress, "rpProgramProgress", LookMode.Def, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                reputations ??= new List<ReputationDef>();
                reputations.RemoveAll(r => r == null);
                revealed ??= new List<ReputationDef>();
                revealed.RemoveAll(r => r == null);
                passedPrograms ??= new List<ProgramDef>();
                passedPrograms.RemoveAll(x => x == null);
                programProgress ??= new Dictionary<ProgramDef, int>();
                programProgress.RemoveAll(kv => kv.Key == null);
                records ??= new List<MisconductRecord>();
                records.RemoveAll(r => r?.def == null);
                mail ??= new List<MailRecord>();
                mail.RemoveAll(m => m?.kind == null);
                if (want?.def == null) want = null;
                pastGrudges ??= new List<GrudgeRecord>();
                pastGrudges.RemoveAll(g => g == null);
            }
        }

        public override void CompTickInterval(int delta)
        {
            if (Pawn == null) return;
            if (parent.IsHashIntervalTick(60, delta)) RefreshActiveFlag();
            if (!activeFlag) return;
            // (A walk is checked often: the guard that is not on it any more is the end of the pass.)
            if (leg != null && parent.IsHashIntervalTick(15, delta)) EscortWalk.Validate(Pawn, this);
            if (parent.IsHashIntervalTick(250, delta)) Rare();
            if (parent.IsHashIntervalTick(GenDate.TicksPerHour, delta)) Hourly();
        }

        private void Rare()
        {
            Pawn p = Pawn;
            // Kitchen workers may pass the work-zone door at work time, and keep the pass while still inside so they
            // can walk out when the shift ends.
            // Anyone standing in a staff zone may leave it (job revoked, punished, shift over); entering needs the job.
            PrisonZoneDef pass = null;
            PrisonZoneDef inside = PrisonMap.Of(p.Map)?.zones.ZoneAt(p.Position);
            if (inside is { staffOnly: true } && !p.InAggroMentalState) pass = inside;
            else if (job?.zone is { staffOnly: true } wz && !p.InMentalState && forcedActivity == null
                     && PrisonGame.Instance.CurrentActivity(p) == RPLaborDefOf.RP_Work) pass = wz;
            if (pass != workPass)
            {
                workPass = pass;
                PrisonMap.Of(p.Map)?.Notify_WorkPassChanged(p);
            }
            if (forcedActivity != null && forcedUntilTick >= 0 && Find.TickManager.TicksGame >= forcedUntilTick)
                PunishmentUtility.End(p, this);
            if (pendingPunishmentHours > 0 && p.InBed() && p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)
                && (PunishmentUtility.IsSolitaryBed(p.CurrentBed()) || !PunishmentUtility.AnySolitaryBed(p.Map)))
                PunishmentUtility.Begin(p, this);
            else PunishmentUtility.ServeWhereNoBed(p, this);
            if (visitor != null && (visitor.Destroyed || !visitor.Spawned)) VisitationSystem.EndVisit(this, null);
            // Laid up after the visitor came (shot, collapsed): no point waiting; the visitor goes home now.
            else if (visitor != null && p.CurJobDef != RPVisitDefOf.RP_VisitTalk && (p.Downed || HealthAIUtility.ShouldSeekMedicalRest(p)))
                VisitationSystem.EndVisit(this, visitor);
            else if (visitor != null && visitorArrivedTick < 0 && PrisonMap.Of(p.Map)?.zones.ZoneAt(visitor.Position) == RPVisitDefOf.RP_Visitation)
            {
                visitorArrivedTick = Find.TickManager.TicksGame;
                Amends.Notify_FamilyArrived(p, this);
            }
            // The visitor is waiting (searched or not needed; the victim's family, with the counsellor at the table): put down the horseshoes.
            else if (visitor != null && VisitorSearch.Cleared(this) && Amends.ReadyToTalk(p, this) && p.CurJobDef != RPVisitDefOf.RP_VisitTalk && forcedActivity == null
                     && !PrisonGame.Instance.CurrentActivity(p).lockup && PrisonMap.Of(p.Map)?.zones.ZoneAt(visitor.Position) == RPVisitDefOf.RP_Visitation)
                InmateUtility.InterruptForRegimeChange(p);
            // Bursting bladder wakes them up (the urgent-needs node then finds the toilet).
            if (!p.Awake() && p.InBed() && p.needs.TryGetNeed(RPDefOf.RP_Bladder) is { CurLevel: < 0.1f } && !p.Downed)
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (!p.InMentalState) ContrabandUtility.HideWeapon(p);
            if (PrisonMap.Of(p.Map) is { } spm) StashUtility.Rare(p, this, spm);
            if (!processed)
            {
                Building_Bed bed = p.ownership?.OwnedBed;
                if (bed != null && p.GetRoom() == bed.GetRoom())
                {
                    processed = true;
                    // In their cell: the way in from the shuttle is over (whatever holding they did or did not pass through).
                    if (inTransit) PrisonGame.Instance?.shuttle.Notify_Processed(p);
                }
            }
        }

        /// <summary>An hour of prison work done (the hourly pass, for an inmate who is working): the hour is counted and paid, and what working the job lets slip happens - the tools that go missing, the fruit, the garden's patch.</summary>
        internal void WorkedAnHour(Pawn p, PrisonMap pm)
        {
            hoursWorked++;
            lastWorkDay = Ledger.CurrentDay;
            CommissarySystem.PayWage(this);
            if (job.toolTheft) ContrabandUtility.Notify_WorkedWithTools(p, job.theftKind, job.zone);
            if (job.stealsFood) HoochUtility.Notify_WorkedInKitchen(p, this, pm);
            if (job.growsContraband) GardenPatches.Notify_Gardening(p, this, pm);
        }

        private void Hourly()
        {
            Pawn p = Pawn;
            PrisonMap pm = PrisonMap.Of(p.Map);
            if (pm == null || security == null) return;
            // The home bed held for them is let go here when it is stale (the counts of free beds only read it).
            PunishmentUtility.SyncHomeBed(p, this);
            if (p.ownership != null && forcedActivity == null && !p.Downed)
            {
                Building_Bed bed = p.ownership.OwnedBed;
                SecurityLevelDef wing = IntakeSystem.WingOf(bed);
                if (bed == null) InmateUtility.TryReassignBed(p, this);
                // (Not while an evacuation has them on its roll call: bed owners never change in one, and the way back after the all-clear is to the bed they had.)
                else if (wing != null && !InmateUtility.BedFitsLevel(bed, security) && !EvacuationSystem.HoldsBed(p) && IntakeSystem.FreeBedCount(p.Map, security, inWingOnly: true, gender: p.gender) > 0
                         && Cellmates.LastUsable(p, IntakeSystem.FreePrisonerBeds(p.Map, security, p.gender)) is { } better && IntakeSystem.WingOf(better) == security)
                {
                    // In the wrong wing (security changed, wing repainted): move when a proper bed is free (the move keeps the home bed in step). Never into the cell of someone the warden keeps them apart from: that bed is not a proper one.
                    Cellmates.Move(p, this, better);
                }
            }
            float watchers = TensionUtility.Watchers(p, pm);
            FearSystem.Hourly(p, this, pm, watchers);
            Wants.Hourly(p, this);
            TensionUtility.HourlyUpdate(p, this, pm, watchers);
            if (job != null && LaborUtility.IsWorking(p, this)) WorkedAnHour(p, pm);
            LaundrySystem.Hourly(p, this);
            HungerStrike.Hourly(p, this);
            if (forcedActivity == null && p.Awake() && !p.Downed && !p.InMentalState)
                for (int i = 0; i < reputations.Count; i++)
                    reputations[i].Behavior?.Hourly(p, this, pm);
            if (forcedActivity == null)
            {
                EscapeUtility.HourlyCheck(p, this);
                MisconductUtility.HourlyRoll(p, this, pm);
                GangUtility.TryRecruit(p, this);
            }
            TunnelSystem.Hourly(p, this, PrisonGame.Instance.CurrentActivity(p));
            SupermaxSystem.Hourly(p, this);
            PsychWing.Hourly(p, this);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // (Not on those on their way out: they are not the prison's to command any more.)
            if (Pawn == null || !Pawn.IsPrisonerOfColony || security == null || ShuttleSystem.OnTheWayOut(Pawn)) yield break;
            SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
            if (pc != null && security != pc && DeathRowSystem.Ext(this) == null)
            {
                var toPc = new Command_Action
                {
                    defaultLabel = "RP_ToProtective".Translate(),
                    defaultDesc = "RP_ToProtectiveDesc".Translate(),
                    icon = TexCommand.ForbidOff,
                    action = () => InmateUtility.ChangeSecurity(Pawn, this, pc),
                };
                if (!Unlocks.IsUnlocked(pc) || !InmateUtility.HasRoomIn(Pawn, pc)) toPc.Disable(InmateUtility.NoRoomText(Pawn, pc));
                yield return toPc;
            }
            // Death row is a state sentence: the warden cannot move them elsewhere.
            if (DeathRowSystem.Ext(this) != null) yield break;
            // Supermax, once the clearance is done: to a free cell of the wing.
            if (SupermaxSystem.Level is { } supermax && security != supermax && Unlocks.IsUnlocked(supermax))
            {
                var toSupermax = new Command_Action
                {
                    defaultLabel = "RP_ToSupermax".Translate(),
                    defaultDesc = "RP_ToSupermaxDesc".Translate(),
                    icon = TexCommand.ForbidOn,
                    action = () => SupermaxSystem.TrySend(Pawn, this, SupermaxReason.Warden),
                };
                if (SupermaxSystem.CannotSend(Pawn, this) is { } why) toSupermax.Disable(why);
                yield return toSupermax;
            }
            yield return new Command_Action
            {
                defaultLabel = "RP_TransferOut".Translate(Money.Format(InmateUtility.TransferCost)),
                defaultDesc = "RP_TransferOutDesc".Translate(),
                icon = TexCommand.ClearPrioritizedWork,
                action = () => InmateUtility.TransferOut(Pawn, this),
            };
        }

        /// <summary>
        /// Called when a stay ends (<see cref="ReleaseSystem.Release"/>) and again when the same person is admitted anew
        /// (reoffender return), so nothing of the old stay is carried into the next. Fields are decided one by one:
        /// <list type="bullet">
        /// <item>Reset: the cell stash (destroyed, it stayed behind with the cell), punishment and confinement (owed hours, forced
        /// activity, the restraint), the home bed, the tunnel and what staff suspect of it (suspected, cell search pending, a tip
        /// waiting, who smuggled for them), sanctions (yard, shop and visit bans), a hunger strike, the care refusal, and the
        /// arrangements of the stay (appeal, innocence claim, referral, mentor, remission, informant pay and task, dealer, the restorative meeting), doubts about the gang and an old gang's green light, the library book in the cell and how far it was read, and the watch list (a period, its answer and clocks, the last trigger; the count of crises stays).</item>
        /// <item>Kept on purpose: <see cref="staffMemories"/> (what staff feel about them outlives a stay, a story feature), the
        /// misconduct records, reputations, gang, life and mail history, passed programs, relatives, the answered care decisions about chronic conditions (the condition stays with them; "nothing" and unanswered ones are asked again, see <see cref="OldAge"/>), cash and the psychologist's
        /// file, and the job (re-checked for eligibility by the labor pass).</item>
        /// </list>
        /// </summary>
        public void ResetForNewStay()
        {
            stash.ClearAndDestroyContents();
            books = bookTicks = 0;
            pendingPunishmentHours = 0;
            forcedActivity = null;
            forcedUntilTick = -1;
            homeBed = null;
            if (Pawn?.health?.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained) is { } restraint) Pawn.health.RemoveHediff(restraint);
            tunnelProgress = 0f;
            tunnelKnown = false;
            suspected = false;
            cellSearchPending = false;
            informedBy = null;
            smuggledBy = null;
            yardBanUntilTick = shopBanUntilTick = visitBanUntilTick = lastVisitTick = -1;
            assailantId = assaultTick = -1;
            hungerStrikeUntilTick = -1;
            careRefusedTick = -1;
            // (A letter that was never answered, or answered with nothing, is asked again: the answered history stays.)
            care.RemoveAll(d => d.choice is CareChoice.Pending or CareChoice.Nothing);
            appealResolveTick = -1;
            innocenceClaimed = appealBacked = false;
            referral = null;
            Mentors.Clear(this);
            Remission.Clear(this);
            daysAdded = 0;
            gangDoubtTick = -1;
            exMarked = false;
            exBeatings = 0;
            informantPaid = informantQuit = false;
            taskTargetId = taskSinceTick = -1;
            wardenWordTick = -999999;
            headsUpTick = wardenFirmUntil = -1;
            protectedFrom = null;
            lastDealerId = -1;
            (lastDrugUseTick, lastTestedTick, lastFailedTestTick, lastTestResult, lastTestRandom, testedFor) = (DrugTesting.Never, DrugTesting.Never, DrugTesting.Never, DrugTestResult.None, false, DrugTesting.FreshTestedFor());
            ringBustedTick = RingSystem.NeverBusted;
            supplyCutUntilTick = -1;
            paroleDenial = null;
            paroleDenialTick = -1;
            amendsState = AmendsState.None;
            amendsTick = -1;
            releasePlan = ReleaseSupport.None;
            (riskTick, watchUntilTick, riskEvent, riskEventTick, riskCause, lowMoodTick) = (-1, -1, RiskTrigger.None, -1, RiskTrigger.None, -1);
            (watchAnswer, watchCheckTick, counselTick, riskEndTick, measureTick) = (WatchAnswer.Pending, -1, -1, -1, -1);
        }

        public void RefreshActiveFlag()
        {
            if (Pawn == null) return;
            bool active = InmateUtility.IsActiveInmate(Pawn);
            if (active == activeFlag) return;
            activeFlag = active;
            if (Pawn.Spawned) Pawn.Map.reachability.ClearCacheFor(Pawn);
            // (Released, paroled or transferred: no walk is theirs any more, and the long stay does not walk out with them.)
            if (!active)
            {
                evacDoors = null;
                if (leg != null) EscortWalk.End(Pawn, this);
                SupermaxSystem.Sync(Pawn, this);
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad) => RefreshActiveFlag();

        public override string CompInspectStringExtra()
        {
            if (!Pawn.IsPrisonerOfColony || security == null) return null;
            var sb = new StringBuilder();
            sb.Append("RP_InmateLine".Translate(inmateNumber.ToString("D5"), security.LabelCap, crime?.LabelCap ?? "?"));
            sb.AppendLine();
            sb.Append(DeathRowSystem.Status(this) ?? (IsLifer ? "RP_SentenceLife".Translate(DaysServed.ToString("0.0")) : "RP_SentenceDays".Translate(DaysServed.ToString("0.0"), TermDays)));
            if (gang != null && gangKnown) sb.AppendLine().Append("RP_GangLine".Translate(gang.LabelCap, GangRanks.RankText(this)));
            string reps = ReputationsText();
            if (reps.Length > 0) sb.AppendLine().Append("RP_ReputationLine".Translate(reps));
            sb.AppendLine().Append(TensionUtility.Describe(this));
            if (PrisonGame.Instance != null && activeFlag)
            {
                sb.AppendLine();
                sb.Append("RP_CurrentActivity".Translate(PrisonGame.Instance.CurrentActivity(Pawn).LabelCap));
                if (forcedActivity != null) sb.Append(" (" + "RP_HoursLeft".Translate(((forcedUntilTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("F0")) + ")");
            }
            if (pendingPunishmentHours > 0) sb.AppendLine().Append("RP_PunishmentPending".Translate(pendingPunishmentHours));
            if (job != null) sb.AppendLine().Append("RP_JobLine".Translate(job.LabelCap));
            if (cash > 0) sb.AppendLine().Append("RP_CashLine".Translate(Money.Format(cash)));
            if (suspected) sb.AppendLine().Append("RP_Suspected".Translate());
            return sb.ToString();
        }

        public string ReputationsText()
        {
            var sb = new StringBuilder();
            foreach (ReputationDef r in reputations)
            {
                if (!KnownReputation(r)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(r.label);
            }
            return sb.ToString();
        }
    }

    public static class InmateUtility
    {
        public static SecurityLevelDef DefaultSecurity => RPSecurityDefOf.RP_Medium;

        public static CompInmate Inmate(this Pawn p) => p.GetComp<CompInmate>();

        /// <summary>A prisoner of the player that the regime AI drives (not released, not in a vanilla breakout).</summary>
        public static bool IsActiveInmate(Pawn p)
        {
            if (p == null || !p.Spawned || !p.IsPrisonerOfColony || p.guest.Released) return false;
            if (!p.Map.IsPlayerHome || PrisonBreakUtility.IsPrisonBreaking(p)) return false;
            return p.GetComp<CompInmate>() != null;
        }

        private static readonly HashSet<JobDef> RegimeInterruptible = new HashSet<JobDef>();

        /// <summary>Ends idle/regime jobs so the inmate picks up the new activity right away.</summary>
        public static void InterruptForRegimeChange(Pawn p) => InterruptForRegimeChange(p, emergency: false);

        /// <summary>Emergency (lockdown): also ends classes, visits, work and fixture use.</summary>
        public static void InterruptForRegimeChange(Pawn p, bool emergency)
        {
            if (WouldInterruptForRegimeChange(p, emergency)) p.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>Whether <see cref="InterruptForRegimeChange(Pawn, bool)"/> would end what the pawn is doing now.</summary>
        public static bool WouldInterruptForRegimeChange(Pawn p, bool emergency)
        {
            if (RegimeInterruptible.Count == 0)
            {
                RegimeInterruptible.Add(JobDefOf.GotoWander);
                RegimeInterruptible.Add(JobDefOf.Wait_Wander);
                RegimeInterruptible.Add(JobDefOf.Wait);
                RegimeInterruptible.Add(JobDefOf.Goto);
                RegimeInterruptible.Add(JobDefOf.LayDown);
                RegimeInterruptible.Add(JobDefOf.LayDownAwake);
                RegimeInterruptible.Add(JobDefOf.Wait_MaintainPosture);
            }
            Job job = p.CurJob;
            if (job == null || p.Downed || p.InMentalState) return false;
            if (job.def == JobDefOf.LayDown && HealthAIUtility.ShouldSeekMedicalRest(p)) return false;
            return RegimeInterruptible.Contains(job.def) || job.def.casualInterruptible || (emergency && job.def != JobDefOf.Ingest);
        }

        public static Room CellOf(Pawn p) => p.ownership?.OwnedBed?.GetRoom();

        /// <summary>Whether a bed is a proper home for the level: it is in the level's wing and, for a level that takes only padded cells (psychiatric care), it is a padded bed alone in its room (a plain bed in the wing is no padded cell).</summary>
        public static bool BedFitsLevel(Building_Bed bed, SecurityLevelDef sec) =>
            bed != null && sec != null && IntakeSystem.WingOf(bed) == sec && (!sec.paddedOnly || PaddedCells.IsPadded(bed) && IntakeSystem.SingleCell(bed));

        public const int TransferCost = 1500;

        /// <summary>
        /// Whether the inmate can be housed at <paramref name="sec"/>: their bed already fits (same wing, or an open
        /// bed the level may use), or a fitting bed is free (one of their sex, where the warden keeps a wing for the other).
        /// </summary>
        public static bool HasRoomIn(Pawn p, SecurityLevelDef sec)
        {
            SecurityLevelDef wing = IntakeSystem.WingOf(p.ownership?.OwnedBed);
            if (BedFitsLevel(p.ownership?.OwnedBed, sec) || (wing == null && p.ownership?.OwnedBed != null && !sec.wingOnly)) return true;
            return KeepApart.OrdersOf(p) != null ? IntakeSystem.FreeBedsInOrder(p.Map, sec, p.gender).Exists(b => !KeepApart.RoomHoldsPartner(b, p)) : IntakeSystem.FreeBedCount(p.Map, sec, gender: p.gender) > 0;
        }

        /// <summary>Whether beds at <paramref name="sec"/> are free for them but every one is in the cell of someone the warden keeps them apart from (<see cref="HasRoomIn"/> is false for that reason).</summary>
        public static bool OnlyPartnerBeds(Pawn p, SecurityLevelDef sec) =>
            KeepApart.OrdersOf(p) != null && !HasRoomIn(p, sec) && IntakeSystem.FreeBedCount(p.Map, sec, gender: p.gender) > 0;

        /// <summary>Whether the reason there is no room at <paramref name="sec"/> is the warden's wing policy: that wing is kept for the other sex, and no bed in another wing the level may use is free for them.</summary>
        public static bool WingKeepsThemOut(Pawn p, SecurityLevelDef sec) => !IntakeSystem.WingAllows(sec, p.gender) && !HasRoomIn(p, sec);

        /// <summary>Why there is no room at <paramref name="sec"/> for the warden: the wing kept for the other sex (naming the inmate and the wing) or, else, the plain "no room" line <paramref name="noRoomKey"/> (one argument: the inmate).</summary>
        public static TaggedString NoRoomText(Pawn p, SecurityLevelDef sec, string noRoomKey = "RP_PcNoRoom") =>
            WingKeepsThemOut(p, sec) ? "RP_WingKeptForOther".Translate(p.LabelShort, sec.label, IntakeSystem.WhoWord(sec))
            : OnlyPartnerBeds(p, sec) ? "RP_BedOnlyWithPartner".Translate(p.LabelShort, sec.label) : noRoomKey.Translate(p.LabelShort);

        /// <summary>For a move that was left undone for want of room: if the wing's policy is the reason, the warden is told (a message naming the inmate and the wing). Returns whether it was.</summary>
        public static bool TellIfWingKeepsThemOut(Pawn p, SecurityLevelDef sec)
        {
            if (!WingKeepsThemOut(p, sec)) return false;
            Messages.Message("RP_WingKeptForOther".Translate(p.LabelShort, sec.label, IntakeSystem.WhoWord(sec)), p, MessageTypeDefOf.RejectInput, historical: false);
            return true;
        }

        /// <summary>
        /// Reclassify an inmate (e.g. protective custody); they move to a bed in the new wing when one is free. When the new wing is kept for the other sex and no bed of theirs is free anywhere the level may
        /// use, nothing changes and the warden is told why (false), so nobody is reclassified in their old cell with no way into the wing. <paramref name="onPaper"/> is for a sentence that cannot wait
        /// (a commuted death sentence): they are reclassified all the same, the warden is told they stay where they are, and the hourly pass moves them when a bed of theirs is free.
        /// </summary>
        public static bool ChangeSecurity(Pawn p, CompInmate c, SecurityLevelDef sec, bool onPaper = false, string why = null)
        {
            // (Someone in solitary lives in their home cell: that is the bed that changes, and the one they go back to.)
            Building_Bed bed = Cellmates.HomeBed(p, c);
            SecurityLevelDef wing = IntakeSystem.WingOf(bed);
            List<Building_Bed> free = null;
            if (!BedFitsLevel(bed, sec) && (wing != null || sec.wingOnly))
            {
                free = IntakeSystem.FreePrisonerBeds(p.Map, sec, p.gender);
                // (A bed in the cell of someone the warden keeps them apart from is no bed for the move: when it is all there is, they are told, as for a wing kept for the other sex.)
                if (Cellmates.DropPartnerBeds(p, free) > 0 && free.Count == 0)
                {
                    if (!onPaper)
                    {
                        Messages.Message("RP_BedOnlyWithPartner".Translate(p.LabelShort, sec.label), p, MessageTypeDefOf.RejectInput, historical: false);
                        return false;
                    }
                    Messages.Message("RP_BedOnlyWithPartnerStays".Translate(p.LabelShort, sec.label), p, MessageTypeDefOf.CautionInput, historical: false);
                }
                else if (free.Count == 0 && !IntakeSystem.WingAllows(sec, p.gender))
                {
                    if (!onPaper)
                    {
                        Messages.Message("RP_WingKeptForOther".Translate(p.LabelShort, sec.label, IntakeSystem.WhoWord(sec)), p, MessageTypeDefOf.RejectInput, historical: false);
                        return false;
                    }
                    Messages.Message("RP_WingKeptForOtherStays".Translate(p.LabelShort, sec.label, IntakeSystem.WhoWord(sec)), p, MessageTypeDefOf.CautionInput, historical: false);
                }
            }
            c.security = sec;
            c.lastReclassTick = Find.TickManager.TicksGame;
            // (Supermax is not paroled from: a hearing that was on is off.)
            if (sec.escortOnly) ParoleScene.Notify_NoHearing(p);
            if (free is { Count: > 0 }) Cellmates.Move(p, c, Cellmates.PickBed(free, c));
            InmateGenerator.DressInUniform(p);
            // (Led off by a guard when the level changed under them: the walk is over, the long stay follows the new level.)
            if (c.leg != null) EscortWalk.End(p, c);
            SupermaxSystem.Sync(p, c);
            PsychWing.Sync(c);
            InterruptForRegimeChange(p);
            Messages.Message("RP_SecurityChanged".Translate(p.LabelShort, sec.label), p, MessageTypeDefOf.NeutralEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, why == null ? "RP_Chr_Reclassified".Translate(p.LabelShort, sec.label) : "RP_Chr_ReclassifiedWhy".Translate(p.LabelShort, sec.label, why), p);
            return true;
        }

        /// <summary>Ship a troublemaker to another facility: costs money and reputation, ends the daily payment.</summary>
        /// <param name="cause">Why they are sent (a keyed string), written on the record: the chronicle line and the outbound entry; null for the warden's plain transfer.</param>
        public static void TransferOut(Pawn p, CompInmate c, string cause = null)
        {
            PrisonGame game = PrisonGame.Instance;
            // Already on the way out (released, paroled or transferred): nothing to pay for twice.
            if (ShuttleSystem.OnTheWayOut(p)) return;
            if (!game.ledger.TrySpend(RPDefOf.RP_Fines, TransferCost, "RP_TransferOutNote".Translate(p.LabelShort)))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(TransferCost)), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            game.reputation = UnityEngine.Mathf.Max(0f, game.reputation - 2f);
            // (The wing sees them taken off: the rumour mill.)
            Rumours.Notify_Source(p.MapHeld, c.security, RumourKind.Transfer, p.LabelShort, p);
            // (A custody contract's bonus for them is forfeit; the admission payment stays, and nothing is asked back.)
            CustodyContracts.Notify_Departed(p, "RP_CustodyEnd_Transferred");
            // An open murder case closes with them (and costs the reputation of a case lost, not a quiet way out of one).
            Trials.Notify_TransferredOut(game, p);
            PrisonRelations.Notify_Leaving(p);
            PrisonGame.Instance.story.Notify_Leaving(p);
            game.stats.transferredOut++;
            p.ownership?.UnclaimAll();
            WinterClothing.Collect(p, game);
            Messages.Message("RP_TransferredOut".Translate(p.LabelShort), MessageTypeDefOf.NeutralEvent);
            if (cause != null) Story.Record(RPChronicleDefOf.RP_Chr_Release, 2, "RP_Chr_TransferredCause".Translate(p.LabelShort, cause.Translate()), p);
            // They go out on the state shuttle like the released (to the ramp if it is down, else to reception); somebody on the floor, or on a map
            // with no place for a shuttle, is taken off at once.
            if (p.Spawned && !p.Downed && p.Map is { IsPlayerHome: true } m && game.shuttle.ReceptionAnchor(m).IsValid)
            {
                game.shuttle.Register(p, OutboundKind.Transferred);
                if (game.shuttle.OutboundOf(p) is { } entry) entry.cause = cause;
                return;
            }
            p.DeSpawn();
            Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.Discard);
        }

        /// <summary>An inmate who lost their bed (injury, vanilla reshuffles) gets their old one back or a free one.</summary>
        public static void TryReassignBed(Pawn p, CompInmate c)
        {
            Building_Bed home = c.homeBed;
            if (home != null && home.Spawned && home.OwnersForReading.Count < home.SleepingSlotsCount && !PunishmentUtility.IsSolitaryBed(home))
            {
                p.ownership.ClaimBedIfNonMedical(home);
                c.homeBed = null;
                return;
            }
            // (Counted first: every bedless inmate asks every hour, and an overcrowded prison has none free to build a shuffled list of.)
            if (IntakeSystem.FreeBedCount(p.Map, c.security, gender: p.gender) == 0) return;
            List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(p.Map, c.security, p.gender);
            if (free.Count > 0 && Cellmates.PickBed(free, c) is { } bed)
            {
                p.ownership.ClaimBedIfNonMedical(bed);
                c.homeBed = null; // a new bed: the next punishment brings them back here
            }
        }
    }
}
