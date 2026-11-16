using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The showcase's cold store: what is watched every hour of the run, and the report the "the freezer is at" problem carries (census c85: 12.3C in the freezer with 5.6C outside, and the log was lost, so nothing
    /// said why). The cause is in one of: the cooler (power, breakdown, fire, target), the doors standing open, the room's roof and walls, a blackout, a heat source inside.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The freezer's hourly looks, newest last (the last day): when it warmed, with the cooler, the doors and the mains at that hour.</summary>
        private static readonly List<(string line, int doorOpenTicks, float temp, bool blackout)> freezerLooks = new List<(string, int, float, bool)>();
        private const int FreezerLooksKept = 24;
        /// <summary>A freezer above this is warm (the showcase's check, and the look a blackout is measured from).</summary>
        private const float FreezerWarmAbove = 5f;

        /// <summary>The doors in the freezer's walls (found at the hourly look), and how many ticks they stood open since it, all of them added up.</summary>
        private static List<Building_Door> freezerDoors = new List<Building_Door>();
        private static int freezerDoorOpenTicks;

        /// <summary>The start of a showcase run: no looks, doors or counts left from an earlier one in this process (a door from a map since left would be read).</summary>
        private static void ShowcaseFreezerReset()
        {
            freezerLooks.Clear();
            freezerDoors = new List<Building_Door>();
            freezerDoorOpenTicks = 0;
        }

        private static PrisonBuilder.BuiltRoom? ShowcaseFreezer() =>
            PrisonBuilder.Rooms.Where(r => r.Name == "Freezer").Select(r => (PrisonBuilder.BuiltRoom?)r).FirstOrDefault();

        /// <summary>Every tick of the showcase run: the freezer's doors that stand open (a handful of field reads).</summary>
        private static void ShowcaseFreezerTick()
        {
            for (int i = 0; i < freezerDoors.Count; i++)
                if (freezerDoors[i].Open) freezerDoorOpenTicks++;
        }

        private static List<Building_Door> DoorsOf(Map map, CellRect interior)
        {
            var doors = new List<Building_Door>();
            foreach (IntVec3 c in interior.ExpandedBy(1).Cells)
                if (c.InBounds(map) && !interior.Contains(c) && c.GetDoor(map) is { } d && !doors.Contains(d)) doors.Add(d);
            return doors;
        }

        /// <summary>The buildings that hold the temperature of the room (anything with a temperature control, in or beside it).</summary>
        private static List<Building> ControlsOf(Map map, CellRect interior)
        {
            var found = new List<Building>();
            foreach (IntVec3 c in interior.ExpandedBy(2).Cells)
                if (c.InBounds(map))
                    foreach (Thing t in c.GetThingList(map))
                        if (t is Building b && b.GetComp<CompTempControl>() != null && !found.Contains(b)) found.Add(b);
            return found;
        }

        private static string CoolerLine(Map map, Building b)
        {
            IntVec3 origin = PrisonBuilder.Origin(map);
            CompPowerTrader power = b.GetComp<CompPowerTrader>();
            CompTempControl tc = b.GetComp<CompTempControl>();
            string net = power?.PowerNet is { } n ? $"net {(n.hasPowerSource ? "with a source" : "no source")} {n.CurrentEnergyGainRate():F0}W gain, {n.powerComps.Count} consumers" : "no net";
            // (Building_Cooler: the cold side is south of it, turned by its rotation, the hot side north of it.)
            IntVec3 cold = b.Position + IntVec3.South.RotatedBy(b.Rotation), hot = b.Position + IntVec3.North.RotatedBy(b.Rotation);
            return $"{b.def.defName} at {b.Position - origin} facing {b.Rotation} powered {power?.PowerOn.ToString() ?? "n/a"} ({net}; utilities {(UtilitySystem.Active ? "on the mains" : "off")}), wants to be on {FlickUtility.WantsToBeOn(b)}, broken down {b.IsBrokenDown()}, "
                   + $"on fire {b.IsBurning()}, hp {b.HitPoints}/{b.MaxHitPoints}, target {tc?.targetTemperature:F0}C, working hard {tc?.operatingAtHighPower}, cold side {cold.GetTemperature(map):F1}C ({(cold.InBounds(map) && cold.Impassable(map) ? "blocked" : "clear")}), hot side {hot.GetTemperature(map):F1}C ({(hot.InBounds(map) && hot.Impassable(map) ? "blocked" : "clear")})";
        }

        private static string DoorLine(Map map, Building_Door d) =>
            $"{d.def.defName} at {d.Position - PrisonBuilder.Origin(map)} open {d.Open}, held open {d.HoldOpen}, powered {d.DoorPowerOn}, mode {d.GetComp<CompPrisonDoor>()?.mode.ToString() ?? "n/a"}";

        /// <summary>Every hour of the showcase run: one look at the cold store, kept for the last day.</summary>
        private static void ShowcaseFreezerWatch(Map map)
        {
            if (ShowcaseFreezer() is not { } f) return;
            freezerDoors = DoorsOf(map, f.Interior);
            float temp = f.Interior.CenterCell.GetTemperature(map);
            string coolers = string.Join("; ", ControlsOf(map, f.Interior).Select(b => $"{b.def.defName} {(b.GetComp<CompPowerTrader>()?.PowerOn == true ? "on" : "OFF")}{(b.IsBrokenDown() ? " BROKEN" : "")}{(b.IsBurning() ? " BURNING" : "")}"));
            PrisonGame game = PrisonGame.Instance;
            freezerLooks.Add(($"day {GenDate.DaysPassed} h{GenLocalDate.HourOfDay(map)}: {temp:F1}C (outside {map.mapTemperature.OutdoorTemp:F1}C), {(coolers.Length == 0 ? "no cooler" : coolers)}, doors open {freezerDoorOpenTicks} ticks (all doors added up, {GenDate.TicksPerHour} an hour for each door){(game.BlackoutActive ? ", BLACKOUT" : "")}", freezerDoorOpenTicks, temp, game.BlackoutActive));
            if (freezerLooks.Count > FreezerLooksKept) freezerLooks.RemoveAt(0);
            freezerDoorOpenTicks = 0;
        }

        /// <summary>
        /// The mains are down and every cooler of the cold store is off with them (the utilities are the prison's abstract mains, <see cref="UtilitySystem"/>: a cooler is on it with no conduit, and a blackout switches
        /// it off, as the incident says: "machines off"), and the freezer was cold at the last look before the blackout began: a warm freezer that a blackout of up to six hours explains (census-c89-sc: -4.0C, then 8.2C
        /// after the sixth hour of a blackout, the cooler "powered False (no net)" that its report read as a missing conduit). With no look before the blackout, or a cold store that was already warm, it explains nothing.
        /// </summary>
        private static bool FreezerWarmFromBlackout(Map map, PrisonBuilder.BuiltRoom f)
        {
            if (!PrisonGame.Instance.BlackoutActive || !UtilitySystem.Active) return false;
            List<Building> coolers = ControlsOf(map, f.Interior).Where(b => b.GetComp<CompPowerTrader>() != null).ToList();
            if (coolers.Count == 0 || coolers.Any(b => b.GetComp<CompPowerTrader>().PowerOn)) return false;
            int i = freezerLooks.Count - 1;
            while (i >= 0 && freezerLooks[i].blackout) i--;
            return i >= 0 && freezerLooks[i].temp <= FreezerWarmAbove;
        }

        /// <summary>What to read a warm freezer by: the room, its roof and walls, each cooler, each door and how long they stood open, the mains, and the hours before.</summary>
        private static string FreezerReport(Map map, PrisonBuilder.BuiltRoom f)
        {
            PrisonGame game = PrisonGame.Instance;
            var parts = new List<string>();
            IntVec3 center = f.Interior.CenterCell;
            Room room = center.GetRoom(map);
            int unroofed = f.Interior.Cells.Count(c => c.InBounds(map) && !c.Roofed(map));
            parts.Add(room == null
                ? "no room at the middle of the interior"
                : $"room of {room.CellCount} cells (the interior is {f.Interior.Area}), {room.Temperature:F1}C, uses the outdoor temperature {room.UsesOutdoorTemperature}, open roof cells {room.OpenRoofCount} ({unroofed} of the interior unroofed), touches the map edge {room.TouchesMapEdge}, proper room {room.ProperRoom}, outdoors {room.PsychologicallyOutdoors}");
            // The ring round the interior: a cell with no wall, or a passable one that is not a door, is a gap.
            IntVec3 origin = PrisonBuilder.Origin(map);
            var gaps = new List<string>();
            foreach (IntVec3 c in f.Interior.ExpandedBy(1).Cells)
            {
                if (f.Interior.Contains(c) || !c.InBounds(map)) continue;
                Building edifice = c.GetEdifice(map);
                if (edifice == null || edifice.def.passability != Traversability.Impassable && edifice is not Building_Door) gaps.Add($"{c - origin} {edifice?.def.defName ?? "nothing"}");
            }
            parts.Add("walls " + (gaps.Count == 0 ? "intact" : "with gaps at " + string.Join(", ", gaps.Take(8))));
            List<Building> controls = ControlsOf(map, f.Interior);
            parts.Add("coolers: " + (controls.Count == 0 ? "none found in or beside the room" : string.Join(" | ", controls.Select(b => CoolerLine(map, b)))));
            List<Building_Door> doors = DoorsOf(map, f.Interior);
            int dayOpen = freezerLooks.Sum(l => l.doorOpenTicks) + freezerDoorOpenTicks;
            parts.Add($"doors: {(doors.Count == 0 ? "none" : string.Join(" | ", doors.Select(d => DoorLine(map, d))))}; open {freezerDoorOpenTicks} ticks this hour so far, {freezerLooks.LastOrDefault().doorOpenTicks} the hour before, {dayOpen} in the last {freezerLooks.Count + 1} hours (all doors added up)");
            parts.Add($"mains: blackout {game.BlackoutActive}" + (game.BlackoutActive ? $" for {(game.blackoutUntilTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour:F1} more hours" : "") + $", hostage power cut {PrisonMap.Of(map)?.heldPowerCut}");
            // Heat inside: a fire, or a building that pushes heat (a stove or heater), and how many pawns are in the room.
            var heat = new List<string>();
            int pawns = 0;
            foreach (IntVec3 c in f.Interior.Cells)
                foreach (Thing t in c.GetThingList(map))
                {
                    if (t is Pawn) pawns++;
                    else if (t is Fire || t.IsBurning()) heat.Add($"fire at {c - origin}");
                    else if (t.TryGetComp<CompHeatPusher>() is { ShouldPushHeatNow: true }) heat.Add($"{t.def.defName} pushing heat at {c - origin}");
                }
            parts.Add($"heat inside: {(heat.Count == 0 ? "none" : string.Join(", ", heat.Take(6)))}, {pawns} pawns in the room");
            parts.Add("the hours before: " + (freezerLooks.Count == 0 ? "no look yet" : string.Join(" / ", freezerLooks.Skip(Math.Max(0, freezerLooks.Count - 12)).Select(l => l.line))));
            return string.Join("; ", parts);
        }
    }
}
