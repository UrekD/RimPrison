using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPDrugTestDefOf
    {
        public static ThingDef RP_DrugTestKit;
        public static JobDef RP_DrugTest, RP_DrugTestWait;
        public static MisconductDef RP_DrugUse, RP_RefusedTest;

        static RPDrugTestDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDrugTestDefOf));
    }

    /// <summary>The warden's drug-testing policy (Policy tab): nobody (the default, so an old save is unchanged), those with a reason, or those and a few picked at random each day.</summary>
    public enum DrugTestPolicy : byte
    {
        Off,
        Targeted,
        TargetedAndRandom,
    }

    /// <summary>How an inmate's last test came out (the values are saved: new ones go last).</summary>
    public enum DrugTestResult : byte
    {
        None,
        Negative,
        Positive,
        Refused,
        /// <summary>An addiction and no drug used in the last two days: treated, not punished.</summary>
        Dependence,
    }

    /// <summary>A doctor's round that has been taken on: who tests whom, why, and whether the inmate has been taken aside yet. Saved with the day's log (a round goes on across a load), and the prison's cap counts the ones not begun.</summary>
    public class DrugTestClaim : IExposable
    {
        public Pawn doctor, inmate;
        public int reasons;
        public bool begun;

        public void ExposeData()
        {
            Scribe_References.Look(ref doctor, "doctor");
            Scribe_References.Look(ref inmate, "inmate");
            Scribe_Values.Look(ref reasons, "reasons");
            Scribe_Values.Look(ref begun, "begun");
        }

        public string Describe() => $"{doctor?.thingIDNumber}>{inmate?.thingIDNumber}:{reasons}:{begun}";
    }

    /// <summary>What the doctors' testing round did today and yesterday (the Policy row reads it; the day rolls over when the hour pass or a test next looks at it), the hour's count of tests begun and the rounds under way.</summary>
    public class DrugTestLog : IExposable
    {
        public int day = -1, tests, positives, randoms, yesterdayTests, yesterdayPositives;
        /// <summary>The game hour <see cref="started"/> counts the tests begun in (the cap of <see cref="DrugTesting.MaxPerHour"/> is the prison's, and a load does not reset it).</summary>
        public int hourKey = -1, started;
        public List<DrugTestClaim> claims = new List<DrugTestClaim>();

        /// <summary>Starts the day <paramref name="today"/>: what was counted becomes yesterday's (only if it was the day before).</summary>
        public void Roll(int today)
        {
            if (day == today) return;
            (yesterdayTests, yesterdayPositives) = day >= 0 && today == day + 1 ? (tests, positives) : (0, 0);
            (day, tests, positives, randoms) = (today, 0, 0, 0);
        }

        public int TestsYesterday(int today) => today == day ? yesterdayTests : today == day + 1 ? tests : 0;

        public int PositivesYesterday(int today) => today == day ? yesterdayPositives : today == day + 1 ? positives : 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref day, "day", -1);
            Scribe_Values.Look(ref tests, "tests");
            Scribe_Values.Look(ref positives, "positives");
            Scribe_Values.Look(ref randoms, "randoms");
            Scribe_Values.Look(ref yesterdayTests, "yesterdayTests");
            Scribe_Values.Look(ref yesterdayPositives, "yesterdayPositives");
            Scribe_Values.Look(ref hourKey, "hourKey", -1);
            Scribe_Values.Look(ref started, "started");
            Scribe_Collections.Look(ref claims, "claims", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                claims ??= new List<DrugTestClaim>();
                claims.RemoveAll(c => c == null || c.doctor == null || c.inmate == null);
            }
        }

        public string Describe() => $"{day}:{tests}:{positives}:{randoms}:{yesterdayTests}:{yesterdayPositives}:{hourKey}:{started}:[{string.Join(",", claims.Select(c => c.Describe()))}]";
    }

    /// <summary>
    /// Drug testing (gap analysis 6, item 4). The warden's policy (<see cref="PrisonGame.drugTests"/>) says who is tested: with a reason (a contraband find, a tip, an overdose survived, the two days before the inmate's next
    /// parole hearing or trial: each reason tests an inmate at most once in two days, a hearing or a trial once), and, in the third mode, a few picked at random each day. Once an hour the due list is built
    /// (<see cref="Hourly"/>); a doctor on duty (the day shift's work hours by the timetable, never the night doctor, never the only doctor on site, at most two tests an hour in the prison, the ones taken on counted) takes a
    /// kit and an inmate from it and takes them aside (<see cref="WorkGiver_DrugTest"/>). A test is positive when they used a drug in the last two days (<see cref="CompInmate.lastDrugUseTick"/>, set when they take one,
    /// wherever it came from): a seen drug use on the record, a referral to substance abuse treatment, lower parole odds, and on the dealer's customer the ring's own trace; or, with no use lately but an addiction, a positive
    /// for dependence: the referral and the parole odds, no misconduct and no punishment (use is punished, dependence is treated). A refusal is a seen misconduct of its own and counts like a positive for the parole board.
    /// No kit, no test. A riot or a hostage crisis stops the round at once.
    /// </summary>
    public static class DrugTesting
    {
        /// <summary>The tick of "never" in the file (a long time ago, as the other last-times).</summary>
        public const int Never = -999999;
        public const int DefaultPerDay = 2, MinPerDay = 1, MaxPerDay = 6;
        /// <summary>Why they are due: one bit each (the first four are the targeted reasons, kept per inmate in <see cref="CompInmate.testedFor"/>); the random pick has no memory of its own.</summary>
        public const int ReasonFind = 1, ReasonTip = 2, ReasonOverdose = 4, ReasonHearing = 8, ReasonRandom = 16;
        public const int TargetedReasons = 4;
        /// <summary>A reason tests an inmate at most this often; a find or an overdose counts as one for this long.</summary>
        public const int ReasonGapTicks = 2 * GenDate.TicksPerDay;
        /// <summary>A hearing or a trial this near (or past, and not yet held) is a reason, once for each.</summary>
        public const int HearingLeadTicks = 2 * GenDate.TicksPerDay;
        /// <summary>A drug used this recently makes a positive.</summary>
        public const int UseWindowTicks = 2 * GenDate.TicksPerDay;
        /// <summary>A random pick is not picked again this soon after any test.</summary>
        public const int RandomGapTicks = GenDate.TicksPerDay;
        /// <summary>Random tests are picked in the day shift's hours only.</summary>
        public const int RoundFromHour = 6, RoundToHour = 20;
        /// <summary>Taking an inmate aside: a few hundred ticks.</summary>
        public const int TestTicks = 300;
        /// <summary>Tests that may start in one hour in the whole prison.</summary>
        public const int MaxPerHour = 2;
        /// <summary>A positive or a refusal counts against them with the parole board for this long, by this much.</summary>
        public const int ParoleWindowTicks = 5 * GenDate.TicksPerDay;
        public const float ParolePenalty = 0.1f;
        /// <summary>Being tested at random is humiliating: a small tension cause for these hours after (on the inmate tested, not the wing).</summary>
        public const float SearchedTension = 0.5f;
        public const int SearchedHours = 12;
        /// <summary>The refusal chance: this, more with a drug used lately, with tension, less when they are beaten down.</summary>
        public const float RefuseBase = 0.04f, RefuseUsed = 0.25f, RefuseTension = 0.3f, RefuseSuppressed = 0.1f;
        /// <summary>Kits kept in stock: a few spare, and these days of the expected tests.</summary>
        public const int StockSpare = 6, StockDays = 2;
        /// <summary>The kits one test uses up.</summary>
        public const int KitsPerTest = 1;
        /// <summary>How many doctors may have part of one stack of kits reserved (a reservation names how many pawns may share a thing).</summary>
        public const int KitReservers = 10;

        // ---- the hour's due list -------------------------------------------------------------------------------------------------------------------

        private static PrisonGame builtFor;
        private static readonly List<Thing> dueList = new List<Thing>();
        private static readonly Dictionary<Pawn, int> dueMask = new Dictionary<Pawn, int>();
        private static readonly List<Pawn> randomPool = new List<Pawn>();
        private static int doctorsTick = -1, doctorsMapId = -1, doctorsCount;

        public static ThingDef Kit => RPDrugTestDefOf.RP_DrugTestKit;

        /// <summary>The rounds under way and the hour's count: saved, not kept in memory (see <see cref="DrugTestLog"/>).</summary>
        private static DrugTestLog Log => PrisonGame.Instance.drugTestLog;

        public static int[] FreshTestedFor()
        {
            int[] a = new int[TargetedReasons];
            for (int i = 0; i < a.Length; i++) a[i] = Never;
            return a;
        }

        /// <summary>A list built for another game (a load) is not this one's: the due list is not saved, the next hour builds it again (what is saved is in <see cref="DrugTestLog"/>).</summary>
        private static void Sync(PrisonGame game)
        {
            if (builtFor == game) return;
            builtFor = game;
            dueList.Clear();
            dueMask.Clear();
            doctorsTick = -1;
        }

        /// <summary>The inmates due a test as the hour pass found them (not a copy: the work giver reads it as the doctor's list).</summary>
        public static List<Thing> DueThings()
        {
            Sync(PrisonGame.Instance);
            return dueList;
        }

        public static bool IsDue(Pawn p, out int mask)
        {
            Sync(PrisonGame.Instance);
            return dueMask.TryGetValue(p, out mask);
        }

        public static int DueCount => DueThings().Count;

        /// <summary>For a check: the list is put to exactly these inmates, with these reasons.</summary>
        internal static void SetDueForTests(PrisonGame game, params (Pawn p, int mask)[] list)
        {
            Sync(game);
            dueList.Clear();
            dueMask.Clear();
            foreach ((Pawn p, int mask) in list)
            {
                dueList.Add(p);
                dueMask[p] = mask;
            }
        }

        /// <summary>What the static state held (the due list, and the doctors and the hour a check decided): taken before a check, put back by <see cref="RestoreForTests"/>.</summary>
        internal sealed class StateForTests
        {
            public PrisonGame builtFor;
            public List<Thing> due;
            public Dictionary<Pawn, int> mask;
            public int? doctors, hour;
            public bool? refuse;
            public int? hourKey;
        }

        internal static StateForTests SnapshotForTests() => new StateForTests { builtFor = builtFor, due = dueList.ToList(), mask = new Dictionary<Pawn, int>(dueMask), doctors = DoctorsForTests, hour = HourForTests, refuse = RefuseForTests, hourKey = HourKeyForTests };

        internal static void RestoreForTests(StateForTests s)
        {
            builtFor = s.builtFor;
            dueList.Clear();
            dueList.AddRange(s.due);
            dueMask.Clear();
            foreach (KeyValuePair<Pawn, int> kv in s.mask) dueMask[kv.Key] = kv.Value;
            (DoctorsForTests, HourForTests, RefuseForTests, HourKeyForTests) = (s.doctors, s.hour, s.refuse, s.hourKey);
            doctorsTick = -1;
        }

        /// <summary>For a check: the list is emptied and what the check decided (doctors, hour) is let go.</summary>
        internal static void ClearForTests()
        {
            dueList.Clear();
            dueMask.Clear();
            doctorsTick = -1;
            (DoctorsForTests, HourForTests, RefuseForTests, HourKeyForTests) = (null, null, null, null);
        }

        /// <summary>For a check: what a load does to the static state (the due list and what was counted are lost, nothing saved is touched): the round and the cap must not depend on it.</summary>
        internal static void LoseStaticsForTests()
        {
            builtFor = null;
            dueList.Clear();
            dueMask.Clear();
            doctorsTick = -1;
        }

        /// <summary>Hourly: the due list again, from one pass over the inmates (a list of those with a reason, and the pool the random picks come from).</summary>
        public static void Hourly(Map map, PrisonGame game)
        {
            Sync(game);
            dueList.Clear();
            dueMask.Clear();
            int now = Find.TickManager.TicksGame;
            game.drugTestLog.Roll(Ledger.CurrentDay);
            PruneClaims(game.drugTestLog);
            if (game.drugTests == DrugTestPolicy.Off || map == null || CrisisOn(map)) return;
            bool random = game.drugTests == DrugTestPolicy.TargetedAndRandom;
            randomPool.Clear();
            bool riot = false;
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                if (p.Inmate() is not { activeFlag: true } c) continue;
                if (p.MentalStateDef == RPMentalStateDefOf.RP_Rioting) riot = true;
                int mask = DueReasons(game, p, c, now);
                if (mask != 0)
                {
                    dueList.Add(p);
                    dueMask[p] = mask;
                }
                else if (random && now - c.lastTestedTick >= RandomGapTicks && !Claimed(p) && Takeable(p, c)) randomPool.Add(p);
            }
            // (A riot is on: nobody is taken aside, and nobody picked for it waits: the next hour looks again.)
            if (riot)
            {
                dueList.Clear();
                dueMask.Clear();
                randomPool.Clear();
                return;
            }
            if (random) PickRandom(map, game);
            randomPool.Clear();
        }

        /// <summary>The day's random tests still to do (not counting the ones a doctor has taken on), spread over the hours of the round that are left.</summary>
        private static void PickRandom(Map map, PrisonGame game)
        {
            int hour = Hour(map);
            if (hour < RoundFromHour || hour >= RoundToHour) return;
            int need = game.drugTestsPerDay - game.drugTestLog.randoms - game.drugTestLog.claims.Count(c => (c.reasons & ReasonRandom) != 0);
            if (need <= 0 || randomPool.Count == 0) return;
            float share = need / (float)Mathf.Max(1, RoundToHour - hour);
            int picks = Mathf.FloorToInt(share) + (Rand.Chance(share - Mathf.Floor(share)) ? 1 : 0);
            for (int k = 0; k < picks && randomPool.Count > 0; k++)
            {
                int at = Rand.Range(0, randomPool.Count);
                Pawn p = randomPool[at];
                randomPool.RemoveAt(at);
                dueList.Add(p);
                dueMask[p] = ReasonRandom;
            }
        }

        // ---- who is due, and who can be taken --------------------------------------------------------------------------------------------------

        /// <summary>The targeted reasons that apply to them now, whether or not they were tested for them lately (see <see cref="DueReasons"/>).</summary>
        public static int ActiveReasons(PrisonGame game, Pawn p, CompInmate c, int now)
        {
            int mask = 0;
            // A find on them or in their cell is a seen contraband record (a pat-down, a cell search, a garden patch pulled up, the mail).
            for (int i = c.records.Count - 1; i >= 0; i--)
            {
                MisconductRecord r = c.records[i];
                if (now - r.tick >= ReasonGapTicks) break;
                if (r.seen && r.def == RPContrabandDefOf.RP_ContrabandPossession)
                {
                    mask |= ReasonFind;
                    break;
                }
            }
            // A tip names them until a search acts on it.
            if (c.informedBy != null) mask |= ReasonTip;
            if (now - c.lastOverdoseTick < ReasonGapTicks) mask |= ReasonOverdose;
            if (NextHearingTick(game, p, c, now) is var next && next != Never && next - now <= HearingLeadTicks) mask |= ReasonHearing;
            return mask;
        }

        /// <summary>The targeted reasons they are due a test for: active, and not tested for that reason in the last two days (a hearing or a trial: not tested in the two days before it).</summary>
        public static int DueReasons(PrisonGame game, Pawn p, CompInmate c, int now)
        {
            int active = ActiveReasons(game, p, c, now), due = 0;
            for (int i = 0; i < TargetedReasons; i++)
            {
                if ((active & (1 << i)) == 0) continue;
                int since = 1 << i == ReasonHearing ? NextHearingTick(game, p, c, now) - HearingLeadTicks - 1 : now - ReasonGapTicks;
                if (c.testedFor[i] <= since) due |= 1 << i;
            }
            return due;
        }

        /// <summary>
        /// When their next parole hearing or their trial is (<see cref="Never"/>: neither). A trial is the accused's, on the day the court has set (<see cref="MurderTrial.trialTick"/>). A hearing is due from half the
        /// sentence and then every few days (<see cref="ParoleSystem.HearingGapDays"/>): the later of the two. A hearing that is due and not yet held is "now or past" until it is.
        /// </summary>
        public static int NextHearingTick(PrisonGame game, Pawn p, CompInmate c, int now)
        {
            if (Trials.TrialOf(game, p) is { } trial) return trial.trialTick;
            if (c.IsLifer || !ParoleSystem.MayBeParoled(c) || DeathRowSystem.Ext(c) != null) return Never;
            return Mathf.Max(now + Mathf.RoundToInt((c.TermDays * 0.5f - c.DaysServed) * GenDate.TicksPerDay), c.lastHearingTick + ParoleSystem.HearingGapDays * GenDate.TicksPerDay);
        }

        /// <summary>
        /// Nothing in the way of taking them aside now: on their feet and awake, not on the way out, not in the hole, supermax, a hostage situation, a riot or a mental break, not in a class or at an appointment
        /// (a hearing, a visit, the court). The test waits for the next hour.
        /// </summary>
        public static bool Takeable(Pawn p, CompInmate c)
        {
            if (!p.Spawned || c == null || !c.activeFlag || p.Downed || p.InMentalState || !p.Awake() || ShuttleSystem.OnTheWayOut(p)) return false;
            if (HostageCrisis.IsHeld(p) || HostageCrisis.Holding(p) || SupermaxSystem.IsIn(c) || c.security is { escortOnly: true }) return false;
            if (PunishmentUtility.IsSolitaryBed(p.ownership?.OwnedBed) || PunishmentUtility.IsSolitaryBed(p.CurrentBed())) return false;
            if (WorkGiver_Frisk.AtAppointment(p) || c.visitor != null || Trials.TrialOf(PrisonGame.Instance, p) is { courtLock: true }) return false;
            return true;
        }

        /// <summary>On the due list this hour, free to be taken, and no doctor has them yet.</summary>
        public static bool Candidate(Pawn p)
        {
            Sync(PrisonGame.Instance);
            return PrisonGame.Instance.drugTests != DrugTestPolicy.Off && dueMask.ContainsKey(p) && !Claimed(p) && Takeable(p, p.Inmate());
        }

        // ---- the doctors, the kits and the cap -------------------------------------------------------------------------------------------------

        /// <summary>For a check: the count of doctors on site, decided (null: counted).</summary>
        internal static int? DoctorsForTests;

        /// <summary>For a check: the hour of the day, decided (null: the map's). The random picks are spread over it and a doctor's duty is read at it.</summary>
        internal static int? HourForTests;

        /// <summary>For a check: how the refusal roll falls, decided (null: the dice).</summary>
        internal static bool? RefuseForTests;

        /// <summary>For a check: the game hour the cap counts in, held (null: the clock's), so that a check that runs real ticks is not caught by the hour turning.</summary>
        internal static int? HourKeyForTests;

        public static int Hour(Map map) => HourForTests ?? GenLocalDate.HourOfDay(map);

        /// <summary>
        /// On duty now, by the timetable and not only the shift flag: a doctor of the day shift in one of their own work hours (not the commute hour before the shift, a break, the hours of sleep), and not due home
        /// (leave, a suspension). The night doctor is never on duty for this.
        /// </summary>
        public static bool OnDuty(Pawn p)
        {
            if (p.Map == null || p.GetComp<CompStaff>() is not { } sc || sc.nightShift) return false;
            int hour = Hour(p.Map);
            return p.timetable?.GetAssignment(hour) == TimeAssignmentDefOf.Work && !StaffCommute.ShouldBeHome(p, sc, hour);
        }

        /// <summary>A doctor who may take a test: on duty (the day shift's work hours), on the staff, not down, not on leave.</summary>
        public static bool IsTestDoctor(Pawn p) =>
            p.Spawned && !p.Downed && !p.Dead && WorkGiver_MedicationRound.IsRoundDoctor(p) && StaffCareers.Available(p.GetComp<CompStaff>()) && OnDuty(p);

        /// <summary>Doctors on site and able (the day doctor and the night doctor alike): a test is never taken by the only one. Counted once a tick for a map.</summary>
        public static int DoctorsOnSite(Map map)
        {
            if (DoctorsForTests is { } forced) return forced;
            int now = Find.TickManager.TicksGame;
            if (doctorsTick == now && doctorsMapId == map.uniqueID) return doctorsCount;
            (doctorsTick, doctorsMapId, doctorsCount) = (now, map.uniqueID, 0);
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (!colonists[i].Downed && !colonists[i].Dead && WorkGiver_MedicationRound.IsRoundDoctor(colonists[i]) && StaffCareers.Available(colonists[i].GetComp<CompStaff>())) doctorsCount++;
            return doctorsCount;
        }

        /// <summary>The doctor may take a test: on duty, and one of at least two on site.</summary>
        public static bool MayTest(Pawn doctor) => IsTestDoctor(doctor) && DoctorsOnSite(doctor.Map) >= 2;

        /// <summary>
        /// A riot or a hostage crisis on the map stops the round at once: the work giver does not offer a test and a test under way ends. Read from the rioters' own ticks (<see cref="RiotPolice.RiotActive"/>) and the
        /// running crisis: no scan of the inmates.
        /// </summary>
        public static bool CrisisOn(Map map) => map != null && (RiotPolice.RiotActive(map) || HostageCrisis.Running(map) != null);

        public static bool PolicyOff => PrisonGame.Instance == null || PrisonGame.Instance.drugTests == DrugTestPolicy.Off;

        /// <summary>The round cannot go on: the policy is off, a crisis is on, the inmate cannot be taken or the doctor may not take a test (off duty, the only doctor).</summary>
        public static bool Stopped(Pawn doctor, Pawn inmate) => PolicyOff || CrisisOn(doctor.Map) || !Takeable(inmate, inmate.Inmate()) || !MayTest(doctor);

        public static int KitCount(Map map) => Kit == null ? 0 : SupplySystem.CountOnMap(map, Kit);

        private static int CurrentHourKey => HourKeyForTests ?? Find.TickManager.TicksGame / GenDate.TicksPerHour;

        /// <summary>Tests that have begun in this hour (a doctor taking an inmate aside counts from the moment they do).</summary>
        public static int StartedThisHour
        {
            get
            {
                DrugTestLog log = Log;
                if (log.hourKey != CurrentHourKey) (log.hourKey, log.started) = (CurrentHourKey, 0);
                return log.started;
            }
        }

        /// <summary>The hour's places taken: the tests begun, and the rounds a doctor has taken on and not yet begun (another claim than <paramref name="mine"/>).</summary>
        private static int SlotsTaken(DrugTestClaim mine = null)
        {
            int taken = StartedThisHour;
            foreach (DrugTestClaim c in Log.claims)
                if (!c.begun && c != mine) taken++;
            return taken;
        }

        public static bool HourFull => SlotsTaken() >= MaxPerHour;

        public static DrugTestClaim ClaimOf(Pawn inmate)
        {
            foreach (DrugTestClaim c in Log.claims)
                if (c.inmate == inmate) return c;
            return null;
        }

        public static bool Claimed(Pawn inmate) => ClaimOf(inmate) != null;

        /// <summary>The doctor's round of this inmate has been taken on (the reasons it carries are what they are due for now).</summary>
        public static bool RoundOf(Pawn doctor, Pawn inmate) => doctor?.CurJob is { } j && j.def == RPDrugTestDefOf.RP_DrugTest && j.targetA.Thing == inmate;

        /// <summary>A round whose doctor is no longer on it (the job ended without its finish action: the doctor died or left the map) is let go.</summary>
        private static void PruneClaims(DrugTestLog log) => log.claims.RemoveAll(c => c.doctor is not { Spawned: true, Dead: false } d || !RoundOf(d, c.inmate));

        /// <summary>The doctor takes this inmate on: null when the policy is off, a crisis is on, the hour's two are taken, somebody has them already, or they are due for nothing now.</summary>
        public static DrugTestClaim Claim(Pawn doctor, Pawn p)
        {
            Sync(PrisonGame.Instance);
            if (PolicyOff || CrisisOn(p.Map) || SlotsTaken() >= MaxPerHour || Claimed(p)) return null;
            // (No reason, no test: not due, or no longer due when a round that was put aside comes round again.)
            if (!dueMask.TryGetValue(p, out int mask) || mask == 0) return null;
            var claim = new DrugTestClaim { doctor = doctor, inmate = p, reasons = mask };
            Log.claims.Add(claim);
            return claim;
        }

        /// <summary>The doctor takes this inmate aside now: false when the hour's two are done or the inmate is another doctor's. Counts the start (and takes the inmate on, if the doctor had not).</summary>
        public static bool TryBegin(Pawn doctor, Pawn p)
        {
            Sync(PrisonGame.Instance);
            if (PolicyOff || StartedThisHour >= MaxPerHour) return false;
            DrugTestClaim claim = ClaimOf(p) ?? Claim(doctor, p);
            if (claim == null || claim.doctor != doctor || claim.begun) return false;
            claim.begun = true;
            Log.started++;
            return true;
        }

        /// <summary>The doctor's round is over, whichever way: the inmate is let go.</summary>
        public static void Release(Pawn doctor) => Log.claims.RemoveAll(c => c.doctor == doctor);

        /// <summary>This wait is the inmate's, and belongs to this doctor's round (<see cref="JobDriver_DrugTestWait"/>).</summary>
        public static bool OwnsWait(Pawn doctor, Pawn inmate) => inmate?.CurJob is { } j && j.def == RPDrugTestDefOf.RP_DrugTestWait && j.targetA.Thing == doctor;

        /// <summary>Ends the inmate's wait if it is the round's own: whatever else they have begun to do is theirs and is left alone.</summary>
        public static void EndWait(Pawn doctor, Pawn inmate)
        {
            if (inmate is { Spawned: true } && OwnsWait(doctor, inmate)) inmate.jobs.EndCurrentJob(JobCondition.Succeeded);
        }

        /// <summary>Why the round cannot run now (a keyed phrase for the Policy row), or null.</summary>
        public static string Blocker(Map map)
        {
            if (Kit == null || map == null) return null;
            if (CrisisOn(map)) return "RP_DrugTestCrisis";
            if (KitCount(map) == 0) return "RP_DrugTestNoKits";
            if (DoctorsOnSite(map) < 2) return "RP_DrugTestNoDoctor";
            return null;
        }

        // ---- the test ---------------------------------------------------------------------------------------------------------------------------

        public static bool UsedLately(CompInmate c, int now) => now - c.lastDrugUseTick < UseWindowTicks;

        /// <summary>Positive on a drug used in the last two days; with none, an addiction is a positive for dependence; else negative.</summary>
        public static DrugTestResult Classify(Pawn p, CompInmate c, int now) =>
            UsedLately(c, now) ? DrugTestResult.Positive : ReputationBehavior_Dealer.HasAddiction(p) ? DrugTestResult.Dependence : DrugTestResult.Negative;

        /// <summary>The chance they refuse: more with a drug used lately and with tension, less when they are beaten down.</summary>
        public static float RefuseChance(Pawn p, CompInmate c, int now) =>
            Mathf.Clamp(RefuseBase + (UsedLately(c, now) ? RefuseUsed : 0f) + Mathf.Max(0f, c.tension - 40f) / 60f * RefuseTension - c.suppressed / 100f * RefuseSuppressed, 0.02f, 0.7f);

        /// <summary>
        /// The doctor reads the test: a refusal (the kit is not used), or a negative, a positive or a dependence. Everything that follows is here: the file, the record, the referral, the ring's trace, the counts.
        /// <paramref name="refuse"/>: for a check, the roll decided (null: as it falls). <paramref name="reasons"/>: what they were tested for, as the doctor took them on (null: what they are due for now).
        /// </summary>
        public static DrugTestResult Conduct(Pawn doctor, Pawn p, bool? refuse = null, int? reasons = null)
        {
            PrisonGame game = PrisonGame.Instance;
            Sync(game);
            CompInmate c = p.Inmate();
            if (c == null) return DrugTestResult.None;
            int now = Find.TickManager.TicksGame;
            dueMask.TryGetValue(p, out int dueNow);
            int mask = reasons ?? (dueNow & ReasonRandom) | DueReasons(game, p, c, now);
            bool random = (mask & ReasonRandom) != 0;
            Map map = p.MapHeld ?? doctor?.MapHeld;
            PrisonMap pm = PrisonMap.Of(map);
            game.drugTestLog.Roll(Ledger.CurrentDay);
            DrugTestResult result = refuse ?? RefuseForTests ?? Rand.Chance(RefuseChance(p, c, now)) ? DrugTestResult.Refused : Classify(p, c, now);
            c.lastTestedTick = now;
            c.lastTestResult = result;
            c.lastTestRandom = random;
            if (result is DrugTestResult.Refused or DrugTestResult.Positive or DrugTestResult.Dependence) c.lastFailedTestTick = now;
            for (int i = 0; i < TargetedReasons; i++)
                if ((mask & (1 << i)) != 0) c.testedFor[i] = now;
            if (random) game.drugTestLog.randoms++;
            dueMask.Remove(p);
            dueList.Remove(p);
            doctor?.skills?.Learn(SkillDefOf.Medicine, 80f);
            if (result == DrugTestResult.Refused)
            {
                MisconductUtility.Record(p, c, pm, RPDrugTestDefOf.RP_RefusedTest, evidence: true, chronicle: false);
                Messages.Message("RP_DrugTestRefused".Translate(p.LabelShort), p, MessageTypeDefOf.NegativeEvent, historical: false);
                return result;
            }
            game.stats.drugTests++;
            game.drugTestLog.tests++;
            if (result == DrugTestResult.Negative) return result;
            game.stats.drugPositives++;
            game.drugTestLog.positives++;
            Refer(p, c);
            if (result == DrugTestResult.Dependence)
            {
                // (No drug used lately: nothing to punish, and no find on a dealer's customer. The addiction is treated.)
                Messages.Message("RP_DrugTestDependence".Translate(p.LabelShort), p, MessageTypeDefOf.NegativeEvent, historical: false);
                return result;
            }
            MisconductUtility.Record(p, c, pm, RPDrugTestDefOf.RP_DrugUse, evidence: true, chronicle: false);
            // (A find on someone the dealer sold to may trace the dealer, by the ring's own rule and line. Not on a member of a ring: that is a find on the ring, and a test must not heat it.)
            if (c.lastDealerId >= 0 && RingSystem.LinkOf(p, out _) == null) RingSystem.Notify_Found(p, c, null, traced: false);
            Messages.Message("RP_DrugTestPositive".Translate(p.LabelShort), p, MessageTypeDefOf.NegativeEvent, historical: false);
            return result;
        }

        /// <summary>A referral to substance abuse treatment, if there is such a program, they have not passed it and have no referral already (as the overdose letter's help does).</summary>
        public static bool Refer(Pawn p, CompInmate c)
        {
            ProgramDef rehab = RPProgramDefOf.RP_SubstanceAbuse;
            if (rehab == null || c.referral != null || c.passedPrograms.Contains(rehab)) return false;
            c.referral = rehab;
            if (!PrisonGame.Instance.programs.IsRunning(rehab)) Messages.Message("RP_ReferredNotRunning".Translate(p.LabelShort, rehab.label), p, MessageTypeDefOf.CautionInput, historical: false);
            return true;
        }

        // ---- what the rest of the prison reads ------------------------------------------------------------------------------------------------

        /// <summary>They tested positive (or for dependence) or refused in the last five days, whatever came after: the parole board counts it against them.</summary>
        public static bool FailedRecently(CompInmate c) => Find.TickManager.TicksGame - c.lastFailedTestTick < ParoleWindowTicks;

        /// <summary>Tested at random lately: a small tension cause (they were searched again).</summary>
        public static bool SearchedAgain(CompInmate c) =>
            c.lastTestRandom && c.lastTestResult != DrugTestResult.None && Find.TickManager.TicksGame - c.lastTestedTick < SearchedHours * GenDate.TicksPerHour;

        /// <summary>The file's line: when they were last tested and how it came out, with the addictions a positive found; null if they never were.</summary>
        public static string FileLine(Pawn p, CompInmate c, int now)
        {
            if (c.lastTestResult == DrugTestResult.None) return null;
            string line = "RP_FileDrugTest".Translate(("RP_DrugTestResult_" + c.lastTestResult).Translate(), ((now - c.lastTestedTick) / (float)GenDate.TicksPerDay).ToString("0.0"));
            if (c.lastTestResult is not (DrugTestResult.Positive or DrugTestResult.Dependence) || p.health?.hediffSet == null) return line;
            var names = new List<string>();
            foreach (Hediff h in p.health.hediffSet.hediffs)
                if (h is Hediff_Addiction) names.Add(h.def.label);
            return names.Count > 0 ? line + " " + "RP_FileDrugTestAddicted".Translate(string.Join(", ", names)) : line;
        }

        // ---- stock ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The tests expected in a day, for the stock: the random number, yesterday's count, at least two.</summary>
        private static int ExpectedPerDay(PrisonGame game) =>
            Mathf.Max(2, game.drugTests == DrugTestPolicy.TargetedAndRandom ? game.drugTestsPerDay : 0, game.drugTestLog.TestsYesterday(Ledger.CurrentDay));

        /// <summary>The kits are kept in stock as the antipsychotic is (<see cref="PsychWing.Daily"/>): ordered from the supply company when they run low. Returns whether an order was placed.</summary>
        public static bool EnsureStock(Map map, PrisonGame game)
        {
            if (Kit == null || game.drugTests == DrugTestPolicy.Off) return false;
            int have = SupplySystem.CountOnMap(map, Kit), pending = game.supply.PendingCount(Kit), want = StockSpare + StockDays * ExpectedPerDay(game);
            if (have + pending >= want) return false;
            return game.supply.TryOrder(Kit, Mathf.CeilToInt((want - have - pending) / 10f) * 10, RPDefOf.RP_Supplies, silent: true);
        }

        /// <summary>Midnight: the day rolls over, the kits are kept in stock, and a prison testing with none is told so.</summary>
        public static void Daily(Map map, PrisonGame game)
        {
            game.drugTestLog.Roll(Ledger.CurrentDay);
            if (game.drugTests == DrugTestPolicy.Off || Kit == null) return;
            EnsureStock(map, game);
            if (KitCount(map) == 0) Messages.Message("RP_DrugTestNoKits".Translate(), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <summary>For the snapshot of a save/load round trip: the policy, the day's counts, the hour's cap and the rounds under way.</summary>
        public static string Describe(PrisonGame game) => $"{game.drugTests}:{game.drugTestsPerDay}:{game.drugTestLog.Describe()}";

        public static string Describe(CompInmate c) => $"{c.lastDrugUseTick}:{c.lastTestedTick}:{c.lastFailedTestTick}:{c.lastTestResult}:{c.lastTestRandom}:{string.Join("+", c.testedFor)}";

        /// <summary>A drug that makes an inmate's test positive when they take it: any drug but medicine (a doctor's pill is no use of drugs).</summary>
        public static bool IsTestedDrug(ThingDef def) => def?.ingestible != null && def.IsDrug && def.ingestible.drugCategory != DrugCategory.Medical;
    }

    /// <summary>
    /// Wherever an inmate takes a drug, the last-use tick is set: the Ingest job of <see cref="ContrabandUtility.TryUseDrugs"/> is the one way they take one, and what it takes came from a visitor's gift, a stash, hooch brewed
    /// in the kitchen, a garden patch's joints or a dealer's sale (those only put the drug in a pocket: nobody takes it there).
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
    public static class Patch_Thing_Ingested_DrugUse
    {
        public static void Postfix(Thing __instance, Pawn ingester)
        {
            if (DrugTesting.IsTestedDrug(__instance.def) && ingester?.Inmate() is { } c) c.lastDrugUseTick = Find.TickManager.TicksGame;
        }
    }

    // ---- the doctor's round ----------------------------------------------------------------------------------------------------------------------------

    public class WorkGiver_DrugTest : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => DrugTesting.DueThings();

        /// <summary>No round with the policy off or a riot or a hostage crisis on, nobody due, no kit or a doctor who may not take one (off duty, the only one on site).</summary>
        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            DrugTesting.PolicyOff || DrugTesting.CrisisOn(pawn.Map) || DrugTesting.DueCount == 0 || DrugTesting.HourFull || !DrugTesting.MayTest(pawn) || DrugTesting.KitCount(pawn.Map) == 0;

        private static Pawn kitDoctor;
        private static int kitTick = -1;
        private static Thing kitFound;

        /// <summary>The nearest kit the doctor can take (one of it: another doctor may have part of the stack reserved). Asked for each inmate the doctor is offered and again for the job, so the answer is kept for the tick.</summary>
        public static Thing KitFor(Pawn doctor)
        {
            int now = Find.TickManager.TicksGame;
            if (kitDoctor == doctor && kitTick == now && kitFound is { Spawned: true } && doctor.CanReserve(kitFound, DrugTesting.KitReservers, DrugTesting.KitsPerTest)) return kitFound;
            kitFound = GenClosest.ClosestThingReachable(doctor.Position, doctor.Map, ThingRequest.ForDef(DrugTesting.Kit), PathEndMode.ClosestTouch, TraverseParms.For(doctor), 9999f,
                t => !t.IsForbidden(doctor) && doctor.CanReserve(t, DrugTesting.KitReservers, DrugTesting.KitsPerTest));
            (kitDoctor, kitTick) = (doctor, now);
            return kitFound;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && !DrugTesting.CrisisOn(pawn.Map) && DrugTesting.MayTest(pawn) && !DrugTesting.HourFull && DrugTesting.Candidate(inmate)
            && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Some, 1, -1, null, forced) && KitFor(pawn) != null;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Job job = JobMaker.MakeJob(RPDrugTestDefOf.RP_DrugTest, t, KitFor(pawn));
            job.count = DrugTesting.KitsPerTest;
            return job;
        }
    }

    /// <summary>
    /// The round: take the inmate on (the hour's cap, the reasons they are tested for), pick up a kit, walk to the inmate, take them aside for a few hundred ticks, read the strip. The reasons and the kit are the job's
    /// own and are saved with it. The round fails, and lets the inmate go, when the policy is turned off, a riot or a hostage crisis begins, the doctor goes off duty, the inmate cannot be taken or stops waiting, or the kit
    /// is lost.
    /// </summary>
    public class JobDriver_DrugTest : JobDriver
    {
        private Pawn Inmate => job.targetA.Thing as Pawn;

        /// <summary>What they are tested for (the claim's reasons, kept with the job through a load and the hours after).</summary>
        private int reasons;

        /// <summary>The kit in the doctor's hands: what is verified before a result and what a completed test uses up.</summary>
        private Thing kit;

        private bool used;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref reasons, "reasons");
            Scribe_References.Look(ref kit, "kit");
            Scribe_Values.Look(ref used, "used");
        }

        private bool KitHeld => kit is { Destroyed: false } && kit.def == DrugTesting.Kit && kit.stackCount == DrugTesting.KitsPerTest && pawn.carryTracker?.CarriedThing == kit;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetB, job, DrugTesting.KitReservers, DrugTesting.KitsPerTest, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => DrugTesting.Stopped(pawn, Inmate) || !used && kit != null && !KitHeld);
            // (The round is over whichever way it ends: the inmate is let go, and their wait with it if it is still the round's own; a kit picked up and not used goes back on the ground.)
            AddFinishAction(condition =>
            {
                DrugTesting.Release(pawn);
                DrugTesting.EndWait(pawn, Inmate);
                if (pawn.carryTracker?.CarriedThing != null) pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            });
            Toil takeOn = ToilMaker.MakeToil("TakeOn");
            takeOn.initAction = () =>
            {
                // (The hour's two are taken, or another has them: this one waits for the next hour.)
                if (DrugTesting.Claim(pawn, Inmate) is not { } claim)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                reasons = claim.reasons;
            };
            takeOn.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return takeOn;
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, putRemainderInQueue: false, subtractNumTakenFromJobCount: true);
            Toil picked = ToilMaker.MakeToil("KitInHand");
            picked.initAction = () =>
            {
                kit = pawn.carryTracker.CarriedThing;
                if (!KitHeld) EndJobWith(JobCondition.Incompletable);
            };
            picked.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return picked;
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil aside = ToilMaker.MakeToil("TakeAside");
            aside.initAction = () =>
            {
                if (!DrugTesting.TryBegin(pawn, Inmate))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Job wait = JobMaker.MakeJob(RPDrugTestDefOf.RP_DrugTestWait, pawn);
                wait.expiryInterval = DrugTesting.TestTicks + 120;
                Inmate.jobs.StartJob(wait, JobCondition.InterruptForced);
                // (Taken aside only if the inmate took the wait: the round owns it from here, and a test needs it.)
                if (!DrugTesting.OwnsWait(pawn, Inmate)) EndJobWith(JobCondition.Incompletable);
            };
            aside.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return aside;
            Toil test = Toils_General.Wait(DrugTesting.TestTicks);
            test.handlingFacing = true;
            test.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            test.WithProgressBarToilDelay(TargetIndex.A);
            test.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, 3f) || !DrugTesting.OwnsWait(pawn, Inmate));
            yield return test;
            Toil done = ToilMaker.MakeToil("TestRead");
            done.initAction = () =>
            {
                // (Checked again before a result: the exact kit in the hands, the inmate still waiting for this doctor, the round still allowed.)
                if (!KitHeld || !DrugTesting.OwnsWait(pawn, Inmate) || DrugTesting.Stopped(pawn, Inmate))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // (A refusal opens no kit: it is carried back and dropped by the finish action.)
                if (DrugTesting.Conduct(pawn, Inmate, reasons: reasons) == DrugTestResult.Refused) return;
                used = true;
                kit.Destroy();
            };
            yield return done;
        }
    }

    /// <summary>
    /// The inmate's side of a drug test: standing by for the doctor, for as long as the doctor's round lasts. It is the round's own (the doctor is its target): the round ends it, and it ends itself when the round does
    /// (the doctor's job gone, or not this inmate's), so the inmate is never left waiting, and a job they took up instead is not cut.
    /// </summary>
    public class JobDriver_DrugTestWait : JobDriver_WaitMaintainPosture
    {
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !DrugTesting.RoundOf(job.targetA.Thing as Pawn, pawn));
            foreach (Toil toil in base.MakeNewToils()) yield return toil;
        }
    }
}
