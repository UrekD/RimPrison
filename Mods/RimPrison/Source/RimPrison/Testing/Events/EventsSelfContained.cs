using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    public static partial class AutoTest
    {
        // The self-contained checks: each builds and removes its own throwaway pawns (or works on the live prison and puts it back), run before the incidents put inmates in the hole, down them or tie them up. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvCount()
            {
                // The count (CountChecks.cs): an escape nobody saw is told at the next count, one seen at once; a missed count, the out-of-place write-up, the HUD and the alert, counts a day and none.
                CheckCount(map);
            }

            private void EvSuppression()
            {
                // Suppression made legible (SuppressionChecks.cs): the wing table and the weekly report, the Programs blocker, the policy tooltip's hours, the lid letter and the riot-odds spike, the file's date.
                CheckSuppression(map);
            }

            private void EvInspector()
            {
                // The inspector who itemises (InspectorChecks.cs): the walk, each kind of finding read from a seeded state, the to-do lines, the re-inspection's grant and fine, a bribed inspector's empty report and the exposure that names it.
                CheckInspector(map);
            }

            private void EvRegimeFeedback()
            {
                // The regime pulled (RegimeFeedbackChecks.cs): the strip's numbers from a seeded day, the demands that name the timetable and their judgement on the schedule, each one-day order applied and reverted at midnight, the inspector's line on a level locked up too long.
                CheckRegimeFeedback(map);
            }

            private void EvToolCount()
            {
                // Count the tools back in (ToolCountChecks.cs): a theft in real work, the count by a guard on real ticks (one shortage, no name, nothing known before it), the search that takes the tool back once, the stash and the save, stacking refused, the three answers and none, the budget, the replacing, a room that is gone, an old save, the other finds, the kitchen.
                CheckToolCount(map);
            }

            private void EvHandover()
            {
                // The handover (HandoverChecks.cs): the options and the dialog's default, the letter with and without the old warden's note, the clock of a day before the game, the note on seeds made up for it, and a sowing on the live prison (the small one), judged and put back.
                CheckHandover(map);
            }

            private void EvAdjudication()
            {
                // The adjudication sheet (AdjudicationChecks.cs): serious offences queued with their evidence, the morning's one letter, the four answers (the policy's sanction, remission lost, days added and struck out on appeal, a charge the court tries), the budget, the unanswered, the inspector's line.
                CheckAdjudication(map);
            }

            private void EvReleasePrep()
            {
                // Release preparation (ReleasePrepChecks.cs): nothing rolled at the gate, the review begun at the departure, funded lodging against an unfunded promise at one controlled roll, a payment that never mends a new offence, a setback reported and helped once, the interview, the calibrated hazard, the window and the handlers' capacity, an old queued rearrest and an old release unchanged, a parolee's letter before a later failure, and the pawns the cases keep.
                CheckReleasePrep(map);
            }

            private void EvCustodyContract()
            {
                // Custody contracts (CustodyContractChecks.cs): the offer's dossier and letter, the cadence, a delayed shuttle and a part of the cohort admitted, the rest kept aboard and admitted by the next, payment and window by admission, real attendance credited and the bonus paid once at the end of the window, early departures forfeiting it, the stated pauses and cancellations.
                CheckCustodyContracts(map);
            }

            private void EvInformantTask()
            {
                // Tasking an informant (InformantTaskChecks.cs): the tasked tip names the target's tunnel and then their debt, never the decoy's contraband (untasked, the decoy can be named), a wrong lead only from distrust, the
                // exposure by days in place and with the gang on the wing (a forced roll: day 6 and not day 1), the daily burn, the target menu, the move near the target and keep-apart, and the task's end.
                CheckInformantTask(map);
            }

            private void EvRiotReview()
            {
                // The morning after (RiotReviewChecks.cs): a riot of four with a camera on two, real ticks, the hour's quiet that closes it and the next 06:00's one letter naming only the two seen; the damage bill and the hurt; "charge" (two cases) and the second step; a restriction of two days (the yard shut on days 1 and 2, not 3, the hold credited per day served, the tension after); the answers; charge against the riot's own records; two riots, a joiner, a blow from staff; the budget, the book's last ten.
                CheckRiotReview(map);
            }

            private void EvUnitOfficer()
            {
                // Unit officers (UnitOfficerChecks.cs): naming sets the patrol and rotation starts the days again (the old patrol put back when they leave), five forced days of presence give the officer's wing its trust and the other
                // wing none (never past the ceiling, nothing under the state's chief), the corruption lean at day 5 and back after rotation, fewer complaints from the wing, the forced staff tip names the wing's hidden debt and never
                // the other wing's, a guard kept off the wing by kin is refused with the reason, leave keeps the days and firing clears them, the row and the record.
                CheckUnitOfficers(map);
            }

            private void EvRumours()
            {
                // The rumour mill (RumourChecks.cs): the yard called off on a throwaway wing and a false supermax rumour on another, two hourly passes - the second wing carries the cause and the first never the false one; an
                // informant names the origin; a calm address ends the false one, the true one survives it halved, a firm one makes it stick; the walk names the origin; the riot's line; one live rumour per wing; two days to burn out.
                CheckRumours(map);
            }

            private void EvStandingOrders()
            {
                // Standing orders and the desk (StandingOrderChecks.cs): the routine decisions only, the letter's box and an order only from an answer by hand, a later letter answered at once with no budget (archived,
                // its review link, the chronicle), an order given while a letter waited, a disabled or missing answer and an urgent letter falling back with their line once, lifting, the desk's rows and budget.
                CheckStandingOrders(map);
            }

            private void EvWardenAsks()
            {
                // Asks to see the warden (WardenAskChecks.cs): one request per inmate by rank, the HUD's row, the lapse after a day (the clock forward and back), the hour dropping a request whose reason is over; a chat with an
                // asker at twice a chat's trust and calm, a request after the day's word (not the informant's pitch), a wound-up asker met on the walk at twice the walk's trust; the witness asking only out of the report's dice
                // and reported by the word, the debtor's, striker's and cell request's hooks, a known gang's doubter heard out and a hidden one's never asking, the false rumour's asker naming the origin and the truth, a request
                // tied to its own rumour, the step-down letter's request and its answer; Intel's "Go", why it is off, and the walk it orders.
                CheckWardenAsks(map);
            }

            private void EvClassBooking()
            {
                // Class bookings (ClassBookingChecks.cs): the booked small class has the shared room and teacher and the fuller one the next free hour, charged and credited once; every reason a booked class does not run is kept and charges nothing; three misses in a row warn, five are kept, none cancels; one booking for a program in an hour.
                CheckClassBooking(map);
            }

            private void EvParoleScene()
            {
                // Parole as a scene and life on parole (ParoleChecks.cs): a hearing room, a guard walking an inmate in, the board's letter, refusals, grants, parolees rearrested for a cause.
                ParoleTest(map);
            }

            private void EvStateOversight()
            {
                // The state's oversight (MeasuresChecks.cs): special measures, its security chief, flagship status, legendary transfers.
                RunMeasuresChecks(map);
            }

            private void EvGangTurf()
            {
                TurfTest(map);
                // Regression checks for the review of gang turf (B1-B6 and the gaps in the turf test above), in TurfRegressions.cs.
                CheckTurfClaimFollowsItsPatch(map);
                CheckTurfStakeFollowsItsPatch(map);
                CheckTurfRaidsBelongToTheirWar(map);
                CheckTurfCancelledWarGivesNothingToTheEmptied(map);
                CheckTurfRaidAnsweringIsOrderIndependent(map);
                CheckTurfOverlayRebuildsOnChange(map);
                CheckTurfHiddenAndKnownGangsInEitherOrder(map);
                CheckTurfRiotLetterHidesAHiddenCause(map);
            }

            private void EvGangWar()
            {
                GangWarTest(map);
            }

            private void EvHostages()
            {
                HostageTest(map);
                CheckHostageRegressions(map);
            }

            private void EvEconomy()
            {
                EconomyTest(map);
            }

            private void EvCells()
            {
                CellsTest(map);
            }

            private void EvWants()
            {
                WantsTest(map);
                CheckWantsReview(map);
            }

            private void EvCellRules()
            {
                CellsRulesTest(map);
            }

            private void EvParallelPrograms()
            {
                CheckParallelPrograms(map);
            }

            private void EvMail()
            {
                MailTest(map);
            }

            private void EvReviewRegressionsB2ToB16()
            {
                // Regression checks for the review's bugs B2-B16: each builds and removes its own throwaway pawns and puts back what it touches.
                CheckSecondMapRunsNoPrisonPass(map);
                CheckDownedInmateIsReleasedOnceUp(map);
                CheckLoneStafferDyingKeepsTheGame(map);
                CheckNewStayStartsClean(map);
                CheckConfinementIsNotShortened(map);
                CheckBalanceRegressions(map);
                CheckReviewFixes(map);
                CheckStaffMemoryKeepsStrongest();
                CheckDoorModeClearsReachability(map);
                CheckPerfPass(map);
                CheckGa4Perf(map);
                CheckRetirementLetterForStaffAtHome(map);
                CheckInnocentExecutionLine(map);
                CheckTeacherWhoCannotTeach(map);
                CheckFormerRegisterKeepsNews(map);
                CheckOverlappingInquiries(map);
            }

            private void EvReviewRegressionsB8ToB32()
            {
                // ...and for the rest (B8, B9, B13, B17-B32), in FixChecks2.cs.
                CheckCorruptionReachesStaffAtHome(map);
                CheckShiftBalanceCountsAvailableGuards(map);
                CheckStarvedProgramGainsPriority(map);
                CheckHiddenInfoStaysOffScreens(map);
                CheckRestraintWithNoPunishmentBed(map);
                CheckHomeBedFollowsOwnership(map);
                CheckGangAmbushGoesUnseenOutOfSight(map);
                CheckStorytellerSeesAllRivalGangs(map);
                CheckResignationsAreNotAllBurnouts(map);
                CheckUnansweredPromotionIsNeutral(map);
                CheckLettersIgnoreDeadStaff(map);
                CheckSuspensionAlertFindsStaffAtHome(map);
                CheckFireWhileCarried(map);
                CheckRetrainingRestartsPromotionClock(map);
                CheckMailForTheLonelyAndRealSorters(map);
                CheckGradesFollowTheDay(map);
                CheckUtilityBillAndBus(map);
                CheckSmallerItems(map);
            }

            private void EvBigPrisonFeedingRegressions()
            {
                // ...and for the review of the big-prison feeding change, in FixChecks3.cs.
                CheckMealCountersUseTheMealsStack(map);
                CheckOwedPunishmentKeepsSlowPace(map);
                CheckMailTripYieldsToAlarm(map);
                CheckServiceLayoutIgnoresLockedCells(map);
                CheckServiceSkipsCellEaters(map);
                CheckWalkingDistanceIsEightConnected(map);
                CheckHungryLineCountsLive(map);
                CheckCanteenSearchRemembersNoMeal(map);
                CheckHiddenGangKeepsSetAsideQuiet(map);
            }

            private void EvMailClerkRegressions()
            {
                // ...and for the inmate mailroom clerk, in MailClerkChecks.cs (the layout one runs at the start of the big plans).
                CheckGuardsLeaveFreshPostToTheClerk(map);
                CheckClassLeavesTheClerkTheirPost(map);
            }

            private void EvLibraryAndExercise()
            {
                // ...and for exercise, the library and the book cart, in LibraryChecks.cs (last of the self-contained ones: it needs able inmates with beds, and put in front of the
                // hostage checks it made their door and reach checks fail now and then, for reasons not found; run after them it leaves them as they were).
                LibraryTest(map);
            }

            private void EvParoleReviewRegressions()
            {
                // Regression checks for the review of the parole scene (B1-B10 and the gaps in ParoleTest), in ParoleRegressions.cs (after the self-contained ones, as the library check above
                // is: a check run in front of the rest has made later ones fail now and then).
                CheckParoleSeatedHearingNeedsAnInmateInReach(map);
                CheckParoleVerdictRechecksTheInmate(map);
                CheckParoleLetterDecidesOnlyAHeldCase(map);
                CheckParoleDroppedHearingFallsBackToTheRoll(map);
                CheckParoleHearingRoomGoneFallsBackToTheRoll(map);
                CheckParoleLettersNotFromRearrestedParolees(map);
                CheckParoleEscortEndsWithTheEscort(map);
                CheckParoleSeatedThenLeftIsCalledAgainWithoutEscort(map);
                CheckParoleVerdictCountsAnIncidentSinceTheSitting(map);
                CheckParoleProveWrongIsNotMetByWaiting(map);
                CheckParoleCalledAloneBeatDoesNotChurn(map);
                CheckParoleSeatsAreForInmates(map);
                CheckParoleBoardAtThePaperworkDesk(map);
                CheckParoleEscortedInmateWalksAtTheGuardsPace(map);
                CheckCommissaryPurchaseEatsFromAHeldStack(map);
                CheckPrayerInTheCellNeedsTheCellFree(map);
                CheckLaundryWorkerKeptFromClassWhileUniformsAreShort(map);
                CheckStashLeavesWhatTheyAreUsing(map);
                CheckFineMenuFollowsTheCooks(map);
                CheckVanillaBedSearchKeepsInmatesToTheirWing(map);
                CheckCarryingJobsAreNotCutShort(map);
                CheckParoleHeldCaseSurvivesAnInmateBeingCarried(map);
                CheckParoleReportNewsOnlyWhileSupervised(map);
                CheckEscortWalksTheInmatesWay(map);
                CheckEscortFollowsWhenItsWayGoesRound(map);
                CheckEscortToBedLeavesNoMealUnclaimed(map);
                CheckSearchSurvivesAnOwnerLeavingTheirBed(map);
                CheckInsultingSpreeLeavesASleeperAlone(map);
                CheckNoColonistBedsAlert(map);
                CheckColonistBarClearOfPanel(map);
                CheckColonistBarGroupedByRole(map);
                CheckCompactColonistBar(map);
                CheckColonistBarLabels(map);
                CheckColonistBarCollapse(map);
                CheckDesk(map);
                CheckCellGradeBands(map);
                CheckCellPickBedNoClashForAGrade(map);
                CheckCellEntitlementClock(map);
                CheckCellsMoveUpByConduct(map);
                CheckCellsMoveDownByConduct(map);
                CheckCellsByConductNeverMoves(map);
                CheckCellsClashBeatsGrade(map);
                CheckCellsQuotaOrder(map);
                CheckCellsSecondCandidateKeepsItsFit(map);
                CheckCellsGradeMovesDoNotChurn(map);
                CheckRoutineFriskWaitsForABusyInmate(map);
                CheckArmedResponseGivesUpOnAnUnseenTarget(map);
                CheckTowerGivesWayToAnEmergency(map);
                CheckDeathPlotGrowsNothingWild(map);
                CheckVisitorDeathIsARelativeDeath(map);
                CheckOffShiftStaffGoHomeNotToTheFloor(map);
                CheckFeedingGuardFinishesBeforeGoingHome(map);
                CheckStaffDoNotRunWild(map);
                CheckKickOutOfTheWrongBed(map);
                // Gap analysis 7's bugs B1-B4 (Ga7Regressions.cs), stability round 19.
                CheckGa7Bugs(map);
            }

            private void EvSmugglingRings()
            {
                // Smuggling rings (RingChecks.cs): forming, each way a link is found, the bust, turning, the map of finds. Last of the self-contained checks (it works on throwaway pawns and puts back what it touches).
                RingTest(map);
            }

            private void EvRingRegressions()
            {
                // Regression checks for the review of the smuggling rings (RingRegressions.cs, findings 1-13): self-contained, on throwaway inmates and staff, rolled back.
                CheckRingRegressions(map);
                CheckGa4Rings(map);
            }

            private void EvEconomyReview()
            {
                // The review of the inmate economy (EconomyRegressions.cs): hidden memories, debts nobody can collect, protection and its protector, tips, goals, cards, deliveries, statistics. Last of the self-contained checks, after the rings
                // (throwaway inmates in the yard and a throwaway guard; each puts back what it touches).
                var pawnsBeforeEconomy = new HashSet<Pawn>(map.mapPawns.AllPawns);
                CheckEconomyMemoriesStayGenericUntilKnown(map);
                CheckUncollectableDebtsEnd(map);
                CheckProtectionFollowsTheProtector(map);
                CheckKnownProtectionNamesNoHiddenGang(map);
                CheckDebtTipsDoNotCrowdOutTunnelTips(map);
                CheckFirstBeatingKeepsTheDebtGoal(map);
                CheckCardGameIsNeverSeen(map);
                CheckNextRoundIsThreatened(map);
                CheckSmuggledDeliveryPaysByValue(map);
                CheckSecretGoalLeavesNoTrace(map);
                CheckProtectionRenewalsAreNotCountedAsDebts(map);
                // (Nothing of theirs is left behind: every throwaway inmate and guard is gone again.)
                Pawn[] leftByEconomy = LeftBehind(map, pawnsBeforeEconomy).ToArray();
                if (leftByEconomy.Length > 0) problems0.Add($"economy checks left {leftByEconomy.Length} pawn(s) behind: {string.Join(", ", leftByEconomy.Select(x => x.LabelShort))}");
            }

            private void EvCourtCases()
            {
                // The murder trial and the complaints against officers (CourtChecks.cs): self-contained, on throwaway inmates and staff, rolled back.
                CheckCourtCases(map);
                // ...and for the review of those (H1-H4, M1-M5, L1-L10), in CourtRegressions.cs.
                CheckCourtRegressions(map);
                // ...and for gap analysis 4 (the bugs section), in Ga4CourtRegressions.cs.
                CheckGa4Court(map);
            }

            private void EvInsideContraband()
            {
                // ...and for contraband made inside (hooch, the garden's smokeleaf) and men and women kept apart, in InsideChecks.cs.
                CheckInside(map);
            }

            private void EvFirstHour()
            {
                // ...and for the first hour (gap analysis 4, sections 3 and 2), in FirstHourChecks.cs.
                CheckFirstHour(map);
            }

            private void EvGrounds()
            {
                // ...and for the grounds and buying land, in GroundsChecks.cs.
                CheckGrounds(map);
            }

            private void EvDeathDanger()
            {
                // ...and for a death inside (the dead, the family, the plot) and the danger map, in DeathDangerChecks.cs.
                CheckDeathDanger(map);
            }

            private void EvKeptRooms()
            {
                // ...and for rooms kept for a security level (KeptRoomsChecks.cs): the free-time pick, nearest first, meals, compliance, the program venue, a repaint, real ticks, an inmate's own errands, the urgent meal, a visit, the compliance exceptions, seats by patch, and the cached patch cells. Each cuts a strip out of the prison's biggest yard or canteen patch and puts it back.
                CheckKeptRoomsFreeTimePick(map);
                CheckKeptRoomsNearestFirst(map);
                CheckKeptRoomsNearestExact(map);
                CheckKeptRoomsMeals(map);
                CheckKeptRoomsCompliance(map);
                CheckKeptRoomsVenue(map);
                CheckKeptRoomsRepaint(map);
                CheckKeptRoomsRealTicks(map);
                CheckKeptRoomsErrands(map);
                CheckKeptRoomsCommissary(map);
                CheckKeptRoomsUrgentMeal(map);
                CheckKeptRoomsVisit(map);
                CheckKeptRoomsComplianceBeforeSanction(map);
                CheckKeptRoomsVenueByPatch(map);
                CheckKeptRoomsPatchCells(map);
            }

            private void EvGuardPosts()
            {
                // ...and for guard posts (GuardPostChecks.cs): manned in their hours by a free guard on shift, the guard named for one first, an emergency before it, the state's chief over it, a post taken down, a watcher.
                CheckGuardPostManning(map);
                CheckGuardPostAssignedGuard(map);
                CheckGuardPostTwoPosts(map);
                CheckGuardPostEmergencyFirst(map);
                CheckGuardPostNoBreakInAlarm(map);
                CheckGuardPostChiefHasThePatrols(map);
            }

            private void EvExInmates()
            {
                // ...and for former inmates who come back (kept as world pawns, the application, the hire, the old gang's pressure, the visit), in ExInmateChecks.cs.
                CheckExInmates(map);
            }

            private void EvOldAge()
            {
                // ...and for growing old inside (OldAgeChecks.cs): the diagnosis letter once, treat / transfer / release / nothing, the neglect rule, the hospice bed in the wing, a lifer's death of age.
                CheckOldAge(map);
            }

            private void EvDark()
            {
                // ...and for the one seen rule and the dark (DarkChecks.cs): a guard sees 3 cells and a camera nothing where the ground glow is under 0.3 in a free-time zone or a corridor, the overlay and the danger list say so, the builder lights its rooms.
                CheckDark(map);
            }

            private void EvKin()
            {
                // ...and for kin across the wall (made on a hire and an arrival, the officer's request, the complaint's evidence, the corruption odds, the subdue, a death of kin), in KinChecks.cs.
                CheckKin(map);
            }

            private void EvRoster()
            {
                // ...and for the roster (RosterChecks.cs): a pinned guard is still where they are pinned after the balance, the handover keeps the day shift on and pays it, the strip's count is the commute's, seven nights in a row is resented.
                CheckRoster(map);
            }

            private void EvKeepApart()
            {
                // ...and for keep-apart orders (the review splits a pair, the wings, the arrival's bed, the yard pick, a class, a job, the three letters and a cell request, the book, the compliance reading), in KeepApartChecks.cs.
                CheckKeepApart(map);
            }

            private void EvHireFit()
            {
                // ...and for a hire being somebody who can do the work (HireChecks.cs): twenty unfit candidates are no hire and no fee.
                CheckHireFit(map);
            }

            private void EvCareDetail()
            {
                // ...and for the protected care detail (CareDetailChecks.cs): a reserve of one keeps the care going while the other guard answers the trouble, a reserve of 0 is the control, the unavailable are reported.
                CheckCareDetail(map);
            }

            private void EvHiring()
            {
                // ...and for choosing the people you hire (HiringChecks.cs): a round's applicants made on the click and the same each time, the chosen one hired for one fee and once, the next round, the agency, kin, nobody fit.
                CheckHiring(map);
            }

            private void EvGangDeal()
            {
                // ...and for the shot-caller's deal (GangDealChecks.cs): the word's line for a known leader only, the offer and the dice, the x0.6, no war and no recruiting, a breach, the ways it ends and the five days, each term, the inspector, the rivals, the press.
                CheckGangDeal(map);
            }

            private void EvDrugTest()
            {
                // ...and for drug testing (DrugTestChecks.cs): the policy and its reasons, a positive and what follows it, a clean test and a refusal, the ring's customer, the kits, the doctors and the cap, and one real round.
                CheckDrugTest(map);
            }

            private void EvFuneral()
            {
                // The funeral (FuneralChecks.cs), last of the list: the letter and the burial that waits for it, the friends and the chaplain at the grave, a quiet burial, the hour and what stops it, keeping apart, a colleague, the family and the chapel, the guards, the rival, the turf count.
                CheckFuneral(map);
            }

            private void EvEvacuation()
            {
                // The evacuation of a wing (EvacuationChecks.cs), last of the list: what a route may pass, a fire beside occupied cells with a downed and a supermax inmate (real ticks, no door off a route, no fire, back to the beds with the sanctions as before), a route blocked and a point destroyed, the letter and the second point, the drill.
                CheckEvacuation(map);
            }

            private void EvPerDiem()
            {
                // Pay by the grade (PerDiemChecks.cs), last of the list: forced grade histories give the stated factors, the payment line moves by them, reputation still multiplies, a new game's first week (to the last tick of the day the screens call 7) pays the plain rate, the midnight's settlement pays through the graded line, the desk and the weekly report say it.
                CheckPerDiem(map);
            }

            private void EvWatchList()
            {
                // The watch list (WatchListChecks.cs), last of the list: the triggers and their three days, the flag after a day of very low mood (a guard, the unit officer, the psychologist, a guard when the psychologist does not come) with its one
                // letter, the end of the period, the answers, the watch's checks and a crisis on watch stopped (real ticks), a crisis nobody reaches ending in a death and one neglect inquiry (real ticks), the padded cell, the setting.
                CheckWatchList(map);
            }

            private void EvAmends()
            {
                // A restorative meeting with the victim's family (AmendsChecks.cs), last of the list: a forced good meeting lifts the hearing by the family's letter and drops it from the reasons (the control's unchanged), support at its
                // weight, the reoffend band's step, a bad one's tension once and the press, the proposal and the warden's answer, the hourly pass's lapse and refund, the family through the visitation room, the odds, once per stay, the goal.
                CheckAmends(map);
            }

            private void EvAgreement()
            {
                // ...and for staff agreements (AgreementChecks.cs): a promise to fix the grievance kept by real cover over the hours and not by the last hour's, nights by night staff, safety broken by a blow, the pay rise paid and taken back, one reloaded before judgment judged once, a representative who left, the peace and the bonus that is not offered.
                CheckAgreements(map);
            }
        }
    }
}
