using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    // Unloading: passengers stepping off, the grant booked, crates set down, and what goes back.
    public partial class ShuttleSystem
    {
        // ---- unloading ------------------------------------------------------------------------------------------

        private void Disembark(PrisonGame game, Map map, Building_StateShuttle shuttle)
        {
            ShuttleVisit v = visit;
            Pawn p = shuttle.passengers[0];
            shuttle.passengers.Remove(p);
            IntVec3 cell = RampCell(map, shuttle);
            // The state's own prisoners come back at no price (the state pays nothing for the return) and are counted as admitted by their own return.
            bool marshal = IsMarshal(p);
            IntakeSystem.AdmitArrival(game, map, p, cell, fromShuttle: true);
            v.steppedOff++;
            if (!marshal)
            {
                v.grant += p.Inmate()?.security?.intakeGrant ?? 0;
                game.stats.admitted++;
            }
        }

        private void FinishArrivals(PrisonGame game, Map map) => BookArrivals(game, visit);

        /// <summary>The grant of the arrivals who stepped off goes into the books, with the line for the warden. Once per visit, at the end of the unloading or whenever the visit ends first.</summary>
        private static void BookArrivals(PrisonGame game, ShuttleVisit v)
        {
            if (v.arrivalsDone) return;
            v.arrivalsDone = true;
            if (v.steppedOff == 0) return;
            game.ledger.Add(RPDefOf.RP_IntakeGrants, v.grant, "RP_IntakeNote".Translate(v.steppedOff));
            if (v.Map is { } landedOn && PrisonMap.Of(landedOn) is { } landedPm)
            {
                if (!landedPm.zones.Any(RPDefOf.RP_Holding)) Concepts.Teach(Concepts.Holding);
                else Concepts.Demonstrated(Concepts.Holding);
            }
            Messages.Message("RP_IntakeArrived".Translate(v.steppedOff, Money.Format(v.grant)), v.shuttle is { Spawned: true } ? new LookTargets(v.shuttle) : LookTargets.Invalid, MessageTypeDefOf.NeutralEvent);
            if (AutoTest.Active && v.Map is { } map) RPLog.Message($"[intake] arrived={v.steppedOff} freeBeds={IntakeSystem.FreeBedCount(map)} capacity={IntakeSystem.Capacity(map)} inmates={map.mapPawns.PrisonersOfColonySpawnedCount} (shuttle)");
        }

        /// <summary>A cell by the ramp for whoever steps off: clear ground, on the pad when there is one, not under the hull.</summary>
        public static IntVec3 RampCell(Map map, Building_StateShuttle shuttle)
        {
            IntVec3 ramp = shuttle.InteractionCell;
            CellRect hull = shuttle.OccupiedRect();
            if (ramp.InBounds(map) && ramp.Standable(map) && ramp.GetFirstPawn(map) == null) return ramp;
            return CellFinder.TryFindRandomCellNear(ramp, map, 3, c => c.Standable(map) && !hull.Contains(c) && c.GetFirstPawn(map) == null, out IntVec3 near) ? near : ramp;
        }

        private void UnloadCargo(PrisonGame game, Map map, Building_StateShuttle shuttle)
        {
            ShuttleVisit v = visit;
            SupplyOrder o = v.cargo[0];
            if (o.count <= 0 || o.thing == null)
            {
                v.cargo.RemoveAt(0);
                return;
            }
            int n = Mathf.Min(o.count, o.thing.stackLimit);
            Thing t = o.thing.MadeFromStuff ? ThingMaker.MakeThing(o.thing, GenStuff.DefaultStuffFor(o.thing)) : ThingMaker.MakeThing(o.thing);
            t.stackCount = n;
            CellRect keepClear = KeepClear(shuttle.InteractionCell);
            CellRect hull = shuttle.OccupiedRect();
            // With no Deliveries zone the crates go down on the pad's own cells (the free ones outside the margin kept clear round the hull); only when the pad has none left do they go on the nearest clear ground.
            List<IntVec3> padCells = PadCargoCells(map, shuttle, keepClear, hull);
            IntVec3 spot = CargoSpot(map, shuttle, padCells);
            Thing placed = null;
            if (PlaceCrateFailsForTests && PlaceCrateMergesFirstForTests > 0 && PlaceCrateMergesFirstForTests < n)
            {
                // (The tests' partial merge: part of the crate goes down for real, then the rest finds no room, as when GenPlace stacks part of it onto a leftover and then runs out of cells.)
                Thing part = t.SplitOff(PlaceCrateMergesFirstForTests);
                if (!GenPlace.TryPlaceThing(part, spot, map, ThingPlaceMode.Near, out placed)) part.Destroy();
            }
            bool set = !PlaceCrateFailsForTests && (padCells != null && padCells.Count > 0 && GenPlace.TryPlaceThing(t, spot, map, ThingPlaceMode.Near, out placed, null, c => padCells.Contains(c))
                || GenPlace.TryPlaceThing(t, spot, map, ThingPlaceMode.Near, out placed, null, c => !keepClear.Contains(c) && !hull.Contains(c)));
            // A failed placement can still have stacked part of the crate onto a stack on the ground (it keeps what it could not place in t): those units are down, only the rest stays aboard.
            int down = set ? n : Mathf.Max(0, n - (t.Destroyed ? 0 : t.stackCount));
            if (!set)
            {
                // No clear ground to set the rest down on: it stays aboard, paid for, and goes back with the shuttle (the order is not touched beyond what was set down), instead of being lost; the unloading gives up on the
                // rest rather than trying again at every step and holding the shuttle up for it.
                if (!t.Destroyed) t.Destroy();
                v.cargoBlocked = true;
                if (down == 0) return;
            }
            // What is set down is paid for, what is still aboard is not: a cancelled blueprint refunds only the crates that were not unloaded.
            int paid = o.count > down ? Mathf.RoundToInt(o.cost * (down / (float)o.count)) : o.cost;
            o.cost -= paid;
            o.count -= down;
            if (o.count <= 0) v.cargo.RemoveAt(0);
            v.cargoStacks++;
            v.cargoUnits += down;
            if (placed != null) game.shuttle.unloadedThings.Add(placed);
        }

        /// <summary>The tests' switch: no crate can be set down (the ground is not clear anywhere).</summary>
        public static bool PlaceCrateFailsForTests;

        /// <summary>The tests' switch with the one above: this many units of the crate are set down first (a partial merge), and the rest finds no room.</summary>
        public static int PlaceCrateMergesFirstForTests;

        /// <summary>The things set down this stay (for the message that points at them and the tests).</summary>
        public readonly List<Thing> unloadedThings = new List<Thing>();

        /// <summary>The cells of the pad under the shuttle that crates may be set down on (outside the margin kept clear round the hull and the ramp, walkable): null when there is a Deliveries zone, or no pad under the hull.</summary>
        private static List<IntVec3> PadCargoCells(Map map, Building_StateShuttle shuttle, CellRect keepClear, CellRect hull)
        {
            if (PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Deliveries) is { Count: > 0 }) return null;
            Thing pad = shuttle.Position.GetThingList(map).Find(t => t.def == RPShuttleDefOf.RP_ShuttlePad);
            if (pad == null) return null;
            var cells = new List<IntVec3>();
            foreach (IntVec3 c in pad.OccupiedRect())
                if (c.InBounds(map) && !keepClear.Contains(c) && !hull.Contains(c) && c.Walkable(map)) cells.Add(c);
            return cells;
        }

        /// <summary>Where the crates go: the Deliveries zone if there is one (the crew carries them there), else a pad cell with no item on it (<paramref name="padCells"/>; a leftover on the cell picked would send the crate to the nearest cell, off the pad's edge), else the old spot beside the ramp.</summary>
        private static IntVec3 CargoSpot(Map map, Building_StateShuttle shuttle, List<IntVec3> padCells)
        {
            List<IntVec3> zone = PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Deliveries);
            if (zone != null && zone.Count > 0) return zone.RandomElement();
            if (padCells != null && padCells.Where(c => c.GetFirstItem(map) == null).TryRandomElement(out IntVec3 free)) return free;
            if (padCells != null && padCells.Count > 0) return padCells.RandomElement();
            IntVec3 ramp = shuttle.InteractionCell;
            return ramp + new IntVec3(Rand.Bool ? 4 : -4, 0, Rand.RangeInclusive(-1, 3));
        }

        private void FinishCargo(PrisonGame game, Map map, Building_StateShuttle shuttle)
        {
            ShuttleVisit v = visit;
            v.cargoDone = true;
            if (v.cargoStacks == 0) return;
            List<Thing> delivered = unloadedThings.FindAll(t => t != null && t.Spawned);
            Messages.Message("RP_DeliveryArrived".Translate(), new LookTargets(delivered.Count > 0 ? delivered : new List<Thing> { shuttle }), MessageTypeDefOf.SilentInput, historical: false);
            unloadedThings.Clear();
            CargoContraband(game, map, v);
        }

        /// <summary>Orders not set down go back to the office (ready at once, so the next shuttle brings them; they stay counted as ordered and were paid for).</summary>
        private static void ReturnCargo(PrisonGame game, ShuttleVisit v)
        {
            for (int i = v.cargo.Count - 1; i >= 0; i--)
                if (v.cargo[i].count > 0 && v.cargo[i].thing != null) game.supply.pending.Insert(0, v.cargo[i]);
            v.cargo.Clear();
        }
    }
}
