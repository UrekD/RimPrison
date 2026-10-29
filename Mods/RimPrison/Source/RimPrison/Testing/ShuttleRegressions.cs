using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the state shuttle (b2daa7a, findings 1-19). Each is its own method, called from <see cref="RunShuttleChecks"/> in the events
    /// plan, and each fails without the fix it is named for: it looks at what only the fixed code does. They run on a clear stage (<see cref="ShuttleStage"/>), on
    /// throwaway inmates and a shuttle of their own, and put back what they touch (settings, orders, the pad, roofs, walls, plants, the weather, their inmates).
    /// A job that is handed out again and again in one tick ("started 10 jobs in one tick") fails the shuttle checks (<see cref="AutoTest.JobLoopErrors"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void RunShuttleRegressions(Map map)
        {
            CheckShuttleRegReleasedOnce(map);
            CheckShuttleRegOnTheWayOut(map);
            CheckShuttleRegWaitingNeeds(map);
            CheckShuttleRegUnloadOutlastsStay(map);
            CheckShuttleRegUnloadCap(map);
            CheckShuttleRegStowawayTiming(map);
            CheckShuttleRegSites(map);
            CheckShuttleRegNoSiteHold(map);
            CheckShuttleRegStuckArrivals(map);
            CheckShuttleRegReleaseCounters(map);
            CheckShuttleRegCargoSpots(map);
            CheckShuttleRegClearSite(map);
            CheckShuttleRegRoofedPad(map);
            CheckShuttleRegArrivalDoors(map);
            CheckShuttleRegSmallThings(map);
        }

        /// <summary>A free patch of open ground for a hull and the margin asked for round it (and the way down), clear of the compound, its zones, buildings and items; invalid when there is none.</summary>
        private static IntVec3 ShuttleOpenPatch(Map map, int margin)
        {
            CellRect compound = PrisonBuilder.Footprint(map).ExpandedBy(2);
            PrisonMap pm = PrisonMap.Of(map);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 60f, true))
            {
                IntVec3 ramp = ShuttleSystem.Footprint(c, out CellRect rect);
                CellRect area = rect.ExpandedBy(margin);
                area.Encapsulate(ramp);
                if (!area.InBounds(map) || area.Overlaps(compound)) continue;
                bool open = true;
                foreach (IntVec3 x in area)
                {
                    if (!x.Standable(map) || x.Roofed(map) || x.GetEdifice(map) != null || x.GetTerrain(map).IsWater || pm.zones.ZoneAt(x) != null
                        || x.GetThingList(map).Any(t => t is Pawn || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame))
                    {
                        open = false;
                        break;
                    }
                }
                if (open) return c;
            }
            // (Nothing within 60 cells: say what stands in the way, by kind, so that a run that has none can be read.)
            var why = new Dictionary<string, int>();
            foreach (IntVec3 x in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 60f, true))
            {
                if (!x.InBounds(map) || compound.Contains(x)) continue;
                void Count(string k) => why[k] = why.TryGetValue(k, out int n) ? n + 1 : 1;
                if (!x.Standable(map)) Count(x.GetEdifice(map) != null ? "edifice " + x.GetEdifice(map).def.defName : x.GetThingList(map).FirstOrDefault(t => t.def.category == ThingCategory.Plant) is { } plant ? "plant " + plant.def.defName : "not standable");
                if (x.Roofed(map)) Count("roofed");
                if (pm.zones.ZoneAt(x) != null) Count("zone " + pm.zones.ZoneAt(x).defName);
                foreach (Thing t in x.GetThingList(map))
                    if (t is Pawn || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame) Count((t is Pawn ? "pawn " : "thing ") + t.def.defName);
            }
            RPLog.Message($"[RimPrison:test] shuttle: no open patch for a hull and margin {margin} within 60 cells of {PrisonBuilder.Footprint(map).CenterCell} (map {map.Size}), blocked by: " + string.Join(", ", why.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key + " " + kv.Value)));
            return IntVec3.Invalid;
        }

        /// <summary>The state's own settings that a regression check leans on, put back by the rollback: lets a throwaway be served out and let go at once.</summary>
        private static void ShuttleServed(Pawn p, Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            CompInmate c = p.Inmate();
            c.processed = true;
            c.sentenceDays = 1;
            c.admittedTick = ShNow - 3 * GenDate.TicksPerDay;
            r.Add(() =>
            {
                g.shuttle.Forget(p);
                g.fugitives.RemoveAll(f => f.pawn == p);
                Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            });
        }

        // ---- 1, 9: released once; 7, 14: not an inmate any more ---------------------------------------------------------------

        /// <summary>
        /// A released inmate still waiting for the shuttle at the next midnight is not released a second time (no second record, reoffence or line); a transfer whose
        /// sentence runs out while it waits stays a transfer.
        /// </summary>
        private static void CheckShuttleRegReleasedOnce(Map map)
        {
            Guarded("shuttle: released once", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "released once")) return;
                Pawn p = Throwaway(map, r), t = Throwaway(map, r);
                ShuttleServed(p, r);
                ShuttleServed(t, r);
                Rand.PushState(11);
                try
                {
                    ReleaseSystem.Release(g, p, record: false);
                }
                finally
                {
                    Rand.PopState();
                }
                int former1 = g.story.formerInmates.Count, rearrests1 = g.rearrests.Count, reoffended1 = g.stats.reoffended;
                g.shuttle.Register(t, OutboundKind.Transferred);
                int transferred0 = g.stats.transferredOut;
                // The midnight pass, and the same call made again directly.
                ReleaseSystem.ReleaseServedSentences(g, map, p);
                ReleaseSystem.ReleaseServedSentences(g, map, t);
                ReleaseSystem.Release(g, p, record: false);
                ReleaseSystem.Release(g, t, record: false);
                bool once = g.story.formerInmates.Count == former1 && g.rearrests.Count == rearrests1 && g.stats.reoffended == reoffended1;
                bool kept = g.shuttle.OutboundOf(t) is { kind: OutboundKind.Transferred } && g.shuttle.OutboundOf(p) is { kind: OutboundKind.Released };
                RPLog.Message($"[RimPrison:test] shuttle released once: not served again for the midnight pass {!ReleaseSystem.SentenceServed(g, p, p.Inmate())}, records {g.story.formerInmates.Count - former1} more, rearrests {g.rearrests.Count - rearrests1} more, a transfer kept its kind {kept}");
                if (ReleaseSystem.SentenceServed(g, p, p.Inmate()) || ReleaseSystem.SentenceServed(g, t, t.Inmate())) problems0.Add("shuttle released once: the midnight pass would release somebody who is waiting for the shuttle again");
                if (!once) problems0.Add($"shuttle released once: a second release made {g.story.formerInmates.Count - former1} more records, {g.rearrests.Count - rearrests1} more rearrests, {g.stats.reoffended - reoffended1} more reoffences");
                if (!kept) problems0.Add($"shuttle released once: the transfer became {g.shuttle.OutboundOf(t)?.kind} (kept {g.shuttle.OutboundOf(t) != null}), the release {g.shuttle.OutboundOf(p)?.kind}");
                if (g.stats.transferredOut != transferred0) problems0.Add("shuttle released once: a transfer was counted twice");
            });
        }

        /// <summary>
        /// Somebody waiting for the shuttle is not one of the prison's inmates: no daily payment, no room taken in the intake, no command to give (the file only, no
        /// transfer to charge for twice), no gang to lead, and no rearrest while they stand on the pad (it comes once they have left, and not at all for the dead).
        /// </summary>
        private static void CheckShuttleRegOnTheWayOut(Map map)
        {
            Guarded("shuttle: on the way out is not an inmate", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "on the way out")) return;
                Pawn p = Throwaway(map, r);
                ShuttleServed(p, r);
                CompInmate c = p.Inmate();
                // A gang of their own to lead, if one is without a leader on this map.
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault(d => !GangUtility.HasLeader(map, d, p));
                if (gang != null)
                {
                    c.gang = gang;
                    c.gangLeader = true;
                }
                int pay0 = DailySettlement.DailyPayments(g, map, out int inmates0), count0 = ShuttleSystem.InmateCount(map);
                bool led0 = gang != null && GangUtility.HasLeader(map, gang, null);
                Rand.PushState(11);
                try
                {
                    ReleaseSystem.Release(g, p, record: false);
                }
                finally
                {
                    Rand.PopState();
                }
                int pay1 = DailySettlement.DailyPayments(g, map, out int inmates1), count1 = ShuttleSystem.InmateCount(map);
                bool led1 = gang != null && GangUtility.HasLeader(map, gang, null);
                // (Is it they who still count as the leader: another inmate of the gang can lead by now, a lieutenant who took over or one made leader meanwhile, which is not theirs to hold.)
                bool theyLead1 = led1 && !GangUtility.HasLeader(map, gang, p);
                string leaders1 = gang == null ? "" : string.Join(", ", map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { gangLeader: true } xc && xc.gang == gang).Select(x => $"{x.LabelShort}{(x == p ? " (the one released)" : "")}{(ShuttleSystem.OnTheWayOut(x) ? " on the way out" : "")}"));
                int gizmos = c.CompGetGizmosExtra().Count();
                var actions = (List<FloatMenuOption>)AccessTools.Method(typeof(InmatesTab), "Actions").Invoke(null, new object[] { p, c });
                int funds0 = g.ledger.funds, transferred0 = g.stats.transferredOut;
                InmateUtility.TransferOut(p, c);
                bool charged = g.ledger.funds != funds0 || g.stats.transferredOut != transferred0 || g.shuttle.OutboundOf(p) is not { kind: OutboundKind.Released };
                // A rearrest that falls due while they stand on the pad waits; once they are gone it comes.
                var due = new Rearrest { name = p.LabelShort, pawn = p, dueTick = ShNow - 1 };
                g.rearrests.Add(due);
                ReleaseSystem.CheckRearrests(g);
                bool waited = g.rearrests.Contains(due);
                g.shuttle.Forget(p);
                p.DeSpawn();
                Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                r.Add(() =>
                {
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                });
                ReleaseSystem.CheckRearrests(g);
                bool came = !g.rearrests.Contains(due);
                RPLog.Message($"[RimPrison:test] shuttle on the way out: payments {pay0}->{pay1} for {inmates0}->{inmates1}, inmates counted {count0}->{count1}, gang leader {(gang == null ? "(no free gang to test)" : led0 + "->" + led1)}, gizmos {gizmos}, tab actions {actions.Count}, charged again {charged}, rearrest waited {waited} then came {came}");
                if (inmates1 != inmates0 - 1 || pay1 >= pay0 && pay0 > 0 && p.Inmate().security.dailyPayment > 0) problems0.Add($"shuttle on the way out: the daily payment still counts the one waiting for the shuttle (payments {pay0}->{pay1}, inmates {inmates0}->{inmates1})");
                if (count1 != count0 - 1) problems0.Add($"shuttle on the way out: the intake still counts them as taking up room ({count0}->{count1})");
                if (gang != null && (!led0 || theyLead1)) problems0.Add($"shuttle on the way out: a gang leader waiting for the shuttle still holds the post ({led0}->{led1}, they lead {theyLead1}; marked leaders now: {leaders1})");
                if (gizmos != 0) problems0.Add($"shuttle on the way out: {gizmos} inmate commands are offered on somebody on the way out");
                if (actions.Count != 1) problems0.Add($"shuttle on the way out: the Inmates tab offers {actions.Count} actions (the file only) on somebody on the way out");
                if (charged) problems0.Add("shuttle on the way out: Transfer Out was charged (or counted, or changed their kind) on somebody already on the way out");
                if (!waited) problems0.Add("shuttle on the way out: a rearrest was made of somebody still standing on the pad");
                if (!came) problems0.Add("shuttle on the way out: the rearrest did not come once they had left");
            });
        }

        // ---- 4: needs and death while waiting ------------------------------------------------------------------------------

        /// <summary>Waiting for the shuttle they eat when hungry (vanilla's own food search), and a death while they wait is not a death in custody.</summary>
        private static void CheckShuttleRegWaitingNeeds(Map map)
        {
            Guarded("shuttle: waiting for it, they eat and are not in custody", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "waiting needs")) return;
                Pawn p = Throwaway(map, r), q = Throwaway(map, r);
                ShuttleServed(p, r);
                ShuttleServed(q, r);
                Rand.PushState(11);
                try
                {
                    ReleaseSystem.Release(g, p, record: false);
                    ReleaseSystem.Release(g, q, record: false);
                }
                finally
                {
                    Rand.PopState();
                }
                // Hungry, with a meal lying beside them.
                Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                GenPlace.TryPlaceThing(meal, p.Position, map, ThingPlaceMode.Near);
                r.Add(() =>
                {
                    if (!meal.Destroyed) meal.Destroy(DestroyMode.Vanish);
                });
                p.needs.food.CurLevelPercentage = 0.08f;
                Job job = new JobGiver_Outbound().TryIssueJobPackage(p, default).Job;
                bool eats = job?.def == JobDefOf.Ingest;
                // Fed, nothing pressing: they wait (in holding, else by the pad), they do not wander off the map.
                p.needs.food.CurLevelPercentage = 0.9f;
                Job calm = new JobGiver_Outbound().TryIssueJobPackage(p, default).Job;
                // A death while they wait is not a death in custody: not in the books, not in the grade, no inquiry.
                int deaths0 = g.stats.deaths, today0 = g.today.deaths, inquiries0 = g.stats.inquiries;
                q.Kill(null);
                r.Add(() =>
                {
                    if (q.Corpse != null && !q.Corpse.Destroyed) q.Corpse.Destroy(DestroyMode.Vanish);
                });
                bool custody = g.stats.deaths != deaths0 || g.today.deaths != today0 || g.stats.inquiries != inquiries0;
                RPLog.Message($"[RimPrison:test] shuttle waiting needs: hungry with a meal beside them - {job?.def?.defName ?? "no job"} (eats {eats}); fed - {calm?.def?.defName ?? "no job"}; died waiting counted as a custody death {custody}");
                if (!eats) problems0.Add($"shuttle waiting needs: a hungry inmate waiting for the shuttle with a meal beside them gets {job?.def?.defName ?? "no job"}, not a meal (the node above vanilla's needs gave them a wait every time)");
                if (calm == null || calm.def == JobDefOf.Ingest) problems0.Add($"shuttle waiting needs: a fed inmate waiting for the shuttle gets {calm?.def?.defName ?? "no job"} (a wait, or a walk to holding or the pad, was expected)");
                if (custody) problems0.Add("shuttle waiting needs: somebody who died waiting for the shuttle, already released, was counted as a death in custody (deaths, the day's grade, an inquiry)");
            });
        }

        // ---- 2: the stay and the unloading --------------------------------------------------------------------------------

        /// <summary>A daily shuttle with more passengers and crates than the stay's time allows: it stays until everything is unloaded; the grant and the admissions are booked, the orders delivered.</summary>
        private static void CheckShuttleRegUnloadOutlastsStay(Map map)
        {
            Guarded("shuttle: unloading outlasts the stay", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "unloading outlasts the stay")) return;
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(s => Unlocks.IsUnlocked(s) && !s.wingOnly).OrderByDescending(s => s.intakeGrant).First();
                var before = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                r.Add(() =>
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        if (!before.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                int steel = ThingDefOf.Steel.stackLimit * 3;
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = steel, cost = 0, arriveTick = ShNow - 1 });
                int number0 = g.intake.nextInmateNumber, admitted0 = g.stats.admitted;
                (int perDay0, int lastRun0) = (g.intake.PerDay(sec), g.intake.lastRunDay);
                g.intake.perDay[sec] = 0;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                if (v?.shuttle == null)
                {
                    problems0.Add($"shuttle unloading: no shuttle called ({v?.Describe()}, hold {v?.hold}, the pad is blocked by {ShuttleSystem.PadBlocker(map)?.LabelShort ?? "nothing"} at {ShuttleSystem.PadBlocker(map)?.Position})");
                    return;
                }
                const int Aboard = 5;
                for (int i = 0; i < Aboard; i++)
                {
                    Pawn p = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                    if (v.shuttle.passengers.TryAdd(p, canMergeWithExistingStacks: false)) v.rosterSize++;
                    else p.Destroy();
                }
                g.intake.perDay[sec] = perDay0;
                g.intake.lastRunDay = lastRun0;
                int roster = v.rosterSize;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add($"shuttle unloading: it never landed ({v.Describe()})");
                    return;
                }
                r.Add(() =>
                {
                    foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { } c && c.inmateNumber >= number0).ToList()) ShuttleRemovePawn(p);
                });
                // The stay is over before the unloading is: lift-off in 150 ticks.
                v.departTick = ShNow + 150;
                int landed = v.landedTick;
                ShuttleTicks(() => g.shuttle.visit != v, 6000);
                int grant = r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_IntakeGrants).Sum(e => e.amount);
                RPLog.Message($"[RimPrison:test] shuttle unloading outlasts the stay: {v.steppedOff}/{roster} stepped off, {v.cargoUnits}/{steel} unloaded, grant {v.grant} (books {grant}), admitted {g.stats.admitted - admitted0}, the visit lasted {ShNow - landed} ticks, back in the office {g.supply.pending.Sum(o => o.count)}, carried over {g.shuttle.carriedOver.Count}");
                if (v.steppedOff != roster || v.cargoUnits != steel) problems0.Add($"shuttle unloading: the shuttle left with {roster - v.steppedOff} passengers and {steel - v.cargoUnits} steel not unloaded (the stay ran out at lift-off)");
                if (grant != v.grant || g.stats.admitted != admitted0 + v.steppedOff) problems0.Add($"shuttle unloading: the grant booked {grant} of {v.grant}, admissions counted {g.stats.admitted - admitted0} of {v.steppedOff}");
                if (g.shuttle.visit == v) problems0.Add("shuttle unloading: the shuttle never lifted off");
                g.shuttle.carriedOver.Clear();
            });
        }

        /// <summary>At the hard cap with passengers and crates still aboard: the orders go back to be delivered next time, the passengers come first on the next roster, the grant of those who stepped off is in the books.</summary>
        private static void CheckShuttleRegUnloadCap(Map map)
        {
            Guarded("shuttle: what is left at the cap goes back", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "unload cap")) return;
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(s => Unlocks.IsUnlocked(s) && !s.wingOnly).OrderByDescending(s => s.intakeGrant).First();
                int steel = ThingDefOf.Steel.stackLimit * 4;
                const int cost = 400;
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = steel, cost = cost, arriveTick = ShNow - 1 });
                int number0 = g.intake.nextInmateNumber;
                (int perDay0, int lastRun0) = (g.intake.PerDay(sec), g.intake.lastRunDay);
                g.intake.perDay[sec] = 0;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                if (v?.shuttle == null)
                {
                    problems0.Add($"shuttle cap: no shuttle called ({v?.Describe()}, hold {v?.hold}, the pad is blocked by {ShuttleSystem.PadBlocker(map)?.LabelShort ?? "nothing"} at {ShuttleSystem.PadBlocker(map)?.Position})");
                    return;
                }
                var aboard = new List<Pawn>();
                for (int i = 0; i < 6; i++)
                {
                    Pawn p = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                    if (v.shuttle.passengers.TryAdd(p, canMergeWithExistingStacks: false)) aboard.Add(p);
                    else p.Destroy();
                }
                v.rosterSize = aboard.Count;
                g.intake.perDay[sec] = perDay0;
                g.intake.lastRunDay = lastRun0;
                r.Add(() =>
                {
                    foreach (Pawn p in aboard)
                        if (!p.Destroyed && !p.Spawned) p.Destroy();
                    g.shuttle.carriedOver.Clear();
                });
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add($"shuttle cap: it never landed ({v.Describe()})");
                    return;
                }
                r.Add(() =>
                {
                    foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { } c && c.inmateNumber >= number0).ToList()) ShuttleRemovePawn(p);
                });
                // The hard cap comes in 200 ticks: two passengers, perhaps three, step off in that time, and no crate is set down.
                v.landedTick = ShNow - ShuttleSystem.StayTicks(v) - ShuttleSystem.OvertimeTicks + 200;
                v.departTick = ShNow + 100;
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                int left = aboard.Count - v.steppedOff, pending = g.supply.pending.Where(o => o.thing == ThingDefOf.Steel).Sum(o => o.count), pendingCost = g.supply.pending.Where(o => o.thing == ThingDefOf.Steel).Sum(o => o.cost);
                int grant = r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_IntakeGrants).Sum(e => e.amount);
                bool carried = g.shuttle.carriedOver.Count == left && aboard.Where(p => !p.Spawned).All(p => g.shuttle.carriedOver.Contains(p) && Find.WorldPawns.Contains(p));
                RPLog.Message($"[RimPrison:test] shuttle cap: {v.steppedOff}/{aboard.Count} stepped off, {left} carried over {carried}, steel back in the office {pending}/{steel} (paid {pendingCost}/{cost}), grant {v.grant} booked {grant}");
                if (left <= 0 || pending + v.cargoUnits != steel) problems0.Add($"shuttle cap: {left} passengers left aboard and {steel - v.cargoUnits} steel undelivered, {pending} in the office again");
                if (!carried) problems0.Add($"shuttle cap: the passengers who never stepped off ({left}) are not kept for the next roster ({g.shuttle.carriedOver.Count} carried over)");
                if (v.cargoUnits == 0 && pendingCost != cost || pendingCost > cost) problems0.Add($"shuttle cap: the orders back in the office carry a price of {pendingCost} of {cost} (nothing was set down)");
                if (grant != v.grant || v.grant <= 0 && v.steppedOff > 0 && sec.intakeGrant > 0) problems0.Add($"shuttle cap: the grant of those who did step off is {v.grant}, booked {grant}");
                // The next daily shuttle's roster has them first (when the prison has room for any).
                (int perDayNext0, bool over0) = (g.intake.PerDay(sec), g.intake.allowOvercrowding);
                g.intake.perDay[sec] = 50;
                g.intake.allowOvercrowding = true;
                List<Pawn> next = IntakeSystem.GenerateRoster(g, map, out _, out _, 2);
                g.intake.perDay[sec] = perDayNext0;
                g.intake.allowOvercrowding = over0;
                r.Add(() =>
                {
                    foreach (Pawn p in next)
                        if (!p.Destroyed && !p.Spawned) p.Destroy();
                });
                bool first = next.Count == 0 || next.Take(Math.Min(left, 2)).All(p => aboard.Contains(p));
                RPLog.Message($"[RimPrison:test] shuttle cap: next roster of {next.Count} (capped at 2), the carried-over first {first}");
                if (!first) problems0.Add("shuttle cap: new inmates were generated for the next roster ahead of those the last shuttle could not unload");
                if (next.Count > 2) problems0.Add($"shuttle cap: the roster is capped at 2 and has {next.Count}");
            });
        }

        // ---- 5: the stowaway, rolled after pad duty has started ------------------------------------------------------------

        /// <summary>
        /// The stowaway is not rolled at the touch-down (no guard is on the pad yet, whatever the setting): it is rolled once pad duty has had time to start, and a guard
        /// on the pad stops the runner when they reach the ramp.
        /// </summary>
        private static void CheckShuttleRegStowawayTiming(Map map)
        {
            Guarded("shuttle: the stowaway meets the guard on the pad", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "stowaway timing")) return;
                // (A guard whose own shift covers the next three hours: the hourly shift pass rewrites timetables, and a guard whose shift ended walked home off the pad.)
                int hour0 = GenLocalDate.HourOfDay(map);
                bool OnDutyAhead(Pawn x) => x.timetable == null || Enumerable.Range(0, 3).All(k => x.timetable.GetAssignment((hour0 + k) % 24) != TimeAssignmentDefOf.Sleep);
                Pawn guard = map.mapPawns.FreeColonistsSpawned.Where(x => GuardUtility.IsGuard(x) && !x.Downed && !x.InMentalState).OrderByDescending(OnDutyAhead).FirstOrDefault();
                if (guard == null || !ShuttleSystem.HasPad(map))
                {
                    problems0.Add($"stowaway timing: a guard on site {guard != null}, a pad {ShuttleSystem.HasPad(map)}");
                    return;
                }
if (guard.needs?.rest != null) guard.needs.rest.CurLevel = 1f;
                Pawn forced0 = ShuttleSystem.ForceStowawayOnlyFor;
                g.shuttle.padGuards = 1;
                r.Add(() =>
                {
                    ShuttleSystem.ForceStowawayOnlyFor = forced0;
                    g.shuttle.stowaway = null;
                    guard.jobs?.EndCurrentJob(JobCondition.InterruptForced);
                });
                int caught0 = g.stats.stowawaysCaught, away0 = g.stats.stowawaysAway;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    problems0.Add("stowaway timing: the shuttle never landed");
                    return;
                }
                // At the touch-down nobody has run yet, and nobody has been rolled for.
                bool rolledAtLanding = v.stowawayRolled || g.shuttle.stowaway != null;
                // The runner, made now: one whose own hour (CompInmate.Hourly, at the pawn's hash tick) does not come between here and the roll. An inmate on open ground by the pad is outside the walls,
                // and the hour's escape check (EscapeUtility.HourlyCheck) sends anyone outside for the map edge at once: in a quarter of the runs the runner bolted on their own before the roll. Put back
                // calm the tick before the roll, they were lost all the same when the hour came in the roll's own tick (pawns tick before the shuttle): "the roll never came after the delay (rolled True,
                // stowaway )", amends-am1b, the runner on JobGiver_EscapeAttempt's walk to the map edge. And a guard on the pad who goes to subdue a bolting runner leaves the ramp unwatched by the
                // time the real run comes - most likely standing-so2a's "not stopped at the ramp (caught 0, away 1)", the guard on RP_Subdue and off the post from 200 ticks before the roll (the line
                // did not say on whom: it does now).
                int rollTick = v.landedTick + ShuttleSystem.StowawayDelayTicks;
                bool HourComesBy(Pawn x, int until)
                {
                    int hash = x.thingIDNumber.HashOffset();
                    for (int t = ShNow; t <= until; t++)
                    {
                        // (As vanilla's IsHashIntervalTick reads it, int sum and all; pawns tick their interval in batches of up to 15 ticks: a margin either side.)
                        int phase = Math.Abs(unchecked(t + hash) % GenDate.TicksPerHour);
                        if (phase < 20 || phase > GenDate.TicksPerHour - 20) return true;
                    }
                    return false;
                }
                Pawn runner = null;
                for (int tries = 0; tries < 20 && runner == null; tries++)
                {
                    Pawn candidate = Throwaway(map, r, spawn: false);
                    if (HourComesBy(candidate, rollTick + 60)) candidate.Destroy(DestroyMode.Vanish);
                    else runner = candidate;
                }
                if (runner == null)
                {
                    problems0.Add("stowaway timing: no runner of twenty whose own hour does not come before the roll (the hash offsets: the check's own fault)");
                    return;
                }
                IntVec3 yard = PrisonBuilder.Origin(map) + new IntVec3(12, 0, 4);
                GenSpawn.Spawn(runner, yard, map);
                InmateGenerator.MakeInmate(runner, null);
                CompInmate rc = runner.Inmate();
                rc.processed = true;
                rc.RefreshActiveFlag();
                ShuttleSystem.ForceStowawayOnlyFor = runner;
                r.Add(() =>
                {
                    runner.mindState?.mentalStateHandler?.Reset();
                    g.fugitives.RemoveAll(f => f.pawn == runner);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(runner);
                });
                // The guard takes the post (set down by the pad, as the pad duty check does).
                IntVec3 ramp = v.shuttle.InteractionCell;
                if (CellFinder.TryFindRandomCellNear(ramp, map, 4, c => c.Standable(map) && c.GetFirstPawn(map) == null && !v.shuttle.OccupiedRect().Contains(c), out IntVec3 nearPad))
                {
                    guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    guard.pather.StopDead();
                    guard.Position = nearPad;
                    guard.Notify_Teleported(true, true);
                }
                IntVec3 post = ShuttleSystem.PostCell(guard, v.shuttle);
                if (post.IsValid) guard.jobs.StartJob(JobMaker.MakeJob(RPShuttleDefOf.RP_PadDuty, v.shuttle, post), JobCondition.InterruptForced);
                // (The first time the guard is off the post from here to the end, and on what: a guard who answered something else leaves the ramp unwatched, and the line says so.)
                string guardLeft = null;
                void WatchGuard()
                {
                    if (guardLeft == null && guard.CurJobDef != RPShuttleDefOf.RP_PadDuty)
                        guardLeft = $"{guard.CurJobDef?.defName ?? "no job"} on {guard.CurJob?.targetA.Thing?.LabelShort ?? guard.CurJob?.targetA.Cell.ToString()} at +{ShNow - v.landedTick} (the roll at +{ShuttleSystem.StowawayDelayTicks})";
                }
                // The runner waits a couple of cells from the ramp (the guards who answer an escape are not there yet).
                CellRect hullRect = v.shuttle.OccupiedRect();
                if (CellFinder.TryFindRandomCellNear(ramp, map, 5, c => c.Standable(map) && !hullRect.Contains(c) && c.DistanceTo(ramp) >= 2f && c.GetFirstPawn(map) == null, out IntVec3 spot))
                {
                    runner.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    runner.pather.StopDead();
                    runner.Position = spot;
                    runner.Notify_Teleported(true, true);
                }
                // (Should they still break before the roll - something else than their hour - they are put back, calm and in place, the tick before it, and it is told.)
                string bolted = null;
                ShuttleTicks(() =>
                {
                    WatchGuard();
                    if (bolted == null && runner.InMentalState) bolted = $"{runner.MentalStateDef?.defName} at +{ShNow - v.landedTick} on {runner.CurJobDef?.defName}";
                    return g.shuttle.visit != v || ShNow >= rollTick - 1;
                }, ShuttleSystem.StowawayDelayTicks + 400);
                if (bolted != null) RPLog.Message($"[RimPrison:test] shuttle stowaway timing: the runner broke on their own before the roll ({bolted}), put back calm");
                if (g.shuttle.visit == v && runner.Spawned)
                {
                    runner.mindState.mentalStateHandler.Reset();
                    runner.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    runner.pather.StopDead();
                    if (runner.Position.DistanceTo(ramp) > 6f && CellFinder.TryFindRandomCellNear(ramp, map, 5, c => c.Standable(map) && !hullRect.Contains(c) && c.DistanceTo(ramp) >= 2f && c.GetFirstPawn(map) == null, out IntVec3 back))
                    {
                        runner.Position = back;
                        runner.Notify_Teleported(true, true);
                    }
                }
                ShuttleTicks(() =>
                {
                    WatchGuard();
                    return g.shuttle.visit != v || v.stowawayRolled;
                }, 400);
                bool guardedWhenRolled = ShuttleSystem.Guarded(v);
                bool rolled = v.stowawayRolled && g.shuttle.stowaway == runner;
                if (!rolled)
                    RPLog.Message($"[RimPrison:test] shuttle stowaway timing: the roll did not pick the runner - active {rc.activeFlag}, processed {rc.processed}, downed {runner.Downed}, awake {runner.Awake()}, mental {runner.InMentalState}, arrival {ShuttleSystem.IsArrival(runner)}, punishment {rc.pendingPunishmentHours}, forced {rc.forcedActivity != null}, death row {DeathRowSystem.Ext(rc) != null}, escorted {ParoleScene.IsEscorted(runner)}, "
                        + $"distance {runner.Position.DistanceTo(ramp):F1}, can reach {runner.CanReach(ramp, PathEndMode.OnCell, Danger.Deadly, canBashDoors: true, canBashFences: false, TraverseMode.PassDoors)}, spawned {runner.Spawned} at {runner.Position}, on {runner.CurJobDef?.defName}, own hour by the roll {HourComesBy(runner, ShNow)}"
                        + (map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.carryTracker?.CarriedThing == runner) is { } carrier ? $", carried by {carrier.LabelShort} doing {carrier.CurJobDef?.defName} (from {carrier.CurJob?.jobGiver?.GetType().Name ?? carrier.CurJob?.workGiverDef?.defName}), free prisoner beds {IntakeSystem.FreeBedCount(map)}" : ""));
                // (Held where they run to: the ramp, where a guard is.)
                string leftBeforeTheRun = guardLeft;
                int ran = ShuttleTicksTraced(() =>
                {
                    WatchGuard();
                    return g.stats.stowawaysCaught != caught0 || !runner.Spawned || g.shuttle.visit != v;
                }, 1800, runner, "stowaway timing");
                // Stopped at the ramp, or taken by the arm by the guard standing beside it before they got there (a warden answers an escape): either way they did not get aboard.
                bool seized = !runner.Spawned && runner.ParentHolder is Pawn_CarryTracker;
                bool stopped = g.stats.stowawaysCaught == caught0 + 1 && runner.Spawned && g.stats.stowawaysAway == away0 || seized && g.stats.stowawaysAway == away0;
                RPLog.Message($"[RimPrison:test] shuttle stowaway timing: rolled at the touch-down {rolledAtLanding}, rolled later {rolled} (a guard on the pad by then {guardedWhenRolled}), stopped at the ramp {stopped} (away {g.stats.stowawaysAway - away0}); after {ran} ticks the runner is {(runner.Spawned ? "at " + runner.Position : "gone")}, on {runner.CurJobDef?.defName}, the visit {(g.shuttle.visit == v ? v.phase.ToString() : "over")}, guard on {guard.CurJobDef?.defName} at {guard.Position}, lastGuardTick {v.lastGuardTick} (now {ShNow}), the guard off the post first {guardLeft ?? "never"}, the runner broke before the roll {bolted ?? "no"}");
                if (rolledAtLanding) problems0.Add("shuttle stowaway timing: the stowaway was rolled at the touch-down, before any guard could be on the pad");
                if (!rolled) problems0.Add($"shuttle stowaway timing: the roll never came after the delay (rolled {v.stowawayRolled}, stowaway {g.shuttle.stowaway?.LabelShort}, the runner mental {runner.MentalStateDef?.defName} on {runner.CurJobDef?.defName}, broke before the roll {bolted ?? "no"})");
                if (rolled && guardedWhenRolled && !stopped) problems0.Add($"shuttle stowaway timing: with a guard on the pad the runner was not stopped at the ramp (caught {g.stats.stowawaysCaught - caught0}, away {g.stats.stowawaysAway - away0}, mental {runner.MentalStateDef?.defName}; the guard off the post before the run {leftBeforeTheRun ?? "never"}, first at all {guardLeft ?? "never"}, last on the pad {ShNow - v.lastGuardTick} ticks ago; the runner broke before the roll {bolted ?? "no"})");
                runner.mindState?.mentalStateHandler?.Reset();
                guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
                EndShuttleVisit(g, map);
                // Not the condemned, nor anyone a guard has by the arm: they are no candidates.
                SecurityLevelDef death = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(s => s.GetModExtension<DeathRowExtension>() != null);
                if (death != null)
                {
                    Pawn condemned = Throwaway(map, r, death);
                    condemned.Inmate().processed = true;
                    condemned.Inmate().RefreshActiveFlag();
                    ShuttleSystem.CallCargo(g, map);
                    ShuttleVisit w = g.shuttle.visit;
                    ShuttleTicks(() => g.shuttle.visit != w || w.phase == ShuttlePhase.Landed, 1500);
                    if (g.shuttle.visit == w && w.phase == ShuttlePhase.Landed)
                    {
                        Pawn who = g.shuttle.TryStowaway(g, map, w, condemned);
                        if (who != null) problems0.Add("shuttle stowaway timing: an inmate on death row was picked as the stowaway");
                    }
                    EndShuttleVisit(g, map);
                }
            });
        }

        // ---- 6, 13, 18: sites ---------------------------------------------------------------------------------------------

        /// <summary>Plants do not rule out a landing site; the open-ground site stays put while pawns cross it; a pad with a roof is no landing site (an alert says why).</summary>
        private static void CheckShuttleRegSites(Map map)
        {
            Guarded("shuttle: sites", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "sites")) return;
                IntVec3 patch = ShuttleOpenPatch(map, 1);
                if (!patch.IsValid)
                {
                    RPLog.Message("[RimPrison:test] shuttle sites: no open patch of ground on this map, not checked");
                    return;
                }
                // Plants on the hull's spot and round it: the landing clears them.
                bool fits0 = ShuttleSystem.SiteFits(map, patch, allowZones: true);
                IntVec3 ramp = ShuttleSystem.Footprint(patch, out CellRect rect);
                var plants = new List<Thing>();
                foreach (IntVec3 c in rect.ExpandedBy(1))
                {
                    if (!c.Standable(map) || c.GetPlant(map) != null) continue;
                    Thing grass = ThingMaker.MakeThing(ThingDefOf.Plant_Grass);
                    GenSpawn.Spawn(grass, c, map);
                    plants.Add(grass);
                }
                r.Add(() =>
                {
                    foreach (Thing t in plants)
                        if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                bool fitsGrassy = ShuttleSystem.SiteFits(map, patch, allowZones: true);
                RPLog.Message($"[RimPrison:test] shuttle sites: a patch at {patch} fits {fits0}, with {plants.Count} plants on it {fitsGrassy}");
                if (!fits0 || !fitsGrassy) problems0.Add($"shuttle sites: grass on open ground rules out a landing site (fits bare {fits0}, with {plants.Count} plants {fitsGrassy})");
                // The open-ground site stays the site while it is open ground, whoever walks over it: reception does not move.
                g.shuttle.ForgetSite();
                IntVec3 first = g.shuttle.FallbackSite(map);
                if (first.IsValid)
                {
                    Pawn walker = Throwaway(map, r, spawn: false);
                    ShuttleSystem.Footprint(first, out CellRect hull);
                    GenSpawn.Spawn(walker, hull.CenterCell, map);
                    IntVec3 second = g.shuttle.FallbackSite(map);
                    IntVec3 reception = g.shuttle.ReceptionAnchor(map);
                    walker.DeSpawn();
                    RPLog.Message($"[RimPrison:test] shuttle sites: the fallback site {first}, with somebody standing on it {second}, reception at {reception}");
                    if (second != first) problems0.Add($"shuttle sites: the open-ground site moved from {first} to {second} when somebody stood on it (reception would move)");
                    g.shuttle.ForgetSite();
                }
            });
        }

        /// <summary>
        /// No pad and no clear ground: the shuttle holds ("no pad" in the status line and the letters), the map is swept for ground once an hour (not every minute), and it
        /// is called off after six hours.
        /// </summary>
        private static void CheckShuttleRegNoSiteHold(Map map)
        {
            Guarded("shuttle: no pad and no ground", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "no site")) return;
                List<Building> pads = ShuttleSystem.Pads(map).ToList();
                foreach (Building pad in pads) pad.DeSpawn();
                bool noGround0 = ShuttleSystem.NoOpenGroundForTests;
                r.Add(() =>
                {
                    ShuttleSystem.NoOpenGroundForTests = noGround0;
                    foreach (Building pad in pads)
                        if (!pad.Spawned) GenSpawn.Spawn(pad, pad.Position, map, pad.Rotation, WipeMode.VanishOrMoveAside);
                    g.shuttle.ForgetSite();
                });
                ShuttleSystem.NoOpenGroundForTests = true;
                g.shuttle.ForgetSite();
                int sweeps0 = ShuttleSystem.SiteSweeps, cancelled0 = g.stats.shuttlesCancelled;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                // Two hours of ticks: a sweep when it is called and one more an hour on, not one every minute.
                ShuttleTicks(() => false, GenDate.TicksPerHour + 400);
                int sweeps = ShuttleSystem.SiteSweeps - sweeps0;
                bool held = g.shuttle.visit == v && v.phase == ShuttlePhase.Holding && v.hold == ShuttleHold.NoSite;
                string status = held ? ShuttleSystem.StatusLine(v) : "";
                bool statusSaysIt = status == "RP_ShuttleStatusHolding_NoSite".Translate().ToString();
                string delayed = "RP_ShuttleDelayedText_NoSite".Translate("RP_ShuttleTheDaily".Translate()).ToString();
                bool letter = r.NewLetters.Any(l => l is ChoiceLetter cl && cl.Label.ToString() == "RP_ShuttleDelayedLabel".Translate().ToString() && cl.Text.ToString() == delayed);
                RPLog.Message($"[RimPrison:test] shuttle no site: held for no ground {held}, {sweeps} sweeps in {GenDate.TicksPerHour + 400} ticks, status line {statusSaysIt}, letter {letter}");
                if (!held) problems0.Add($"shuttle no site: with no pad and no ground the shuttle should hold for no site ({v?.Describe()})");
                if (sweeps > 2) problems0.Add($"shuttle no site: the map was swept for ground {sweeps} times in an hour and a bit (once an hour at most: twice)");
                if (held && !statusSaysIt) problems0.Add($"shuttle no site: the status line says '{status}', not that there is no pad and no ground");
                if (!letter) problems0.Add("shuttle no site: no letter told the warden the shuttle has no pad and no ground (the hold was silent)");
                if (g.shuttle.visit == v)
                {
                    v.calledTick = ShNow - ShuttleSystem.MaxHoldTicks - 1;
                    ShuttleTicks(() => g.shuttle.visit != v, 400);
                    bool off = g.shuttle.visit != v && g.stats.shuttlesCancelled == cancelled0 + 1;
                    string cancelledText = "RP_ShuttleCancelledText_NoSite".Translate("RP_ShuttleTheDaily".Translate()).ToString();
                    bool offLetter = r.NewLetters.Any(l => l is ChoiceLetter cl && cl.Text.ToString() == cancelledText);
                    if (!off || !offLetter) problems0.Add($"shuttle no site: after six hours it was called off {off}, with the letter that says why {offLetter}");
                }
                ShuttleSystem.NoOpenGroundForTests = noGround0;
            });
        }

        /// <summary>A pad with a roof over its hull or the cell round it is not a landing site (the shuttle comes down on open ground), and an alert says so.</summary>
        private static void CheckShuttleRegRoofedPad(Map map)
        {
            Guarded("shuttle: a roofed pad", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "roofed pad")) return;
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad == null)
                {
                    problems0.Add("roofed pad: no pad");
                    return;
                }
                ShuttleSystem.Footprint(pad.Position, out CellRect rect);
                IntVec3 cell = rect.ExpandedBy(1).Cells.FirstOrDefault(c => c.InBounds(map) && !c.Roofed(map) && c.Standable(map));
                if (!cell.IsValid)
                {
                    problems0.Add("roofed pad: no cell round the pad to roof");
                    return;
                }
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                r.Add(() =>
                {
                    map.roofGrid.SetRoof(cell, null);
                    g.shuttle.ForgetSite();
                });
                IntVec3 anchor = ShuttleSystem.Anchor(map, out bool zone);
                ShuttleSystem.PadProblem problem = ShuttleSystem.ProblemOf(map, pad, anchor, zone);
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                bool alert = PrisonAlerts.PadProblem == ShuttleSystem.PadProblem.Roofed && new Alert_RP_PadUnusable().GetReport().active;
                g.shuttle.ForgetSite();
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                bool offPad = v != null && v.phase == ShuttlePhase.Landed && !v.onPad && v.shuttle.Position != pad.Position;
                IntVec3 reception = g.shuttle.ReceptionAnchor(map);
                bool receptionAway = reception.IsValid && v != null && v.shuttle != null && reception.DistanceTo(v.shuttle.Position) <= 4f;
                RPLog.Message($"[RimPrison:test] shuttle roofed pad: problem {problem}, alert {alert}, landed off the pad {offPad}, reception by it {receptionAway}");
                if (problem != ShuttleSystem.PadProblem.Roofed) problems0.Add($"shuttle roofed pad: a roof over the pad's margin was not seen as a problem ({problem})");
                if (!alert) problems0.Add("shuttle roofed pad: no alert says the pad is roofed");
                if (!offPad) problems0.Add($"shuttle roofed pad: the shuttle came down on a roofed pad ({v?.Describe()})");
                if (offPad && !receptionAway) problems0.Add("shuttle roofed pad: reception is not by the place the shuttle set down");
                EndShuttleVisit(g, map);
            });
        }

        // ---- 8: stuck arrivals ----------------------------------------------------------------------------------------------

        /// <summary>A new inmate who cannot get in stays on the way in after ten hours (the gates stay open to them) and is reported; once inside the walk ends.</summary>
        private static void CheckShuttleRegStuckArrivals(Map map)
        {
            Guarded("shuttle: an arrival stuck outside", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "stuck arrival")) return;
                PrisonMap pm = PrisonMap.Of(map);
                IntVec3 patch = ShuttleOpenPatch(map, 1);
                IntVec3 holding = pm.zones.CellsOf(RPDefOf.RP_Holding).FirstOrDefault();
                if (!patch.IsValid || !holding.IsValid)
                {
                    RPLog.Message($"[RimPrison:test] shuttle stuck arrival: no open ground {patch.IsValid} or no holding {holding.IsValid}, not checked");
                    return;
                }
                Pawn p = Throwaway(map, r, spawn: false);
                GenSpawn.Spawn(p, patch, map);
                InmateGenerator.MakeInmate(p, null);
                r.Add(() => ShuttleRemovePawn(p));
                g.shuttle.AddArrival(p);
                ArrivalEntry e = g.shuttle.ArrivalOf(p);
                e.sinceTick = ShNow - ShuttleSystem.ArrivalTimeoutTicks - 1;
                bool outside = !ShuttleSystem.IsInside(p);
                if (!outside)
                {
                    RPLog.Message("[RimPrison:test] shuttle stuck arrival: the open ground by the compound is not cut off from the map's edge here, not checked");
                    return;
                }
                g.shuttle.Prune(map);
                bool kept = g.shuttle.ArrivalOf(p) != null && p.Inmate().inTransit;
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                bool reported = g.shuttle.StuckArrivals(map).Contains(p) && PrisonAlerts.StuckArrivals.Contains(p) && new Alert_RP_StuckArrivals().GetReport().active;
                // Inside (put in holding): the walk is over.
                p.pather.StopDead();
                p.Position = holding;
                p.Notify_Teleported(true, true);
                g.shuttle.Prune(map);
                bool ended = g.shuttle.ArrivalOf(p) == null && !p.Inmate().inTransit;
                RPLog.Message($"[RimPrison:test] shuttle stuck arrival: outside the perimeter {outside}, ten hours on still on the way in {kept}, reported {reported}, inside the walk ends {ended}");
                if (!kept) problems0.Add("shuttle stuck arrival: after ten hours outside the perimeter they were taken off the way in (the gates close to them for good)");
                if (!reported) problems0.Add("shuttle stuck arrival: nothing reports an arrival who cannot get in (no alert)");
                if (!ended) problems0.Add("shuttle stuck arrival: once inside they are still on the way in");
            });
        }

        // ---- 10: the release counters --------------------------------------------------------------------------------------

        /// <summary>Vanilla's own Release order is not a release the prison counts; the reoffence of a release is counted when it is, when they leave.</summary>
        private static void CheckShuttleRegReleaseCounters(Map map)
        {
            Guarded("shuttle: release counters", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "release counters")) return;
                Pawn order = Throwaway(map, r), served = Throwaway(map, r);
                ShuttleServed(order, r);
                ShuttleServed(served, r);
                int released0 = g.stats.released, reoffended0 = g.stats.reoffended;
                // (A throwaway can be rolled innocent, and an innocent one never reoffends: the roll below would not be asked.)
                CompInmate servedFile = served.Inmate();
                bool wasInnocent = servedFile.innocent;
                servedFile.innocent = false;
                r.Add(() => servedFile.innocent = wasInnocent);
                // The warden's own Release order: they go by the shuttle, and are not a release.
                GenGuest.PrisonerRelease(order);
                bool outbound = g.shuttle.OutboundOf(order) is { counted: false };
                ShuttleSystem.Notify_Left(order);
                bool uncounted = g.stats.released == released0;
                // A release that is followed by a reoffence: both are counted when they leave, not one a day before the other.
                int seed = Enumerable.Range(1, 400).First(s =>
                {
                    Rand.PushState(s);
                    bool re = Rand.Chance(ParoleSystem.ReoffendChance(served.Inmate()));
                    Rand.PopState();
                    return re;
                });
                Rand.PushState(seed);
                ReleaseSystem.LegacyReoffendRoll = true;
                try
                {
                    ReleaseSystem.Release(g, served, record: false);
                }
                finally
                {
                    ReleaseSystem.LegacyReoffendRoll = false;
                    Rand.PopState();
                }
                bool reoffends = g.shuttle.OutboundOf(served) is { reoffends: true };
                bool notYet = g.stats.released == released0 && g.stats.reoffended == reoffended0;
                ShuttleSystem.Notify_Left(served);
                bool both = g.stats.released == released0 + 1 && g.stats.reoffended == reoffended0 + 1;
                RPLog.Message($"[RimPrison:test] shuttle release counters: vanilla's order registered uncounted {outbound}, not counted when they left {uncounted}; a release with a reoffence (roll {reoffends}): nothing counted before they left {notYet}, both after {both}");
                if (!outbound || !uncounted) problems0.Add($"shuttle release counters: vanilla's own Release order counted as a release (entry uncounted {outbound}, released counted {g.stats.released - released0})");
                if (!reoffends) problems0.Add("shuttle release counters: the reoffence rolled for the release was not kept with it");
                if (!notYet) problems0.Add($"shuttle release counters: before they left, released went up {g.stats.released - released0} and reoffended {g.stats.reoffended - reoffended0}");
                if (!both) problems0.Add($"shuttle release counters: after they left, released went up {g.stats.released - released0} and reoffended {g.stats.reoffended - reoffended0} (1 and 1)");
            });
        }

        // ---- 11: the crates are set down clear of the ramp ----------------------------------------------------------------

        /// <summary>Without a Deliveries zone the crates go to the pad's edge: never on the ramp or the margin kept clear, so the next shuttle finds the pad free.</summary>
        private static void CheckShuttleRegCargoSpots(Map map)
        {
            Guarded("shuttle: crates clear of the ramp", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "cargo spots")) return;
                PrisonMap pm = PrisonMap.Of(map);
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad == null)
                {
                    problems0.Add("cargo spots: no pad");
                    return;
                }
                List<IntVec3> zoneCells = pm.zones.CellsOf(RPDefOf.RP_Deliveries).ToList();
                foreach (IntVec3 c in zoneCells) pm.zones.Set(c, null);
                var before = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                r.Add(() =>
                {
                    foreach (IntVec3 c in zoneCells) pm.zones.Set(c, RPDefOf.RP_Deliveries);
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        if (!before.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                int stacks = 12;
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = ThingDefOf.Steel.stackLimit * stacks, cost = 0, arriveTick = ShNow - 1 });
                if (!ShuttleSystem.CallCargo(g, map))
                {
                    problems0.Add("cargo spots: the cargo run could not be called");
                    return;
                }
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.cargoDone, 6000);
                IntVec3 ramp = v.shuttle.InteractionCell;
                CellRect keepClear = ShuttleSystem.KeepClear(ramp);
                ShuttleDepartIn(v, 30);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                List<Thing> placed = map.listerThings.ThingsOfDef(ThingDefOf.Steel).Where(t => !before.Contains(t)).ToList();
                int inMargin = placed.Count(t => keepClear.Contains(t.Position) || t.Position == ramp);
                bool padFree = ShuttleSystem.FirstBlocker(map, pad.Position) == null;
                RPLog.Message($"[RimPrison:test] shuttle cargo spots: {placed.Count} stacks, {inMargin} on the ramp or the margin kept clear, pad free for the next shuttle {padFree}");
                if (placed.Count < stacks) problems0.Add($"shuttle cargo spots: {placed.Count} of {stacks} stacks were set down");
                if (inMargin > 0) problems0.Add($"shuttle cargo spots: {inMargin} stacks were set down on the ramp or the margin kept clear round it");
                if (!padFree) problems0.Add($"shuttle cargo spots: after unloading the pad is blocked for the next shuttle by {ShuttleSystem.FirstBlocker(map, pad.Position)?.LabelShort}");
                EndShuttleVisit(g, map);
            });
        }

        // ---- 12: the landing moves pawns only where they could walk -----------------------------------------------------------

        /// <summary>A pawn on the hull's spot is set down where they could have walked from where they stood, not through a wall.</summary>
        private static void CheckShuttleRegClearSite(Map map)
        {
            Guarded("shuttle: the landing moves pawns where they could walk", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "clear site")) return;
                IntVec3 center = ShuttleOpenPatch(map, 3);
                if (!center.IsValid)
                {
                    RPLog.Message("[RimPrison:test] shuttle clear site: no open ground for a walled yard, not checked");
                    return;
                }
                IntVec3 ramp = ShuttleSystem.Footprint(center, out CellRect rect);
                // A wall round the hull and the margin of one cell: whoever stands on the hull can walk to the margin only.
                var walls = new List<Thing>();
                CellRect inner = rect.ExpandedBy(1);
                inner.Encapsulate(ramp);
                CellRect outer = inner.ExpandedBy(1);
                foreach (IntVec3 c in outer)
                {
                    if (inner.Contains(c)) continue;
                    Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                    wall.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(wall, c, map);
                    walls.Add(wall);
                }
                r.Add(() =>
                {
                    foreach (Thing w in walls)
                        if (!w.Destroyed) w.Destroy(DestroyMode.Vanish);
                });
                var standing = new List<Pawn>();
                foreach (IntVec3 c in new[] { rect.CenterCell, new IntVec3(rect.minX, 0, rect.minZ), new IntVec3(rect.maxX, 0, rect.maxZ), new IntVec3(rect.minX, 0, rect.maxZ) })
                {
                    Pawn p = Throwaway(map, r, spawn: false);
                    GenSpawn.Spawn(p, c, map);
                    standing.Add(p);
                }
                ShuttleSystem.ClearSite(map, center, RPShuttleDefOf.RP_StateShuttle);
                int outside = standing.Count(p => !inner.Contains(p.Position) || rect.Contains(p.Position));
                RPLog.Message($"[RimPrison:test] shuttle clear site: {standing.Count} pawns on the hull, {outside} put down outside the wall or still on the hull ({string.Join(" ", standing.Select(p => p.Position.ToString()))})");
                if (outside > 0) problems0.Add($"shuttle clear site: {outside} of {standing.Count} pawns on the landing spot were put down outside the wall that shut them in (or left on the hull)");
            });
        }

        // ---- 15: the door rule for arrivals ---------------------------------------------------------------------------------

        /// <summary>A new arrival on the way in passes the gates, but not a staff-only door, nor one held by rioters; a transfer on the way out keeps vanilla's rules for the released.</summary>
        private static void CheckShuttleRegArrivalDoors(Map map)
        {
            Guarded("shuttle: doors for arrivals", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "arrival doors")) return;
                Building_Door door = map.listerThings.ThingsOfDef(ThingDefOf.Door).OfType<Building_Door>().FirstOrDefault(d => d.GetComp<CompPrisonDoor>() != null && d.Spawned);
                if (door == null)
                {
                    RPLog.Message("[RimPrison:test] shuttle arrival doors: no door with a prison door comp on this map, not checked");
                    return;
                }
                CompPrisonDoor comp = door.GetComp<CompPrisonDoor>();
                Pawn p = Throwaway(map, r);
                p.Inmate().processed = true;
                p.Inmate().RefreshActiveFlag();
                CompInmate c = p.Inmate();
                (PrisonDoorMode mode0, bool pass0, DoorSide a0, DoorSide b0, HashSet<int> held0, bool transit0, bool arrival0) = (comp.mode, comp.inmatePass, comp.sideA, comp.sideB, comp.heldCells, c.inTransit, comp.arrivalPass);
                r.Add(() =>
                {
                    comp.arrivalPass = arrival0;
                    comp.mode = mode0;
                    comp.inmatePass = pass0;
                    comp.sideA = a0;
                    comp.sideB = b0;
                    comp.heldCells = held0;
                    c.inTransit = transit0;
                    map.reachability.ClearCache();
                });
                c.inTransit = true;
                comp.inmatePass = false;
                comp.heldCells = null;
                bool Opens(PrisonDoorMode mode, DoorSide a, DoorSide b, bool heldByRioters = false, bool portA = false)
                {
                    comp.mode = mode;
                    comp.sideA = a;
                    comp.sideB = b;
                    // (The verdict is the door pass's: the same rule, with the sides set here; portA: the room on side A is a sally port, a gatehouse.)
                    comp.arrivalPass = CompPrisonDoor.ArrivalPass(mode, a, b, portA, false);
                    comp.heldCells = heldByRioters ? new HashSet<int> { 0 } : null;
                    return door.PawnCanOpen(p);
                }
                bool gate = Opens(PrisonDoorMode.Auto, DoorSide.Outside, DoorSide.InmateSpace);
                bool staffGate = Opens(PrisonDoorMode.StaffOnly, DoorSide.Outside, DoorSide.InmateSpace);
                bool staffInside = Opens(PrisonDoorMode.StaffOnly, DoorSide.InmateSpace, DoorSide.InmateSpace);
                bool kitchen = Opens(PrisonDoorMode.Auto, DoorSide.InmateSpace, DoorSide.Staff);
                bool held = Opens(PrisonDoorMode.Auto, DoorSide.Outside, DoorSide.InmateSpace, heldByRioters: true);
                bool locked = Opens(PrisonDoorMode.Locked, DoorSide.Outside, DoorSide.InmateSpace);
                // The sally port: a gate into the gatehouse (a staff room), and its door on into the prison; not its door into the staff corridor.
                Opens(PrisonDoorMode.StaffOnly, DoorSide.Outside, DoorSide.Staff);
                comp.arrivalPass = CompPrisonDoor.ArrivalPass(PrisonDoorMode.StaffOnly, DoorSide.Outside, DoorSide.Staff, false, true);
                bool intoGatehouse = door.PawnCanOpen(p);
                bool throughGatehouse = Opens(PrisonDoorMode.StaffOnly, DoorSide.Staff, DoorSide.InmateSpace, portA: true);
                bool gatehouseToStaff = Opens(PrisonDoorMode.StaffOnly, DoorSide.Staff, DoorSide.Staff, portA: true);
                if (!intoGatehouse || !throughGatehouse) problems0.Add($"shuttle arrival doors: an arrival cannot get through the sally port (gate into the gatehouse {intoGatehouse}, on into the prison {throughGatehouse})");
                if (gatehouseToStaff) problems0.Add("shuttle arrival doors: an arrival passes from the gatehouse into another staff room");
                c.inTransit = false;
                bool notArriving = Opens(PrisonDoorMode.Auto, DoorSide.Outside, DoorSide.InmateSpace);
                RPLog.Message($"[RimPrison:test] shuttle arrival doors: an arrival passes the gate {gate}, a staff-only gate {staffGate}, a staff-only door inside {staffInside}, a door into a staff room {kitchen}, one held by rioters {held}, a locked one {locked}; an inmate not arriving passes the gate {notArriving}");
                if (!gate || !staffGate) problems0.Add($"shuttle arrival doors: an arrival on the way in cannot pass the gate (open {gate}, set to staff only {staffGate})");
                if (staffInside) problems0.Add("shuttle arrival doors: an arrival passes a door inside the prison that is set to staff only");
                if (kitchen) problems0.Add("shuttle arrival doors: an arrival passes a door into a staff room (a starving arrival would walk into the kitchen)");
                if (held) problems0.Add("shuttle arrival doors: an arrival passes a door held by rioters in a hostage crisis");
                if (locked) problems0.Add("shuttle arrival doors: an arrival passes a locked door");
                if (notArriving) problems0.Add("shuttle arrival doors: an inmate who is not on the way in passes the gate (the arrival rule is for arrivals)");
            });
        }

        // ---- 7-9, 16-19: what is left -------------------------------------------------------------------------------------

        /// <summary>
        /// Smaller things of the review, each by what only the fixed code does: a cancelled blueprint refunds only the crates not yet unloaded, a new inmate in a mental
        /// break is not led by the arm, a released inmate sealed in is not sent to a spot they cannot reach, a transfer leaves the world as a transfer (not a freed
        /// prisoner), a pad held up for a moment by a passer-by is not announced, and the statistics screen has no row for the smuggling that got past the guards.
        /// </summary>
        private static void CheckShuttleRegSmallThings(Map map)
        {
            Guarded("shuttle: the small findings", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "small findings")) return;
                // 17: half unloaded, then cancelled: the refund is of what is still aboard.
                {
                    const int blueprint = 987654, price = 200;
                    int count = ThingDefOf.Steel.stackLimit * 2;
                    var before = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                    r.Add(() =>
                    {
                        foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                            if (!before.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                    });
                    g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = count, cost = price, arriveTick = ShNow - 1, blueprintId = blueprint });
                    if (ShuttleSystem.CallCargo(g, map))
                    {
                        ShuttleVisit v = g.shuttle.visit;
                        ShuttleTicks(() => g.shuttle.visit != v || v.cargoStacks >= 1, 3000);
                        int funds0 = g.ledger.funds;
                        g.supply.CancelForBlueprint(blueprint);
                        int refund = g.ledger.funds - funds0;
                        RPLog.Message($"[RimPrison:test] shuttle small findings: one stack of two unloaded, then cancelled - refund {refund} of {price}");
                        if (v.cargoStacks >= 1 && refund >= price) problems0.Add($"shuttle small findings: cancelling a blueprint with half its crates unloaded refunded the full price ({refund} of {price})");
                        if (v.cargoStacks >= 1 && refund <= 0) problems0.Add($"shuttle small findings: cancelling a blueprint with its crates still aboard refunded nothing ({refund})");
                        EndShuttleVisit(g, map);
                    }
                }
                // 16: a new inmate in a mental break is not taken by the arm.
                {
                    Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => GuardUtility.IsGuard(x));
                    Pawn raving = Throwaway(map, r);
                    // (Out on the open ground, not in holding: a guard is only sent for somebody who is not there yet.)
                    IntVec3 outer = ShuttleOpenPatch(map, 1);
                    if (outer.IsValid)
                    {
                        raving.pather.StopDead();
                        raving.Position = outer;
                        raving.Notify_Teleported(true, true);
                    }
                    raving.Inmate().processed = true;
                    raving.Inmate().RefreshActiveFlag();
                    g.shuttle.AddArrival(raving);
                    r.Add(() => g.shuttle.Forget(raving));
                    var giver = new WorkGiver_EscortArrival { def = DefDatabase<WorkGiverDef>.GetNamed("RP_EscortArrival_WorkGiver") };
                    bool calm = guard != null && giver.HasJobOnThing(guard, raving);
                    bool started = raving.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true);
                    bool taken = guard != null && started && giver.HasJobOnThing(guard, raving);
                    raving.mindState.mentalStateHandler.Reset();
                    RPLog.Message($"[RimPrison:test] shuttle small findings: a guard takes a calm arrival {calm}, one in a break {taken} (break started {started})");
                    if (started && taken) problems0.Add("shuttle small findings: a guard was sent to lead a new inmate who is in a mental break");
                }
                // 19: a transfer leaves the world as a transfer.
                {
                    ShuttleSystem.CallDaily(g, map);
                    ShuttleVisit v = g.shuttle.visit;
                    ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                    if (g.shuttle.visit == v && v.phase == ShuttlePhase.Landed)
                    {
                        Pawn sent = Throwaway(map, r);
                        sent.Inmate().processed = true;
                        g.shuttle.Register(sent, OutboundKind.Transferred);
                        int released0 = g.stats.released, boarded0 = v.boarded;
                        ShuttleSystem.Board(sent, v.shuttle);
                        bool gone = !sent.Spawned && g.shuttle.OutboundOf(sent) == null;
                        bool discarded = sent.Discarded;
                        RPLog.Message($"[RimPrison:test] shuttle small findings: a transfer boarded - gone {gone}, left the world as a discarded pawn {discarded}, released counted {g.stats.released - released0}, boarded {v.boarded - boarded0}");
                        if (!gone || !discarded) problems0.Add($"shuttle small findings: a transfer boarding the shuttle left as a freed prisoner (gone {gone}, discarded from the world {discarded})");
                        if (g.stats.released != released0) problems0.Add("shuttle small findings: a transfer was counted as released");
                    }
                    EndShuttleVisit(g, map);
                }
                // 18: sealed in, they are not sent to a spot they cannot reach (the same goto, every tick).
                {
                    IntVec3 center = ShuttleOpenPatch(map, 1);
                    if (center.IsValid)
                    {
                        ShuttleSystem.Footprint(center, out CellRect hullRect);
                        IntVec3 closet = hullRect.CenterCell;
                        var walls = new List<Thing>();
                        foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(closet, map)))
                        {
                            Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                            wall.SetFaction(Faction.OfPlayer);
                            GenSpawn.Spawn(wall, c, map);
                            walls.Add(wall);
                        }
                        r.Add(() =>
                        {
                            foreach (Thing w in walls)
                                if (!w.Destroyed) w.Destroy(DestroyMode.Vanish);
                        });
                        Pawn sealedIn = Throwaway(map, r, spawn: false);
                        GenSpawn.Spawn(sealedIn, closet, map);
                        InmateGenerator.MakeInmate(sealedIn, null);
                        sealedIn.Inmate().processed = true;
                        ShuttleServed(sealedIn, r);
                        g.shuttle.Register(sealedIn, OutboundKind.Released);
                        sealedIn.Inmate().RefreshActiveFlag();
                        Job job = new JobGiver_Outbound().TryIssueJobPackage(sealedIn, default).Job;
                        bool waits = job == null || job.def == JobDefOf.Wait;
                        RPLog.Message($"[RimPrison:test] shuttle small findings: a released inmate sealed in a closet gets {job?.def?.defName ?? "no job"}");
                        if (!waits) problems0.Add($"shuttle small findings: a released inmate who cannot get out of a closet is given {job.def.defName} (a walk that cannot start, given again every tick)");
                    }
                }
                // 9 (message): somebody crossing the pad for a moment is not announced; something that stays is, and an alert shows it.
                {
                    Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                    if (pad != null)
                    {
                        Thing crate = ThingMaker.MakeThing(ThingDefOf.Steel);
                        crate.stackCount = 5;
                        GenSpawn.Spawn(crate, pad.Position, map);
                        r.Add(() =>
                        {
                            if (!crate.Destroyed) crate.Destroy(DestroyMode.Vanish);
                        });
                        ShuttleSystem.CallDaily(g, map);
                        ShuttleVisit v = g.shuttle.visit;
                        ShuttleTicks(() => v.blockedSince >= 0, 200);
                        ShuttleTicks(() => false, 60);
                        bool quiet = !v.announcedBlocked;
                        v.blockedSince = ShNow - ShuttleSystem.PadBlockedNoticeTicks - 1;
                        ShuttleTicks(() => v.announcedBlocked || g.shuttle.visit != v, 130);
                        PrisonAlerts.Invalidate();
                        PrisonAlerts.Refresh();
                        bool told = v.announcedBlocked && PrisonAlerts.PadBlocker == crate && new Alert_RP_PadBlocked().GetReport().active;
                        RPLog.Message($"[RimPrison:test] shuttle small findings: a pad blocked for a moment is announced {!quiet}; held up for a while it is, with an alert {told}");
                        if (!quiet) problems0.Add("shuttle small findings: the pad-blocked message came the moment something stood on the pad (a passer-by, a squirrel)");
                        if (!told) problems0.Add("shuttle small findings: a pad held up for a while by a crate was not announced with an alert");
                        EndShuttleVisit(g, map);
                    }
                }
                // Hidden information: no row on the statistics screen for what got past the guards.
                if (StatisticsTab.Rows.Any(row => row.counter.Contains("ContrabandMissed") && row.counter.StartsWith("cargo")))
                    problems0.Add("shuttle small findings: the statistics screen counts the cargo contraband that was missed (it would tell the warden that something got past the guards)");
            });
        }
    }
}
