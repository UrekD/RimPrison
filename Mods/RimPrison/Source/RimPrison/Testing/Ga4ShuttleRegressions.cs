using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the shuttle items of gap analysis 4 (H3, M7, M8, M9 and the shuttle lows). They run on the clear stage of <see cref="ShuttleStage"/>, like the other shuttle
    /// regressions, and each fails without its fix (the walk to the ramp during overtime and the closed tick of H3 were shown by a copy of the mod with the fix taken out).
    /// </summary>
    public static partial class AutoTest
    {
        private static void RunGa4ShuttleChecks(Map map)
        {
            CheckGa4BoardingStaysOpenInOvertime(map);
            CheckGa4CargoRespectsTheSupplyCut(map);
            CheckGa4EscortNeedsAWayToHolding(map);
            CheckGa4CrateThatCannotBePlacedGoesBack(map);
            CheckGa4DepartureDeliversWhatWasSetDown(map);
            CheckGa4ColonistExPrisonerIsNotRoutedAboard(map);
            CheckGa4TransferWalkingOutIsNotFreed(map);
            CheckGa4CarriedOverWhoseLevelWantsNoneAreTakenBack(map);
            CheckGa4ArrivalClocksAreSeparate(map);
            CheckGa4PadBlockerIsOnAUsablePad(map);
        }

        // ---- H3 and M9: overtime ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The shuttle stays on for what is left to unload, and boarding stays open all that time: it closed for a tick every hundred ticks (pawns tick before the game components, and the
        /// lift-off was put back only after the boarding had closed), and every boarding walk in progress failed on that tick, so released inmates more than ~100 ticks from the ramp never
        /// boarded on exactly the busy days. Pad duty and the status line follow: pad duty is possible while the shuttle stays, and the status says it is unloading, not "lifting off in 0.1 h".
        /// </summary>
        private static void CheckGa4BoardingStaysOpenInOvertime(Map map)
        {
            Guarded("gap analysis 4 H3 and M9: boarding stays open in overtime", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "boarding in overtime")) return;
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(x => Unlocks.IsUnlocked(x) && !x.wingOnly).OrderByDescending(x => x.intakeGrant).First();
                int number0 = g.intake.nextInmateNumber;
                (int perDay0, int lastRun0) = (g.intake.PerDay(sec), g.intake.lastRunDay);
                g.intake.perDay[sec] = 0;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                if (v?.shuttle == null)
                {
                    Ga4Fail("H3: no shuttle called");
                    return;
                }
                // (Enough aboard that the unloading, which keeps the shuttle on, outlasts a walk of twelve cells in deep snow, forty ticks a cell and worse: ten stepped off in about a thousand ticks and the shuttle lifted
                // off with the walker still on their way, in the weather of some runs.)
                const int Aboard = 24;
                for (int i = 0; i < Aboard; i++)
                {
                    Pawn p = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                    if (v.shuttle.passengers.TryAdd(p, canMergeWithExistingStacks: false)) v.rosterSize++;
                    else p.Destroy();
                }
                g.intake.perDay[sec] = perDay0;
                g.intake.lastRunDay = lastRun0;
                r.Add(() =>
                {
                    foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { } c && c.inmateNumber >= number0).ToList()) ShuttleRemovePawn(p);
                    g.shuttle.carriedOver.Clear();
                });
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    Ga4Fail($"H3: the shuttle never landed ({v.Describe()})");
                    return;
                }
                // A released inmate a good walk from the ramp, waiting for the shuttle.
                Pawn walker = Throwaway(map, r);
                ShuttleServed(walker, r);
                IntVec3 ramp = v.shuttle.InteractionCell;
                // (The nearest cell 12 to 40 cells from the ramp from which the ramp can be reached: not where the throwaway stands, which is shut in the prison in most runs and could not reach it from there.)
                // (And one whose walk takes a good while, but not longer than the unloading outlasts: the straight distance says little - the weather's snow slows a walker to 40-65 ticks a cell and a wall makes the way round
                // twice as long. stability10-s10negB2: a walker twelve cells off was still walking, cell by cell, 22 cells further on, when the 1500 ticks of the wait ran out, with the shuttle there and boarding open. The pick is
                // by the path's cost - ticks, as the game counts them - from the walker's own start: first one that costs 150 to 1500, else the nearest there is.)
                IntVec3 far = IntVec3.Invalid, nearest = IntVec3.Invalid;
                float farCost = 0f, nearestCost = 0f;
                int tried = 0;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(ramp, 40f, true))
                {
                    if (c.DistanceTo(ramp) < 12f || !c.InBounds(map) || !c.Standable(map) || c.GetFirstPawn(map) != null || !map.reachability.CanReach(c, ramp, PathEndMode.OnCell, TraverseParms.For(walker, Danger.Deadly))) continue;
                    using PawnPath path = map.pathFinder.FindPathNow(c, ramp, TraverseParms.For(walker, Danger.Deadly));
                    float cost = path.Found ? path.TotalCost : float.MaxValue;
                    if (!nearest.IsValid) (nearest, nearestCost) = (c, cost);
                    if (cost >= 150f && cost <= 1500f)
                    {
                        (far, farCost) = (c, cost);
                        break;
                    }
                    if (++tried >= 60) break;
                }
                if (!far.IsValid) (far, farCost) = (nearest, nearestCost);
                if (!far.IsValid)
                {
                    int standable = 0, reachableCells = 0;
                    float farthest = 0f;
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(ramp, 40f, true))
                        if (c.InBounds(map) && c.Standable(map))
                        {
                            standable++;
                            if (map.reachability.CanReach(c, ramp, PathEndMode.OnCell, TraverseParms.For(walker, Danger.Deadly)))
                            {
                                reachableCells++;
                                farthest = Math.Max(farthest, c.DistanceTo(ramp));
                            }
                        }
                    RPLog.Message($"[RimPrison:test] gap analysis 4 H3: no cell 12-40 cells from the ramp to start the walk from, only the closed window was checked ({standable} standable cells within 40 of the ramp, {reachableCells} the walker can walk from and to, the farthest {farthest:F0}; the walker is at {walker.Position}, spawned {walker.Spawned})");
                }
                // (Released as the prison releases: no longer an active inmate, so nothing that looks for an inmate's chances (the hourly escape check, the stowaway roll) takes them for one.)
                ReleaseSystem.Release(g, walker, record: false);
                if (far.IsValid)
                {
                    walker.pather.StopDead();
                    walker.Position = far;
                    walker.Notify_Teleported();
                    // (Looks for something to do now, with the shuttle down: not when the wait it was in runs out.)
                    walker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                int walkBegan = ShNow;
                // (Nobody is rolled as a stowaway meanwhile: an inmate by the pad can be, the walker among them, and runs for the edge in an escape break instead of boarding.)
                v.stowawayRolled = true;
                // The stay is over: the shuttle stays on for the rest of the unloading.
                v.departTick = ShNow + 150;
                ShuttleTicks(() => g.shuttle.visit != v || v.stretched, 600);
                if (g.shuttle.visit != v || !v.stretched)
                {
                    Ga4Fail($"H3: the stay was not stretched for the unloading ({v.Describe()})");
                    return;
                }
                // The instant before it is put back (what the boarding walks see, ticking before the game components): still open.
                int departWas = v.departTick;
                v.departTick = ShNow + ShuttleSystem.BoardingClosesTicks;
                bool open = ShuttleSystem.BoardingOpen(v.shuttle) && ShuttleSystem.Boardable(map) != null;
                // The pad: a guard may stand duty while the shuttle stays (the last 400 ticks), the status says it is unloading.
                v.departTick = ShNow + 250;
                Pawn guard = PrisonMap.Of(map).Guards.FirstOrDefault(x => x.Spawned && !x.Downed && GuardUtility.IsGuard(x));
                bool padDuty = guard == null || new WorkGiver_PadDuty().HasJobOnThing(guard, v.shuttle);
                string status = ShuttleSystem.StatusLine(v);
                bool statusOk = ShuttleSystem.InOvertime(v) && status.StartsWith("RP_ShuttleStatusUnloading".Translate(((ShuttleSystem.HardDepartTick(v) - ShNow) / (float)GenDate.TicksPerHour).ToString("0.0"), v.boarded).ToString());
                v.departTick = departWas;
                if (!open) Ga4Fail("H3: boarding was shut at the tick before the shuttle's stay is put back, while it was staying on to unload (every boarding walk fails on that tick)");
                if (!padDuty) Ga4Fail("M9: pad duty was impossible in the last 400 ticks of an unloading that goes on (the cargo search runs unguarded)");
                if (!statusOk) Ga4Fail($"M9: the status line says \"{status}\" while the shuttle is staying on to unload");
                if (far.IsValid)
                {
                    // (How many boarding jobs the walk took: one if boarding stays open; every closed tick ends the job and the pawn is given another, so a walk of 400 ticks took five.)
                    var boardingJobs = new HashSet<int>();
                    // (The walk began at the pick, the stretch above included: it is given what its path costs and half as much again, 1500 ticks at the least, and no more than the shuttle can stay: the hard lift-off.)
                    int budget = Math.Max(1500, (int)(Math.Min(farCost, 100000f) * 1.5f) + 300) - (ShNow - walkBegan);
                    int stays = ShuttleSystem.HardDepartTick(v) - ShNow;
                    if (budget > stays)
                    {
                        RPLog.Message($"[RimPrison:test] gap analysis 4 H3: NOT RUN, the walk of {far.DistanceTo(ramp):F0} cells costs {farCost:F0} ticks and the shuttle may stay {stays} more (budget {budget})");
                        EndShuttleVisit(g, map);
                        return;
                    }
                    int walked = ShuttleTicksTraced(() =>
                    {
                        if (walker.CurJobDef == RPShuttleDefOf.RP_BoardShuttle) boardingJobs.Add(walker.CurJob.loadID);
                        return !walker.Spawned || g.shuttle.visit != v;
                    }, Math.Max(budget, 1), walker, "gap analysis 4 H3 walk");
                    bool boarded = !walker.Spawned && v.boarded >= 1;
                    RPLog.Message($"[RimPrison:test] gap analysis 4 H3: a walker {far.DistanceTo(ramp):F0} cells from the ramp boarded during overtime {boarded} after {walked} ticks (boarded on the visit {v.boarded}, passengers who stepped off {v.steppedOff}/{v.rosterSize})");
                    if (!boarded) Ga4Fail($"H3: an outbound inmate {far.DistanceTo(ramp):F0} cells from the ramp did not board while the shuttle stayed for its unloading (still on the map {walker.Spawned} at {walker.Position}, job {walker.CurJob}, {v.boarded} boarded, {boardingJobs.Count} boarding jobs, boardable {ShuttleSystem.Boardable(map) != null}, open {ShuttleSystem.BoardingOpen(v.shuttle)}; walked {walked} ticks after the {(ShNow - walkBegan - walked)} before the wait, the path from the start cost {farCost:F0} and the wait was given {Math.Max(budget, 1)}, the visit is {(g.shuttle.visit == v ? "still on, " + ShuttleSystem.StatusLine(v) : "over")}, {v.steppedOff}/{v.rosterSize} stepped off)");
                    else if (boardingJobs.Count > 2) Ga4Fail($"H3: the walk to the ramp during overtime took {boardingJobs.Count} boarding jobs (the job failed each time boarding closed for a tick, and was given again): one is expected");
                }
                EndShuttleVisit(g, map);
            });
        }

        // ---- M7: shuttle cargo ignores the supply cut --------------------------------------------------------------------------------------------------------

        private static void CheckGa4CargoRespectsTheSupplyCut(Map map)
        {
            Guarded("gap analysis 4 M7: shuttle cargo respects the supply cut", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer820", dealer: true);
                CompInmate dc = dealer.Inmate();
                dc.supplyCutUntilTick = s.Now + 3 * GenDate.TicksPerDay;
                var visit = new ShuttleVisit { kind = ShuttleKind.Cargo };
                bool slipped = g.shuttle.TryCargoContraband(g, map, visit, force: true, roll: 1f, only: dealer);
                if (slipped) Ga4Fail("M7: the cargo crew slipped a package in for a dealer who was taken out of a ring lately (every other way in is cut)");
                dc.supplyCutUntilTick = -1;
                bool control = g.shuttle.TryCargoContraband(g, map, visit, force: true, roll: 1f, only: dealer);
                if (!control) Ga4Fail("M7: once the cut is over the cargo crew slipped nothing in for the dealer (the control)");
            });
        }

        // ---- M8: the arrival escort loops while a gate is locked ----------------------------------------------------------------------------------------------

        private static void CheckGa4EscortNeedsAWayToHolding(Map map)
        {
            Guarded("gap analysis 4 M8: no escort for an arrival who cannot reach holding", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "escort and a locked gate")) return;
                PrisonMap pm = PrisonMap.Of(map);
                Pawn guard = pm.Guards.FirstOrDefault(x => x.Spawned && !x.Downed && GuardUtility.IsGuard(x));
                IntVec3 patch = ShuttleOpenPatch(map, 1);
                if (guard == null || !patch.IsValid || !pm.zones.Any(RPDefOf.RP_Holding))
                {
                    RPLog.Message($"[RimPrison:test] gap analysis 4 M8: no guard {guard != null}, open ground {patch.IsValid} or holding for the check, not checked");
                    return;
                }
                Pawn arrival = Throwaway(map, r, spawn: false);
                GenSpawn.Spawn(arrival, patch, map);
                InmateGenerator.MakeInmate(arrival, null);
                r.Add(() => ShuttleRemovePawn(arrival));
                g.shuttle.AddArrival(arrival);
                var giver = new WorkGiver_EscortArrival();
                if (!guard.CanReach(arrival, PathEndMode.Touch, Danger.Deadly))
                {
                    RPLog.Message("[RimPrison:test] gap analysis 4 M8: the guard cannot reach the open ground at all, not checked");
                    return;
                }
                // Every door locked: the guard (with a key) reaches the arrival, the arrival does not reach holding.
                var modes = pm.Doors.Select(d => (d, d.mode)).ToList();
                r.Add(() =>
                {
                    foreach ((CompPrisonDoor d, PrisonDoorMode mode) in modes) d.mode = mode;
                    pm.ForceDoorUpdate();
                    map.reachability.ClearCache();
                });
                // (Locked, not staff only: an arrival may pass a staff-only gate on the way in, and a guard with a key passes a locked one.)
                CompStaff gsc = guard.GetComp<CompStaff>();
                bool key0 = gsc.passLocked;
                r.Add(() => gsc.passLocked = key0);
                gsc.passLocked = true;
                foreach ((CompPrisonDoor d, PrisonDoorMode _) in modes) d.mode = PrisonDoorMode.Locked;
                pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                bool arrivalReaches = RegimeActivityWorker.RandomReachableZoneCell(arrival, pm, RPDefOf.RP_Holding).IsValid;
                if (arrivalReaches)
                {
                    RPLog.Message("[RimPrison:test] gap analysis 4 M8: the arrival still reaches holding with every door locked (the way in is not by a door), not checked");
                    return;
                }
                if (giver.HasJobOnThing(guard, arrival)) Ga4Fail("M8: a guard was sent to meet an arrival who cannot reach holding (they would lead them to the locked gate and come back for them, for as long as it stays shut)");
            });
        }

        // ---- lows ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A crate that cannot be set down anywhere stays aboard, paid for, and goes back to the office at lift-off, instead of being lost; the shuttle is not held up for it.</summary>
        private static void CheckGa4CrateThatCannotBePlacedGoesBack(Map map)
        {
            Guarded("gap analysis 4 low: a crate that cannot be set down is not lost", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "crate that cannot be placed")) return;
                bool fails0 = ShuttleSystem.PlaceCrateFailsForTests;
                r.Add(() => ShuttleSystem.PlaceCrateFailsForTests = fails0);
                var before = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                r.Add(() =>
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        if (!before.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                const int Steel = 150, Cost = 300;
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = Steel, cost = Cost, arriveTick = ShNow - 1 });
                ShuttleSystem.PlaceCrateFailsForTests = true;
                if (!ShuttleSystem.CallCargo(g, map))
                {
                    Ga4Fail("low: the cargo run could not be called");
                    return;
                }
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.cargoDone, 3000);
                bool aboard = v.cargo.Sum(o => o.count) == Steel && v.cargo.Sum(o => o.cost) == Cost && v.cargoStacks == 0;
                bool notHeld = g.shuttle.visit == v && v.cargoDone && !ShuttleSystem.Unloading(v, v.shuttle);
                ShuttleDepartIn(v, 0);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                int back = g.supply.pending.Where(o => o.thing == ThingDefOf.Steel).Sum(o => o.count), backCost = g.supply.pending.Where(o => o.thing == ThingDefOf.Steel).Sum(o => o.cost);
                ShuttleSystem.PlaceCrateFailsForTests = false;
                if (!aboard || !notHeld) Ga4Fail($"low: with nowhere to set a crate down the order was changed (aboard {v.cargo.Sum(o => o.count)} of {Steel}, paid {v.cargo.Sum(o => o.cost)} of {Cost}, stacks {v.cargoStacks}) or the unloading was not given up (done {v.cargoDone})");
                if (back != Steel || backCost != Cost) Ga4Fail($"low: the crates that could not be set down are back in the office as {back} steel paid {backCost} (of {Steel} and {Cost}): paid cargo was lost");
            });
        }

        /// <summary>A lift-off at the cap with crates already set down still delivers them (the message, the contraband roll) and does not carry the list of what was set down into the next visit.</summary>
        private static void CheckGa4DepartureDeliversWhatWasSetDown(Map map)
        {
            Guarded("gap analysis 4 low: a lift-off at the cap finishes the cargo", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "cargo at the cap")) return;
                var before = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                r.Add(() =>
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        if (!before.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = ThingDefOf.Steel.stackLimit * 12, cost = 0, arriveTick = ShNow - 1 });
                if (!ShuttleSystem.CallCargo(g, map))
                {
                    Ga4Fail("low: the cargo run could not be called");
                    return;
                }
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.cargoStacks >= 2 && v.cargo.Count > 0, 6000);
                if (g.shuttle.visit != v || v.cargoStacks < 2 || v.cargoDone)
                {
                    Ga4Fail($"low: could not get a shuttle part-way through its crates ({v.Describe()})");
                    return;
                }
                int delivered = g.shuttle.unloadedThings.Count;
                ShuttleDepartIn(v, 1);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                if (delivered == 0 || g.shuttle.unloadedThings.Count != 0 || !v.cargoDone)
                    Ga4Fail($"low: a lift-off with crates left aboard left {g.shuttle.unloadedThings.Count} of the {delivered} things set down on the list for the next visit (done {v.cargoDone})");
            });
        }

        /// <summary>One of the colony's own who was held here is a free colonist again when released: nobody sends them to the shuttle.</summary>
        private static void CheckGa4ColonistExPrisonerIsNotRoutedAboard(Map map)
        {
            Guarded("gap analysis 4 low: a colonist is not routed aboard", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "colonist ex-prisoner")) return;
                Pawn colonist = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                g.shuttle.Register(colonist, OutboundKind.Released);
                if (g.shuttle.OutboundOf(colonist) != null) Ga4Fail("low: a free colonist was put on the way out to the shuttle");
                g.shuttle.outbound.Add(new OutboundEntry { pawn = colonist, kind = OutboundKind.Released, sinceTick = ShNow });
                r.Add(() => g.shuttle.Forget(colonist));
                g.shuttle.Prune(map);
                if (g.shuttle.OutboundOf(colonist) != null) Ga4Fail("low: the housekeeping left a free colonist on the way out");
            });
        }

        /// <summary>A transfer that walks out (there is no shuttle to take it) is sent to another facility all the same: not freed (no freed-prisoner goodwill, the release is not undone, no world pawn kept).</summary>
        private static void CheckGa4TransferWalkingOutIsNotFreed(Map map)
        {
            Guarded("gap analysis 4 low: a transfer walking out is not freed", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "transfer walking out")) return;
                Pawn p = Throwaway(map, r);
                ShuttleServed(p, r);
                g.shuttle.Register(p, OutboundKind.Transferred);
                bool released = p.guest is { Released: true };
                p.ExitMap(false, Rot4.Invalid);
                if (!released || p.Spawned || p.guest is not { Released: true } || g.shuttle.OutboundOf(p) != null)
                    Ga4Fail($"low: a transfer that left the map on foot was freed (released before {released}, after {p.guest?.Released}, still spawned {p.Spawned}): vanilla's exit sets a released prisoner free");
            });
        }

        /// <summary>The state takes back the passengers a shuttle could not unload when their level wants none (they would pile up, shuttle after shuttle).</summary>
        private static void CheckGa4CarriedOverWhoseLevelWantsNoneAreTakenBack(Map map)
        {
            Guarded("gap analysis 4 low: carried-over inmates of a level that wants none go back", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "carried over")) return;
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.First(x => Unlocks.IsUnlocked(x) && !x.wingOnly);
                Pawn carried = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
                if (Find.WorldPawns.Contains(carried)) Find.WorldPawns.RemovePawn(carried);
                Find.WorldPawns.PassToWorld(carried, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                g.shuttle.carriedOver.Add(carried);
                r.Add(() =>
                {
                    g.shuttle.carriedOver.Remove(carried);
                    if (!carried.Destroyed && !carried.Discarded && Find.WorldPawns.Contains(carried)) Find.WorldPawns.RemovePawn(carried);
                });
                g.intake.perDay[sec] = 0;
                IntakeSystem.GenerateRoster(g, map, out _, out _, 2);
                if (g.shuttle.carriedOver.Contains(carried)) Ga4Fail("low: a passenger carried over for a level that wants no new inmates is still waiting for a shuttle that will never take them");
            });
        }

        /// <summary>The wait for an escort and the ten hours to be inside are two clocks: the lift-off sends the unescorted on their own without moving the ten hours.</summary>
        private static void CheckGa4ArrivalClocksAreSeparate(Map map)
        {
            Guarded("gap analysis 4 low: the escort wait and the ten hours are separate clocks", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "arrival clocks")) return;
                IntVec3 patch = ShuttleOpenPatch(map, 1);
                if (!patch.IsValid)
                {
                    RPLog.Message("[RimPrison:test] gap analysis 4 low: no open ground for an arrival, not checked");
                    return;
                }
                Pawn p = Throwaway(map, r, spawn: false);
                GenSpawn.Spawn(p, patch, map);
                InmateGenerator.MakeInmate(p, null);
                r.Add(() => ShuttleRemovePawn(p));
                g.shuttle.AddArrival(p);
                ArrivalEntry e = g.shuttle.ArrivalOf(p);
                int since = e.sinceTick = ShNow - 1000;
                e.walkOnTick = ShNow + ShuttleSystem.EscortWaitTicks;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    Ga4Fail("low: the shuttle never landed for the arrival clocks check");
                    return;
                }
                ShuttleDepartIn(v, 0);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                if (g.shuttle.ArrivalOf(p) is not { } after)
                {
                    RPLog.Message("[RimPrison:test] gap analysis 4 low: the arrival went on its way during the lift-off, not checked");
                    return;
                }
                if (after.sinceTick != since || after.WalkOnAt > ShNow) Ga4Fail($"low: the lift-off moved the ten-hour clock ({since} -> {after.sinceTick}) or did not send the unescorted arrival on their own (walks on at {after.WalkOnAt}, now {ShNow})");
            });
        }

        /// <summary>The pad-blocked message names a blocker only on a pad that could take the shuttle: a blocker on a roofed pad is not the reason it waits.</summary>
        private static void CheckGa4PadBlockerIsOnAUsablePad(Map map)
        {
            Guarded("gap analysis 4 low: the blocker named is on a usable pad", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "pad blocker")) return;
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad == null)
                {
                    Ga4Fail("low: no pad");
                    return;
                }
                ShuttleSystem.Footprint(pad.Position, out CellRect rect);
                IntVec3 cell = rect.ExpandedBy(1).Cells.FirstOrDefault(c => c.InBounds(map) && !c.Roofed(map) && c.Standable(map));
                if (!cell.IsValid)
                {
                    RPLog.Message("[RimPrison:test] gap analysis 4 low: no cell round the pad to roof, not checked");
                    return;
                }
                Thing crate = ThingMaker.MakeThing(ThingDefOf.Steel);
                crate.stackCount = 5;
                GenSpawn.Spawn(crate, pad.Position, map);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                r.Add(() =>
                {
                    map.roofGrid.SetRoof(cell, null);
                    if (!crate.Destroyed) crate.Destroy(DestroyMode.Vanish);
                    g.shuttle.ForgetSite();
                });
                Thing named = ShuttleSystem.PadBlocker(map);
                map.roofGrid.SetRoof(cell, null);
                Thing namedClear = ShuttleSystem.PadBlocker(map);
                if (named != null) Ga4Fail($"low: the pad-blocked message would name {named.LabelShort} on a roofed pad, which the shuttle could not use anyway");
                if (namedClear != crate) Ga4Fail($"low: the blocker on a usable pad was {namedClear?.LabelShort ?? "nothing"}, not the crate on it");
            });
        }
    }
}
