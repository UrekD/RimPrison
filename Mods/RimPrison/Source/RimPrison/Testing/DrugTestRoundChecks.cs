using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// Drug testing's doctors' round walked in game ticks (the events plan): a whole round, a random test that keeps its reason across the hour and a load, the hour's cap across a load, and the ways a round is cut
    /// short (the policy turned off, the kit lost or swapped, the doctor off duty or interrupted, the inmate's wait replaced, a riot): the round stops, nothing is read, the kit goes back, the inmate is let go.
    /// Each scene is the ring checks' quiet yard (<see cref="RingScene"/>) with throwaway doctors working round the clock.
    /// </summary>
    public static partial class AutoTest
    {
        private sealed class DtRound
        {
            public PrisonGame g;
            public Map map;
            public RingScene s;
            public Pawn[] doctors, inmates;
            public WorkGiver_Scanner giver;
        }

        /// <summary>
        /// The scenes' doctors, on duty at every hour for as long as the scene stands. The prison's hourly pass writes every member of staff's shift into their timetable (a day doctor sleeps from 22:00, has two breaks, and a
        /// third of three or more doctors is put on nights) and a walked check crosses an hour now and then: a doctor the scene set to work all day was off duty (<see cref="DrugTesting.OnDuty"/>) from then on, and the
        /// third doctor was not offered a test after the two rounds of the hour had been walked. The same pass sends the off-shift home in the same tick it writes the shift (<c>PrisonGame.Notify_HourChanged</c>:
        /// <c>ShiftSystem.Balance</c>, then <c>StaffCommute.Hourly</c>), before anything a check does after the tick can put the timetable back: a doctor was on <c>RP_GoHome</c> in the middle of a round (the hour-22 pass in
        /// caredetail-cd3a and cd3b). So the pass does not write a held doctor's shift at all (<see cref="Patch_ShiftSystem_Apply_HeldDoctors"/>).
        /// </summary>
        private static readonly List<Pawn> dtDoctors = new List<Pawn>();

        /// <summary>
        /// Held doctors whose shift the check "the doctor's shift ends in the middle of it" has ended on purpose: they stay held (so that a pass crossing the hour in the ticks after the cut cannot write the hour back to
        /// work), and <see cref="KeepDrugTestDoctorsOnShift"/> does not put the shift back.
        /// </summary>
        private static readonly List<Pawn> dtShiftEnded = new List<Pawn>();

        /// <summary>
        /// The scenes' throwaway inmates, which no warden takes to a bed. They have none of their own, and vanilla's warden work (<c>WorkGiver_Warden_TakeToBed</c>) escorts such an inmate to a free bed in another room: a real guard
        /// reserved the inmate for <c>EscortPrisonerToBed</c> in the hour the cap check came back to offer its third doctor a test ("the inmate reserved by Whistler (EscortPrisonerToBed, -1 of 1)", stability10-s10f,
        /// inspector-in3a: one run in three). The walked rounds are not about beds, so the work is kept off these inmates (<see cref="Patch_WorkGiver_Warden_TakeToBed_HeldInmates"/>).
        /// </summary>
        private static readonly List<Pawn> dtInmates = new List<Pawn>();

        /// <summary>An inmate of a scene whom no warden takes to a bed (see <see cref="dtInmates"/>).</summary>
        internal static bool DrugTestInmateHeld(Pawn p) => dtInmates.Contains(p);

        /// <summary>
        /// Whether this thing is kept from every automatic haul: a drug test kit while a scene stands. A real workman hauls a kit lying on open ground to a stockpile (the work scan's <c>HaulToCell</c>), and the reservation
        /// the stack is then under is his, so the doctor of a round is offered no test: census c77-ev2 ("kits lying about: 2 at (60, 0, 67) ... reserved by Rob (HaulToCell, -1 of 1), the doctor can reserve one False").
        /// The scene's own doctors are held too: a round cut short puts its kit down, and the doctor's next job was an automatic haul of it, with the two left lying, to a stockpile, so the check found the doctor "still
        /// carrying RP_DrugTestKit27133 (stack 3, ... job HaulToCell)" (census c80-ev2). A round's own pickup is the job's toil and asks nothing of this. See
        /// <see cref="Patch_HaulAIUtility_CanAutomaticallyHaul_SceneKits"/>.
        /// </summary>
        internal static bool DrugTestKitHeld(Thing t) => dtDoctors.Count > 0 && t.def == DrugTesting.Kit;

        /// <summary>A new inmate of the scene, kept from the wardens' take-to-bed work for as long as the scene stands.</summary>
        private static Pawn DtInmate(RingScene s, string name)
        {
            Pawn p = s.Inmate(name);
            dtInmates.Add(p);
            s.r.Add(() => dtInmates.Remove(p));
            return p;
        }

        /// <summary>A doctor of a scene: the hourly pass leaves their shift as the scene wrote it (see <see cref="dtDoctors"/>).</summary>
        internal static bool DrugTestDoctorHeld(Pawn p) => dtDoctors.Contains(p);

        /// <summary>The scenes' doctors' shift written as the scene wrote it, once something has written another (the pass cannot: see <see cref="DrugTestDoctorHeld"/>; the night-shift flag of the daily balance can).</summary>
        private static void KeepDrugTestDoctorsOnShift()
        {
            foreach (Pawn d in dtDoctors)
            {
                if (d.Destroyed || d.timetable == null || dtShiftEnded.Contains(d) || d.GetComp<CompStaff>() is not { } sc) continue;
                bool changed = sc.nightShift;
                for (int hour = 0; hour < 24 && !changed; hour++) changed = d.timetable.GetAssignment(hour) != TimeAssignmentDefOf.Work;
                if (changed) DtWorkAllDay(d);
            }
        }

        /// <summary>Doctors on duty, inmates due for an overdose (a user, for a life sentence: no hearing), a stack of kits at the first doctor's feet, the policy targeted, the refusal roll decided against.</summary>
        private static DtRound DtRoundScene(Map map, Rollback r, int doctors, int inmates, int kits)
        {
            var t = new DtRound { g = PrisonGame.Instance, map = map, giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("RP_DrugTest").Worker };
            // (The real doctors are put off first, while the real log is the one in place: a real round in progress ends against it, and lets go of its own claim.)
            QuietTheRealStaff(map, r, "Doctor");
            DrugTestSetup(t.g, r);
            t.s = new RingScene(map, r);
            int now = Find.TickManager.TicksGame;
            t.inmates = new Pawn[inmates];
            for (int i = 0; i < inmates; i++)
            {
                Pawn p = DtInmate(t.s, "Zdt6" + (char)('a' + i));
                CompInmate c = p.Inmate();
                (c.lastOverdoseTick, c.lastDrugUseTick, c.tension, c.sentenceDays) = (now - 10, now - 100, 0f, -1);
                if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
                // (Held out of the regime: the walked checks cross real hours, and a change of the regime's activity ends the inmate's wait, or sends them to bed or to their cell, where nobody can take them aside.)
                (c.forcedActivity, c.forcedUntilTick) = (RPDefOf.RP_FreeTime, -1);
                t.inmates[i] = p;
            }
            t.doctors = new Pawn[doctors];
            for (int i = 0; i < doctors; i++) t.doctors[i] = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor);
            // (The check's own doctors are not left to the work tree: a round is started by hand.)
            foreach (Pawn d in t.doctors)
            {
                DtWorkAllDay(d);
                d.workSettings?.SetPriority(WorkTypeDefOf.Doctor, 0);
                dtDoctors.Add(d);
            }
            r.Add(() =>
            {
                dtDoctors.RemoveAll(t.doctors.Contains);
                dtShiftEnded.RemoveAll(t.doctors.Contains);
            });
            for (int i = 0; i < doctors; i++) t.s.Place(t.doctors[i], i);
            for (int i = 0; i < inmates; i++) t.s.Place(t.inmates[i], 4 + i);
            // (Every kit the check leaves on the ground is taken away again: the one placed, and what a round put down or split off.)
            HashSet<Thing> kitsBefore = map.listerThings.ThingsOfDef(DrugTesting.Kit).ToHashSet();
            r.Add(() =>
            {
                foreach (Thing left in map.listerThings.ThingsOfDef(DrugTesting.Kit).Where(k => !kitsBefore.Contains(k)).ToList())
                    if (!left.Destroyed) left.Destroy(DestroyMode.Vanish);
            });
            Thing kit = ThingMaker.MakeThing(DrugTesting.Kit);
            kit.stackCount = kits;
            GenPlace.TryPlaceThing(kit, t.doctors[0].Position, map, ThingPlaceMode.Near, out _);
            t.g.drugTests = DrugTestPolicy.Targeted;
            DrugTesting.RefuseForTests = false;
            DrugTesting.Hourly(map, t.g);
            return t;
        }

        /// <summary>The kits in the scene: on the ground (all of them on the map) and in the doctors' hands.</summary>
        private static int DtKits(DtRound t) =>
            t.map.listerThings.ThingsOfDef(DrugTesting.Kit).Sum(k => k.stackCount) + t.doctors.Sum(d => d.carryTracker?.CarriedThing is { } held && held.def == DrugTesting.Kit ? held.stackCount : 0);

        /// <summary>
        /// Where an inmate of a scene stands as drug testing reads them: each test of <see cref="DrugTesting.Takeable"/> and each way the hour's pass leaves them off the due list, by name. The walked checks cross real hours
        /// and the real prison's passes (a riot, an appointment, a lockdown, a bed) can take a throwaway inmate out from under a check; the lines that find one not due or not takeable say which, so the cause is read from the log.
        /// </summary>
        private static string DtInmateState(Map map, Pawn p)
        {
            CompInmate c = p.Inmate();
            if (c == null) return $"{p.LabelShort} has no inmate comp";
            int now = Find.TickManager.TicksGame;
            PrisonGame g = PrisonGame.Instance;
            return $"{p.LabelShort}: spawned {p.Spawned} (held by {p.ParentHolder?.GetType().Name ?? "nobody"}) at {p.PositionHeld} on the prisoners' list {map.mapPawns.PrisonersOfColonySpawned.Contains(p)} active flag {c.activeFlag} (now {InmateUtility.IsActiveInmate(p)}, released {p.guest?.Released}, breaking out {PrisonBreakUtility.IsPrisonBreaking(p)}, lord {p.GetLord()?.LordJob?.GetType().Name ?? "none"}), downed {p.Downed}, " +
                $"mental {p.MentalStateDef?.defName ?? "none"}, awake {p.Awake()}, job {p.CurJobDef?.defName ?? "none"}, in bed {p.CurrentBed()?.def.defName ?? "none"} (solitary {PunishmentUtility.IsSolitaryBed(p.CurrentBed())}), owns {p.ownership?.OwnedBed?.Position.ToString() ?? "no bed"} (solitary {PunishmentUtility.IsSolitaryBed(p.ownership?.OwnedBed)}), rest {p.needs?.rest?.CurLevel:F2}, " +
                $"on the way out {ShuttleSystem.OnTheWayOut(p)}, held {HostageCrisis.IsHeld(p)}, holding {HostageCrisis.Holding(p)}, supermax {SupermaxSystem.IsIn(c)}, escort only {c.security is { escortOnly: true }}, at an appointment {WorkGiver_Frisk.AtAppointment(p)}, " +
                $"visitor {c.visitor != null}, court lock {Trials.TrialOf(g, p) is { courtLock: true }}, forced {c.forcedActivity?.defName ?? "none"}, locked down {g.LockedDown(c)} (prison {g.LockdownActive}), " +
                $"crisis {DrugTesting.CrisisOn(map)} (riot flag {RiotPolice.RiotActive(map)}, hostage arc {HostageCrisis.Running(map) != null}), policy {g.drugTests}, due {DrugTesting.IsDue(p, out int mask)} (mask {mask}), " +
                $"reasons now {DrugTesting.ActiveReasons(g, p, c, now)} due now {DrugTesting.DueReasons(g, p, c, now)} (overdose {(now - c.lastOverdoseTick) / (float)GenDate.TicksPerHour:F1} h ago, tested for {string.Join("/", c.testedFor)}), " +
                $"claimed {DrugTesting.Claimed(p)}, tick {now} ({DrugTesting.Hour(map)}:00)";
        }

        /// <summary>
        /// Where a doctor of a scene stands as a round reads them: where they are and what they are doing, the shift (the timetable's hour, the night flag, due home, on duty as drug testing reads it) and whether the
        /// pass is held off them. A round that is cut short or never taken up says here whether the doctor was sent home (the commute's pass: <c>RP_GoHome</c>), off shift or busy.
        /// </summary>
        private static string DtDoctorState(Pawn d)
        {
            CompStaff sc = d.GetComp<CompStaff>();
            Map map = d.MapHeld ?? Find.CurrentMap;
            int hour = DrugTesting.Hour(map), real = GenLocalDate.HourOfDay(map);
            return $"{d.LabelShort}: spawned {d.Spawned} at {d.PositionHeld}, job {d.CurJobDef?.defName ?? "none"}, carrying {d.carryTracker?.CarriedThing?.ToString() ?? "nothing"}, at home {StaffCommute.AtHome(d)}, " +
                $"timetable {d.timetable?.GetAssignment(hour)?.defName} at {hour}:00 (the clock says {real}:00), night shift {sc?.nightShift}, due home {(sc != null && StaffCommute.ShouldBeHome(d, sc, hour))}, " +
                $"on duty {DrugTesting.OnDuty(d)}, a test doctor {DrugTesting.IsTestDoctor(d)}, held on shift {DrugTestDoctorHeld(d)}, tick {Find.TickManager.TicksGame}";
        }

        /// <summary>Who holds a reservation on the thing, as the reservation manager has it: the claimant, their job, and the stack and the number of pawns it was made for.</summary>
        private static string DtReservedBy(Map map, Thing thing)
        {
            string[] holders = map.reservationManager.ReservationsReadOnly.Where(x => x.Target.Thing == thing).Select(x => $"{x.Claimant?.LabelShort} ({x.Job?.def.defName ?? "no job"}, {x.StackCount} of {x.MaxPawns})").ToArray();
            return holders.Length == 0 ? "nobody" : string.Join(" + ", holders);
        }

        /// <summary>
        /// Each term of <see cref="WorkGiver_DrugTest.HasJobOnThing"/> by name, for a doctor who was not offered an inmate: the crisis, the doctor's leave to test, the hour's cap, the inmate as a candidate, the inmate reserved by anyone
        /// and reachable on the giver's own terms (danger: some), and a kit the doctor can reserve; with who holds each reservation on the inmate and on every kit lying about, and everyone who is carrying a kit (a hauler takes one off
        /// the ground, and the reservation the stack is under is theirs).
        /// </summary>
        private static string DtOfferWhy(DtRound t, Pawn doctor, Pawn inmate)
        {
            Map map = t.map;
            Thing kit = WorkGiver_DrugTest.KitFor(doctor);
            string kits = string.Join("; ", map.listerThings.ThingsOfDef(DrugTesting.Kit).Select(k =>
                $"{k.stackCount} at {k.Position} (forbidden {k.IsForbidden(doctor)}, reserved by {DtReservedBy(map, k)}, the doctor can reserve one {doctor.CanReserve(k, DrugTesting.KitReservers, DrugTesting.KitsPerTest)})"));
            string carried = string.Join("; ", map.mapPawns.AllPawnsSpawned.Where(p => p.carryTracker?.CarriedThing is { } held && held.def == DrugTesting.Kit)
                .Select(p => $"{p.LabelShort} carries {p.carryTracker.CarriedThing.stackCount} (job {p.CurJobDef?.defName ?? "none"})"));
            return $"the offer's terms for {doctor.LabelShort}: crisis {DrugTesting.CrisisOn(map)}, may test {DrugTesting.MayTest(doctor)}, hour full {DrugTesting.HourFull}, candidate {DrugTesting.Candidate(inmate)}, " +
                $"the inmate reserved by {DtReservedBy(map, inmate)} (the doctor can reserve them {doctor.CanReserve(inmate)}), reach with danger some {doctor.CanReach(inmate, PathEndMode.Touch, Danger.Some)}, " +
                $"a kit {(kit == null ? "none" : kit.stackCount + " at " + kit.Position)}; kits lying about: {(kits.Length == 0 ? "none" : kits)}; kits carried: {(carried.Length == 0 ? "none" : carried)}; {DtDoctorState(doctor)}";
        }

        /// <summary>The doctor is offered the inmate and starts the round. False if it could not be started (a premise, or a failure said).</summary>
        private static bool DtStart(DtRound t, Pawn doctor, Pawn inmate, string what)
        {
            if (!DrugTesting.Takeable(inmate, inmate.Inmate()))
            {
                RPLog.Message($"[RimPrison:test] drug testing ({what}): the inmate could not be taken before the round began, the round was not walked ({DtInmateState(t.map, inmate)})");
                return false;
            }
            if (!t.giver.HasJobOnThing(doctor, inmate))
            {
                DtFail($"{what}: the doctor is offered no test (due {DrugTesting.IsDue(inmate, out _)}, kits {DrugTesting.KitCount(t.map)}, doctors {DrugTesting.DoctorsOnSite(t.map)}, on duty {DrugTesting.IsTestDoctor(doctor)}, hour full {DrugTesting.HourFull}, reach {(doctor.CanReach(inmate, PathEndMode.Touch, Danger.Deadly) ? "yes" : "no")}; {DtOfferWhy(t, doctor, inmate)}; {DtInmateState(t.map, inmate)})");
                return false;
            }
            doctor.jobs.StartJob(t.giver.JobOnThing(doctor, inmate), JobCondition.InterruptForced);
            return true;
        }

        /// <summary>Ticks until the doctor has taken the inmate aside (the inmate's own wait began), or the round ended before it.</summary>
        private static bool DtUntilAside(Pawn doctor, Pawn inmate, string what)
        {
            int ticked = ShuttleTicks(() => DrugTesting.OwnsWait(doctor, inmate) || doctor.CurJob?.def != RPDrugTestDefOf.RP_DrugTest, 1500);
            if (DrugTesting.OwnsWait(doctor, inmate)) return true;
            DtFail($"{what}: the doctor did not take the inmate aside in {ticked} ticks (doctor {doctor.CurJob?.def.defName} at {doctor.Position - inmate.Position}, carrying {doctor.carryTracker.CarriedThing}, started {DrugTesting.StartedThisHour}, claims {PrisonGame.Instance.drugTestLog.claims.Count}; {DtDoctorState(doctor)})");
            return false;
        }

        // ---- a whole round ------------------------------------------------------------------------------------------------------------------

        /// <summary>The doctor's round in game ticks: a kit is picked up, carried to the inmate, the inmate is taken aside, the test is read and the kit used up; a refusal keeps it.</summary>
        private static void CheckDrugTestRound(Map map)
        {
            Guarded("drug testing: a doctor takes a kit to an inmate and tests them (real ticks), a refusal keeps the kit", map, r =>
            {
                DtRound t = DtRoundScene(map, r, 2, 2, 3);
                PrisonGame g = t.g;
                Pawn doctor = t.doctors[0], second = t.doctors[1], inmate = t.inmates[0], refuser = t.inmates[1];
                CompInmate c = inmate.Inmate(), rc = refuser.Inmate();
                int tests0 = g.stats.drugTests, kits0 = DtKits(t);
                if (!DtStart(t, doctor, inmate, "a round")) return;
                int ticked = ShuttleTicks(() => c.lastTestResult != DrugTestResult.None, 2500);
                if (c.lastTestResult == DrugTestResult.None)
                {
                    DtFail($"after {ticked} ticks the inmate has no test; the doctor is {doctor.CurJob?.def.defName} at {doctor.Position - inmate.Position}, carrying {doctor.carryTracker.CarriedThing}, started {DrugTesting.StartedThisHour}; {DtDoctorState(doctor)}");
                    return;
                }
                if (c.lastTestResult != DrugTestResult.Positive) DtFail($"an inmate who used a drug a moment ago tested {c.lastTestResult}");
                if (DtKits(t) != kits0 - 1) DtFail($"the kits are {DtKits(t)} after a test ({kits0} before, one used up)");
                if (g.stats.drugTests != tests0 + 1) DtFail("the test was not counted");
                if (DrugTesting.StartedThisHour != 1) DtFail($"{DrugTesting.StartedThisHour} tests are counted in the hour (1 begun)");
                ShuttleTicks(() => doctor.CurJob?.def != RPDrugTestDefOf.RP_DrugTest && DrugTesting.ClaimOf(inmate) == null, 60);
                if (g.drugTestLog.claims.Count != 0) DtFail("the round is still taken on after the test");
                if (doctor.carryTracker.CarriedThing != null) DtFail("the doctor is still carrying after the test");
                if (DrugTesting.OwnsWait(doctor, inmate) || inmate.CurJob?.def == RPDrugTestDefOf.RP_DrugTestWait) DtFail("the inmate is still waiting for the doctor after the test");

                // A refusal opens no kit: it is carried back and put down, the inmate's refusal is a seen misconduct, and it is no test in the counts.
                DrugTesting.RefuseForTests = true;
                int kits1 = DtKits(t), tests1 = g.stats.drugTests;
                if (!DtStart(t, second, refuser, "a refusal")) return;
                ticked = ShuttleTicks(() => rc.lastTestResult != DrugTestResult.None, 2500);
                if (rc.lastTestResult != DrugTestResult.Refused) DtFail($"the refusal came out {rc.lastTestResult} after {ticked} ticks ({DtDoctorState(second)}; {DtInmateState(map, refuser)})");
                ShuttleTicks(() => second.carryTracker.CarriedThing == null, 200);
                if (DtKits(t) != kits1) DtFail($"a refusal used up a kit ({kits1} -> {DtKits(t)})");
                if (g.stats.drugTests != tests1) DtFail("a refusal was counted as a test");
                if (second.carryTracker.CarriedThing != null) DtFail("the doctor is still carrying the kit after a refusal");
                if (!rc.records.Any(x => x.def == RPDrugTestDefOf.RP_RefusedTest && x.seen)) DtFail("a refusal in a round left no seen misconduct");
            });
        }

        /// <summary>
        /// A workman standing beside the scene's kits is not offered them to haul (a hauler reserved the stack under a round's doctor in census c77-ev2), and neither is the scene's own doctor (whose next job after a round
        /// cut short was hauling the kit it had put down and the rest of the stack, census c80-ev2).
        /// </summary>
        private static void CheckDrugTestKitsNotHauled(Map map)
        {
            Guarded("drug testing: the scene's kits are not offered to a hauler, nor to the scene's own doctor", map, r =>
            {
                DtRound t = DtRoundScene(map, r, 1, 1, 2);
                Thing kit = map.listerThings.ThingsOfDef(DrugTesting.Kit).OrderBy(k => k.Position.DistanceTo(t.doctors[0].Position)).FirstOrDefault();
                Pawn workman = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Workman);
                if (kit == null || !kit.Spawned)
                {
                    DtFail("the scene left no kit on the ground to offer a hauler");
                    return;
                }
                workman.Position = kit.Position;
                workman.Notify_Teleported();
                // (The premise: nothing but the scene's hold keeps the workman off the stack - they could reserve it and reach it.)
                bool free = workman.CanReserve(kit) && workman.CanReach(kit, PathEndMode.ClosestTouch, Danger.Deadly) && !kit.IsForbidden(workman);
                bool offered = HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp(workman, kit, false);
                if (!free)
                    RPLog.Message($"[RimPrison:test] drug testing (kits not hauled): the workman could not take the stack anyway (reserve {workman.CanReserve(kit)}, forbidden {kit.IsForbidden(workman)}); the check is skipped");
                else if (offered)
                    DtFail("a workman was offered the scene's kits to haul: a real hauler would reserve the stack under the round's doctor");
                // The scene's own doctor, beside the same stack: a round cut short puts its kit down, and a haul offered next carries it and the stack off.
                Pawn doctor = t.doctors[0];
                doctor.Position = kit.Position;
                doctor.Notify_Teleported();
                bool doctorFree = doctor.CanReserve(kit) && doctor.CanReach(kit, PathEndMode.ClosestTouch, Danger.Deadly) && !kit.IsForbidden(doctor);
                bool doctorOffered = HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp(doctor, kit, false);
                if (!doctorFree)
                    RPLog.Message($"[RimPrison:test] drug testing (kits not hauled): the doctor could not take the stack anyway (reserve {doctor.CanReserve(kit)}, forbidden {kit.IsForbidden(doctor)}); the doctor's part is skipped");
                else if (doctorOffered)
                    DtFail("the scene's own doctor was offered the scene's kits to haul: after a round cut short the doctor carries the stack off and the check finds a kit still in their hands");
            });
        }

        // ---- the reason and the cap across an hour and a load ---------------------------------------------------------------------------------

        /// <summary>
        /// A random test is begun at 19:00 and read after 20:00 (the hour's pass rebuilds the list and the random picks stop at 20:00), and a load comes in between (the due list is lost, what is saved is not): the test is
        /// still a random one, the day's quota counts it, and no second inmate is picked for it at the hour's pass.
        /// </summary>
        private static void CheckDrugTestRoundAcrossAnHourAndALoad(Map map)
        {
            Guarded("drug testing: a random test keeps its reason across the hour and a load, and the day's quota counts it", map, r =>
            {
                DtRound t = DtRoundScene(map, r, 2, 2, 3);
                PrisonGame g = t.g;
                Pawn doctor = t.doctors[0];
                foreach (Pawn p in t.inmates) (p.Inmate().lastOverdoseTick, p.Inmate().lastDrugUseTick, p.Inmate().lastTestedTick) = (DrugTesting.Never, DrugTesting.Never, DrugTesting.Never);
                (g.drugTests, g.drugTestsPerDay) = (DrugTestPolicy.TargetedAndRandom, 1);
                DrugTesting.HourForTests = 19;
                DrugTesting.Hourly(map, g);
                Pawn picked = DrugTesting.IsDue(t.inmates[0], out _) ? t.inmates[0] : DrugTesting.IsDue(t.inmates[1], out _) ? t.inmates[1] : null;
                if (picked == null)
                {
                    DtFail("random mode picked nobody at 19:00 with one random test owed and two inmates to pick from");
                    return;
                }
                Pawn left = picked == t.inmates[0] ? t.inmates[1] : t.inmates[0];
                CompInmate pc = picked.Inmate();
                if (!DtStart(t, doctor, picked, "a random test") || !DtUntilAside(doctor, picked, "a random test")) return;
                if (g.drugTestLog.claims.Count != 1 || g.drugTestLog.claims[0].reasons != DrugTesting.ReasonRandom) DtFail($"the round taken on carries the reasons {string.Join(",", g.drugTestLog.claims.Select(c => c.reasons))} (the random one expected)");

                // The hour's pass again (19:00 and then 20:00): the round under way counts against the day's one random test, so nobody else is picked; and at 20:00 nobody is picked at all.
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(left, out _)) DtFail("a second inmate was picked for the day's one random test while a doctor is doing it");
                DrugTesting.HourForTests = 20;
                DrugTesting.Hourly(map, g);
                if (DrugTesting.IsDue(left, out _) || DrugTesting.IsDue(picked, out _)) DtFail("a random pick is made at 20:00, after the doctors' round");
                // A load: the due list and the memory of the hour are gone, the cap and the round are saved.
                DrugTesting.LoseStaticsForTests();
                if (DrugTesting.StartedThisHour != 1) DtFail($"after a load the hour has {DrugTesting.StartedThisHour} tests begun (1 expected: the cap is saved)");
                int ticked = ShuttleTicks(() => pc.lastTestResult != DrugTestResult.None, 1500);
                if (pc.lastTestResult == DrugTestResult.None)
                {
                    DtFail($"after the hour and a load the test was not read in {ticked} ticks (doctor {doctor.CurJob?.def.defName}; {DtDoctorState(doctor)}; {DtInmateState(map, picked)})");
                    return;
                }
                if (!pc.lastTestRandom) DtFail("a random test read after the hour turned is not marked random (the reason was lost)");
                if (g.drugTestLog.randoms != 1) DtFail($"the day's random tests are {g.drugTestLog.randoms} after one (the reason was lost)");
                if (!DrugTesting.SearchedAgain(pc)) DtFail("an inmate tested at random across the hour is not searched again");
                if (g.drugTestLog.claims.Count != 0) DtFail("the round is still taken on after the test");
            });
        }

        /// <summary>
        /// Three doctors are offered tests in one tick: only two rounds are taken on (the cap counts the rounds taken on and not begun), and the cap is the same after a load, with the rounds under way, with the tests done
        /// (the third is not sent), and until the hour turns.
        /// </summary>
        private static void CheckDrugTestCapAcrossALoad(Map map)
        {
            Guarded("drug testing: two tests an hour, the rounds already taken on counted, and a load does not reset the cap", map, r =>
            {
                DtRound t = DtRoundScene(map, r, 3, 4, 6);
                PrisonGame g = t.g;
                Pawn third = t.doctors[2];
                string atStart = DtInmateState(map, t.inmates[2]), afterRounds = null;
                var jobs = new List<Job>();
                for (int i = 0; i < 3; i++)
                {
                    if (!t.giver.HasJobOnThing(t.doctors[i], t.inmates[i]))
                    {
                        DtFail($"doctor {i} is offered no test before any is taken on");
                        return;
                    }
                    jobs.Add(t.giver.JobOnThing(t.doctors[i], t.inmates[i]));
                }
                if (t.inmates.FirstOrDefault(p => !DrugTesting.Takeable(p, p.Inmate())) is { } stuck)
                {
                    RPLog.Message($"[RimPrison:test] drug testing (the cap): an inmate could not be taken, the cap was not walked ({DtInmateState(map, stuck)})");
                    return;
                }
                for (int i = 0; i < 3; i++) t.doctors[i].jobs.StartJob(jobs[i], JobCondition.InterruptForced);
                if (g.drugTestLog.claims.Count != 2 || third.CurJob?.def == RPDrugTestDefOf.RP_DrugTest)
                    DtFail($"three doctors started a round in one tick: {g.drugTestLog.claims.Count} are taken on, the third's job is {third.CurJob?.def.defName} (two taken on, the third not)");
                // A load with two rounds taken on and neither begun: the next hour's pass lists the inmates again, and the third doctor is still not sent.
                DrugTesting.LoseStaticsForTests();
                DrugTesting.Hourly(map, g);
                if (g.drugTestLog.claims.Count != 2) DtFail($"the hour's pass after a load let go of the rounds under way ({g.drugTestLog.claims.Count} of 2)");
                if (!DrugTesting.HourFull || !t.giver.ShouldSkip(third) || t.giver.HasJobOnThing(third, t.inmates[2])) DtFail("with two rounds taken on a third doctor is offered a test after a load");
                int ticked = ShuttleTicks(() => t.inmates[0].Inmate().lastTestResult != DrugTestResult.None && t.inmates[1].Inmate().lastTestResult != DrugTestResult.None, 2500);
                if (t.inmates[0].Inmate().lastTestResult == DrugTestResult.None || t.inmates[1].Inmate().lastTestResult == DrugTestResult.None)
                {
                    DtFail($"the two rounds were not read in {ticked} ticks (claims {g.drugTestLog.claims.Count}, begun {DrugTesting.StartedThisHour}, kits {DtKits(t)}; {string.Join("; ", t.doctors.Select(DtDoctorState))}; {string.Join("; ", t.inmates.Take(2).Select(p => DtInmateState(map, p)))})");
                    return;
                }
                afterRounds = DtInmateState(map, t.inmates[2]);
                if (DrugTesting.StartedThisHour != 2 || g.drugTestLog.claims.Count != 0) DtFail($"after two tests the hour has {DrugTesting.StartedThisHour} begun (2) and {g.drugTestLog.claims.Count} rounds taken on (0)");
                // The tests done, a load in the same hour: the third is not sent, and the cap cannot be got round by a start.
                DrugTesting.LoseStaticsForTests();
                DrugTesting.Hourly(map, g);
                if (!DrugTesting.HourFull || t.giver.HasJobOnThing(third, t.inmates[2]) || DrugTesting.TryBegin(third, t.inmates[2])) DtFail("after a load in the same hour a third test is offered or begun (the cap was lost with the memory)");
                if (DrugTesting.StartedThisHour != 2) DtFail($"the hour's count is {DrugTesting.StartedThisHour} after a load (2)");
                // The next hour: the cap opens again.
                DrugTesting.HourKeyForTests++;
                if (DrugTesting.HourFull || !t.giver.HasJobOnThing(third, t.inmates[2]))
                    DtFail($"the next hour the third doctor is still not offered a test (hour full {DrugTesting.HourFull}, doctor spawned {third.Spawned} on duty {DrugTesting.IsTestDoctor(third)} night shift {third.GetComp<CompStaff>()?.nightShift} " +
                        $"at {DrugTesting.Hour(map)}:00 {third.timetable?.GetAssignment(DrugTesting.Hour(map))?.defName}, inmate due {DrugTesting.IsDue(t.inmates[2], out _)} takeable {DrugTesting.Takeable(t.inmates[2], t.inmates[2].Inmate())}, kits {DrugTesting.KitCount(map)}); {DtOfferWhy(t, third, t.inmates[2])}; the third inmate now: {DtInmateState(map, t.inmates[2])}; at the start of the check: {atStart}; after the two rounds: {afterRounds}; the others now: {string.Join("; ", t.inmates.Where(p => p != t.inmates[2]).Select(p => DtInmateState(map, p)))}");
            });
        }

        // ---- the doctors held on shift through the hourly pass ---------------------------------------------------------------------------------

        /// <summary>
        /// The hourly pass writes a day doctor asleep from 22:00 and the commute sends them home in the same tick; a scene's doctor is held (<see cref="dtDoctors"/>), so the pass does not write them (the shift the pass writes is
        /// <see cref="ShiftSystem.Apply"/>, called for each doctor): their 22:00 stays work and they are not due home, while another doctor, and the same one once let go, are written asleep and due home. The control is what
        /// shows that the hold is the thing that kept it.
        /// </summary>
        private static void CheckDrugTestDoctorsHeldThroughThePass(Map map)
        {
            Guarded("drug testing: the hourly pass does not write a scene's doctor off shift, so no pass sends them home in the middle of a round", map, r =>
            {
                Pawn held = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor), other = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor);
                foreach (Pawn d in new[] { held, other }) DtWorkAllDay(d);
                dtDoctors.Add(held);
                r.Add(() => dtDoctors.Remove(held));
                const int Hour = ShiftSystem.DayShiftEnd;
                string Say(Pawn d) => $"{d.LabelShort}: {Hour}:00 is {d.timetable.GetAssignment(Hour)?.defName}, due home {StaffCommute.ShouldBeHome(d, d.GetComp<CompStaff>(), Hour)}";
                ShiftSystem.Apply(held, false);
                ShiftSystem.Apply(other, false);
                if (held.timetable.GetAssignment(Hour) != TimeAssignmentDefOf.Work || StaffCommute.ShouldBeHome(held, held.GetComp<CompStaff>(), Hour))
                    DtFail($"the hourly pass wrote a held doctor off shift ({Say(held)})");
                if (other.timetable.GetAssignment(Hour) != TimeAssignmentDefOf.Sleep || !StaffCommute.ShouldBeHome(other, other.GetComp<CompStaff>(), Hour))
                {
                    RPLog.Message($"[RimPrison:test] drug testing: the control doctor was not written asleep at {Hour}:00 by the pass, the hold was not told from it ({Say(other)})");
                    return;
                }
                // The daily balance puts a third of three or more doctors (the real ones count too) on nights, and a doctor on nights is not on duty for the round: it does not set the flag on a held doctor.
                CompStaff hc = held.GetComp<CompStaff>(), oc = other.GetComp<CompStaff>();
                ShiftSystem.SetShift(held, hc, true);
                ShiftSystem.SetShift(other, oc, true);
                if (hc.nightShift) DtFail("the daily balance put a held doctor on nights");
                if (!oc.nightShift)
                {
                    RPLog.Message("[RimPrison:test] drug testing: the control doctor was not put on nights by the balance, the hold was not told from it");
                    return;
                }
                oc.nightShift = false;
                // (Let go of the hold, the same doctor is written like the other: the hold is what kept the shift.)
                dtDoctors.Remove(held);
                ShiftSystem.Apply(held, false);
                if (held.timetable.GetAssignment(Hour) != TimeAssignmentDefOf.Sleep) DtFail($"a doctor let go of the hold is not written by the pass either ({Say(held)})");
                ShiftSystem.SetShift(held, hc, true);
                if (!hc.nightShift) DtFail("a doctor let go of the hold is not put on nights by the balance either");
            });
        }

        // ---- a round cut short --------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A round is begun, the inmate is waiting, and then something ends it (<paramref name="cut"/>): the round stops within a few ticks, nothing is read (no result, no count, no record), the kit goes back (or is gone
        /// as it was lost), the doctor carries nothing, the claim is let go, and the inmate's own wait is over (unless they took up another job: that one is not cut, <paramref name="replacementStays"/>).
        /// </summary>
        private static void DtCutShort(Map map, string what, Action<DtRound, Pawn, Pawn> cut, int kitsLost = 0, bool replacementStays = false, bool immediately = false,
            Action<DtRound, Pawn, Pawn> before = null, Action<DtRound, Pawn, Pawn> after = null)
        {
            Guarded($"drug testing: {what}, the round stops, nothing is read and the inmate is let go", map, r =>
            {
                DtRound t = DtRoundScene(map, r, 2, 1, 3);
                PrisonGame g = t.g;
                Pawn doctor = t.doctors[0], inmate = t.inmates[0];
                CompInmate c = inmate.Inmate();
                before?.Invoke(t, doctor, inmate);
                if (!DtStart(t, doctor, inmate, what) || !DtUntilAside(doctor, inmate, what)) return;
                int tests0 = g.stats.drugTests, positives0 = g.stats.drugPositives, kits0 = DtKits(t), misconducts0 = c.records.Count;
                cut(t, doctor, inmate);
                if (immediately && DrugTesting.OwnsWait(doctor, inmate)) DtFail($"{what}: the inmate is still waiting for the doctor at the moment the round ended");
                int ticked = ShuttleTicks(() => doctor.CurJob?.def != RPDrugTestDefOf.RP_DrugTest, 15);
                if (doctor.CurJob?.def == RPDrugTestDefOf.RP_DrugTest)
                {
                    DtFail($"{what}: the round went on for {ticked} ticks after");
                    return;
                }
                ShuttleTicks(() => false, 5);
                if (c.lastTestResult != DrugTestResult.None || g.stats.drugTests != tests0 || g.stats.drugPositives != positives0 || c.records.Count != misconducts0)
                    DtFail($"{what}: a result was read (the file says {c.lastTestResult}, tests {tests0} -> {g.stats.drugTests}, records {misconducts0} -> {c.records.Count})");
                if (g.drugTestLog.claims.Count != 0) DtFail($"{what}: the round is still taken on ({g.drugTestLog.claims.Count})");
                // (The round's finish puts down whatever is in the doctor's hands, the kit it picked up or another: a pawn's own tick comes every 15 ticks off camera, so the doctor has a few ticks after the round, not none, to be rid of it.)
                if (doctor.carryTracker.CarriedThing != null) ShuttleTicks(() => doctor.carryTracker.CarriedThing == null, 60);
                if (doctor.carryTracker.CarriedThing is { } held)
                    DtFail($"{what}: the doctor is still carrying {held} (stack {held.stackCount}, destroyed {held.Destroyed}, spawned {held.Spawned}, job {doctor.CurJob?.def.defName}, at {doctor.Position})");
                if (DtKits(t) != kits0 - kitsLost) DtFail($"{what}: the kits are {DtKits(t)} ({kits0} before, {kitsLost} lost with it)");
                if (DrugTesting.OwnsWait(doctor, inmate) || inmate.CurJob?.def == RPDrugTestDefOf.RP_DrugTestWait) DtFail($"{what}: the inmate is still waiting for the doctor");
                if (replacementStays && inmate.CurJob?.def != JobDefOf.Wait_MaintainPosture) DtFail($"{what}: the job the inmate took up instead was cut (now {inmate.CurJob?.def.defName})");
                after?.Invoke(t, doctor, inmate);
            });
        }

        private static void CheckDrugTestRoundCutShort(Map map)
        {
            DtCutShort(map, "the policy is turned off in the middle of it", (t, doctor, inmate) => t.g.drugTests = DrugTestPolicy.Off);
            DtCutShort(map, "the kit is lost in the middle of it", (t, doctor, inmate) => doctor.carryTracker.innerContainer.ClearAndDestroyContents(), kitsLost: 1);
            DtCutShort(map, "another kit than the one picked up is in the doctor's hands", (t, doctor, inmate) =>
            {
                // (A kit off the ground, as a round's own is: the one the round picked up is gone from the doctor's hands, and another that was lying there is in them.)
                doctor.carryTracker.innerContainer.ClearAndDestroyContents();
                Thing other = t.map.listerThings.ThingsOfDef(DrugTesting.Kit).FirstOrDefault(k => k.Spawned);
                if (other == null || doctor.carryTracker.TryStartCarry(other, 1) != 1) DtFail("set-up: the doctor could not take up another kit");
            }, kitsLost: 1);
            // The doctor's shift is over (the timetable's hour for the doctor turns to sleep; the hour is a decided one that is not the real hour, so the doctor's real day is left alone).
            int hour = (GenLocalDate.HourOfDay(map) + 12) % 24;
            // (The doctor stays held through the hourly pass, which would write the hour back to work if it crossed an hour in the ticks after the cut, but is let off the keeping of their shift: the timetable is the very
            // thing the check changes.)
            DtCutShort(map, "the doctor's shift ends in the middle of it", (t, doctor, inmate) =>
                {
                    dtShiftEnded.Add(doctor);
                    doctor.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                },
                before: (t, doctor, inmate) => DrugTesting.HourForTests = hour,
                after: (t, doctor, inmate) =>
                {
                    if (t.giver.HasJobOnThing(doctor, inmate) || DrugTesting.IsTestDoctor(doctor)) DtFail("a doctor whose shift has ended is offered a drug test");
                });
            DtCutShort(map, "the inmate takes up another job in place of the wait", (t, doctor, inmate) =>
            {
                Job other = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                other.expiryInterval = 600;
                inmate.jobs.StartJob(other, JobCondition.InterruptForced);
            }, replacementStays: true);
            DtCutShort(map, "the doctor is interrupted in the middle of it", (t, doctor, inmate) => doctor.jobs.EndCurrentJob(JobCondition.InterruptForced), immediately: true);
            Pawn rioter = null;
            DtCutShort(map, "a riot begins in the middle of it", (t, doctor, inmate) =>
            {
                rioter = DtInmate(t.s, "Zdt6r");
                t.s.Place(rioter, 25);
                rioter.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                // (The flag is the rioter's own tick, which off camera comes every 15 ticks, from a phase the check does not choose: the round has its few ticks to stop from the tick the flag goes up, not from the start of the riot.)
                int rioting = ShuttleTicks(() => RiotPolice.RiotActive(t.map), 3 * 15);
                if (!RiotPolice.RiotActive(t.map)) DtFail($"a riot begins in the middle of it: the rioter's flag was not up after {rioting} ticks (state {rioter.MentalStateDef?.defName})");
            }, after: (t, doctor, inmate) =>
            {
                // (The rioters' own ticks are the flag: the giver reads it, and it goes down on its own when the riot does.)
                if (!RiotPolice.RiotActive(t.map) || !t.giver.ShouldSkip(t.doctors[1]) || t.giver.HasJobOnThing(t.doctors[1], inmate)) DtFail($"a riot is on and the work giver still offers a test (flag {RiotPolice.RiotActive(t.map)})");
                rioter.mindState.mentalStateHandler.Reset();
                int ticked = ShuttleTicks(() => !RiotPolice.RiotActive(t.map), 300);
                if (RiotPolice.RiotActive(t.map))
                {
                    DtFail($"the riot's flag is still up {ticked} ticks after the riot ended");
                    RiotPolice.ForgetRiotForTests(t.map);
                }
            });
        }
    }

    /// <summary>
    /// Autotest: the hourly pass leaves a drug-test scene's doctor's timetable alone (<see cref="AutoTest.DrugTestDoctorHeld"/>), so that no pass in the middle of a walked round writes Sleep into it and sends the doctor home
    /// in the same tick.
    /// </summary>
    [HarmonyPatch(typeof(ShiftSystem), nameof(ShiftSystem.Apply))]
    [TestOnly]
    public static class Patch_ShiftSystem_Apply_HeldDoctors
    {
        // (The post scenes' throwaway guards and the tool count's scene staff are held the same way: see AutoTest.PostGuardHeld, AutoTest.ToolStaffHeld.)
        public static bool Prefix(Pawn p) => !(AutoTest.Active && (AutoTest.DrugTestDoctorHeld(p) || AutoTest.PostGuardHeld(p) || AutoTest.ToolStaffHeld(p)));
    }

    /// <summary>
    /// Autotest: the same for the night flag. The daily balance (<see cref="ShiftSystem.Rebalance"/>) puts a third of three or more doctors on nights - the cap check has three, and the real ones count - and the flag
    /// takes the doctor off duty (<see cref="DrugTesting.OnDuty"/>) in the tick it is set, a round before <c>KeepDrugTestDoctorsOnShift</c> can put it back.
    /// </summary>
    [HarmonyPatch(typeof(ShiftSystem), nameof(ShiftSystem.SetShift), typeof(Pawn), typeof(CompStaff), typeof(bool), typeof(int))]
    [TestOnly]
    public static class Patch_ShiftSystem_SetShift_HeldDoctors
    {
        public static bool Prefix(Pawn p) => !(AutoTest.Active && (AutoTest.DrugTestDoctorHeld(p) || AutoTest.PostGuardHeld(p) || AutoTest.ToolStaffHeld(p)));
    }

    /// <summary>
    /// Autotest: no warden takes a drug-test scene's inmate to a bed (<see cref="AutoTest.DrugTestInmateHeld"/>): the job is not made, so nothing reserves the inmate against the doctor the check offers them to.
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_Warden_TakeToBed), nameof(WorkGiver_Warden_TakeToBed.TryMakeJob))]
    [TestOnly]
    public static class Patch_WorkGiver_Warden_TakeToBed_HeldInmates
    {
        public static bool Prefix(Thing t, ref Job __result)
        {
            if (!AutoTest.Active || t is not Pawn p || !AutoTest.DrugTestInmateHeld(p)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// Autotest: nobody, the scene's own doctors included, is offered a drug-test scene's kits to haul (<see cref="AutoTest.DrugTestKitHeld"/>). Every automatic haul - the haul work scan's, the merge scan's, the
    /// opportunistic one and the idle haul job's - asks this question, so the stack lying at the doctors' feet is never reserved or carried off by a haul while a round is walked.
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp))]
    [TestOnly]
    public static class Patch_HaulAIUtility_CanAutomaticallyHaul_SceneKits
    {
        public static bool Prefix(Thing t, ref bool __result)
        {
            if (!AutoTest.Active || !AutoTest.DrugTestKitHeld(t)) return true;
            __result = false;
            return false;
        }
    }
}
