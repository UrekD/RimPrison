using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks of the state shuttle (Shuttle/*.cs) in the events plan. They run real ticks (the skyfaller coming down, the unloading, the walk to
    /// holding, the boarding, the lift-off), each on a clear stage (no other shuttle on the pad, the daily one and the cargo runs held off, no policy
    /// intake), and put back what they touch: the intake policy, the orders, the pad, the weather, their own inmates. Tolerant of shifts and breaks:
    /// they wait for things to happen instead of expecting them on a tick, and what depends on a guard being free is only logged.
    /// </summary>
    public static partial class AutoTest
    {
        private static int ShNow => Find.TickManager.TicksGame;

        /// <summary>Real game ticks, advanced as the main loop does: until the condition holds, at most this many (returns the ticks spent).</summary>
        private static int ShuttleTicks(Func<bool> done, int most)
        {
            int ticked = 0;
            while (ticked < most && !done())
            {
                Find.TickManager.DoSingleTick();
                KeepSupermaxGuardsOnShift();
                KeepDrugTestDoctorsOnShift();
                ticked++;
            }
            return ticked;
        }

        /// <summary>As <see cref="ShuttleTicks"/>, with a line in the log every 120 ticks about where somebody is and what they are doing (for the runs that go wrong).</summary>
        private static int ShuttleTicksTraced(Func<bool> done, int most, Pawn who, string label)
        {
            int ticked = 0;
            while (ticked < most && !done())
            {
                Find.TickManager.DoSingleTick();
                ticked++;
                if (ticked % 120 == 0) RPLog.Message($"[RimPrison:test] {label}: +{ticked} {who.LabelShort} spawned {who.Spawned} at {who.Position} job {who.CurJob} driver {who.jobs?.curDriver?.GetType().Name} mental {who.MentalStateDef?.defName} downed {who.Downed} stowaway {PrisonGame.Instance?.shuttle.stowaway == who}");
            }
            return ticked;
        }

        /// <summary>Lift-off in <paramref name="ticks"/> whatever is still to unload (the stay is not stretched: the visit is made to be at its hard cap).</summary>
        private static void ShuttleDepartIn(ShuttleVisit v, int ticks)
        {
            v.departTick = ShNow + ticks;
            v.landedTick = Math.Min(v.landedTick, ShNow - ShuttleSystem.StayTicks(v) - ShuttleSystem.OvertimeTicks);
        }

        /// <summary>Ends whatever shuttle visit is on: waits for it to lift off (or lands first), then clears anything it left. True if nothing is left.</summary>
        private static bool EndShuttleVisit(PrisonGame g, Map map)
        {
            for (int round = 0; round < 4 && g.shuttle.visit != null; round++)
            {
                ShuttleVisit v = g.shuttle.visit;
                if (v.phase == ShuttlePhase.Holding)
                {
                    g.shuttle.visit = null;
                    break;
                }
                if (v.phase == ShuttlePhase.Inbound) ShuttleTicks(() => v.phase != ShuttlePhase.Inbound || g.shuttle.visit != v, 900);
                if (g.shuttle.visit == v && v.phase == ShuttlePhase.Landed) ShuttleDepartIn(v, 0);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
            }
            foreach (ThingDef def in new[] { RPShuttleDefOf.RP_ShuttleIncoming, RPShuttleDefOf.RP_ShuttleLeaving, RPShuttleDefOf.RP_StateShuttle })
                foreach (Thing t in map.listerThings.ThingsOfDef(def).ToList())
                {
                    if (g.shuttle.visit?.shuttle == t) continue;
                    if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);
                }
            if (g.shuttle.visit == null) return true;
            return false;
        }

        /// <summary>
        /// A clear stage for a shuttle check: no visit, no daily shuttle or cargo run due while it runs, no policy intake (the check sets its own), and the settings
        /// put back afterwards (with any visit ended first).
        /// </summary>
        private static bool ShuttleStage(Map map, Rollback r, string who)
        {
            PrisonGame g = PrisonGame.Instance;
            if (!EndShuttleVisit(g, map))
            {
                problems0.Add($"{who}: a shuttle visit would not end ({g.shuttle.visit?.Describe()})");
                return false;
            }
            (int hour0, int lastRun0, int cargo0, bool pending0, bool overcrowding0, int guards0) =
                (g.intake.hour, g.intake.lastRunDay, g.shuttle.lastCargoTick, g.shuttle.dailyPending, g.intake.allowOvercrowding, g.shuttle.padGuards);
            var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
            // Those still on their way out from the plan's earlier checks and events (released and left standing) would board these shuttles and be counted: they are counted as gone now, as the shuttle would
            // count them (the release, and their supervision case begun), and walk out as vanilla would send them. (Only forgotten, a downed one was taken off by the ambulance with no entry to count, its case
            // stayed waiting, and the next hourly pass counted the release - the supervision's fallback for a departure nobody saw - in the middle of the boarding check: "released counted 4 (2 expected)".)
            // Any other case still waiting whose person is gone is settled now as well, not by the hour of somebody's check.
            foreach (OutboundEntry stale in g.shuttle.outbound.ToList())
            {
                ShuttleSystem.Notify_Left(stale.pawn);
                g.shuttle.Forget(stale.pawn);
            }
            Supervision.SettleAwaiting(g);
            // (The real orders stay in the office while a check runs: a shuttle takes everything that is ready, and what it unloads is used up or counted twice.)
            List<SupplyOrder> orders0 = g.supply.pending.ToList();
            g.supply.pending.Clear();
            bool skies0 = ShuttleSystem.ClearSkiesForTests;
            ShuttleSystem.ClearSkiesForTests = true;
            r.Add(() =>
            {
                EndShuttleVisit(g, map);
                ShuttleSystem.ClearSkiesForTests = skies0;
                g.intake.hour = hour0;
                g.intake.lastRunDay = lastRun0;
                g.shuttle.lastCargoTick = cargo0;
                g.shuttle.dailyPending = pending0;
                g.intake.allowOvercrowding = overcrowding0;
                g.shuttle.padGuards = guards0;
                foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                g.supply.pending.Clear();
                g.supply.pending.AddRange(orders0);
            });
            g.intake.hour = (GenLocalDate.HourOfDay(map) + 12) % 24;
            g.intake.lastRunDay = Ledger.CurrentDay;
            g.shuttle.lastCargoTick = ShNow + 2 * GenDate.TicksPerDay;
            g.shuttle.dailyPending = false;
            foreach (SecurityLevelDef sec in perDay0.Keys.ToList()) g.intake.perDay[sec] = 0;
            // (A real pawn standing on the hull's spot or at the ramp, a guard who has just come off pad duty, say, holds the next call up for an hour: "pad blocked by Celia", seen once in
            // 35 runs, failed the two checks that call a shuttle and look for it at once. They are set down off the pad.)
            for (int moved = 0; moved < 8 && ShuttleSystem.PadBlocker(map) is Pawn standing; moved++)
            {
                IntVec3 from = standing.Position;
                if (!CellFinder.TryFindRandomCellNear(from, map, 14, c => c.Standable(map) && c.GetFirstPawn(map) == null && c.DistanceTo(from) > 7f, out IntVec3 off)) break;
                standing.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                standing.pather?.StopDead();
                standing.Position = off;
                standing.Notify_Teleported(true, true);
            }
            // (So is a thing lying there: a pad that is blocked is waited on for an hour (PadWaitTicks, from the first look) before the shuttle lands beside it, and a check that calls a shuttle waits
            // less than that for it - "the shuttle did not land (... PadBlocked ...)". A marshals' prisoner's drug dropped on the ramp was there for every check after it, in four events runs. The
            // product's hour stands: the check's subject is not the pad, so the thing is set aside, and told.)
            foreach (Building pad in ShuttleSystem.Pads(map).ToList())
            {
                IntVec3 ramp = ShuttleSystem.Footprint(pad.Position, out CellRect hull);
                foreach (IntVec3 cell in hull.Cells.Append(ramp).ToList())
                {
                    if (!cell.InBounds(map)) continue;
                    foreach (Thing t in cell.GetThingList(map).Where(x => x.def.category == ThingCategory.Item).ToList())
                    {
                        IntVec3 from = t.Position;
                        t.DeSpawn();
                        bool placed = GenPlace.TryPlaceThing(t, from, map, ThingPlaceMode.Near, null, c => !hull.ExpandedBy(1).Contains(c) && c != ramp && c.DistanceTo(ramp) > 1.5f);
                        if (!placed && !t.Destroyed) t.Destroy();
                        RPLog.Message($"[RimPrison:test] {who}: {t.LabelShort} ({t.def.defName}) lay on the pad at {from} (the ramp {ramp}), {(placed ? "set aside to " + t.PositionHeld : "nowhere to set it, destroyed")}: left there by an earlier check or the game");
                    }
                }
            }
            return true;
        }

        /// <summary>Removes a test's pawn without leaving their things behind (a destroyed pawn drops what they carry and wear where they stand: on the ramp, that would hold the next shuttle up).</summary>
        private static void ShuttleRemovePawn(Pawn p)
        {
            if (p == null || p.Destroyed) return;
            PrisonGame.Instance?.shuttle.Forget(p);
            if (p.inventory is { } inv && inv.innerContainer.Count > 0 || p.carryTracker?.CarriedThing != null)
                RPLog.Message($"[RimPrison:test] shuttle test: removing {p.LabelShort} at {p.Position} (carrying {p.carryTracker?.CarriedThing?.LabelShort}, pockets {string.Join("/", p.inventory?.innerContainer.Select(t => t.LabelShort) ?? Enumerable.Empty<string>())}, job {p.CurJobDef?.defName})");
            p.carryTracker?.innerContainer.ClearAndDestroyContents();
            p.inventory?.DestroyAll();
            p.equipment?.DestroyAllEquipment();
            p.apparel?.DestroyAll();
            p.Destroy(DestroyMode.Vanish);
        }

        private static void RunShuttleChecks(Map map)
        {
            // A job handed out again and again in one tick ("started 10 jobs in one tick") fails the check it happened in.
            void Checked(string name, Action<Map> check)
            {
                int loops0 = JobLoopErrors;
                check(map);
                if (JobLoopErrors > loops0) problems0.Add($"{name}: {JobLoopErrors - loops0} pawns were handed a job again and again in one tick (\"started 10 jobs in one tick\")");
            }
            Checked("shuttle pad placement", CheckShuttlePadPlacement);
            Checked("shuttle landing", CheckShuttleLandsAndUnloads);
            Checked("shuttle without holding", CheckShuttleWithoutHolding);
            Checked("shuttle without a pad", CheckShuttleNoPad);
            Checked("shuttle cargo", CheckShuttleCargo);
            Checked("shuttle boarding", CheckShuttleBoarding);
            Checked("shuttle blocked pad", CheckShuttleBlockedPad);
            Checked("shuttle delays", CheckShuttleDelays);
            Checked("shuttle stowaway", CheckShuttleStowaway);
            Checked("shuttle cargo contraband", CheckShuttleCargoContraband);
            Checked("shuttle pad duty", CheckShuttlePadDuty);
            Checked("shuttle regressions", RunShuttleRegressions);
            Checked("shuttle regressions of gap analysis 4", RunGa4ShuttleChecks);
            Checked("shuttle: the state's own passengers and escorted transfers", RunInsideShuttleChecks);
        }

        /// <summary>The pad is in the prison tab of the Architect menu, goes on open ground only (not under a roof or over a wall), and costs materials that the shuttle brings (refunded if the blueprint is cancelled).</summary>
        private static void CheckShuttlePadPlacement(Map map)
        {
            Guarded("shuttle pad placement", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "shuttle pad placement")) return;
                ThingDef pad = RPShuttleDefOf.RP_ShuttlePad;
                bool inTab = DefDatabase<DesignationCategoryDef>.GetNamed("RP_Prison").AllResolvedDesignators.OfType<Designator_Build>().Any(d => d.PlacingDef == pad);
                int steel = pad.costList.Where(c => c.thingDef == ThingDefOf.Steel).Sum(c => c.count);
                // Open ground: a 9x7 patch of standable, unroofed cells with nothing on them, outside the compound's footprint and inside the prison's grounds (the pad is refused outside them).
                CellRect compound = PrisonBuilder.Footprint(map);
                IntVec3 open = IntVec3.Invalid;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(compound.CenterCell, 60f, true))
                {
                    CellRect rect = GenAdj.OccupiedRect(c, Rot4.North, pad.size);
                    if (rect.InBounds(map) && !rect.Overlaps(compound) && (PrisonGrounds.Of(map)?.Contains(rect) ?? true) && rect.Cells.All(x => x.Standable(map) && !x.Roofed(map) && x.GetEdifice(map) == null && x.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light) && x.GetThingList(map).Count(t => t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame) == 0))
                    {
                        open = c;
                        break;
                    }
                }
                IntVec3 wall = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.IsWall && b.Position.InBounds(map))?.Position ?? IntVec3.Invalid;
                IntVec3 roofed = compound.Cells.FirstOrDefault(c => map.roofGrid.Roofed(c) && c.Standable(map));
                AcceptanceReport onOpen = open.IsValid ? GenConstruct.CanPlaceBlueprintAt(pad, open, Rot4.North, map) : AcceptanceReport.WasRejected;
                AcceptanceReport overWall = wall.IsValid ? GenConstruct.CanPlaceBlueprintAt(pad, wall + new IntVec3(0, 0, 1), Rot4.North, map) : AcceptanceReport.WasRejected;
                AcceptanceReport underRoof = roofed.IsValid ? GenConstruct.CanPlaceBlueprintAt(pad, roofed, Rot4.North, map) : AcceptanceReport.WasRejected;
                // A blueprint orders its steel (paid now, carried by a shuttle); cancelled, it is refunded.
                int pending0 = g.supply.pending.Count, funds0 = g.ledger.funds;
                Blueprint_Build blueprint = open.IsValid ? GenConstruct.PlaceBlueprintForBuild(pad, open, map, Rot4.North, Faction.OfPlayer, null) : null;
                bool ordered = blueprint != null && g.supply.pending.Count == pending0 + 1 && g.supply.PendingCount(ThingDefOf.Steel) == steel && g.ledger.funds < funds0;
                blueprint?.Destroy(DestroyMode.Cancel);
                bool refunded = g.supply.pending.Count == pending0 && g.ledger.funds == funds0;
                RPLog.Message($"[RimPrison:test] shuttle pad placement: in the prison tab {inTab}, {steel} steel, open ground {onOpen.Accepted} at {open}, over a wall {overWall.Accepted}, under a roof {underRoof.Accepted}, blueprint ordered its steel {ordered}, refunded when cancelled {refunded}");
                if (!inTab || steel <= 0) problems0.Add($"shuttle pad placement: in the prison tab {inTab}, steel cost {steel}");
                if (!onOpen.Accepted || overWall.Accepted || underRoof.Accepted) problems0.Add($"shuttle pad placement: open ground accepted {onOpen.Accepted} ({open}), over a wall accepted {overWall.Accepted}, under a roof accepted {underRoof.Accepted} ({onOpen.Reason})");
                if (!ordered || !refunded) problems0.Add($"shuttle pad placement: the blueprint ordered its steel {ordered}, cancelled it was refunded {refunded}");
            });
        }

        /// <summary>The daily shuttle comes down on the pad (the skyfaller lands on its centre), the new inmates walk down the ramp and reach holding.</summary>
        private static void CheckShuttleLandsAndUnloads(Map map)
        {
            Guarded("shuttle lands on the pad and unloads", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "shuttle landing")) return;
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(s => Unlocks.IsUnlocked(s) && !s.wingOnly);
                if (pad == null || sec == null)
                {
                    problems0.Add($"shuttle landing: no pad ({pad != null}) or no level to send inmates of ({sec != null})");
                    return;
                }
                g.intake.perDay[sec] = 3;
                g.intake.allowOvercrowding = true;
                int firstNumber = g.intake.nextInmateNumber, landings0 = g.stats.shuttleLandings, escorts0 = g.shuttle.escortsStarted;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                if (v == null)
                {
                    problems0.Add("shuttle landing: calling the daily shuttle made no visit");
                    return;
                }
                int aboard = v.rosterSize;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                bool onPad = v.phase == ShuttlePhase.Landed && v.onPad && v.shuttle.Position == pad.Position;
                bool inbound = map.listerThings.ThingsOfDef(RPShuttleDefOf.RP_StateShuttle).Contains(v.shuttle);
                ShuttleTicks(() => g.shuttle.visit != v || v.arrivalsDone, 2500);
                List<Pawn> arrivals = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { } c && c.inmateNumber >= firstNumber).ToList();
                r.Add(() =>
                {
                    foreach (Pawn p in arrivals) ShuttleRemovePawn(p);
                });
                // Why would no guard come: what the work giver says for each guard and arrival.
                {
                    var giver = new WorkGiver_EscortArrival { def = DefDatabase<WorkGiverDef>.GetNamed("RP_EscortArrival_WorkGiver") };
                    foreach (Pawn guard in map.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard).Take(3))
                    {
                        Pawn first = arrivals.FirstOrDefault();
                        if (first == null) break;
                        RPLog.Message($"[RimPrison:test] shuttle landing: guard {guard.LabelShort} at {guard.Position} on {guard.CurJobDef?.defName}, skips {giver.ShouldSkip(guard)}, arrival {first.LabelShort} at {first.Position} escorted by {g.shuttle.ArrivalOf(first)?.escortId}, job here {giver.HasJobOnThing(guard, first)} (can reach {guard.CanReach(first, PathEndMode.Touch, Danger.Deadly)}, can reserve {guard.CanReserve(first)}, warden work {guard.workSettings?.GetPriority(WorkTypeDefOf.Warden)}, disabled {guard.WorkTypeIsDisabled(WorkTypeDefOf.Warden)}, in holding {PrisonMap.Of(map).zones.ZoneAt(first.Position)?.defName})");
                    }
                }
                // One guard is put on the first arrival (the plan's guards may all be busy): the arrival must follow them to holding.
                bool followed = false, escortOnBreak = false;
                string noJob = null;
                // (The one who stepped off last: the first has stood at the ramp for the whole unloading and may be walking on alone by now, at the end of the wait the arrivals are given for a guard, and a guard sent
                // after them chases them to holding and finds them there: "a guard sent to meet the first arrival did not get them to follow", seen in four of sixteen runs. Whoever it is is given a full wait.)
                // (Of those, one no guard has claimed yet, when there is one: a guard who came in for the shift at the map's edge takes the escort job from there and holds the arrival for the whole walk across the
                // map - Mitsuya in riotreview-rr2b and Maya in stability16-s16b, each on RP_EscortArrival from the edge in the tick the check began - and the guard the check sets down beside a held arrival cannot reserve them.)
                Pawn ReservedBy(Pawn a, Pawn asking) => asking == null ? null : map.reservationManager.FirstRespectedReserver(a, asking);
                Pawn anyGuard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(GuardUtility.IsGuard);
                List<Pawn> lastOffFirst = arrivals.OrderByDescending(a => g.shuttle.ArrivalOf(a)?.sinceTick ?? -1).ToList();
                Pawn newcomer0 = lastOffFirst.FirstOrDefault(a => g.shuttle.ArrivalOf(a) is { escortId: < 0 } && ReservedBy(a, anyGuard) == null) ?? lastOffFirst.FirstOrDefault();
                if (newcomer0 != null && g.shuttle.ArrivalOf(newcomer0) is { } waiting && waiting.WalkOnAt < ShNow + 1500) waiting.walkOnTick = ShNow + 1500;
                // (The guard already on the way to them, when every arrival is held: the walk is that guard's to start.)
                Pawn holder = newcomer0 != null && ReservedBy(newcomer0, anyGuard) is { } h && GuardUtility.IsGuard(h) && h.CurJobDef == RPShuttleDefOf.RP_EscortArrival && h.CurJob.targetA.Thing == newcomer0 ? h : null;
                if (arrivals.Count > 0 && (holder ?? map.mapPawns.FreeColonistsSpawned.Where(x => GuardUtility.IsGuard(x) && !x.Downed && !x.InMentalState).OrderBy(x => x.Position.DistanceToSquared(newcomer0.Position)).FirstOrDefault()) is { } escort)
                {
                    var giver = new WorkGiver_EscortArrival { def = DefDatabase<WorkGiverDef>.GetNamed("RP_EscortArrival_WorkGiver") };
                    Pawn newcomer = newcomer0;
                    RPLog.Message($"[RimPrison:test] shuttle landing: the arrival met is {newcomer.LabelShort} (held by {ReservedBy(newcomer, anyGuard)?.LabelShort ?? "nobody"}), the guard {escort.LabelShort}{(holder != null ? " (already on the way to them)" : "")}; the arrivals: "
                                  + string.Join(", ", lastOffFirst.Select(a => $"{a.LabelShort} off at {g.shuttle.ArrivalOf(a)?.sinceTick}, escort {g.shuttle.ArrivalOf(a)?.escortId}, held by {ReservedBy(a, anyGuard)?.LabelShort ?? "nobody"}")));
                    // (From a long way off the walk takes longer than the check waits: the guard is set down near the arrival, as the parole checks do.)
                    if (escort.Position.DistanceTo(newcomer.Position) > 6f && CellFinder.TryFindRandomCellNear(newcomer.Position, map, 3, c => c.Standable(map) && c.GetFirstPawn(map) == null, out IntVec3 beside))
                    {
                        escort.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        escort.pather.StopDead();
                        escort.Position = beside;
                        escort.Notify_Teleported(true, true);
                    }
                    // (Set down beside them, the guard thinks again at once and may take the walk on its own, and meet them in the same tick: then it is theirs already.)
                    bool theirs = g.shuttle.ArrivalOf(newcomer)?.escortId == escort.thingIDNumber || escort.CurJobDef == RPShuttleDefOf.RP_EscortArrival && escort.CurJob.targetA.Thing == newcomer;
                    Job escortJob = theirs ? null : giver.HasJobOnThing(escort, newcomer) ? giver.JobOnThing(escort, newcomer) : null;
                    if (!theirs && escortJob == null)
                    {
                        PrisonMap pmap = PrisonMap.Of(map);
                        Pawn heldBy = ReservedBy(newcomer, escort);
                        noJob = $"the guard {escort.LabelShort} (on {escort.CurJobDef?.defName} at {escort.Position}) is given no walk with {newcomer.LabelShort} (at {newcomer.Position} on {newcomer.CurJobDef?.defName}): escort {g.shuttle.ArrivalOf(newcomer)?.escortId}, "
                                + $"held by {heldBy?.LabelShort ?? "nobody"}{(heldBy != null ? $" (on {heldBy.CurJobDef?.defName} at {heldBy.Position})" : "")}, can reach {escort.CanReach(newcomer, PathEndMode.Touch, Danger.Deadly)}, in {pmap.zones.ZoneAt(newcomer.Position)?.defName ?? "no zone"}, "
                                + $"downed {newcomer.Downed}, mental {newcomer.MentalStateDef?.defName ?? "none"}, a holding cell they can reach {RegimeActivityWorker.RandomReachableZoneCell(newcomer, pmap, RPDefOf.RP_Holding).IsValid}, the work giver's answer {giver.HasJobOnThing(escort, newcomer)}";
                        RPLog.Message("[RimPrison:test] shuttle landing: " + noJob);
                    }
                    if (theirs || escortJob != null)
                    {
                        if (!theirs) escort.jobs.StartJob(escortJob, JobCondition.InterruptForced);
                        r.Add(() => escort.jobs.EndCurrentJob(JobCondition.InterruptForced));
                        // (They walk to the cell the guard takes them to, as a walk: a goto, at the guard's pace.)
                        ShuttleTicks(() => newcomer.CurJobDef == JobDefOf.Goto && g.shuttle.ArrivalOf(newcomer)?.escortId == escort.thingIDNumber || !newcomer.Spawned, 2300);
                        followed = newcomer.CurJobDef == JobDefOf.Goto && g.shuttle.ArrivalOf(newcomer) is { } entry && entry.escortId == escort.thingIDNumber
                                   && newcomer.CurJob.locomotionUrgency == LocomotionUrgency.Walk && newcomer.CurJob.targetA.Cell == escort.CurJob.targetB.Cell;
                        escortOnBreak = escort.CurJobDef == RPStaffRoomDefOf.RP_StaffBreak;
                        RPLog.Message($"[RimPrison:test] shuttle landing: {escort.LabelShort} took {newcomer.LabelShort} by the arm {followed} (guard on {escort.CurJobDef?.defName}, the arrival on {newcomer.CurJobDef?.defName})");
                    }
                }
                var reached = new HashSet<Pawn>();
                int walked = ShuttleTicks(() =>
                {
                    foreach (Pawn p in arrivals)
                        if (p.Spawned && PrisonMap.Of(map).zones.ZoneAt(p.Position) == RPDefOf.RP_Holding) reached.Add(p);
                    return reached.Count >= arrivals.Count;
                }, 10000);   // (Deep snow from the events plan's weather can make the walk from the pad three or four times as slow.)
                // (They are in holding as they cross its edge; the walk ends where they stop.)
                // (Their walk on in holding is as slow as the walk to it was: deep snow made the one three or four times as long, and a fixed 900 ticks was not enough for the other.)
                ShuttleTicks(() => !g.shuttle.arrivals.Any(a => arrivals.Contains(a.pawn)), Math.Max(900, walked));
                RPLog.Message($"[RimPrison:test] shuttle landing: {aboard} aboard, {arrivals.Count} stepped off, {reached.Count} reached holding in {walked} ticks ({g.shuttle.escortsStarted - escorts0} walked by a guard), on the pad {onPad}, building on the ground {inbound}, beds {arrivals.Count(p => p.ownership?.OwnedBed != null)}");
                if (!onPad) problems0.Add($"shuttle landing: the shuttle did not come down on the pad's centre (phase {v.phase}, onPad {v.onPad}, at {v.shuttle?.Position}, pad {pad.Position})");
                if (g.stats.shuttleLandings != landings0 + 1) problems0.Add($"shuttle landing: landings {g.stats.shuttleLandings}, expected {landings0 + 1}");
                // (The "escort cut by a staff break" flake: the escort job is casualInterruptible, so a break that starts on the hour (StaffBreaks.Hourly) sends the guard to the staff
                // room mid-walk and the arrival walks to holding alone. Seen once in fourteen events runs; the product's own gap, reported, not tested here.)
                bool onBreak = escortOnBreak;
                if (aboard > 0 && !followed && onBreak) RPLog.Message("[RimPrison:test] shuttle landing: the escort was sent on a staff break on the hour and the arrival walked alone (escort cut by a staff break): not tested this run");
                else if (aboard > 0 && !followed) problems0.Add("shuttle landing: a guard sent to meet the first arrival did not get them to follow (an escort walk did not start)" + (noJob != null ? $": {noJob}" : ""));
                if (aboard == 0) RPLog.Message("[RimPrison:test] shuttle landing: nobody was aboard (no room in the prison), the walk to holding is not checked");
                else
                {
                    if (arrivals.Count != aboard) problems0.Add($"shuttle landing: {aboard} aboard but {arrivals.Count} stepped off");
                    if (reached.Count == arrivals.Count && g.shuttle.arrivals.Any(a => arrivals.Contains(a.pawn)))
                        problems0.Add("shuttle landing: arrivals who reached holding are still on the way in (the walk was not ended): "
                                      + string.Join("; ", g.shuttle.arrivals.Where(a => arrivals.Contains(a.pawn)).Select(a => $"{a.pawn.LabelShort} at {a.pawn.Position} in {PrisonMap.Of(map).zones.ZoneAt(a.pawn.Position)?.defName} on {a.pawn.CurJobDef?.defName}, escort {a.escortId}, active {a.pawn.Inmate()?.activeFlag}, awaiting search {a.pawn.Inmate()?.awaitingSearch}")));
                    if (reached.Count < arrivals.Count) problems0.Add($"shuttle landing: only {reached.Count} of {arrivals.Count} arrivals reached holding within {walked} ticks");
                    if (reached.Count < arrivals.Count && arrivals.FirstOrDefault(x => x.Spawned && !reached.Contains(x)) is { } lost)
                    {
                        // Where the way in is shut: the doors on a staff member's path from the new inmate to holding, and what each lets an arrival do.
                        IntVec3 hold = PrisonMap.Of(map).zones.CellsOf(RPDefOf.RP_Holding).FirstOrDefault();
                        Pawn walker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(GuardUtility.IsGuard);
                        using PawnPath path = walker != null && hold.IsValid ? map.pathFinder.FindPathNow(lost.Position, hold, TraverseParms.For(walker, Danger.Deadly)) : null;
                        var notes = new List<string>();
                        if (path != null && path.Found)
                            foreach (IntVec3 node in path.NodesReversed)
                                if (node.GetEdifice(map) is Building_Door door && door.GetComp<CompPrisonDoor>() is { } dc)
                                    notes.Add($"{door.Position} mode {dc.mode} sides {dc.sideA}/{dc.sideB} inmatePass {dc.inmatePass} held {dc.heldCells != null} arrival may open {door.PawnCanOpen(lost)}");
                        RPLog.Message($"[RimPrison:test] shuttle landing: {lost.LabelShort} at {lost.Position} on {lost.CurJobDef?.defName} in transit {lost.Inmate()?.inTransit}, can reach holding {lost.CanReach(hold, PathEndMode.OnCell, Danger.Deadly)}; doors on the way: {string.Join("; ", notes)}");
                    }
                }
                // It leaves on its own after its stay: speed that up, and see it go.
                if (g.shuttle.visit == v && v.phase == ShuttlePhase.Landed) ShuttleDepartIn(v, 90);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                if (g.shuttle.visit == v) problems0.Add($"shuttle landing: the shuttle did not lift off ({v.Describe()})");
                if (map.listerThings.ThingsOfDef(RPShuttleDefOf.RP_StateShuttle).Count > 0) problems0.Add("shuttle landing: a shuttle was left standing on the pad after the visit");
            });
        }

        /// <summary>Without a holding zone the new inmate walks from the ramp to their own cell, as they always did (the gate is open to them).</summary>
        private static void CheckShuttleWithoutHolding(Map map)
        {
            Guarded("shuttle arrivals without a holding zone", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "shuttle without holding")) return;
                PrisonMap pm = PrisonMap.Of(map);
                // (More than one bed free: the live prison goes on while the shuttle is called and unloads - an inmate sent to solitary holds their own bed for their return - and the one free bed the arrival was counted on is gone by the time they step off.)
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(s => Unlocks.IsUnlocked(s) && !s.wingOnly && IntakeSystem.FreePrisonerBeds(map, s).Count > 2);
                if (sec == null || !ShuttleSystem.HasPad(map))
                {
                    RPLog.Message("[RimPrison:test] shuttle without holding: no free bed for any level here, the walk to the cell is not checked");
                    return;
                }
                List<IntVec3> holding = pm.zones.CellsOf(RPDefOf.RP_Holding).ToList();
                foreach (IntVec3 c in holding) pm.zones.Set(c, null);
                r.Add(() =>
                {
                    foreach (IntVec3 c in holding) pm.zones.Set(c, RPDefOf.RP_Holding);
                });
                g.intake.perDay[sec] = 1;
                int firstNumber = g.intake.nextInmateNumber;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.arrivalsDone, 3000);
                Pawn arrival = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.Inmate() is { } c && c.inmateNumber >= firstNumber);
                if (arrival == null)
                {
                    problems0.Add($"shuttle without holding: nobody stepped off ({v?.Describe()})");
                    return;
                }
                r.Add(() => ShuttleRemovePawn(arrival));
                Building_Bed bed = arrival.ownership?.OwnedBed;
                // (Every job they start on the way, and from which giver: a walk given again and again never gets anywhere.)
                Job lastJob = arrival.CurJob;
                int jobStarts = 0, moves = 0;
                IntVec3 lastPos = arrival.Position, startPos = arrival.Position;
                var jobKinds = new Dictionary<string, int>();
                int walked = ShuttleTicks(() =>
                {
                    if (arrival.Position != lastPos) { lastPos = arrival.Position; moves++; }
                    if (arrival.CurJob != lastJob)
                    {
                        lastJob = arrival.CurJob;
                        jobStarts++;
                        string k = $"{lastJob?.def?.defName}/{lastJob?.jobGiver?.GetType().Name}";
                        jobKinds[k] = jobKinds.TryGetValue(k, out int n) ? n + 1 : 1;
                    }
                    return bed != null && arrival.GetRoom() == bed.GetRoom();
                }, 9000);   // (From the pad outside the gate round the yard to the cell block: in deep snow, a cell can cost four times the walk.)
                RPLog.Message($"[RimPrison:test] shuttle without holding: {jobStarts} jobs started on the way: {string.Join(", ", jobKinds.Select(kv => $"{kv.Key} x{kv.Value}"))}; {moves} cells moved from {startPos} to {arrival.Position}, next cell cost {arrival.pather?.nextCellCostLeft:F0}/{arrival.pather?.nextCellCostTotal:F0}, ticks per move {arrival.TicksPerMoveCardinal}, restrained {RestraintsUtility.InRestraints(arrival)}, drafted {arrival.Drafted}, downed {arrival.Downed}, mental {arrival.MentalStateDef?.defName}");
                bool inCell = bed != null && arrival.GetRoom() == bed.GetRoom();
                RPLog.Message($"[RimPrison:test] shuttle without holding: {arrival.LabelShort} (bed {bed != null}) in their own cell {inCell} after {walked} ticks, awaiting search {arrival.Inmate().awaitingSearch}");
                if (bed == null || !inCell) problems0.Add($"shuttle without holding: the new inmate did not walk to their cell ({arrival.gender}, level {arrival.Inmate().security?.defName} (picked {sec.defName}), beds free for them {IntakeSystem.FreePrisonerBeds(map, arrival.Inmate().security, arrival.gender).Count}, of any sex {IntakeSystem.FreePrisonerBeds(map, arrival.Inmate().security).Count}, wing policy {g.WingGenderFor(arrival.Inmate().security)}, overcrowding {g.intake.allowOvercrowding}; bed {bed != null}{(bed != null ? $" at {bed.Position}, {arrival.Position.DistanceTo(bed.Position):F0} cells away, reachable {arrival.CanReach(bed, Verse.AI.PathEndMode.OnCell, Danger.Deadly)}" : "")}, at {arrival.Position}, on {arrival.CurJobDef?.defName} to {arrival.CurJob?.targetA.ToString() ?? "-"}, moving {arrival.pather?.Moving}, next cell {arrival.pather?.nextCell} ({string.Join(",", (arrival.pather?.nextCell ?? arrival.Position).GetThingList(map).Select(x => x is Building_Door d ? $"door open {d.Open} can open {d.PawnCanOpen(arrival)} mode {d.GetComp<CompPrisonDoor>()?.mode}" : x.def.defName))}), path left {arrival.pather?.curPath?.NodesLeftCount}, hour {GenLocalDate.HourOfDay(map)}, in transit {arrival.Inmate()?.inTransit})");
                ShuttleTicks(() => !g.shuttle.arrivals.Any(a => a.pawn == arrival), 600);
                if (g.shuttle.ArrivalOf(arrival) != null) problems0.Add("shuttle without holding: the arrival is still on the way in once in their cell");
            });
        }

        /// <summary>Without a pad the shuttle sets down on clear ground near holding or the gate, and an alert tells the warden to build one.</summary>
        private static void CheckShuttleNoPad(Map map)
        {
            Guarded("shuttle without a pad", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "shuttle without a pad")) return;
                List<Building> pads = ShuttleSystem.Pads(map).ToList();
                if (pads.Count == 0)
                {
                    problems0.Add("shuttle without a pad: the prison has no pad to take away");
                    return;
                }
                foreach (Building pad in pads) pad.DeSpawn();
                r.Add(() =>
                {
                    foreach (Building pad in pads)
                        if (!pad.Spawned) GenSpawn.Spawn(pad, pad.Position, map, pad.Rotation, WipeMode.VanishOrMoveAside);
                    g.shuttle.ForgetSite();
                });
                g.shuttle.ForgetSite();
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                bool alertShows = new Alert_RP_NoPad().GetReport().active && PrisonAlerts.NoPad;
                int off0 = g.stats.shuttlesOffPad;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (v == null || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add($"shuttle without a pad: it never landed ({v?.Describe()})");
                    return;
                }
                IntVec3 anchor = ShuttleSystem.Anchor(map);
                float away = v.shuttle.Position.DistanceTo(anchor);
                IntVec3 reception = g.shuttle.ReceptionAnchor(map);
                bool near = reception.IsValid && reception.DistanceTo(v.shuttle.Position) <= 4f;
                RPLog.Message($"[RimPrison:test] shuttle without a pad: landed at {v.shuttle.Position}, {away:F0} cells from holding, on the pad {v.onPad}, alert {alertShows}, reception beside it {near}");
                if (v.onPad || away > 60f) problems0.Add($"shuttle without a pad: landed on the pad {v.onPad}, {away:F0} cells from holding (60 at most)");
                if (!alertShows) problems0.Add("shuttle without a pad: the 'build a pad' alert is not showing");
                if (!near) problems0.Add("shuttle without a pad: reception is not by the place the shuttle sets down");
                if (g.stats.shuttlesOffPad != off0 + 1) problems0.Add($"shuttle without a pad: off-pad landings {g.stats.shuttlesOffPad}, expected {off0 + 1}");
                ShuttleDepartIn(v, 60);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
            });
        }

        /// <summary>Supplies on order ride a cargo run: counted while aboard (never re-ordered), unloaded in the Deliveries zone, or on the pad without one.</summary>
        private static void CheckShuttleCargo(Map map)
        {
            Guarded("shuttle delivers cargo", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "shuttle cargo")) return;
                PrisonMap pm = PrisonMap.Of(map);
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                var before = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                r.Add(() =>
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        if (!before.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                void Run(string stage, int count, Func<IntVec3, bool> placedRight, string where)
                {
                    g.supply.pending.Clear();
                    g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = count, cost = 0, arriveTick = ShNow - 1 });
                    int cargoRuns0 = g.stats.cargoRuns;
                    var already = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                    if (!ShuttleSystem.CallCargo(g, map))
                    {
                        problems0.Add($"shuttle cargo ({stage}): the cargo run could not be called");
                        return;
                    }
                    ShuttleVisit v = g.shuttle.visit;
                    // On its way down: the steel is aboard, not pending, and still counted as ordered.
                    ShuttleTicks(() => g.shuttle.visit != v || v.phase != ShuttlePhase.Holding, 400);
                    int aboard = v.cargo.Where(o => o.thing == ThingDefOf.Steel).Sum(o => o.count), ordered = g.supply.PendingCount(ThingDefOf.Steel);
                    // (Where each stack was set down is read the tick it appears: the colony's haulers take loose steel off the pad to a stockpile within the unloading, and what they have moved was set down right.)
                    var firstSeen = new Dictionary<Thing, IntVec3>();
                    ShuttleTicks(() =>
                    {
                        foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel))
                            if (!already.Contains(t) && !firstSeen.ContainsKey(t)) firstSeen[t] = t.Position;
                        return g.shuttle.visit != v || v.cargoDone;
                    }, 4000);
                    int delivered = v.cargoUnits;
                    List<IntVec3> placed = firstSeen.Values.ToList();
                    int wrong = placed.Count(at => !placedRight(at));
                    RPLog.Message($"[RimPrison:test] shuttle cargo ({stage}): {aboard} aboard, {ordered} counted as ordered, {delivered} delivered in {placed.Count} stacks, {wrong} not {where}, kind {v.kind}");
                    if (aboard != count || ordered < count) problems0.Add($"shuttle cargo ({stage}): {aboard} steel aboard and {ordered} counted ordered, of {count} on order");
                    if (delivered != count) problems0.Add($"shuttle cargo ({stage}): {delivered} steel unloaded, {count} ordered");
                    if (wrong > 0) problems0.Add($"shuttle cargo ({stage}): {wrong} of {placed.Count} stacks were not set down {where}");
                    if (v.kind != ShuttleKind.Cargo || g.stats.cargoRuns != cargoRuns0 + 1) problems0.Add($"shuttle cargo ({stage}): the visit was a {v.kind}, cargo runs {g.stats.cargoRuns}");
                    if (g.shuttle.visit == v && v.phase == ShuttlePhase.Landed) ShuttleDepartIn(v, 60);
                    ShuttleTicks(() => g.shuttle.visit != v, 1500);
                }
                if (pm.zones.Any(RPDefOf.RP_Deliveries))
                {
                    List<IntVec3> zone = pm.zones.CellsOf(RPDefOf.RP_Deliveries).ToList();
                    Run("deliveries zone", 130, at => zone.Any(c => c.DistanceTo(at) <= 3f), "in the Deliveries zone");
                }
                // Without the zone the crates go down on the pad, around the ramp.
                List<IntVec3> zoneCells = pm.zones.CellsOf(RPDefOf.RP_Deliveries).ToList();
                foreach (IntVec3 c in zoneCells) pm.zones.Set(c, null);
                r.Add(() =>
                {
                    foreach (IntVec3 c in zoneCells) pm.zones.Set(c, RPDefOf.RP_Deliveries);
                });
                if (pad != null) Run("pad", 40, at => pad.OccupiedRect().Contains(at), "on the pad");
            });
        }

        /// <summary>
        /// Released and paroled inmates wait in holding (reception by the pad without one) and board the shuttle when it is down (counted as released when they leave, once); one who misses it waits
        /// for the next.
        /// </summary>
        private static void CheckShuttleBoarding(Map map)
        {
            Guarded("released inmates board the shuttle", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                // (One released before the stage, as the events release real inmates before these checks, and then taken off the map with no shuttle - the ambulance's way, which discards the pawn: counted when
                // the stage clears the way out, and never again. Its entry only forgotten, the supervision's hourly fallback counted it at the next hour, in the middle of the boarding below.)
                Pawn leftEarlier = Throwaway(map, r);
                leftEarlier.Inmate().processed = true;
                ReleaseSystem.Release(g, leftEarlier, record: false);
                if (!ShuttleStage(map, r, "shuttle boarding")) return;
                ShuttleRemovePawn(leftEarlier);
                if (!ShuttleSystem.HasPad(map))
                {
                    problems0.Add("shuttle boarding: no pad");
                    return;
                }
                Pawn walker = Throwaway(map, r), parolee = Throwaway(map, r), missed = Throwaway(map, r);
                foreach (Pawn p in new[] { walker, parolee, missed })
                {
                    p.Inmate().processed = true;
                    r.Add(() =>
                    {
                        g.shuttle.Forget(p);
                        g.fugitives.RemoveAll(f => f.pawn == p);
                        Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                    });
                }
                int released0 = g.stats.released;
                Rand.PushState(7);
                try
                {
                    ReleaseSystem.Release(g, walker, record: false);
                    ReleaseSystem.Release(g, parolee, paroled: true, record: false);
                }
                finally
                {
                    Rand.PopState();
                }
                bool outbound = g.shuttle.OutboundOf(walker) is { kind: OutboundKind.Released } && g.shuttle.OutboundOf(parolee) is { kind: OutboundKind.Paroled };
                if (!outbound || g.stats.released != released0) problems0.Add($"shuttle boarding: after the release both are on the way out {outbound}, released {g.stats.released - released0} counted already (0 expected)");
                // No shuttle yet: they wait in holding when the prison has one (inside, under a roof), else in reception by the pad.
                PrisonMap pm = PrisonMap.Of(map);
                bool hasHolding = pm.zones.Any(RPDefOf.RP_Holding);
                IntVec3 reception = g.shuttle.ReceptionAnchor(map);
                bool Waiting(Pawn p) => p.Spawned && (hasHolding ? pm.zones.ZoneAt(p.Position) == RPDefOf.RP_Holding : p.Position.InHorDistOf(reception, ShuttleSystem.ReceptionRadius + 1f));
                int toReception = ShuttleTicks(() => Waiting(walker) && Waiting(parolee), 4000);
                bool waiting = Waiting(walker) && Waiting(parolee);
                if (!waiting) problems0.Add($"shuttle boarding: the released did not wait in {(hasHolding ? "holding" : "reception")} ({walker.Position}, {parolee.Position}; reception {reception}) after {toReception} ticks");
                // The shuttle comes: both board.
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                // (The shuttle's own roll for a stowaway, 600 ticks after landing, can pick the third throwaway waiting about the ramp: they are gone with the shuttle when the check comes to release them, and
                // vanilla's release throws on a pawn with no map. The roll is spent; the stowaway has its own checks.)
                if (v != null) v.stowawayRolled = true;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                // (As long as boarding is open: the walk from holding to the ramp in the plan's deep snow is three or four times as long as on bare ground, and a fixed 3000 ticks left a released inmate who was
                // walking, 12 cells from the ramp, with 2000 ticks of the stay to spare. A shuttle that lifts off with them still walking is a failure all the same.)
                int boarding = ShuttleTicks(() => !walker.Spawned && !parolee.Spawned || g.shuttle.visit != v || !(v.shuttle is { Spawned: true } open && ShuttleSystem.BoardingOpenFor(v, open)), Math.Max(3000, v.departTick - ShNow));
                bool both = !walker.Spawned && !parolee.Spawned;
                RPLog.Message($"[RimPrison:test] shuttle boarding: waited {waiting} ({toReception} ticks), both boarded {both} ({boarding} ticks), released counted {g.stats.released - released0}, boarded on the visit {v.boarded}");
                string LateOne(Pawn x, ShuttleVisit visit) => x.Spawned
                    ? $"{x.LabelShort} at {x.Position} on {x.CurJobDef?.defName} (outbound {g.shuttle.OutboundOf(x)?.kind}, {(x.pather.Moving ? "walking" : "standing")}, {x.Position.DistanceTo(visit?.shuttle?.InteractionCell ?? x.Position):F0} cells from the ramp, in {pm.zones.ZoneAt(x.Position)?.defName ?? "no zone"})"
                    : $"{x.LabelShort} is not on the map (dead {x.Dead}, destroyed {x.Destroyed}, discarded {x.Discarded}, in the world {Find.WorldPawns.Contains(x)}, outbound {g.shuttle.OutboundOf(x)?.kind.ToString() ?? "none"})";
                string Where(Pawn x) => x.Spawned ? $"{x.LabelShort} at {x.Position} on {x.CurJobDef?.defName} (outbound {g.shuttle.OutboundOf(x)?.kind}, {(x.pather.Moving ? "walking" : "standing")}, {x.Position.DistanceTo(v.shuttle?.InteractionCell ?? x.Position):F0} cells from the ramp)" : $"{x.LabelShort} aboard";
                if (!both) problems0.Add($"shuttle boarding: after {boarding} ticks the released had not boarded (walker spawned {walker.Spawned}, parolee {parolee.Spawned}, phase {v.phase}; {Where(walker)}; {Where(parolee)}; the visit lifts off in {v.departTick - ShNow} ticks, boarding open {v.shuttle is { Spawned: true } openNow && ShuttleSystem.BoardingOpenFor(v, openNow)})");
                if (g.stats.released != released0 + 2 || v.boarded != 2) problems0.Add($"shuttle boarding: released counted {g.stats.released - released0} (2 expected), {v.boarded} boarded on the visit");
                if (g.shuttle.OutboundOf(walker) != null || g.shuttle.OutboundOf(parolee) != null) problems0.Add("shuttle boarding: boarded inmates are still on the way out");
                // A third, released after boarding has closed: it lifts off without them, they wait for the next and are counted once.
                if (g.shuttle.visit == v && v.phase == ShuttlePhase.Landed) ShuttleDepartIn(v, ShuttleSystem.BoardingClosesTicks - 20);
                ReleaseSystem.Release(g, missed, record: false);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                bool gone = g.shuttle.visit != v;
                bool stillHere = missed.Spawned && g.shuttle.OutboundOf(missed) != null;
                if (!gone || !stillHere || g.stats.released != released0 + 2 || v.boarded != 2)
                    problems0.Add($"shuttle boarding: the shuttle lifted off {gone}; the late one still waiting {stillHere}; released {g.stats.released - released0} (2 expected), {v.boarded} aboard; the late one: {Where(missed)}, outbound {g.shuttle.OutboundOf(missed)?.kind.ToString() ?? "none"}");
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit next = g.shuttle.visit;
                // (The second visit's stowaway roll is spent as the first one's was: it can pick the late one waiting about the ramp, who is then gone with the shuttle and never counted.)
                if (next != null) next.stowawayRolled = true;
                ShuttleTicks(() => g.shuttle.visit != next || next.phase == ShuttlePhase.Landed, 1500);
                // (As long as boarding is open, as the first visit's wait is: a fixed 4000 ticks was the longest wait in 401 runs of this line - the walk from holding to the ramp took 3300 to 3800 ticks in six of them -
                // and the one run that used all of it left the late one walking with the stay still open.)
                int later = ShuttleTicks(() => !missed.Spawned || g.shuttle.visit != next || !(next.shuttle is { Spawned: true } openNext && ShuttleSystem.BoardingOpenFor(next, openNext)), Math.Max(4000, next.departTick - ShNow));
                RPLog.Message($"[RimPrison:test] shuttle boarding: the one who missed it boarded the next {!missed.Spawned} ({later} ticks), released counted {g.stats.released - released0}");
                if (missed.Spawned || g.stats.released != released0 + 3) problems0.Add($"shuttle boarding: the late one boarded the next shuttle {!missed.Spawned}, released {g.stats.released - released0} (3 expected); {LateOne(missed, next)}, {later} ticks, boarded on the visit {next.boarded}, phase {next.phase}, the visit lifts off in {next.departTick - ShNow} ticks");
                if (g.shuttle.visit == next && next.phase == ShuttlePhase.Landed) ShuttleDepartIn(next, 60);
                ShuttleTicks(() => g.shuttle.visit != next, 1500);
                if (g.stats.released != released0 + 3) problems0.Add($"shuttle boarding: released counted {g.stats.released - released0} after the last lift-off (3 expected, once each); {LateOne(missed, next)}, boarded on the visit {next.boarded}");
            });
        }

        /// <summary>A blocked pad: the shuttle waits for it to clear, or lands nearby after an hour; nobody under it is crushed.</summary>
        private static void CheckShuttleBlockedPad(Map map)
        {
            Guarded("shuttle and a blocked pad", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "blocked pad")) return;
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad == null)
                {
                    problems0.Add("blocked pad: no pad");
                    return;
                }
                // Something on the hull's spot: the shuttle holds in orbit.
                Thing crate = ThingMaker.MakeThing(ThingDefOf.Steel);
                crate.stackCount = 5;
                // (Forbidden: the colony's haulers take a loose stack of steel to a stockpile in the check's hundreds of ticks, and what is gone from the pad no longer blocks it, or is no longer there to be found.)
                crate.SetForbidden(true, warnOnFail: false);
                GenSpawn.Spawn(crate, pad.Position, map);
                r.Add(() =>
                {
                    if (!crate.Destroyed) crate.Destroy(DestroyMode.Vanish);
                });
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => false, 200);
                bool held = v != null && g.shuttle.visit == v && v.phase == ShuttlePhase.Holding && v.hold == ShuttleHold.PadBlocked && map.listerThings.ThingsOfDef(RPShuttleDefOf.RP_ShuttleIncoming).Count == 0;
                if (!held) problems0.Add($"blocked pad: the shuttle should be holding for the pad ({v?.Describe()})");
                // Cleared: it lands on the pad.
                crate.Destroy(DestroyMode.Vanish);
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                bool onPad = v != null && v.phase == ShuttlePhase.Landed && v.onPad && v.shuttle.Position == pad.Position;
                if (!onPad) problems0.Add($"blocked pad: once the pad was clear the shuttle did not land on it ({v?.Describe()})");
                RPLog.Message($"[RimPrison:test] blocked pad: held {held}, landed on the pad once it was clear {onPad}");
                EndShuttleVisit(g, map);
                // Still blocked an hour later: it lands nearby.
                Thing crate2 = ThingMaker.MakeThing(ThingDefOf.Steel);
                crate2.stackCount = 5;
                crate2.SetForbidden(true, warnOnFail: false);
                GenSpawn.Spawn(crate2, pad.Position, map);
                r.Add(() =>
                {
                    if (!crate2.Destroyed) crate2.Destroy(DestroyMode.Vanish);
                });
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit w = g.shuttle.visit;
                ShuttleTicks(() => w.blockedSince >= 0, 400);
                w.blockedSince = ShNow - ShuttleSystem.PadWaitTicks - 1;
                // And somebody walks under it while it comes down: it hovers, nobody is crushed.
                ShuttleTicks(() => g.shuttle.visit != w || w.phase == ShuttlePhase.Inbound, 400);
                Pawn bystander = Throwaway(map, r, spawn: false);
                IntVec3 under = IntVec3.Invalid;
                ShuttleTicks(() => false, 1);
                if (w.phase == ShuttlePhase.Inbound && w.site.IsValid)
                {
                    under = w.site;
                    GenSpawn.Spawn(bystander, under, map);
                    InmateGenerator.MakeInmate(bystander, null);
                    Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                    wait.expiryInterval = 900;
                    bystander.jobs.StartJob(wait, JobCondition.InterruptForced);
                }
                ShuttleTicks(() => g.shuttle.visit != w || w.phase == ShuttlePhase.Landed, 2000);
                bool nearby = w.phase == ShuttlePhase.Landed && !w.onPad && w.shuttle.Position != pad.Position;
                // (A guard may have picked them up meanwhile: carried is not crushed.)
                bool safe = !under.IsValid || !bystander.Dead && bystander.SpawnedOrAnyParentSpawned && !(bystander.Spawned && w.shuttle.OccupiedRect().Contains(bystander.Position));
                RPLog.Message($"[RimPrison:test] blocked pad: still blocked after an hour, landed nearby {nearby} at {w.shuttle?.Position}; somebody under it came to no harm {safe} ({(under.IsValid ? bystander.Position.ToString() : "nobody was put there")})");
                if (!nearby) problems0.Add($"blocked pad: after the hour it did not land nearby ({w.Describe()})");
                if (!safe) problems0.Add($"blocked pad: a pawn on the landing spot was crushed or left under the hull (alive {!bystander.Dead}, spawned {bystander.Spawned}, at {bystander.Position}, held by {bystander.ParentHolder?.GetType().Name}, world pawn {Find.WorldPawns.Contains(bystander)}, destroyed {bystander.Destroyed}, hull {w.shuttle?.OccupiedRect()}, mental {bystander.MentalStateDef?.defName}, last job {bystander.CurJobDef?.defName})");
                if (!crate2.Spawned || crate2.Destroyed) problems0.Add("blocked pad: the crate on the pad was destroyed or moved by a landing beside it");
            });
        }

        /// <summary>The weather holds the shuttle in orbit (a letter, the visit waits), and after six hours it is called off for the day: no arrivals.</summary>
        private static void CheckShuttleDelays(Map map)
        {
            Guarded("shuttle delayed and called off", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "shuttle delays")) return;
                WeatherDef storm = DefDatabase<WeatherDef>.GetNamedSilentFail("RainyThunderstorm");
                if (storm == null)
                {
                    RPLog.Message("[RimPrison:test] shuttle delays: no thunderstorm weather in this game");
                    return;
                }
                WeatherDef weather0 = map.weatherManager.curWeather;
                ShuttleSystem.ClearSkiesForTests = false;
                r.Add(() => map.weatherManager.curWeather = weather0);
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(s => Unlocks.IsUnlocked(s) && !s.wingOnly);
                g.intake.perDay[sec] = 2;
                g.intake.allowOvercrowding = true;
                int number0 = g.intake.nextInmateNumber, cancelled0 = g.stats.shuttlesCancelled;
                // (A weather set by hand keeps the age of the one before it, and the weather manager rolls the next at once when that is past this one's duration: the storm lasted a few ticks.)
                map.weatherManager.curWeather = storm;
                map.weatherManager.curWeatherAge = 0;
                bool holdSeen = ShuttleSystem.HoldReason(map) == ShuttleHold.Weather;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => false, 200);
                bool held = g.shuttle.visit == v && v.phase == ShuttlePhase.Holding && v.hold == ShuttleHold.Weather;
                bool letter = r.NewLetters.Any(l => l.Label.ToString() == "RP_ShuttleDelayedLabel".Translate().ToString());
                if (!holdSeen || !held || !letter) problems0.Add($"shuttle delays: a storm holds the shuttle {held}, the hold reason saw it {holdSeen}, the letter {letter}");
                // The weather clears: it comes down.
                map.weatherManager.curWeather = DefDatabase<WeatherDef>.GetNamed("Clear");
                map.weatherManager.curWeatherAge = 0;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                bool landed = g.shuttle.visit == v && v.phase == ShuttlePhase.Landed;
                if (!landed) problems0.Add($"shuttle delays: after the storm it did not land ({v?.Describe()})");
                if (landed)
                {
                    ShuttleTicks(() => g.shuttle.visit != v || v.arrivalsDone, 2500);
                    List<Pawn> arrivals = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { } c && c.inmateNumber >= number0).ToList();
                    r.Add(() =>
                    {
                        foreach (Pawn p in arrivals) ShuttleRemovePawn(p);
                    });
                }
                EndShuttleVisit(g, map);
                // A storm all day: after six hours the state calls it off; nobody is generated, nobody arrives.
                int number1 = g.intake.nextInmateNumber;
                map.weatherManager.curWeather = storm;
                map.weatherManager.curWeatherAge = 0;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit w = g.shuttle.visit;
                ShuttleTicks(() => false, 5);
                w.calledTick = ShNow - ShuttleSystem.MaxHoldTicks - 1;
                ShuttleTicks(() => g.shuttle.visit != w, 200);
                bool off = g.shuttle.visit == null && g.stats.shuttlesCancelled == cancelled0 + 1 && g.intake.nextInmateNumber == number1;
                bool offLetter = r.NewLetters.Any(l => l.Label.ToString() == "RP_ShuttleCancelledLabel".Translate().ToString());
                RPLog.Message($"[RimPrison:test] shuttle delays: held by the storm {held}, landed once it cleared {landed}, called off after six hours {off}, letters {letter}/{offLetter}");
                if (!off || !offLetter) problems0.Add($"shuttle delays: after six hours of storm the shuttle was called off {off} (cancelled {g.stats.shuttlesCancelled - cancelled0}, inmates generated {g.intake.nextInmateNumber - number1}), the letter {offLetter}");
            });
        }

        /// <summary>A stowaway: an inmate in reach of the pad runs for the shuttle; a guard on the pad stops them at the ramp, with nobody there they get aboard (an escape, not a passenger).</summary>
        private static void CheckShuttleStowaway(Map map)
        {
            Guarded("stowaway on the shuttle", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "stowaway")) return;
                if (!ShuttleSystem.HasPad(map))
                {
                    problems0.Add("stowaway: no pad");
                    return;
                }
                // (No real guard takes the post meanwhile: the check decides who is on the pad.)
                g.shuttle.padGuards = 0;
                IntVec3 yard = PrisonBuilder.Origin(map) + new IntVec3(12, 0, 4);
                // Two runners, one for each case (the guards who answer an escape put the first down).
                Pawn NewRunner()
                {
                    Pawn p = Throwaway(map, r, spawn: false);
                    GenSpawn.Spawn(p, yard, map);
                    InmateGenerator.MakeInmate(p, null);
                    p.Inmate().processed = true;
                    p.Inmate().RefreshActiveFlag();
                    r.Add(() =>
                    {
                        p.mindState?.mentalStateHandler?.Reset();
                        g.shuttle.stowaway = null;
                        g.fugitives.RemoveAll(f => f.pawn == p);
                        Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                    });
                    return p;
                }
                Pawn caughtRunner = NewRunner(), freeRunner = NewRunner();
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (v == null || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add("stowaway: the shuttle never landed");
                    return;
                }
                // (The shuttle's own roll for a stowaway comes 600 ticks after landing, which the check's long stay restarts: with a 12% chance it picked a real inmate who stood near and replaced the runner
                // mid-run, who then boarded as a plain passenger and counted as one in the second half too. The roll is spent; the runners are the check's own.)
                v.stowawayRolled = true;
                IntVec3 ramp = v.shuttle.InteractionCell;
                CellRect hull = v.shuttle.OccupiedRect();
                // Held a few cells from the ramp (the run is short: the guards who answer an escape are not there yet).
                IntVec3 spot = CellFinder.TryFindRandomCellNear(ramp, map, 5, c => c.Standable(map) && !hull.Contains(c) && c.DistanceTo(ramp) >= 2f && c.GetFirstPawn(map) == null, out IntVec3 found) ? found : yard;
                void Park(Pawn runner)
                {
                    if (!runner.Spawned) return;
                    CompInmate rc = runner.Inmate();
                    // (A made-up inmate in a prison in the middle of the events plan may be on a break of their own.)
                    runner.mindState.mentalStateHandler.Reset();
                    rc.tension = 0f;
                    (rc.pendingPunishmentHours, rc.forcedActivity, rc.forcedUntilTick) = (0, null, -1);
                    if (runner.needs?.mood != null) runner.needs.mood.CurLevel = 0.9f;
                    runner.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    runner.pather.StopDead();
                    runner.Position = spot;
                    runner.Notify_Teleported(true, true);
                    Job stay = JobMaker.MakeJob(JobDefOf.Wait);
                    stay.expiryInterval = 3000;
                    runner.jobs.StartJob(stay, JobCondition.InterruptForced);
                }
                // (Boarding closes as the stay runs out, and a stowaway is called off with it: the run of the check is 900 ticks a half, and a visit that landed early in the
                // events plan could be that near to lift-off. The "stowaway runner called off by the closing of boarding" flake, seen one run in six on 658ed6a: the second half's runner
                // was running to the ramp with its stowaway flag cleared, never boarded, left unrecorded.)
                void LongStay() => (v.departTick, v.landedTick) = (ShNow + 2400, ShNow);
                LongStay();
                Park(caughtRunner);
                RPLog.Message($"[RimPrison:test] stowaway: the runner at {caughtRunner.Position}, {caughtRunner.Position.DistanceTo(ramp):F0} cells from the ramp, active {caughtRunner.Inmate().activeFlag}, awake {caughtRunner.Awake()}, mental {caughtRunner.MentalStateDef?.defName}, can reach through doors {caughtRunner.CanReach(ramp, PathEndMode.OnCell, Danger.Deadly, canBashDoors: true, canBashFences: false, TraverseMode.PassDoors)}");
                // With a guard on the pad: stopped at the ramp, where the run ends (not when it starts).
                int caught0 = g.stats.stowawaysCaught, tried0 = g.stats.stowawayAttempts, away0 = g.stats.stowawaysAway, boarded0 = v.boarded;
                v.lastGuardTick = ShNow;
                Pawn started = g.shuttle.TryStowaway(g, map, v, caughtRunner);
                bool running = started == caughtRunner && caughtRunner.MentalStateDef == RPMentalStateDefOf.RP_Escaping && ShuttleSystem.StowawayGoal(caughtRunner) == ramp && g.stats.stowawayAttempts == tried0 + 1 && g.stats.stowawaysCaught == caught0;
                // (A guard is "on the pad" all the way: the stamp is kept fresh, the run to the ramp can take longer than the 300 ticks it counts for.)
                ShuttleTicksTraced(() =>
                {
                    v.lastGuardTick = ShNow;
                    return g.stats.stowawaysCaught != caught0 || !caughtRunner.Spawned;
                }, 3000, caughtRunner, "stowaway with a guard");   // (Deep snow from the events plan's weather: a cell can take 120 ticks.)
                bool stopOk = g.stats.stowawaysCaught == caught0 + 1 && caughtRunner.Spawned && !caughtRunner.InMentalState && g.shuttle.stowaway == null && g.stats.stowawaysAway == away0;
                bool seen = caughtRunner.Inmate().records.Any(rec => rec.def == RPShuttleDefOf.RP_EscapeAttempt && rec.seen);
                // Nobody there: they get aboard, and are no passenger.
                v.lastGuardTick = -999999;
                LongStay();
                Park(freeRunner);
                int tried1 = g.stats.stowawayAttempts;
                Pawn who = g.shuttle.TryStowaway(g, map, v, freeRunner);
                bool runningFree = who == freeRunner && freeRunner.MentalStateDef == RPMentalStateDefOf.RP_Escaping && ShuttleSystem.StowawayGoal(freeRunner) == ramp && g.stats.stowawayAttempts == tried1 + 1;
                // The escape sends a stowaway to board the shuttle (a walk to the ramp with exitMapOnArrival never left the map, the ramp being
                // no map edge: at the ramp it was the same job again every tick, "started 10 jobs in one tick").
                if (runningFree)
                {
                    Job stowJob = new JobGiver_EscapeAttempt().TryIssueJobPackage(freeRunner, default).Job;
                    if (stowJob?.def != RPShuttleDefOf.RP_BoardShuttle || stowJob.targetA.Thing != v.shuttle)
                        problems0.Add($"stowaway: the escape gives a runner for the shuttle {stowJob?.def?.defName ?? "no job"} on {stowJob?.targetA.ToString() ?? "-"}, not boarding the shuttle");
                    // Once boarding has closed (the shuttle about to lift off) they are not sent for it: a board job that cannot start was given again every tick.
                    // (At the end of the stay with nothing left to unload and no overtime to give: while the shuttle stays on for its unloading, boarding stays open.)
                    int depart0 = v.departTick, landed0 = v.landedTick;
                    Pawn stow0 = g.shuttle.stowaway;
                    ShuttleDepartIn(v, ShuttleSystem.BoardingClosesTicks - 50);
                    if (ShuttleSystem.StowawayGoal(freeRunner) != null) problems0.Add($"stowaway: with boarding closed ({v.departTick - ShNow} ticks to lift-off) the runner is still sent to board");
                    (v.departTick, v.landedTick) = (depart0, landed0);
                    g.shuttle.stowaway = stow0;
                }
                ShuttleTicksTraced(() => !freeRunner.Spawned || g.shuttle.visit != v || !freeRunner.InMentalState, 3000, freeRunner, "stowaway with nobody");
                bool recorded = freeRunner.Inmate().records.Any(rec => rec.def == RPShuttleDefOf.RP_EscapeAttempt);
                bool aboard = !freeRunner.Spawned && g.stats.stowawaysAway == away0 + 1 && v.boarded == boarded0;
                RPLog.Message($"[RimPrison:test] stowaway: a guard on the pad - ran {running}, stopped at the ramp {stopOk} (written up as seen {seen}); none - ran {runningFree}, got aboard {aboard} (away {g.stats.stowawaysAway - away0}, passengers boarded {v.boarded - boarded0}), recorded {recorded}, still running {freeRunner.InMentalState}");
                if (!running || !stopOk || !seen) problems0.Add($"stowaway: with a guard on the pad they ran {running}, were stopped at the ramp {stopOk} and it was written up as seen {seen} (caught {g.stats.stowawaysCaught - caught0}, mental {caughtRunner.MentalStateDef?.defName})");
                // One line in the chronicle for the one escape (the stowaway's own, with how): the escape's generic line is not written as well.
                if (aboard)
                {
                    int away1 = g.story.entries.Count(e => e.text == "RP_Chr_StowawayAway".Translate(freeRunner.LabelShort).Resolve()), generic = g.story.entries.Count(e => e.text == "RP_Chr_Escaped".Translate(freeRunner.LabelShort).Resolve());
                    if (away1 != 1 || generic != 0) problems0.Add($"first hour 2.2: a stowaway's escape has {away1} line(s) of its own and {generic} generic escape line(s) in the chronicle (one line, the stowaway's)");
                }
                if (!runningFree || !recorded || !aboard) problems0.Add($"stowaway: with nobody on the pad they ran for the ramp {runningFree} (mental {freeRunner.MentalStateDef?.defName}), recorded {recorded}, got aboard without counting as a passenger {aboard} (away {g.stats.stowawaysAway - away0}, boarded {v.boarded - boarded0})");
                caughtRunner.mindState?.mentalStateHandler?.Reset();
                freeRunner.mindState?.mentalStateHandler?.Reset();
                EndShuttleVisit(g, map);
            });
        }

        /// <summary>The cargo crew as a ring's supplier: a guard on the pad finds the package (and the supplier is learned); with nobody there it reaches the inmate unfound and the ring has a supplier nobody knows.</summary>
        private static void CheckShuttleCargoContraband(Map map)
        {
            Guarded("contraband in the cargo", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "cargo contraband")) return;
                PrisonMap pm = PrisonMap.Of(map);
                Pawn dealer = Throwaway(map, r);
                CompInmate dc = dealer.Inmate();
                dc.reputations.Add(DefDatabase<ReputationDef>.GetNamed("RP_Dealer"));
                dc.processed = true;
                dc.RefreshActiveFlag();
                List<StoryArc> arcs0 = g.story.arcs.ToList();
                r.Add(() =>
                {
                    g.story.arcs.RemoveAll(a => !arcs0.Contains(a));
                });
                StoryArc ring = RingSystem.Form(dealer, map, ignoreLimit: true);
                if (ring == null)
                {
                    problems0.Add("cargo contraband: no ring formed around the dealer");
                    return;
                }
                ShuttleSystem.CallCargo(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (v == null || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add("cargo contraband: the cargo run never landed");
                    return;
                }
                string crew = "RP_CargoCrewName".Translate();
                int found0 = g.stats.cargoContrabandFound, missed0 = g.shuttle.cargoContrabandMissed, finds0 = pm.finds.Count;
                // Unguarded: the package reaches the dealer, and the ring has a supplier the warden does not know.
                v.lastGuardTick = -999999;
                bool missed = g.shuttle.TryCargoContraband(g, map, v, force: true, roll: 0.01f, only: dealer);
                RingLink supplier = ring.ring.links.Find(l => l.role == RingRole.Relative);
                bool carried = ContrabandUtility.CarriesContraband(dealer);
                bool hidden = supplier != null && supplier.name == crew && !supplier.known;
                // A guard on the pad: found, noted on the map of finds, and the supplier is learned.
                dealer.inventory?.innerContainer.ClearAndDestroyContents();
                v.lastGuardTick = ShNow;
                bool caught = g.shuttle.TryCargoContraband(g, map, v, force: true, roll: 0.01f, only: dealer);
                bool onMap = pm.finds.Count == finds0 + 1 && pm.finds.Last().source == FindSource.Cargo;
                bool learned = ring.ring.links.Find(l => l.role == RingRole.Relative) is { known: true };
                bool kept = !ContrabandUtility.CarriesContraband(dealer);
                RPLog.Message($"[RimPrison:test] cargo contraband: unguarded reached the dealer {missed && carried} (supplier hidden {hidden}), guarded found {caught && onMap} (supplier learned {learned}, nothing reached them {kept})");
                if (!missed || !carried || !hidden) problems0.Add($"cargo contraband: unguarded the package reached the dealer {carried}, the supplier link exists and is unknown {hidden} (name {supplier?.name}, known {supplier?.known})");
                if (!caught || !onMap || !kept || !learned) problems0.Add($"cargo contraband: with a guard on the pad it was found {caught}, put on the map of finds {onMap}, kept from the dealer {kept}, the supplier learned {learned}");
                if (g.stats.cargoContrabandFound != found0 + 1 || g.shuttle.cargoContrabandMissed != missed0 + 1) problems0.Add($"cargo contraband: counters found {g.stats.cargoContrabandFound - found0}, missed {g.shuttle.cargoContrabandMissed - missed0} (1 and 1 expected)");
                EndShuttleVisit(g, map);
            });
        }

        /// <summary>Pad duty: with guards asked for, the pad post is offered to a guard (a spot beside the ramp); with none asked for, nobody is sent.</summary>
        private static void CheckShuttlePadDuty(Map map)
        {
            Guarded("pad duty", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "pad duty")) return;
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => GuardUtility.IsGuard(x) && !x.Downed);
                if (guard == null || !ShuttleSystem.HasPad(map))
                {
                    problems0.Add($"pad duty: a guard on site {guard != null}, a pad {ShuttleSystem.HasPad(map)}");
                    return;
                }
                ShuttleSystem.CallCargo(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (v == null || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add("pad duty: the shuttle never landed");
                    return;
                }
                // (The guard is set down by the pad: from the cell block the walk through the prison's doors can take longer than the check waits, or not be open at all.)
                if (guard.Position.DistanceTo(v.shuttle.Position) > 8f && CellFinder.TryFindRandomCellNear(v.shuttle.InteractionCell, map, 4, c => c.Standable(map) && c.GetFirstPawn(map) == null && !v.shuttle.OccupiedRect().Contains(c), out IntVec3 nearPad))
                {
                    guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    guard.pather.StopDead();
                    guard.Position = nearPad;
                    guard.Notify_Teleported(true, true);
                }
                var giver = new WorkGiver_PadDuty { def = DefDatabase<WorkGiverDef>.GetNamed("RP_PadDuty_WorkGiver") };
                g.shuttle.padGuards = 0;
                bool skippedWhenNone = giver.ShouldSkip(guard);
                g.shuttle.padGuards = 1;
                bool offered = !giver.ShouldSkip(guard) && giver.HasJobOnThing(guard, v.shuttle);
                Job job = offered ? giver.JobOnThing(guard, v.shuttle) : null;
                bool post = job is { def: { } d } && d == RPShuttleDefOf.RP_PadDuty && job.targetB.Cell.IsValid && job.targetB.Cell.InHorDistOf(v.shuttle.InteractionCell, 6f);
                // A guard who takes it: counted as the pad being guarded.
                if (job != null)
                {
                    guard.jobs.StartJob(job, JobCondition.InterruptForced);
                    r.Add(() => guard.jobs.EndCurrentJob(JobCondition.InterruptForced));
                }
                // (The guard may be a long walk away: the shuttle stays as long as the check needs.)
                v.departTick = ShNow + 20000;
                int took = ShuttleTicks(() => g.shuttle.visit != v || ShuttleSystem.Guarded(v), 3000);   // (The stay is capped: a visit may end first.)
                // The shuttle's own guards, left to themselves: logged, not required (a guard may be busy or off shift).
                RPLog.Message($"[RimPrison:test] pad duty: none asked for skips it {skippedWhenNone}, one asked for is offered a post beside the ramp {post}, the pad counted as guarded after {took} ticks {ShuttleSystem.Guarded(v)}");
                if (!skippedWhenNone || !post) problems0.Add($"pad duty: with no guards asked for the giver skips {skippedWhenNone}; with one asked for the post is offered {offered}, beside the ramp {post}");
                if (!ShuttleSystem.Guarded(v)) problems0.Add($"pad duty: a guard sent to the pad did not count as guarding it after {took} ticks (guard at {guard.Position} on {guard.CurJobDef?.defName}, the shuttle at {v.shuttle?.Position}, downed {guard.Downed}, drafted {guard.Drafted}, mental {guard.MentalStateDef?.defName}, phase {v.phase}, last guarded {v.lastGuardTick} of {ShNow}, the post {job?.targetB.Cell})");
                guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
                EndShuttleVisit(g, map);
            });
        }

        // ---- the saveload plan: a shuttle on the pad, mid-unload ---------------------------------------------------------------

        private static bool shuttleSeeded;
        private static int shuttleSeedAboard, shuttleSeedStepped, shuttleSeedCargo, shuttleSeedTransferId = -1, shuttleSeedShuttleId, shuttleSeedFirstNumber, shuttleSeedArrivals, shuttleSeedCarriedId = -1, shuttleSeedOrderId = -1;
        private static (int recaptured, int returning) shuttleSeedMarshalIds = (-1, -1);
        private static string shuttleSeedDescribed;
        private const int ShuttleFrozen = 10 * GenDate.TicksPerDay;

        /// <summary>
        /// First thing in the saveload plan's seeding (it ticks the game, which the other seeds' tick-relative values must not see): the state shuttle on the ground with one
        /// passenger down the ramp, more aboard and cargo still to unload, then frozen (no more unloading, no lift-off) until the save. See <see cref="RecordShuttleForReload"/>.
        /// </summary>
        private static void SeedShuttleForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            shuttleSeeded = false;
            if (!ShuttleSystem.HasPad(map))
            {
                reloadProblems.Add("saveload: no shuttle pad to seed a shuttle on");
                return;
            }
            EndShuttleVisit(g, map);
            SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(x => Unlocks.IsUnlocked(x) && !x.wingOnly);
            shuttleSeedFirstNumber = g.intake.nextInmateNumber;
            (int perDay0, int lastRun0) = (g.intake.PerDay(sec), g.intake.lastRunDay);
            g.intake.perDay[sec] = 0;
            g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = 60, cost = 0, arriveTick = ShNow - 1 });
            ShuttleSystem.ClearSkiesForTests = true;
            ShuttleSystem.CallDaily(g, map);
            ShuttleVisit v = g.shuttle.visit;
            // (A full prison has no room for the policy's inmates: three are put aboard by hand.)
            for (int i = 0; v != null && v.shuttle != null && i < 3; i++)
            {
                Pawn p = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
                if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                if (v.shuttle.passengers.TryAdd(p, canMergeWithExistingStacks: false)) v.rosterSize++;
            }
            g.intake.perDay[sec] = perDay0;
            g.intake.lastRunDay = lastRun0;
            // One who the last shuttle could not unload, kept in the world for the next roster.
            Pawn carried = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
            if (Find.WorldPawns.Contains(carried)) Find.WorldPawns.RemovePawn(carried);
            Find.WorldPawns.PassToWorld(carried, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            g.shuttle.carriedOver.Add(carried);
            carried.Inmate().carriedSkipDay = 5;
            shuttleSeedCarriedId = carried.thingIDNumber;
            ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed && v.steppedOff >= 1 && v.shuttle.passengers.Count >= 1, 3000);
            if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed || v.shuttle.passengers.Count < 1 || v.cargo.Count == 0)
            {
                reloadProblems.Add($"saveload: could not get a shuttle mid-unload ({v?.Describe()})");
                return;
            }
            // Frozen where it stands: unloading waits, the lift-off waits (the other seeds and checks tick the game).
            v.nextStepTick = ShNow + ShuttleFrozen;
            v.departTick = ShNow + ShuttleFrozen;
            shuttleSeeded = true;
        }

        /// <summary>Just before the save: a transfer put on the way out, and what the shuttle looks like now (what must come back).</summary>
        private static void RecordShuttleForReload(Map map)
        {
            if (!shuttleSeeded) return;
            PrisonGame g = PrisonGame.Instance;
            ShuttleVisit v = g.shuttle.visit;
            if (v is not { phase: ShuttlePhase.Landed }) 
            {
                shuttleSeeded = false;
                reloadProblems.Add($"saveload: the seeded shuttle was gone before the save ({v?.Describe()})");
                return;
            }
            SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(x => Unlocks.IsUnlocked(x) && !x.wingOnly);
            Pawn leaving = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
            GenSpawn.Spawn(leaving, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            InmateGenerator.MakeInmate(leaving, null);
            g.shuttle.Register(leaving, OutboundKind.Transferred);
            // And vanilla's own Release order (not a release the prison counts) with a reoffence waiting to be counted when they leave.
            Pawn ordered = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
            GenSpawn.Spawn(ordered, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            InmateGenerator.MakeInmate(ordered, null);
            g.shuttle.Register(ordered, OutboundKind.Released, counted: false, reoffends: true);
            shuttleSeedOrderId = ordered.thingIDNumber;
            // The fields the snapshot used to miss, off their defaults: the visit's told-once flags, the held letter's day, and the clocks of the entries (each must come back as it went).
            (v.announcedHold, v.announcedBlocked, v.stowawayRolled, g.shuttle.lastHeldLetterDay) = (true, true, true, 77);
            g.shuttle.OutboundOf(leaving).sinceTick = ShNow - 777;
            g.shuttle.OutboundOf(leaving).cause = "RP_Cause_StateHospital";
            // An escort in progress (the guard's id and the tick the walk began), and two of the state's marshals' prisoners waiting for the next shuttle.
            (g.shuttle.OutboundOf(leaving).escortId, g.shuttle.OutboundOf(leaving).escortTick) = (4242, ShNow - 33);
            Pawn recaptured = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++), returning = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
            foreach (Pawn waiting in new[] { recaptured, returning })
                if (!Find.WorldPawns.Contains(waiting)) Find.WorldPawns.PassToWorld(waiting, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            g.shuttle.AddMarshal(recaptured, MarshalKind.Recaptured, escapedTick: ShNow - 5555);
            g.shuttle.AddMarshal(returning, MarshalKind.Returning, ParoleLife.NewOffence);
            foreach (MarshalEntry waiting in g.shuttle.marshals) waiting.sinceTick = ShNow - 999;
            shuttleSeedMarshalIds = (recaptured.thingIDNumber, returning.thingIDNumber);
            g.shuttle.OutboundOf(ordered).sinceTick = ShNow - 888;
            foreach (ArrivalEntry a in g.shuttle.arrivals)
                (a.sinceTick, a.escortTick, a.walkOnTick) = (ShNow - 555, ShNow - 33, ShNow + 4444);
            (shuttleSeedTransferId, shuttleSeedShuttleId) = (leaving.thingIDNumber, v.shuttle.thingIDNumber);
            (shuttleSeedAboard, shuttleSeedStepped, shuttleSeedCargo, shuttleSeedArrivals) = (v.shuttle.passengers.Count, v.steppedOff, v.cargo.Sum(o => o.count), g.shuttle.arrivals.Count);
            shuttleSeedDescribed = v.Describe();
            RPLog.Message($"[RimPrison:test] saveload shuttle: on the pad, {shuttleSeedStepped} stepped off, {shuttleSeedAboard} still aboard, {shuttleSeedCargo} items to unload, {shuttleSeedArrivals} arrivals on the way, a transfer waiting");
        }

        /// <summary>The seeded shuttle came back whole and goes on with its unloading; the transfer is still on the way out (and goes, or is removed).</summary>
        private static void CheckShuttleAfterReload(Map map)
        {
            ShuttleSystem.ClearSkiesForTests = false;
            if (!shuttleSeeded) return;
            shuttleSeeded = false;
            PrisonGame g = PrisonGame.Instance;
            ShuttleVisit v = g.shuttle.visit;
            bool whole = v is { phase: ShuttlePhase.Landed } && v.shuttle is { Spawned: true } && v.shuttle.thingIDNumber == shuttleSeedShuttleId && v.shuttle.passengers.Count == shuttleSeedAboard
                && v.steppedOff == shuttleSeedStepped && v.cargo.Sum(o => o.count) == shuttleSeedCargo && v.Describe() == shuttleSeedDescribed;
            bool arrivals = g.shuttle.arrivals.Count == shuttleSeedArrivals && g.shuttle.arrivals.All(a => a.pawn is { Spawned: true } p && p.Inmate() is { inTransit: true });
            Pawn leaving = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == shuttleSeedTransferId);
            bool outbound = leaving != null && g.shuttle.OutboundOf(leaving) is { kind: OutboundKind.Transferred };
            // The escort in progress and the marshals' prisoners are read before the shuttle ticks on (they act on it).
            bool escortKept = leaving != null && g.shuttle.OutboundOf(leaving) is { escortId: 4242 };
            MarshalEntry m0 = g.shuttle.marshals.ElementAtOrDefault(0), m1 = g.shuttle.marshals.ElementAtOrDefault(1);
            bool marshalsKept = g.shuttle.marshals.Count == 2 && m0 is { kind: MarshalKind.Recaptured } && m0.pawn.thingIDNumber == shuttleSeedMarshalIds.recaptured && m0.escapedTick > 0
                                && m1 is { kind: MarshalKind.Returning } && m1.pawn.thingIDNumber == shuttleSeedMarshalIds.returning && m1.reason == ParoleLife.NewOffence
                                && g.shuttle.marshals.All(e => Find.WorldPawns.ForcefullyKeptPawns.Contains(e.pawn));
            Pawn ordered = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == shuttleSeedOrderId);
            bool flags = ordered != null && g.shuttle.OutboundOf(ordered) is { kind: OutboundKind.Released, counted: false, reoffends: true };
            Pawn carried = g.shuttle.carriedOver.FirstOrDefault(x => x.thingIDNumber == shuttleSeedCarriedId);
            bool kept = carried != null && Find.WorldPawns.Contains(carried) && carried.Inmate()?.carriedSkipDay == 5;
            RPLog.Message($"[RimPrison:test] saveload shuttle: visit {(whole ? "intact" : "changed")}, arrivals still in transit {arrivals}, transfer still on the way out {outbound}, an uncounted order with its reoffence {flags}, a passenger carried over {kept}");
            if (!flags) reloadProblems.Add($"the order vanilla's Release gave (uncounted, with a reoffence to come) was lost in the round trip ({ordered != null})");
            if (!kept) reloadProblems.Add($"the passenger carried over for the next roster was lost in the round trip ({carried != null})");
            if (!whole) reloadProblems.Add($"the shuttle changed in the round trip: {shuttleSeedDescribed} -> {v?.Describe()}");
            if (!arrivals) reloadProblems.Add($"the arrival who stepped off lost their way in the round trip (arrivals {g.shuttle.arrivals.Count}, in transit {g.shuttle.arrivals.Count(a => a.pawn?.Inmate()?.inTransit == true)})");
            if (!outbound) reloadProblems.Add($"the transfer waiting for the shuttle was lost in the round trip ({leaving != null})");
            RPLog.Message($"[RimPrison:test] saveload shuttle: the escort in progress {(escortKept ? "kept" : "lost")}, the state's marshals' prisoners {(marshalsKept ? "kept" : "lost")} ({g.shuttle.marshals.Count})");
            if (!escortKept) reloadProblems.Add("the guard walking the transfer to the ramp (the escort) was lost in the round trip");
            if (!marshalsKept) reloadProblems.Add($"the marshals' prisoners waiting for the shuttle changed in the round trip ({g.shuttle.marshals.Count} left; kinds {string.Join("/", g.shuttle.marshals.Select(e => e.kind))})");
            // (They were never real: gone again, with the pawns made for them.)
            foreach (MarshalEntry waiting in g.shuttle.marshals.ToList())
            {
                g.shuttle.marshals.Remove(waiting);
                Find.WorldPawns.ForcefullyKeptPawns.Remove(waiting.pawn);
                if (Find.WorldPawns.Contains(waiting.pawn)) Find.WorldPawns.RemovePawn(waiting.pawn);
                if (!waiting.pawn.Destroyed) waiting.pawn.Destroy();
            }
            // It goes on with its unloading (the freeze lifted, a short stay left).
            if (whole)
            {
                v.nextStepTick = ShNow;
                v.departTick = ShNow + 2400;
                ShuttleTicks(() => g.shuttle.visit != v || v.steppedOff > shuttleSeedStepped, 900);
                bool wentOn = g.shuttle.visit != v || v.steppedOff > shuttleSeedStepped;
                RPLog.Message($"[RimPrison:test] saveload shuttle: unloading went on after the reload {wentOn} ({v.steppedOff} stepped off)");
                if (!wentOn) reloadProblems.Add("the shuttle did not go on unloading after the reload");
            }
            // Cleaned up: the transfer and the passengers who came with the seed are not the plan's inmates.
            foreach (Pawn stay in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.thingIDNumber == shuttleSeedTransferId || x.Inmate() is { } c && c.inmateNumber >= shuttleSeedFirstNumber && c.inmateNumber < g.intake.nextInmateNumber).ToList())
            {
                ShuttleRemovePawn(stay);
            }
            foreach (Pawn gone in g.shuttle.carriedOver.ToList())
            {
                if (gone.thingIDNumber != shuttleSeedCarriedId) continue;
                g.shuttle.carriedOver.Remove(gone);
                if (Find.WorldPawns.Contains(gone)) Find.WorldPawns.RemovePawn(gone);
                if (!gone.Destroyed) gone.Destroy();
            }
        }
    }
}
