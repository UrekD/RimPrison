using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of special measures (findings 1-13), each its own method (its names stay its own) with its own rollback, run
    /// through <see cref="LeakChecked"/> so that none leaves anything behind: the end-of-run "three bad days and the state never stepped in" check
    /// reads <see cref="PrisonStats.measuresStarted"/>, which a leaked counter from these checks would hide. Each is driven through the real midnight
    /// pass (<see cref="SpecialMeasures.Daily"/>) on a throwaway <see cref="StateOversight"/>, the game's own put back after.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>What these checks could leave behind, as one string: the counters they move, the money, the grades, the projects, the oversight and the people on the staff.</summary>
        private static string MeasuresFootprint(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonStats s = g.stats;
            var sb = new StringBuilder();
            sb.Append($"measures={s.measuresStarted}/{s.measuresLifted}/{s.measuresFailed}/{s.flagshipsEarned}/{s.legendsArrived} escaped={s.escaped} admitted={s.admitted} grantsDone={s.grantsCompleted} ");
            sb.Append($"funds={g.ledger.funds} rep={g.reputation:F2} loan={g.story.loanOwed}/{g.story.debtDays} grade={g.lastGrade:F1}/[{string.Join(",", g.gradeHistory)}] ");
            sb.Append($"bur={g.bureaucracy.done.Count}/{g.bureaucracy.active?.defName}/{g.bureaucracy.progress:F2} armed={g.armedResponse} ");
            sb.Append($"intake={g.story.admissions}/{string.Join(",", g.intake.perDay.OrderBy(kv => kv.Key.defName).Select(kv => kv.Key.defName + ":" + kv.Value))} ");
            sb.Append($"oversight={SpecialMeasures.Describe(g.story.oversight)} ");
            sb.Append($"staff={string.Join(",", StaffCommute.AllStaff(map).Select(p => p.thingIDNumber).OrderBy(i => i))}");
            return sb.ToString();
        }

        /// <summary>Runs a check and fails the run if it left the prison different from how it found it.</summary>
        private static void LeakChecked(string name, Map map, Action<Map> check)
        {
            string before = MeasuresFootprint(map);
            check(map);
            string after = MeasuresFootprint(map);
            if (before != after) problems0.Add($"{name} left the prison changed: {before} -> {after}");
        }

        /// <summary>The whole group, each check on its own, then the probe's own check (it must be able to see a leak, or its silence means nothing).</summary>
        private static void RunMeasuresChecks(Map map)
        {
            LeakChecked("special measures test", map, MeasuresTest);
            LeakChecked("special measures: failing is worse", map, CheckMeasuresFailingIsWorse);
            LeakChecked("special measures: the chief taken hostage", map, CheckMeasuresChiefHostage);
            LeakChecked("special measures: the chief cannot be drafted", map, CheckMeasuresChiefCannotBeDrafted);
            LeakChecked("special measures: the chief's death", map, CheckMeasuresChiefDeathIsNoWayOut);
            LeakChecked("special measures: the chief at home", map, CheckMeasuresChiefAtHome);
            LeakChecked("special measures: recalling the last colonist", map, CheckMeasuresRecallNeverEndsTheGame);
            LeakChecked("special measures: the half-way visit of an extended term", map, CheckMeasuresHalfWayVisitAfterExtension);
            LeakChecked("special measures: the solitary cap", map, CheckMeasuresSolitaryCapIsOnlyForSolitary);
            LeakChecked("special measures: lifting needs the cause put right", map, CheckMeasuresLiftNeedsTheCause);
            LeakChecked("special measures: the chief is not an employee", map, CheckMeasuresChiefIsNotAnEmployee);
            LeakChecked("special measures: armed response where it is used", map, CheckMeasuresArmedResponseWhereItIsUsed);
            LeakChecked("special measures: flagship after the lift", map, CheckMeasuresNoFlagshipOnTheLiftingNight);
            LeakChecked("special measures: the overseer left", map, CheckMeasuresStaleOverseer);
            CheckBureaucracyLoseLatestKeepsWhatIsBuiltOn();
            CheckMeasuresFootprintSeesALeak(map);
        }

        /// <summary>The probe is only worth its silence if it speaks when something is left behind: leak a counter on purpose and see it.</summary>
        private static void CheckMeasuresFootprintSeesALeak(Map map)
        {
            PrisonStats s = PrisonGame.Instance.stats;
            string before = MeasuresFootprint(map);
            s.measuresStarted++;
            bool seen = MeasuresFootprint(map) != before;
            s.measuresStarted--;
            if (!seen || MeasuresFootprint(map) != before) problems0.Add($"special measures: the leak probe did not see a leaked counter ({seen})");
        }

        // ---- shared set-up ----------------------------------------------------------------------------------------------

        /// <summary>A new oversight for a check, with the game's own, the state's pawns it brought and the grades, projects and loan the check moves, put back on rollback.</summary>
        private static StateOversight MeasuresState(Rollback r, Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            StateOversight real = g.story.oversight;
            HashSet<int> pawnsBefore = map.mapPawns.AllPawnsSpawned.Concat(g.OffSite).Select(p => p.thingIDNumber).ToHashSet();
            List<float> history0 = g.gradeHistory.ToList();
            float lastGrade0 = g.lastGrade;
            (int loan0, int debtDays0) = (g.story.loanOwed, g.story.debtDays);
            List<BureaucracyDef> done0 = g.bureaucracy.done.ToList();
            (BureaucracyDef active0, float progress0) = (g.bureaucracy.active, g.bureaucracy.progress);
            ArmedResponsePolicy armed0 = g.armedResponse;
            List<StoryArc> arcs0 = g.story.arcs.ToList();
            var lastArc0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
            var state = new StateOversight();
            g.story.oversight = state;
            // (No loan in default unless a check makes one: whatever the prison owes says nothing about the state the check is about.)
            (g.story.loanOwed, g.story.debtDays) = (0, 0);
            r.Add(() =>
            {
                g.story.oversight = real;
                DismissNewPeople(map, pawnsBefore);
                g.gradeHistory.Clear();
                g.gradeHistory.AddRange(history0);
                g.lastGrade = lastGrade0;
                (g.story.loanOwed, g.story.debtDays) = (loan0, debtDays0);
                g.bureaucracy.done.Clear();
                g.bureaucracy.done.AddRange(done0);
                (g.bureaucracy.active, g.bureaucracy.progress) = (active0, progress0);
                g.armedResponse = armed0;
                g.story.arcs.Clear();
                g.story.arcs.AddRange(arcs0);
                g.story.lastArcStart.Clear();
                foreach (KeyValuePair<StoryArcDef, int> kv in lastArc0) g.story.lastArcStart[kv.Key] = kv.Value;
            });
            return state;
        }

        /// <summary>
        /// The people a check brought (an overseer, chiefs, a dead chief's corpse) go, whoever holds them: on the map, at home or lying dead. Only people who were not
        /// here before the check: every human has the staff component, the prison's inmates and staff included.
        /// </summary>
        private static void DismissNewPeople(Map map, HashSet<int> pawnsBefore)
        {
            PrisonGame g = PrisonGame.Instance;
            var gone = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Concat(g.OffSite).ToList())
                if (p.GetComp<CompStaff>() != null && !pawnsBefore.Contains(p.thingIDNumber)) gone.Add(p);
            foreach (Corpse corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().ToList())
                if (corpse.InnerPawn?.GetComp<CompStaff>() != null && !pawnsBefore.Contains(corpse.InnerPawn.thingIDNumber))
                {
                    gone.Add(corpse.InnerPawn);
                    if (!corpse.Destroyed) corpse.Destroy();
                }
            foreach (Pawn p in gone)
            {
                g.home.Remove(p);
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            }
            StaffCareers.InvalidateLeads();
        }

        private static void SetGrades(params float[] grades)
        {
            PrisonGame.Instance.gradeHistory.Clear();
            PrisonGame.Instance.gradeHistory.AddRange(grades);
        }

        /// <summary>Three bad days and the midnight pass: the first term begins.</summary>
        private static void BeginMeasures(PrisonGame g, Map map, MeasuresCause cause = MeasuresCause.Grades)
        {
            switch (cause)
            {
                case MeasuresCause.Grades:
                    SetGrades(30f, 30f, 30f);
                    SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                    break;
                case MeasuresCause.Loan:
                    SetGrades(80f, 80f, 80f);
                    (g.story.loanOwed, g.story.debtDays) = (5000, SpecialMeasures.LoanDefaultDays);
                    SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                    break;
                default:
                    SetGrades(80f, 80f, 80f);
                    SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents { deaths = SpecialMeasures.WeeklyLimit(map) });
                    break;
            }
        }

        /// <summary>Ends the first term on its review day, then the extension on its own: with the targets not met, the second review fails the measures.</summary>
        private static void FailMeasures(PrisonGame g, Map map, StateOversight o)
        {
            for (int term = 0; term < 2; term++)
            {
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
            }
        }

        /// <summary>At least <paramref name="n"/> finished projects with nothing built on any of them, for the state to take back.</summary>
        private static void GiveProjects(PrisonGame g, int n)
        {
            g.bureaucracy.done.Clear();
            g.bureaucracy.active = null;
            g.bureaucracy.done.AddRange(DefDatabase<BureaucracyDef>.AllDefsListForReading.Where(d => d.prerequisites.Count == 0).Take(n));
        }

        private static int LettersLabelled(Rollback r, string key) => r.NewLetters.Count(l => l.Label.ToString() == key.Translate().ToString());

        // ---- 1: failing is worse than being under the measures, and shields the prison from nothing -------------------------------------

        private static void CheckMeasuresFailingIsWorse(Map map)
        {
            Guarded("special measures: failing is worse", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, failing: " + what);
                StateOversight o = MeasuresState(r, map);
                GiveProjects(g, 2);
                BeginMeasures(g, map);
                (int firstBus, float firstPay, ArmedResponsePolicy firstArmed, int firstHole) = (SpecialMeasures.IntakeLimit, SpecialMeasures.PaymentFactor,
                    SpecialMeasures.LimitArmed(ArmedResponsePolicy.EscapesAndViolence), SpecialMeasures.LimitSolitary(72));
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                int extendedBus = SpecialMeasures.IntakeLimit;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                // The term is over, the restrictions are not: nothing is better than the extended term, the bus is no more than it was.
                bool under = !o.active && o.underChief && o.ChiefInPost && SpecialMeasures.Restricted;
                bool noBetter = SpecialMeasures.IntakeLimit == 0 && SpecialMeasures.IntakeLimit <= extendedBus && SpecialMeasures.IntakeLimit <= firstBus
                                && Mathf.Approximately(SpecialMeasures.PaymentFactor, firstPay) && SpecialMeasures.LimitArmed(ArmedResponsePolicy.EscapesAndViolence) == firstArmed
                                && SpecialMeasures.LimitSolitary(72) == firstHole && firstPay < 1f && firstHole == SpecialMeasures.SolitaryCapHours;
                if (!under || !noBetter)
                    Fail($"after the second review: chief in post {under} (active {o.active}, under the chief {o.underChief}), restrictions no lighter than the measures' {noBetter} (bus {SpecialMeasures.IntakeLimit}, payments x{SpecialMeasures.PaymentFactor:F2}, armed {SpecialMeasures.LimitArmed(ArmedResponsePolicy.EscapesAndViolence)}, solitary {SpecialMeasures.LimitSolitary(72)} h)");
                // Middling grades while the chief is here: nothing lifts, nothing changes.
                SetGrades(60f, 60f, 60f);
                for (int i = 0; i < 3; i++) SpecialMeasures.Daily(g, map, MeasuresCard(60f), new DailyIncidents());
                Pawn chief = o.chief;
                if (!o.underChief || !o.ChiefInPost || !SpecialMeasures.Restricted || SpecialMeasures.IntakeLimit != 0) Fail("middling grades lifted the restrictions or took the chief back");
                // Not shielded: a record failing again, with the chief here, costs another project (once, then the grace), and the week of B grades starts over.
                o.goodDays = 4;
                o.graceUntilDay = Ledger.CurrentDay;
                int projects = g.bureaucracy.done.Count;
                SetGrades(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                bool tightened = g.bureaucracy.done.Count == projects - 1 && o.goodDays == 0 && o.graceUntilDay == Ledger.CurrentDay + SpecialMeasures.GraceDays && o.chief == chief && o.underChief
                                 && LettersLabelled(r, "RP_MeasuresTightenedLabel") == 1;
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                bool once = g.bureaucracy.done.Count == projects - 1 && LettersLabelled(r, "RP_MeasuresTightenedLabel") == 1;
                if (!tightened || !once) Fail($"a failing record with the chief in post: another project lost {tightened} (projects {g.bureaucracy.done.Count} of {projects}, days of B {o.goodDays}), only once inside the grace {once}");
                // The other two causes tighten it too.
                g.bureaucracy.done.Clear();
                GiveProjects(g, 2);
                int before = g.bureaucracy.done.Count;
                o.graceUntilDay = Ledger.CurrentDay;
                SetGrades(80f, 80f, 80f);
                o.weekLog = Enumerable.Repeat(0, 6).Concat(new[] { SpecialMeasures.WeeklyLimit(map) }).ToList();
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool byIncidents = g.bureaucracy.done.Count == before - 1 && o.cause == MeasuresCause.Incidents;
                o.weekLog.Clear();
                o.graceUntilDay = Ledger.CurrentDay;
                (g.story.loanOwed, g.story.debtDays) = (5000, SpecialMeasures.LoanDefaultDays);
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool byLoan = g.bureaucracy.done.Count == before - 2 && o.cause == MeasuresCause.Loan;
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                if (!byIncidents || !byLoan) Fail($"deaths and escapes over the limit with the chief in post took another project {byIncidents}, a loan in default {byLoan}");
                // The week of B grades takes the chief back, and the restrictions with them.
                o.goodDays = SpecialMeasures.ChiefRecallDays - 1;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool recalled = !o.underChief && !o.ChiefInPost && !SpecialMeasures.Restricted && SpecialMeasures.IntakeLimit == int.MaxValue && Mathf.Approximately(SpecialMeasures.PaymentFactor, 1f)
                                && SpecialMeasures.LimitSolitary(72) == 72 && SpecialMeasures.LimitArmed(ArmedResponsePolicy.EscapesAndViolence) == ArmedResponsePolicy.EscapesAndViolence;
                if (!recalled) Fail($"a week of B grades: the chief recalled {!o.ChiefInPost}, the restrictions ended {!SpecialMeasures.Restricted}");
                RPLog.Message($"[RimPrison:test] special measures, failing: restrictions held {under && noBetter}, tightened {tightened && once}, by incidents/loan {byIncidents}/{byLoan}, recalled {recalled}");
            });
        }

        // ---- 2: a chief taken hostage gets the crisis button ----------------------------------------------------------------------------

        private static void CheckMeasuresChiefHostage(Map map)
        {
            Guarded("special measures: the chief taken hostage", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                void Fail(string what) => problems0.Add("special measures, hostage chief: " + what);
                if (HostageCrisis.Running() != null)
                {
                    Fail("could not run (a crisis is already running)");
                    return;
                }
                IntVec3 canteenCell = PrisonBuilder.CanteenCell(map);
                Room canteen = canteenCell.GetRoom(map);
                List<IntVec3> spots = GenRadial.RadialCellsAround(canteenCell, 6f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == canteen && c.GetEdifice(map) == null).ToList();
                if (HostageCrisis.HoldRoom(canteenCell, map) == null || spots.Count < 3)
                {
                    Fail($"could not run (the canteen is not a room the rioters could hold: {spots.Count} cells to stand on)");
                    return;
                }
                StateOversight o = MeasuresState(r, map);
                Pawn chief = SpecialMeasures.InstallChief(map, o);
                Pawn leader = null;
                for (int attempt = 0; attempt < 8 && leader == null; attempt++)
                {
                    Pawn candidate = Throwaway(map, r);
                    if (!candidate.WorkTagIsDisabled(WorkTags.Violent)) leader = candidate;
                }
                if (chief == null || leader == null)
                {
                    Fail($"could not run (a chief {chief != null}, a ringleader {leader != null})");
                    return;
                }
                StoryArcDef arcDef = RPHostageDefOf.RP_Arc_Hostage;
                bool hadStart = g.story.lastArcStart.TryGetValue(arcDef, out int start0);
                (int drama0, int beats0, int lastRiot0) = (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick);
                (NeedDef need0, int deadline0, int spokes0, string spokesName0, int cooldown0) = (g.demand.need, g.demand.deadlineTick, g.demand.spokesId, g.demand.spokesName, g.demand.cooldownUntilTick);
                g.story.lastArcStart.Remove(arcDef);
                foreach (Pawn p in new[] { chief, leader })
                {
                    p.pather?.StopDead();
                    p.jobs?.StopAll();
                }
                chief.Position = spots[0];
                chief.Notify_Teleported();
                leader.Position = spots[1];
                leader.Notify_Teleported();
                StoryArc arc = HostageCrisis.Seize(map, leader, leader.Inmate(), new Pawn[0], chief, HostageCrisis.Riot);
                r.Add(() =>
                {
                    if (arc is { done: false } && HostageCrisis.IsHeld(chief)) ((HostageArc)arc.def.Worker).Finish(arc, map, HostageHow.Talked, HostageFate.Freed, "the test");
                    HostageCrisis.Release(chief);
                    pm.ForceDoorUpdate();
                    if (hadStart) g.story.lastArcStart[arcDef] = start0;
                    else g.story.lastArcStart.Remove(arcDef);
                    (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick) = (drama0, beats0, lastRiot0);
                    (g.demand.need, g.demand.deadlineTick, g.demand.spokesId, g.demand.spokesName, g.demand.cooldownUntilTick) = (need0, deadline0, spokes0, spokesName0, cooldown0);
                });
                if (arc == null)
                {
                    Fail("the crisis did not start with the chief as the hostage");
                    return;
                }
                List<Gizmo> gizmos = chief.GetComp<CompStaff>().CompGetGizmosExtra().ToList();
                string decideLabel = "RP_Hostage_Decide".Translate().ToString();
                bool button = gizmos.OfType<Command_Action>().Any(c => !c.Disabled && c.defaultLabel == decideLabel);
                bool lockStays = gizmos.Any(c => c is Command { Disabled: true });
                bool noPatrol = gizmos.Count == 2;
                if (!button || !lockStays || !noPatrol)
                    Fail($"a chief held hostage: the crisis button {button}, the locked state-appointee gizmo still there {lockStays}, nothing else (patrol zone) {noPatrol} ({gizmos.Count} gizmos: {string.Join(", ", gizmos.OfType<Command>().Select(c => c.defaultLabel))})");
                RPLog.Message($"[RimPrison:test] special measures, hostage chief: button={button} lock={lockStays} gizmos={gizmos.Count}");
            });
        }

        // ---- 3: the warden cannot draft the chief ---------------------------------------------------------------------------------------

        private static void CheckMeasuresChiefCannotBeDrafted(Map map)
        {
            Guarded("special measures: the chief cannot be drafted", map, r =>
            {
                void Fail(string what) => problems0.Add("special measures, drafting the chief: " + what);
                StateOversight o = MeasuresState(r, map);
                Pawn chief = SpecialMeasures.InstallChief(map, o);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (chief?.drafter == null || guard.drafter == null)
                {
                    Fail($"could not run (a chief {chief != null}, a draft controller {chief?.drafter != null}/{guard.drafter != null})");
                    return;
                }
                // The control: an ordinary guard drafts and undrafts.
                bool guardButton = guard.drafter.ShowDraftGizmo;
                guard.drafter.Drafted = true;
                bool guardDrafted = guard.drafter.Drafted;
                guard.drafter.Drafted = false;
                bool chiefButton = chief.drafter.ShowDraftGizmo;
                chief.drafter.Drafted = true;
                bool chiefDrafted = chief.drafter.Drafted;
                chief.drafter.Drafted = false;
                if (!guardButton || !guardDrafted) problems0.Add($"special measures, drafting the chief: the check could not draft an ordinary guard (button {guardButton}, drafted {guardDrafted})");
                if (chiefButton || chiefDrafted) Fail($"the state's chief: the draft button shows {chiefButton}, they were drafted {chiefDrafted}");
                RPLog.Message($"[RimPrison:test] special measures, drafting: guard button={guardButton} drafted={guardDrafted}; chief button={chiefButton} drafted={chiefDrafted}");
            });
        }

        // ---- 3: a chief's death is not a free way out ---------------------------------------------------------------------------------------

        private static void CheckMeasuresChiefDeathIsNoWayOut(Map map)
        {
            Guarded("special measures: the chief's death", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, the chief's death: " + what);
                StateOversight o = MeasuresState(r, map);
                GiveProjects(g, 1);
                BeginMeasures(g, map);
                FailMeasures(g, map, o);
                Pawn first = o.chief;
                if (first == null || !o.underChief)
                {
                    Fail("could not run (the measures were not failed)");
                    return;
                }
                // The chief is killed (the real death: a corpse, the staff-death letter), a day short of the week of B grades that would take them back.
                o.goodDays = SpecialMeasures.ChiefRecallDays - 1;
                first.Kill(null);
                bool deadNotInPost = first.Dead && !o.ChiefInPost;
                bool heldMeanwhile = o.underChief && SpecialMeasures.Restricted && SpecialMeasures.IntakeLimit == 0 && SpecialMeasures.PaymentFactor < 1f;
                int graceBefore = o.graceUntilDay;
                int replaced0 = LettersLabelled(r, "RP_ChiefReplacedLabel");
                SetGrades(80f, 80f, 80f);
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                Pawn second = o.chief;
                bool replaced = second != null && second != first && second.Spawned && o.ChiefInPost && o.underChief && o.goodDays == 0 && !o.active && SpecialMeasures.Restricted
                                && o.graceUntilDay == graceBefore && LettersLabelled(r, "RP_ChiefReplacedLabel") == replaced0 + 1
                                && r.NewEntries.Any(e => e.text == "RP_Chr_ChiefReplaced".Translate(first.LabelShort, second.LabelShort, SpecialMeasures.ChiefRecallDays).Resolve());
                if (!deadNotInPost || !heldMeanwhile || !replaced)
                    Fail($"a killed chief: no longer in post {deadNotInPost}, restrictions held until the next midnight {heldMeanwhile}, replaced with the days of B starting over {replaced} (days of B {o.goodDays}, a new chief {second != null && second != first}, still restricted {SpecialMeasures.Restricted})");
                // One who vanishes (left, destroyed) is replaced just the same.
                if (second != null)
                {
                    second.Destroy(DestroyMode.Vanish);
                    SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                    Pawn third = o.chief;
                    if (third == null || third == second || !o.ChiefInPost || !o.underChief || o.goodDays != 0) Fail("a chief who vanished was not replaced");
                }
                RPLog.Message($"[RimPrison:test] special measures, the chief's death: dead={deadNotInPost} held={heldMeanwhile} replaced={replaced}");
            });
        }

        // ---- 7: a chief who commutes ------------------------------------------------------------------------------------------------------------

        private static void CheckMeasuresChiefAtHome(Map map)
        {
            Guarded("special measures: the chief at home", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, the chief at home: " + what);
                StateOversight o = MeasuresState(r, map);
                Pawn chief = SpecialMeasures.InstallChief(map, o);
                if (chief == null)
                {
                    Fail("could not run (no chief)");
                    return;
                }
                StaffCommute.Leave(chief);
                bool home = StaffCommute.AtHome(chief) && !chief.Spawned;
                // Midnight with the chief off site: they are still the state's, nobody is sent in their place, the restrictions stay.
                SetGrades(60f, 60f, 60f);
                int replaced0 = LettersLabelled(r, "RP_ChiefReplacedLabel");
                SpecialMeasures.Daily(g, map, MeasuresCard(60f), new DailyIncidents());
                bool stays = o.chief == chief && o.ChiefInPost && o.underChief && SpecialMeasures.Restricted && LettersLabelled(r, "RP_ChiefReplacedLabel") == replaced0;
                // The warden cannot let them go from home either; the state can recall them there.
                StaffUtility.Fire(chief);
                bool notFired = chief.GetComp<CompStaff>().role != null && StaffCommute.AtHome(chief);
                o.goodDays = SpecialMeasures.ChiefRecallDays - 1;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool recalled = o.chief == null && !o.underChief && chief.GetComp<CompStaff>().role == null && !StaffCommute.AtHome(chief) && !SpecialMeasures.Restricted;
                if (!home || !stays || !notFired || !recalled)
                    Fail($"sent home {home}, still the state's and not replaced at midnight {stays}, not fired by the warden {notFired}, recalled from home after a week of B {recalled}");
                RPLog.Message($"[RimPrison:test] special measures, the chief at home: home={home} stays={stays} notFired={notFired} recalled={recalled}");
            });
        }

        // ---- 12: recalling the chief can never start vanilla's game-over countdown ---------------------------------------------------------

        private static void CheckMeasuresRecallNeverEndsTheGame(Map map)
        {
            Guarded("special measures: recalling the last colonist", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, recalling the last colonist: " + what);
                StateOversight o = MeasuresState(r, map);
                Pawn chief = SpecialMeasures.InstallChief(map, o);
                Pawn other = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (chief == null)
                {
                    Fail("could not run (no chief)");
                    return;
                }
                // Who counts: the chief alone does not, the chief and another does.
                bool alone = !SpecialMeasures.OthersRemain(chief, new[] { chief });
                bool withOther = SpecialMeasures.OthersRemain(chief, new[] { chief, other });
                bool real = SpecialMeasures.OthersRemain(chief);
                o.goodDays = SpecialMeasures.ChiefRecallDays;
                // The chief is the last colonist: the week of B grades does not recall them (the countdown would begin), and nothing else changes.
                int letters0 = LettersLabelled(r, "RP_ChiefRecalledLabel");
                SpecialMeasures.RecallChief(g, map, o, new[] { chief });
                bool kept = o.chief == chief && o.ChiefInPost && o.underChief && SpecialMeasures.Posted(chief) && chief.Spawned && o.graceUntilDay < Ledger.CurrentDay
                            && LettersLabelled(r, "RP_ChiefRecalledLabel") == letters0 && !Find.GameEnder.gameEnding;
                // Others on the staff: the same recall goes ahead.
                SpecialMeasures.RecallChief(g, map, o);
                bool recalled = o.chief == null && !o.underChief && !SpecialMeasures.Posted(chief) && chief.GetComp<CompStaff>().role == null && LettersLabelled(r, "RP_ChiefRecalledLabel") == letters0 + 1;
                if (!alone || !withOther || !real || !kept || !recalled)
                    Fail($"the last colonist counted as others {!alone}, another colonist not counted {!withOther}, the real colonists not counted {!real}; recall refused for the last colonist {kept}, done with others about {recalled}");
                RPLog.Message($"[RimPrison:test] special measures, the last colonist: alone={alone} withOther={withOther} kept={kept} recalled={recalled}");
            });
        }

        // ---- 4: the half-way visit of an extended term -----------------------------------------------------------------------------------------

        private static void CheckMeasuresHalfWayVisitAfterExtension(Map map)
        {
            Guarded("special measures: the half-way visit of an extended term", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, the extended term's visit: " + what);
                StateOversight o = MeasuresState(r, map);
                BeginMeasures(g, map);
                // The review, seven days on (the days are written, the way the plan moves the clock).
                (o.startDay, o.deadlineDay) = (o.startDay - SpecialMeasures.TermDays, o.deadlineDay - SpecialMeasures.TermDays);
                o.midVisitDone = true;
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                bool extended = o.active && o.extensions == 1 && o.startDay == Ledger.CurrentDay && !o.midVisitDone;
                // The next night is the first of the new term: no progress report yet.
                int reports0 = LettersLabelled(r, "RP_MeasuresProgressLabel");
                (o.startDay, o.deadlineDay) = (o.startDay - 1, o.deadlineDay - 1);
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                bool early = !o.midVisitDone && LettersLabelled(r, "RP_MeasuresProgressLabel") == reports0;
                // Half way into it (day 3), the report comes.
                (o.startDay, o.deadlineDay) = (o.startDay - 2, o.deadlineDay - 2);
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                bool onTime = o.midVisitDone && LettersLabelled(r, "RP_MeasuresProgressLabel") == reports0 + 1 && SpecialMeasures.DaysLeft(o) == SpecialMeasures.TermDays - 3;
                if (!extended || !early || !onTime)
                    Fail($"extension began a new term {extended} (start {o.startDay - Ledger.CurrentDay}), no report on the first night {early}, the report half way {onTime} ({SpecialMeasures.DaysLeft(o)} days left)");
                RPLog.Message($"[RimPrison:test] special measures, extended term's visit: extended={extended} early={early} onTime={onTime}");
            });
        }

        // ---- 5: the cap is for solitary only ----------------------------------------------------------------------------------------------------

        private static void CheckMeasuresSolitaryCapIsOnlyForSolitary(Map map)
        {
            Guarded("special measures: the solitary cap", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, the solitary cap: " + what);
                MisconductDef def = DefDatabase<MisconductDef>.AllDefsListForReading.FirstOrDefault(d => d.sanctionAs == null && d.punishmentHours > SpecialMeasures.SolitaryCapHours);
                if (def == null)
                {
                    Fail("could not run (no misconduct with solitary beyond a day)");
                    return;
                }
                StateOversight o = MeasuresState(r, map);
                PunishmentPolicy policy = g.punishments;
                var hours0 = new Dictionary<MisconductDef, int>(policy.hours);
                var kinds0 = new Dictionary<MisconductDef, Sanction>(policy.kinds);
                r.Add(() =>
                {
                    policy.hours.Clear();
                    foreach (KeyValuePair<MisconductDef, int> kv in hours0) policy.hours[kv.Key] = kv.Value;
                    policy.kinds.Clear();
                    foreach (KeyValuePair<MisconductDef, Sanction> kv in kinds0) policy.kinds[kv.Key] = kv.Value;
                });
                BeginMeasures(g, map);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                int Now() => Find.TickManager.TicksGame;
                policy.Set(def, 72);
                // A visit ban of 72 hours is a visit ban of 72 hours, and the policy screen shows the 72 that was set.
                policy.SetKind(def, Sanction.LoseVisits);
                c.visitBanUntilTick = -1;
                int now = Now();
                Sanctions.Apply(p, c, def);
                bool lighterUncapped = c.visitBanUntilTick - now == 72 * GenDate.TicksPerHour && policy.HoursFor(def) == 72;
                // The same hours as solitary are capped at a day when the sentence is set, and when it comes due, though it was set before the measures.
                policy.SetKind(def, Sanction.Solitary);
                c.pendingPunishmentHours = 0;
                Sanctions.Apply(p, c, def);
                bool solitaryCapped = c.pendingPunishmentHours == SpecialMeasures.SolitaryCapHours && policy.HoursFor(def) == 72 && policy.SolitaryHoursFor(def) == SpecialMeasures.SolitaryCapHours;
                c.pendingPunishmentHours = 72;
                bool dueCapped = PunishmentUtility.HoursToServe(c) == SpecialMeasures.SolitaryCapHours;
                PunishmentUtility.Begin(p, c);
                bool served = c.forcedActivity != null && c.forcedUntilTick - Now() <= SpecialMeasures.SolitaryCapHours * GenDate.TicksPerHour;
                PunishmentUtility.End(p, c);
                // Once the measures are over, the 72 hours serve in full.
                o.active = false;
                c.pendingPunishmentHours = 72;
                bool liftedFull = PunishmentUtility.HoursToServe(c) == 72 && policy.SolitaryHoursFor(def) == 72;
                if (!lighterUncapped || !solitaryCapped || !dueCapped || !served || !liftedFull)
                    Fail($"a visit ban of 72 h under special measures is 72 h {lighterUncapped} (policy shows {policy.HoursFor(def)}), solitary capped when set {solitaryCapped}, a sentence already due capped {dueCapped}, served at most a day {served}, uncapped when lifted {liftedFull}");
                RPLog.Message($"[RimPrison:test] special measures, the solitary cap: lighter={lighterUncapped} set={solitaryCapped} due={dueCapped} served={served} lifted={liftedFull}");
            });
        }

        // ---- 9: lifting needs the cause put right, and the state warns ----------------------------------------------------------------------------

        private static void CheckMeasuresLiftNeedsTheCause(Map map)
        {
            Guarded("special measures: lifting needs the cause put right", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, the cause: " + what);
                StateOversight o = MeasuresState(r, map);
                int limit = SpecialMeasures.WeeklyLimit(map);
                int lifted0 = g.stats.measuresLifted;
                // Safety and no escapes are met in each case; whether the review lifts is up to the cause.
                // Grades: safety at C but the grade still D, so the term is extended; with the grade back at C it is lifted.
                BeginMeasures(g, map);
                SetGrades(30f, 30f, 30f);
                string gradesTarget = SpecialMeasures.CauseTarget(g, map, o);
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool gradesHeld = o.active && o.extensions == 1 && g.stats.measuresLifted == lifted0;
                SetGrades(80f, 80f);
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool gradesLifted = !o.active && g.stats.measuresLifted == lifted0 + 1;
                // Loan: in default still at the review, so no lift; paid up, lifted.
                o = MeasuresState(r, map);
                BeginMeasures(g, map, MeasuresCause.Loan);
                string loanTarget = SpecialMeasures.CauseTarget(g, map, o);
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool loanHeld = o.active && o.extensions == 1 && g.stats.measuresLifted == lifted0 + 1;
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool loanLifted = !o.active && g.stats.measuresLifted == lifted0 + 2;
                // The cycle the review feared: lifted, 5 days of grace, and the same default brings the state back. It cannot be lifted again without paying.
                (g.story.loanOwed, g.story.debtDays) = (5000, SpecialMeasures.LoanDefaultDays);
                o.graceUntilDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool noFarm = o.active && g.stats.measuresLifted == lifted0 + 2;
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                // Incidents: the week still over the limit at the review, no lift; a quiet week, lifted.
                o = MeasuresState(r, map);
                BeginMeasures(g, map, MeasuresCause.Incidents);
                string incidentsTarget = SpecialMeasures.CauseTarget(g, map, o);
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool incidentsHeld = o.active && o.extensions == 1 && g.stats.measuresLifted == lifted0 + 2;
                o.weekLog = Enumerable.Repeat(0, 7).ToList();
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(75f), new DailyIncidents());
                bool incidentsLifted = !o.active && g.stats.measuresLifted == lifted0 + 3;
                // The letters and the alert name the target.
                string targets = SpecialMeasures.Targets(g, map, o);
                bool named = targets.Contains(SpecialMeasures.CauseTarget(g, map, o)) && gradesTarget != loanTarget && loanTarget != incidentsTarget;
                if (!gradesHeld || !gradesLifted || !loanHeld || !loanLifted || !noFarm || !incidentsHeld || !incidentsLifted || !named)
                    Fail($"grade still D at the review kept the measures {gradesHeld}, grade back lifted {gradesLifted}; loan in default kept {loanHeld}, paid lifted {loanLifted}, no second lift for the same default {noFarm}; week over the limit kept {incidentsHeld}, quiet week lifted {incidentsLifted}; targets named {named}");
                // The warning before a trigger: two bad days, the week within two of the limit, a loan two nights short of default (and none when nothing is near, or under measures).
                o = MeasuresState(r, map);
                SetGrades(80f, 30f, 30f);
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                bool gradeWarn = SpecialMeasures.Watching(g, map, o).Count == 1 && SpecialMeasures.Trigger(g, map, o) == null;
                SetGrades(80f, 80f, 80f);
                bool quiet = SpecialMeasures.Watching(g, map, o).Count == 0;
                o.weekLog = Enumerable.Repeat(0, 6).Concat(new[] { limit - 1 }).ToList();
                bool weekWarn = SpecialMeasures.Watching(g, map, o).Count == 1;
                o.weekLog[6] = limit;
                bool weekTriggered = SpecialMeasures.Watching(g, map, o).Count == 0 && SpecialMeasures.Trigger(g, map, o) == MeasuresCause.Incidents;
                o.weekLog.Clear();
                (g.story.loanOwed, g.story.debtDays) = (5000, SpecialMeasures.LoanDefaultDays - 2);
                bool loanWarn = SpecialMeasures.Watching(g, map, o).Count == 1;
                var alert = new Alert_RP_MeasuresWatch();
                bool alerted = alert.GetReport().active && alert.GetExplanation().ToString().Length > 0;
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                bool noAlert = !alert.GetReport().active;
                o.active = true;
                SetGrades(30f, 30f);
                bool underMeasures = SpecialMeasures.Watching(g, map, o).Count == 0;
                if (!gradeWarn || !quiet || !weekWarn || !weekTriggered || !loanWarn || !alerted || !noAlert || !underMeasures)
                    Fail($"the warning: two bad days {gradeWarn}, nothing near {quiet}, the week near the limit {weekWarn} and at it {weekTriggered}, a loan two nights short {loanWarn}, the alert {alerted}/{noAlert}, none under measures {underMeasures}");
                RPLog.Message($"[RimPrison:test] special measures, the cause: grades={gradesHeld && gradesLifted} loan={loanHeld && loanLifted && noFarm} incidents={incidentsHeld && incidentsLifted} warning={gradeWarn && weekWarn && loanWarn && alerted}");
            });
        }

        // ---- 10: the chief is not an employee to promote, train or strike with -------------------------------------------------------------------------------

        private static void CheckMeasuresChiefIsNotAnEmployee(Map map)
        {
            Guarded("special measures: the chief is not an employee", map, r =>
            {
                void Fail(string what) => problems0.Add("special measures, the chief on the staff: " + what);
                StateOversight o = MeasuresState(r, map);
                Pawn chief = SpecialMeasures.InstallChief(map, o);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (chief == null)
                {
                    Fail("could not run (no chief)");
                    return;
                }
                CompStaff csc = chief.GetComp<CompStaff>(), gsc = guard.GetComp<CompStaff>();
                // Long in post, nothing against them: a guard in this position is up for promotion, the state's chief is not.
                foreach (CompStaff sc in new[] { csc, gsc })
                    (sc.hiredTick, sc.roleSinceTick) = (Find.TickManager.TicksGame - 200 * GenDate.TicksPerDay, Find.TickManager.TicksGame - 200 * GenDate.TicksPerDay);
                bool guardUp = StaffCareers.Eligible(guard, gsc);
                bool chiefUp = StaffCareers.Eligible(chief, csc);
                // The course: not for them.
                string chiefCourse = StaffTraining.CannotGo(chief, csc);
                bool noCourse = chiefCourse == "RP_TrainingStatePosted".Translate().ToString();
                // The strike: the list the union is made of leaves them out.
                List<Pawn> strikers = IncidentWorker_StaffStrike.Staff(map);
                bool noStrike = !strikers.Contains(chief) && strikers.Contains(guard);
                if (!guardUp) RPLog.Message("[RimPrison:test] special measures, the chief on the staff: the control guard is not up for promotion either; the promotion check is skipped");
                if ((guardUp && chiefUp) || !noCourse || !noStrike)
                    Fail($"promotion offered to the chief {chiefUp} (an ordinary guard in the same position {guardUp}), sent on a course {!noCourse} ({chiefCourse}), among the staff who strike {!noStrike}");
                RPLog.Message($"[RimPrison:test] special measures, the chief on the staff: promotion={chiefUp}/{guardUp} course={noCourse} strike={noStrike}");
            });
        }

        // ---- 7: armed response where it is used ------------------------------------------------------------------------------------------------------------

        private static void CheckMeasuresArmedResponseWhereItIsUsed(Map map)
        {
            Guarded("special measures: armed response where it is used", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, armed response: " + what);
                StateOversight o = MeasuresState(r, map);
                Pawn rioter = null;
                for (int attempt = 0; attempt < 8 && rioter == null; attempt++)
                {
                    Pawn candidate = Throwaway(map, r);
                    if (!candidate.WorkTagIsDisabled(WorkTags.Violent)) rioter = candidate;
                }
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (rioter == null || !rioter.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true))
                {
                    Fail("could not run (no rioter)");
                    return;
                }
                guard.pather?.StopDead();
                guard.Position = rioter.Position;
                guard.Notify_Teleported();
                (int lastRiot0, int drama0, int beats0) = (g.story.lastRiotTick, g.story.dramaToday, g.story.beatsToday);
                r.Add(() => (g.story.lastRiotTick, g.story.dramaToday, g.story.beatsToday) = (lastRiot0, drama0, beats0));
                var worker = new WorkGiver_ArmedResponse();
                g.armedResponse = ArmedResponsePolicy.EscapesAndViolence;
                bool shotWithout = ArmedResponse.ShouldShoot(rioter);
                bool jobWithout = worker.HasJobOnThing(guard, rioter);
                BeginMeasures(g, map);
                bool shotUnder = ArmedResponse.ShouldShoot(rioter);
                bool jobUnder = worker.HasJobOnThing(guard, rioter);
                // The state's chief holds the same line.
                o.active = false;
                o.underChief = true;
                bool shotUnderChief = ArmedResponse.ShouldShoot(rioter);
                g.armedResponse = ArmedResponsePolicy.HoldFire;
                bool holdFire = !ArmedResponse.ShouldShoot(rioter);
                if (!shotWithout) problems0.Add("special measures, armed response: the check could not make a rioter a target with the policy open");
                if (shotUnder || jobUnder || shotUnderChief || !holdFire || (shotWithout && !jobWithout))
                    Fail($"rioters a target of the shooting under special measures {shotUnder}, the work giver offers a guard the job {jobUnder} (without {jobWithout}), a target under the chief {shotUnderChief}, a target with the policy at hold fire {!holdFire}");
                RPLog.Message($"[RimPrison:test] special measures, armed response: open={shotWithout}/{jobWithout} measures={shotUnder}/{jobUnder} chief={shotUnderChief}");
            });
        }

        // ---- 13: no flagship on the night the measures are lifted --------------------------------------------------------------------------------------------

        private static void CheckMeasuresNoFlagshipOnTheLiftingNight(Map map)
        {
            Guarded("special measures: flagship after the lift", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, flagship after the lift: " + what);
                StateOversight o = MeasuresState(r, map);
                int lifted0 = g.stats.measuresLifted;
                BeginMeasures(g, map);
                // Four A days into the term already (graded A under the measures does not count), then the night of the lift.
                o.aDays = SpecialMeasures.FlagshipDays - 1;
                SetGrades(90f, 90f, 90f);
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded;
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(90f), new DailyIncidents());
                bool liftedNotFlagship = !o.active && !o.flagship && o.aDays == 0 && g.stats.measuresLifted == lifted0 + 1;
                // After it, the A days count from the first night.
                SpecialMeasures.Daily(g, map, MeasuresCard(90f), new DailyIncidents());
                bool counting = o.aDays == 1 && !o.flagship;
                if (!liftedNotFlagship || !counting) Fail($"the measures lifted {!o.active}, the flagship the same night {o.flagship}, A days {o.aDays} (0 expected, then 1)");
                // And under the state's chief no A days count either.
                o = MeasuresState(r, map);
                BeginMeasures(g, map);
                FailMeasures(g, map, o);
                o.aDays = 0;
                SetGrades(90f, 90f, 90f);
                for (int i = 0; i < SpecialMeasures.FlagshipDays + 1; i++) SpecialMeasures.Daily(g, map, MeasuresCard(90f), new DailyIncidents());
                if (o.flagship && o.underChief) Fail("the flagship status was earned with the state's chief in post");
                RPLog.Message($"[RimPrison:test] special measures, flagship after the lift: lifted={liftedNotFlagship} counting={counting}");
            });
        }

        // ---- 13: the letter's overseer ----------------------------------------------------------------------------------------------------------------------

        private static void CheckMeasuresStaleOverseer(Map map)
        {
            Guarded("special measures: the overseer left", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                void Fail(string what) => problems0.Add("special measures, a stale overseer: " + what);
                StateOversight o = MeasuresState(r, map);
                BeginMeasures(g, map);
                o.deadlineDay = Ledger.CurrentDay;
                SpecialMeasures.Daily(g, map, MeasuresCard(30f), new DailyIncidents());
                Pawn visitor = o.overseer;
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_MeasuresExtended") && !l.answered);
                if (visitor == null || letter == null)
                {
                    Fail($"could not run (an overseer {visitor != null}, the extension letter {letter != null})");
                    return;
                }
                // The visit is over (they were let go at the end of it); the letter is answered later.
                string name = visitor.LabelShort;
                visitor.GetComp<CompStaff>().role = null;
                letter.answered = true;
                PrisonDecisions.Choose(letter, 1);
                Find.LetterStack.RemoveLetter(letter);
                Letter failed = r.NewLetters.LastOrDefault(l => l.Label.ToString() == "RP_MeasuresFailedLabel".Translate().ToString());
                // (The name as a word, without case: a short name can sit inside another word of the letter - the new chief's name, a project's - as "Gran" in "Gransier", a census run.)
                string text = (failed as ChoiceLetter)?.Text.ToString() ?? "";
                bool named = System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bool anonymous = failed is ChoiceLetter && !named && text.Contains("RP_MeasuresAnOverseer".Translate().ToString()) && failed.lookTargets?.TryGetPrimaryTarget().Thing != visitor;
                if (!anonymous) Fail($"the letter after the overseer had gone still names them or points at them (the letter {failed != null}, named {named}, the overseer {name}, pointing at them {failed?.lookTargets?.TryGetPrimaryTarget().Thing == visitor}: {(text.Length > 160 ? text.Substring(0, 160) : text)})");
            });
        }

        // ---- 11: the state does not take back what the project in progress builds on --------------------------------------------------------------------------

        private static void CheckBureaucracyLoseLatestKeepsWhatIsBuiltOn()
        {
            void Fail(string what) => problems0.Add("bureaucracy, losing a project: " + what);
            (BureaucracyDef need, BureaucracyDef builds) = DefDatabase<BureaucracyDef>.AllDefsListForReading.SelectMany(b => b.prerequisites.Select(a => (a, b))).FirstOrDefault();
            if (need == null)
            {
                Fail("could not run (no project builds on another)");
                return;
            }
            // (A bureaucracy of its own, not the game's: nothing here is the prison's.)
            var inProgress = new BureaucracySystem { done = new List<BureaucracyDef> { need }, active = builds, progress = 3f };
            BureaucracyDef takenWhileBuilding = inProgress.LoseLatest();
            bool kept = takenWhileBuilding == null && inProgress.done.Contains(need) && inProgress.active == builds;
            var idle = new BureaucracySystem { done = new List<BureaucracyDef> { need } };
            bool idleLoses = idle.NextToLose() == need && idle.LoseLatest() == need && idle.done.Count == 0;
            if (!kept || !idleLoses) Fail($"{need.defName} was taken back while {builds.defName} was being built on it {!kept}, and not when nothing builds on it {idleLoses}");
        }
    }
}
