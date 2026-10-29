using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    // The visit: the daily call and cargo runs, the hold, the launch, the landed stay, the lift-off and the end of a visit.
    public partial class ShuttleSystem
    {
        // ---- the daily call and the cargo runs ------------------------------------------------------------------

        /// <summary>The intake hour: the daily shuttle is called (it waits for the pad if another one is still down).</summary>
        public static void CallDaily(PrisonGame game, Map map)
        {
            ShuttleSystem s = game.shuttle;
            if (s.visit != null) s.dailyPending = true;
            else s.Start(game, map, ShuttleKind.Daily);
        }

        /// <summary>A cargo run now (the tests and the developer button): does nothing while another shuttle is in the way.</summary>
        public static bool CallCargo(PrisonGame game, Map map)
        {
            ShuttleSystem s = game.shuttle;
            if (s.visit != null) return false;
            s.Start(game, map, ShuttleKind.Cargo);
            return true;
        }

        /// <summary>Hourly: a cargo run when supplies are due, and the housekeeping of the lists.</summary>
        public static void Hourly(PrisonGame game, Map map, int hour)
        {
            ShuttleSystem s = game.shuttle;
            s.Prune(map);
            // The one place the map is swept for clear ground between calls: with no pad that can take the shuttle, the site is found here (once an hour at most) so that readers, which
            // never sweep, have it: reception for the released, and whether a transfer has a way out.
            if (!PadAvailable(map)) s.FallbackSite(map);
            if (s.visit != null) return;
            if (s.dailyPending)
            {
                s.dailyPending = false;
                s.Start(game, map, ShuttleKind.Daily);
                return;
            }
            if (CargoRunDue(game, map)) s.Start(game, map, ShuttleKind.Cargo);
        }

        /// <summary>
        /// Orders (manual, restocking, blueprint materials) ride the daily shuttle. Orders due between shuttles get a cargo run, so food and building materials are not a
        /// day late: an order is ready two hours after it was placed (as the truck took), and a run is called when enough is ready (<see cref="CargoRunLoad"/> items) or
        /// something has waited <see cref="CargoRunWaitTicks"/> since it was ready, the last run was six hours ago and the next daily shuttle is more than three hours off
        /// (runs are batched: every order that is ready goes on the one shuttle, and the restock's hourly trickle does not call a shuttle for twenty items).
        /// </summary>
        public static bool CargoRunDue(PrisonGame game, Map map)
        {
            ShuttleSystem s = game.shuttle;
            int now = Now;
            if (now - s.lastCargoTick < CargoGapTicks || HoursToDaily(game, map) <= CargoSkipIfDailyWithinHours) return false;
            int ready = 0, longestWait = 0;
            foreach (SupplyOrder o in game.supply.pending)
            {
                if (o.arriveTick > now) continue;
                ready += o.count;
                longestWait = Mathf.Max(longestWait, now - o.arriveTick);
            }
            return ready > 0 && (ready >= CargoRunLoad || longestWait >= CargoRunWaitTicks);
        }

        private void Start(PrisonGame game, Map map, ShuttleKind kind)
        {
            visit = new ShuttleVisit { kind = kind, phase = ShuttlePhase.Holding, mapId = map.uniqueID, calledTick = Now };
            if (kind == ShuttleKind.Cargo) lastCargoTick = Now;
            TryLaunch(game, map);
        }

        // ---- the visit ------------------------------------------------------------------------------------------

        /// <summary>Every tick while a visit is on (cheap when there is none).</summary>
        public void Tick()
        {
            if (visit == null)
            {
                if (dailyPending && PrisonGame.Instance is { } g && Find.AnyPlayerHomeMap is { } m && Now % 250 == 0)
                {
                    dailyPending = false;
                    Start(g, m, ShuttleKind.Daily);
                }
                return;
            }
            PrisonGame game = PrisonGame.Instance;
            Map map = visit.Map;
            if (game == null || map == null)
            {
                if (game != null) EndVisit(game, visit, "its map is gone");
                else visit = null;
                return;
            }
            int now = Now;
            switch (visit.phase)
            {
                case ShuttlePhase.Holding:
                    if (now % 60 == 0) TryLaunch(game, map);
                    break;
                case ShuttlePhase.Inbound:
                case ShuttlePhase.Leaving:
                    if (now % 250 == 0 && !SkyfallerAround(map, visit.phase == ShuttlePhase.Inbound ? RPShuttleDefOf.RP_ShuttleIncoming : RPShuttleDefOf.RP_ShuttleLeaving))
                        // Gone without a word (the skyfaller was removed): the visit is over.
                        EndVisit(game, visit, $"it lost its skyfaller in phase {visit.phase}");
                    break;
                case ShuttlePhase.Landed:
                    TickLanded(game, map, now);
                    break;
            }
        }

        private static bool SkyfallerAround(Map map, ThingDef def) => map.listerThings.ThingsOfDef(def).Count > 0;

        public static int StayTicks(ShuttleVisit v) => v.Daily ? DailyStayTicks : CargoStayTicks;

        /// <summary>Passengers still aboard, crates still to set down, or the unloading not yet closed.</summary>
        public static bool Unloading(ShuttleVisit v, Building_StateShuttle shuttle) => shuttle.passengers.Count > 0 || !v.arrivalsDone || v.cargo.Count > 0 && !v.cargoBlocked || !v.cargoDone;

        /// <summary>The latest the shuttle lifts off, whatever is left to unload.</summary>
        public static int HardDepartTick(ShuttleVisit v) => v.landedTick + StayTicks(v) + OvertimeTicks;

        private void TickLanded(PrisonGame game, Map map, int now)
        {
            ShuttleVisit v = visit;
            if (v.shuttle is not { Spawned: true } shuttle)
            {
                EndVisit(game, v, "its shuttle is no longer on the ground");
                return;
            }
            if (now % 30 == 0 && GuardOnPad(map, shuttle)) v.lastGuardTick = now;
            if (!v.stowawayRolled && v.Daily && now >= v.landedTick + StowawayDelayTicks) RollStowaway(game, map, v);
            // The stay ends with passengers or crates still to unload: the shuttle waits for them (boarding stays open meanwhile), up to the cap.
            if (now >= v.departTick - BoardingClosesTicks && v.departTick < HardDepartTick(v) && Unloading(v, shuttle))
            {
                if (!v.stretched)
                {
                    v.stretched = true;
                    Messages.Message("RP_ShuttleStays".Translate(), new LookTargets(shuttle), MessageTypeDefOf.NeutralEvent, historical: false);
                }
                v.departTick = Mathf.Min(HardDepartTick(v), now + BoardingClosesTicks + 100);
            }
            if (now >= v.departTick)
            {
                Depart(game, map, shuttle);
                return;
            }
            if (now < v.nextStepTick) return;
            if (shuttle.passengers.Count > 0)
            {
                Disembark(game, map, shuttle);
                v.nextStepTick = now + PassengerStepTicks + Rand.RangeInclusive(0, 25);
                return;
            }
            if (!v.arrivalsDone)
            {
                FinishArrivals(game, map);
                v.nextStepTick = now + CargoStepTicks;
                return;
            }
            if (v.cargo.Count > 0 && !v.cargoBlocked)
            {
                UnloadCargo(game, map, shuttle);
                v.nextStepTick = now + CargoStepTicks;
                if (v.cargo.Count == 0) FinishCargo(game, map, shuttle);
                return;
            }
            if (!v.cargoDone) FinishCargo(game, map, shuttle);
        }

        /// <summary>The tests' switch: no weather or threat holds the shuttle (they set up their own weather when they mean to).</summary>
        public static bool ClearSkiesForTests;

        /// <summary>The weather (a thunderstorm, hard snow, toxic fallout) or a hostile threat keeps the shuttle off: the call is put off, and given up after six hours.</summary>
        public static ShuttleHold HoldReason(Map map)
        {
            if (ClearSkiesForTests) return ShuttleHold.None;
            WeatherDef weather = map.weatherManager.curWeather;
            if (weather != null && weather.HasModExtension<ShuttleHoldExtension>()) return ShuttleHold.Weather;
            if (map.gameConditionManager.ConditionIsActive(GameConditionDefOf.ToxicFallout)) return ShuttleHold.Weather;
            if (GenHostility.AnyHostileActiveThreatToPlayer(map)) return ShuttleHold.Threat;
            return ShuttleHold.None;
        }

        /// <summary>A hold the warden is told of (a letter and a line in the chronicle), once per visit.</summary>
        private void AnnounceHold(ShuttleVisit v, ShuttleHold reason)
        {
            v.hold = reason;
            if (v.announcedHold) return;
            v.announcedHold = true;
            delayedVisits++;
            Find.LetterStack.ReceiveLetter("RP_ShuttleDelayedLabel".Translate(), ("RP_ShuttleDelayedText_" + reason).Translate(v.Daily ? "RP_ShuttleTheDaily".Translate() : "RP_ShuttleTheCargo".Translate()),
                LetterDefOf.NeutralEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 1, ("RP_Chr_ShuttleDelayed_" + reason).Translate());
        }

        /// <summary>
        /// Called every 60 ticks while the shuttle holds. The weather and threat holds are looked at each time (cheap); a blocked pad is waited on for an hour, then the
        /// shuttle lands nearby; without a usable pad, or with no clear ground, the sweep of the map for ground is made once an hour (<see cref="SiteSearchEveryTicks"/>).
        /// </summary>
        private void TryLaunch(PrisonGame game, Map map)
        {
            ShuttleVisit v = visit;
            int now = Now;
            ShuttleHold reason = HoldReason(map);
            if (reason != ShuttleHold.None)
            {
                AnnounceHold(v, reason);
                if (now - v.calledTick >= MaxHoldTicks) Cancel(game, map, reason);
                return;
            }
            Building pad = BestFreePad(map, out bool usablePad);
            if (pad != null)
            {
                Launch(game, map, pad.Position, onPad: true);
                return;
            }
            ShuttleHold waiting = ShuttleHold.NoSite;
            if (usablePad)
            {
                // The pad is taken (something on the hull's spot or at the ramp): it waits for it to be cleared, then lands nearby.
                waiting = ShuttleHold.PadBlocked;
                v.hold = waiting;
                if (v.blockedSince < 0) v.blockedSince = now;
                if (!v.announcedBlocked && now - v.blockedSince >= PadBlockedNoticeTicks)
                {
                    v.announcedBlocked = true;
                    Thing blocker = PadBlocker(map);
                    Messages.Message("RP_ShuttlePadBlocked".Translate(blocker != null ? blocker.LabelShortCap : "?"), blocker != null ? new LookTargets(blocker) : LookTargets.Invalid, MessageTypeDefOf.CautionInput);
                    if (AutoTest.Active) RPLog.Message($"[shuttle] pad blocked by {blocker?.LabelShort} ({blocker?.def.defName}) at {blocker?.Position}");
                }
                if (now - v.blockedSince < PadWaitTicks) return;
            }
            // No usable pad (none, roofed, or cut off from holding), or the blocked one waited out: clear ground near holding or the gate.
            IntVec3 site = FallbackSite(map);
            if (!site.IsValid)
            {
                // No clear ground at all near the prison: tried again in an hour; the call is given up after six.
                if (waiting == ShuttleHold.NoSite) AnnounceHold(v, waiting);
                else v.hold = waiting;
                if (now - v.calledTick >= MaxHoldTicks) Cancel(game, map, waiting);
                return;
            }
            Launch(game, map, site, onPad: false);
        }

        /// <summary>No shuttle today: the arrivals are not carried over to the next day (the state sends a new lot), the supplies stay on order.</summary>
        private void Cancel(PrisonGame game, Map map, ShuttleHold reason)
        {
            ShuttleVisit v = visit;
            visit = null;
            if (v.Daily)
            {
                game.stats.shuttlesCancelled++;
                CustodyContracts.Notify_ShuttleCalledOff(game);
            }
            Find.LetterStack.ReceiveLetter("RP_ShuttleCancelledLabel".Translate(), ("RP_ShuttleCancelledText_" + reason).Translate(v.Daily ? "RP_ShuttleTheDaily".Translate() : "RP_ShuttleTheCargo".Translate()),
                LetterDefOf.NegativeEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, ("RP_Chr_ShuttleCancelled_" + reason).Translate());
        }

        private void Launch(PrisonGame game, Map map, IntVec3 site, bool onPad)
        {
            ShuttleVisit v = visit;
            var shuttle = (Building_StateShuttle)ThingMaker.MakeThing(RPShuttleDefOf.RP_StateShuttle);
            v.shuttle = shuttle;
            v.site = site;
            v.onPad = onPad;
            if (v.Daily)
            {
                // (No more than can step off in the stay: the rest is for another day, not lost at lift-off.)
                List<Pawn> roster = IntakeSystem.GenerateRoster(game, map, out bool held, out int limit, MaxRoster);
                foreach (Pawn p in roster)
                {
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                    if (!shuttle.passengers.TryAdd(p, canMergeWithExistingStacks: false)) p.Destroy();
                }
                v.rosterSize = shuttle.passengers.Count;
                if (held && limit == 0 && Ledger.CurrentDay - lastHeldLetterDay >= HeldLetterEveryDays)
                {
                    // The state keeps its prisoners off the shuttle altogether: a letter the first day, a message after.
                    lastHeldLetterDay = Ledger.CurrentDay;
                    Find.LetterStack.ReceiveLetter("RP_ShuttleHeldLabel".Translate(), "RP_ShuttleHeldText".Translate(), LetterDefOf.NegativeEvent);
                }
                else if (held) Messages.Message((limit == 0 ? "RP_IntakeHeldBackAll" : "RP_IntakeHeldBack").Translate(limit), MessageTypeDefOf.NegativeEvent);
                else if (v.rosterSize == 0 && IntakeSystem.AnyWanted(game) && IntakeSystem.FreeBedCount(map) == 0)
                    Messages.Message("RP_IntakeNoBeds".Translate(), MessageTypeDefOf.CautionInput);
                else if (v.rosterSize >= MaxRoster && game.intake.perDay.Values.Sum() > MaxRoster)
                    Messages.Message("RP_ShuttleRosterCapped".Translate(MaxRoster), MessageTypeDefOf.CautionInput, historical: false);
            }
            // Everything that has been ready since the last shuttle is aboard.
            int ready = Now;
            for (int i = game.supply.pending.Count - 1; i >= 0; i--)
            {
                SupplyOrder o = game.supply.pending[i];
                if (o.arriveTick > ready) continue;
                game.supply.pending.RemoveAt(i);
                v.cargo.Add(o);
            }
            v.cargo.Reverse();
            v.phase = ShuttlePhase.Inbound;
            SkyfallerMaker.SpawnSkyfaller(RPShuttleDefOf.RP_ShuttleIncoming, shuttle, site, map);
            string key = v.Daily ? (onPad ? "RP_ShuttleInbound" : "RP_ShuttleInboundNoPad") : (onPad ? "RP_ShuttleCargoInbound" : "RP_ShuttleCargoInboundNoPad");
            if (!onPad) Concepts.Teach(Concepts.Pad);
            if (AnnouncesInbound(v)) Messages.Message(key.Translate(), new LookTargets(site, map), MessageTypeDefOf.NeutralEvent, historical: false);
            if (AutoTest.Active) RPLog.Message($"[shuttle] {v.kind} inbound to {site} onPad={onPad} passengers={v.rosterSize} cargo={v.cargo.Sum(o => o.count)}");
        }

        /// <summary>
        /// A cargo run posted four messages (coming down, landed, the things set down, lifting off) up to four times a day: it now says it once on landing. Only the day's own shuttle is
        /// announced coming down (it brings the inmates), and a cargo run lifting off with nobody aboard and nothing left behind has nothing to tell.
        /// </summary>
        public static bool AnnouncesInbound(ShuttleVisit v) => v.Daily;

        /// <summary>The landing message: the day's shuttle, or the cargo run (which has no message coming down).</summary>
        public static string LandedKey(ShuttleVisit v) => v.Daily ? (v.onPad ? "RP_ShuttleLanded" : "RP_ShuttleLandedNoPad") : (v.onPad ? "RP_ShuttleCargoLanded" : "RP_ShuttleCargoLandedNoPad");

        public static bool LiftOffNeedsMessage(ShuttleVisit v, int waiting, int aboard, int crates) => v.Daily || v.boarded > 0 || waiting > 0 || aboard > 0 || crates > 0;

        /// <summary>The shuttle has touched down: the visit begins its stay.</summary>
        public static void Notify_Landed(Building_StateShuttle shuttle)
        {
            PrisonGame game = PrisonGame.Instance;
            ShuttleVisit v = VisitOf(shuttle);
            if (game == null || v == null) return;
            Map map = shuttle.Map;
            v.phase = ShuttlePhase.Landed;
            v.landedTick = Now;
            v.departTick = Now + StayTicks(v);
            v.nextStepTick = Now + 60;
            v.site = shuttle.Position;
            v.hold = ShuttleHold.None;
            game.stats.shuttleLandings++;
            if (!v.Daily) game.stats.cargoRuns++;
            if (!v.onPad) game.stats.shuttlesOffPad++;
            Messages.Message(LandedKey(v).Translate(), new LookTargets(shuttle), MessageTypeDefOf.NeutralEvent, historical: false);
            if (v.onPad) Concepts.Demonstrated(Concepts.Pad);
            // The ones waiting for it see it: they go to board.
            foreach (OutboundEntry e in game.shuttle.outbound)
                if (e.pawn is { Spawned: true } p && p.Map == map && p.CurJob is { } job && job.def != RPShuttleDefOf.RP_BoardShuttle) p.jobs.EndCurrentJob(JobCondition.InterruptForced);
            // (The stowaway is rolled a little later: see TickLanded. The guards on pad duty have to get to their posts first.)
            if (AutoTest.Active) RPLog.Message($"[shuttle] {v.kind} landed at {shuttle.Position} onPad={v.onPad} departs in {(v.departTick - Now) / (float)GenDate.TicksPerHour:F1}h");
        }

        /// <summary>The shuttle is out of sight.</summary>
        public static void Notify_Gone(Building_StateShuttle shuttle)
        {
            ShuttleSystem s = Sys;
            if (s == null) return;
            if (s.visit != null && s.visit.shuttle == shuttle) s.EndVisit(PrisonGame.Instance, s.visit, null);
            else s.CarryOver(shuttle);
        }

        /// <summary>
        /// The visit is over (the shuttle is out of sight, or the visit was lost): whatever it did not unload goes back (orders to the office, passengers to the next
        /// roster) and the grant of those who stepped off is in the books. Nothing it carried is lost.
        /// </summary>
        private void EndVisit(PrisonGame game, ShuttleVisit v, string lostBecause)
        {
            if (lostBecause != null) RPLog.Message($"[shuttle] visit ended early ({lostBecause}); phase {v.phase}");
            BookArrivals(game, v);
            ReturnCargo(game, v);
            if (v.shuttle != null) CarryOver(v.shuttle);
            if (visit == v) visit = null;
        }

        private void Depart(PrisonGame game, Map map, Building_StateShuttle shuttle)
        {
            ShuttleVisit v = visit;
            if (v.shuttle != shuttle) return;
            // The state shuttle carries the exports away: whatever lies in the Exports zone at lift-off is counted as loaded (the crew loads it while the shuttle waits).
            LaborUtility.SellExports(map, game);
            // ...and the dead: a body in the morgue at lift-off counts as loaded too (the family is told, or the plot has it: see CondolenceSystem).
            CondolenceSystem.Ship(map, game);
            ArrivalEscortsEnd(map);
            // The cap was reached with some still to unload: they go back (the grant of those who stepped off is booked first).
            int aboard = shuttle.passengers.Count, crates = v.cargo.Sum(o => o.count);
            BookArrivals(game, v);
            // What was set down before the cap is delivered all the same (the message, the contraband roll), and the list of things set down is not carried into the next visit.
            if (!v.cargoDone) FinishCargo(game, map, shuttle);
            unloadedThings.Clear();
            ReturnCargo(game, v);
            CarryOver(shuttle);
            v.cargoDone = true;
            int waiting = outbound.Count(e => e.pawn is { Spawned: true } p && p.Map == map);
            string leaves = (v.boarded > 0 ? "RP_ShuttleLeaves" : "RP_ShuttleLeavesEmpty").Translate(v.boarded).ToString();
            if (waiting > 0) leaves += " " + "RP_ShuttleLeftBehind".Translate(waiting);
            if (aboard > 0 || crates > 0) leaves += " " + "RP_ShuttleUnloadCut".Translate(aboard, crates);
            if (LiftOffNeedsMessage(v, waiting, aboard, crates)) Messages.Message(leaves, new LookTargets(shuttle), aboard > 0 || crates > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.NeutralEvent, historical: false);
            if (AutoTest.Active) RPLog.Message($"[shuttle] {v.kind} lifts off with {v.boarded} boarded, {waiting} left waiting for the next, {aboard} passengers and {crates} items sent back");
            IntVec3 pos = shuttle.Position;
            v.phase = ShuttlePhase.Leaving;
            if (stowaway != null && stowaway.Spawned) stowaway = null;
            shuttle.DeSpawn();
            SkyfallerMaker.SpawnSkyfaller(RPShuttleDefOf.RP_ShuttleLeaving, shuttle, pos, map);
        }
    }
}
