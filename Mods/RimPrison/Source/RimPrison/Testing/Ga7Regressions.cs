using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for gap analysis 7's bugs (stability round 19, see WORKLOG): B1 a corrupt guard at home loses a transferred patron, B2 the inquiry does not offer to suspend a callout officer,
    /// B3 the midnight pass counts the counsellors now, not from the screen's real-time cache, B4 a goal of an inmate on no map reads no map. Each is self-contained on throwaway pawns and puts back what it
    /// touches, and fails against the code as it was.
    /// </summary>
    public static partial class AutoTest
    {
        private static void Ga7Fail(string name, string what) => problems0.Add($"gap 7 {name}: {what}");

        private static void CheckGa7Bugs(Map map)
        {
            CheckGa7PatronTransferredWhileTheGuardIsAtHome(map);
            CheckGa7InquiryDoesNotSuspendACalloutOfficer(map);
            CheckGa7MidnightOfferCountsTheCounsellorsNow(map);
            CheckGa7WantOffTheMapReadsNoMap(map);
        }

        /// <summary>Every member of staff's corruption record, put back by the rollback (the corruption pass drifts everybody's integrity and may open a case or drop a gone patron).</summary>
        private static void Ga7KeepCorruptionRecords(Map map, Rollback r)
        {
            foreach (Pawn s in StaffCommute.AllStaff(map))
            {
                CompStaff c = s.GetComp<CompStaff>();
                (float integrity, float suspicion, bool open, bool corrupt, bool agent, bool exposed, Pawn patron, GangDef gang, bool carrying) =
                    (c.integrity, c.suspicion, c.iaOpen, c.corrupt, c.doubleAgent, c.exposedCorrupt, c.patron, c.patronGang, c.carryingToday);
                List<string> evidence = c.evidence.ToList();
                r.Add(() =>
                {
                    (c.integrity, c.suspicion, c.iaOpen, c.corrupt, c.doubleAgent, c.exposedCorrupt, c.patron, c.patronGang, c.carryingToday) = (integrity, suspicion, open, corrupt, agent, exposed, patron, gang, carrying);
                    c.evidence = evidence;
                });
            }
        }

        /// <summary>
        /// B1: a corrupt guard at home (a night guard by day) whose patron, with no gang, is transferred straight off the floor (<see cref="InmateUtility.TransferOut"/>'s direct path: despawned and discarded,
        /// vanilla's exit skipped, so the pawn stays a prisoner of the colony and not dead) loses the patron when the patron leaves, and is off the payroll, not left corrupt and idle with a phantom patron that a
        /// save would then turn into none. <see cref="Story.Notify_Leaving"/> looked only at the staff on the map, and the daily pass's test missed a discarded pawn. Then the daily pass's own net: a patron
        /// discarded the same way and still on the guard's record is dropped by <see cref="CorruptionSystem.Daily"/>.
        /// </summary>
        private static void CheckGa7PatronTransferredWhileTheGuardIsAtHome(Map map)
        {
            Guarded("gap 7 B1: a corrupt guard at home loses a transferred patron", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Ga7KeepCorruptionRecords(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                CompStaff sc = guard.GetComp<CompStaff>();
                Pawn patron = Throwaway(map, r);
                CompInmate pc = patron.Inmate();
                HealthUtility.DamageUntilDowned(patron, allowBleedingWounds: false);
                if (!g.home.Contains(guard) || guard.Spawned || pc == null || patron.Dead || !patron.Downed || !patron.IsPrisonerOfColony)
                {
                    Ga7Fail("B1", $"the check's own premise: the guard at home {g.home.Contains(guard)} and off the map {!guard.Spawned}, the patron an inmate {pc != null}, alive {!patron.Dead}, downed {patron.Downed} (the direct transfer path), a prisoner {patron.IsPrisonerOfColony}");
                    return;
                }
                (sc.corrupt, sc.doubleAgent, sc.exposedCorrupt, sc.patron, sc.patronGang, sc.suspicion) = (true, false, false, patron, null, 0f);
                g.ledger.funds += InmateUtility.TransferCost;
                InmateUtility.TransferOut(patron, pc);
                if (!patron.Destroyed || patron.Spawned)
                {
                    Ga7Fail("B1", $"the check's own premise: the transfer did not take the patron straight off the floor (destroyed {patron.Destroyed}, on the map {patron.Spawned}, on the way out {ShuttleSystem.OnTheWayOut(patron)})");
                    return;
                }
                (bool keptOnLeaving, bool corruptOnLeaving) = (sc.patron != null, sc.corrupt);
                CorruptionSystem.Daily(map);
                if (keptOnLeaving || corruptOnLeaving || sc.patron != null || sc.corrupt)
                    Ga7Fail("B1", $"a corrupt guard at home whose patron was transferred kept them: when the patron left, patron kept {keptOnLeaving}, corrupt {corruptOnLeaving}; "
                                  + $"after the daily pass, patron {sc.patron?.LabelShort ?? "none"}, corrupt {sc.corrupt} (the transferred pawn: a prisoner {patron.IsPrisonerOfColony}, dead {patron.Dead}, discarded {patron.Discarded})");

                // The daily pass's net: a patron discarded off the floor while still on the record (a save from before the fix, any other way out) is noticed gone.
                Pawn gone = Throwaway(map, r);
                gone.DeSpawn();
                Find.WorldPawns.PassToWorld(gone, PawnDiscardDecideMode.Discard);
                (sc.corrupt, sc.patron, sc.patronGang) = (true, gone, null);
                CorruptionSystem.Daily(map);
                if (sc.patron != null || sc.corrupt)
                    Ga7Fail("B1", $"the daily pass kept a discarded patron on a guard at home: patron {sc.patron?.LabelShort ?? "none"}, corrupt {sc.corrupt} (the pawn: a prisoner {gone.IsPrisonerOfColony}, dead {gone.Dead}, destroyed {gone.Destroyed}, discarded {gone.Discarded})");
            });
        }

        /// <summary>
        /// B2: the inquiry into a death by an officer offers "suspend the officer" for one of the prison's own staff, and not for a callout officer of the riot squad, who leaves on schedule within hours whatever
        /// the suspension says (<see cref="StaffCommute.ShouldBeHome"/> is false for temporary staff): the cheapest answer would cost nothing.
        /// </summary>
        private static void CheckGa7InquiryDoesNotSuspendACalloutOfficer(Map map)
        {
            Guarded("gap 7 B2: the inquiry does not offer to suspend a callout officer", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn officer = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_RiotPolice);
                officer.GetComp<CompStaff>().leaveAtTick = Find.TickManager.TicksGame + RiotPolice.StayHours * GenDate.TicksPerHour;
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn victim = Throwaway(map, r);
                ChoiceLetter_PrisonDecision Inquiry(Pawn by)
                {
                    DeathAftermath.OpenInquiry(g, victim, by, map);
                    return r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Inquiry") && l.pawns.Contains(by));
                }
                ChoiceLetter_PrisonDecision callout = Inquiry(officer), own = Inquiry(guard);
                if (callout == null || own == null)
                {
                    Ga7Fail("B2", $"the check's own premise: an inquiry letter for the callout officer {callout != null}, for the prison's guard {own != null}");
                    return;
                }
                string calloutWhy = callout.decision.Worker.Disabled(callout, 2), ownWhy = own.decision.Worker.Disabled(own, 2);
                if (calloutWhy == null) Ga7Fail("B2", $"the inquiry offers to suspend {officer.LabelShort}, a callout officer of the riot squad (leaves at tick {officer.GetComp<CompStaff>().leaveAtTick}): the answer costs nothing");
                if (ownWhy != null) Ga7Fail("B2", $"the inquiry does not offer to suspend {guard.LabelShort}, one of the prison's own guards: {ownWhy}");
            });
        }

        /// <summary>
        /// B3: the midnight pass asks whether the prison has a classroom and a counsellor now, not from the screen's count kept for half a second of wall-clock time: a counsellor fired in that half second
        /// (hundreds of ticks in a fast run) was still counted and an offer went out to a prison with none. The prison's own teachers set aside, a throwaway counsellor counted (the cache filled), then
        /// fired, and the pass run in the same half second: no offer.
        /// </summary>
        private static void CheckGa7MidnightOfferCountsTheCounsellorsNow(Map map)
        {
            Guarded("gap 7 B3: the midnight offer counts the counsellors now", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodyContractDef def = CustodyContracts.Def;
                CustodyBook b = g.custody;
                if (def?.demandProgram == null || b == null)
                {
                    Ga7Fail("B3", $"the check's own premise: a contract def with a course {def?.demandProgram != null}, a book {b != null}");
                    return;
                }
                if (SpecialMeasures.Restricted || !def.levels.Any(Unlocks.IsUnlocked))
                {
                    RPLog.Message($"[RimPrison:test] gap 7 B3 skipped: the state's measures {SpecialMeasures.Restricted}, a contract level unlocked {def.levels.Any(Unlocks.IsUnlocked)}");
                    return;
                }
                (CustodyOffer offer0, CustodyContract contract0, CustodyContract last0, int nextId0, int nextDay0) = (b.offer, b.contract, b.last, b.nextId, b.nextOfferDay);
                r.Add(() => (b.offer, b.contract, b.last, b.nextId, b.nextOfferDay) = (offer0, contract0, last0, nextId0, nextDay0));
                // (The prison's own teachers off the books for the check: nothing ticks meanwhile.)
                List<StaffRoleDef> teacherRoles = def.demandProgram.TeacherRoles;
                var aside = StaffCommute.AllStaff(map).Select(p => p.GetComp<CompStaff>()).Where(c => c.role != null && teacherRoles.Contains(c.role)).Select(c => (c, c.role)).ToList();
                foreach ((CompStaff c, StaffRoleDef _) in aside) c.role = null;
                StaffCommute.StaffVersion++;
                r.Add(() =>
                {
                    foreach ((CompStaff c, StaffRoleDef role) in aside) c.role = role;
                    StaffCommute.StaffVersion++;
                });
                Pawn counsellor = null;
                for (int i = 0; i < 6 && counsellor == null; i++)
                {
                    Pawn p = ThrowawayStaff(map, r, teacherRoles[0]);
                    if (ProgramSystem.CanTeach(p, teacherRoles[0])) counsellor = p;
                }
                CustodySupply before = CustodyContracts.Supply(map, def, 1, fresh: true);
                if (counsellor == null || before.rooms == 0 || before.teachers != 1)
                {
                    RPLog.Message($"[RimPrison:test] gap 7 B3 skipped: a counsellor who can teach {counsellor != null}, classrooms {before.rooms}, teachers counted {before.teachers} (1 wanted)");
                    return;
                }
                StaffUtility.Fire(counsellor, record: false);
                // (Still the same half second of wall-clock time: the screen's count has the counsellor. If it turned over between the two lines, the check proves nothing and says so.)
                if (CustodyContracts.Supply(map, def, 1).teachers != 1)
                {
                    RPLog.Message("[RimPrison:test] gap 7 B3 skipped: the half second turned over between filling the cache and the pass");
                    return;
                }
                (b.offer, b.contract, b.nextOfferDay) = (null, null, Ledger.CurrentDay);
                int letters0 = r.NewLetters.Count();
                CustodyContracts.Daily(g, map);
                bool letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_CustodyContract"));
                if (b.offer != null || letter)
                    Ga7Fail("B3", $"the midnight pass made a custody offer to a prison whose only counsellor was fired in the same half second (offer {b.offer != null}, letter {letter}, {r.NewLetters.Count() - letters0} new letters; counted now {CustodyContracts.Supply(map, def, 1, fresh: true).teachers} teachers)");
            });
        }

        /// <summary>
        /// B4: a goal of an inmate on no map (generated for a shuttle not yet landed) reads no map, not the one the player is looking at: the grudge against a member of staff is found on the inmate's own map
        /// only. The same inmate spawned on the prison's map finds it (the control).
        /// </summary>
        private static void CheckGa7WantOffTheMapReadsNoMap(Map map)
        {
            Guarded("gap 7 B4: a goal off the map reads no map", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn inmate = Throwaway(map, r, spawn: false);
                CompInmate c = inmate.Inmate();
                if (c == null || inmate.MapHeld != null)
                {
                    Ga7Fail("B4", $"the check's own premise: an inmate's file {c != null}, on no map {inmate.MapHeld == null}");
                    return;
                }
                c.staffMemories.Add(new StaffMemory { staffId = guard.thingIDNumber, name = guard.LabelShort, score = Wants.GrudgeAt - 20f, lastTick = Find.TickManager.TicksGame });
                bool offMap = WantWorker_Score.FindTarget(inmate, c, out _, out string offName, out _);
                GenSpawn.Spawn(inmate, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
                InmateGenerator.MakeInmate(inmate, null);
                NoMentalStateTraits(inmate);
                bool onMap = WantWorker_Score.FindTarget(inmate, c, out _, out _, out _);
                if (offMap) Ga7Fail("B4", $"an inmate on no map found a grudge target ({offName}) on the map the player is looking at ({Find.CurrentMap})");
                if (!onMap) Ga7Fail("B4", "the control: the same inmate on the prison's map did not find the guard they hold a grudge against");
            });
        }
    }
}
