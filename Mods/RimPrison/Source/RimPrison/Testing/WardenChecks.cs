using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The warden on the floor (events plan; gap analysis 4, section 1.5): the prison starts with a warden, who is never let go, sent home, shifted, retrained or led astray; the three
    /// abilities (address the canteen, calm and firm; walk the wing; a word with an inmate) with their stated effects and scribed cooldowns, each also through real ticks (the job walks,
    /// waits and does it); what an inmate can be told is only what the warden knows; and the risk: a blow to the warden is a letter, a ringleader takes the warden before anyone else
    /// in reach, a crisis over them is shorter and graver, and a warden who dies (or a prison from before there was one) gets a successor from the state. Self-contained: made-up wardens and
    /// inmates, and what a check touches of the real prison is put back. Called once from the events plan, before <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckWarden(Map map)
        {
            string before = LiveStateDigest(map);
            CheckWardenAtStart(map);
            CheckWardenCot(map);
            CheckWardenProtected(map);
            CheckWardenGizmos(map);
            CheckWardenAddress(map);
            CheckWardenWalk(map);
            CheckWardenWord(map);
            CheckWardenRisk(map);
            CheckWardenVacancy(map);
            // What the review of it found (WardenFixChecks.cs).
            CheckWardenFixes(map);
            // The checks use made-up wardens and inmates, and put back what they touch of the real ones: nothing of the live prison (the real staff's shifts and corruption, a real inmate's tunnel, suspicion, gang
            // or informant, the passes that are counted) is different after them.
            string after = LiveStateDigest(map);
            if (before != after) WardenFail($"the warden checks left the live prison changed: {Diff(before, after)}");
        }

        private static string Diff(string a, string b)
        {
            string[] x = a.Split('|'), y = b.Split('|');
            return string.Join("; ", Enumerable.Range(0, Math.Max(x.Length, y.Length)).Where(i => i >= x.Length || i >= y.Length || x[i] != y[i]).Take(12).Select(i => $"{(i < x.Length ? x[i] : "-")} -> {(i < y.Length ? y[i] : "-")}"));
        }

        /// <summary>What the warden checks must leave as they found it: the real staff and inmates' fields that a shift balance, a corruption pass or a snitch's tip would change, and the counters of the passes.</summary>
        private static string LiveStateDigest(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            var parts = new List<string> { $"day {g.lastShiftBalanceDay}", $"corruption passes {CorruptionSystem.DailyPasses}", $"vacant {g.wardenVacantTick}", $"tips {g.stats.tips}" };
            foreach (Pawn p in StaffCommute.AllStaff(map).OrderBy(x => x.thingIDNumber))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                parts.Add($"staff {p.thingIDNumber} night {sc.nightShift} int {sc.integrity:0.0} sus {sc.suspicion:0.0} corrupt {sc.corrupt} leave {sc.leaveAtTick} role {sc.role?.defName} times {string.Join("", Enumerable.Range(0, 24).Select(h => p.timetable?.GetAssignment(h)?.defName[0] ?? '-'))}");
            }
            foreach (Pawn p in map.mapPawns.PrisonersOfColony.OrderBy(x => x.thingIDNumber))
                if (p.Inmate() is { } c)
                    parts.Add($"inmate {p.thingIDNumber} tunnel {c.tunnelKnown}/{c.cellSearchPending} suspected {c.suspected} gang {c.gangKnown} snitch {c.snitchExposed}/{c.informantPaid} revealed {c.revealed.Count} informedBy {c.informedBy?.thingIDNumber} tension {c.tension:0.0}");
            return string.Join("|", parts);
        }

        private static void WardenFail(string what) => problems0.Add("warden: " + what);

        // (Without case: a letter's label is capitalised, and a name can be lower-case - "von Schild was struck" is shown "Von Schild was struck".)
        private static bool LetterIs(Letter l, TaggedString label) => string.Equals(l.Label.ToString(), label.ToString(), System.StringComparison.OrdinalIgnoreCase);

        /// <summary>A scene of made-up wardens and inmates: what the real inmates carry of the warden (a word, a firm address, a goal backed, what they feel about whom) is put back.</summary>
        private sealed class WardenScene
        {
            public readonly Map map;
            public readonly Rollback r;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly PrisonMap pm;
            /// <summary>The real prison's pawns when the scene began: what a tip, or anything else that looks about it, must not reach.</summary>
            private readonly HashSet<Pawn> real;
            /// <summary>The beds there were: a made-up warden's cot is one that was not (built for them, and taken down again), and a bed that was is only given up.</summary>
            private readonly HashSet<Building_Bed> beds;

            public WardenScene(Map map, Rollback r)
            {
                this.map = map;
                this.r = r;
                pm = PrisonMap.Of(map);
                real = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned.Concat(g.OffSite));
                beds = new HashSet<Building_Bed>(map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>());
                // (A snitch's tip looks at every inmate and every guard within 30 cells: here only at the made-up ones, or it would mark a real tunnel known, flag a real inmate's contraband or put suspicion on a real guard.)
                InformantSystem.TipPool = p => !real.Contains(p);
                r.Add(() => InformantSystem.TipPool = null);
                foreach (CompInmate c in map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).ToList())
                {
                    (float suppressed, int word, int headsUp, int firm, float tension) = (c.suppressed, c.wardenWordTick, c.headsUpTick, c.wardenFirmUntil, c.tension);
                    List<StaffMemory> memories = c.staffMemories.ToList();
                    List<(StaffMemory m, float score, int tick)> kept = memories.Select(m => (m, m.score, m.lastTick)).ToList();
                    InmateWant want = c.want;
                    (bool backed, int deadline) = want == null ? (false, -1) : (want.backed, want.deadlineTick);
                    r.Add(() =>
                    {
                        (c.suppressed, c.wardenWordTick, c.headsUpTick, c.wardenFirmUntil, c.tension) = (suppressed, word, headsUp, firm, tension);
                        c.staffMemories = memories;
                        foreach ((StaffMemory m, float score, int tick) in kept) (m.score, m.lastTick) = (score, tick);
                        if (want != null) (want.backed, want.deadlineTick) = (backed, deadline);
                    });
                }
                int vacant = g.wardenVacantTick;
                r.Add(() => g.wardenVacantTick = vacant);
            }

            public int Now => Find.TickManager.TicksGame;

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather?.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>A made-up warden on site (not the real one: that one is never touched).</summary>
            public Pawn Warden(IntVec3? at = null)
            {
                Pawn w = ThrowawayStaff(map, r, RPWardenDefOf.RP_Warden);
                r.Add(() =>
                {
                    if (w.ownership?.OwnedBed is not { } bed) return;
                    if (beds.Contains(bed)) w.ownership.UnclaimBed();
                    else if (!bed.Destroyed) bed.Destroy();
                });
                if (at.HasValue) Place(w, at.Value);
                return w;
            }

            /// <summary>A made-up inmate on their feet and active, at <paramref name="at"/> (the intake when null), with the given tension.</summary>
            public Pawn Inmate(IntVec3? at = null, float tension = 30f)
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.want = null;
                c.processed = true;
                c.assessed = true;
                // (A made-up inmate rolls a gang at intake, and may be its leader: neither is kept.)
                c.gang = null;
                c.gangLeader = false;
                c.gangRank = 0;
                c.fear = 0f;
                c.tension = tension;
                c.suppressed = 0f;
                c.RefreshActiveFlag();
                c.activeFlag = true;
                if (at.HasValue) Place(p, at.Value);
                return p;
            }

            /// <summary>Holds an inmate where they stand (the regime would walk them off at the next hour).</summary>
            public void Pin(Pawn p)
            {
                if (p.CurJobDef == JobDefOf.Wait) return;
                Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                wait.expiryInterval = 6000;
                p.jobs.StartJob(wait, JobCondition.InterruptForced);
            }

            /// <summary>Real ticks until the condition holds (the inmates in <paramref name="pinned"/> kept where they are), at most <paramref name="max"/>; returns the ticks used.</summary>
            public int Run(Func<bool> done, int max, params Pawn[] pinned)
            {
                int ticks = 0;
                while (ticks < max && !done())
                {
                    // (Every few ticks: at an hour's change the regime ends their job, and at night they set off for their cells, which is a cell or two from the canteen in a short while.)
                    // (And calm: a made-up inmate at tension 50 broke into a vandalism in the middle of the warden's address twice in a day's census, and an inmate in a mental state does not
                    // hear the warden. The checks are about the warden, not the inmates' breaks.)
                    if (ticks % 5 == 0)
                        foreach (Pawn p in pinned)
                        {
                            if (p.InMentalState) p.MentalState.RecoverFromState();
                            Pin(p);
                        }
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                return ticks;
            }

            public bool Has(Pawn p, ThoughtDef d) => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(d) != null;

            /// <summary>Free standable cells of a zone's painted cells, the warden can reach from where they are.</summary>
            public List<IntVec3> Cells(PrisonZoneDef zone, Pawn who) => pm.zones.CellsOf(zone).Where(c => c.Standable(map) && c.GetFirstPawn(map) == null && who.CanReach(c, PathEndMode.OnCell, Danger.Deadly)).ToList();
        }

        // ---- the start --------------------------------------------------------------------------------------------------------

        /// <summary>The prison starts with a warden in post: one, on staff, out of the payroll, on the player's side, and not one that could be hired.</summary>
        private static void CheckWardenAtStart(Map map)
        {
            Guarded("warden at start", map, r =>
            {
                List<Pawn> wardens = StaffCommute.AllStaff(map).Where(WardenSystem.IsWarden).ToList();
                if (wardens.Count != 1)
                {
                    WardenFail($"{wardens.Count} wardens on the staff at the start (expected one)");
                    return;
                }
                Pawn w = wardens[0];
                CompStaff sc = w.GetComp<CompStaff>();
                if (WardenSystem.InPost(map) != w || w.Faction != Faction.OfPlayer || !w.Spawned || w.Dead) WardenFail("the start's warden is not in post, on the player's side and on the map");
                if (StaffCareers.Wage(sc) != 0 || sc.role.hireable || StaffUtility.Roles.Contains(sc.role)) WardenFail($"the warden is on the payroll or can be hired (wage {StaffCareers.Wage(sc)}, hireable {sc.role.hireable})");
                if (sc.hiredTick == CompStaff.NotHired) WardenFail("the warden has no hiring tick (no role was applied)");
                // They never go home, so they sleep here: in a bed of their own (the prison has a staff-only room to put one in: it is what the cot is built in).
                if (DefDatabase<PrisonZoneDef>.GetNamedSilentFail("RP_StaffOnly") is { } staffOnly && PrisonMap.Of(map).zones.CellsOf(staffOnly).Count > 0 && (w.ownership?.OwnedBed == null || w.ownership.OwnedBed.ForPrisoners || w.ownership.OwnedBed.Medical))
                    WardenFail("the warden has no bed of their own (they never go home, and would sleep on the floor)");
                if (w.workSettings != null && DefDatabase<WorkTypeDef>.AllDefsListForReading.Any(t => !t.defName.StartsWith("Patient") && t != WorkTypeDefOf.Firefighter && t.defName != "BasicWorker" && w.workSettings.GetPriority(t) > 0))
                    WardenFail("the warden has a job on the work tab (they are the player on the floor, not a worker)");
            });
        }

        // ---- a bed to sleep in --------------------------------------------------------------------------------------------------

        /// <summary>The warden never goes home, so they sleep here: a cot in a staff-only room (not the staff room), theirs alone, and the walk to it the hour before the timetable says sleep.</summary>
        private static void CheckWardenCot(Map map)
        {
            Guarded("warden cot", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                int beds0 = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().Count();
                Building_Bed cot = WardenSystem.EnsureCot(w);
                if (cot == null)
                {
                    WardenFail("no cot was found or built for a warden with no bed (a staff-only room is painted)");
                    return;
                }
                r.Add(() =>
                {
                    if (cot.Spawned) cot.Destroy();
                });
                PrisonZoneDef zone = s.pm.zones.ZoneAt(cot.Position);
                Room room = cot.Position.GetRoom(map);
                if (!cot.OwnersForReading.Contains(w) || w.ownership.OwnedBed != cot || cot.ForPrisoners || cot.Medical) WardenFail("the warden's cot is not theirs alone, or is a prisoners' or a medical bed");
                if (zone == RPStaffRoomDefOf.RP_StaffRoom || zone is { inmateAccess: true } || room == null || room.PsychologicallyOutdoors || room.IsPrisonCell || !cot.Position.Roofed(map))
                    WardenFail($"the cot is in the wrong place: zone {zone?.defName ?? "none"}, indoors {room is { PsychologicallyOutdoors: false }}, a prison cell {room?.IsPrisonCell}, roofed {cot.Position.Roofed(map)}");
                if (WardenSystem.EnsureCot(w) != cot || map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().Count() != beds0 + 1) WardenFail("a second look for a bed built another");

                // The hour before sleep: they walk to it (and not when held, or when they are there already). Real ticks.
                int hour = (GenLocalDate.HourOfDay(map) + 5) % 24;
                w.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                w.timetable.SetAssignment((hour + 1) % 24, TimeAssignmentDefOf.Sleep);
                // (Ten cells or so off, indoors: more than the four from which they are already in bed, and a walk that takes a few hundred ticks, not hours of the plan's day.)
                IntVec3? far = FarFrom(map, w, cot.Position);
                if (far == null)
                {
                    WardenFail("could not run the walk to bed (no reachable cell far from the cot)");
                    return;
                }
                s.Place(w, far.Value);
                HediffDef held = RPHostageDefOf.RP_Hostage;
                w.health.AddHediff(held);
                bool whileHeld = WardenSystem.Retire(w);
                w.health.RemoveHediff(w.health.hediffSet.GetFirstHediffOfDef(held));
                if (whileHeld) WardenFail("a held warden was sent to bed");
                if (WardenSystem.RetireHour(w, (hour + 3) % 24) || WardenSystem.RetireHour(w, (hour + 1) % 24) || !WardenSystem.RetireHour(w, hour)) WardenFail("the hour before sleep is not told from the others by the warden's timetable");
                bool went = WardenSystem.Retire(w);
                if (!went || w.CurJobDef != RPWardenDefOf.RP_WardenRetire) WardenFail($"the warden was not sent to bed the hour before sleep (job {w.CurJobDef?.defName ?? "none"})");
                if (WardenSystem.Retire(w)) WardenFail("the warden was sent to bed twice at once");

                // Their bedroom is nobody's to envy: a colonist with no room is not jealous of the warden's (any room beats none, and the staff own none), and is of a guard's (the control). The rule on
                // rooms given by hand (the room of a cot in an office is nobody's bedroom: vanilla's room roles), and the vanilla thought itself where the cot does stand in a bedroom.
                Pawn envious = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), owner = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Room theRoom = RPDefOf.RP_Canteen == null ? null : s.pm.zones.CellsOf(RPDefOf.RP_Canteen).Select(c => c.GetRoom(map)).FirstOrDefault(x => x != null);
                if (theRoom == null) WardenFail("could not run the jealousy rule (no canteen room to give out)");
                else
                {
                    Pawn envied = Patch_ThoughtWorker_BedroomJealous_Warden.Envied(envious, x => x == w || x == owner ? theRoom : null);
                    if (envied != owner) WardenFail($"(control) a colonist with no room does not envy a guard's room (envied {envied?.LabelShort ?? "nobody"})");
                    owner.ownership.UnclaimBed();
                    envied = Patch_ThoughtWorker_BedroomJealous_Warden.Envied(envious, x => x == w ? theRoom : null);
                    if (envied != null) WardenFail($"a colonist envies the warden's room ({envied.LabelShort})");
                }
                ThoughtDef jealous = DefDatabase<ThoughtDef>.GetNamed("Jealous");
                if (w.ownership.OwnedRoom != null)
                {
                    envious.story.traits.GainTrait(new Trait(TraitDefOf.Jealous));
                    if (jealous.Worker.CurrentState(envious).Active) WardenFail("a member of staff with the Jealous trait is jealous of the warden's bedroom in the game");
                }
                else RPLog.Message("[RimPrison:test] warden jealousy: the vanilla thought was not run (the cot is in a room that is nobody's bedroom: the rule on rooms given by hand above is what ran)");
            });
        }

        /// <summary>A roofed, free cell the pawn can reach, about ten cells from <paramref name="from"/> and at least six (null when there is none: <c>FirstOrDefault</c> of cells gives (0,0,0), which is a valid cell).</summary>
        private static IntVec3? FarFrom(Map map, Pawn who, IntVec3 from) =>
            GenRadial.RadialCellsAround(from, 14f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.Roofed(map) && c.GetFirstPawn(map) == null && c.DistanceTo(from) >= 6f && who.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                .OrderBy(c => Mathf.Abs(c.DistanceTo(from) - 10f)).Select(c => (IntVec3?)c).FirstOrDefault();

        // ---- protection ---------------------------------------------------------------------------------------------------------

        private static void CheckWardenProtected(Map map)
        {
            Guarded("warden protected", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                Pawn w = s.Warden();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff sc = w.GetComp<CompStaff>(), gc = guard.GetComp<CompStaff>();
                int now = s.Now;
                // Sent home: not off shift, not on leave, not suspended; a guard in the same state is.
                int hour = (GenLocalDate.HourOfDay(map) + 3) % 24;
                foreach (Pawn p in new[] { w, guard }) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                if (StaffCommute.ShouldBeHome(w, sc, hour)) WardenFail("the warden is sent home at an hour their timetable says sleep");
                if (!StaffCommute.ShouldBeHome(guard, gc, hour)) WardenFail("(control) a guard is not sent home at an hour their timetable says sleep");
                foreach (Pawn p in new[] { w, guard }) p.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                sc.leaveUntilTick = gc.leaveUntilTick = now + GenDate.TicksPerDay;
                if (StaffCommute.ShouldBeHome(w, sc, hour)) WardenFail("the warden is sent home on leave");
                if (!StaffCommute.ShouldBeHome(guard, gc, hour)) WardenFail("(control) a guard on leave is not sent home");
                sc.leaveUntilTick = gc.leaveUntilTick = -1;
                CorruptionSystem.Suspend(w, sc);
                CorruptionSystem.Discipline(w, sc);
                CorruptionSystem.StandDown(w, sc, now + GenDate.TicksPerDay);
                if (sc.suspendedUntilTick >= 0 || sc.disciplineUntilTick >= 0 || sc.standDownUntilTick >= 0 || CorruptionSystem.Suspended(sc)) WardenFail("the warden was suspended, disciplined or stood down");
                CorruptionSystem.Suspend(guard, gc);
                if (!CorruptionSystem.Suspended(gc)) WardenFail("(control) a guard was not suspended by the same call");
                gc.suspendedUntilTick = -1;
                // Corruption never touches them: no integrity is rolled, nobody approaches them, the daily pass leaves them alone.
                sc.integrity = -1f;
                CorruptionSystem.Hourly(w, sc);
                // (The daily pass drifts every real member of staff and counts a pass: both are put back, or the run's "the daily corruption pass never ran" would pass on this one.)
                int dailyPasses = CorruptionSystem.DailyPasses;
                List<(CompStaff c, float integrity, float suspicion)> drifted = StaffCommute.AllStaff(map).Select(p => p.GetComp<CompStaff>()).Select(c => (c, c.integrity, c.suspicion)).ToList();
                CorruptionSystem.Daily(map);
                CorruptionSystem.DailyPasses = dailyPasses;
                foreach ((CompStaff c, float integrity, float suspicion) in drifted)
                    if (c != gc) (c.integrity, c.suspicion) = (integrity, suspicion);
                if (sc.integrity >= 0f || sc.corrupt || sc.suspicion > 0f) WardenFail($"corruption reached the warden (integrity {sc.integrity}, corrupt {sc.corrupt}, suspicion {sc.suspicion})");
                if (gc.integrity < 0f) WardenFail("(control) the corruption passes rolled no integrity for a guard");
                // No night shift, however it was left: the balance puts them back on days, and the timetable sleeps at night. (The pass counts the made-up guard in the night split and writes every real
                // member of staff's timetable: what it changes is put back, so a real guard is not on nights for the rest of the run.)
                int balanceDay = g.lastShiftBalanceDay;
                List<(Pawn p, bool night)> shifts = StaffCommute.AllStaff(map).Select(p => (p, p.GetComp<CompStaff>().nightShift)).ToList();
                r.Add(() =>
                {
                    g.lastShiftBalanceDay = balanceDay;
                    foreach ((Pawn p, bool night) in shifts)
                        if (!p.Destroyed && p.GetComp<CompStaff>() is { } shiftSc && !shiftSc.role.warden)
                        {
                            shiftSc.nightShift = night;
                            ShiftSystem.Apply(p, night);
                        }
                });
                // (Three more guards, and the day not yet balanced: the pass is due, and a third of a bigger roster works nights, so a real guard is flipped to nights unless it is put back.)
                for (int extra = 0; extra < 3; extra++) ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                shifts = StaffCommute.AllStaff(map).Select(p => (p, p.GetComp<CompStaff>().nightShift)).ToList();
                g.lastShiftBalanceDay = -1;
                sc.nightShift = true;
                ShiftSystem.Balance(map, g);
                // (A night shift sleeps in the day, ten to six: the warden sleeps at night, whatever the guards do, and not in the day.)
                if (sc.nightShift || w.timetable.GetAssignment(3) != TimeAssignmentDefOf.Sleep || w.timetable.GetAssignment(23) != TimeAssignmentDefOf.Sleep || w.timetable.GetAssignment(12) == TimeAssignmentDefOf.Sleep || w.timetable.GetAssignment(15) == TimeAssignmentDefOf.Sleep)
                    WardenFail($"the warden was put on a night shift (night {sc.nightShift}, 3h {w.timetable.GetAssignment(3)?.defName}, 12h {w.timetable.GetAssignment(12)?.defName})");
                // Careers, life, strikes and courses leave them out; a guard is in all of them.
                // (Twelve days of service, which the game's first hours are short of: the tick is that far back, as a negative one if need be.)
                sc.hiredTick = gc.hiredTick = s.Now - 12 * GenDate.TicksPerDay;
                sc.roleSinceTick = gc.roleSinceTick = -1;
                foreach (Pawn p in new[] { w, guard }) p.needs.mood.CurLevel = 0.7f;
                if (StaffCareers.Eligible(w, sc)) WardenFail("the warden is up for promotion");
                if (!StaffCareers.Eligible(guard, gc)) WardenFail("(control) a guard with twelve days of service is not up for promotion");
                if (StaffLives.Storied(map).Contains(w)) WardenFail("life happens to the warden (burnout, debt, retirement)");
                if (!StaffLives.Storied(map).Contains(guard)) WardenFail("(control) life does not happen to a guard on site");
                if (IncidentWorker_StaffStrike.Staff(map).Contains(w)) WardenFail("the warden is among the staff who strike");
                if (!IncidentWorker_StaffStrike.Staff(map).Contains(guard)) WardenFail("(control) a guard is not among the staff who strike");
                if (StaffTraining.CannotGo(w, sc) != "RP_TrainingWarden".Translate().ToString()) WardenFail($"the warden can be sent on a course, or is turned down for another reason ({StaffTraining.CannotGo(w, sc) ?? "no reason"})");
                // Nothing to change about them in the staff tab either: no retraining, no letting go in the gizmos.
                List<string> labels = sc.CompGetGizmosExtra().OfType<Command>().Select(c => c.defaultLabel).ToList();
                if (labels.Contains("RP_ChangeRole".Translate()) || labels.Contains("RP_PatrolAnywhere".Translate())) WardenFail("the warden has the retrain-or-fire gizmo or a patrol zone");
                if (!gc.CompGetGizmosExtra().OfType<Command>().Any(c => c.defaultLabel == "RP_ChangeRole".Translate())) WardenFail("(control) a guard has no retrain-or-fire gizmo");
                // Let go: refused, with a message, and nothing is recorded (last: a warden who was let go has no role for the rest to look at).
                int entries = r.NewEntries.Count();
                StaffUtility.Fire(w);
                if (sc.role == null || !w.Spawned || w.Faction != Faction.OfPlayer || sc.pendingFire != 0 || r.NewEntries.Count() != entries) WardenFail("the warden was let go (or the attempt left a mark)");
                StaffUtility.Fire(w, arrest: true, record: false);
                if (sc.role == null || w.Faction != Faction.OfPlayer) WardenFail("the warden was let go with an arrest");
            });
        }

        // ---- the gizmos -------------------------------------------------------------------------------------------------------

        private static void CheckWardenGizmos(Map map)
        {
            Guarded("warden gizmos", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                string[] labels = { "RP_WardenAddressGizmo".Translate(), "RP_WardenWalkGizmo".Translate(), "RP_WardenWordGizmo".Translate() };
                List<Command> mine = sc.CompGetGizmosExtra().OfType<Command>().Where(c => labels.Contains(c.defaultLabel)).ToList();
                if (mine.Count != 3) WardenFail($"the warden has {mine.Count} of the three ability gizmos");
                if (guard.GetComp<CompStaff>().CompGetGizmosExtra().OfType<Command>().Any(c => labels.Contains(c.defaultLabel))) WardenFail("a guard has the warden's gizmos");
                // Ready: all three on offer (a canteen and a wing are painted).
                foreach (Command c in mine)
                    if (c.Disabled) WardenFail($"\"{c.defaultLabel}\" is disabled for a warden who is ready: {c.disabledReason}");
                // Held: all three are off, with the reason.
                HediffDef held = RPHostageDefOf.RP_Hostage;
                w.health.AddHediff(held);
                r.Add(() =>
                {
                    if (w.health.hediffSet.GetFirstHediffOfDef(held) is { } h) w.health.RemoveHediff(h);
                });
                foreach (Command c in sc.CompGetGizmosExtra().OfType<Command>().Where(c => labels.Contains(c.defaultLabel)))
                    if (!c.Disabled || c.disabledReason != "RP_WardenWhy_Held".Translate().ToString()) WardenFail($"\"{c.defaultLabel}\" is not off, with the reason, for a held warden ({c.Disabled}: {c.disabledReason})");
                w.health.RemoveHediff(w.health.hediffSet.GetFirstHediffOfDef(held));
                // Cooling down: each shows the hours left, and is off.
                sc.addressReadyTick = sc.walkReadyTick = sc.wordReadyTick = s.Now + 3 * GenDate.TicksPerHour;
                foreach (Command c in sc.CompGetGizmosExtra().OfType<Command>().Where(c => labels.Contains(c.defaultLabel)))
                    if (!c.Disabled || !c.disabledReason.Contains("3")) WardenFail($"\"{c.defaultLabel}\" is not off, with the hours left, while cooling down ({c.Disabled}: {c.disabledReason})");
                sc.addressReadyTick = sc.walkReadyTick = sc.wordReadyTick = -1;
            });
        }

        // ---- address the canteen ------------------------------------------------------------------------------------------------

        private static void CheckWardenAddress(Map map)
        {
            Guarded("warden address", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                List<IntVec3> canteen = s.Cells(RPDefOf.RP_Canteen, w);
                if (canteen.Count < 4)
                {
                    WardenFail($"could not run (room to stand in the canteen: {canteen.Count} cells)");
                    return;
                }
                IntVec3 outside = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                Pawn a = s.Inmate(canteen[0], 50f), b = s.Inmate(canteen[1], 50f), away = s.Inmate(outside, 50f);
                foreach (Pawn p in new[] { a, b, away }) p.Inmate().suppressed = 40f;
                s.Place(w, canteen[2]);
                int others = WardenSystem.InCanteen(map, WardenSystem.CanteenOf(w)).Count(p => p != a && p != b);
                int now = s.Now;

                // Calm: tension down 10 and suppression down 15, for everyone in the canteen and no one outside it; a kind thought and a little trust; the cooldown starts.
                int heard = WardenSystem.Address(w, firm: false);
                CompInmate ac = a.Inmate(), bc = b.Inmate(), awayc = away.Inmate();
                if (heard != 2 + others) WardenFail($"a calm address was heard by {heard} (expected {2 + others}: two made-up and {others} real)");
                if (Mathf.Abs(ac.tension - 40f) > 0.01f || Mathf.Abs(bc.tension - 40f) > 0.01f || Mathf.Abs(ac.suppressed - 25f) > 0.01f || Mathf.Abs(bc.suppressed - 25f) > 0.01f)
                    WardenFail($"calm: tension {ac.tension}/{bc.tension} (expected 40), suppression {ac.suppressed}/{bc.suppressed} (expected 25)");
                if (awayc.tension != 50f || awayc.suppressed != 40f) WardenFail($"calm: an inmate outside the canteen was affected (tension {awayc.tension}, suppression {awayc.suppressed})");
                if (!s.Has(a, RPWardenDefOf.RP_WardenCalmed) || StaffMemories.ScoreFor(ac, w) < 1.9f) WardenFail("calm: no kind thought, or no trust, in an inmate who heard it");
                if (sc.addressReadyTick < now + (WardenSystem.AddressCooldownHours - 1) * GenDate.TicksPerHour) WardenFail($"calm: the cooldown did not start ({sc.addressReadyTick - now} ticks)");
                if (WardenSystem.Unready(w, sc.addressReadyTick) == null) WardenFail("calm: the warden is ready again at once");
                sc.addressReadyTick = now - 1;
                if (WardenSystem.Unready(w, sc.addressReadyTick) != null) WardenFail($"calm: not ready when the cooldown has run out ({WardenSystem.Unready(w, sc.addressReadyTick)})");

                // Firm: suppression up 20, the tension creeps up after (a hour of it is more than the same hour without), the cooldown starts.
                foreach (CompInmate c in new[] { ac, bc }) (c.tension, c.suppressed) = (50f, 40f);
                heard = WardenSystem.Address(w, firm: true);
                if (heard != 2 + others || Mathf.Abs(ac.suppressed - 60f) > 0.01f || Mathf.Abs(bc.suppressed - 60f) > 0.01f || Mathf.Abs(ac.tension - 50f) > 0.01f)
                    WardenFail($"firm: heard {heard}, suppression {ac.suppressed}/{bc.suppressed} (expected 60), tension {ac.tension} (expected unchanged 50)");
                if (ac.wardenFirmUntil < now + (WardenSystem.FirmHours - 1) * GenDate.TicksPerHour || awayc.wardenFirmUntil >= 0) WardenFail($"firm: the creep did not start for those who heard it (until {ac.wardenFirmUntil - now} ticks), or started for one who did not");
                if (!s.Has(a, RPWardenDefOf.RP_WardenFirm) || StaffMemories.ScoreFor(ac, w) > 0f) WardenFail("firm: no sore thought, or no resentment, in an inmate who heard it");
                if (sc.addressReadyTick < now + (WardenSystem.AddressCooldownHours - 1) * GenDate.TicksPerHour) WardenFail("firm: the cooldown did not start");
                (float withoutFirm, float withFirm) = (0f, 0f);
                // (Run on the one inmate twice, from the same state: the hour's difference is the creep.)
                PrisonMap pm = s.pm;
                int firmUntil = ac.wardenFirmUntil;
                ac.wardenFirmUntil = -1;
                (ac.tension, ac.suppressed) = (50f, 20f);
                TensionUtility.HourlyUpdate(a, ac, pm);
                withoutFirm = ac.tension;
                ac.wardenFirmUntil = firmUntil;
                (ac.tension, ac.suppressed) = (50f, 20f);
                TensionUtility.HourlyUpdate(a, ac, pm);
                withFirm = ac.tension;
                if (withFirm - withoutFirm < 0.5f) WardenFail($"firm: an hour of the creep added {withFirm - withoutFirm:0.00} tension (expected about {WardenSystem.FirmTensionPerHour}, bent by who they are)");
                ac.wardenFirmUntil = -1;

                // Another canteen across the prison (painted for the check, where nobody real is): the warden in it is heard by those in it and by nobody in the first, and in an empty one nothing is
                // done and the cooldown does not start.
                sc.addressReadyTick = -1;
                s.Place(a, canteen[0]);
                s.Place(b, canteen[1]);
                List<IntVec3> second = PaintSecondCanteen(s, w);
                if (second == null)
                {
                    WardenFail("could not run the second canteen (no free ground of four cells away from the first)");
                    return;
                }
                Pawn far = s.Inmate(second[0], 50f);
                (ac.tension, bc.tension) = (50f, 50f);
                s.Place(w, second[1]);
                int farHeard = WardenSystem.Address(w, firm: false);
                if (farHeard != 1 || Mathf.Abs(far.Inmate().tension - 40f) > 0.01f || ac.tension != 50f || bc.tension != 50f)
                    WardenFail($"an address in a second canteen was heard by {farHeard} (expected the one inmate in it), their tension {far.Inmate().tension} (expected 40), the first canteen's {ac.tension}/{bc.tension} (expected 50)");
                s.Place(far, outside);
                sc.addressReadyTick = -1;
                if (WardenSystem.Address(w, firm: false) != 0 || sc.addressReadyTick != -1) WardenFail("an address to an empty canteen was heard, or cost the cooldown");
                s.Place(w, canteen[2]);
                (far.Inmate().tension, ac.tension) = (50f, 50f);
                s.Place(far, second[0]);
                int mainHeard = WardenSystem.Address(w, firm: false);
                if (mainHeard != 2 + others || Mathf.Abs(far.Inmate().tension - 50f) > 0.01f || Mathf.Abs(ac.tension - 40f) > 0.01f)
                    WardenFail($"an address in the first canteen was heard by {mainHeard} (expected {2 + others}) and reached an inmate in the second (tension {far.Inmate().tension}, expected 50)");
            });
        }

        /// <summary>
        /// Four free cells of ground (no zone, standable, a dozen cells or more from any canteen cell) painted as a canteen of its own, put back by the scene's rollback; the cells, or null when there is no such
        /// ground. The warden can stand in it (that is all the check asks of it).
        /// </summary>
        private static List<IntVec3> PaintSecondCanteen(WardenScene s, Pawn w)
        {
            List<IntVec3> canteen = s.pm.zones.CellsOf(RPDefOf.RP_Canteen).ToList();
            foreach (IntVec3 c in s.map.AllCells.OrderBy(x => (x - w.Position).LengthHorizontalSquared))
            {
                var block = new List<IntVec3> { c, c + new IntVec3(1, 0, 0), c + new IntVec3(0, 0, 1), c + new IntVec3(1, 0, 1) };
                if (block.Any(x => !x.InBounds(s.map) || !x.Standable(s.map) || s.pm.zones.ZoneAt(x) != null || x.GetFirstPawn(s.map) != null || canteen.Any(k => k.DistanceTo(x) < 12f))) continue;
                foreach (IntVec3 x in block) s.pm.zones.Set(x, RPDefOf.RP_Canteen);
                s.r.Add(() =>
                {
                    foreach (IntVec3 x in block) s.pm.zones.Set(x, null);
                });
                return block;
            }
            return null;
        }

        // ---- walk the wing ----------------------------------------------------------------------------------------------------

        private static void CheckWardenWalk(Map map)
        {
            Guarded("warden walk", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                PrisonZoneDef wing = WardenSystem.Wings(map).OrderByDescending(z => s.pm.zones.CellsOf(z).Count).FirstOrDefault();
                if (wing == null)
                {
                    WardenFail("could not run (no wing is painted)");
                    return;
                }
                List<IntVec3> cells = s.Cells(wing, w);
                if (cells.Count < 4)
                {
                    WardenFail($"could not run (room to stand in the {wing.defName}: {cells.Count} cells)");
                    return;
                }
                s.Place(w, cells[cells.Count / 2]);
                // The plan: stops in the wing, apart from one another, reachable, and more than one when the wing is big enough.
                List<IntVec3> stops = WardenSystem.WalkPlan(w, wing);
                if (stops.Count == 0 || stops.Any(c => s.pm.zones.ZoneAt(c) != wing || !w.CanReach(c, PathEndMode.OnCell, Danger.Deadly))) WardenFail($"the walk plan: {stops.Count} stops, not all in the wing and reachable");
                for (int i = 0; i < stops.Count; i++)
                    for (int j = i + 1; j < stops.Count; j++)
                        if (stops[i].DistanceTo(stops[j]) < WardenSystem.WalkSpacing - 1.5f) WardenFail($"two stops of the walk are {stops[i].DistanceTo(stops[j]):0.0} cells apart");
                if (cells.Count >= 40 && cells.Max(c => c.DistanceTo(cells[0])) > 2f * WardenSystem.WalkSpacing && stops.Count < 2) WardenFail("a big wing got one stop");

                // Who the walk meets: a calm inmate trusts more, a wound-up one resents it, one in between feels nothing; one outside the wing is not met.
                IntVec3 first = stops[0];
                List<IntVec3> near = GenRadial.RadialCellsAround(first, 3f, true).Where(c => c.InBounds(map) && c.Standable(map) && s.pm.zones.ZoneAt(c) == wing && c.GetFirstPawn(map) == null).ToList();
                if (near.Count < 4)
                {
                    WardenFail($"could not run (room to stand near the first stop: {near.Count} cells)");
                    return;
                }
                Pawn calm = s.Inmate(near[0], 20f), restless = s.Inmate(near[1], 90f), middling = s.Inmate(near[2], 55f);
                Pawn elsewhere = s.Inmate(IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), 20f);
                s.Place(w, first);
                List<Pawn> metNow = WardenSystem.NearOnWing(w, wing);
                if (!metNow.Contains(calm) || !metNow.Contains(restless) || !metNow.Contains(middling) || metNow.Contains(elsewhere)) WardenFail($"who is in earshot on the wing: {metNow.Count} inmates (calm {metNow.Contains(calm)}, restless {metNow.Contains(restless)}, between {metNow.Contains(middling)}, one outside the wing {metNow.Contains(elsewhere)})");
                if (WardenSystem.Meet(w, calm) != 1 || WardenSystem.Meet(w, restless) != -1 || WardenSystem.Meet(w, middling) != 0) WardenFail("a meeting went the wrong way for a calm, a restless or a middling inmate");
                (float cs, float rs, float ms) = (StaffMemories.ScoreFor(calm.Inmate(), w), StaffMemories.ScoreFor(restless.Inmate(), w), StaffMemories.ScoreFor(middling.Inmate(), w));
                if (Mathf.Abs(cs - 3f) > 0.01f || Mathf.Abs(rs + 4f) > 0.01f || ms != 0f) WardenFail($"trust after a meeting: calm {cs} (expected 3), restless {rs} (expected -4), between {ms} (expected 0)");

                // The end of the walk: a snitch near passes on a tip in person (the same letter as the daily one), and a complaint not yet on the desk is raised.
                Pawn snitch = s.Inmate(near[3], 20f), digger = s.Inmate(near[3], 20f);
                snitch.Inmate().reputations.Add(ReputationDefOf.RP_Snitch);
                r.Add(() => snitch.Inmate().reputations.Remove(ReputationDefOf.RP_Snitch));
                bool tipped = false;
                int tries = 0;
                for (; tries < 40 && !tipped; tries++)
                {
                    digger.Inmate().tunnelProgress = 0.3f;
                    digger.Inmate().tunnelKnown = false;
                    snitch.Inmate().snitchExposed = false;
                    tipped = WardenSystem.FinishWalk(w, new List<Pawn> { snitch }, 0, 0, wing).tipped;
                }
                if (!tipped || !r.NewLetters.Any(l => LetterIs(l, "RP_TipLabel".Translate()))) WardenFail($"a snitch on the walk never passed on a tip in {tries} walks (letter {r.NewLetters.Any(l => LetterIs(l, "RP_TipLabel".Translate()))})");
                if (sc.walkReadyTick < s.Now + (WardenSystem.WalkCooldownHours - 1) * GenDate.TicksPerHour) WardenFail("the walk's cooldown did not start");
                Pawn officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                // (The real prison's own complaints on the desk would fill the two places there are for letters: the check has the desk to itself.)
                List<Complaint> complaints0 = g.story.complaints;
                g.story.complaints = new List<Complaint>();
                r.Add(() => g.story.complaints = complaints0);
                Complaint cm = MakeFiledComplaint(g, calm, officer);
                r.Add(() => g.story.complaints.Remove(cm));
                if (ComplaintSystem.RaiseInPerson(g, map, elsewhere)) WardenFail("a complaint was raised in person from an inmate who has none");
                int letters = r.NewLetters.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_Complaint"));
                (bool _, int raised) = WardenSystem.FinishWalk(w, new List<Pawn> { calm }, 0, 0, wing);
                if (raised != 1 || cm.stage != ComplaintStage.Waiting || r.NewLetters.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_Complaint")) != letters + 1)
                    WardenFail($"a filed complaint was not raised in person: {raised} raised, stage {cm.stage}");

                // The order: the warden is sent on the plan's stops.
                sc.walkReadyTick = -1;
                s.Place(w, cells[cells.Count / 2]);
                if (!WardenOrders.Walk(w, wing) || w.CurJobDef != RPWardenDefOf.RP_WardenWalk || w.CurJob.targetQueueA.Count != stops.Count) WardenFail($"the walk order was refused, or did not send the warden on the plan's {stops.Count} stops (job {w.CurJobDef?.defName ?? "none"})");
            });
        }

        private static int wardenComplaintId = 98000;

        /// <summary>A complaint filed and not yet on the desk, against an officer, by an inmate (made up; the check takes it out again).</summary>
        private static Complaint MakeFiledComplaint(PrisonGame g, Pawn inmate, Pawn officer)
        {
            var cm = new Complaint
            {
                id = wardenComplaintId++,
                inmateId = inmate.thingIDNumber,
                officerId = officer.thingIDNumber,
                inmateName = inmate.LabelShort,
                officerName = officer.LabelShort,
                kind = ComplaintKind.Force,
                isTrue = false,
                stage = ComplaintStage.Filed,
                filedTick = Find.TickManager.TicksGame,
                dueTick = Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay,
                spot = inmate.Position,
            };
            g.story.complaints.Add(cm);
            return cm;
        }

        // ---- a word with an inmate ---------------------------------------------------------------------------------------------

        private static void CheckWardenWord(Map map)
        {
            Guarded("warden word", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                int now = s.Now;
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                Pawn p = s.Inmate(null, 40f);
                CompInmate c = p.Inmate();
                string Words(Pawn inmate) => string.Join(",", WardenSystem.WordOptions(w, inmate).Select(o => o.kind + (o.disabled != null ? "!" : "")));

                // What there is to say: a few words, always; nothing of a goal, a hearing or informing unless it is so and the warden knows of it.
                c.informantPaid = false;
                c.reputations.RemoveAll(x => x == ReputationDefOf.RP_Snitch);
                c.revealed.Remove(ReputationDefOf.RP_Snitch);
                c.admittedTick = now;
                c.sentenceDays = 60;
                if (Words(p) != "Chat,Informant") WardenFail($"the options for a new inmate with no goal and no hearing near: {Words(p)} (expected Chat,Informant)");
                c.reputations.Add(ReputationDefOf.RP_Snitch);
                if (!c.revealed.Contains(ReputationDefOf.RP_Snitch) && Words(p) != "Chat,Informant") WardenFail($"a snitch the warden has not learned of is not on offer for informing: {Words(p)}");
                c.revealed.Add(ReputationDefOf.RP_Snitch);
                if (Words(p) != "Chat") WardenFail($"a known informant can be sounded out again: {Words(p)}");
                c.reputations.Remove(ReputationDefOf.RP_Snitch);
                c.revealed.Remove(ReputationDefOf.RP_Snitch);
                // A secret goal, and one that would name a gang nobody has found, are not on offer.
                c.want = new InmateWant { def = RPWantDefOf.RP_Want_Family, formedTick = now, deadlineTick = now + 2 * GenDate.TicksPerDay, announced = true, secret = true, note = "x" };
                if (Words(p).Contains("Goal")) WardenFail("a secret goal is on offer");
                c.want.secret = false;
                if (!Words(p).Contains("Goal")) WardenFail($"a goal the warden knows of is not on offer: {Words(p)}");
                c.want.backed = true;
                if (Words(p).Contains("Goal")) WardenFail("a goal already backed is on offer again");
                c.want = null;
                if (Words(p).Contains("Hearing")) WardenFail("a hearing far off is on offer");
                c.sentenceDays = 4;
                c.admittedTick = Mathf.Max(1, now - 3 * GenDate.TicksPerDay);
                if (!Words(p).Contains("Hearing")) WardenFail($"a hearing near is not on offer ({Words(p)}; hearing in {WantWorker_Parole.HearingTick(c) - now} ticks)");
                c.sentenceDays = 60;
                c.admittedTick = now;

                // A few words: trust, a little calm, a kind thought, and the day (no second word with them today; the cooldown starts).
                c.tension = 40f;
                StaffMemories.Fade(c);
                string chat = WardenSystem.Word(w, p, WardenWord.Chat);
                if (chat == null || Mathf.Abs(c.tension - 35f) > 0.01f || StaffMemories.ScoreFor(c, w) < 5.9f || !s.Has(p, RPWardenDefOf.RP_WardenHadAWord)) WardenFail($"a few words: told \"{chat}\", tension {c.tension} (expected 35), trust {StaffMemories.ScoreFor(c, w)} (expected 6)");
                if (c.wardenWordTick != now || sc.wordReadyTick < now + (WardenSystem.WordCooldownHours - 1) * GenDate.TicksPerHour) WardenFail("a word did not start the inmate's day or the warden's cooldown");
                if (WardenSystem.WordOptions(w, p).Any(o => o.disabled == null)) WardenFail("a second word with the same inmate the same day is on offer");
                c.wardenWordTick = -999999;

                // Backing a goal (the family's: the call counts; the base: three more days; a grudge: talked down, when it takes, and the staff member vouched for a little).
                c.want = new InmateWant { def = RPWantDefOf.RP_Want_Family, formedTick = now, deadlineTick = now + 2 * GenDate.TicksPerDay, announced = true, note = "x" };
                InmateWant family = c.want;
                WardenSystem.Word(w, p, WardenWord.Goal);
                if (!family.backed || family.count != 1 || family.deadlineTick != now + 2 * GenDate.TicksPerDay) WardenFail($"backing a family goal: backed {family.backed}, contacts {family.count} (expected 1), deadline moved {family.deadlineTick - now - 2 * GenDate.TicksPerDay}");
                c.wardenWordTick = -999999;
                c.want = new InmateWant { def = RPWantDefOf.RP_Want_Parole, formedTick = now, deadlineTick = now + 2 * GenDate.TicksPerDay, announced = true, status = WantStatus.Slipping };
                InmateWant parole = c.want;
                c.admittedTick = Mathf.Max(1, now - 2 * GenDate.TicksPerDay);
                WardenSystem.Word(w, p, WardenWord.Goal);
                if (!parole.backed || parole.deadlineTick != now + (2 + Wants.BackedDays) * GenDate.TicksPerDay) WardenFail($"backing a goal: backed {parole.backed}, deadline moved {(parole.deadlineTick - now) / (float)GenDate.TicksPerDay - 2:0.0} days (expected {Wants.BackedDays})");
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_WardenBacked".Translate(w.LabelShort, p.LabelShort, Wants.Title(parole)).Resolve())) WardenFail("no chronicle line for the goal backed");
                c.wardenWordTick = -999999;
                Pawn target = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                StaffMemories.Note(c, target, -70f);
                c.want = new InmateWant { def = RPWantDefOf.RP_Want_Score, formedTick = now, deadlineTick = now + 5 * GenDate.TicksPerDay, announced = true, targetId = target.thingIDNumber, targetName = target.LabelShort, targetIsStaff = true };
                InmateWant grudge = c.want;
                // (When it takes: the dice are the check's. When it does not: CheckWardenTalkDown.)
                WardenSystem.ForcedRoll = true;
                try
                {
                    WardenSystem.Word(w, p, WardenWord.Goal);
                }
                finally
                {
                    WardenSystem.ForcedRoll = null;
                }
                if (grudge.status != WantStatus.Lost || Mathf.Abs(StaffMemories.ScoreFor(c, target) + 60f) > 0.5f) WardenFail($"a grudge talked down: status {grudge.status} (expected Lost), the target's standing {StaffMemories.ScoreFor(c, target)} (expected -60)");
                c.want = null;
                c.wardenWordTick = -999999;

                // Informing: they agree (a wage, the file, the line) or they will not, and think the less of the warden.
                WardenSystem.ForcedRoll = false;
                try
                {
                    c.tension = 30f;
                    float before = StaffMemories.ScoreFor(c, w);
                    WardenSystem.Word(w, p, WardenWord.Informant);
                    if (c.informantPaid || c.HasReputation(ReputationDefOf.RP_Snitch) || StaffMemories.ScoreFor(c, w) > before - 3.9f || c.tension < 32.9f) WardenFail($"a refusal to inform: paid {c.informantPaid}, snitch {c.HasReputation(ReputationDefOf.RP_Snitch)}, trust {StaffMemories.ScoreFor(c, w) - before} (expected -4), tension {c.tension} (expected 33)");
                    c.wardenWordTick = -999999;
                    WardenSystem.ForcedRoll = true;
                    int recruited = g.stats.informantsRecruited;
                    WardenSystem.Word(w, p, WardenWord.Informant);
                    if (!c.informantPaid || !c.KnownReputation(ReputationDefOf.RP_Snitch) || g.stats.informantsRecruited != recruited + 1) WardenFail($"an agreement to inform: paid {c.informantPaid}, known as one {c.KnownReputation(ReputationDefOf.RP_Snitch)}, recruited {g.stats.informantsRecruited - recruited}");
                    if (!r.NewEntries.Any(e => e.text == "RP_Chr_InformantRecruited".Translate(p.LabelShort).Resolve())) WardenFail("no chronicle line for the informant recruited");
                }
                finally
                {
                    WardenSystem.ForcedRoll = null;
                    c.informantPaid = false;
                    c.reputations.RemoveAll(x => x == ReputationDefOf.RP_Snitch);
                    c.revealed.Remove(ReputationDefOf.RP_Snitch);
                }
                // The odds: a gang's leader never, one who is afraid more than one who is not, one who trusts the warden more than one who does not.
                c.wardenWordTick = -999999;
                // (A made-up inmate may roll a reputation that makes them vulnerable, and so one who wants a protector: not in a comparison of the afraid with the calm.)
                c.reputations.RemoveAll(x => x.vulnerable);
                c.snitchExposed = false;
                c.fear = 0f;
                c.cash = 50;
                StaffMemories.Fade(c);
                c.staffMemories.RemoveAll(m => m.staffId == w.thingIDNumber);
                float calmOdds = WardenSystem.InformantChance(w, c);
                c.fear = 60f;
                float afraidOdds = WardenSystem.InformantChance(w, c);
                StaffMemories.Note(c, w, 40f);
                float trustedOdds = WardenSystem.InformantChance(w, c);
                c.gangLeader = true;
                float leaderOdds = WardenSystem.InformantChance(w, c);
                c.gangLeader = false;
                c.fear = 0f;
                if (afraidOdds <= calmOdds + 0.2f || trustedOdds <= afraidOdds + 0.1f || leaderOdds != 0f) WardenFail($"informant odds: calm {calmOdds:0.00}, afraid {afraidOdds:0.00}, afraid and trusting {trustedOdds:0.00}, a gang's leader {leaderOdds:0.00}");

                // Where they stand before a hearing: calm, trust, the board's odds up a little for a few days (and not in what a goal measures), the warden told the odds and what is in the way.
                c.sentenceDays = 4;
                c.admittedTick = Mathf.Max(1, now - 3 * GenDate.TicksPerDay);
                c.tension = 30f;
                c.wardenWordTick = -999999;
                string told = WardenSystem.Word(w, p, WardenWord.Hearing);
                // (The odds with and without the heads-up, on the same inmate in the same state: the difference is the bonus, and only in the board's own odds.)
                int headsUp = c.headsUpTick;
                float with = ParoleSystem.ParoleChance(c), withStanding = ParoleSystem.ParoleChance(c, recentIncidents: false);
                c.headsUpTick = -1;
                float without = ParoleSystem.ParoleChance(c), withoutStanding = ParoleSystem.ParoleChance(c, recentIncidents: false);
                c.headsUpTick = headsUp;
                if (headsUp != now || !ParoleSystem.Prepared(c) || Mathf.Abs(with - without - 0.06f) > 0.001f)
                    WardenFail($"the heads-up before a hearing: told at {headsUp}, the odds {without:0.00} -> {with:0.00} (expected +0.06)");
                if (Mathf.Abs(withStanding - withoutStanding) > 0.001f) WardenFail($"the heads-up counts in what a goal measures ({withoutStanding:0.00} -> {withStanding:0.00})");
                if (Mathf.Abs(c.tension - 24f) > 0.01f || StaffMemories.ScoreFor(c, w) < 3.9f) WardenFail($"the heads-up did not calm them or earn trust: tension {c.tension} (expected 24), trust {StaffMemories.ScoreFor(c, w)}");
                if (told == null || !told.Contains(with.ToStringPercent())) WardenFail($"the warden was not told the odds: \"{told}\"");
                c.headsUpTick = now - (ParoleSystem.PreparedDays + 1) * GenDate.TicksPerDay;
                if (ParoleSystem.Prepared(c) || Mathf.Abs(ParoleSystem.ParoleChance(c) - without) > 0.001f) WardenFail("the heads-up outlasts its days");

            });
        }

        // ---- the risk ---------------------------------------------------------------------------------------------------------------

        private static void CheckWardenRisk(Map map)
        {
            if (HostageCrisis.Running() != null)
            {
                WardenFail("risk checks could not run (a crisis is already running)");
                return;
            }
            Guarded("warden risk", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                int now = s.Now;
                // Nobody real is in reach of the made-up ringleader: the real staff (the real warden, who may be standing in the canteen at the hour, among them) are set aside for the check, as the
                // hostage checks do.
                foreach (Pawn real in StaffCommute.AllStaff(map).ToList())
                    if (real.GetComp<CompStaff>() is { } rsc)
                    {
                        int away = rsc.leaveAtTick;
                        rsc.leaveAtTick = int.MaxValue;
                        r.Add(() => rsc.leaveAtTick = away);
                    }
                List<IntVec3> canteen = s.Cells(RPDefOf.RP_Canteen, map.mapPawns.FreeColonistsSpawned.First());
                Room room = canteen.Count > 0 ? canteen[0].GetRoom(map) : null;
                List<IntVec3> spots = room == null ? new List<IntVec3>() : canteen.Where(c => c.GetRoom(map) == room && c.DistanceTo(canteen[0]) <= 5f).ToList();
                if (spots.Count < 6 || HostageCrisis.HoldRoom(spots[0], map) == null)
                {
                    WardenFail($"risk checks could not run (room to stand in a closed canteen: {spots.Count} cells)");
                    return;
                }
                // A blow to the warden (eight damage, through armour: the uniform's armour turned even a blow of eight to nothing once in a census run, and a blow that does no damage is none): a line
                // and a letter (once for a fight), and those who trust them take it hard.
                Pawn w = s.Warden(spots[0]);
                Pawn attacker = s.Inmate(spots[1], 30f), friend = s.Inmate(spots[2], 20f);
                StaffMemories.Note(friend.Inmate(), w, 40f);
                int struck = g.stats.wardenStruck;
                var blow = new DamageInfo(DamageDefOf.Blunt, 8f, 1f, -1f, attacker);
                w.TakeDamage(blow);
                int letters = r.NewLetters.Count(l => LetterIs(l, "RP_WardenStruckLabel".Translate(w.LabelShort)));
                w.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 8f, 1f, -1f, attacker));
                if (letters != 1 || r.NewLetters.Count(l => LetterIs(l, "RP_WardenStruckLabel".Translate(w.LabelShort))) != 1 || g.stats.wardenStruck != struck + 1)
                    WardenFail($"a blow to the warden: {letters} letters after the first and {r.NewLetters.Count(l => LetterIs(l, "RP_WardenStruckLabel".Translate(w.LabelShort)))} after the second (expected 1 and 1), count {g.stats.wardenStruck - struck}");
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_WardenStruck".Translate(w.LabelShort, attacker.LabelShort).Resolve())) WardenFail("no chronicle line for the blow to the warden");
                if (friend.Inmate().tension < 26f - 0.01f || attacker.Inmate().tension > 31f) WardenFail($"a blow to the warden: a trusting inmate's tension {friend.Inmate().tension} (expected 26), the attacker's {attacker.Inmate().tension}");
                // (A blow to a guard is no such letter.)
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Place(guard, spots[3]);
                // (The warden's letter, not any letter: a blow that puts a guard down has letters of its own.)
                int WardenLetters() => r.NewLetters.Count(l => LetterIs(l, "RP_WardenStruckLabel".Translate(w.LabelShort)) || LetterIs(l, "RP_WardenStruckLabel".Translate(guard.LabelShort)));
                int before = WardenLetters(), allBefore = r.NewLetters.Count();
                guard.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 8f, 0f, -1f, attacker));
                if (WardenLetters() != before) WardenFail($"a blow to a guard gave the warden's letter ({string.Join(" | ", r.NewLetters.Skip(allBefore).Select(l => l.Label.ToString()))})");
                foreach (Pawn p in new[] { w, guard }) p.health.RemoveAllHediffs();

                // The prize: a ringleader with the warden and a guard in reach, the guard the nearer, takes the warden; with no warden in reach, the guard. A seizure of the warden is twice as likely.
                Pawn leader = null;
                for (int attempt = 0; attempt < 8 && leader == null; attempt++)
                {
                    Pawn p = s.Inmate(spots[4], 80f);
                    if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    leader = p;
                }
                if (leader == null)
                {
                    WardenFail("risk checks could not run (no inmate who can fight)");
                    return;
                }
                CompInmate lc = leader.Inmate();
                lc.crime = DefDatabase<CrimeDef>.GetNamed("RP_Crime_Racketeering");
                lc.sentenceDays = 30;
                s.Place(guard, spots[5]);
                s.Place(w, spots[0]);
                Pawn taken = HostageCrisis.Candidate(leader, lc, guard);
                if (taken != w) WardenFail($"a ringleader with the warden and a nearer guard in reach took {taken?.LabelShort ?? "nobody"}, not the warden");
                s.Place(w, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
                taken = HostageCrisis.Candidate(leader, lc, guard);
                if (taken != guard) WardenFail($"a ringleader with only a guard in reach took {taken?.LabelShort ?? "nobody"}");
                if (Mathf.Abs(HostageCrisis.SeizureChance(w, HostageCrisis.RiotChance) - 2f * HostageCrisis.RiotChance) > 0.001f || HostageCrisis.SeizureChance(guard, HostageCrisis.RiotChance) != HostageCrisis.RiotChance)
                    WardenFail("a seizure of the warden is not twice as likely as one of a guard");

                // The crisis over the warden: it runs the hostage arc, the stand-off is shorter, the talks have less to go on, the end is likelier a death, the letter says whose it is.
                s.Place(w, spots[0]);
                s.Place(guard, spots[5]);
                StoryArcDef arcDef = RPHostageDefOf.RP_Arc_Hostage;
                bool hadStart = g.story.lastArcStart.TryGetValue(arcDef, out int start0);
                (int drama0, int beats0) = (g.story.dramaToday, g.story.beatsToday);
                bool? forced = HostageCrisis.ForcedRoll;
                HostageCrisis.ForcedRoll = true;
                StoryArc arc = null;
                var worker = (HostageArc)arcDef.Worker;
                try
                {
                    arc = HostageCrisis.TryStart(leader, lc, new[] { leader }, HostageCrisis.Riot, guard);
                    if (arc == null || arc.def != arcDef || arc.A != w)
                    {
                        WardenFail($"a riot with the warden in reach did not run the hostage arc over the warden (arc {arc?.def.defName}, held {arc?.A?.LabelShort})");
                        return;
                    }
                    if (!HostageCrisis.IsHeld(w)) WardenFail("the seized warden is not held");
                    if (Math.Abs(arc.nextTick - now - 4 * GenDate.TicksPerHour) > 5) WardenFail($"the stand-off over the warden runs {(arc.nextTick - now) / (float)GenDate.TicksPerHour:0.0} h (expected 4, a guard's is 6)");
                    if (!Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.arcId == arc.id && !l.answered && LetterIs(l, "RP_HostageWardenLabel".Translate(w.LabelShort))))
                        WardenFail("the warden's own letter for the crisis over the warden is not on the stack");
                    if (WardenSystem.Unready(w, -1) != "RP_WardenWhy_Held".Translate().ToString()) WardenFail("a held warden can start an ability");
                    (float talkWarden, float killWarden) = (HostageArc.TalkChance(arc, map, out _), HostageCrisis.KillChance(arc));
                    HostageFate stormWarden = HostageCrisis.StormFate(arc, 0.12f);
                    arc.pawns[0] = guard;
                    (float talkGuard, float killGuard) = (HostageArc.TalkChance(arc, map, out _), HostageCrisis.KillChance(arc));
                    HostageFate stormGuard = HostageCrisis.StormFate(arc, 0.12f);
                    arc.pawns[0] = w;
                    if (Mathf.Abs(talkGuard - talkWarden - 0.1f) > 0.001f) WardenFail($"the talks over the warden: {talkWarden:0.00} against {talkGuard:0.00} for a guard (expected 0.1 less)");
                    if (Mathf.Abs(killWarden - killGuard - 0.1f) > 0.001f) WardenFail($"the last deadline over the warden kills at {killWarden:0.00} against {killGuard:0.00} for a guard (expected 0.1 more)");
                    if (stormWarden != HostageFate.Dead || stormGuard == HostageFate.Dead) WardenFail($"a storm at a roll of 0.12 over the warden is {stormWarden}, over a guard {stormGuard} (expected Dead and not Dead)");
                    // The first deadline comes sooner too (and hurts them), and the end costs the prison's name twice as much as it does for a guard.
                    int stageBefore = arc.stage;
                    worker.DeadlineReached(arc, map);
                    if (arc.stage != HostageCrisis.Deadline || Math.Abs(arc.nextTick - s.Now - 3 * GenDate.TicksPerHour) > 5)
                        WardenFail($"the deadline over the warden: stage {stageBefore} -> {arc.stage}, the last one in {(arc.nextTick - s.Now) / (float)GenDate.TicksPerHour:0.0} h (expected 3, a guard's is 4)");
                    g.reputation = 50f;
                    worker.Finish(arc, map, HostageHow.Killed, HostageFate.Dead, leader: leader, charge: false);
                    float lost = 50f - g.reputation;
                    if (Mathf.Abs(lost - 10f) > 0.01f) WardenFail($"the end of a crisis that killed the warden cost the prison's name {lost} (expected 10: twice a guard's 5)");
                }
                finally
                {
                    HostageCrisis.ForcedRoll = forced;
                    if (arc != null)
                    {
                        arc.done = true;
                        HostageCrisis.Release(w);
                        HostageCrisis.Release(guard);
                        foreach (Letter extra in r.NewLetters.ToList())
                        {
                            if (extra is ChoiceLetter_PrisonDecision decision) decision.answered = true;
                            Find.LetterStack.RemoveLetter(extra);
                        }
                    }
                    leader.MentalState?.RecoverFromState();
                    (g.story.dramaToday, g.story.beatsToday) = (drama0, beats0);
                    if (hadStart) g.story.lastArcStart[arcDef] = start0;
                    else g.story.lastArcStart.Remove(arcDef);
                    g.story.arcs.RemoveAll(a => a.def == arcDef && a == arc);
                    s.pm.ForceDoorUpdate();
                    PrisonAlerts.Invalidate();
                }
            });
        }

        // ---- a successor ------------------------------------------------------------------------------------------------------------

        private static void CheckWardenVacancy(Map map)
        {
            Guarded("warden vacancy", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                Pawn real = WardenSystem.InPost(map);
                if (real == null)
                {
                    WardenFail("vacancy checks could not run (no warden in post)");
                    return;
                }
                CompStaff rsc = real.GetComp<CompStaff>();
                StaffRoleDef role = rsc.role;
                // The real warden steps out of the post for the length of the check (the role is theirs again after).
                rsc.role = null;
                StaffCommute.StaffVersion++;
                r.Add(() =>
                {
                    rsc.role = role;
                    StaffCommute.StaffVersion++;
                });
                if (WardenSystem.InPost(map) != null)
                {
                    WardenFail("vacancy checks could not run (another warden is in post)");
                    return;
                }
                // A prison with no warden (a save from before there was one, or a death): the first midnight starts the wait, no one comes before the days are out.
                g.wardenVacantTick = -1;
                WardenSystem.Daily(g, map, s.Now);
                if (g.wardenVacantTick != s.Now || WardenSystem.InPost(map) != null) WardenFail($"the first midnight with no warden: the wait is at {g.wardenVacantTick} (expected now), a warden in post {WardenSystem.InPost(map) != null}");
                WardenSystem.Daily(g, map, s.Now);
                if (WardenSystem.InPost(map) != null) WardenFail("a warden came before the days were out");
                // (A game is hours old: the days run on the clock the pass is given.)
                int start = g.wardenVacantTick;
                WardenSystem.Daily(g, map, start + WardenSystem.SuccessionDays * GenDate.TicksPerDay - 500);
                if (WardenSystem.InPost(map) != null) WardenFail("a warden came a few hours early");
                // The days are out: the state names one, who is on staff, out of the payroll, with a letter and a line; and the wait is over.
                WardenSystem.Daily(g, map, start + WardenSystem.SuccessionDays * GenDate.TicksPerDay + 10);
                Pawn successor = WardenSystem.InPost(map);
                if (successor == null)
                {
                    WardenFail($"the state named no warden after the days were out (the wait is at {g.wardenVacantTick}, now {s.Now}; roles of warden among the colonists: {map.mapPawns.FreeColonists.Count(WardenSystem.IsWarden)}, in the staff list {StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden)})");
                    return;
                }
                r.Add(() =>
                {
                    if (successor.ownership?.OwnedBed is { } cot && !cot.Destroyed && cot.OwnersForReading.Count <= 1) cot.Destroy();
                    if (!successor.Destroyed) successor.Destroy(DestroyMode.Vanish);
                });
                if (successor == real || g.wardenVacantTick != -1 || StaffCareers.Wage(successor.GetComp<CompStaff>()) != 0 || !successor.Spawned) WardenFail($"the successor: the same pawn {successor == real}, the wait at {g.wardenVacantTick} (expected -1), wage {StaffCareers.Wage(successor.GetComp<CompStaff>())}, spawned {successor.Spawned}");
                if (!r.NewLetters.Any(l => LetterIs(l, "RP_WardenNamedLabel".Translate())) || !r.NewEntries.Any(e => e.text == "RP_Chr_WardenAppointed".Translate(successor.LabelShort).Resolve())) WardenFail("no letter, or no chronicle line, for the new warden");
                int wardens = StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden);
                WardenSystem.Daily(g, map, s.Now);
                if (StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden) != wardens) WardenFail("a second successor was named with a warden in post");

                // A warden who dies: no family letter to answer (no compensation), the post is empty and the wait starts; the same successor path.
                Pawn doomed = successor;
                successor.GetComp<CompStaff>().role = null;
                StaffCommute.StaffVersion++;
                Pawn mortal = s.Warden();
                int decisions = Find.LetterStack.LettersListForReading.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_StaffDeath"));
                g.wardenVacantTick = -1;
                // (Of neglect, bled out: a death that is the prison's fault, whose family would write a bitter letter. The warden has none.)
                Hediff bleeding = HediffMaker.MakeHediff(HediffDefOf.BloodLoss, mortal);
                mortal.health.AddHediff(bleeding);
                r.Add(() => g.story.bitterDeaths.Remove(mortal.thingIDNumber));
                mortal.Kill(null, bleeding);
                if (mortal.Corpse != null) r.Add(() => mortal.Corpse?.Destroy());
                if (!mortal.Dead || g.wardenVacantTick < 0) WardenFail($"the warden died: dead {mortal.Dead}, the post's wait at {g.wardenVacantTick} (expected started)");
                if (CondolenceSystem.AtFault(mortal)) WardenFail("the warden's death of neglect was noted as the prison's fault, for a family's bitter letter (the warden has none)");
                if (!r.NewLetters.Any(l => LetterIs(l, "RP_WardenDiedLabel".Translate(mortal.LabelShort)))) WardenFail("no letter for the warden's death");
                if (Find.LetterStack.LettersListForReading.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_StaffDeath")) != decisions) WardenFail("the warden's death asked for compensation for a family");
                doomed.GetComp<CompStaff>().role = role;
                StaffCommute.StaffVersion++;
            });
        }

        // ---- through real ticks -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The jobs of the warden's abilities, run through real ticks (a made-up warden and inmates: what each does in the end, and that the walking, the waiting and the talking get there). Called at the
        /// end of the events plan, by <c>Evaluate</c>: the ticks they use are hours of the plan's own clock, and the checks that run after them in the day would find a different hour.
        /// </summary>
        private static void CheckWardenThroughTicks(Map map)
        {
            int start = Find.TickManager.TicksGame;
            CheckWardenAddressTicks(map);
            CheckWardenWalkTicks(map);
            CheckWardenWalkBrokenOffTicks(map);
            CheckWardenWordTicks(map);
            CheckWardenRetireTicks(map);
            CheckWardenRetireQueueTicks(map);
            RPLog.Message($"[RimPrison:test] warden checks through ticks used {Find.TickManager.TicksGame - start} ticks");
        }

        /// <summary>The job walks to the canteen, speaks, and the room has heard it.</summary>
        private static void CheckWardenAddressTicks(Map map)
        {
            Guarded("warden address through ticks", map, r =>
            {
                // (Both addresses within one hour: the hour pass rolls misconduct for the made-up inmates (tension 50, side by side), and a fight puts one of them in a mental state, where the room
                // does not hear the warden: "inmates heard: 1", seen in 1 of the night's events runs.)
                ClearOfTheHourChange(map, 1800);
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                List<IntVec3> canteen = s.Cells(RPDefOf.RP_Canteen, w);
                if (canteen.Count < 4)
                {
                    WardenFail($"could not run (room to stand in the canteen: {canteen.Count} cells)");
                    return;
                }
                Pawn a = s.Inmate(canteen[0], 50f), b = s.Inmate(canteen[1], 50f);
                foreach (Pawn p in new[] { a, b }) p.Inmate().suppressed = 40f;
                // (A few cells from where they will stand: the walk is ticks of the plan's day.)
                IntVec3 spot = WardenSystem.CanteenSpot(w);
                s.Place(w, canteen.Where(c => c != canteen[0] && c != canteen[1]).OrderBy(c => Mathf.Abs(c.DistanceTo(spot) - 7f)).First());
                int stats0 = s.g.stats.wardenAddresses;
                if (!WardenOrders.Address(w, firm: false)) WardenFail("the address order was refused");
                int ticks = s.Run(() => sc.addressReadyTick > 0, 3500, a, b);
                CompInmate ac = a.Inmate();
                if (sc.addressReadyTick <= 0 || !s.Has(a, RPWardenDefOf.RP_WardenCalmed) || ac.tension > 46f || s.g.stats.wardenAddresses != stats0 + 1)
                    WardenFail($"through ticks the warden did not address the canteen in {ticks} ticks (job {w.CurJobDef?.defName ?? "none"} at {w.Position}, cooldown {sc.addressReadyTick}, calm thought {s.Has(a, RPWardenDefOf.RP_WardenCalmed)}, tension {ac.tension})");
                if (WardenSystem.DoingAbility(w)) WardenFail("the warden is still at it after the address");

                // The firm one through ticks as well (a job that lost its order's count would do the calm one, and the check above would not see it): suppression up, the creep on, a sore thought, tension not down.
                foreach (Pawn p in new[] { a, b }) (p.Inmate().tension, p.Inmate().suppressed) = (50f, 40f);
                sc.addressReadyTick = -1;
                s.Place(w, canteen.Where(c => c != canteen[0] && c != canteen[1]).OrderBy(c => Mathf.Abs(c.DistanceTo(spot) - 7f)).First());
                if (!WardenOrders.Address(w, firm: true)) WardenFail("the firm address order was refused");
                int records0 = ac.records.Count;
                ticks = s.Run(() => sc.addressReadyTick > 0, 3500, a, b);
                // (A made-up inmate can start a real fight in the canteen while the warden speaks (a census run: a feud with a real inmate, a fight, twelve hours of lockup): the misconduct and its
                // sanction move tension and suppression on their own, so those two are only asked of an inmate with no new misconduct; the sore thought, the creep and the cooldown always.)
                bool fought = ac.records.Count != records0;
                // (Tension "not down" against the calm address's cut of four or more: it drifts by itself over the walk's ticks, 0.35 in 521 ticks in a census run, as suppression does by under a point.)
                if (sc.addressReadyTick <= 0 || !s.Has(a, RPWardenDefOf.RP_WardenFirm) || !fought && (ac.suppressed < 59f || ac.tension < 48f) || ac.wardenFirmUntil < 0)
                    WardenFail($"through ticks the warden did not lay down the law in {ticks} ticks (job {w.CurJobDef?.defName ?? "none"}, cooldown {sc.addressReadyTick}, sore thought {s.Has(a, RPWardenDefOf.RP_WardenFirm)}, suppression {ac.suppressed} (expected 60), tension {ac.tension} (expected 50), creep until {ac.wardenFirmUntil}, new misconduct {fought}; the inmate at {a.Position}, in the canteen {WardenSystem.CanteenOf(w)?.Contains(a.Position)}, job {a.CurJobDef?.defName ?? "none"}, mental state {a.MentalStateDef?.defName ?? "none"}, downed {a.Downed}, active {InmateUtility.IsActiveInmate(a)}; heard now: {string.Join(", ", WardenSystem.InCanteen(map, WardenSystem.CanteenOf(w)).Select(x => x.LabelShort))})");
            });
        }

        /// <summary>The job walks the stops, meets the inmates, and at the end the walk is done (its cooldown, its counts, the complaint of an inmate met on the way).</summary>
        private static void CheckWardenWalkTicks(Map map)
        {
            Guarded("warden walk through ticks", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                PrisonZoneDef wing = WardenSystem.Wings(map).OrderByDescending(z => s.pm.zones.CellsOf(z).Count).FirstOrDefault();
                List<IntVec3> cells = wing == null ? new List<IntVec3>() : s.Cells(wing, w);
                if (cells.Count < 4)
                {
                    WardenFail($"could not run (room to stand in the wing: {cells.Count} cells)");
                    return;
                }
                s.Place(w, cells[cells.Count / 2]);
                List<IntVec3> stops = WardenSystem.WalkPlan(w, wing);
                List<IntVec3> near = stops.Count == 0 ? new List<IntVec3>() : GenRadial.RadialCellsAround(stops[0], 3f, true).Where(c => c.InBounds(map) && c.Standable(map) && s.pm.zones.ZoneAt(c) == wing && c.GetFirstPawn(map) == null).ToList();
                if (near.Count < 3)
                {
                    WardenFail($"could not run (room to stand near the first stop: {near.Count} cells)");
                    return;
                }
                Pawn calm = s.Inmate(near[0], 20f), restless = s.Inmate(near[1], 90f), middling = s.Inmate(near[2], 55f);
                Pawn officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                // (The real prison's own complaints on the desk would fill the two places there are for letters: the check has the desk to itself.)
                List<Complaint> complaints0 = g.story.complaints;
                g.story.complaints = new List<Complaint>();
                r.Add(() => g.story.complaints = complaints0);
                Complaint cm = MakeFiledComplaint(g, calm, officer);
                r.Add(() => g.story.complaints.Remove(cm));
                // (The walk itself, the first two stops of the plan: the whole of it is hours of the plan's day.)
                Job shortWalk = JobMaker.MakeJob(RPWardenDefOf.RP_WardenWalk);
                shortWalk.targetQueueA = stops.Take(2).Select(c => new LocalTargetInfo(c)).ToList();
                shortWalk.count = wing.gridIndex;
                s.Place(w, cells[cells.Count / 2]);
                int walks0 = g.stats.wardenWalks;
                w.jobs.StartJob(shortWalk, JobCondition.InterruptForced);
                // (Who ended the walk, when it was not its own end: the mod frames of the stack, for the failure text.)
                string walkEnded = null;
                w.jobs.curDriver?.AddFinishAction(c =>
                {
                    if (c == JobCondition.Succeeded) return;
                    // (The whole stack but the system's, a lambda by its outer type: the mod's named methods alone showed only the scene's loop.)
                    walkEnded = $"{c} at tick {Find.TickManager.TicksGame} by [" + string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Select(f => f.GetMethod()).Where(m => m != null)
                        .Select(m => (m.DeclaringType is { } t && t.Name.StartsWith("<") ? t.DeclaringType : m.DeclaringType, m.Name))
                        .Where(x => x.Item1 == null || !(x.Item1.Namespace ?? "").StartsWith("System"))
                        .Select(x => (x.Item1?.Name ?? "?") + "." + x.Name).Skip(1).Take(10)) + "]";
                });
                // (The three held at the tensions they were made with, every tick: the walk's meeting reads the tension at the stop, and a made-up inmate at 90 does not keep it - a misconduct's relief or the hour's pass took the
                // restless one down to 53 in a census run, under the 65 the walk resents, with the calm one met and the restless one a cell from the first stop. What the check is about is the walk, not the inmates' tension;
                // what moved is in the log, with the tick, so that a cause is not guessed.)
                var held = new[] { (calm, 20f), (restless, 90f), (middling, 55f) };
                var moved = new List<string>();
                int start = Find.TickManager.TicksGame;
                int ticks = s.Run(() =>
                {
                    foreach ((Pawn p, float tension) in held)
                        if (p.Inmate() is { } ic && Mathf.Abs(ic.tension - tension) > 0.01f)
                        {
                            moved.Add($"{p.LabelShort} {tension:F0} -> {ic.tension:F0} at tick {Find.TickManager.TicksGame - start} (hour {GenLocalDate.HourOfDay(map)}, job {p.CurJobDef?.defName ?? "none"}, records {ic.records.Count}, warden at {w.Position}, job {w.CurJobDef?.defName ?? "none"})");
                            ic.tension = tension;
                        }
                    return g.stats.wardenWalks != walks0;
                }, 2500, calm, restless, middling);
                if (moved.Count > 0) RPLog.Message($"[RimPrison:test] warden walk: tension moved under the check and was held - {string.Join("; ", moved.Take(6))}");
                if (sc.walkReadyTick <= 0 || g.stats.wardenWalks != walks0 + 1) WardenFail($"through ticks the walk did not finish in {ticks} ticks (job {w.CurJobDef?.defName ?? "none"} at {w.Position}, stops {stops.Count}; the walk ended {walkEnded ?? "not before"}, the warden's timetable now {w.timetable?.CurrentAssignment?.defName}, rest {w.needs?.rest?.CurLevel:F2})");
                else
                {
                    if (StaffMemories.ScoreFor(calm.Inmate(), w) < 2.9f || StaffMemories.ScoreFor(restless.Inmate(), w) > -3.9f)
                        WardenFail($"through ticks the walk met nobody: calm {StaffMemories.ScoreFor(calm.Inmate(), w)}, restless {StaffMemories.ScoreFor(restless.Inmate(), w)}"
                                   // (Where each of the two was, against the walk's two stops, and what they were doing: a restless one who wandered off or broke is not one the walk passed.)
                                   + $"; tension moved {(moved.Count == 0 ? "never" : string.Join("; ", moved.Take(6)))}"
                                   + $"; the stops {string.Join(" ", stops.Take(2))}; {string.Join("; ", new[] { calm, restless }.Select(x => $"{x.LabelShort} at {x.Position} ({string.Join("/", stops.Take(2).Select(st => x.Position.DistanceTo(st).ToString("F1")))} cells), job {x.CurJobDef?.defName ?? "none"}, mental {x.MentalStateDef?.defName ?? "none"}, downed {x.Downed}, tension {x.Inmate()?.tension:F0}"))}");
                    if (cm.stage != ComplaintStage.Waiting) WardenFail($"through ticks a complaint of an inmate met on the walk was not raised (stage {cm.stage})");
                }
            });
        }

        /// <summary>The warden goes to the inmate and has the word.</summary>
        private static void CheckWardenWordTicks(Map map)
        {
            Guarded("warden word through ticks", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                Pawn p = s.Inmate(null, 40f);
                CompInmate c = p.Inmate();
                IntVec3 spot = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                s.Place(p, spot);
                IntVec3? startCell = GenRadial.RadialCellsAround(spot, 8f, true).Where(x => x.InBounds(map) && x.Standable(map) && x.GetRoom(map) == spot.GetRoom(map) && x.DistanceTo(spot) >= 5f && x.GetFirstPawn(map) == null).Select(x => (IntVec3?)x).FirstOrDefault();
                if (startCell == null)
                {
                    WardenFail("could not run (no free cell five cells or more from the inmate, in their room)");
                    return;
                }
                IntVec3 start = startCell.Value;
                s.Place(w, start);
                if (!WardenOrders.Word(w, p, WardenWord.Chat)) WardenFail("the word order was refused");
                int ticks = s.Run(() => c.wardenWordTick > -999999, 3000, p);
                if (c.wardenWordTick == -999999 || sc.wordReadyTick <= 0 || !s.Has(p, RPWardenDefOf.RP_WardenHadAWord)) WardenFail($"through ticks the warden did not have the word in {ticks} ticks (job {w.CurJobDef?.defName ?? "none"} at {w.Position}, the inmate at {p.Position}, word {c.wardenWordTick}, tension {c.tension})");

                // The other kinds as well (a job whose order lost its kind would have a few words every time): a goal backed, and where they stand before a hearing.
                int now = s.Now;
                c.wardenWordTick = -999999;
                sc.wordReadyTick = -1;
                c.want = new InmateWant { def = RPWantDefOf.RP_Want_Family, formedTick = now, deadlineTick = now + 2 * GenDate.TicksPerDay, announced = true, note = "x" };
                InmateWant family = c.want;
                s.Place(w, start);
                if (!WardenOrders.Word(w, p, WardenWord.Goal)) WardenFail("the goal word order was refused");
                ticks = s.Run(() => c.wardenWordTick > -999999, 3000, p);
                if (!family.backed || family.count != 1) WardenFail($"through ticks the warden did not back the goal in {ticks} ticks (backed {family.backed}, calls {family.count}, job {w.CurJobDef?.defName ?? "none"})");
                c.want = null;
                c.wardenWordTick = -999999;
                sc.wordReadyTick = -1;
                c.sentenceDays = 4;
                c.admittedTick = Mathf.Max(1, now - 3 * GenDate.TicksPerDay);
                c.headsUpTick = -1;
                s.Place(w, start);
                if (!WardenOrders.Word(w, p, WardenWord.Hearing)) WardenFail("the hearing word order was refused");
                ticks = s.Run(() => c.wardenWordTick > -999999, 3000, p);
                if (c.headsUpTick < 0) WardenFail($"through ticks the warden did not tell them where they stand in {ticks} ticks (heads-up {c.headsUpTick}, job {w.CurJobDef?.defName ?? "none"})");
            });
        }

        /// <summary>The walk to bed the hour before sleep: they get there, wait there, and the job is over when the timetable says sleep.</summary>
        private static void CheckWardenRetireTicks(Map map)
        {
            Guarded("warden retires through ticks", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                Building_Bed cot = WardenSystem.EnsureCot(w);
                if (cot == null)
                {
                    WardenFail("no cot was found or built for a warden with no bed (a staff-only room is painted)");
                    return;
                }
                r.Add(() =>
                {
                    if (cot.Spawned) cot.Destroy();
                });
                // (No sleep in the timetable until the check says so: the hour changes while they walk, and the wait is over the moment the timetable says sleep.)
                for (int h = 0; h < 24; h++) w.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                // (Ten cells or so off, indoors: more than the four from which they are already in bed, and a walk that takes a few hundred ticks.)
                IntVec3? far = FarFrom(map, w, cot.Position);
                if (far == null)
                {
                    WardenFail("could not run the walk to bed (no reachable cell far from the cot)");
                    return;
                }
                s.Place(w, far.Value);
                bool went = WardenSystem.Retire(w);
                if (!went || w.CurJobDef != RPWardenDefOf.RP_WardenRetire) WardenFail($"the warden was not sent to bed the hour before sleep (job {w.CurJobDef?.defName ?? "none"})");
                int ticks = s.Run(() => w.Position == cot.Position, 2000);
                // (At the bed they wait there, not wander off, until the timetable says sleep; then the job is over.)
                s.Run(() => false, 60);
                if (w.CurJobDef != RPWardenDefOf.RP_WardenRetire) WardenFail($"the warden did not stay at the bed until the hour of sleep (job {w.CurJobDef?.defName ?? "none"} at {w.Position}, {ticks} ticks to get there)");
                w.timetable.SetAssignment(GenLocalDate.HourOfDay(w), TimeAssignmentDefOf.Sleep);
                s.Run(() => w.CurJobDef != RPWardenDefOf.RP_WardenRetire, 60);
                if (w.CurJobDef == RPWardenDefOf.RP_WardenRetire) WardenFail("the warden is still waiting at the bed when the timetable says sleep");
                if (WardenSystem.Retire(w)) WardenFail("the warden was sent to a bed they are at already");
            });
        }

        /// <summary>
        /// The walk to bed is not followed by a stroll that was in the queue (vanilla puts a stroll back at the head of the queue when a haul is slipped in before it, and the queue outranks the rest
        /// giver: the warden stood up in bed at the stroke of sleep and lay down at the far end of the prison an hour later, which a normal run counted as staff asleep on the floor); and a warden who
        /// could not be sent at the hour's start is sent by the next pass once they can be, not left to wander.
        /// </summary>
        private static void CheckWardenRetireQueueTicks(Map map)
        {
            Guarded("warden retires with a stroll queued, and after a word", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                Building_Bed cot = WardenSystem.EnsureCot(w);
                if (cot == null)
                {
                    WardenFail("no cot was found or built for a warden with no bed (a staff-only room is painted)");
                    return;
                }
                IntVec3? far = FarFrom(map, w, cot.Position);
                if (far == null || w.needs?.rest == null)
                {
                    WardenFail($"could not run the queue walk to bed (far cell {far}, rest need {w.needs?.rest != null})");
                    return;
                }
                int hour = GenLocalDate.HourOfDay(w);
                // (Work all day but for the sleep that starts with the next hour: this is the hour before sleep. Tired, or they would not lie down once the timetable says sleep.)
                for (int h = 0; h < 24; h++) w.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                w.timetable.SetAssignment((hour + 1) % 24, TimeAssignmentDefOf.Sleep);
                w.needs.rest.CurLevel = 0.3f;
                s.Place(w, far.Value);
                w.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(JobDefOf.GotoWander, far.Value));
                if (!WardenSystem.Retire(w) || w.CurJobDef != RPWardenDefOf.RP_WardenRetire)
                {
                    WardenFail($"the warden with a stroll queued was not sent to bed (job {w.CurJobDef?.defName ?? "none"})");
                    return;
                }
                s.Run(() => w.Position == cot.Position, 2000);
                // (The hour turns: the timetable says sleep now, and the walk is over. The hour is asked again, and the timetable written whole: the staff's hourly pass (`Shifts.Balance`) writes everyone's
                // day over a made-up warden's, as it does the real one's, when an hour has turned during the walk; and it is held so while the check waits, as the hour can turn in the wait too, and the
                // pass's day shift then said work at the bed, so the warden strolled there instead of lying down: a census failure.)
                void sleepNow()
                {
                    int now = GenLocalDate.HourOfDay(w);
                    if (w.timetable.GetAssignment(now) == TimeAssignmentDefOf.Sleep) return;
                    for (int h = 0; h < 24; h++) w.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                    w.timetable.SetAssignment(now, TimeAssignmentDefOf.Sleep);
                }
                sleepNow();
                s.Run(() =>
                {
                    sleepNow();
                    return w.CurJobDef != RPWardenDefOf.RP_WardenRetire;
                }, 300);
                s.Run(() =>
                {
                    sleepNow();
                    return false;
                }, 60);
                if (w.CurJobDef != JobDefOf.LayDown || w.Position != cot.Position)
                    WardenFail($"the warden who was sent to bed with a stroll queued did not lie down in it (job {w.CurJobDef?.defName ?? "none"} at {w.Position}, the bed at {cot.Position}, {w.jobs.jobQueue.Count} queued)");

                // A word with an inmate at the hour's start: not sent (they cannot be: the word is not cut short), and sent by the warden's own pass once the word is over.
                w.jobs.StopAll();
                w.jobs.ClearQueuedJobs();
                // (The hour may turn while this runs: it is waited out if little of it is left, and the timetable is written again for the hour it is then.)
                hour = GenLocalDate.HourOfDay(w);
                if (GenLocalDate.DayTick(w) % GenDate.TicksPerHour > GenDate.TicksPerHour - 900) s.Run(() => GenLocalDate.HourOfDay(w) != hour, 1000);
                hour = GenLocalDate.HourOfDay(w);
                for (int h = 0; h < 24; h++) w.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                w.timetable.SetAssignment((hour + 1) % 24, TimeAssignmentDefOf.Sleep);
                s.Place(w, far.Value);
                Pawn inmate = s.Inmate(cot.Position);
                s.Pin(inmate);
                if (!WardenOrders.Word(w, inmate, WardenWord.Chat) || w.CurJobDef != RPWardenDefOf.RP_WardenWord)
                {
                    WardenFail($"the warden was not sent to have a word with an inmate (job {w.CurJobDef?.defName ?? "none"})");
                    return;
                }
                if (WardenSystem.Retire(w)) WardenFail("a warden in the middle of a word was sent to bed");
                w.jobs.StopAll();
                // (Sent, or gone to the cot of their own accord: tired from the walks above, vanilla's rest can take them there first, which is where the warden's pass would send them.)
                bool ToBed() => w.CurJobDef == RPWardenDefOf.RP_WardenRetire || w.CurJobDef == JobDefOf.LayDown && w.CurJob.targetA.Thing == cot;
                int ticks = s.Run(ToBed, 600, inmate);
                if (!ToBed()) WardenFail($"the warden who could not be sent to bed at the hour's start was not sent in {ticks} ticks once they could be (job {w.CurJobDef?.defName ?? "none"} on {w.CurJob?.targetA.Thing?.def.defName ?? w.CurJob?.targetA.Cell.ToString()} at {w.Position}, the cot at {cot.Position}, rest {w.needs.rest.CurLevel:F2}, hour {GenLocalDate.HourOfDay(w)} of {hour}, retire hour {WardenSystem.RetireHour(w, GenLocalDate.HourOfDay(w))}, faction {w.Faction?.Name})");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------

        /// <summary>The seeded warden state of the saveload plan, to be read back after the reload.</summary>
        private static int wardenSeedStaffId = -1;
        private static StaffRoleDef wardenSeedRole;
        private static (int address, int walk, int word) wardenSeedReady;
        private static (int word, int headsUp, int firm) wardenSeedInmate;

        /// <summary>
        /// Saveload plan: cooldowns on the warden, a prison with no warden (the post taken from them, and the vacancy long enough that the first midnight names a successor: the path of an old save, now
        /// through a real save and load and the real settlement), and an inmate with a word, a heads-up, a firm address (the snapshot reads them all through <see cref="WardenSystem.Describe"/>). The
        /// goal backed is seeded with the goal of the plan's own (see <c>backed</c> there).
        /// </summary>
        private static void SeedWarden(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            int now = Find.TickManager.TicksGame;
            if (WardenSystem.InPost(map) is not { } real)
            {
                WardenFail("saveload: no warden in post to seed (the cooldowns and the vacancy were not seeded)");
                return;
            }
            CompStaff sc = real.GetComp<CompStaff>();
            (sc.addressReadyTick, sc.walkReadyTick, sc.wordReadyTick) = (now + 7000, now + 3000, now + 1500);
            wardenSeedReady = (sc.addressReadyTick, sc.walkReadyTick, sc.wordReadyTick);
            // The post is theirs no more, and has been empty for two hours (the wait is an hour, see below): the midnight after the reload names another.
            (wardenSeedStaffId, wardenSeedRole) = (real.thingIDNumber, sc.role);
            sc.role = null;
            StaffCommute.StaffVersion++;
            real.Notify_DisabledWorkTypesChanged();
            // (The wait is shortened to an hour for the run: a game that is hours old has no two days behind it, and a vacancy tick under zero means "not vacant".)
            WardenSystem.WaitOverrideTicks = GenDate.TicksPerHour;
            g.wardenVacantTick = Mathf.Max(0, now - 2 * GenDate.TicksPerHour);
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null && x.Inmate().want == null).OrderBy(x => x.thingIDNumber).FirstOrDefault()
                ?? map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() != null);
            if (p == null)
            {
                WardenFail("saveload: no inmate to seed the warden's words on");
                return;
            }
            CompInmate c = p.Inmate();
            (c.wardenWordTick, c.headsUpTick, c.wardenFirmUntil) = (now - 123, now - 45, now + 4321);
            wardenSeedInmate = (c.wardenWordTick, c.headsUpTick, c.wardenFirmUntil);
            // (Added to the counters, not set over them: they are the real prison's.)
            g.stats.wardenAddresses += 3;
            g.stats.wardenWalks += 2;
            g.stats.wardenWords += 5;
            g.stats.wardenStruck += 1;
            wardenSeedInmateId = p.thingIDNumber;
            RPLog.Message($"[RimPrison:test] saveload warden: {p.LabelShort} word {c.wardenWordTick} heads-up {c.headsUpTick} firm until {c.wardenFirmUntil}; {real.LabelShort} is out of the post, vacant since {g.wardenVacantTick}");
        }

        private static int wardenSeedInmateId = -1;

        /// <summary>After the reload: the cooldowns, the words and the empty post came back as they went (the snapshot says so as a whole; this reads the seeds one by one).</summary>
        private static void CheckWardenAfterReload(Map map)
        {
            if (wardenSeedStaffId < 0) return;
            Pawn ex = map.mapPawns.FreeColonists.FirstOrDefault(x => x.thingIDNumber == wardenSeedStaffId);
            CompStaff sc = ex?.GetComp<CompStaff>();
            if (sc == null || sc.role != null || (sc.addressReadyTick, sc.walkReadyTick, sc.wordReadyTick) != wardenSeedReady)
                WardenFail($"after the reload the pawn who was the warden has role {sc?.role?.defName ?? "none"} (expected none) and cooldowns {sc?.addressReadyTick}/{sc?.walkReadyTick}/{sc?.wordReadyTick} (expected {wardenSeedReady.address}/{wardenSeedReady.walk}/{wardenSeedReady.word})");
            CompInmate c = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == wardenSeedInmateId)?.Inmate();
            if (c == null || (c.wardenWordTick, c.headsUpTick, c.wardenFirmUntil) != wardenSeedInmate)
                WardenFail($"after the reload an inmate's word, heads-up and firm address are {c?.wardenWordTick}/{c?.headsUpTick}/{c?.wardenFirmUntil} (expected {wardenSeedInmate.word}/{wardenSeedInmate.headsUp}/{wardenSeedInmate.firm})");
            if (WardenSystem.InPost(map) != null || PrisonGame.Instance.wardenVacantTick < 0) WardenFail($"after the reload the post is not empty, or its wait is gone (wait {PrisonGame.Instance.wardenVacantTick})");
            RPLog.Message("[RimPrison:test] saveload warden: the post is empty after the reload; the midnight settlement is to name another");
        }

        /// <summary>
        /// End of the saveload plan: the real midnight settlement named a successor (an empty post, its wait over, through the real hook), who is on the staff with a cot; the pawn who was the warden is
        /// given the post back and the successor goes, so the run ends as it began.
        /// </summary>
        private static void CheckWardenOldSaveAtEnd(Map map)
        {
            if (wardenSeedStaffId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            Pawn ex = map.mapPawns.FreeColonists.FirstOrDefault(x => x.thingIDNumber == wardenSeedStaffId);
            Pawn successor = WardenSystem.InPost(map);
            if (successor == null || successor == ex || g.wardenVacantTick != -1 || !g.story.entries.Any(e => e.text == "RP_Chr_WardenAppointed".Translate(successor.LabelShort).Resolve()))
                WardenFail($"a prison with no warden was not given one by the midnight settlement after the reload (in post {successor?.LabelShort ?? "nobody"}, the wait {g.wardenVacantTick}, the line {successor != null && g.story.entries.Any(e => e.text == "RP_Chr_WardenAppointed".Translate(successor.LabelShort).Resolve())})");
            if (successor != null && successor != ex)
            {
                if (successor.ownership?.OwnedBed is { } cot && !cot.Destroyed && cot.OwnersForReading.Count <= 1) cot.Destroy();
                successor.Destroy(DestroyMode.Vanish);
            }
            if (ex?.GetComp<CompStaff>() is { } sc) sc.role = wardenSeedRole;
            StaffCommute.StaffVersion++;
            ex?.Notify_DisabledWorkTypesChanged();
            wardenSeedStaffId = -1;
            WardenSystem.WaitOverrideTicks = -1;
        }

        /// <summary>The warden's real hooks ran in a run of days, and the warden is still in post after them (an extreme break that walks them off or sets them wild would have taken them).</summary>
        private static void CheckWardenAtEnd(Map map, int days)
        {
            if (days >= 1 && WardenSystem.HourlyPasses == 0) problems0.Add("warden: the hourly pass of the warden never ran");
            if (days >= 2 && WardenSystem.DailyPasses == 0) problems0.Add("warden: the midnight pass of the warden never ran");
            // (The plans where nothing kills a member of staff: a warden who dies leaves the post empty for the days the state takes, and a run of the events or the stress plan has their deaths.)
            if (Plan is not ("normal" or "showcase")) return;
            Pawn w = WardenSystem.InPost(map);
            RPLog.Message($"[RimPrison:test] warden after {days} days: {(w == null ? "none in post" : $"{w.LabelShort} mood {w.needs?.mood?.CurLevelPercentage ?? -1f:0.00} joy {w.needs?.joy?.CurLevelPercentage ?? -1f:0.00} rest {w.needs?.rest?.CurLevelPercentage ?? -1f:0.00}, mental state {w.MentalStateDef?.defName ?? "none"}")}, wardens on the map {StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden)}");
            if (w == null) WardenFail($"after {days} days there is no warden in post (the wait {PrisonGame.Instance.wardenVacantTick})");
            else if (w.Faction != Faction.OfPlayer || !w.Spawned || w.MentalStateDef is { } m && (m.defName == "GiveUpExit" || m.defName == "Jailbreaker")) WardenFail($"after {days} days the warden is not on the floor ({w.Faction?.Name ?? "no faction"}, spawned {w.Spawned}, mental state {w.MentalStateDef?.defName ?? "none"})");
            if (StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden) > 1) WardenFail("after the run there is more than one warden on the staff");
        }
    }
}
