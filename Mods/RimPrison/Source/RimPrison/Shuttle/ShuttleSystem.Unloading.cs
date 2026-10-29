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
            IntVec3 spot = CargoSpot(map, shuttle);
            CellRect keepClear = KeepClear(shuttle.InteractionCell);
            Thing placed = null;
            if (PlaceCrateFailsForTests || !GenPlace.TryPlaceThing(t, spot, map, ThingPlaceMode.Near, out placed, null, c => !keepClear.Contains(c) && !shuttle.OccupiedRect().Contains(c)))
            {
                // No clear ground to set it down on: it stays aboard, paid for, and goes back with the shuttle (the order is not touched), instead of being lost; the unloading gives up on the
                // rest rather than trying again at every step and holding the shuttle up for it.
                t.Destroy();
                v.cargoBlocked = true;
                return;
            }
            // What is set down is paid for, what is still aboard is not: a cancelled blueprint refunds only the crates that were not unloaded.
            int paid = o.count > n ? Mathf.RoundToInt(o.cost * (n / (float)o.count)) : o.cost;
            o.cost -= paid;
            o.count -= n;
            if (o.count <= 0) v.cargo.RemoveAt(0);
            v.cargoStacks++;
            v.cargoUnits += n;
            game.shuttle.unloadedThings.Add(placed);
        }

        /// <summary>The tests' switch: no crate can be set down (the ground is not clear anywhere).</summary>
        public static bool PlaceCrateFailsForTests;

        /// <summary>The things set down this stay (for the message that points at them and the tests).</summary>
        public readonly List<Thing> unloadedThings = new List<Thing>();

        /// <summary>Where the crates go: the Deliveries zone if there is one (the crew carries them there), else the pad's own cells at the side of the margin kept clear round the ramp.</summary>
        private static IntVec3 CargoSpot(Map map, Building_StateShuttle shuttle)
        {
            List<IntVec3> zone = PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Deliveries);
            if (zone != null && zone.Count > 0) return zone.RandomElement();
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
