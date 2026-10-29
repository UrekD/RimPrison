using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Which ready-made prison the builder puts up (the scenario's facility option).</summary>
    public enum Facility : byte
    {
        /// <summary>The inherited facility: a small working prison (<see cref="PrisonBuilder.Cells"/> cells), the reference layout of the autotest.</summary>
        Inherited,
        /// <summary>The state penitentiary: a large, fully fitted prison (<c>PrisonBuilder.Penitentiary.cs</c>).</summary>
        Penitentiary,
    }

    /// <summary>
    /// Builds a complete prison instantly (no construction, no blueprints): the "inherited facility" and "state penitentiary" scenario
    /// options, and the reference layout of the autotest. The common part is here (size, footprint, the finishing pass and its self-check);
    /// the layouts are <c>PrisonBuilder.Inherited.cs</c> and <c>PrisonBuilder.Penitentiary.cs</c> and the building blocks (rooms, doors,
    /// furniture, painting, the layout preview) <c>PrisonBuilder.Kit.cs</c>. All coordinates in the layouts are local: x east, z north,
    /// (0, 0) the south-west corner of the perimeter wall, the gate in the south wall.
    /// </summary>
    public static partial class PrisonBuilder
    {
        /// <summary>The facility the next <see cref="BuildStarter"/> puts up (set from the scenario, see <see cref="ScenPart_PrisonStart"/>).</summary>
        public static Facility Kind = Facility.Inherited;
        /// <summary>Cells in the inherited facility's blocks; its compound widens with them. (The penitentiary has its own count.)</summary>
        public static int Cells = 8;
        /// <summary>The next <see cref="BuildStarter"/> sows the handover's secrets once the inmates are in (see <see cref="Handover"/>): set from the scenario's start, taken by the build. Off for every test start unless its plan asks.</summary>
        public static bool InheritProblems;

        /// <summary>Outer size of the walled compound of the current <see cref="Kind"/> (and <see cref="Cells"/>).</summary>
        public static int Width => Kind == Facility.Penitentiary ? PenWidth : InheritedWidth;
        public static int Depth => Kind == Facility.Penitentiary ? PenDepth : InheritedDepth;
        /// <summary>Rows south of the perimeter that belong to the facility (deliveries, exports, the road to the gate, the landing pad).</summary>
        public static int SouthMargin => Kind == Facility.Penitentiary ? PenSouthMargin : 8;

        private static Map map;
        private static IntVec3 origin;
        private static ThingDef wallStuff, doorStuff, furnitureStuff;
        public static readonly List<string> Errors = new List<string>();

        /// <summary>The walled compound and what belongs to it outside the gate: floors, flooring and "inside the compound" tests use it.</summary>
        public static CellRect Footprint(Map m)
        {
            IntVec3 o = OriginFor(m);
            return new CellRect(o.x, o.z - SouthMargin, Width, Depth + SouthMargin);
        }

        /// <summary>Building it again (tests) needs a fresh origin per map.</summary>
        public static IntVec3 Origin(Map m) => OriginFor(m);

        private static IntVec3 OriginFor(Map m) => new IntVec3(m.Center.x - Width / 2, 0, m.Center.z - (Depth + SouthMargin) / 2 + SouthMargin);

        /// <summary>A cell of the main canteen with open floor around it (the hostage checks stage their crisis there), in map coordinates.</summary>
        public static IntVec3 CanteenCell(Map m) => OriginFor(m) + canteenCell;
        /// <summary>A cell of the workshop room, in map coordinates.</summary>
        public static IntVec3 WorkshopCell(Map m) => OriginFor(m) + workshopCell;
        /// <summary>A cell of the corridor next to the canteen (painting it as canteen zone makes the hold two rooms), in map coordinates.</summary>
        public static IntVec3 CorridorCell(Map m) => OriginFor(m) + corridorCell;
        /// <summary>The cell outside the gate staff are moved to when the walls went up around them.</summary>
        private static IntVec3 gateOutside;
        private static IntVec3 canteenCell, workshopCell, corridorCell;

        /// <summary>Builds the facility of <see cref="Kind"/> on the map: floors, walls, doors, furniture, zones, and (penitentiary) staff and inmates.</summary>
        public static void BuildStarter(Map m)
        {
            // The penitentiary and its road need a plot of at least ~140 (a vanilla new game can pick any size): on a smaller map the inherited facility is built.
            if (Kind == Facility.Penitentiary && (m.Size.x < PenWidth + 20 || m.Size.z < PenDepth + PenSouthMargin + 20))
            {
                Messages.Message("RP_PenitentiaryNeedsPlot".Translate(m.Size.x, m.Size.z), MessageTypeDefOf.NegativeEvent, historical: false);
                Kind = Facility.Inherited;
            }
            map = m;
            origin = OriginFor(m);
            bool inherit = InheritProblems;
            InheritProblems = false;
            Errors.Clear();
            ResetKit();
            wallStuff = ThingDefOf.BlocksGranite;
            doorStuff = ThingDefOf.Steel;
            furnitureStuff = ThingDefOf.WoodLog;
            PrisonMap pm = PrisonMap.Of(map);

            ClearArea(Footprint(m).ExpandedBy(1));
            if (Kind == Facility.Penitentiary) BuildPenitentiary(pm);
            else BuildInherited(pm);

            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            pm.Notify_DoorsChanged();
            pm.ForceDoorUpdate();
            SelfCheck(pm);
            FreeTrapped(m);
            // Every room has a floor (the outbuildings in the yard were standing on soil).
            foreach (IntVec3 c in Footprint(m).ClipInsideMap(map))
                if (map.roofGrid.Roofed(c) && !c.GetTerrain(map).IsFloor) map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
            int lit = LightTheFreeTime(pm);
            RPLog.Message($"{Kind} prison built at {origin}: {Errors.Count} placement problems, {lit} lamps added to the free-time rooms and corridors" + (Errors.Count > 0 ? ": " + string.Join("; ", Errors) : ""));
            if (Kind == Facility.Penitentiary) Populate(m);
            // The seeds go in after the inmates are admitted (behind the scenario's flag): the prison you were given has a past.
            if (inherit) Handover.Sow(m, Kind);
            DumpLayout();
        }

        /// <summary>
        /// The inmates the old warden left in the inherited facility's cells (the handover's; the penitentiary has its own forty): admitted at once into their wings as a start admits them, and the intake grants left out
        /// (they were here already: the shuttle's grant is for the ones that arrive).
        /// </summary>
        internal static List<Pawn> AdmitLeftBehind(Map m, params (string level, int count)[] population)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return new List<Pawn>();
            int funds0 = game.ledger.funds, recent0 = game.ledger.recent.Count;
            var today0 = new Dictionary<LedgerCategoryDef, int>(game.ledger.Today.totals);
            List<Pawn> arrived = AdmitStarting(m, game, population);
            game.ledger.funds = funds0;
            if (game.ledger.recent.Count > recent0) game.ledger.recent.RemoveRange(recent0, game.ledger.recent.Count - recent0);
            game.ledger.Today.totals.Clear();
            foreach (KeyValuePair<LedgerCategoryDef, int> kv in today0) game.ledger.Today.totals[kv.Key] = kv.Value;
            RPLog.Message($"handover: {arrived.Count} inmates left in the cells of the {Kind} prison");
            return arrived;
        }

        /// <summary>
        /// The starting staff stand wherever vanilla put them, and the walls go up around them: anyone walled in
        /// (who cannot reach the map edge) is moved out through the gate.
        /// </summary>
        private static void FreeTrapped(Map m)
        {
            IntVec3 gate = At(gateOutside.x, gateOutside.z);
            foreach (Pawn p in m.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Dead || p.CanReachMapEdge()) continue;
                if (!CellFinder.TryFindRandomCellNear(gate, m, 8, c => c.Standable(m) && !c.Fogged(m) && m.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.PassDoors)), out IntVec3 cell))
                    continue;
                p.pather?.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
                RPLog.Message($"starter prison: moved {p.LabelShort} out of a walled-in spot");
            }
        }

        /// <summary>Logs doors inmates cannot use, rooms without a door, and cells inmates cannot reach from holding (layout bugs).</summary>
        private static void SelfCheck(PrisonMap pm)
        {
            var seen = new HashSet<Room>();
            foreach (IntVec3 c in Footprint(map).ClipInsideMap(map))
            {
                if (c.GetRoom(map) is not { PsychologicallyOutdoors: false } room || !seen.Add(room)) continue;
                if (!room.ContainedAndAdjacentThings.Any(t => t is Building_Door))
                    Errors.Add($"sealed room ({room.CellCount} cells) at {room.Cells.First() - origin}");
            }
            foreach (CompPrisonDoor d in pm.Doors)
                if (d.mode == PrisonDoorMode.Auto && !d.inmatePass && d.workZone == null) // (a work-zone door, the clerk's mailroom, opens for a worker only)
                    Errors.Add($"auto door at {d.parent.Position - origin} closed to inmates ({d.sideA}/{d.sideB})");
            List<IntVec3> holding = pm.zones.CellsOf(RPDefOf.RP_Holding);
            if (holding.Count == 0) return;
            IntVec3 from = holding[holding.Count / 2];
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                // (The hole is reached by a guard carrying, a supermax cell by a guard walking them in: the unit's door is staff-only, see EscortKind.Home.)
                if (b is not Building_Bed { ForPrisoners: true, Medical: false } bed || PunishmentUtility.IsSolitaryBed(bed) || IntakeSystem.WingOf(bed) is { escortOnly: true }) continue;
                if (!InmateReachability.CanInmateWalk(map, from, bed.Position))
                    Errors.Add($"cell bed at {bed.Position - origin} unreachable for inmates from holding");
            }
        }

        /// <summary>The starting stoves come with their standing meal bill and some fuel (the game's own <see cref="KitchenBills"/> does the bill).</summary>
        public static void AddCookBill(Thing stove)
        {
            // (A stove put up in the painted kitchen has its bill already: the game's own hook made it when the stove was built.)
            if (stove is IBillGiver giver && !KitchenBills.HasMealBill(giver)) KitchenBills.AddBill(stove, Kind == Facility.Penitentiary ? PenInmates : Cells);
            stove?.TryGetComp<CompRefuelable>()?.Refuel(50f);
        }

        /// <summary>
        /// Where the tour's camera looks for a named shot, in local coordinates with the zoom (half the rows in view), or null where the facility
        /// has no such place: overview, cells, canteen (the penitentiary's second: canteen-east), yard, reception, gate, landing, infirmary, chapel, library, visitation, control, workshop.
        /// </summary>
        public static (int x, int z, float size)? TourSpot(string key)
        {
            if (Kind == Facility.Penitentiary)
                return key switch
                {
                    "overview" => (60, 36, 62f),
                    "cells" => (25, 80, 15f),
                    "canteen" => (33, 61, 14f),
                    "canteen-east" => (87, 61, 14f),
                    "yard" => (60, 48, 22f),
                    "reception" => (42, 19, 15f),
                    "gate" => (60, 8, 14f),
                    "landing" => (48, -9, 13f),
                    "infirmary" => (107, 14, 14f),
                    "chapel" => (107, 47, 13f),
                    "library" => (12, 34, 12f),
                    "visitation" => (82, 19, 15f),
                    "control" => (60, 80, 12f),
                    "workshop" => (86, 47, 12f),
                    _ => null,
                };
            NorthPlan np = PlanNorth();
            return key switch
            {
                "overview" => (np.Width / 2, 21, 33f),
                "cells" => (8, 42, 12f),
                "canteen" => (np.CanteenX0 + 10, 42, 12f),
                "yard" => (np.Width / 2, 24, 15f),
                "reception" => (18, 7, 12f),
                "gate" => (np.Width / 2, 5, 11f),
                "library" => (10, 31, 11f),
                "visitation" => (np.Width - 10, 6, 11f),
                "workshop" => (np.Width - 10, 31, 11f),
                _ => null,
            };
        }
    }
}
