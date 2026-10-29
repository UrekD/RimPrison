using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The state's oversight (events plan, and the round trip in the saveload plan): the three triggers, the arc's term with its overseer, the
    /// restrictions and their effects (intake cap, payment cut, armed response, solitary), lifting, the extension, the state's own security chief
    /// (cannot be let go, sets the patrols, is recalled), flagship status with its grants, legendary transfers and their arc. Everything is driven through
    /// the real midnight pass (<see cref="SpecialMeasures.Daily"/>) with the day's card and incidents handed to it, and the clock moved by writing days.
    /// Works on throwaway pawns and puts back what it touches.
    /// </summary>
    public static partial class AutoTest
    {
        private static ReportCard MeasuresCard(float overall, float safety = -1f) =>
            new ReportCard { inmates = 8, safety = safety >= 0f ? safety : overall, security = overall, health = overall, conditions = overall, order = overall, reform = overall };

        private static StateOversight CopyOversight(StateOversight o) => new StateOversight
        {
            active = o.active, cause = o.cause, startDay = o.startDay, deadlineDay = o.deadlineDay, extensions = o.extensions, baselineEscapes = o.baselineEscapes,
            safetyDays = o.safetyDays, midVisitDone = o.midVisitDone, overseer = o.overseer, chief = o.chief, goodDays = o.goodDays, graceUntilDay = o.graceUntilDay,
            weekLog = o.weekLog.ToList(), aDays = o.aDays, slumpDays = o.slumpDays, flagship = o.flagship, nextTransferDay = o.nextTransferDay,
        };

        private static void MeasuresTest(Map map)
        {
            Guarded("special measures", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                int Now() => Find.TickManager.TicksGame;
                int problemsBefore = problems0.Count;
                void Fail(string what) => problems0.Add("special measures test: " + what);
                string log = "";

                // ---- what is put back -------------------------------------------------------------------------------------
                StateOversight oversight0 = g.story.oversight;
                List<float> history0 = g.gradeHistory.ToList();
                float lastGrade0 = g.lastGrade;
                (int loan0, int debtDays0) = (g.story.loanOwed, g.story.debtDays);
                List<BureaucracyDef> done0 = g.bureaucracy.done.ToList();
                (BureaucracyDef active0, float progress0) = (g.bureaucracy.active, g.bureaucracy.progress);
                var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
                (int number0, int admissions0, bool overcrowding0) = (g.intake.nextInmateNumber, g.story.admissions, g.intake.allowOvercrowding);
                List<StoryArc> arcs0 = g.story.arcs.ToList();
                var lastArc0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                List<GrantState> grantsActive0 = g.grants.active.ToList();
                List<GrantDef> grantsDone0 = g.grants.completed.ToList();
                ArmedResponsePolicy armed0 = g.armedResponse;
                r.Add(() =>
                {
                    Retire(g.story.oversight);
                    g.story.oversight = oversight0;
                    g.gradeHistory.Clear();
                    g.gradeHistory.AddRange(history0);
                    g.lastGrade = lastGrade0;
                    (g.story.loanOwed, g.story.debtDays) = (loan0, debtDays0);
                    g.bureaucracy.done.Clear();
                    g.bureaucracy.done.AddRange(done0);
                    (g.bureaucracy.active, g.bureaucracy.progress) = (active0, progress0);
                    g.intake.perDay.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                    (g.intake.nextInmateNumber, g.story.admissions, g.intake.allowOvercrowding) = (number0, admissions0, overcrowding0);
                    g.story.arcs.Clear();
                    g.story.arcs.AddRange(arcs0);
                    g.story.lastArcStart.Clear();
                    foreach (KeyValuePair<StoryArcDef, int> kv in lastArc0) g.story.lastArcStart[kv.Key] = kv.Value;
                    g.grants.active.Clear();
                    g.grants.active.AddRange(grantsActive0);
                    g.grants.completed.Clear();
                    g.grants.completed.AddRange(grantsDone0);
                    g.armedResponse = armed0;
                    StaffCareers.InvalidateLeads();
                });
                // The pawns the oversight brings (an overseer, a chief) are gone again when a state is dropped for the next one.
                void Retire(StateOversight o)
                {
                    // (The game's own state, if it has people in it, is not the test's to dismiss.)
                    if (o == null || o == oversight0) return;
                    foreach (Pawn p in new[] { o?.overseer, o?.chief })
                    {
                        if (p == null || p.Destroyed) continue;
                        g.home.Remove(p);
                        p.Destroy(DestroyMode.Vanish);
                    }
                    StaffCareers.InvalidateLeads();
                }
                StateOversight Fresh()
                {
                    Retire(g.story.oversight);
                    g.story.oversight = new StateOversight();
                    return g.story.oversight;
                }
                void History(params float[] grades)
                {
                    g.gradeHistory.Clear();
                    g.gradeHistory.AddRange(grades);
                }
                int Today() => Ledger.CurrentDay;
                ChoiceLetter_PrisonDecision DecisionLetter(string decision) =>
                    r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is(decision) && !l.answered);
                Letter LetterLabelled(string label) => r.NewLetters.LastOrDefault(l => l.Label.ToString() == label);
                void Answer(ChoiceLetter_PrisonDecision letter, int option)
                {
                    letter.answered = true;
                    PrisonDecisions.Choose(letter, option);
                    Find.LetterStack.RemoveLetter(letter);
                }
                bool Chronicled(Func<ChronicleEntry, bool> test) => r.NewEntries.Any(test);
                ReportCard bad = MeasuresCard(30f), good = MeasuresCard(90f), fine = MeasuresCard(75f);
                SecurityLevelDef sec = InmateUtility.DefaultSecurity;
                ChronicleCategoryDef outside = RPChronicleDefOf.RP_Chr_Outside;

                // The welcome letter says so: it is on by default and there is no switch.
                if (!PrisonGame.IntroText("x", 9, null).ToLowerInvariant().Contains("special measures") || !PrisonGame.IntroText("x", 9, Facility.Inherited).ToLowerInvariant().Contains("special measures")) Fail("the welcome letter does not mention special measures");

                // ---- the triggers (the rule, each condition and its edge) -------------------------------------------------------
                StateOversight o = Fresh();
                int limit = SpecialMeasures.WeeklyLimit(map);
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                History(80f, 80f, 80f);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("a clean record triggered special measures");
                History(30f, 30f, 80f, 30f, 30f);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("two bad days, a good one and two bad days triggered them (three in a row are needed)");
                History(80f, 30f, 30f);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("two bad days triggered them");
                History(55f, 30f, 30f);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("a C in the three days still counted as bad");
                History(80f, 30f, 45f, 54f);
                if (SpecialMeasures.Trigger(g, map, o) != MeasuresCause.Grades) Fail("D, D and F in a row did not trigger special measures");
                History(80f, 80f, 80f);
                o.weekLog = Enumerable.Repeat(0, 6).Concat(new[] { limit - 1 }).ToList();
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail($"{limit - 1} deaths and escapes in a week triggered them (the limit is {limit})");
                o.weekLog[6] = limit;
                if (SpecialMeasures.Trigger(g, map, o) != MeasuresCause.Incidents) Fail($"{limit} deaths and escapes in a week did not trigger them");
                o.weekLog.Clear();
                (g.story.loanOwed, g.story.debtDays) = (5000, SpecialMeasures.LoanDefaultDays - 1);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("a loan one night short of default triggered them");
                g.story.debtDays = SpecialMeasures.LoanDefaultDays;
                if (SpecialMeasures.Trigger(g, map, o) != MeasuresCause.Loan) Fail("a loan in default did not trigger them");
                (g.story.loanOwed, g.story.debtDays) = (0, 9);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("nights in the red with no loan counted as a default");
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                o.graceUntilDay = Today() + 3;
                History(30f, 30f, 30f);
                if (SpecialMeasures.Trigger(g, map, o) != null) Fail("special measures began inside the grace after the last ones");
                o.graceUntilDay = -1;

                // ---- three F days: the midnight pass starts the arc ---------------------------------------------------------------
                o = Fresh();
                g.reputation = 50f;
                History(30f, 30f, 30f);
                int started0 = g.stats.measuresStarted;
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                bool started = o.active && o.cause == MeasuresCause.Grades && o.startDay == Today() && o.deadlineDay == Today() + SpecialMeasures.TermDays && o.extensions == 0
                               && o.baselineEscapes == g.stats.escaped && g.stats.measuresStarted == started0 + 1;
                Pawn overseer = o.overseer;
                CompStaff osc = overseer?.GetComp<CompStaff>();
                bool visit = overseer is { Spawned: true } && osc?.role == RPMeasuresDefOf.RP_Overseer && osc.leaveAtTick > Now() && osc.leaveAtTick <= Now() + SpecialMeasures.OverseerVisitHours * GenDate.TicksPerHour;
                Letter startLetter = LetterLabelled("RP_MeasuresLabel".Translate().ToString());
                bool letter = startLetter is ChoiceLetter l0 && l0.Text.ToString().Contains("RP_MeasuresCause_Grades".Translate(SpecialMeasures.BadDays, SpecialMeasures.BadGradeText).ToString());
                bool chronicle = Chronicled(e => e.cat == outside && e.importance == 3 && e.text.Contains(overseer?.LabelShort ?? "?"));
                if (!started || !visit || !letter || !chronicle)
                    Fail($"three F days: arc started {started} (active {o.active}, cause {o.cause}, deadline {o.deadlineDay - Today()} days), overseer on site for a visit {visit}, letter with the cause {letter}, chronicle milestone {chronicle}");
                log += $" start={started && visit && letter && chronicle}";
                // The wage bill does not carry the overseer (no wage, no commute home).
                if (overseer != null && StaffCareers.Payroll(new List<Pawn> { overseer }) != 0) Fail("the visiting overseer is paid");
                // The alert says what is happening.
                Alert_RP_SpecialMeasures alert = new Alert_RP_SpecialMeasures();
                if (!alert.GetReport().active || !alert.GetLabel().Contains(SpecialMeasures.TermDays.ToString())) Fail($"no alert for special measures ({alert.GetLabel()})");

                // ---- the restrictions and their effects -----------------------------------------------------------------------
                // The intake cap: two a day (free beds made, and given back after).
                foreach (SecurityLevelDef level in DefDatabase<SecurityLevelDef>.AllDefsListForReading) g.intake.perDay[level] = 0;
                g.intake.perDay[sec] = 6;
                g.intake.allowOvercrowding = false;
                var freed = new List<(Building_Bed bed, Pawn owner)>();
                foreach (Building_Bed bed in pm.PrisonerBeds.ToList())
                {
                    if (IntakeSystem.FreePrisonerBeds(map, sec).Count >= 3) break;
                    if (!bed.Spawned || bed.CompAssignableToPawn is not { } assign || assign.AssignedPawnsForReading.Count != 1) continue;
                    Pawn owner = assign.AssignedPawnsForReading[0];
                    int before = IntakeSystem.FreePrisonerBeds(map, sec).Count;
                    assign.ForceRemovePawn(owner);
                    if (IntakeSystem.FreePrisonerBeds(map, sec).Count <= before)
                    {
                        assign.TryAssignPawn(owner);
                        continue;
                    }
                    freed.Add((bed, owner));
                }
                r.Add(() =>
                {
                    foreach ((Building_Bed bed, Pawn owner) in freed)
                        if (!owner.Destroyed && bed.Spawned && bed.CompAssignableToPawn is { } assign && !assign.AssignedPawnsForReading.Contains(owner)) assign.TryAssignPawn(owner);
                });
                var arrivals = new List<Pawn>();
                List<Pawn> Bus()
                {
                    List<Pawn> got = IntakeSystem.RunIntake(g, map, silent: true);
                    arrivals.AddRange(got);
                    foreach (Pawn p in got)
                    {
                        Pawn arrived = p;
                        r.Add(() =>
                        {
                            if (!arrived.Destroyed) arrived.Destroy(DestroyMode.Vanish);
                        });
                    }
                    return got;
                }
                void Vacate(List<Pawn> got)
                {
                    // (Back out of their beds, so the next bus has the same room.)
                    foreach (Pawn p in got)
                    {
                        p.ownership?.UnclaimBed();
                        if (p.Inmate() is { } ic) ic.homeBed = null;
                    }
                }
                if (IntakeSystem.FreePrisonerBeds(map, sec).Count < 3) Fail($"the intake cap could not be tried: {IntakeSystem.FreePrisonerBeds(map, sec).Count} free beds for the {sec.defName} level");
                else
                {
                    List<Pawn> capped = Bus();
                    Vacate(capped);
                    o.extensions = 1;
                    List<Pawn> frozen = Bus();
                    Vacate(frozen);
                    o.extensions = 0;
                    o.active = false;
                    List<Pawn> open = Bus();
                    Vacate(open);
                    o.active = true;
                    bool intakeOk = capped.Count == SpecialMeasures.IntakeCapPerDay && frozen.Count == 0 && open.Count > SpecialMeasures.IntakeCapPerDay;
                    if (!intakeOk) Fail($"intake under special measures: the bus brought {capped.Count} (expected {SpecialMeasures.IntakeCapPerDay}), {frozen.Count} once extended (expected 0), {open.Count} with no measures (more than {SpecialMeasures.IntakeCapPerDay})");
                    log += $" intake={capped.Count}/{frozen.Count}/{open.Count}";
                }
                // The payment cut: 10% off what the state pays per inmate.
                float cut = g.PaymentMultiplier;
                o.active = false;
                float uncut = g.PaymentMultiplier;
                o.active = true;
                if (Mathf.Abs(cut / uncut - 0.9f) > 0.001f) Fail($"payment under special measures {cut:F3} against {uncut:F3} without (expected a 10% cut)");
                log += $" pay={cut / uncut:F2}";
                // Armed response held at escapes; solitary capped at a day.
                g.armedResponse = ArmedResponsePolicy.EscapesAndViolence;
                bool armedOk = ArmedResponse.Effective(g) == ArmedResponsePolicy.Escapes;
                g.armedResponse = ArmedResponsePolicy.HoldFire;
                armedOk &= ArmedResponse.Effective(g) == ArmedResponsePolicy.HoldFire;
                g.armedResponse = ArmedResponsePolicy.Escapes;
                armedOk &= ArmedResponse.Effective(g) == ArmedResponsePolicy.Escapes;
                g.armedResponse = ArmedResponsePolicy.EscapesAndViolence;
                o.active = false;
                armedOk &= ArmedResponse.Effective(g) == ArmedResponsePolicy.EscapesAndViolence;
                o.active = true;
                if (!armedOk) Fail("armed response under special measures is not held at escapes only (and left alone below that)");
                MisconductDef longHole = DefDatabase<MisconductDef>.AllDefsListForReading.FirstOrDefault(d => d.sanctionAs == null && d.punishmentHours > SpecialMeasures.SolitaryCapHours);
                if (longHole == null) Fail("no misconduct with solitary beyond a day to try the cap on");
                else
                {
                    int capped24 = g.punishments.SolitaryHoursFor(longHole);
                    o.active = false;
                    int uncapped = g.punishments.SolitaryHoursFor(longHole);
                    o.active = true;
                    int shown = g.punishments.HoursFor(longHole);
                    if (capped24 != SpecialMeasures.SolitaryCapHours || uncapped != longHole.punishmentHours || shown != longHole.punishmentHours)
                        Fail($"solitary for {longHole.defName}: {capped24} h under special measures (expected {SpecialMeasures.SolitaryCapHours}), {uncapped} h without (expected {longHole.punishmentHours}), the policy screen shows {shown} h (what is set: {longHole.punishmentHours})");
                    log += $" solitary={capped24}/{uncapped}";
                }

                // ---- the overseer's tour half way through, with the inspector's score ----------------------------------------------
                o.startDay = Today() - SpecialMeasures.TermDays / 2;
                o.deadlineDay = o.startDay + SpecialMeasures.TermDays;
                o.safetyDays = 0;
                SpecialMeasures.Daily(g, map, fine, new DailyIncidents());
                bool progress = o.midVisitDone && o.active && o.extensions == 0 && LetterLabelled("RP_MeasuresProgressLabel".Translate().ToString()) is ChoiceLetter { } pl
                                && pl.Text.ToString().Contains(PrisonGrading.Letter(Inspections.Score(g, map, PrisonGrading.Compute(map, g))));
                if (!progress) Fail($"the overseer's mid-term visit: done {o.midVisitDone}, still active {o.active}, a progress letter with the inspector's grade {LetterLabelled("RP_MeasuresProgressLabel".Translate().ToString()) != null}");
                log += $" midterm={progress}";

                // ---- the term runs out with the safety target not met: extended once, the bus held back --------------------------------
                o.safetyDays = 0;
                o.deadlineDay = Today();
                int extendedEntries = r.NewEntries.Count(e => e.cat == outside && e.importance == 2);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                ChoiceLetter_PrisonDecision extendLetter = DecisionLetter("RP_Decision_MeasuresExtended");
                bool extended = o.active && o.extensions == 1 && o.deadlineDay == Today() + SpecialMeasures.TermDays && o.baselineEscapes == g.stats.escaped && SpecialMeasures.IntakeLimit == 0
                                && extendLetter != null && g.stats.measuresLifted == 0 && g.stats.measuresFailed == 0 && r.NewEntries.Count(e => e.cat == outside && e.importance == 2) == extendedEntries + 1;
                if (!extended) Fail($"a missed target: extended {extended} (active {o.active}, extensions {o.extensions}, {o.deadlineDay - Today()} days, bus limit {SpecialMeasures.IntakeLimit}, letter {extendLetter != null})");
                if (extendLetter != null) Answer(extendLetter, 0);
                if (!o.active || o.extensions != 1 || o.ChiefInPost) Fail("carrying on with the extended term changed it");
                log += $" extended={extended}";

                // ---- an escape misses the target whatever the safety grade ---------------------------------------------------------
                o = Fresh();
                History(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                g.stats.escaped++;
                o.safetyDays = 5;
                o.deadlineDay = Today();
                SpecialMeasures.Daily(g, map, good, new DailyIncidents());
                bool escapeMissed = o.active && o.extensions == 1 && g.stats.measuresLifted == 0;
                if (!escapeMissed) Fail($"an escape during the term: lifted {g.stats.measuresLifted > 0}, extensions {o.extensions} (the extension is expected, with safety at {o.safetyDays} days)");
                log += $" escape={escapeMissed}";

                // ---- the targets met: lifted with praise and a grant ---------------------------------------------------------------
                o = Fresh();
                g.reputation = 50f;
                History(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                int funds0 = g.ledger.funds, lifted0 = g.stats.measuresLifted;
                History(80f, 80f); // (the grade is back at C, the third target: see MeasuresFixChecks for the others)
                o.safetyDays = SpecialMeasures.SafetyDaysNeeded - 1;
                o.deadlineDay = Today();
                SpecialMeasures.Daily(g, map, good, new DailyIncidents());
                bool lifted = !o.active && g.stats.measuresLifted == lifted0 + 1 && g.ledger.funds == funds0 + SpecialMeasures.LiftGrant && Mathf.Abs(g.reputation - 55f) < 0.01f
                              && o.graceUntilDay == Today() + SpecialMeasures.GraceDays && SpecialMeasures.IntakeLimit == int.MaxValue && Mathf.Approximately(SpecialMeasures.PaymentFactor, 1f)
                              && LetterLabelled("RP_MeasuresLiftedLabel".Translate().ToString()) != null
                              && Chronicled(e => e.cat == outside && e.importance == 3 && e.text == "RP_Chr_MeasuresLifted".Translate(o.overseer?.LabelShort ?? "RP_MeasuresAnOverseer".Translate().ToString(), Money.Format(SpecialMeasures.LiftGrant)).Resolve())
                              && r.NewLedgerEntries.Any(e => e.category == RPDefOf.RP_Grants && e.amount == SpecialMeasures.LiftGrant);
                if (!lifted) Fail($"the targets met: lifted {!o.active}, funds {g.ledger.funds - funds0:+#;-#;0} (expected +{SpecialMeasures.LiftGrant}), reputation {g.reputation:F1} (expected 55), grace until +{o.graceUntilDay - Today()}, restrictions gone {SpecialMeasures.IntakeLimit == int.MaxValue}");
                log += $" lifted={lifted}";
                // The grace: the same bad record does not bring them straight back; once it is over it does.
                History(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                if (o.active) Fail("special measures began again inside the grace after being lifted");
                o.graceUntilDay = Today();
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                if (!o.active) Fail("special measures did not begin again once the grace was over");

                // ---- failing them: the state's own security chief, a project lost, and no vanilla game ending ----------------------------
                o = Fresh();
                History(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                o.deadlineDay = Today();
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents()); // the extension
                if (g.bureaucracy.done.Count == 0) g.bureaucracy.done.Add(DefDatabase<BureaucracyDef>.AllDefsListForReading.First(d => d.prerequisites.Count == 0));
                int projects0 = g.bureaucracy.done.Count;
                BureaucracyDef toLose = g.bureaucracy.NextToLose();
                int failed0 = g.stats.measuresFailed;
                o.deadlineDay = Today();
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                Pawn chief = o.chief;
                CompStaff csc = chief?.GetComp<CompStaff>();
                bool failedOk = !o.active && g.stats.measuresFailed == failed0 + 1 && o.ChiefInPost && csc is { statePosted: true } && csc.role == RPMeasuresDefOf.RP_SecurityChief && chief.Spawned
                                && g.bureaucracy.done.Count == projects0 - 1 && !g.bureaucracy.done.Contains(toLose)
                                && LetterLabelled("RP_MeasuresFailedLabel".Translate().ToString()) != null
                                && Chronicled(e => e.cat == outside && e.importance == 3 && e.text.Contains(chief.LabelShort) && e.text.Contains(toLose.label))
                                && o.underChief && SpecialMeasures.IntakeLimit == 0 && SpecialMeasures.PaymentFactor < 1f;
                if (!failedOk) Fail($"failing the measures: ended {!o.active}, a chief in post {o.ChiefInPost}, projects {g.bureaucracy.done.Count} (expected {projects0 - 1}), {toLose?.defName} still done {g.bureaucracy.done.Contains(toLose)}");
                // The visible end is the state's chief and a letter, not the game's end.
                if (Find.GameEnder.gameEnding) Fail("failing special measures ended the game");
                if (r.NewLetters.Any(l => l.Label.ToString().ToLowerInvariant().Contains("game over"))) Fail("failing special measures sent a game-over letter");
                log += $" failed={failedOk} lost={toLose?.defName}";
                if (chief != null)
                {
                    // The chief cannot be let go, is not the warden's to retrain, and is not offered to the staff stories.
                    StaffUtility.Fire(chief);
                    bool stays = csc.role != null && chief.Spawned && csc.statePosted;
                    List<Gizmo> gizmos = csc.CompGetGizmosExtra().ToList();
                    bool locked = gizmos.Count == 1 && gizmos[0] is Command { Disabled: true };
                    // (Still on site for breaks and the rest, but life events never pick them.)
                    bool storiesSkip = !StaffLives.Storied(map).Contains(chief) && StaffLives.OnSite(map).Contains(chief);
                    if (!stays || !locked || !storiesSkip) Fail($"the state's chief: stays after being let go {stays}, only a locked gizmo {locked} ({gizmos.Count}), left out of the staff stories {storiesSkip}");
                    // Deployment is theirs: the warden's patrol zone is not used while they are here.
                    Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                    guard.GetComp<CompStaff>().patrolZone = RPDefOf.RP_Yard;
                    bool deployed = SpecialMeasures.DeployedZone(guard) == null;
                    // A bad record while the chief is in post does not bring special measures on top.
                    History(30f, 30f, 30f);
                    SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                    bool noSecond = !o.active && o.ChiefInPost;
                    if (!deployed || !noSecond) Fail($"with the chief in post: patrol zone ignored {deployed}, no second special measures {noSecond}");
                    // They are recalled when the prison has been graded B for a week, not before.
                    o.goodDays = SpecialMeasures.ChiefRecallDays - 2;
                    SpecialMeasures.Daily(g, map, fine, new DailyIncidents());
                    bool stillThere = o.ChiefInPost;
                    SpecialMeasures.Daily(g, map, fine, new DailyIncidents());
                    bool recalled = stillThere && !o.ChiefInPost && o.chief == null && !SpecialMeasures.Posted(chief) && csc.role == null && o.graceUntilDay == Today() + SpecialMeasures.GraceDays
                                    && LetterLabelled("RP_ChiefRecalledLabel".Translate().ToString()) != null && Chronicled(e => e.cat == outside && e.importance == 3 && e.text.Contains("RP_Chr_ChiefRecalled".Translate(chief.LabelShort).Resolve()))
                                    && SpecialMeasures.DeployedZone(guard) == RPDefOf.RP_Yard && !o.underChief && !SpecialMeasures.Restricted && SpecialMeasures.IntakeLimit == int.MaxValue;
                    if (!recalled) Fail($"recalling the chief: still there a day early {stillThere}, gone after a week of B {!o.ChiefInPost} (days {o.goodDays}), role {csc.role?.defName ?? "none"}, the warden's patrols back {SpecialMeasures.DeployedZone(guard) == RPDefOf.RP_Yard}");
                    log += $" chief={stays && locked && storiesSkip && deployed && noSecond && recalled}";
                }
                else Fail("failing the measures installed no chief");
                // (A chief who dies or vanishes is replaced, the restrictions held: CheckMeasuresChiefDeathIsNoWayOut.)

                // ---- asking for the chief at once, from the extension's letter ---------------------------------------------------------------
                o = Fresh();
                History(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                o.deadlineDay = Today();
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                ChoiceLetter_PrisonDecision askLetter = DecisionLetter("RP_Decision_MeasuresExtended");
                int failedAsk0 = g.stats.measuresFailed;
                if (askLetter == null) Fail("no extension letter to ask for the chief from");
                else
                {
                    Answer(askLetter, 1);
                    bool asked = !o.active && o.ChiefInPost && o.underChief && g.stats.measuresFailed == failedAsk0 + 1 && SpecialMeasures.IntakeLimit == 0;
                    Pawn askedChief = o.chief;
                    // (A stale copy of the letter hands over nothing more.)
                    int projectsAfter = g.bureaucracy.done.Count;
                    new MeasuresExtendedDecision().Choose(askLetter, map, 1);
                    bool once = o.chief == askedChief && g.stats.measuresFailed == failedAsk0 + 1 && g.bureaucracy.done.Count == projectsAfter;
                    if (!asked || !once) Fail($"asking for the chief: the measures ended and the chief came {asked}, a second answer changed nothing {once}");
                    log += $" ask={asked && once}";
                }

                // ---- the other two triggers, through the midnight pass --------------------------------------------------------------------------
                o = Fresh();
                History(80f, 80f, 80f);
                SpecialMeasures.Daily(g, map, fine, new DailyIncidents { deaths = limit - 1, escapes = 1 });
                bool incidents = o.active && o.cause == MeasuresCause.Incidents && Chronicled(e => e.cat == outside && e.text.Contains("RP_MeasuresCause_Incidents".Translate(limit, limit).Resolve()));
                if (!incidents) Fail($"deaths and escapes over the week's limit of {limit}: began {o.active} ({o.cause})");
                o = Fresh();
                (g.story.loanOwed, g.story.debtDays) = (5000, SpecialMeasures.LoanDefaultDays);
                SpecialMeasures.Daily(g, map, fine, new DailyIncidents());
                bool loan = o.active && o.cause == MeasuresCause.Loan;
                if (!loan) Fail($"a loan in default: began {o.active} ({o.cause})");
                (g.story.loanOwed, g.story.debtDays) = (0, 0);
                log += $" triggers={incidents && loan}";

                // ---- flagship: a sustained A ------------------------------------------------------------------------------------------------------
                o = Fresh();
                History(90f, 90f, 90f);
                o.aDays = SpecialMeasures.FlagshipDays - 1;
                int earned0 = g.stats.flagshipsEarned;
                SpecialMeasures.Daily(g, map, good, new DailyIncidents());
                bool flagship = o.flagship && g.stats.flagshipsEarned == earned0 + 1 && o.nextTransferDay == Today() + 2 && LetterLabelled("RP_FlagshipLabel".Translate().ToString()) != null
                                && Chronicled(e => e.cat == outside && e.importance == 3 && e.text == "RP_Chr_FlagshipEarned".Translate(SpecialMeasures.FlagshipDays).Resolve());
                if (!flagship) Fail($"five days of A: flagship {o.flagship} (A days {o.aDays})");
                // Better grants: a real grant paid out, with and without the status.
                var grant = new GrantDef { defName = "RP_TestGrant", label = "test grant", reward = 1000 };
                int PaidFor(bool flag)
                {
                    o.flagship = flag;
                    g.grants.active.Clear();
                    g.grants.active.Add(new GrantState { def = grant, acceptedTick = Now() });
                    int before = g.ledger.funds;
                    g.grants.Check(map);
                    return g.ledger.funds - before;
                }
                int withStatus = PaidFor(true), without = PaidFor(false);
                o.flagship = true;
                if (withStatus != 1250 || without != 1000) Fail($"grants for a flagship: {withStatus} (expected 1250), {without} without the status (expected 1000)");
                log += $" flagship={flagship} grant={withStatus}/{without}";
                // Legendary transfers: the offer on its day, the legend arriving with the premium and a story of their own.
                SecurityLevelDef max = Celebrities.Maximum;
                if (!Unlocks.IsUnlocked(max)) g.bureaucracy.UnlockAll();
                if (!SpecialMeasures.CanReceiveLegend(map))
                {
                    // The starter prison has no maximum wing: one free bed (one of those emptied above, or another emptied now) is painted as one, and put back.
                    Building_Bed wingBed = freed.Select(f => f.bed).FirstOrDefault(b => b.Spawned && b.CompAssignableToPawn is { } a && a.AssignedPawnsForReading.Count == 0);
                    if (wingBed == null)
                        foreach (Building_Bed bed in pm.PrisonerBeds.ToList())
                        {
                            if (!bed.Spawned || bed.CompAssignableToPawn is not { } assign || assign.AssignedPawnsForReading.Count != 1) continue;
                            Pawn owner = assign.AssignedPawnsForReading[0];
                            assign.ForceRemovePawn(owner);
                            wingBed = bed;
                            r.Add(() =>
                            {
                                if (!owner.Destroyed && bed.Spawned && bed.CompAssignableToPawn is { } back && !back.AssignedPawnsForReading.Contains(owner)) back.TryAssignPawn(owner);
                            });
                            break;
                        }
                    if (wingBed != null)
                    {
                        IntVec3 wingCell = wingBed.Position;
                        PrisonZoneDef wasZone = pm.zones.ZoneAt(wingCell);
                        pm.zones.Set(wingCell, DefDatabase<PrisonZoneDef>.GetNamed("RP_WingMaximum"));
                        r.Add(() => pm.zones.Set(wingCell, wasZone));
                    }
                }
                if (!SpecialMeasures.CanReceiveLegend(map)) Fail("a legendary transfer could not be tried: no free maximum-security bed");
                else
                {
                    // The celebrity incident takes a seat on the bus too: not while the bus is held back (the check is skipped if the incident would not fire at all).
                    var celebrity = new IncidentWorker_CelebrityInmate { def = DefDatabase<IncidentDef>.GetNamed("RP_CelebrityInmate") };
                    MethodInfo celebrityCanFire = typeof(IncidentWorker_CelebrityInmate).GetMethod("CanFireNowSub", BindingFlags.Instance | BindingFlags.NonPublic);
                    bool CelebrityCanFire() => (bool)celebrityCanFire.Invoke(celebrity, new object[] { new IncidentParms { target = map } });
                    if (CelebrityCanFire())
                    {
                        (o.active, o.extensions) = (true, 1);
                        bool heldBack = !CelebrityCanFire();
                        (o.active, o.extensions) = (false, 0);
                        if (!heldBack) Fail("a celebrity could still be sentenced to the prison while the state held the bus back");
                    }
                    else RPLog.Message("[RimPrison:test] special measures: the celebrity incident would not fire at all here; the held-back bus check is skipped");
                    o.nextTransferDay = Today() + 3;
                    SpecialMeasures.Daily(g, map, good, new DailyIncidents());
                    bool early = DecisionLetter("RP_Decision_FlagshipTransfer") == null;
                    o.nextTransferDay = Today();
                    SpecialMeasures.Daily(g, map, good, new DailyIncidents());
                    ChoiceLetter_PrisonDecision offer = DecisionLetter("RP_Decision_FlagshipTransfer");
                    bool offered = early && offer != null && o.nextTransferDay == Today() + SpecialMeasures.TransferEveryDays;
                    if (!offered) Fail($"the legendary transfer: none before its day {early}, offered on the day {offer != null}, the next one {o.nextTransferDay - Today()} days on (expected {SpecialMeasures.TransferEveryDays})");
                    if (offer != null)
                    {
                        int legends0 = g.stats.legendsArrived, funds1 = g.ledger.funds, inmates0 = map.mapPawns.PrisonersOfColonySpawnedCount;
                        Answer(offer, 0);
                        StoryArc arc = g.story.arcs.LastOrDefault(a => a.def == RPMeasuresDefOf.RP_Arc_Legend);
                        Pawn legend = arc?.A;
                        if (legend != null) r.Add(() =>
                        {
                            if (!legend.Destroyed) legend.Destroy(DestroyMode.Vanish);
                        });
                        bool arrived = legend != null && g.stats.legendsArrived == legends0 + 1 && map.mapPawns.PrisonersOfColonySpawnedCount == inmates0 + 1 && g.ledger.funds == funds1 + max.intakeGrant * SpecialMeasures.LegendPremiumFactor
                                       && arc != null && arc.stage == 0 && arc.nextTick > Now();
                        if (!arrived) Fail($"accepting the legendary transfer: the legend arrived {legend != null}, premium {g.ledger.funds - funds1} (expected {max.intakeGrant * SpecialMeasures.LegendPremiumFactor}), arc {arc != null}");
                        if (arc != null && legend != null)
                        {
                            // Two days on: the cameras. Let them in once (fee, reputation, tension), then keep them out (nothing).
                            arc.nextTick = Now() - 1;
                            arc.def.Worker.Hourly(arc, map);
                            ChoiceLetter_PrisonDecision cameras = DecisionLetter("RP_Decision_Arc");
                            bool asked = arc.stage == 1 && cameras != null && cameras.arcId == arc.id;
                            float rep1 = g.reputation, tension1 = legend.Inmate().tension;
                            int funds2 = g.ledger.funds;
                            if (cameras != null) Answer(cameras, 0);
                            bool let = arc.done && g.ledger.funds == funds2 + SpecialMeasures.LegendMediaFee && g.reputation > rep1 + 2.9f && legend.Inmate().tension >= Mathf.Min(100f, tension1 + 19.9f);
                            arc.done = false;
                            int funds3 = g.ledger.funds;
                            arc.def.Worker.Choose(arc, map, 1);
                            bool kept = arc.done && g.ledger.funds == funds3;
                            arc.done = true;
                            if (!asked || !let || !kept) Fail($"the legend's cameras: asked {asked}, let in {let} (fee, reputation, tension), kept out {kept}");
                            log += $" legend={arrived && asked && let && kept}";
                        }
                    }
                }
                // The status is lost after two days below B, and when the state steps in.
                o.slumpDays = SpecialMeasures.FlagshipSlumpDays - 1;
                SpecialMeasures.Daily(g, map, MeasuresCard(60f), new DailyIncidents());
                bool slumped = !o.flagship && LetterLabelled("RP_FlagshipLostLabel".Translate().ToString()) != null && Chronicled(e => e.text == "RP_Chr_FlagshipLost".Translate().Resolve());
                if (!slumped) Fail("two days below B did not take the flagship status away");
                o.flagship = true;
                History(30f, 30f, 30f);
                SpecialMeasures.Daily(g, map, bad, new DailyIncidents());
                if (o.flagship || !o.active) Fail($"special measures with the flagship status held: flagship {o.flagship}, measures {o.active}");
                // No flagship while the state is overseeing the prison, however many A days.
                o.aDays = SpecialMeasures.FlagshipDays;
                o.deadlineDay = Today() + 5;
                SpecialMeasures.Daily(g, map, good, new DailyIncidents());
                if (o.flagship) Fail("the flagship status was earned under special measures");

                // ---- the overseer's visit ends by itself, with no word in the chronicle -----------------------------------------------------------------
                o = Fresh();
                Pawn visitor = SpecialMeasures.OverseerVisit(map, o, 1);
                if (visitor == null) Fail("no overseer could visit");
                else
                {
                    CompStaff vsc = visitor.GetComp<CompStaff>();
                    int entries = g.story.entries.Count;
                    vsc.leaveAtTick = Now() - 1;
                    for (int i = 0; i < 520 && vsc.role != null; i++) Find.TickManager.DoSingleTick();
                    bool gone = vsc.role == null && !Chronicled(e => e.text.Contains(visitor.LabelShort) && e.cat == RPChronicleDefOf.RP_Chr_Staff);
                    if (!gone) Fail($"the overseer's visit: still on the staff after the time was up {vsc.role != null}, a staff line in the chronicle {!gone && vsc.role == null}");
                    log += $" visit-ends={gone}";
                }

                RPLog.Message($"[RimPrison:test] special measures:{log} problems={problems0.Count - problemsBefore}");
            });
        }

        /// <summary>
        /// The stress plan's last day: two bad days on the books and a day that starts with a week's limit of deaths (so the day is bad too, and the weekly
        /// count is met whatever the day's grade). The midnight after it is the state's step-in through the real settlement, which the plan's end checks. Not earlier:
        /// under special measures the bus is capped, the payments cut, armed response held and solitary capped, and the plan's riots and escapes are the
        /// stress that must run without them.
        /// </summary>
        private static void SeedStressMeasures(Map map)
        {
            stressMeasuresSeeded = true;
            PrisonGame g = PrisonGame.Instance;
            g.gradeHistory.Clear();
            g.gradeHistory.AddRange(new[] { 30f, 30f });
            g.today.deaths = Mathf.Max(g.today.deaths, SpecialMeasures.WeeklyLimit(map));
            RPLog.Message($"[RimPrison:test] stress: special measures part, a bad record seeded for the midnight ({g.today.deaths} deaths today, limit {SpecialMeasures.WeeklyLimit(map)})");
        }

        private static bool stressMeasuresSeeded;
        private static int measuresSeedChiefId = -1, measuresSeedOverseerId = -1, measuresSeedLeaveTick = -1, measuresBeforeChiefId = -1, measuresBeforeOverseerId = -1;
        private static string measuresSeedState;
        private static StateOversight measuresBeforeSeed;
        private static int[] measuresStatsBeforeSeed;

        /// <summary>
        /// The saveload plan seeds a term part-way through its extension, with every counter set, and the state's own people as they really come: a security chief from
        /// <see cref="SpecialMeasures.InstallChief"/> who is at home (commuting) when the game is saved, and an overseer on a visit with a time to leave.
        /// </summary>
        private static void SeedMeasuresForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            measuresBeforeSeed = CopyOversight(g.story.oversight);
            (measuresBeforeChiefId, measuresBeforeOverseerId) = (g.story.oversight.chief?.thingIDNumber ?? -1, g.story.oversight.overseer?.thingIDNumber ?? -1);
            PrisonStats st = g.stats;
            measuresStatsBeforeSeed = new[] { st.measuresStarted, st.measuresLifted, st.measuresFailed, st.flagshipsEarned, st.legendsArrived };
            (st.measuresStarted, st.measuresLifted, st.measuresFailed, st.flagshipsEarned, st.legendsArrived) = (7, 3, 2, 1, 4);
            int today = Ledger.CurrentDay;
            var o = new StateOversight
            {
                active = true, cause = MeasuresCause.Loan, startDay = today - 3, deadlineDay = today + 4, extensions = 1, baselineEscapes = g.stats.escaped + 3, safetyDays = 1, midVisitDone = true,
                graceUntilDay = today + 2, weekLog = new List<int> { 0, 2, 1, 0, 0, 3, 1 }, aDays = 2, slumpDays = 1, flagship = true, nextTransferDay = today + 6, underChief = true,
            };
            g.story.oversight = o;
            Pawn chief = SpecialMeasures.InstallChief(map, o);
            Pawn overseer = SpecialMeasures.OverseerVisit(map, o, SpecialMeasures.OverseerVisitHours);
            if (chief == null || overseer == null)
            {
                problems0.Add($"saveload: no chief or overseer could be seeded (chief {chief != null}, overseer {overseer != null})");
                foreach (Pawn p in new[] { chief, overseer })
                    if (p != null && !p.Destroyed) p.Destroy(DestroyMode.Vanish);
                g.story.oversight = measuresBeforeSeed;
                (st.measuresStarted, st.measuresLifted, st.measuresFailed, st.flagshipsEarned, st.legendsArrived) = (measuresStatsBeforeSeed[0], measuresStatsBeforeSeed[1], measuresStatsBeforeSeed[2], measuresStatsBeforeSeed[3], measuresStatsBeforeSeed[4]);
                return;
            }
            o.goodDays = 4;
            // The chief commutes: saved at home, off the map.
            StaffCommute.Leave(chief);
            (measuresSeedChiefId, measuresSeedOverseerId, measuresSeedLeaveTick) = (chief.thingIDNumber, overseer.thingIDNumber, overseer.GetComp<CompStaff>().leaveAtTick);
            measuresSeedState = SpecialMeasures.Describe(o);
            RPLog.Message($"[RimPrison:test] saveload special measures: {measuresSeedState} (chief at home {StaffCommute.AtHome(chief)}, overseer leaves at {measuresSeedLeaveTick})");
        }

        /// <summary>After the reload: the whole state came back, with the chief (at home) and the overseer (still to leave) the same people; then the plan's own state is put back and the state's people dismissed.</summary>
        private static void CheckMeasuresAfterReload(Map map)
        {
            if (measuresSeedChiefId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            StateOversight o = g.story.oversight;
            string after = SpecialMeasures.Describe(o);
            CompStaff chiefComp = o.chief?.GetComp<CompStaff>(), overseerComp = o.overseer?.GetComp<CompStaff>();
            bool chiefOk = o.chief?.thingIDNumber == measuresSeedChiefId && chiefComp is { statePosted: true, role: not null } && o.ChiefInPost && StaffCommute.AtHome(o.chief) && !o.chief.Spawned;
            bool overseerOk = o.overseer?.thingIDNumber == measuresSeedOverseerId && overseerComp is { role: not null } && overseerComp.leaveAtTick == measuresSeedLeaveTick && o.overseer.Spawned;
            bool peopleOk = chiefOk && overseerOk;
            RPLog.Message($"[RimPrison:test] saveload special measures: {(after == measuresSeedState && peopleOk ? "intact" : "changed")} ({after}), chief at home still the state's {chiefOk}, overseer still to leave at the same time {overseerOk}");
            if (after != measuresSeedState || !peopleOk) reloadProblems.Add($"the state's oversight changed in the round trip: {measuresSeedState} -> {after} (the chief at home {chiefOk}, the overseer on a visit {overseerOk})");
            PrisonStats st = g.stats;
            bool statsOk = (st.measuresStarted, st.measuresLifted, st.measuresFailed, st.flagshipsEarned, st.legendsArrived) == (7, 3, 2, 1, 4);
            RPLog.Message($"[RimPrison:test] saveload special measures: counters {(statsOk ? "intact" : "changed")}");
            if (!statsOk) reloadProblems.Add("the special measures counters changed in the round trip");
            // The seeded people were made for this: they go (a real chief of the game, if there was one, is another matter: it is found again by its number below).
            foreach (Pawn p in new[] { o.chief, o.overseer })
            {
                if (p == null || p.Destroyed) continue;
                g.home.Remove(p);
                p.Destroy(DestroyMode.Vanish);
            }
            StaffCareers.InvalidateLeads();
            // The game's own oversight comes back, its people (loaded again, so not the pawn objects the copy holds) found by their numbers.
            Pawn ByNumber(int id) => id < 0 ? null : map.mapPawns.AllPawnsSpawned.Concat(g.OffSite).FirstOrDefault(p => p.thingIDNumber == id);
            StateOversight back = measuresBeforeSeed;
            back.chief = ByNumber(measuresBeforeChiefId);
            back.overseer = ByNumber(measuresBeforeOverseerId);
            if (measuresBeforeChiefId >= 0 && back.chief == null) reloadProblems.Add("the game's own chief of the state was lost in the round trip");
            g.story.oversight = back;
            (st.measuresStarted, st.measuresLifted, st.measuresFailed, st.flagshipsEarned, st.legendsArrived) =
                (measuresStatsBeforeSeed[0], measuresStatsBeforeSeed[1], measuresStatsBeforeSeed[2], measuresStatsBeforeSeed[3], measuresStatsBeforeSeed[4]);
            (measuresSeedChiefId, measuresSeedOverseerId, measuresSeedLeaveTick, measuresBeforeChiefId, measuresBeforeOverseerId) = (-1, -1, -1, -1, -1);
        }
    }
}
