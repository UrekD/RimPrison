using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The regression checks of the supermax review's fixes (SupermaxChecks.cs has the unit and the first checks): what the pass opens, the errands of a walker, a supermax inmate left outside the cell,
    /// a walk across a load, a hearing's escort, no parole, the pay, the acquitted killer, single cells, the long-stay hediff after release, a fire, the shower shared with someone, the yard hours. Each is
    /// built to fail without the fix it is named for (see WORKLOG.md for how each was shown to).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The kind of walk that fetches an inmate who is out of the cell with nobody (by number: the checks look at what the work giver offers, whatever the name).</summary>
        private const EscortKind HomeWalk = (EscortKind)3;

        private static void CheckSupermaxFixes(Map map)
        {
            CheckSupermaxPassIsThePathsOwn(map);
            CheckSupermaxNoErrandsOnAWalk(map);
            CheckSupermaxFetchedWhenLeftOutside(map);
            CheckSupermaxPassEndsWhenTheGuardLetsGo(map);
            CheckSupermaxLoadRebuildsTheWalk(map);
            CheckSupermaxHearingEscort(map);
            CheckSupermaxSharedShower(map);
            CheckSupermaxNoParole(map);
            CheckSupermaxPay(map);
            CheckSupermaxAcquittedAndWitnesses(map);
            CheckSupermaxSingleCellsOnly(map);
            CheckSupermaxHediffLeavesWithThem(map);
            CheckSupermaxFireOpensTheCell(map);
            CheckSupermaxYardHours(map);
            CheckSupermaxStranded(map);
        }

        /// <summary>The walk the work giver offers this guard for this inmate (a problem noted, and null, when there is none).</summary>
        private static Job SupermaxOffer(SupermaxScene s, Pawn guard, Pawn p, string what, EscortKind? expected = null)
        {
            InmateCandidates.Shared.Invalidate();
            var giver = new WorkGiver_EscortInmate();
            if (!giver.HasJobOnThing(guard, p))
            {
                bool planned = SupermaxSystem.TryPlan(guard, p, out EscortKind kind, out IntVec3 target, out _);
                s.Fail($"{what}: the guard was not offered a walk for {p.LabelShort} (candidate {SupermaxSystem.Candidate(p)}, plan {planned} {kind} to {(target.IsValid ? (target - s.origin).ToString() : "nowhere")}, at {p.Position - s.origin}, leg {(p.Inmate().leg != null)})");
                return null;
            }
            Job job = giver.JobOnThing(guard, p);
            if (job != null && expected != null && (EscortKind)job.count != expected)
            {
                s.Fail($"{what}: the walk offered for {p.LabelShort} is a {(EscortKind)job.count} walk ({expected} expected)");
                return null;
            }
            return job;
        }

        private static bool SupermaxWalkOver(Pawn guard, CompInmate c, int ticked) => guard.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate && ticked > 30 && c.leg == null;

        // ---- the pass opens the doors of the walk's path, and no others --------------------------------------------------------------------------

        /// <summary>
        /// The walk to the shower: while the guard has them, the cell's own door and the shower's open to them; another inmate's cell door and a staff-only door that is not on the way (the yard's) do not.
        /// (The pass used to open every door that was not locked or touching staff's, cell doors of other inmates and staff-only doors between inmate spaces included.)
        /// </summary>
        private static void CheckSupermaxPassIsThePathsOwn(Map map)
        {
            Guarded("supermax: the pass opens the doors of the walk's path and no others", map, r =>
            {
                const string check = "pass";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zpassg1", s.CorridorWest);
                Pawn a = s.Inmate("Zpass1", s.bedA, s.CellA);
                s.Inmate("Zpass2", s.bedB, s.CellB);
                s.Inmate("Zpass3", s.bedC, s.CellC);
                CompInmate ac = a.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                // The yard's door is staff-only: not on the way to the shower, and shut to them.
                CompPrisonDoor yard = s.yardDoor.GetComp<CompPrisonDoor>();
                yard.mode = PrisonDoorMode.StaffOnly;
                r.Add(() => yard.mode = PrisonDoorMode.Auto);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Job job = SupermaxOffer(s, guard, a, "the walk to the shower", EscortKind.Shower);
                if (job == null) return;
                s.Start(guard, job);
                var opened = new HashSet<string>();
                bool ownDoor = false, finished = false;
                int ticked = 0;
                while (ticked < 7000 && !finished)
                {
                    SupermaxTick();
                    ticked++;
                    if (ac.leg is { phase: EscortPhase.Out })
                    {
                        if (s.doorB.PawnCanOpen(a)) opened.Add("the second cell's door");
                        if (s.doorC.PawnCanOpen(a)) opened.Add("the third cell's door");
                        if (s.yardDoor.PawnCanOpen(a)) opened.Add("the staff-only yard door");
                        ownDoor |= s.doorA.PawnCanOpen(a);
                    }
                    finished = SupermaxWalkOver(guard, ac, ticked);
                }
                if (opened.Count > 0 || !ownDoor || !finished)
                    s.Fail($"the pass on the walk to the shower: opened {(opened.Count == 0 ? "nothing it should not" : string.Join(", ", opened))} (nothing expected), their own cell's door {ownDoor} (expected), the walk finished {finished} after {ticked} ticks");
                // (The shower's own door is an ordinary one: it opens to any inmate in inmate space, guard or none.)
                if (s.doorA.PawnCanOpen(a)) s.Fail("after the walk their cell's door still opens to the inmate by themselves");
            });
        }

        // ---- no errand of a walker's uses the pass; the yard is the patch they were walked to --------------------------------------------------------

        /// <summary>
        /// A walker with a full bladder in the yard does not go for the nearest toilet (the one in the next cell, behind a door the pass would open), and one who has got out of the patch is walked back to it
        /// (the wander used to take the zone at their feet, a null zone in an unzoned corridor: an exception on every think).
        /// </summary>
        private static void CheckSupermaxNoErrandsOnAWalk(Map map)
        {
            Guarded("supermax: no toilet trips or wandering off on a walk", map, r =>
            {
                const string check = "errands";
                SupermaxScene s = SupermaxScene.Build(map, r, check, toilets: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zerrg1", s.CorridorWest);
                Pawn a = s.Inmate("Zerr1", s.bedA, s.CellA);
                s.Inmate("Zerr2", s.bedB, s.CellB);
                s.Inmate("Zerr3", s.bedC, s.CellC);
                CompInmate ac = a.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPSupermaxDefOf.RP_YardAlone);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                a.needs.TryGetNeed(RPDefOf.RP_Bladder).CurLevel = 0.1f;
                Job job = SupermaxOffer(s, guard, a, "the yard walk", EscortKind.Yard);
                if (job == null) return;
                s.Start(guard, job);
                bool intoOthers = false, usedFixture = false, finished = false, checkedBack = false;
                string backJob = "not tried";
                int ticked = 0, arrivedAt = -1;
                while (ticked < 9000 && !finished)
                {
                    SupermaxTick();
                    ticked++;
                    intoOthers |= s.InCell(a, s.bedB) || s.InCell(a, s.bedC);
                    usedFixture |= a.CurJobDef == RPDefOf.RP_UseFixture;
                    if (ac.leg is { arrivedTick: >= 0 } && arrivedAt < 0) arrivedAt = ticked;
                    // Pushed out of the patch into the unzoned corridor: the way back to it is the job (and no exception).
                    if (arrivedAt >= 0 && ticked == arrivedAt + 120 && !checkedBack && ac.leg != null)
                    {
                        checkedBack = true;
                        s.Place(a, s.CorridorEast);
                        try
                        {
                            Job back = EscortWalk.JobFor(a, ac, s.pm);
                            backJob = back == null ? "none" : $"{back.def.defName} to {(back.targetA.Cell - s.origin)}";
                            if (back == null || back.def != JobDefOf.Goto || s.pm.zones.ZoneAt(back.targetA.Cell) != RPSupermaxDefOf.RP_SupermaxYard) s.Fail($"an inmate pushed out of the yard patch into the corridor is given {backJob} (a walk back to the yard expected)");
                        }
                        catch (Exception e)
                        {
                            s.Fail($"the inmate's job when they stand outside the yard patch on a walk threw {e.GetType().Name}: {e.Message}");
                        }
                    }
                    finished = SupermaxWalkOver(guard, ac, ticked);
                }
                if (intoOthers || usedFixture || !finished || !s.InCell(a, s.bedA) || arrivedAt < 0)
                    s.Fail($"the yard walk with a full bladder: went into another inmate's cell {intoOthers}, used a toilet on the way {usedFixture}, finished {finished} after {ticked} ticks, back in the cell {s.InCell(a, s.bedA)}, arrived in the yard {(arrivedAt >= 0)}; pushed-out job {backJob}");
            });
        }

        // ---- left outside the cell with nobody: they wait, and a guard fetches them (also through a staff-only door) --------------------------------

        /// <summary>
        /// Moved into supermax from another cell (the walk to the new cell is a guard's, not theirs); left in the yard by a guard who stopped (they stay put, another guard fetches them through the staff-only unit
        /// door); never offered to a patient. (They walked home alone, and with a staff-only unit door were locked out for good.)
        /// </summary>
        private static void CheckSupermaxFetchedWhenLeftOutside(Map map)
        {
            Guarded("supermax: left outside the cell they wait for a guard, who fetches them", map, r =>
            {
                const string check = "fetch";
                SupermaxScene s = SupermaxScene.Build(map, r, check, unitDoor: true);
                if (s == null) return;
                PrisonGame g = s.g;
                CompPrisonDoor unit = s.unitDoor.GetComp<CompPrisonDoor>();
                r.Add(() => unit.mode = PrisonDoorMode.Auto);
                Pawn g1 = s.Guard("Zfetchg1", s.CorridorWest), g2 = s.Guard("Zfetchg2", s.CorridorEast);
                // A medium security inmate in the third cell (no wing), who is sent to supermax: one of the two cells of the wing, whichever they are given.
                Pawn x = s.Inmate("Zfetch1", s.bedC, s.CellC, InmateUtility.DefaultSecurity);
                CompInmate xc = x.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPSupermaxDefOf.RP_YardAlone);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (!SupermaxSystem.TrySend(x, xc, SupermaxReason.Warden) || x.ownership.OwnedBed is not { } home || home != s.bedA && home != s.bedB) { s.Fail($"the check's set-up is wrong: the inmate was not sent to a cell of the wing (bed {x.ownership.OwnedBed?.Position - s.origin})"); return; }
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                // Nobody comes for ten seconds: they do not walk the prison alone to the cell (they stand where they are).
                bool alone = false;
                Room waitingIn = x.GetRoom();
                ShuttleTicks(() =>
                {
                    alone |= x.GetRoom() != waitingIn;
                    return alone;
                }, 600);
                bool placed = InmateUtility.IsActiveInmate(x) && RegimePlacement.InExpectedPlace(x, RPSupermaxDefOf.RP_YardAlone, s.pm);
                if (alone || xc.leg != null || placed) s.Fail($"the inmate sent to supermax from another cell {(alone ? "walked off alone" : xc.leg != null ? "has a walk with nobody" : "counts as where the regime wants them (a supermax inmate outside the cell with no guard)")}");
                Job fetch = SupermaxOffer(s, g1, x, "the walk to the new cell", HomeWalk);
                if (fetch == null) return;
                s.Start(g1, fetch);
                int ticked = 0;
                while (ticked < 4000 && !SupermaxWalkOver(g1, xc, ticked))
                {
                    SupermaxTick();
                    ticked++;
                }
                if (!s.InCell(x, home) || xc.leg != null || g1.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate)
                    s.Fail($"the guard did not see the inmate into the new cell: after {ticked} ticks in it {s.InCell(x, home)}, walk still on {xc.leg != null}, guard {g1.CurJobDef?.defName}, inmate at {x.Position - s.origin}");
                // From here the unit's door is staff-only (the player keeps general population out): the walk to the yard and back, and a walk home from the yard, are the guards'.
                unit.mode = PrisonDoorMode.StaffOnly;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                // ---- a guard who stops while they are in the yard (the unit door is staff-only: they cannot walk back alone)
                Job yard = SupermaxOffer(s, g1, x, "the yard walk", EscortKind.Yard);
                if (yard == null) return;
                s.Start(g1, yard);
                ticked = ShuttleTicks(() => xc.leg is { arrivedTick: >= 0 } && s.pm.zones.ZoneAt(x.Position) == RPSupermaxDefOf.RP_SupermaxYard, 5000);
                if (xc.leg is not { arrivedTick: >= 0 }) { s.Fail($"the inmate did not get to the yard through the staff-only unit door ({ticked} ticks, at {x.Position - s.origin}, doing {x.CurJobDef?.defName})"); return; }
                g1.jobs.StopAll();
                SupermaxTick();
                bool noLeg = xc.leg == null;
                bool stayed = true;
                int waited = ShuttleTicks(() =>
                {
                    stayed &= s.pm.zones.ZoneAt(x.Position) == RPSupermaxDefOf.RP_SupermaxYard;
                    return !stayed;
                }, 500);
                if (!noLeg || !stayed) s.Fail($"the guard's walk stopped in the yard: the inmate's walk ended {noLeg}, they stayed in the yard for {waited} ticks {stayed} (at {x.Position - s.origin}, doing {x.CurJobDef?.defName})");
                Job lost = SupermaxOffer(s, g2, x, "the walk home after the guard stopped", HomeWalk);
                if (lost == null) return;
                s.Start(g2, lost);
                ticked = 0;
                while (ticked < 5000 && !SupermaxWalkOver(g2, xc, ticked))
                {
                    SupermaxTick();
                    ticked++;
                }
                if (!s.InCell(x, home) || xc.leg != null)
                    s.Fail($"another guard did not bring the inmate home through the staff-only unit door: after {ticked} ticks in the cell {s.InCell(x, home)}, walk still on {xc.leg != null}, inmate at {x.Position - s.origin}");
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (s.unitDoor.PawnCanOpen(x) || (home == s.bedA ? s.doorA : s.doorB).PawnCanOpen(x)) s.Fail("after the walk a door of the unit opens to the inmate by themselves");
                // ---- a patient is not walked: they stay in bed (the medical rest is theirs to leave when they are well)
                s.Place(x, s.CellC);
                var cut = (Hediff_Injury)HediffMaker.MakeHediff(HediffDefOf.Cut, x, x.RaceProps.body.AllParts.First(part => part.def == BodyPartDefOf.Arm));
                cut.Severity = 4f;
                x.health.AddHediff(cut);
                InmateCandidates.Shared.Invalidate();
                bool patientOffered = SupermaxSystem.Candidate(x);
                x.health.RemoveHediff(cut);
                InmateCandidates.Shared.Invalidate();
                bool wellOffered = SupermaxSystem.Candidate(x);
                if (patientOffered || !wellOffered) s.Fail($"a walk home for an inmate outside the cell: offered while they are bleeding {patientOffered}, when they are well {wellOffered} (false, true expected)");
            });
        }

        // ---- the pass ends when the guard stops holding them --------------------------------------------------------------------------------------------

        /// <summary>A guard who is far from the inmate (or down) does not have them by the arm: the pass is gone within a moment. (It used to last as long as the guard's job did, wherever they were.)</summary>
        private static void CheckSupermaxPassEndsWhenTheGuardLetsGo(Map map)
        {
            Guarded("supermax: the pass ends when the guard does not hold them", map, r =>
            {
                const string check = "let go";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                foreach (string how in new[] { "far", "down" })
                {
                    Pawn guard = s.Guard("Zletg" + how, s.CorridorWest);
                    Pawn a = s.Inmate("Zlet" + how, s.bedA, s.CellA);
                    CompInmate ac = a.Inmate();
                    Job job = SupermaxOffer(s, guard, a, "the walk to the shower (" + how + ")", EscortKind.Shower);
                    if (job == null) return;
                    s.Start(guard, job);
                    ShuttleTicks(() => ac.leg is { phase: EscortPhase.Out } && !s.InCell(a, s.bedA), 2000);
                    if (ac.leg == null || s.InCell(a, s.bedA)) { s.Fail($"the walk did not set out ({how}): leg {ac.leg != null}, in the cell {s.InCell(a, s.bedA)}"); return; }
                    bool holding = s.doorA.PawnCanOpen(a);
                    if (how == "far")
                    {
                        // (Somewhere far off: any standable cell more than the walk's reach from them.)
                        IntVec3 farCell = GenRadial.RadialCellsAround(a.Position, 75f, true).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.DistanceTo(a.Position) > EscortWalk.NearCells + 8);
                        if (!farCell.IsValid) { s.Fail("no standable cell far from the unit to send the guard to"); return; }
                        // (Moved, not teleported: a teleport ends the guard's job, and with it the walk, in any case; and not on a path, whose next step would bring them back.)
                        guard.pather.StopDead();
                        guard.Position = farCell;
                    }
                    else HealthUtility.DamageUntilDowned(guard, allowBleedingWounds: false);
                    int waited = ShuttleTicks(() => ac.leg == null, 60);
                    bool afterwards = s.doorA.PawnCanOpen(a);
                    if (!holding || ac.leg != null || afterwards)
                        s.Fail($"the guard let go ({how}): their cell's door opened while the guard held them {holding}, the walk was over after {waited} ticks (within 60 expected) {ac.leg == null}, their cell's door still opens to the inmate {afterwards}");
                    guard.jobs.StopAll();
                    s.Place(a, s.CellA);
                    EscortWalk.End(a, ac);
                    a.Destroy(DestroyMode.Vanish);
                    if (!guard.Destroyed) guard.Destroy(DestroyMode.Vanish);
                }
            });
        }

        // ---- a load in the middle of a walk -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The walk is not saved, the guard's job is: after a load every toil of the job makes the walk again (here: the walk cleared and the driver told it was loaded, as a load leaves it, in each toil of
        /// the job in turn, and a few ticks). The real round trip, with a save in the middle of a walk, is in the saveload plan (<see cref="SeedSupermaxForReload"/>). (After a load the first toil to run found
        /// no walk and ended the job, and the inmate walked on alone.)
        /// </summary>
        private static void CheckSupermaxLoadRebuildsTheWalk(Map map)
        {
            Guarded("supermax: after a load every toil makes the walk again", map, r =>
            {
                const string check = "load";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zloadg1", s.CorridorWest);
                Pawn a = s.Inmate("Zload1", s.bedA, s.CellA);
                CompInmate ac = a.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Job job = SupermaxOffer(s, guard, a, "the walk to the shower", EscortKind.Shower);
                if (job == null) return;
                s.Start(guard, job);
                FieldInfo loadedField = typeof(JobDriver_EscortInmate).GetField("loaded", BindingFlags.NonPublic | BindingFlags.Instance);
                // The toils: 2 lead, 3 stay, 4 fetch, 6 walk home (0 meet, 1 and 5 do something at once; 7, the wait to see them in, ends the moment they are in the cell).
                var tested = new List<int>();
                var failed = new List<string>();
                int ticked = 0;
                foreach (int toil in new[] { 2, 3, 4, 6 })
                {
                    // Into the toil (the stay is ended by hand: the inmate is clean enough).
                    while (ticked < 9000 && (guard.jobs.curDriver?.CurToilIndex ?? -1) < toil && guard.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate)
                    {
                        // (The stay is over, and the inmate is some way off, so that the guard has a walk to them: the fetch is a toil that lasts.)
                        if (toil >= 4 && guard.jobs.curDriver.CurToilIndex == 3 && ac.leg is { } leg)
                        {
                            s.Place(a, s.CorridorEast);
                            leg.done = true;
                        }
                        SupermaxTick();
                        ticked++;
                    }
                    JobDriver driver = guard.jobs.curDriver;
                    if (guard.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate || driver == null || driver.CurToilIndex != toil) { failed.Add($"toil {toil} never ran (job {guard.CurJobDef?.defName}, toil {driver?.CurToilIndex})"); break; }
                    tested.Add(toil);
                    EscortPhase phase = toil <= 4 ? EscortPhase.Out : EscortPhase.Back;
                    // A moment into the toil, then the load: no walk, a driver that has just been loaded.
                    for (int i = 0; i < 4; i++) { SupermaxTick(); ticked++; }
                    if (guard.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate || guard.jobs.curDriver.CurToilIndex != toil) { failed.Add($"toil {toil} ended at once"); continue; }
                    ac.leg = null;
                    loadedField?.SetValue(driver, true);
                    for (int i = 0; i < 3; i++) { SupermaxTick(); ticked++; }
                    bool on = guard.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate;
                    if (!on || ac.leg == null || ac.leg.guard != guard || ac.leg.phase != phase)
                        failed.Add($"toil {toil}: job still on {on}, walk made again {ac.leg != null} (guard {ac.leg?.guard == guard}, phase {ac.leg?.phase}, {phase} expected)");
                    if (!on) break;
                }
                if (failed.Count > 0) s.Fail("after a load: " + string.Join("; ", failed) + $" (toils tested: {string.Join(",", tested)})");
                // The rest of the walk: the inmate is in the cell and nobody holds them.
                ticked = ShuttleTicks(() => SupermaxWalkOver(guard, ac, 99), 4000);
                if (guard.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate && failed.Count == 0 || failed.Count == 0 && !s.InCell(a, s.bedA))
                    s.Fail($"after the loads the walk did not finish: guard {guard.CurJobDef?.defName}, in the cell {s.InCell(a, s.bedA)}, after {ticked} more ticks");
            });
        }

        // ---- the hearing's escort -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A called inmate who may not walk alone (maximum security, a cell locked all day) is walked to the hearing room by a guard: the pass opens the doors on the way and no other, it is made again after a load,
        /// and it ends when the inmate gets up and leaves their chair (the case is called again for another escort), not two hours later. (The escort kept the guard and the pass for up to two hours.)
        /// </summary>
        private static void CheckSupermaxHearingEscort(Map map)
        {
            Guarded("supermax: the escort to a hearing", map, r =>
            {
                const string check = "hearing escort";
                // (The hourly pass drops a hearing that has no room to be held in, which this one has not: not an hour boundary in the middle of the check.)
                int toNextHour = GenDate.TicksPerHour - GenLocalDate.DayTick(map) % GenDate.TicksPerHour;
                if (toNextHour < 2200) ShuttleTicks(() => false, toNextHour + 5);
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                SecurityLevelDef maximum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum");
                SupermaxRegimeAllDay(g, r, maximum, RPDefOf.RP_Lockup);
                Pawn guard = s.Guard("Zhearg1", s.CorridorWest);
                // (The yard's door is staff-only: not on the way to the post, and shut to them.)
                CompPrisonDoor yardMode = s.yardDoor.GetComp<CompPrisonDoor>();
                yardMode.mode = PrisonDoorMode.StaffOnly;
                r.Add(() => yardMode.mode = PrisonDoorMode.Auto);
                Pawn p = s.Inmate("Zhear1", s.bedA, s.CellA, maximum);
                s.Inmate("Zhear2", s.bedB, s.CellB, maximum);
                CompInmate c = p.Inmate();
                List<ParoleCase> cases0 = g.story.paroleCases.ToList();
                r.Add(() =>
                {
                    g.story.paroleCases.Clear();
                    g.story.paroleCases.AddRange(cases0);
                });
                var hc = new ParoleCase { pawnId = p.thingIDNumber, name = p.LabelShort, stage = HearingStage.Called, scheduledTick = s.Now, calledTick = s.Now };
                g.story.paroleCases.Add(hc);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                bool alone = p.CanReach(s.CorridorEast, PathEndMode.OnCell, Danger.Deadly);
                Job job = JobMaker.MakeJob(RPParoleDefOf.RP_EscortToHearing, p, s.CorridorEast);
                job.count = 1;
                job.locomotionUrgency = LocomotionUrgency.Walk;
                s.Start(guard, job);
                int ticked = ShuttleTicks(() => c.leg is { kind: EscortKind.Hearing }, 600);
                if (c.leg is not { kind: EscortKind.Hearing } leg || leg.guard != guard) { s.Fail($"the escort did not set out with the inmate (after {ticked} ticks leg {c.leg?.kind}, at {p.Position - s.origin}, guard doing {guard.CurJobDef?.defName})"); return; }
                bool ownDoor = s.doorA.PawnCanOpen(p), otherCell = s.doorB.PawnCanOpen(p), unrelated = s.yardDoor.PawnCanOpen(p);
                if (alone || !ownDoor || otherCell || unrelated) s.Fail($"the pass of the hearing's escort: they could reach the corridor alone {alone}, their own door opens {ownDoor}, another inmate's cell door {otherCell}, the yard's door {unrelated} (false, true, false, false expected)");
                // A load in the middle of it: the walk is made again by the next beat.
                c.leg = null;
                ShuttleTicks(() => false, 20);
                if (c.leg is not { kind: EscortKind.Hearing } || guard.CurJob?.def != RPParoleDefOf.RP_EscortToHearing) s.Fail($"after a load the hearing's escort was not made again (leg {c.leg?.kind}, guard doing {guard.CurJobDef?.defName})");
                // Now at the post, waiting for the board: the inmate gets up and leaves their chair (the case is called again, nobody escorts it): the guard's wait is over and the pass with it.
                ticked = ShuttleTicks(() => guard.jobs.curDriver?.CurToilIndex >= 3, 3000);
                if ((guard.jobs.curDriver?.CurToilIndex ?? -1) < 3) { s.Fail($"the guard never got to the post ({ticked} ticks, toil {guard.jobs.curDriver?.CurToilIndex}, at {guard.Position - s.origin})"); return; }
                (hc.stage, hc.escorted, hc.escortId, hc.escortName) = (HearingStage.Called, false, -1, null);
                int over = ShuttleTicks(() => guard.CurJob?.def != RPParoleDefOf.RP_EscortToHearing, 300);
                if (guard.CurJob?.def == RPParoleDefOf.RP_EscortToHearing || c.leg != null)
                    s.Fail($"the inmate left their chair and the guard's escort went on ({over} ticks later: guard doing {guard.CurJobDef?.defName}, the inmate's walk {(c.leg != null ? "still on" : "over")})");
            });
        }

        // ---- a shower that is not the wing's own -----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The prison's shower is the wing's showers (none painted here): the stay ends, and they are walked back, when another inmate comes into the room; and an inmate standing in a general shower with
        /// nobody has not got where the regime wants them. The regime's own hours keep supermax's showers off every other level's.
        /// </summary>
        private static void CheckSupermaxSharedShower(Map map)
        {
            Guarded("supermax: a shower that someone else comes into", map, r =>
            {
                const string check = "shared shower";
                SupermaxScene s = SupermaxScene.Build(map, r, check, sharedShower: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zshareg1", s.CorridorWest);
                Pawn a = s.Inmate("Zshare1", s.bedA, s.CellA);
                Pawn intruder = s.Inmate("Zshare2", s.bedB, s.CellB);
                CompInmate ac = a.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                a.needs.TryGetNeed(RPDefOf.RP_Hygiene).CurLevel = 0.02f;
                Job job = SupermaxOffer(s, guard, a, "the walk to a general shower", EscortKind.Shower);
                if (job == null) return;
                s.Start(guard, job);
                int ticked = ShuttleTicks(() => ac.leg is { arrivedTick: >= 0 } && a.CurJobDef == RPDefOf.RP_UseFixture, 4000);
                if (a.CurJobDef != RPDefOf.RP_UseFixture) { s.Fail($"the inmate did not start to wash in {ticked} ticks (at {a.Position - s.origin}, doing {a.CurJobDef?.defName})"); return; }
                // Standing in the general shower with no guard is not where the regime wants them; on a walk it is.
                bool placedOnWalk = RegimePlacement.InExpectedPlace(a, RPDefOf.RP_ShowerTime, s.pm);
                // Another inmate comes into the room: the stay is over within the half hour (they were to wash for as long as it took).
                s.Place(intruder, s.ShowerCell + new IntVec3(1, 0, 0));
                int out_ = ShuttleTicks(() => ac.leg == null || ac.leg.done || ac.leg.phase == EscortPhase.Back, 400);
                bool ended = ac.leg == null || ac.leg.done || ac.leg.phase == EscortPhase.Back;
                if (!placedOnWalk || !ended) s.Fail($"another inmate came into the shower room: the stay was over within {out_} ticks (400 expected at most) {ended}; a walker in the shower counts as in their place {placedOnWalk}");
                guard.jobs.StopAll();
                EscortWalk.End(a, ac);
                s.Place(a, s.ShowerCell);
                bool strayPlaced = RegimePlacement.InExpectedPlace(a, RPDefOf.RP_ShowerTime, s.pm);
                if (strayPlaced) s.Fail("a supermax inmate standing in a general shower with no guard counts as where the regime wants them");
                // The hours: supermax's showers are in hours of their own, and the yard's are not shower hours.
                SecurityLevelDef level = s.level;
                List<int> showerHours = Enumerable.Range(0, 24).Where(h => level.defaultRegime[h] == RPDefOf.RP_ShowerTime).ToList();
                foreach (SecurityLevelDef other in DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(x => x != level && x.defaultRegime.Count == 24))
                {
                    List<int> shared = showerHours.Where(h => other.defaultRegime[h] == RPDefOf.RP_ShowerTime).ToList();
                    if (shared.Count > 0) s.Fail($"{other.defName} and supermax shower at the same hour ({string.Join(", ", shared)})");
                }
            });
        }

        // ---- no parole from supermax --------------------------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxNoParole(Map map)
        {
            Guarded("supermax: no parole", map, r =>
            {
                const string check = "parole";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn p = s.Inmate("Zparole1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                // Past half of a short sentence, not heard for a long time, a calm record: the board would hear anyone else.
                (c.sentenceDays, c.admittedTick) = (0, 0);
                c.lastHearingTick = -999999;
                c.tension = 0f;
                bool dueSupermax = ParoleSystem.HearingDue(g, p, c);
                c.security = InmateUtility.DefaultSecurity;
                bool dueControl = ParoleSystem.HearingDue(g, p, c);
                c.security = s.level;
                int granted = 0;
                for (int i = 0; i < 60; i++)
                    if (ParoleSystem.BoardDecides(p, c)) granted++;
                if (dueSupermax || !dueControl || granted > 0) s.Fail($"parole: a hearing is due for a supermax inmate {dueSupermax}, for the same inmate at medium security {dueControl} (false, true expected); the board's roll granted {granted} of 60 (0 expected)");
            });
        }

        // ---- the pay ------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Sent there by the rules (a conviction): the supermax rate. Sent by the warden's order: the maximum rate. (The warden's shoplifter paid six times what they are worth.)</summary>
        private static void CheckSupermaxPay(Map map)
        {
            Guarded("supermax: the pay depends on who sent them", map, r =>
            {
                const string check = "pay";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                SecurityLevelDef medium = InmateUtility.DefaultSecurity, maximum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum");
                Pawn p = s.Inmate("Zpay1", s.bedC, s.CellC, medium);
                CompInmate c = p.Inmate();
                float multiplier = g.PaymentMultiplier;
                int Pay() => DailySettlement.DailyPayments(g, map, out _);
                int atMedium = Pay();
                bool warden = SupermaxSystem.TrySend(p, c, SupermaxReason.Warden);
                int byWarden = Pay() - atMedium;
                int wantWarden = Mathf.RoundToInt(maximum.dailyPayment * multiplier) - Mathf.RoundToInt(medium.dailyPayment * multiplier);
                // (Let go of the cell: back to medium security with the bed.)
                c.security = medium;
                p.ownership?.UnclaimBed();
                Building_Bed bed = IntakeSystem.FreePrisonerBeds(map, s.level).LastOrDefault();
                if (bed == null) { s.Fail("the check's set-up is wrong: no free supermax cell for the second send"); return; }
                p.ownership.ClaimBedIfNonMedical(s.bedC);
                s.pm.ForceDoorUpdate();
                bool rules = SupermaxSystem.TrySend(p, c, SupermaxReason.Convicted);
                int byRules = Pay() - atMedium;
                int wantRules = Mathf.RoundToInt(s.level.dailyPayment * multiplier) - Mathf.RoundToInt(medium.dailyPayment * multiplier);
                if (!warden || !rules || byWarden != wantWarden || byRules != wantRules)
                    s.Fail($"the daily payment: sent by the warden {warden}, paid {byWarden} more than at medium (maximum's rate: {wantWarden} expected); sent by the rules {rules}, paid {byRules} more (supermax's rate: {wantRules} expected)");
            });
        }

        // ---- the acquitted killer and the witnesses -----------------------------------------------------------------------------------------------------------

        /// <summary>A staff killer put in supermax at the charge goes back to the level they came from when the court acquits them; a supermax inmate is not moved to protective custody as a witness.</summary>
        private static void CheckSupermaxAcquittedAndWitnesses(Map map)
        {
            Guarded("supermax: the acquitted killer and the witness", map, r =>
            {
                const string check = "acquittal";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                var court = new CourtScene(map, r);
                Pawn killer = court.Inmate("Zacquit61"), victim = court.Guard("Zacquitg61");
                CompInmate kc = killer.Inmate();
                SecurityLevelDef before = kc.security;
                StaffDeaths.Notify_Died(victim, victim.GetComp<CompStaff>(), new DamageInfo(DamageDefOf.Stab, 20f, instigator: killer), null);
                MurderTrial trial = Trials.TrialOf(g, killer);
                if (!SupermaxSystem.IsIn(kc) || trial == null) { s.Fail($"the check's set-up is wrong: the killer is in {kc.security?.defName}, a trial {trial != null}"); return; }
                bool convictedStays = false;
                Trials.Acquit(g, map, trial, killer, kc, "the court", "the evidence", 0, false);
                convictedStays = SupermaxSystem.IsIn(kc);
                if (convictedStays || kc.security != before) s.Fail($"the court acquitted the killer of an officer: they are in {kc.security?.defName} (back to {before?.defName} expected), the supermax cell kept {convictedStays}");
                // A witness in supermax is not for protective custody (it lets them mix with the vulnerable, and skips the step-down).
                Pawn witness = s.Inmate("Zwitness61", s.bedB, s.CellB);
                bool witnessProtectable = Trials.ProtectableAsWitness(g, witness, witness.Inmate());
                witness.Inmate().security = InmateUtility.DefaultSecurity;
                bool controlProtectable = Trials.ProtectableAsWitness(g, witness, witness.Inmate());
                witness.Inmate().security = s.level;
                if (witnessProtectable || !controlProtectable) s.Fail($"witness protection: a supermax witness may be moved {witnessProtectable}, the same inmate at medium security {controlProtectable} (false, true expected)");
            });
        }

        // ---- single cells only --------------------------------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxSingleCellsOnly(Map map)
        {
            Guarded("supermax: a cell of the wing with two beds takes nobody", map, r =>
            {
                const string check = "single cells";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                int single = IntakeSystem.FreeBedCount(map, s.level);
                s.PaintCellCAsWing();
                Building_Bed second = s.ExtraBedInCellC();
                int withDoubleCell = IntakeSystem.FreeBedCount(map, s.level);
                Pawn p = s.Inmate("Zsingle1", null, s.CellA, InmateUtility.DefaultSecurity);
                bool refused = SupermaxSystem.CannotSend(p, p.Inmate()) == null && SupermaxSystem.TrySend(p, p.Inmate(), SupermaxReason.Warden);
                bool inDouble = p.ownership?.OwnedBed is { } bed && (bed == s.bedC || bed == second);
                if (single != 2 || withDoubleCell != 2 || inDouble)
                    s.Fail($"free supermax cells: {single} in a two-cell unit (2 expected), {withDoubleCell} with the third cell painted and two beds in it (2 expected: it takes nobody); an inmate sent there was put in the double cell {inDouble} (went {refused})");
            });
        }

        // ---- the long-stay hediff does not leave with them --------------------------------------------------------------------------------------------------

        private static void CheckSupermaxHediffLeavesWithThem(Map map)
        {
            Guarded("supermax: the long stay does not walk out of the prison", map, r =>
            {
                const string check = "hediff on release";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                Pawn p = s.Inmate("Zrelease1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                c.lastReclassTick = s.Now - 21 * GenDate.TicksPerDay;
                SupermaxSystem.Sync(p, c);
                HediffDef def = RPSupermaxDefOf.RP_SupermaxStay;
                bool had = p.health.hediffSet.GetFirstHediffOfDef(def) != null;
                // Released (paroled, transferred: the same flag): the next beat of the inmate's own update drops the active flag.
                p.guest.Released = true;
                r.Add(() =>
                {
                    p.guest.Released = false;
                    c.RefreshActiveFlag();
                });
                c.RefreshActiveFlag();
                bool left = p.health.hediffSet.GetFirstHediffOfDef(def) == null;
                if (!had || !left) s.Fail($"the long-stay hediff: on after three weeks {had}, gone when they are let go {left}");
            });
        }

        // ---- a fire opens a locked cell --------------------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxFireOpensTheCell(Map map)
        {
            Guarded("supermax: a fire opens a locked cell", map, r =>
            {
                const string check = "fire";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                s.Inmate("Zfire1", s.bedA, s.CellA);
                CompPrisonDoor door = s.doorA.GetComp<CompPrisonDoor>();
                s.pm.ForceDoorUpdate();
                int now = s.Now, hour = GenLocalDate.HourOfDay(map);
                bool lockedBefore = !s.pm.CellDoorOpen(door, g, now, hour);
                var results = new List<string>();
                foreach ((string where, IntVec3 cell) in new[] { ("in the cell", s.At(2, 4)), ("in the corridor outside", s.At(3, 1)) })
                {
                    var fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    GenSpawn.Spawn(fire, cell, map);
                    bool open = s.pm.CellDoorOpen(door, g, now, hour);
                    fire.Destroy(DestroyMode.Vanish);
                    if (!open) results.Add(where);
                }
                bool lockedAfter = !s.pm.CellDoorOpen(door, g, now, hour);
                if (!lockedBefore || !lockedAfter || results.Count > 0)
                    s.Fail($"a fire and a locked supermax cell: locked before {lockedBefore} and after {lockedAfter} (both expected), still locked with a fire {(results.Count == 0 ? "nowhere" : string.Join(" and ", results))}");
            });
        }

        // ---- the yard hours ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The regime has the yard in several hours (one patch takes one inmate at a time: a unit of four cells needs four), and a day's yard that was missed is told at midnight.</summary>
        private static void CheckSupermaxYardHours(Map map)
        {
            Guarded("supermax: the yard is in enough hours, and a missed one is told", map, r =>
            {
                const string check = "yard hours";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level;
                int yards = level.defaultRegime.Count(a => a == RPSupermaxDefOf.RP_YardAlone), showers = level.defaultRegime.Count(a => a == RPDefOf.RP_ShowerTime);
                if (yards < 4 || showers < 3) s.Fail($"the default regime has {yards} yard hours and {showers} shower hours (a unit of four cells needs at least 4 and 3)");
                int slot = SupermaxSystem.SlotOf(map);
                Pawn missed = s.Inmate("Zyardm1", s.bedA, s.CellA), had = s.Inmate("Zyardm2", s.bedB, s.CellB), fresh = s.Inmate("Zyardm3", s.bedC, s.CellC);
                missed.Inmate().yardSlot = slot - 30;
                had.Inmate().yardSlot = slot - 5;
                fresh.Inmate().yardSlot = -1;
                fresh.Inmate().lastReclassTick = s.Now - 1000;
                List<Pawn> told = SupermaxSystem.MissedYard(map, s.Now);
                if (!told.Contains(missed) || told.Contains(had) || told.Contains(fresh)) s.Fail($"who missed the yard: {string.Join(", ", told.Select(p => p.LabelShort))} (only {missed.LabelShort} expected)");
            });
        }
    }
}
