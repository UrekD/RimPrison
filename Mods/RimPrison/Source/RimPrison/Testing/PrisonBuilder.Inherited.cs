using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The inherited facility: a small prison that reads as one. A wall around it with a gatehouse at the south gate; behind the gate
    /// a ring of buildings round a walled yard, a small chapel standing in the yard. South (the front): reception (with the administrator's office and the morgue) and the holding
    /// room (with two solitary cells), the gatehouse (a sally port), the staff room and visitation (its own door from outside). North: the cell block (cells on both sides of a corridor; the south row
    /// minimum security, the north row medium), the hall with the guard station, and the canteen with its kitchen at the corridor's end.
    /// West of the yard the common room, the showers and the library, east of it the classroom and the workshop. Deliveries and exports
    /// are outside the gate, and so is the burial plot (bare soil along the south wall). <see cref="PrisonBuilder.Cells"/> sets the number of cells (blocks of up to eight cells, a hall between every
    /// two blocks, the compound widens with them); from 16 cells the halls serve meals and have showers, from 24 a clerk's mailroom
    /// opens off the common room.
    /// </summary>
    public static partial class PrisonBuilder
    {
        // Rows (z): the front band 0..12, the yard and its side buildings 12..35, the north band (cells, halls, canteen) 35..49.
        public const int InheritedDepth = 50;
        private const int NorthZ0 = 35, NorthZ1 = 49, FrontZ1 = 12, CellFrontS = 40, CorridorMid = 42, CellFrontN = 44;
        /// <summary>Cells in a block at most (four on each side of its corridor: no bed is more than ~35 cells' walk from the hall at its end).</summary>
        private const int BlockCellsMax = 8;
        /// <summary>From this many cells the canteen has two serving counters and the kitchen two stoves; from <see cref="ServiceHallCells"/> the halls serve meals and have showers.</summary>
        private const int TwoCountersCells = 12, ServiceHallCells = 16;
        /// <summary>The west building's east wall, the east buildings' width, and the row the east buildings start at (below it the yard reaches the east wall).</summary>
        private const int WestBlockX1 = 20, EastBlockW = 18, EastBlockZ0 = 19;

        /// <summary>Books on each shelf at the start (the shelves are filled every midnight).</summary>
        public const int StartingBooks = 16;
        /// <summary>Blocks of this many cells or more get a mailroom the inmate clerk can reach (and five book shelves).</summary>
        public const int BigMailroomCells = 24;

        /// <summary>The north band, west to east: each block's x range and cells, each hall's x range, then the canteen and kitchen.</summary>
        private sealed class NorthPlan
        {
            public readonly List<(int x0, int x1, int cells)> Blocks = new List<(int, int, int)>();
            public readonly List<(int x0, int x1)> Halls = new List<(int, int)>();
            /// <summary>The elements in order (a block or a hall, with its index in <see cref="Blocks"/> or <see cref="Halls"/>) and their x range.</summary>
            public readonly List<(bool block, int index, int x0, int x1)> Order = new List<(bool, int, int, int)>();
            public int CanteenX0, CanteenX1, KitchenX1, Width;
        }

        private static NorthPlan PlanNorth()
        {
            var p = new NorthPlan();
            int blocks = Math.Max(1, (Cells + BlockCellsMax - 1) / BlockCellsMax), left = Cells, cursor = 0;
            for (int b = 0; b < blocks; b++)
            {
                int cells = Math.Min(BlockCellsMax, left);
                left -= cells;
                int w = 4 * ((cells + 1) / 2);
                p.Order.Add((true, p.Blocks.Count, cursor, cursor + w));
                p.Blocks.Add((cursor, cursor + w, cells));
                cursor += w;
                if (b % 2 == 0)
                {
                    p.Order.Add((false, p.Halls.Count, cursor, cursor + 11));
                    p.Halls.Add((cursor, cursor + 11));
                    cursor += 11;
                }
            }
            p.CanteenX0 = cursor;
            p.CanteenX1 = cursor + 19;
            p.KitchenX1 = p.CanteenX1 + 17;
            p.Width = Math.Max(64, p.KitchenX1 + 1);
            return p;
        }

        public static int InheritedWidth => PlanNorth().Width;

        private static void BuildInherited(PrisonMap pm)
        {
            NorthPlan np = PlanNorth();
            int W = np.Width, cx = W / 2;
            TerrainDef concrete = TerrainDefOf.Concrete, tile = Terrain("PavedTile"), wood = Terrain("WoodPlankFloor");
            gateOutside = new IntVec3(cx, 0, -2);

            WallRect(0, 0, W - 1, InheritedDepth - 1);
            // The yard: concrete round the buildings, a patch of soil in its south-east corner for the garden.
            int yardX1 = W - EastBlockW - 1, stripX1 = W - 2, yardZ1 = NorthZ0 - 1;
            Floor(WestBlockX1 + 1, FrontZ1 + 1, yardX1, yardZ1, concrete);
            Floor(yardX1 + 1, FrontZ1 + 1, stripX1, EastBlockZ0 - 1, concrete);
            Floor(stripX1 - 6, FrontZ1 + 1, stripX1, FrontZ1 + 5, TerrainDefOf.Soil);
            Paint(WestBlockX1 + 1, FrontZ1 + 1, yardX1, yardZ1, RPDefOf.RP_Yard);
            Paint(yardX1 + 1, FrontZ1 + 1, stripX1, EastBlockZ0 - 1, RPDefOf.RP_Yard);

            BuildFront(W, cx, tile, wood, concrete);
            BuildNorth(np, tile, concrete);
            BuildWest(wood, tile);
            BuildEast(W, tile, concrete);
            BuildYard(WestBlockX1 + 1, yardX1, FrontZ1 + 1, yardZ1, stripX1);
            BuildChapel(cx, wood);
            BuildOutside(cx);

            // Anchors for the tests: an aisle cell of the canteen, the workshop floor, and the room the canteen's side door opens on.
            canteenCell = new IntVec3(np.CanteenX0 + 10, 0, 41);
            workshopCell = new IntVec3(W - EastBlockW + 8, 0, 31);
            (bool lastBlock, _, _, int lastX1) = np.Order[np.Order.Count - 1];
            corridorCell = new IntVec3(lastX1 - 1, 0, lastBlock ? CorridorMid : 40);
        }

        // ---- front: reception and holding, gatehouse, staff room, visitation -----------------------------------------------

        private static void BuildFront(int W, int cx, TerrainDef tile, TerrainDef wood, TerrainDef concrete)
        {
            int gx0 = cx - 6, gx1 = cx + 5, sx1 = gx1 + 12;
            // Reception (x 0..12) and holding (x 12..gate): the building along the south wall west of the gate.
            R reception = Shell(0, 0, 12, FrontZ1, tile);
            R holding = Shell(12, 0, gx0, FrontZ1, concrete);
            Door(12, 6, PrisonDoorMode.StaffOnly);                       // reception <-> holding (staff)
            Door(gx0, 6, PrisonDoorMode.StaffOnly);                      // gatehouse -> holding side (staff)
            Door(gx0 - 4, FrontZ1, PrisonDoorMode.Auto);                 // holding -> yard
            PaintIn(reception, Z("RP_StaffOnly"));
            // The holding zone is the part of the room next to the yard door (arrivals stand within a few steps of each other, as the checks that throw a pair of people together expect).
            Paint(gx0 - 7, 5, gx0 - 1, 11, RPDefOf.RP_Holding);
            // Reception: a counter, a lamp. The office (the administrator's desk) in its south-west corner and the morgue in its south-east one, behind staff doors. Holding: benches along the walls, two cells off it.
            PutF("Table2x2c", 5, 8, Rot4.North);
            Chair(4, 8, Rot4.East);
            Chair(7, 8, Rot4.West);
            Lamp(11, 10);
            BuildOffice();
            BuildMorgue();
            for (int x = 14; x <= gx0 - 3; x += 2) Chair(x, 1, Rot4.North);
            for (int z = 8; z <= 10; z += 2) Chair(gx0 - 1, z, Rot4.West);
            Lamp(gx0 - 1, 2);
            Heat(13, 1);
            BuildHoldingCells();

            // Gatehouse: two security doors in line (the sally port) and the guards' post.
            R gate = Shell(gx0, 0, gx1, FrontZ1, concrete);
            foreach (int dx in new[] { cx - 1, cx })
            {
                Door(dx, 0, PrisonDoorMode.StaffOnly);
                Door(dx, FrontZ1, PrisonDoorMode.StaffOnly);
            }
            PaintIn(gate, Z("RP_StaffOnly"));
            PutF("Table2x2c", gx0 + 2, 5, Rot4.North);
            Chair(gx0 + 1, 5, Rot4.East);
            Chair(gx0 + 1, 6, Rot4.East);
            Lamp(gx1 - 1, 2);
            Lamp(gx1 - 1, 10);
            Heat(gx0 + 1, 1);

            // Staff room: door from the gatehouse and one to the yard; a table with chairs, armchairs, the mailroom table in the north-east corner.
            R staff = Shell(gx1, 0, sx1, FrontZ1, wood);
            Door(gx1, 6, PrisonDoorMode.StaffOnly);
            Door(gx1 + 6, FrontZ1, PrisonDoorMode.StaffOnly);
            PaintIn(staff, Z("RP_StaffRoom"));
            DiningTable(gx1 + 5, 2, "we");
            // (Staff go home to sleep: armchairs and a side table, not beds.)
            PutF("Armchair", gx1 + 2, 6, Rot4.South);
            PutF("EndTable", gx1 + 3, 6);
            PutF("Armchair", gx1 + 4, 6, Rot4.South);
            PutF("Table2x2c", sx1 - 3, FrontZ1 - 3, Rot4.North);
            Paint(sx1 - 3, FrontZ1 - 3, sx1 - 1, FrontZ1 - 2, RPMailDefOf.RP_Mailroom);
            Put("PlantPot", sx1 - 1, 1);
            Lamp(gx1 + 1, 10);
            Lamp(sx1 - 1, 5);
            Heat(gx1 + 1, 1);
            Heat(sx1 - 1, 9);

            // Visitation: tables with a chair on each side, the visitors' own door in the south wall, the inmates' (with a metal detector) in the north wall.
            R visit = Shell(sx1, 0, W - 1, FrontZ1, tile);
            int vc = (sx1 + W - 1) / 2;
            Door(vc, 0, PrisonDoorMode.StaffOnly);
            Door(vc, FrontZ1, PrisonDoorMode.Auto);
            Put("RP_MetalDetector", vc, FrontZ1 - 1, Rot4.East);
            PaintIn(visit, Z("RP_Visitation"));
            foreach (int vz in new[] { 3, 7 })
            {
                PutF("Table2x2c", sx1 + 4, vz, Rot4.North);
                Chair(sx1 + 3, vz, Rot4.East);
                Chair(sx1 + 6, vz, Rot4.West);
                PutF("Table2x2c", W - 7, vz, Rot4.North);
                Chair(W - 8, vz, Rot4.East);
                Chair(W - 5, vz, Rot4.West);
            }
            Lamp(sx1 + 1, 1);
            Lamp(W - 2, 1);
            Heat(sx1 + 1, 11);
        }

        /// <summary>The administrator's office: a small room in reception's south-west corner (wood floor, a staff door on the reception hall), with the office desk the bureaucracy works at.</summary>
        private static void BuildOffice()
        {
            R office = Shell(0, 0, 6, 6, Terrain("WoodPlankFloor"));
            Door(6, 3, PrisonDoorMode.StaffOnly);
            Put("RP_OfficeDesk", 3, 4, Rot4.North, null);
            Chair(3, 3, Rot4.North);
            Put("Bookcase", 1, 4);
            Put("PlantPot", 5, 5);
            Lamp(5, 1);
            Heat(1, 1);
            PaintIn(office, Z("RP_StaffOnly"));
        }

        /// <summary>The morgue: a small staff-only room in reception's south-east corner, off the reception hall (the guards carry the dead here).</summary>
        private static void BuildMorgue()
        {
            R morgue = Shell(8, 0, 12, 5, TerrainDefOf.Concrete);
            Door(10, 5, PrisonDoorMode.StaffOnly);
            PaintIn(morgue, RPMorgueDefOf.RP_Morgue);
        }

        /// <summary>
        /// Two solitary cells off holding, at its west end next to reception: where the inmate who arrives fighting, or earns the hole, goes (a bed and a toilet each, a security door onto the holding room).
        /// They are not for intake and not counted among the cells.
        /// </summary>
        private static void BuildHoldingCells()
        {
            PrisonZoneDef solitary = RPSecurityDefOf.RP_SolitaryZone;
            WallLine(12, 7, 20, 7);
            WallLine(16, 7, 16, FrontZ1);
            WallLine(20, 7, 20, FrontZ1);
            foreach (int ix in new[] { 13, 17 })
            {
                Door(ix + 1, 7, PrisonDoorMode.Auto, security: true);
                if (PutF("Bed", ix, 10, Rot4.South) is Building_Bed bed) bed.ForPrisoners = true;
                Put("RP_Toilet", ix + 2, 11);
                Heat(ix + 2, 9);
                Lamp(ix + 2, 8);
                Paint(ix, 8, ix + 2, 11, solitary);
            }
        }

        /// <summary>A small chapel standing in the yard, east of the gate: a door in its south wall, two rows of chairs facing an altar at the east wall.</summary>
        private static void BuildChapel(int cx, TerrainDef wood)
        {
            int x0 = cx + 2, x1 = cx + 10, z0 = 15, z1 = 21;
            R chapel = Shell(x0, z0, x1, z1, wood);
            Door(cx + 7, z0, PrisonDoorMode.Auto);
            foreach (int z in new[] { 17, 19 })
                for (int x = cx + 4; x <= cx + 6; x++) Chair(x, z, Rot4.East);
            PutF("Table1x2c", cx + 8, 18, Rot4.East);
            Put("SculptureSmall", cx + 8, 19);
            Put("PlantPot", cx + 9, 16);
            Put("PlantPot", cx + 9, 20);
            Lamp(cx + 3, 20);
            Heat(cx + 3, 16);
            PaintIn(chapel, RPFaithDefOf.RP_Chapel);
        }

        // ---- north: cell blocks, halls, canteen, kitchen -------------------------------------------------------------------

        private static void BuildNorth(NorthPlan np, TerrainDef tile, TerrainDef concrete)
        {
            PrisonZoneDef minimum = Z("RP_WingMinimum"), medium = Z("RP_WingMedium");
            foreach ((bool isBlock, int index, int x0, int x1) in np.Order)
            {
                if (isBlock)
                {
                    bool exitWest = np.Halls.Any(h => h.x1 == x0), exitEast = np.Halls.Any(h => h.x0 == x1) || x1 == np.CanteenX0;
                    BuildBlock(np.Blocks[index], minimum, medium, exitWest, exitEast, concrete);
                }
                else BuildHall(np.Halls[index].x0, np.Halls[index].x1, np, concrete);
            }
            BuildCanteen(np, tile);
        }

        /// <summary>A block: cells on both sides of a corridor (south row first), a door at either end where it joins a hall or the canteen.</summary>
        private static void BuildBlock((int x0, int x1, int cells) b, PrisonZoneDef southWing, PrisonZoneDef northWing, bool exitWest, bool exitEast, TerrainDef concrete)
        {
            int southCells = (b.cells + 1) / 2, northCells = b.cells - southCells;
            Shell(b.x0, NorthZ0, b.x1, NorthZ1, concrete);
            WallLine(b.x0, CellFrontS, b.x1, CellFrontS);
            WallLine(b.x0, CellFrontN, b.x1, CellFrontN);
            for (int k = 1; k < southCells; k++) WallLine(b.x0 + 4 * k, NorthZ0, b.x0 + 4 * k, CellFrontS);
            for (int k = 1; k < northCells; k++) WallLine(b.x0 + 4 * k, CellFrontN, b.x0 + 4 * k, NorthZ1);
            // (An odd number of cells leaves one slot of the north row unused: solid wall.)
            if (northCells < southCells)
                for (int x = b.x0 + 4 * northCells; x <= b.x1; x++)
                    WallLine(x, CellFrontN, x, NorthZ1);
            for (int k = 0; k < southCells; k++)
            {
                int ix = b.x0 + 4 * k + 1;
                Door(ix + 1, CellFrontS, PrisonDoorMode.Auto, security: true);
                if (PutF("Bed", ix, 36, Rot4.North) is Building_Bed bed) bed.ForPrisoners = true;
                Put("RP_Toilet", ix + 2, 36, Rot4.South);
                Heat(ix + 2, 38);
                Lamp(ix + 2, 39);
                Paint(ix, 36, ix + 2, 39, southWing);
            }
            for (int k = 0; k < northCells; k++)
            {
                int ix = b.x0 + 4 * k + 1;
                Door(ix + 1, CellFrontN, PrisonDoorMode.Auto, security: true);
                if (PutF("Bed", ix, 47, Rot4.South) is Building_Bed bed) bed.ForPrisoners = true;   // (south: the head against the back wall)
                Put("RP_Toilet", ix + 2, 48);
                Heat(ix + 2, 46);
                Lamp(ix + 2, 45);
                Paint(ix, 45, ix + 2, 48, northWing);
            }
            if (exitWest) Door(b.x0, CorridorMid, PrisonDoorMode.Auto);
            if (exitEast) Door(b.x1, CorridorMid, PrisonDoorMode.Auto);
            for (int x = b.x0 + 6; x < b.x1 - 2; x += 8) Lamp(x, 43);
            Heat(b.x0 + 2, 41);
            Heat(b.x1 - 2, 41);
        }

        /// <summary>
        /// A hall joins two blocks (or ends one): doors to the blocks' corridors at the corridor's row, one to the yard in the south wall,
        /// the guard station behind a staff door in the north. Where it meets the canteen it has a door to it. From 16 cells the hall also
        /// serves meals (a counter, a table) and has showers in its east half.
        /// </summary>
        private static void BuildHall(int x0, int x1, NorthPlan np, TerrainDef concrete)
        {
            Shell(x0, NorthZ0, x1, NorthZ1, concrete);
            WallLine(x0, CellFrontN, x1, CellFrontN);
            int mid = (x0 + x1) / 2;
            Door(mid, NorthZ0, PrisonDoorMode.Auto);                 // hall -> yard
            Door(mid, CellFrontN, PrisonDoorMode.StaffOnly);         // hall -> guard station
            if (x1 == np.CanteenX0) Door(x1, 40, PrisonDoorMode.Auto);
            PaintIn(new R(x0, CellFrontN, x1, NorthZ1), Z("RP_StaffOnly"));
            PutF("Table2x2c", x0 + 3, 46, Rot4.North);
            Chair(x0 + 2, 46, Rot4.East);
            Chair(x0 + 5, 46, Rot4.West);
            Lamp(x1 - 1, 47);
            Heat(x0 + 1, 47);
            Lamp(x0 + 1, 37);
            Heat(x1 - 1, 37);
            if (Cells >= ServiceHallCells)
            {
                Put("RP_ServingCounter", x0 + 2, 43);
                DiningTable(x0 + 3, 38, "we");
                Paint(x0 + 1, 36, x0 + 5, 43, RPDefOf.RP_Canteen);
                Row("RP_Shower", x0 + 7, 37, 2, 2);
                Paint(x0 + 6, 36, x1 - 1, 39, RPDefOf.RP_Shower);
            }
        }

        /// <summary>The canteen at the end of the corridor and the kitchen east of it (reached through the canteen).</summary>
        private static void BuildCanteen(NorthPlan np, TerrainDef tile)
        {
            int c0 = np.CanteenX0, c1 = np.CanteenX1, k1 = np.KitchenX1;
            R canteen = Shell(c0, NorthZ0, c1, NorthZ1, tile);
            R kitchen = Shell(c1, NorthZ0, k1, NorthZ1, tile);
            Door(c0 + 8, NorthZ0, PrisonDoorMode.Auto);                  // canteen -> yard
            Door(c1, 44, PrisonDoorMode.StaffOnly);                      // kitchen -> canteen
            PaintIn(canteen, RPDefOf.RP_Canteen);
            PaintIn(kitchen, Z("RP_Kitchen"));
            // Canteen: serving counters by the kitchen door, the commissary at the north wall, tables in two rows with the aisle between.
            Put("RP_ServingCounter", c1 - 5, 45);
            if (Cells >= TwoCountersCells) Put("RP_ServingCounter", c1 - 5, 47);
            Put("RP_CommissaryCounter", c0 + 2, 48);
            foreach (int tx in new[] { c0 + 4, c0 + 9, c0 + 14 })
            {
                DiningTable(tx, 38, "we");
                DiningTable(tx, 42, "we");
            }
            Lamp(c0 + 1, 36);
            Lamp(c1 - 1, 36);
            Lamp(c0 + 1, 47);
            Lamp((c0 + c1) / 2, 47);
            Heat(c0 + 1, 44);
            Heat(c1 - 1, 40);
            // Kitchen: stoves with a standing meal bill along the north wall, a food stockpile, a prep table.
            AddCookBill(Put("FueledStove", c1 + 4, 48, Rot4.North));
            if (Cells >= TwoCountersCells) AddCookBill(Put("FueledStove", c1 + 9, 48, Rot4.North));
            Stockpile(Rect(k1 - 5, 37, k1 - 2, 40), StorageSettingsPreset.DefaultStockpile, food: true);
            PutF("Table2x2c", c1 + 3, 41, Rot4.North);
            PutF("Table2x2c", c1 + 8, 41, Rot4.North);
            FoodShelf(c1 + 1 + 2, 45);
            FoodShelf(c1 + 1 + 5, 45);
            Lamp(c1 + 1, 36);
            Lamp(k1 - 1, 44);
            Heat(c1 + 1, 46);
        }

        // ---- west: common room, showers, library -----------------------------------------------------------------------------

        private static void BuildWest(TerrainDef wood, TerrainDef tile)
        {
            // Common room (z 12..22): the television with seats in front, card tables; from 24 cells the clerk's mailroom in the north-west corner.
            R common = Shell(0, FrontZ1, WestBlockX1, 22, tile);
            Door(WestBlockX1, 17, PrisonDoorMode.Auto);
            PaintIn(common, Z("RP_CommonRoom"));
            Put("TubeTelevision", 14, 21, Rot4.South);
            foreach (int sx in new[] { 12, 13, 14, 15, 16 }) Chair(sx, 18, Rot4.North);
            foreach (int sx in new[] { 13, 14, 15 }) Chair(sx, 19, Rot4.North);
            DiningTable(3, 13, "we");
            DiningTable(9, 13, "we");
            Lamp(WestBlockX1 - 1, 21);
            Lamp(1, 13);
            Lamp(WestBlockX1 - 1, 13);
            Heat(WestBlockX1 - 1, 15);
            Heat(10, 21);
            if (Cells >= BigMailroomCells)
            {
                // The clerk's mailroom: a small room in the north-west corner, behind an Auto door from the common room (inmate space).
                R mail = Shell(0, 17, 8, 22, tile);
                Door(4, 17, PrisonDoorMode.Auto);
                PaintIn(mail, RPMailDefOf.RP_Mailroom);
                PutF("Table2x2c", 5, 19, Rot4.North);
                Chair(4, 19, Rot4.East);
                Lamp(1, 21);
            }
            // Showers (z 22..29): a row along the north wall.
            R showers = Shell(0, 22, WestBlockX1, 29, tile);
            Door(WestBlockX1, 25, PrisonDoorMode.Auto);
            PaintIn(showers, RPDefOf.RP_Shower);
            Row("RP_Shower", 4, 28, 7, 2);
            Lamp(1, 24);
            Lamp(WestBlockX1 - 1, 24);
            Heat(1, 27);
            Heat(WestBlockX1 - 1, 27);
            // Library (z 29..35): shelves along the north wall, a reading table.
            R library = Shell(0, 29, WestBlockX1, NorthZ0, wood);
            Door(WestBlockX1, 32, PrisonDoorMode.Auto);
            PaintIn(library, RPLibraryDefOf.RP_Library);
            int shelves = Cells >= BigMailroomCells ? 5 : 3;
            for (int i = 0; i < shelves; i++)
                if (Put("RP_BookShelf", 3 + 2 * i, NorthZ0 - 1)?.TryGetComp<CompBookShelf>() is { } comp) comp.books = StartingBooks;
            DiningTable(4, 31, "we");
            DiningTable(12, 31, "we");
            Lamp(1, 33);
            Lamp(WestBlockX1 - 1, 33);
            Heat(1, 31);
        }

        // ---- east: classroom, workshop -----------------------------------------------------------------------------------------

        private static void BuildEast(int W, TerrainDef tile, TerrainDef concrete)
        {
            int x0 = W - EastBlockW, x1 = W - 1;
            // Classroom (z 19..27): desks in two rows facing the front.
            R classroom = Shell(x0, EastBlockZ0, x1, 27, tile);
            Door(x0, 23, PrisonDoorMode.Auto);
            PaintIn(classroom, Z("RP_Classroom"));
            Row("RP_SchoolDesk", x0 + 3, 22, 6, 2);
            Row("RP_SchoolDesk", x0 + 3, 24, 6, 2);
            Lamp(x0 + 1, 26);
            Lamp(x1 - 1, 26);
            Heat(x1 - 1, 21);
            // Workshop (z 27..35): two presses and a tailor bench, steel kept inside, a metal detector at the door.
            R workshop = Shell(x0, 27, x1, NorthZ0, concrete);
            Door(x0, 31, PrisonDoorMode.Auto);
            Put("RP_MetalDetector", x0 + 1, 31, Rot4.East);
            PaintIn(workshop, RPLaborDefOf.RP_Workshop);
            Put("RP_LicensePlatePress", x0 + 5, 33, Rot4.South);
            Put("RP_LicensePlatePress", x0 + 5, 29, Rot4.North);
            PutF("HandTailoringBench", x0 + 9, 33, Rot4.South);          // the tailoring course's bench, and the tailors' (vanilla's)
            Stockpile(Rect(x1 - 5, 30, x1 - 4, 32), StorageSettingsPreset.DefaultStockpile, food: false, only: ThingDefOf.Steel);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 150;
            GenPlace.TryPlaceThing(steel, At(x1 - 5, 31), map, ThingPlaceMode.Near);
            Lamp(x0 + 1, 34);
            Lamp(x1 - 1, 34);
            Heat(x1 - 1, 28);
        }

        // ---- yard: recreation --------------------------------------------------------------------------------------------------

        private static void BuildYard(int x0, int x1, int z0, int z1, int stripX1)
        {
            // Gym corner (north-east of the main yard): weights benches, one per ~6 cells and at least three, two rows.
            int benches = Math.Max(3, Cells / 6);
            for (int i = 0; i < benches; i++) Put("RP_WeightBench", x1 - 8 + 2 * (i / 2), z1 - 4 + 2 * (i % 2));
            // Games and a picnic table along the walls; the middle stays open.
            Put("HorseshoesPin", x0 + 3, z1 - 4);
            Put("HorseshoesPin", x0 + 3, z1 - 10);
            Put("ChessTable", x0 + 9, z1 - 3);
            Chair(x0 + 8, z1 - 3, Rot4.East);
            Chair(x0 + 10, z1 - 3, Rot4.West);
            DiningTable(x0 + 12, z1 - 12, "we");
            Put("Telescope", x1 - 2, z0 + 12, Rot4.North, ThingDefOf.Steel);
            Put("RP_PhoneBooth", x0, z0 + 8, Rot4.West);
            for (int x = x0 + 4; x < x1 - 3; x += 8)
            {
                Lamp(x, z0 + 1);
                Lamp(x, z1);
            }
            Lamp(stripX1 - 8, z0 + 1);
        }

        // ---- outside the gate: deliveries, exports, materials ---------------------------------------------------------------

        private static void BuildOutside(int cx)
        {
            // The state shuttle's pad west of the deliveries, by the road to the gate: the shuttle lands on it every day.
            Put("RP_ShuttlePad", cx - 26, -8);
            Paint(cx - 14, -7, cx - 6, -3, RPDefOf.RP_Deliveries);
            Paint(cx + 4, -7, cx + 6, -3, Z("RP_Exports"));
            Stockpile(Rect(cx + 8, -7, cx + 13, -3), StorageSettingsPreset.DefaultStockpile, food: false);
            Stockpile(Rect(cx + 4, -7, cx + 6, -3), StorageSettingsPreset.DefaultStockpile, food: false, only: DefDatabase<ThingDef>.GetNamed("RP_LicensePlates"));
            BuildBurialPlot(cx + 16);
        }

        /// <summary>The burial plot: a small patch of bare soil (6 x 5, room for a dozen graves) along the south wall east of the deliveries, outside the inmates' way and inside the grounds; the workmen carry the dead there from the morgue.</summary>
        private static void BuildBurialPlot(int x0)
        {
            // (Outside the cleared footprint: the map's own trees, chunks and plants are taken off first, or a grown oak stood in the plot and took two graves' room: a census run.)
            ClearArea(Rect(x0, -7, x0 + 5, -3));
            Floor(x0, -7, x0 + 5, -3, TerrainDefOf.Soil);
            Paint(x0, -7, x0 + 5, -3, RPBurialDefOf.RP_BurialPlot);
        }
    }
}
