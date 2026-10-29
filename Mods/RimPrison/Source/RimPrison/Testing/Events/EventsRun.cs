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
        /// <summary>
        /// The events plan's one-shot burst (called once, two hours in): every self-contained check, every prison incident fired, the decision letters answered and the
        /// rest of the checks that need the prison as the incidents left it. One instance holds what the blocks share (<see cref="map"/>, <see cref="g"/>, a guard and a
        /// wing's inmates); each block is its own method, so a new block's locals cannot clash with another's (the old one-scope method's CS0136 gotcha).
        /// </summary>
        private sealed partial class EventsRun
        {
            private readonly Map map;
            /// <summary>The prison's game state, taken in the winter jackets block (every block after it reads it).</summary>
            private PrisonGame g;
            /// <summary>A guard on the map, taken in the released-and-downed block and used by the riot gear, stash, phone tap and in-bed joy blocks.</summary>
            private Pawn guard;
            /// <summary>The active inmates outside a story arc, taken in the feuds block and used again by the friends block.</summary>
            private List<Pawn> wing;

            public EventsRun(Map map) { this.map = map; }

            /// <summary>Every block in the order it has always fired in: the order matters (see the comments on the blocks).</summary>
            public void Run()
            {
                // Self-contained checks that need several able inmates first, before the incidents and the checks below put
                // inmates in the hole, down them or tie them up (an events-plan prison has only eight cells).
                EvParoleScene();
                EvStateOversight();
                EvGangTurf();
                EvGangWar();
                EvHostages();
                EvEconomy();
                EvCells();
                EvWants();
                EvCellRules();
                EvParallelPrograms();
                EvMail();
                EvReviewRegressionsB2ToB16();
                EvReviewRegressionsB8ToB32();
                EvBigPrisonFeedingRegressions();
                EvMailClerkRegressions();
                EvLibraryAndExercise();
                EvParoleReviewRegressions();
                EvSmugglingRings();
                EvRingRegressions();
                EvEconomyReview();
                EvCourtCases();
                EvInsideContraband();
                EvFirstHour();
                EvGrounds();
                EvDeathDanger();
                EvGuardPosts();
                EvCount();
                EvExInmates();
                EvOldAge();
                EvDark();
                EvKeptRooms();
                EvKin();
                EvRoster();
                EvKeepApart();
                EvHireFit();
                EvCareDetail();
                EvHiring();
                EvGangDeal();
                EvDrugTest();
                EvAgreement();
                EvFireIncidents();
                EvAnswerDecisionLetters();
                EvSpecialArrivalsKeepToTheirLevel();
                EvTransferWithNoBedPaysNothing();
                EvWinterJackets();
                EvCellmateFit();
                EvManagedCells();
                EvGraduatedSanctions();
                EvHomeBedNeverOfferedFree();
                EvDemands();
                EvReleasedAndDownedAreNotRescued();
                EvRiotGearKeepsTheirHat();
                EvEscapeAndRecapture();
                EvVisitPolicies();
                EvFearAndProtectiveCustody();
                EvWingLockdown();
                EvSmugglingMergesStacks();
                EvCellStash();
                EvPhoneTaps();
                EvBreachClassification();
                EvReclassification();
                EvRiotJoiners();
                EvInBedJoy();
                EvCorruption();
                EvFeuds();
                EvFriendsStepIn();
                EvFriendsInOpinion();
                EvFamiliesHaveNames();
                EvPress();
                EvStaffHurtByAnInmate();
                EvOrphanedCorruptGuard();
                EvParoleAskedOfTheWarden();
                EvReoffender();
                EvDeathAftermath();
                EvStoryBeats();
                EvHazardPayIntegrity();
                EvStaffLives();
                EvOverdose();
                EvGangTruce();
                EvRookieConned();
                EvFlu();
                EvInnocence();
                EvInspection();
                EvBlackout();
                EvMedicalEmergency();
                EvFoodScare();
                EvExportContract();
                EvMentor();
                EvStaffMorale();
                EvKitchenFire();
                EvStaffTraining();
                EvStaffRoom();
                EvDangerMemories();
                EvGrants();
                EvDeathOnTheStaff();
                EvLifeAfterRelease();
                EvCoverageOverlay();
                EvInmatesRememberStaff();
                EvInmateFileStatusLines();
                EvOneLeaderPerGang();
                EvDoubleAgentBurnt();
                EvContrabandAmnesty();
                EvRemission();
                EvSupervisedDetox();
                EvFamilyDay();
                EvOfficerTooClose();
                EvOffShiftStaff();
                EvKitchenShift();
                EvScuffleNeverAcrossWalls();
                EvPastimes();
                EvOfficerOfTheMonth();
                EvInformantOffThePayroll();
                EvKitchenMenu();
                EvHeatWaveOrColdSnap();
                EvVisitorGoesHomeWhenTheHostIsLaidUp();
                EvWeeklyReport();
                EvStatisticsTable();
                EvNewAlerts();
                EvHiddenTensionCause();
                EvCareers();
                EvShuttleChecks();
                EvSupermax();
                EvHungerStrike();
                EvFuneral();
                EvSuppression();
                EvInspector();
                EvRegimeFeedback();
                EvToolCount();
                EvAdjudication();
                EvClassBooking();
                EvHandover();
                EvCustodyContract();
                EvReleasePrep();
                EvInformantTask();
                EvRiotReview();
                EvUnitOfficer();
                EvEvacuation();
                EvRumours();
                EvStandingOrders();
                EvPerDiem();
                EvWardenAsks();
                EvWatchList();
                EvAmends();
            }

        }
    }
}
