using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the hostage crisis (B1-B5 and the edge cases and UI points that were fixed). Each is its own method,
    /// called once from FireAllEvents, that builds its own scene from throwaway pawns in the starter prison's canteen, and puts back what it
    /// touched in a finally (the scene's, then the rollback's). A check is written so that it fails against the code as it was.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>A crisis scene: the canteen to hold, the pawns made for it, and what to put back (see <see cref="HostageCheck"/>).</summary>
        private sealed class HostageScene
        {
            public Map map;
            public PrisonGame g;
            public PrisonMap pm;
            public Rollback r;
            public Room canteen;
            public List<IntVec3> canteenSpots, workshopSpots;
            public readonly List<StoryArc> started = new List<StoryArc>();
            public readonly List<Pawn> buried = new List<Pawn>();
            private readonly List<Action> undo = new List<Action>();
            private (int drama, int beats, int lastRiot) story0;
            private (NeedDef need, int deadline, int spokes, string name, int cooldown) demand0;
            private bool hadStart;
            private int start0;
            public readonly StoryArcDef arcDef = RPHostageDefOf.RP_Arc_Hostage;
            public HostageArc Worker => (HostageArc)arcDef.Worker;

            public static HostageScene Make(Map map, Rollback r, string name)
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                IntVec3 canteenCell = PrisonBuilder.CanteenCell(map), workshopCell = PrisonBuilder.WorkshopCell(map);
                Room canteen = canteenCell.GetRoom(map), workshop = workshopCell.GetRoom(map);
                if (HostageCrisis.Running() != null || HostageCrisis.HoldRoom(canteenCell, map) == null || HostageCrisis.HoldRoom(workshopCell, map) == null)
                {
                    problems0.Add($"hostage regression {name}: could not run (a crisis is running or the canteen and workshop are not rooms to hold)");
                    return null;
                }
                var s = new HostageScene { map = map, g = g, pm = pm, r = r, canteen = canteen };
                List<IntVec3> Spots(IntVec3 centre, Room room) =>
                    GenRadial.RadialCellsAround(centre, 6f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == room && c.GetEdifice(map) == null).ToList();
                s.canteenSpots = Spots(canteenCell, canteen);
                s.workshopSpots = Spots(workshopCell, workshop);
                if (s.canteenSpots.Count < 8 || s.workshopSpots.Count < 8)
                {
                    problems0.Add($"hostage regression {name}: could not run (room to stand: {s.canteenSpots.Count} cells in the canteen, {s.workshopSpots.Count} in the workshop)");
                    return null;
                }
                // Nothing real is taken, tense or counted as present: the staff on the payroll are out of reach of the check, the real inmates are calm.
                foreach (Pawn real in StaffCommute.AllStaff(map))
                    if (real.GetComp<CompStaff>() is { } rsc)
                    {
                        int away = rsc.leaveAtTick;
                        rsc.leaveAtTick = int.MaxValue;
                        s.undo.Add(() => rsc.leaveAtTick = away);
                    }
                foreach (Pawn calm in map.mapPawns.PrisonersOfColonySpawned)
                    if (calm.Inmate() is { } cc) cc.tension = 0f;
                s.story0 = (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick);
                s.demand0 = (g.demand.need, g.demand.deadlineTick, g.demand.spokesId, g.demand.spokesName, g.demand.cooldownUntilTick);
                s.hadStart = g.story.lastArcStart.TryGetValue(s.arcDef, out s.start0);
                return s;
            }

            public void Put(Pawn p, IntVec3 cell)
            {
                p.pather?.StopDead();
                p.jobs?.StopAll();
                p.Position = cell;
                p.Notify_Teleported();
            }

            public Pawn Staff(StaffRoleDef role, IntVec3? cell = null)
            {
                Pawn p = ThrowawayStaff(map, r, role);
                if (cell != null) Put(p, cell.Value);
                return p;
            }

            public Pawn Guard(int spot = -1) => Staff(RPSecurityDefOf.RP_Guard, spot >= 0 ? canteenSpots[spot] : (IntVec3?)null);

            /// <summary>A violent-capable inmate at <paramref name="cell"/>, tense, with an ordinary short sentence (a graver one is kept by a charge).</summary>
            public Pawn Rioter(IntVec3 cell, bool riot = false)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Pawn p = Throwaway(map, r);
                    if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    r.Add(() => p.ownership?.UnclaimAll());
                    buried.Add(p);
                    Put(p, cell);
                    CompInmate c = p.Inmate();
                    c.tension = 80f;
                    c.crime = DefDatabase<CrimeDef>.GetNamed("RP_Crime_Racketeering");
                    c.sentenceDays = 30;
                    if (riot) p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                    return p;
                }
                problems0.Add("hostage regression: could not run (no violent-capable inmate)");
                return null;
            }

            /// <summary>A crisis in the canteen: the hostage (a guard), the ringleader and followers rioting; null when a pawn could not be made.</summary>
            public StoryArc Begin(out Pawn hostage, out Pawn leader, int followers = 0)
            {
                g.story.lastArcStart.Remove(arcDef);
                hostage = Guard(1);
                leader = Rioter(canteenSpots[0]);
                var crew = new List<Pawn>();
                for (int i = 0; i < followers; i++)
                    if (Rioter(canteenSpots[2 + i], riot: true) is { } follower) crew.Add(follower);
                if (hostage == null || leader == null || crew.Count < followers) return null;
                StoryArc arc = HostageCrisis.Seize(map, leader, leader.Inmate(), crew, hostage, HostageCrisis.Riot);
                if (arc != null)
                {
                    started.Add(arc);
                    pm.ForceDoorUpdate();
                }
                else problems0.Add("hostage regression: the crisis did not start");
                return arc;
            }

            public int OpenLetters(StoryArc a) => Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.arcId == a.id && !l.answered);

            public bool Told(string line) => r.NewEntries.Any(e => e.text == line);

            /// <summary>A seed whose first draws satisfy <paramref name="wanted"/> (-1: none does).</summary>
            public int Seed(Func<bool> wanted)
            {
                for (int seed = 1; seed < 5000; seed++)
                {
                    Rand.PushState(seed);
                    bool ok = wanted();
                    Rand.PopState();
                    if (ok) return seed;
                }
                return -1;
            }

            public void Restore()
            {
                HostageCrisis.ForcedRoll = null;
                foreach (StoryArc over in started)
                {
                    over.done = true;
                    HostageCrisis.Release(over.A);
                }
                foreach (Pawn p in buried) p.MentalState?.RecoverFromState();
                foreach (Letter extra in r.NewLetters.ToList())
                {
                    if (extra is ChoiceLetter_PrisonDecision decision) decision.answered = true;
                    Find.LetterStack.RemoveLetter(extra);
                }
                BodyLeft(buried.ToArray());
                for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
                (g.demand.need, g.demand.deadlineTick, g.demand.spokesId, g.demand.spokesName, g.demand.cooldownUntilTick) = demand0;
                (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick) = story0;
                if (hadStart) g.story.lastArcStart[arcDef] = start0;
                else g.story.lastArcStart.Remove(arcDef);
                g.story.arcs.RemoveAll(a => a.def == arcDef && started.Contains(a));
                Find.GameEnder.gameEnding = false;
                pm.ForceDoorUpdate();
                PrisonAlerts.Invalidate();
            }
        }

        /// <summary>Runs one hostage regression check: its scene is built, the body runs, and the scene is put back whatever the body did (then the rollback).</summary>
        private static void HostageCheck(string name, Map map, Action<HostageScene> body)
        {
            Guarded("hostage " + name, map, r =>
            {
                HostageScene s = HostageScene.Make(map, r, name);
                if (s == null) return;
                try
                {
                    body(s);
                }
                finally
                {
                    s.Restore();
                }
            });
        }

        private static void HostageProblem(string name, string what) => problems0.Add($"hostage regression {name}: {what}");

        private static void DestroySquad(Map map)
        {
            foreach (Pawn police in map.mapPawns.FreeColonistsSpawned.Where(p => p.GetComp<CompStaff>()?.role?.defName == "RP_RiotPolice").ToList()) police.Destroy(DestroyMode.Vanish);
        }

        private static void CheckHostageRegressions(Map map)
        {
            CheckStormedHostageIsNotAmongHostileTakers(map);
            CheckFreedHostageIsNotAmongSpreadRioters(map);
            CheckHeldHostageIsNotTheOneKeptOnSite(map);
            CheckChargeTextSaysWhatHappened(map);
            CheckChargeKeepsTheGraverConviction(map);
            CheckEndingsCloseTheStandoffLetter(map);
            CheckHeldDoorVerdictDoesNotDependOnPosition(map);
            CheckDownedHostageStaysHeld(map);
            CheckHeldHostageCannotBeFiredOrArrested(map);
            CheckBrokenHoldIsToldOnce(map);
            CheckArmedResponseSparesTheTakers(map);
            CheckHostageAlertGizmoAndLabel(map);
            CheckHostageDyingAsTheOnlyColonistOnSiteKeepsTheGame(map);
        }

        // ---- B1: a storm brings the hostage out among takers who are not hostile to them again --------------------------------

        /// <summary>
        /// The riot police are called from the prison pane during a stand-off and the hostage comes out safe: the takers (and a rioter who had
        /// joined them in the room) stand down, so none is hostile to the freed officer (the riot state is only not hostile to a held one).
        /// </summary>
        private static void CheckStormedHostageIsNotAmongHostileTakers(Map map)
        {
            HostageCheck("B1 storm", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss, followers: 1);
                if (arc == null) return;
                Pawn follower = arc.pawns[2];
                Pawn joined = s.Rioter(s.canteenSpots[4], riot: true); // joined by spread: never in the cast
                Pawn elsewhere = s.Rioter(s.workshopSpots[0], riot: true); // another riot, nothing to do with it
                if (joined == null || elsewhere == null) return;
                s.g.ledger.funds = RiotPolice.Cost * 3;
                int seed = s.Seed(() =>
                {
                    (float o, float c) = HostageCrisis.StormRolls();
                    return HostageCrisis.StormFate(arc, o) == HostageFate.Freed && c >= HostageCrisis.StormCasualty;
                });
                if (seed < 0)
                {
                    HostageProblem("B1 storm", "no seed frees the hostage in a storm");
                    return;
                }
                Rand.PushState(seed);
                try { RiotPolice.Call(s.map); }
                finally { Rand.PopState(); }
                try
                {
                    bool freed = arc.done && !guard.Dead && !HostageCrisis.IsHeld(guard);
                    Pawn[] crew = { boss, follower, joined };
                    bool calm = crew.All(p => p.MentalStateDef != RPMentalStateDefOf.RP_Rioting);
                    bool safe = crew.All(p => !p.HostileTo(guard));
                    bool otherRiotGoesOn = elsewhere.MentalStateDef == RPMentalStateDefOf.RP_Rioting;
                    RPLog.Message($"[RimPrison:test] hostage B1 storm: freed {freed}/takers stood down {calm}/none hostile to the freed hostage {safe}/another riot untouched {otherRiotGoesOn}");
                    if (!freed || !calm || !safe || !otherRiotGoesOn)
                        HostageProblem("B1 storm", $"after a storm the freed hostage stands among takers (freed {freed}, every taker and the rioter who joined stood down {calm}, none hostile to the hostage {safe}, a riot elsewhere left alone {otherRiotGoesOn})");
                }
                finally
                {
                    DestroySquad(s.map);
                }
            });
        }

        /// <summary>A calm ending (the takers give up) leaves no rioter who joined by spread, outside the cast, hostile to the hostage who walks free.</summary>
        private static void CheckFreedHostageIsNotAmongSpreadRioters(Map map)
        {
            HostageCheck("B1 spread", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss, followers: 1);
                if (arc == null) return;
                Pawn joined = s.Rioter(s.canteenSpots[4], riot: true);
                if (joined == null) return;
                float tension0 = joined.Inmate().tension;
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                bool calm = joined.MentalStateDef != RPMentalStateDefOf.RP_Rioting && !joined.HostileTo(guard) && !boss.HostileTo(guard);
                bool eased = joined.Inmate().tension < tension0;
                RPLog.Message($"[RimPrison:test] hostage B1 spread: rioter who joined stood down and not hostile {calm}/tired {eased}");
                if (!calm || !eased) HostageProblem("B1 spread", $"a rioter who joined the takers is still rioting or hostile to the freed hostage after they gave up (stood down and not hostile {calm}, tension eased {eased})");
            });
        }

        // ---- B2: the held hostage is not the staffer kept on site ------------------------------------------------------------------

        /// <summary>
        /// Two off-shift guards on site: one goes home and one is kept. With a hostage held and one free off-shift guard, that guard is the one kept
        /// (the hostage is on site but cannot stop a riot), so they do not go.
        /// </summary>
        private static void CheckHeldHostageIsNotTheOneKeptOnSite(Map map)
        {
            HostageCheck("B2 commute", map, s =>
            {
                int hour = GenLocalDate.HourOfDay(map);
                var slept = new List<(Pawn p, TimeAssignmentDef was)>();
                void Sleep(Pawn p)
                {
                    slept.Add((p, p.timetable.GetAssignment(hour)));
                    p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                }
                bool GoingHome(Pawn p) => p.CurJobDef == RPCommuteDefOf.RP_GoHome;
                try
                {
                    // The control: nobody held, two off-shift guards on site; one is sent and one is kept.
                    Pawn a = s.Staff(RPSecurityDefOf.RP_Guard), b = s.Staff(RPSecurityDefOf.RP_Guard);
                    Sleep(a);
                    Sleep(b);
                    StaffCommute.SendOffShiftHome(map, s.g, hour);
                    bool control = GoingHome(a) != GoingHome(b);
                    a.jobs.StopAll();
                    b.jobs.StopAll();
                    if (!control)
                    {
                        HostageProblem("B2 commute", "could not run (two off-shift guards on site did not send one home and keep one)");
                        return;
                    }
                    // A hostage and one free off-shift guard: nobody goes.
                    StoryArc arc = s.Begin(out Pawn hostage, out Pawn boss);
                    if (arc == null) return;
                    // (The control pair is out of the way, both of them: they are the crisis' to cast, not the commute's. Only the first went, and the second stayed on site off shift, so this pass had two free
                    // off-shift guards and sent one: the new one whenever it sorted first - a generated guard who needs medical rest goes home before a fit one (stability round 13) - "the held hostage counted as the
                    // one kept on site" in a run where the product was right.)
                    a.Destroy(DestroyMode.Vanish);
                    b.Destroy(DestroyMode.Vanish);
                    Pawn kept = s.Staff(RPSecurityDefOf.RP_Guard);
                    Sleep(kept);
                    Sleep(hostage);
                    // (Who the pass sees, for the line.)
                    string Seen() => string.Join(", ", map.mapPawns.FreeColonistsSpawned.Where(o => o.GetComp<CompStaff>() is { role: not null } osc && osc.leaveAtTick < 0)
                        .Select(o => $"{o.LabelShort}:{o.timetable?.GetAssignment(hour)?.defName}/{o.CurJobDef?.defName}/held {HostageCrisis.IsHeld(o)}/medical rest {HealthAIUtility.ShouldSeekMedicalRest(o)}"));
                    string before = Seen();
                    StaffCommute.SendOffShiftHome(map, s.g, hour);
                    bool stays = !GoingHome(kept) && !GoingHome(hostage) && kept.Spawned;
                    kept.jobs.StopAll();
                    RPLog.Message($"[RimPrison:test] hostage B2: the free guard is kept {stays} (on site before the pass: {before})");
                    if (!stays) HostageProblem("B2 commute", $"the held hostage counted as the one kept on site: the last free guard was sent home during the stand-off (the free guard {kept.LabelShort}, the hostage {hostage.LabelShort}; on site before the pass: {before})");
                }
                finally
                {
                    foreach ((Pawn p, TimeAssignmentDef was) in slept)
                        if (!p.Destroyed) p.timetable.SetAssignment(hour, was);
                }
            });
        }

        // ---- B3, E3: the charge says only what happened ------------------------------------------------------------------------

        /// <summary>
        /// The chronicle and the letter: "moved to" only when the ringleader moved; "charged" only when they were; "wary" only for a hostage who is.
        /// A ringleader already in the top wing stays, the one who died cannot be charged, and one who left is not.
        /// </summary>
        private static void CheckChargeTextSaysWhatHappened(Map map)
        {
            HostageCheck("B3 charge text", map, s =>
            {
                SecurityLevelDef top = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(l => l.demoteTo == null && !l.wingOnly && InmateUtility.DefaultSecurity != l);
                if (top == null)
                {
                    HostageProblem("B3 charge text", "could not run (no security level without a harsher one to move to)");
                    return;
                }
                string kidnapping = DefDatabase<CrimeDef>.GetNamed("RP_Crime_Kidnapping").label;
                // (The letter starts its sentences with a capital, and a name can be all lower case: "greydoubt" in the line, "Greydoubt" in the letter; its label too: compared without case.)
                string FreedLetter(Pawn hostage) => s.r.NewLetters.OfType<ChoiceLetter>().Last(l => string.Equals(l.Label.ToString(), "RP_HostageFreedLabel".Translate(hostage.LabelShort).ToString(), StringComparison.OrdinalIgnoreCase)).Text.ToString();

                // Nowhere to move to: they stay, and neither line says they moved.
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                CompInmate bc = boss.Inmate();
                bc.security = top;
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                string stays = "RP_HostageChargeLine".Translate(boss.LabelShort, "RP_HostageChargedLife".Translate(kidnapping), "RP_HostageStaysIn".Translate(top.label)).Resolve();
                bool chronicle = s.Told(stays);
                string letter = FreedLetter(guard);
                bool letterStays = letter.Contains(stays, StringComparison.OrdinalIgnoreCase) && !letter.Contains("moved");
                bool wary = letter.Contains("RP_HostageWary".Translate(guard.LabelShort, boss.LabelShort).ToString(), StringComparison.OrdinalIgnoreCase); // (a sentence in the letter starts with a capital, and a name can be lower case: "de Chardin")
                RPLog.Message($"[RimPrison:test] hostage B3: stays in the top wing - chronicle {chronicle}/letter {letterStays}/wary {wary}");
                if (!chronicle || !letterStays || !wary)
                    HostageProblem("B3 charge text", $"a ringleader who could not be moved was told as moved (chronicle line {chronicle}, letter {letterStays}, wary line for a hostage who is wary {wary}): '{letter}'");

                // Somewhere to move to: said exactly when they moved.
                arc = s.Begin(out guard, out boss);
                if (arc == null) return;
                bc = boss.Inmate();
                bc.security = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                SecurityLevelDef before = bc.security;
                s.Worker.Finish(arc, map, HostageHow.Talked, HostageFate.Freed, "the test");
                bool moved = bc.security != before;
                string expected = "RP_HostageChargeLine".Translate(boss.LabelShort, "RP_HostageChargedLife".Translate(kidnapping), (moved ? "RP_HostageMovedTo" : "RP_HostageStaysIn").Translate(bc.security.label)).Resolve();
                bool told = s.Told(expected) && FreedLetter(guard).Contains(expected, StringComparison.OrdinalIgnoreCase);
                RPLog.Message($"[RimPrison:test] hostage B3: moved {moved} ({before?.defName} -> {bc.security?.defName}), told as it was {told}");
                if (!told) HostageProblem("B3 charge text", $"the line and the letter do not say what happened to the ringleader (moved {moved}, expected '{expected}')");

                // Not charged: dead (a storm), or not here to be charged. The letter does not say they are.
                arc = s.Begin(out guard, out boss, followers: 1);
                if (arc == null) return;
                Pawn follower = arc.pawns[2];
                boss.Kill(null);
                BodyLeft(boss);
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed, "", boss);
                letter = FreedLetter(guard);
                string dead = HostageCrisis.NotCharged(boss);
                bool deadSaid = letter.Contains(dead, StringComparison.OrdinalIgnoreCase) && !letter.Contains("charged with") && !s.r.NewEntries.Any(e => e.text.Contains("charged with") && e.text.Contains(boss.LabelShort));
                arc = s.Begin(out guard, out boss);
                if (arc == null) return;
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed, "", boss, charge: false);
                letter = FreedLetter(guard);
                string gone = HostageCrisis.NotCharged(boss);
                bool goneSaid = letter.Contains(gone, StringComparison.OrdinalIgnoreCase) && !letter.Contains("charged with") && !boss.Inmate().records.Any(rec => rec.def == RPHostageDefOf.RP_HostageTaking);
                RPLog.Message($"[RimPrison:test] hostage B3: dead ringleader not charged {deadSaid}/left not charged {goneSaid} ({follower?.LabelShort})");
                if (!deadSaid || !goneSaid) HostageProblem("B3 charge text", $"the letter says a ringleader was charged when none was (dead {deadSaid}, left {goneSaid}): '{letter}'");
            });
        }

        /// <summary>A ringleader already convicted of something graver (a capital murderer, a lifer) keeps that conviction; a short sentence becomes life for kidnapping.</summary>
        private static void CheckChargeKeepsTheGraverConviction(Map map)
        {
            HostageCheck("E3 conviction", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                CompInmate bc = boss.Inmate();
                CrimeDef capital = DefDatabase<CrimeDef>.GetNamed("RP_Crime_CapitalMurder"), murder = DefDatabase<CrimeDef>.GetNamed("RP_Crime_Murder");
                bc.crime = capital;
                bc.sentenceDays = -1;
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                bool capitalStands = bc.crime == capital && bc.sentenceDays < 0 && bc.records.Any(rec => rec.def == RPHostageDefOf.RP_HostageTaking);
                arc = s.Begin(out guard, out boss);
                if (arc == null) return;
                bc = boss.Inmate();
                bc.crime = murder;
                bc.sentenceDays = -1;
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                bool lifeStands = bc.crime == murder && bc.sentenceDays < 0;
                arc = s.Begin(out guard, out boss);
                if (arc == null) return;
                bc = boss.Inmate();
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                bool shortBecomesLife = bc.crime?.defName == "RP_Crime_Kidnapping" && bc.sentenceDays < 0;
                RPLog.Message($"[RimPrison:test] hostage E3: capital murder stands {capitalStands}/life sentence stands {lifeStands}/short sentence becomes life {shortBecomesLife}");
                if (!capitalStands || !lifeStands || !shortBecomesLife)
                    HostageProblem("E3 conviction", $"the charge overwrote a graver conviction or left a short one (capital murder stands {capitalStands}, a life sentence's crime stands {lifeStands}, a short sentence becomes life for kidnapping {shortBecomesLife})");
            });
        }

        // ---- B4: endings that do not come from the letter close it ------------------------------------------------------------------

        /// <summary>
        /// The takers give up on their own, the hostage is killed by a taker, the riot police are called from the prison pane, the hostage is let
        /// go by someone else: each leaves no open stand-off letter, and a stale letter offers nothing to choose.
        /// </summary>
        private static void CheckEndingsCloseTheStandoffLetter(Map map)
        {
            HostageCheck("B4 letters", map, s =>
            {
                var left = new List<string>();
                // The takers give up (nobody is rioting at the hour).
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                int open0 = s.OpenLetters(arc);
                boss.MentalState?.RecoverFromState();
                s.Worker.Hourly(arc, map);
                if (!arc.done || open0 != 1 || s.OpenLetters(arc) != 0) left.Add($"takers gave up (open before {open0}, after {s.OpenLetters(arc)}, done {arc.done})");
                // ...and the letter of a done arc offers only "the arc is over", not the five options.
                ChoiceLetter_PrisonDecision stale = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "stale", "stale", map);
                stale.arcId = arc.id;
                IReadOnlyList<string> offered = stale.decision.Worker.Options(stale);
                if (offered == null || offered.Count != 1 || offered[0] != "RP_ArcOver") left.Add($"a letter of an arc that is over offers {string.Join(",", offered ?? new string[0])}");
                // A taker kills the hostage.
                arc = s.Begin(out guard, out boss, followers: 1);
                if (arc == null) return;
                Pawn taker = arc.pawns[2];
                guard.Kill(new DamageInfo(DamageDefOf.Blunt, 200f, 999f, -1f, taker));
                BodyLeft(guard);
                if (!arc.done || s.OpenLetters(arc) != 0) left.Add($"hostage killed (open {s.OpenLetters(arc)}, done {arc.done})");
                // The riot police called from the prison pane.
                arc = s.Begin(out guard, out boss);
                if (arc == null) return;
                s.g.ledger.funds = RiotPolice.Cost * 3;
                int seed = s.Seed(() =>
                {
                    (float o, float c) = HostageCrisis.StormRolls();
                    return HostageCrisis.StormFate(arc, o) == HostageFate.Freed && c >= HostageCrisis.StormCasualty;
                });
                if (seed < 0)
                {
                    HostageProblem("B4 letters", "no seed frees the hostage in a storm");
                    return;
                }
                Rand.PushState(seed);
                try { RiotPolice.Call(map); }
                finally { Rand.PopState(); }
                DestroySquad(map);
                if (!arc.done || s.OpenLetters(arc) != 0) left.Add($"storm from the prison pane (open {s.OpenLetters(arc)}, done {arc.done})");
                // The hostage is let go by someone else (no letter ended it).
                arc = s.Begin(out guard, out boss);
                if (arc == null) return;
                s.g.story.Notify_Leaving(guard);
                if (!arc.done || s.OpenLetters(arc) != 0) left.Add($"hostage gone (open {s.OpenLetters(arc)}, done {arc.done})");
                RPLog.Message($"[RimPrison:test] hostage B4: letters left open: {(left.Count == 0 ? "none" : string.Join("; ", left))}");
                if (left.Count > 0) HostageProblem("B4 letters", "an ending left the stand-off letter open: " + string.Join("; ", left));
            });
        }

        // ---- B5: the door verdict does not depend on where the pawn stands -----------------------------------------------------------

        /// <summary>
        /// The held room's doors refuse every member of staff, inside or out, so vanilla's per-pawn reachability cache cannot go stale when a pawn
        /// crosses the border; staff caught inside are let out by a job that opens the door and puts them on the far side.
        /// </summary>
        private static void CheckHeldDoorVerdictDoesNotDependOnPosition(Map map)
        {
            HostageCheck("B5 doors", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                List<CompPrisonDoor> held = null;
                CompPrisonDoor door = null;
                IntVec3 inner = IntVec3.Invalid, outer = IntVec3.Invalid;
                // A pawn in a doorway keeps the door open (vanilla's FreePassage: anyone may walk through, whoever they are), and a wandering pawn may stand on the
                // cells beside it: wait, a few ticks at a time, for a moment when no held door is open and a border door has a free cell on each side.
                int waited = 0;
                for (; waited <= 900 && door == null; waited++)
                {
                    held = s.pm.Doors.Where(d => d.heldCells != null && d.Door.Spawned).ToList();
                    if (!held.Any(d => d.Door.FreePassage))
                        foreach (CompPrisonDoor d in held)
                        {
                            IntVec3 i = IntVec3.Invalid, o = IntVec3.Invalid;
                            foreach (IntVec3 dir in GenAdj.CardinalDirections)
                            {
                                IntVec3 c = d.Door.Position + dir;
                                if (!c.InBounds(map) || !c.Standable(map) || c.GetFirstPawn(map) != null) continue;
                                if (HostageHold.InHeld(map, c)) i = c;
                                else o = c;
                            }
                            if (i.IsValid && o.IsValid)
                            {
                                (door, inner, outer) = (d, i, o);
                                break;
                            }
                        }
                    if (door == null) Find.TickManager.DoSingleTick();
                }
                if (door == null)
                {
                    HostageProblem("B5 doors", $"could not run (no border door with a free cell on each side among {held.Count}, {held.Count(d => d.Door.FreePassage)} of them open; "
                        + string.Join(", ", held.Select(d => $"{d.Door.Position} mode {d.mode} open {d.Door.Open} free {d.Door.FreePassage} holdOpen {d.Door.HoldOpen} on it {string.Join("/", d.Door.Position.GetThingList(map).Select(t => t.def.defName + (t is Pawn dp ? " doing " + dp.CurJobDef?.defName : "")))}")) + ")");
                    return;
                }
                Pawn insider = s.Staff(RPSecurityDefOf.RP_Guard, s.canteenSpots[6]), outsider = s.Staff(RPSecurityDefOf.RP_Guard, outer);
                // Neither may open the door: the verdict is the same on both sides.
                bool alike = held.All(d => !d.Door.PawnCanOpen(insider) && !d.Door.PawnCanOpen(outsider));
                // The cache: asking from inside caches an answer that must not turn false later (or true) when the pawn stands on the other side.
                bool outFromInside = insider.CanReach(outer, PathEndMode.OnCell, Danger.Deadly);
                s.Put(insider, outer);
                bool inFromOutside = insider.CanReach(inner, PathEndMode.OnCell, Danger.Deadly);
                // Caught inside, at the door: the door pass sends them out (the job starts on the spot, at its inner cell, and ends on the far side;
                // the walk to a door further off is the same job with a goto in front).
                s.Put(insider, inner);
                s.pm.ForceDoorUpdate();
                for (int tick = 0; tick < 12 && insider.CurJobDef == RPHostageDefOf.RP_LeaveHeld; tick++) Find.TickManager.DoSingleTick();
                bool out2 = insider.Spawned && !HostageHold.InHeld(map, insider.Position) && insider.Position.AdjacentToCardinal(door.Door.Position);
                bool outsiderLeftAlone = outsider.CurJobDef != RPHostageDefOf.RP_LeaveHeld && !HostageHold.InHeld(map, outsider.Position);
                RPLog.Message($"[RimPrison:test] hostage B5: waited {Math.Max(0, waited - 1)} ticks for a quiet door, same verdict both sides {alike}/reach out from inside {outFromInside}/reach in from outside {inFromOutside}/let out through the door {out2}/outsider left alone {outsiderLeftAlone}");
                if (!alike || outFromInside || inFromOutside || !outsiderLeftAlone || !out2)
                    HostageProblem("B5 doors", $"the held doors' verdict depends on position or staff inside cannot get out (refused both sides {alike}, reaches out from inside {outFromInside}, reaches in from outside {inFromOutside}, let out through the door {out2}, outsider left alone {outsiderLeftAlone}); "
                        + $"checked at door {door.Door.Position} with inner {inner} (held {HostageHold.InHeld(map, inner)}) and outer {outer} (held {HostageHold.InHeld(map, outer)}), the insider started at {s.canteenSpots[6]} (held {HostageHold.InHeld(map, s.canteenSpots[6])}); "
                        + "held doors: " + string.Join(", ", held.Select(d => $"{d.Door.Position} mode {d.mode} open {d.Door.Open} free {d.Door.FreePassage} holdOpen {d.Door.HoldOpen} things {string.Join("/", d.Door.Position.GetThingList(map).Select(t => t.def.defName))}")));
            });
        }

        // ---- E1: a downed hostage stays held --------------------------------------------------------------------------------------

        /// <summary>A leg that put the hostage on the floor does not end the state (vanilla's does, and the hostage got up as a free colonist until the next hour).</summary>
        private static void CheckDownedHostageStaysHeld(Map map)
        {
            HostageCheck("E1 downed", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                HealthUtility.DamageUntilDowned(guard, allowBleedingWounds: false);
                bool still = guard.Downed && !guard.Dead && HostageCrisis.IsHeld(guard) && guard.MentalStateDef == RPHostageDefOf.RP_BeingHeld;
                RPLog.Message($"[RimPrison:test] hostage E1: downed hostage still held {still}");
                if (!still) HostageProblem("E1 downed", $"a downed hostage is no longer held (downed {guard.Downed}, dead {guard.Dead}, hediff {HostageCrisis.IsHeld(guard)}, state {guard.MentalStateDef?.defName})");
            });
        }

        // ---- E2: nothing else removes a held hostage -------------------------------------------------------------------------------

        /// <summary>
        /// Firing (retirement, "too close" and a corruption arrest all end in it) waits for the crisis to end, and a held hostage cannot be arrested
        /// by a drafted colonist next to them.
        /// </summary>
        private static void CheckHeldHostageCannotBeFiredOrArrested(Map map)
        {
            HostageCheck("E2 fired or arrested", map, s =>
            {
                Pawn other = s.Staff(RPSecurityDefOf.RP_Guard);
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                CompStaff gsc = guard.GetComp<CompStaff>();
                bool arrestable = other.CanBeArrestedBy(guard);
                bool arrestRefused = !guard.CanBeArrestedBy(other);
                StaffUtility.Fire(guard, record: false);
                bool kept = gsc.role != null && guard.Faction == Faction.OfPlayer && !arc.done && s.Worker.Valid(arc) && HostageCrisis.IsHeld(guard);
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                bool waited = gsc.role != null && gsc.pendingFire != 0 && !HostageCrisis.IsHeld(guard);
                gsc.ResolvePendingFire();
                bool fired = gsc.role == null && guard.Faction != Faction.OfPlayer;
                RPLog.Message($"[RimPrison:test] hostage E2: control can be arrested {arrestable}/held cannot {arrestRefused}/fire kept the hostage {kept}/fired once free {waited && fired}");
                if (!arrestable || !arrestRefused || !kept || !waited || !fired)
                    HostageProblem("E2 fired or arrested", $"(control arrestable {arrestable}) a held hostage can be arrested {!arrestRefused}, or firing removed them from the crisis (kept while held {kept}, dismissal waited for the end {waited}, done once free {fired})");
            });
        }

        // ---- E5: a hold that dissolves is told --------------------------------------------------------------------------------------

        /// <summary>The room's wall or door is gone, so nothing is held but the crisis goes on: the warden is told once (not every door pass).</summary>
        private static void CheckBrokenHoldIsToldOnce(Map map)
        {
            HostageCheck("E5 hold broken", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                string note = arc.note;
                IntVec3 outside = IntakeSystem.IntakeSpot(map);
                if (HostageCrisis.HoldRoom(outside, map) != null) outside = new IntVec3(3, 0, 3);
                if (HostageCrisis.HoldRoom(outside, map) != null)
                {
                    HostageProblem("E5 hold broken", "could not run (the intake spot is a room to hold)");
                    return;
                }
                int told0 = s.pm.holdBrokenMessages;
                bool heldFirst = s.pm.heldCells != null;
                HostageNote n = HostageNote.Of(arc);
                arc.note = HostageNote.Make(outside, n.wing, n.trigger, n.need);
                s.pm.ForceDoorUpdate();
                s.pm.ForceDoorUpdate();
                bool dissolved = s.pm.heldCells == null;
                int toldOnce = s.pm.holdBrokenMessages - told0;
                arc.note = note;
                s.pm.ForceDoorUpdate();
                bool back = s.pm.heldCells != null;
                RPLog.Message($"[RimPrison:test] hostage E5: held first {heldFirst}/dissolved {dissolved}/told {toldOnce}x/held again {back}");
                if (!heldFirst || !dissolved || toldOnce != 1 || !back)
                    HostageProblem("E5 hold broken", $"a hold that broke was not told exactly once (held to begin with {heldFirst}, dissolved {dissolved}, messages {toldOnce}, held again when restored {back})");
            });
        }

        // ---- E7: nobody shoots the takers --------------------------------------------------------------------------------------------

        /// <summary>With the armed response on violence, a rioter elsewhere is shot at but a hostage-taker is not (one who strays out would be, and killing them all ends it at no cost).</summary>
        private static void CheckArmedResponseSparesTheTakers(Map map)
        {
            HostageCheck("E7 armed response", map, s =>
            {
                ArmedResponsePolicy policy0 = s.g.armedResponse;
                s.g.armedResponse = ArmedResponsePolicy.EscapesAndViolence;
                try
                {
                    StoryArc arc = s.Begin(out Pawn guard, out Pawn boss, followers: 1);
                    if (arc == null) return;
                    Pawn follower = arc.pawns[2];
                    Pawn elsewhere = s.Rioter(s.workshopSpots[0], riot: true);
                    if (elsewhere == null) return;
                    bool control = ArmedResponse.ShouldShoot(elsewhere);
                    bool spared = !ArmedResponse.ShouldShoot(boss) && !ArmedResponse.ShouldShoot(follower);
                    RPLog.Message($"[RimPrison:test] hostage E7: other rioter shot at {control}/takers spared {spared}");
                    if (!control || !spared) HostageProblem("E7 armed response", $"the armed response does not leave the takers alone (a rioter elsewhere would be shot {control}, takers spared {spared})");
                }
                finally
                {
                    s.g.armedResponse = policy0;
                }
            });
        }

        // ---- the game goes on when the hostage was the only colonist on site ---------------------------------------------------------

        /// <summary>
        /// The staff are at home and the hostage is the only colonist on the map when the last deadline kills them: vanilla would end the game, the
        /// patch for staff at home does not. (The crisis' own check runs with the real staff on the map, where nothing could end it.)
        /// </summary>
        private static void CheckHostageDyingAsTheOnlyColonistOnSiteKeepsTheGame(Map map)
        {
            HostageCheck("game over", map, s =>
            {
                PrisonGame g = s.g;
                var sentHome = new List<(Pawn pawn, IntVec3 spot, Pawn dog, IntVec3 dogSpot)>();
                foreach (Pawn other in map.mapPawns.FreeColonistsSpawned.Where(x => x.GetComp<CompStaff>() is { role: not null }).ToList())
                {
                    Pawn dog = other.GetComp<CompStaff>().dog;
                    sentHome.Add((other, other.Position, dog, dog?.Position ?? IntVec3.Invalid));
                    StaffCommute.Leave(other);
                }
                s.r.Add(() =>
                {
                    Find.GameEnder.gameEnding = false;
                    foreach ((Pawn pawn, IntVec3 spot, Pawn dog, IntVec3 dogSpot) in sentHome)
                    {
                        if (g.home.Contains(pawn))
                        {
                            g.home.Remove(pawn);
                            GenSpawn.Spawn(pawn, CellFinder.StandableCellNear(spot, map, 6f), map);
                        }
                        if (dog != null && g.home.Contains(dog))
                        {
                            g.home.Remove(dog);
                            GenSpawn.Spawn(dog, CellFinder.StandableCellNear(dogSpot.IsValid ? dogSpot : spot, map, 6f), map);
                        }
                    }
                    StaffCareers.InvalidateLeads();
                });
                if (sentHome.Count == 0)
                {
                    HostageProblem("game over", "could not run (no staff on site to send home)");
                    return;
                }
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                if (map.mapPawns.FreeColonistsSpawnedCount != 1)
                {
                    HostageProblem("game over", $"could not run (the hostage is not the only colonist on site: {map.mapPawns.FreeColonistsSpawnedCount})");
                    return;
                }
                // The first deadline hurts them; the last kills them.
                int now = Find.TickManager.TicksGame;
                arc.nextTick = now - 1;
                s.Worker.Hourly(arc, map);
                arc.nextTick = now - 1;
                float kill = HostageCrisis.KillChance(arc);
                int seed = s.Seed(() => Rand.Chance(kill));
                if (seed < 0)
                {
                    HostageProblem("game over", "no seed kills the hostage at the last deadline");
                    return;
                }
                Rand.PushState(seed);
                try { s.Worker.Hourly(arc, map); }
                finally { Rand.PopState(); }
                BodyLeft(guard);
                bool alone = guard.Dead && Find.Maps.All(m => m.mapPawns.FreeColonistsSpawnedOrInPlayerEjectablePodsCount == 0);
                Find.GameEnder.gameEnding = false;
                Find.GameEnder.CheckOrUpdateGameOver();
                bool goes = !Find.GameEnder.gameEnding;
                RPLog.Message($"[RimPrison:test] hostage game over: the hostage was the only colonist and died {alone}/the game goes on {goes}");
                if (!alone || !goes) HostageProblem("game over", $"the hostage who was the only colonist on site died and the game ended (set-up held {alone}, game goes on {goes})");
            });
        }

        // ---- UI: the alert, the gizmo, the HELD label -----------------------------------------------------------------------------------

        /// <summary>The alert lists the hostage while held and not after; the hostage's gizmo opens the options at once (and goes with the crisis).</summary>
        private static void CheckHostageAlertGizmoAndLabel(Map map)
        {
            HostageCheck("UI", map, s =>
            {
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                CompStaff gsc = guard.GetComp<CompStaff>();
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                bool alert = PrisonAlerts.Hostages.Contains(guard);
                Command_Action gizmo = gsc.CompGetGizmosExtra().OfType<Command_Action>().FirstOrDefault(c => c.defaultLabel == "RP_Hostage_Decide".Translate().ToString());
                bool gizmoShown = gizmo != null;
                bool opens = false;
                if (gizmo != null)
                {
                    s.r.Add(() =>
                    {
                        foreach (Window w in Find.WindowStack.Windows.Where(x => x is Dialog_NodeTree).ToList()) Find.WindowStack.TryRemove(w, doCloseSound: false);
                    });
                    int before = Find.WindowStack.Windows.Count(w => w is Dialog_NodeTree);
                    gizmo.action();
                    opens = Find.WindowStack.Windows.Count(w => w is Dialog_NodeTree) == before + 1 && s.OpenLetters(arc) == 1;
                }
                // The "HELD" label sits on the hostage, not an arbitrary cell.
                bool labelOnHostage = s.pm.HeldLabelCell == guard.Position;
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                bool alertGone = !PrisonAlerts.Hostages.Contains(guard);
                bool gizmoGone = !gsc.CompGetGizmosExtra().OfType<Command_Action>().Any(c => c.defaultLabel == "RP_Hostage_Decide".Translate().ToString());
                RPLog.Message($"[RimPrison:test] hostage UI: alert {alert}/gizmo {gizmoShown}/opens the options {opens}/label on the hostage {labelOnHostage}/alert gone {alertGone}/gizmo gone {gizmoGone}");
                if (!alert || !gizmoShown || !opens || !labelOnHostage || !alertGone || !gizmoGone)
                    HostageProblem("UI", $"alert listed the hostage {alert}, gizmo shown {gizmoShown}, gizmo opens the options {opens}, label on the hostage {labelOnHostage}, alert gone after {alertGone}, gizmo gone after {gizmoGone}");
            });
        }
    }
}
