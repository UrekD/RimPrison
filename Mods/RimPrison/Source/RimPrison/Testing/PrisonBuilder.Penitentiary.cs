using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The state penitentiary: a large prison with everything the mod's systems use, built from a table of rooms (<see cref="Room"/>).
    /// Local coordinates, 120 x 98 inside the perimeter wall (south at z 0, the gate at x 59/60), 24 rows of road, landing pad and
    /// deliveries south of it. Four tiers, south to north:
    /// <list type="bullet">
    /// <item>front (z 0-28): staff wing, reception and intake (holding hall, search room, property) west of the entry court with the
    /// gatehouse (a sally port) at its foot; visitation, the infirmary and a classroom east of it;</item>
    /// <item>middle (z 28-68): a central yard flanked by the west block (kitchen, freezer and stores, canteen, showers, laundry, library)
    /// and the east block (canteen and a second kitchen, showers, workshop, classroom, chapel), each with a corridor of its own;</item>
    /// <item>the cross corridor (z 68-73), joining the blocks and the wings;</item>
    /// <item>north (z 73-93): six housing wings (minimum dormitory, medium, protective custody; maximum, segregation, death row with its
    /// execution chamber) around the control hub (the control room with the camera monitors, a medical ward, records, the command suite), a sterile strip and a tower in each corner.</item>
    /// </list>
    /// Everything is placed finished (no blueprints), staff are hired for every role and posted, the wings are filled by wing, and the
    /// shuttle's landing area is <see cref="ShuttlePadLocal"/> (see <see cref="ShuttlePadRect"/>).
    /// </summary>
    public static partial class PrisonBuilder
    {
        public const int PenWidth = 120, PenDepth = 98, PenSouthMargin = 24;
        /// <summary>Inmates at the start (a few beds stay free for the shuttle's first arrivals).</summary>
        public const int PenInmates = 44;
        /// <summary>Where the shuttle lands: a paved, marked 9 x 7 area outside the gate by reception (local x, z, width, height); the pad building goes here when the mod has one.</summary>
        public static readonly (int x, int z, int w, int h) ShuttlePadLocal = (30, -14, 9, 7);

        /// <summary>The landing area in map coordinates.</summary>
        public static CellRect ShuttlePadRect(Map m)
        {
            IntVec3 o = OriginFor(m);
            return new CellRect(o.x + ShuttlePadLocal.x, o.z + ShuttlePadLocal.z, ShuttlePadLocal.w, ShuttlePadLocal.h);
        }

        private const int GateX = 59;                       // the gate is two doors wide: x 59 and 60
        private const int CorridorZ0 = 68, CorridorZ1 = 73;  // the cross corridor's walls
        private const int WingZ0 = 73;                       // the wings start at the corridor's north wall
        private const PrisonDoorMode Auto = PrisonDoorMode.Auto, Staff = PrisonDoorMode.StaffOnly;

        private static void BuildPenitentiary(PrisonMap pm)
        {
            TerrainDef conc = TerrainDefOf.Concrete, paved = Terrain("PavedTile"), granite = Terrain("TileGranite", "TileSandstone", "PavedTile"), sterile = Terrain("SterileTile", "PavedTile"),
                carpet = Terrain("CarpetGray", "Carpet_Gray", "CarpetBlue", "CarpetRed", "Carpet"), wood = Terrain("WoodPlankFloor"), gravel = Terrain("Gravel", "PackedDirt", "Soil");
            gateOutside = new IntVec3(GateX, 0, -2);

            // The ground: gravel in the sterile strip inside the wall, concrete in the yard and the entry court; the yard is painted first so
            // that the rooms' own zones (the guard post) go over it.
            Floor(1, 1, PenWidth - 2, PenDepth - 2, gravel);
            Floor(42, 29, 77, 67, conc);
            Floor(51, 13, 68, 28, conc);
            Paint(42, 29, 77, 67, RPDefOf.RP_Yard);
            Paint(51, 13, 68, 28, RPDefOf.RP_Yard);
            WallRect(0, 0, PenWidth - 1, PenDepth - 1);

            Front(conc, paved, sterile, carpet, wood);
            Middle(conc, paved, granite, sterile, wood);
            North(conc, paved, granite, carpet, wood, sterile);
            Wings(granite, paved, carpet, granite);
            YardRooms(conc);
            BuildSpecs();

            YardFurniture(paved);
            Perimeter();
            Outside(conc, paved);
            Stores();

            // Anchors for the tests (the hostage checks stage a crisis in a canteen, a workshop and a corridor beside it).
            canteenCell = new IntVec3(33, 0, 61);
            workshopCell = new IntVec3(107, 0, 34);
            corridorCell = new IntVec3(33, 0, 70);
        }

        // ---- front tier ---------------------------------------------------------------------------------------------------------

        private static void Front(TerrainDef conc, TerrainDef paved, TerrainDef sterile, TerrainDef carpet, TerrainDef wood)
        {
            // Gatehouse: a sally port, two security doors in line at each end (the vehicle lane in the middle), a post on either side.
            Room("Gatehouse", 50, 0, 69, 12, conc, "RP_StaffOnly")
                .Door(Side.S, GateX, Staff, true).Door(Side.S, GateX + 1, Staff, true).Door(Side.N, GateX, Staff, true).Door(Side.N, GateX + 1, Staff, true)
                .Door(Side.W, 11, Staff)
                .With(r =>
                {
                    Floor(57, 1, 62, 12, paved);
                    PutF("Table2x2c", 52, 6, Rot4.North);
                    Chair(51, 6, Rot4.East);
                    Chair(51, 7, Rot4.East);
                    PutF("Table2x2c", 66, 6, Rot4.North);
                    Chair(68, 6, Rot4.West);
                    Chair(68, 7, Rot4.West);
                    Cam(55, 11);
                    Cam(64, 11);
                    Corners(r, heaters: true);
                });

            // Staff wing, south row: staff room, armoury, mailroom, property, issue room; the staff corridor behind them.
            Room("Staff room", 4, 0, 18, 10, carpet, "RP_StaffRoom").Door(Side.S, 11, Staff).Door(Side.N, 11, Staff).With(r =>
            {
                PutF("Couch", 6, 7, Rot4.North);
                PutF("Couch", 9, 7, Rot4.North);
                Put("FlatscreenTelevision", 7, 9, Rot4.South);
                PutF("Armchair", 6, 4, Rot4.North);
                PutF("Armchair", 8, 4, Rot4.North);
                DiningTable(14, 3, "we");
                Put("PlantPot", 5, 1);
                Put("PlantPot", 17, 9);
                Decor("PlantPot", 17, 1);
                Corners(r, heaters: true);
            });
            Room("Armoury", 18, 0, 25, 10, conc, "RP_StaffOnly").Door(Side.N, 21, Staff).With(r =>
            {
                Put("ToolCabinet", 19, 9);
                Put("ToolCabinet", 23, 9);
                Put("Shelf", 19, 1);
                Put("Shelf", 22, 1);
                PutF("Table2x2c", 21, 4, Rot4.North);
                Corners(r);
            });
            Room("Mailroom", 25, 0, 33, 10, paved, "RP_Mailroom").Door(Side.N, 29, Staff).With(r =>
            {
                PutF("Table2x2c", 28, 5, Rot4.North);
                Chair(27, 5, Rot4.East);
                Put("Shelf", 26, 1);
                Put("Shelf", 29, 1);
                Corners(r);
            });
            Room("Property room", 33, 0, 38, 10, conc, "RP_StaffOnly").Door(Side.N, 36, Staff).With(r =>
            {
                for (int z = 2; z <= 8; z += 3) { Put("Shelf", 34, z); Put("Shelf", 36, z); }
                Corners(r);
            });
            // The morgue beside it, off the staff corridor (the guards carry the dead here).
            Room("Morgue", 38, 0, 41, 10, conc, "RP_Morgue").Door(Side.N, 40, Staff).With(r => Corners(r));
            Room("Issue room", 41, 0, 50, 10, conc, "RP_StaffOnly").Door(Side.N, 45, Staff).With(r =>
            {
                Put("Shelf", 42, 1);
                Put("Shelf", 45, 1);
                Put("Shelf", 48, 3);
                PutF("Table2x2c", 43, 5, Rot4.North);
                Corners(r);
            });
            Room("Staff corridor", 4, 10, 50, 15, conc, "RP_StaffOnly").With(r =>
            {
                for (int x = 9; x < 50; x += 10) Lamp(x, 13);
                Cam(6, 14);
                Cam(48, 14);
                Heat(5, 12);
            });

            // North row: warden's office, administration, the hearing room, then reception.
            Room("Warden's office", 4, 15, 15, 28, carpet, "RP_StaffOnly").Door(Side.S, 9, Staff).With(r =>
            {
                Put("RP_OfficeDesk", 9, 24, Rot4.North, null);
                Chair(9, 23, Rot4.North);
                Chair(7, 21, Rot4.East);
                Chair(11, 21, Rot4.West);
                Put("Bookcase", 5, 27);
                Put("Bookcase", 12, 27);
                PutF("Couch", 6, 17, Rot4.North);
                Put("SculptureSmall", 14, 27);
                Decor("PlantPot", 14, 16);
                Corners(r, heaters: true);
            });
            Room("Administration", 15, 15, 25, 28, carpet, "RP_StaffOnly").Door(Side.S, 20, Staff).With(r =>
            {
                foreach (int x in new[] { 17, 20, 23 })
                {
                    Put("RP_OfficeDesk", x, 25, Rot4.North, null);
                    Chair(x, 24, Rot4.North);
                }
                Put("Shelf", 16, 27);
                Put("Shelf", 22, 27);
                Decor("PlantPot", 24, 17);
                Corners(r, heaters: true);
            });
            Room("Hearing room", 25, 15, 35, 28, wood, "RP_HearingRoom").Door(Side.S, 30, Staff).Door(Side.E, 21, Auto).With(r =>
            {
                Put("RP_OfficeDesk", 30, 25, Rot4.North, null);
                Chair(30, 24, Rot4.North);
                foreach (int x in new[] { 28, 30, 32 }) Chair(x, 19, Rot4.North);
                Put("Bookcase", 26, 27);
                Decor("PlantPot", 33, 26);
                Decor("PlantPot", 27, 17);
                Corners(r, heaters: true);
            });
            Room("Reception hall", 35, 15, 50, 28, paved, "RP_Holding").Door(Side.S, 42, Staff).Door(Side.E, 21, Auto).With(r =>
            {
                for (int x = 38; x <= 48; x += 2) Chair(x, 17, Rot4.North);
                PutF("Table2x2c", 44, 19, Rot4.North);
                Chair(43, 19, Rot4.East);
                Chair(46, 19, Rot4.West);
                Chair(37, 19, Rot4.East);
                Chair(49, 19, Rot4.West);
                Cam(36, 16);
                foreach (int x in new[] { 36, 49 }) if (FreeCell(x, 16)) Lamp(x, 16);
                Heat(36, 20);
                Heat(49, 17);
            });
            Room("Search room", 35, 22, 41, 28, paved, "RP_StaffOnly").Door(Side.S, 38, Staff).With(r =>
            {
                PutF("Table2x2c", 37, 25, Rot4.North);
                Put("RP_MetalDetector", 38, 23, Rot4.East);
                Corners(r);
            });
            Room("Holding cell A", 41, 22, 46, 28, conc, "RP_Holding").Door(Side.S, 43, Auto).With(r =>
            {
                Chair(42, 27, Rot4.East);
                Chair(44, 27, Rot4.West);
                Put("RP_Toilet", 44, 24, Rot4.South);
                Lamp(42, 24);
            });
            Room("Holding cell B", 46, 22, 50, 28, conc, "RP_Holding").Door(Side.S, 48, Auto).With(r =>
            {
                Chair(47, 27, Rot4.East);
                Chair(49, 27, Rot4.West);
                Put("RP_Toilet", 49, 24, Rot4.South);
                Lamp(47, 24);
            });

            // Visitation: the visitors' lobby (their own door in the south wall) and the hall; the inmates come in from the court or the east corridor.
            Room("Visitors' lobby", 69, 0, 94, 10, paved, "RP_StaffOnly").Door(Side.S, 80, Staff).Door(Side.N, 80, Staff).With(r =>
            {
                for (int x = 72; x <= 90; x += 3) { Chair(x, 8, Rot4.South); Chair(x + 1, 8, Rot4.South); }
                for (int x = 71; x <= 89; x += 6) Put("Shelf", x, 1);
                Put("PlantPot", 70, 9);
                Put("PlantPot", 93, 9);
                Cam(72, 3);
                Corners(r, heaters: true);
            });
            Room("Visitation hall", 69, 10, 94, 28, paved, "RP_Visitation").Door(Side.W, 20, Auto).Door(Side.E, 20, Auto).With(r =>
            {
                Put("RP_MetalDetector", 80, 11, Rot4.North);
                foreach (int z in new[] { 14, 18 })
                    foreach (int x in new[] { 74, 79, 84, 89 })
                        DiningTable(x, z, "we");
                for (int i = 0; i < 4; i++) Put("RP_VisitationBooth", 73 + 6 * i, 26);
                Cam(71, 27);
                Cam(92, 27);
                Corners(r, heaters: true);
            });

            // Infirmary (east end of the front row): medical beds, monitors, a doctor's desk; sterile floor. Its only door is on the corridor (a staff door in the perimeter
            // would stand open now and then for a doctor, and a patient on their feet could walk out through it).
            Room("Infirmary", 99, 0, 115, 16, sterile).Door(Side.W, 8, Auto).With(r =>
            {
                foreach (int z in new[] { 3, 9 })
                    for (int i = 0; i < 4; i++)
                    {
                        if (Put("HospitalBed", 101 + 3 * i, z, Rot4.North, null) is Building_Bed b) b.ForPrisoners = true;
                        Put("VitalsMonitor", 102 + 3 * i, z + 1);
                    }
                Put("RP_OfficeDesk", 108, 14, Rot4.North, null);
                Chair(108, 13, Rot4.North);
                Put("RP_ServingCounter", 101, 14);   // a tray counter for the patients (the cooks stock every counter)
                Put("Shelf", 111, 14);
                Put("PlantPot", 114, 14);
                Corners(r, heaters: true);
            });
            // Classroom B above it (its door on the east corridor).
            Room("Classroom B", 99, 16, 115, 28, paved, "RP_Classroom").Door(Side.W, 22, Auto).With(r => Desks(r, 102, 20));
        }

        // ---- middle tier: west block, east block, the two corridors ---------------------------------------------------------------

        private static void Middle(TerrainDef conc, TerrainDef paved, TerrainDef granite, TerrainDef sterile, TerrainDef wood)
        {
            // Corridors: each block has a corridor down its inside, with doors to the cross corridor at its head.
            Room("West corridor", 20, 28, 25, CorridorZ0, granite).Door(Side.N, 22, Auto).Door(Side.N, 23, Auto).With(r =>
            {
                for (int z = 34; z < CorridorZ0; z += 10) Lamp(21, z);
                Cam(23, 66);
                Heat(24, 31);
            });
            Room("East corridor", 94, 0, 99, CorridorZ0, granite).Door(Side.N, 96, Auto).Door(Side.N, 97, Auto).With(r =>
            {
                for (int z = 34; z < CorridorZ0; z += 10) Lamp(98, z);
                for (int z = 6; z < 28; z += 10) Lamp(98, z);
                Cam(96, 66);
                Cam(96, 4);
                Heat(95, 31);
            });

            // West block, west column: showers, laundry, library (their doors on the corridor).
            Room("Showers (west)", 4, 54, 20, CorridorZ0, granite, "RP_Shower").Door(Side.E, 61, Auto).Door(Side.N, 12, Auto).With(r =>
            {
                Row("RP_Shower", 7, 66, 6, 2);
                Row("RP_Shower", 7, 56, 6, 2);
                Chair(18, 61, Rot4.West);
                Corners(r, heaters: true);
            });
            Room("Laundry", 4, 40, 20, 54, conc, "RP_Laundry").Door(Side.E, 47, Auto).With(r =>
            {
                Row("RP_WashingMachine", 6, 52, 6, 2);
                PutF("Table2x2c", 7, 46, Rot4.North);
                PutF("Table2x2c", 12, 46, Rot4.North);
                Put("Shelf", 6, 41);
                Put("Shelf", 10, 41);
                Put("Shelf", 14, 41);
                Corners(r, heaters: true);
            });
            Room("Library", 4, 28, 20, 40, wood, "RP_Library").Door(Side.E, 34, Auto).With(r =>
            {
                for (int i = 0; i < 6; i++)
                    if (Put("RP_BookShelf", 6 + 2 * i, 38)?.TryGetComp<CompBookShelf>() is { } comp) comp.books = StartingBooks;
                DiningTable(8, 32, "we");
                DiningTable(14, 32, "we");
                Decor("PlantPot", 5, 29);
                Corners(r, heaters: true);
            });

            // West block, east column: the canteen (on the cross corridor, the yard and the corridor), the kitchen behind it, the freezer and dry store behind that.
            Room("Canteen (west)", 25, 54, 41, CorridorZ0, granite, "RP_Canteen").Door(Side.W, 61, Auto).Door(Side.E, 61, Auto).Door(Side.N, 33, Auto).Door(Side.S, 39, Auto).With(r => Canteen(r, 27));
            Room("Kitchen", 25, 40, 41, 54, granite, "RP_Kitchen").Door(Side.W, 47, Auto).With(r =>
            {
                foreach (int x in new[] { 28, 32, 36 })
                    AddCookBill(Put("ElectricStove", x, 52, Rot4.North));
                PutF("Table2x2c", 28, 46, Rot4.North);
                PutF("Table2x2c", 33, 46, Rot4.North);
                FoodShelf(38, 47);
                FoodShelf(38, 49);
                Corners(r, heaters: true);
            });
            Room("Dry store", 25, 28, 32, 40, conc, "RP_StaffOnly").Door(Side.N, 28, Staff).With(r =>
            {
                for (int z = 30; z <= 36; z += 3) { FoodShelf(26, z); FoodShelf(29, z); }
                Corners(r);
            });
            Room("Freezer", 32, 28, 41, 40, conc, "RP_StaffOnly").Door(Side.N, 36, Staff).With(r =>
            {
                // The cooler sits in the east wall: its cold side (west) is in the room, its hot side (east) in the open yard.
                Thing cooler = InWall("Cooler", 41, 33, Rot4.East);
                if (cooler?.TryGetComp<CompTempControl>() is { } tc) tc.targetTemperature = -4f;
                for (int z = 30; z <= 38; z += 4) { FoodShelf(33, z); FoodShelf(36, z); }
                Corners(r);
            });

            // East block, east column (the yard side): the canteen, the workshop, the classroom.
            Room("Canteen (east)", 78, 54, 94, CorridorZ0, granite, "RP_Canteen").Door(Side.W, 61, Auto).Door(Side.E, 61, Auto).Door(Side.N, 86, Auto).Door(Side.S, 89, Auto).With(r => Canteen(r, 80));
            Room("Kitchen (east)", 78, 40, 94, 54, granite, "RP_Kitchen").Door(Side.W, 47, Auto).Door(Side.E, 47, Auto).With(r =>
            {
                foreach (int x in new[] { 80, 85 })
                    AddCookBill(Put("ElectricStove", x, 52, Rot4.North));
                PutF("Table2x2c", 81, 46, Rot4.North);
                PutF("Table2x2c", 86, 46, Rot4.North);
                FoodShelf(90, 42);
                FoodShelf(90, 44);
                Corners(r, heaters: true);
            });
            Room("Classroom A", 78, 28, 94, 40, paved, "RP_Classroom").Door(Side.W, 34, Auto).Door(Side.E, 34, Auto).With(r => Desks(r, 81, 32));

            // East block, far column: showers, the chapel, the workshop.
            Room("Showers (east)", 99, 54, 115, CorridorZ0, granite, "RP_Shower").Door(Side.W, 61, Auto).Door(Side.N, 107, Auto).With(r =>
            {
                Row("RP_Shower", 102, 66, 6, 2);
                Row("RP_Shower", 102, 56, 6, 2);
                Chair(102, 61, Rot4.East);
                Corners(r, heaters: true);
            });
            Room("Chapel", 99, 40, 115, 54, wood, "RP_Chapel").Door(Side.W, 47, Auto).With(r =>
            {
                for (int z = 43; z <= 49; z += 2)
                {
                    foreach (int x in new[] { 102, 103, 104, 105, 107, 108, 109, 110 }) Chair(x, z, Rot4.East);
                }
                // The altar: a table with a sculpture, flowers either side.
                PutF("Table1x2c", 113, 46, Rot4.East);
                Put("SculptureLarge", 113, 47);
                Put("PlantPot", 113, 44);
                Put("PlantPot", 113, 49);
                Corners(r, heaters: true);
            });
            Room("Workshop", 99, 28, 115, 40, conc, "RP_Workshop").Door(Side.W, 34, Auto).With(r =>
            {
                Put("RP_MetalDetector", 100, 34, Rot4.East);
                Row("RP_LicensePlatePress", 102, 38, 3, 3, Rot4.South);
                Row("RP_LicensePlatePress", 102, 30, 3, 3, Rot4.North);
                Row("HandTailoringBench", 102, 36, 2, 4, Rot4.South, wood: true);      // the tailoring course's benches, and the tailors' (vanilla's)
                Stockpile(Rect(111, 32, 113, 36), StorageSettingsPreset.DefaultStockpile, food: false, only: ThingDefOf.Steel);
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 400;
                GenPlace.TryPlaceThing(steel, At(112, 34), map, ThingPlaceMode.Near);
                Corners(r, heaters: true);
            });
        }

        /// <summary>A canteen: three serving counters along the south wall (the kitchen's side), tables in rows.</summary>
        private static void Canteen(R r, int x0)
        {
            for (int i = 0; i < 3; i++) Put("RP_ServingCounter", x0 + 4 * i, 55);
            foreach (int z in new[] { 59, 63 })
                foreach (int x in new[] { x0 + 1, x0 + 6, x0 + 11 })
                    DiningTable(x, z, "we");
            if (r.X0 < 60) Put("RP_CommissaryCounter", x0 + 11, 66);
            Corners(r, heaters: true);
        }

        /// <summary>A classroom: desks in two rows, a teacher's table in front.</summary>
        private static void Desks(R r, int x0, int z0)
        {
            Row("RP_SchoolDesk", x0, z0, 6, 2);
            Row("RP_SchoolDesk", x0, z0 + 3, 6, 2);
            PutF("Table2x2c", x0 + 4, z0 + 6, Rot4.North);
            Corners(r, heaters: true);
        }

        // ---- north tier: the cross corridor, the hub and the wings -----------------------------------------------------------------

        private static void North(TerrainDef conc, TerrainDef paved, TerrainDef granite, TerrainDef carpet, TerrainDef wood, TerrainDef sterile)
        {
            Room("Cross corridor", 4, CorridorZ0, 115, CorridorZ1, granite)
                .Door(Side.S, 52, Auto).Door(Side.S, 59, Auto).Door(Side.S, 60, Auto).Door(Side.S, 67, Auto)
                .With(r =>
                {
                    for (int x = 12; x < 115; x += 10) Lamp(x, 71);
                    foreach (int x in new[] { 8, 30, 52, 67, 89, 111 }) Cam(x, 72);
                    Heat(6, 70);
                    Heat(113, 70);
                });

            // The hub: control (the camera monitors), briefing, records, and the command suite behind the control room.
            Room("Control room", 53, WingZ0, 66, 85, paved, "RP_StaffOnly").Door(Side.S, GateX, Staff).Door(Side.N, GateX, Staff).With(r =>
            {
                for (int i = 0; i < 5; i++) Put("FlatscreenTelevision", 54 + 2 * i, 84, Rot4.South);
                PutF("Table2x2c", 57, 80, Rot4.North);
                PutF("Table2x2c", 61, 80, Rot4.North);
                foreach (int x in new[] { 57, 58, 61, 62 }) Chair(x, 79, Rot4.North);
                Corners(r, heaters: true);
            });
            // A medical ward beside the control room, close to every wing: the injured are carried to the nearest medical bed, and doctors and officers pass here all day.
            Room("Medical ward", 46, WingZ0, 53, 81, sterile, null).Door(Side.S, 49, Auto).With(r =>
            {
                foreach (int z in new[] { 75, 78 })
                    for (int i = 0; i < 3; i++)
                    {
                        if (Put("HospitalBed", 47 + 2 * i, z, Rot4.North, null) is Building_Bed b) b.ForPrisoners = true;
                        Put("VitalsMonitor", 48 + 2 * i, z + 1);
                    }
                Put("RP_ServingCounter", 50, 74);   // a tray counter for the patients
                Decor("PlantPot", 47, 74);
                Corners(r, heaters: true);
                // (The corner spot of the second heater is under the north row's bed: one at the west end of the walkway between the two rows of beds instead.)
                Heat(47, 77);
            });
            // The psych wing, off the ward's north end (the doctor's rounds are one door away): two padded cells, each a room of its own with a padded bed, a toilet and a lamp, behind a security door.
            Room("Padded cell A", 46, 81, 49, 85, sterile, "RP_WingPsych").Door(Side.S, 48, Auto, true).With(r => PaddedCell(47, 82, 48, 84, 47, 84));
            Room("Padded cell B", 49, 81, 53, 85, sterile, "RP_WingPsych").Door(Side.S, 51, Auto, true).With(r =>
            {
                PaddedCell(50, 82, 52, 84, 50, 84);
                Heat(52, 82);
            });
            Room("Records office", 66, WingZ0, 73, 85, carpet, "RP_StaffOnly").Door(Side.S, 69, Staff).With(r =>
            {
                Put("RP_OfficeDesk", 69, 82, Rot4.North, null);
                Chair(69, 81, Rot4.North);
                Put("RP_OfficeDesk", 71, 82, Rot4.North, null);
                Chair(71, 81, Rot4.North);
                Put("Shelf", 68, 76);
                Put("Shelf", 71, 76);
                Corners(r, heaters: true);
            });
            Room("Command suite", 46, 85, 73, 93, carpet, "RP_StaffOnly").With(r =>
            {
                // A sickbay for the staff in the corner: a hospital bed set for prisoners takes no colonist, and the infirmary's and the ward's all are, so a member of staff who was downed had no bed to be rescued
                // to and lay where they fell until they bled out (and a room of both kinds of bed is not a prison cell any more, so these have a room of their own).
                foreach (int x in new[] { 48, 50, 54 })
                {
                    Put("HospitalBed", x, 90, Rot4.North, null);
                    Put("VitalsMonitor", x + 1, 91);
                }
                DiningTable(52, 88, "we");
                PutF("Armchair", 62, 88, Rot4.South);
                PutF("Armchair", 64, 88, Rot4.South);
                PutF("EndTable", 63, 88);
                PutF("Couch", 66, 90, Rot4.South);
                Decor("PlantPot", 47, 86);
                Decor("PlantPot", 72, 86);
                Put("Bookcase", 55, 92);
                Corners(r, heaters: true);
            });

            // The dormitory (minimum security): four rows of beds, toilets along the east wall, a day area with a television at the north end.
            Room("Dormitory", 4, WingZ0, 18, 93, granite).Door(Side.S, 11, Auto).With(r =>
            {
                PrisonZoneDef min = Z("RP_WingMinimum");
                Paint(r.IX0, r.IZ0, r.IX1, 88, min);
                Paint(r.IX0, 89, r.IX1, r.IZ1, Z("RP_CommonRoom"));
                foreach (int z in new[] { 75, 79, 83, 87 })
                    foreach (int x in new[] { 6, 9, 12, 15 })
                        if (PutF("Bed", x, z, Rot4.North) is Building_Bed b) b.ForPrisoners = true;
                foreach (int z in new[] { 75, 79, 83, 87 }) Put("RP_Toilet", 17, z, Rot4.East);
                Put("TubeTelevision", 11, 92, Rot4.South);
                foreach (int x in new[] { 9, 10, 11, 12, 13 }) Chair(x, 90, Rot4.North);
                DiningTable(6, 91, "e");
                Cam(11, 74);
                Lamp(5, 77); Lamp(5, 85); Lamp(17, 89);
                Heat(5, 81);
                Heat(17, 85);
            });
        }

        private static void Wings(TerrainDef conc, TerrainDef paved, TerrainDef carpet, TerrainDef granite)
        {
            Wing("Medium block", 18, 5, "RP_WingMedium", Auto, conc);
            Wing("Protective custody", 32, 4, "RP_WingProtective", Auto, conc);
            Wing("Maximum block", 73, 4, "RP_WingMaximum", Auto, conc);
            // Segregation: two solitary cells a side by the entrance, then the supermax unit behind a security door (two cells a side, a shower room and a yard at the far end).
            Wing("Segregation", 87, 5, "RP_SolitaryZone", Auto, conc, supermaxFrom: 2);
            Wing("Death row", 101, 2, "RP_WingDeathRow", Auto, conc);
            // The north ends of the shorter wings: a shower block for each (no bed is far from a shower), and a small office on the protective custody wing's.
            foreach ((string name, int x0) in new[] { ("Showers (protective custody)", 32), ("Showers (maximum)", 73) })
            {
                int ox = x0;
                Room(name, ox, WingZ0 + 16, ox + 14, 93, granite, "RP_Shower").Door(Side.S, ox + 7, Auto).With(r =>
                {
                    Row("RP_Shower", ox + 3, 91, 4, 2);
                    Lamp(ox + 1, 92);
                    Lamp(ox + 13, 92);
                    Heat(ox + 13, 90);
                });
            }
            // Death row: the execution chamber at the end of the wing, a guard office behind it.
            Room("Execution chamber", 101, WingZ0 + 8, 115, 89, paved, "RP_StaffOnly").Door(Side.S, 108, Staff).Door(Side.N, 108, Staff).With(r =>
            {
                Put("RP_ExecutionTable", 108, 84, Rot4.North, null);
                foreach (int x in new[] { 104, 105, 111, 112 }) Chair(x, 86, Rot4.South);
                Corners(r, heaters: true);
            });
            Room("Death row office", 101, 89, 115, 93, carpet, "RP_StaffOnly").With(r =>
            {
                Put("RP_OfficeDesk", 108, 92, Rot4.North, null);
                Chair(108, 91, Rot4.North);
                Corners(r);
            });
        }

        /// <summary>
        /// A housing wing: a corridor along z with cells on both sides (<paramref name="perSide"/> each), the entrance in the south wall (to the
        /// cross corridor), a camera at the far end. Cells are 4 deep by 3 wide: bed against the outer wall, toilet, heater, lamp; the door in the
        /// middle of the front wall.
        /// </summary>
        private static void Wing(string name, int x0, int perSide, string wingZone, PrisonDoorMode entrance, TerrainDef conc, int supermaxFrom = -1)
        {
            int z1 = WingZ0 + 4 * perSide;
            PrisonZoneDef zone = Z(wingZone);
            Room(name, x0, WingZ0, x0 + 14, z1, conc).Door(Side.S, x0 + 7, entrance, true).With(_ =>
            {
                WallLine(x0 + 5, WingZ0, x0 + 5, z1);
                WallLine(x0 + 9, WingZ0, x0 + 9, z1);
                for (int k = 1; k < perSide; k++)
                {
                    WallLine(x0, WingZ0 + 4 * k, x0 + 5, WingZ0 + 4 * k);
                    WallLine(x0 + 9, WingZ0 + 4 * k, x0 + 14, WingZ0 + 4 * k);
                }
                for (int k = 0; k < perSide; k++)
                {
                    int zb = WingZ0 + 1 + 4 * k;
                    // (A wing with a supermax unit: the last place on each side is the unit's shower room and yard, the places from its first are supermax cells.)
                    if (supermaxFrom >= 0 && k == perSide - 1)
                    {
                        SupermaxAnnex(x0, zb);
                        continue;
                    }
                    PrisonZoneDef cellZone = supermaxFrom >= 0 && k >= supermaxFrom ? Z("RP_WingSupermax") : zone;
                    Door(x0 + 5, zb + 1, Auto, security: true);
                    if (PutF("Bed", x0 + 1, zb, Rot4.East) is Building_Bed bw) bw.ForPrisoners = true;
                    Put("RP_Toilet", x0 + 1, zb + 2);
                    Heat(x0 + 2, zb + 2);
                    Lamp(x0 + 4, zb + 2);
                    Paint(x0 + 1, zb, x0 + 4, zb + 2, cellZone);
                    Door(x0 + 9, zb + 1, Auto, security: true);
                    if (PutF("Bed", x0 + 12, zb, Rot4.West) is Building_Bed be) be.ForPrisoners = true;
                    Put("RP_Toilet", x0 + 13, zb + 2);
                    Heat(x0 + 12, zb + 2);
                    Lamp(x0 + 10, zb + 2);
                    Paint(x0 + 10, zb, x0 + 13, zb + 2, cellZone);
                }
                if (supermaxFrom >= 0)
                {
                    // The unit's own door across the corridor, in line with the wall between the last solitary cells and the first supermax cells: staff only (an inmate passes it only on a guard's walk).
                    int zd = WingZ0 + 4 * supermaxFrom;
                    WallLine(x0 + 6, zd, x0 + 6, zd);
                    WallLine(x0 + 8, zd, x0 + 8, zd);
                    Door(x0 + 7, zd, Staff, security: true);
                    Lamp(x0 + 6, zd + 1);
                    Cam(x0 + 6, zd - 1);
                }
                for (int z = WingZ0 + 3; z < z1 - 1; z += 8) Lamp(x0 + 7, z);
                Cam(x0 + 6, z1 - 1);
                Heat(x0 + 8, z1 - 1);
            });
        }

        /// <summary>A padded cell's furnishing: the padded bed along its west wall from (<paramref name="bedX"/>, <paramref name="bedZ"/>), a toilet and a lamp.</summary>
        private static void PaddedCell(int bedX, int bedZ, int toiletX, int toiletZ, int lampX, int lampZ)
        {
            if (Put("RP_PaddedBed", bedX, bedZ, Rot4.North, null) is Building_Bed bed) bed.ForPrisoners = true;
            Put("RP_Toilet", toiletX, toiletZ);
            Lamp(lampX, lampZ);
        }

        /// <summary>
        /// The far end of the supermax unit: on the west side a shower room with two showers (painted with the wing, so no other inmate's shower is ever there), on the east a small yard of its own
        /// (the supermax yard zone), each behind a door from the corridor.
        /// </summary>
        private static void SupermaxAnnex(int x0, int zb)
        {
            Door(x0 + 5, zb + 1, Auto, security: true);
            Paint(x0 + 1, zb, x0 + 4, zb + 2, Z("RP_WingSupermax"));
            Row("RP_Shower", x0 + 2, zb + 1, 2, 2);
            Lamp(x0 + 1, zb + 2);
            Heat(x0 + 4, zb + 2);
            Door(x0 + 9, zb + 1, Auto, security: true);
            Paint(x0 + 10, zb, x0 + 13, zb + 2, Z("RP_SupermaxYard"));
            Lamp(x0 + 13, zb + 2);
        }

        // ---- the yard -------------------------------------------------------------------------------------------------------------------

        /// <summary>The guard post in the middle of the yard's north side.</summary>
        private static void YardRooms(TerrainDef conc)
        {
            Room("Yard guard post", 57, 60, 62, 65, conc, "RP_StaffOnly").Door(Side.S, 59, Staff).Door(Side.S, 60, Staff).With(r =>
            {
                PutF("Table2x2c", 59, 63, Rot4.North);
                Chair(58, 63, Rot4.East);
                Chair(61, 63, Rot4.West);
                Lamp(58, 61);
                Cam(61, 61);
            });
        }

        private static void YardFurniture(TerrainDef paved)
        {
            // Gym (west): a pad of tile with eight weights benches in two rows.
            Floor(43, 33, 53, 42, paved);
            for (int i = 0; i < 4; i++) { Put("RP_WeightBench", 45 + 2 * i, 35); Put("RP_WeightBench", 45 + 2 * i, 39); }
            // Sports (east): a court with hoops and horseshoes, tables for chess and cards, a billiards table.
            Floor(66, 33, 76, 51, paved);
            foreach (int x in new[] { 67, 70, 73, 76 }) Put("HoopstoneRing", x, 34);
            foreach (int z in new[] { 38, 41 }) { Put("HorseshoesPin", 67, z); Put("HorseshoesPin", 75, z); }
            foreach (int z in new[] { 36, 42 }) { Put("HorseshoesPin", 69, z); Put("HorseshoesPin", 73, z); }
            Put("ChessTable", 67, 45); Chair(66, 45, Rot4.East); Chair(68, 45, Rot4.West);
            Put("ChessTable", 75, 45); Chair(74, 45, Rot4.East); Chair(76, 45, Rot4.West);
            Put("PokerTable", 70, 44, Rot4.North, null);
            foreach (int z in new[] { 44, 45 }) { Chair(69, z, Rot4.East); Chair(72, z, Rot4.West); }
            Put("BilliardsTable", 70, 48, Rot4.North, null);
            // Games in the west, between the gym and the garden: chess, cards, billiards.
            foreach (int x in new[] { 45, 49, 52 }) { Put("ChessTable", x, 46); Chair(x - 1, 46, Rot4.East); Chair(x + 1, 46, Rot4.West); }
            foreach (int x in new[] { 45, 50 }) { Put("PokerTable", x, 49, Rot4.North, null); foreach (int z in new[] { 49, 50 }) { Chair(x - 1, z, Rot4.East); Chair(x + 2, z, Rot4.West); } }
            Put("BilliardsTable", 44, 53, Rot4.North, null);
            // Television in the middle, seats in front; picnic tables, payphones against the block walls, telescopes.
            Put("TubeTelevision", 59, 41, Rot4.South);

            foreach (int x in new[] { 57, 58, 59, 60, 61 }) Chair(x, 38, Rot4.North);
            foreach (int x in new[] { 58, 59, 60 }) Chair(x, 37, Rot4.North);
            DiningTable(54, 47, "we");
            DiningTable(63, 47, "we");
            DiningTable(58, 52, "we");
            foreach (int z in new[] { 36, 44, 52, 60 })
            {
                Put("RP_PhoneBooth", 42, z, Rot4.West);
                Put("RP_PhoneBooth", 77, z, Rot4.East);
            }
            Put("Telescope", 75, 62, Rot4.North, ThingDefOf.Steel);
            Put("Telescope", 54, 66, Rot4.North, ThingDefOf.Steel);
            // The garden: two plots of soil with potatoes and rice.
            foreach ((int x0, string crop) in new[] { (43, "Plant_Potato"), (49, "Plant_Rice") })
            {
                Floor(x0, 57, x0 + 4, 65, TerrainDefOf.Soil);
                var patch = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(patch);
                foreach (IntVec3 c in Rect(x0, 57, x0 + 4, 65))
                {
                    patch.AddCell(c);
                    PrisonMap.Of(map).zones.Set(c, RPLaborDefOf.RP_Garden);
                }
                patch.SetPlantDefToGrow(DefDatabase<ThingDef>.GetNamed(crop));
            }
            // Light and cameras round the yard's edge, in the entry court too.
            for (int x = 45; x <= 75; x += 6) { LampIfFree(x, 30); LampIfFree(x, 66); Cam(x + 3, 30); }
            for (int z = 33; z <= 63; z += 6) { LampIfFree(43, z); LampIfFree(76, z); }
            Cam(43, 66);
            Cam(76, 66);
            Cam(55, 14);
            Cam(64, 14);
            foreach (int z in new[] { 15, 21, 27 }) { LampIfFree(52, z); LampIfFree(67, z); }
            Decor("PlantPot", 52, 25);
            Decor("PlantPot", 67, 25);
            Put("SculptureLarge", 59, 20, Rot4.North, null);
        }

        // ---- perimeter, towers, the road and what lies outside ------------------------------------------------------------------------

        private static void Perimeter()
        {
            // A tower in each corner of the sterile strip, cameras along the strips, lamps between them.
            foreach ((int x, int z) in new[] { (1, 1), (PenWidth - 3, 1), (1, PenDepth - 3), (PenWidth - 3, PenDepth - 3) })
                Put("RP_GuardTower", x, z);
            for (int z = 12; z < PenDepth - 4; z += 14)
            {
                Cam(2, z);
                Cam(PenWidth - 3, z);
                LampIfFree(1, z + 7);
                LampIfFree(PenWidth - 2, z + 7);
            }
            for (int x = 14; x < PenWidth - 14; x += 14)
            {
                Cam(x, PenDepth - 3);
                LampIfFree(x + 7, PenDepth - 2);
            }
        }

        private static void Outside(TerrainDef conc, TerrainDef paved)
        {
            // The road from the map's south edge to the gate, the apron in front of it, the path to the landing pad.
            Floor(GateX - 1, -origin.z, GateX + 2, -1, conc);
            Floor(GateX - 5, -6, GateX + 6, -1, paved);
            Floor(ShuttlePadLocal.x + 9, -11, GateX - 2, -10, conc);
            // The landing pad: a paved ring round concrete, a lamp at each corner (the pad building goes in the middle when the mod has one).
            (int px, int pz, int pw, int ph) = ShuttlePadLocal;
            Floor(px, pz, px + pw - 1, pz + ph - 1, paved);
            Floor(px + 1, pz + 1, px + pw - 2, pz + ph - 2, conc);
            // The marking: an H in light tile (two bars and the crossbar).
            TerrainDef mark = Terrain("TileSandstone", "TileLimestone", "PavedTile");
            for (int dz = 0; dz < 5; dz++) { Floor(px + 2, pz + 1 + dz, px + 2, pz + 1 + dz, mark); Floor(px + pw - 3, pz + 1 + dz, px + pw - 3, pz + 1 + dz, mark); }
            Floor(px + 3, pz + 3, px + pw - 4, pz + 3, mark);
            foreach ((int x, int z) in new[] { (px - 1, pz - 1), (px + pw, pz - 1), (px - 1, pz + ph), (px + pw, pz + ph) }) Lamp(x, z);
            if (DefDatabase<ThingDef>.GetNamedSilentFail("RP_ShuttlePad") is { } pad) Put(pad.defName, px + (pw - pad.size.x) / 2, pz + (ph - pad.size.z) / 2);
            // The deliveries yard east of the road: deliveries, exports, a stockpile for materials.
            Floor(66, -16, 94, -3, conc);
            Paint(67, -15, 76, -4, RPDefOf.RP_Deliveries);
            Paint(79, -15, 82, -4, Z("RP_Exports"));
            Stockpile(Rect(85, -15, 92, -4), StorageSettingsPreset.DefaultStockpile, food: false);
            Stockpile(Rect(79, -15, 82, -4), StorageSettingsPreset.DefaultStockpile, food: false, only: DefDatabase<ThingDef>.GetNamed("RP_LicensePlates"));
            foreach (int x in new[] { 66, 77, 83, 94 }) Lamp(x, -3);
            foreach (int x in new[] { 52, 67 }) Lamp(x, -2);
            // The burial plot: bare soil (6 x 5, room for a dozen graves) just outside the south wall, behind the morgue (x 38-41), out of the inmates' way and inside the grounds.
            ClearArea(Rect(38, -6, 43, -2));
            Floor(38, -6, 43, -2, TerrainDefOf.Soil);
            Paint(38, -6, 43, -2, RPBurialDefOf.RP_BurialPlot);
        }

        /// <summary>The stores the cooks start from: food stockpiles in the freezer and the dry store, with the first stock in them, and the first meals on the counters.</summary>
        private static void Stores()
        {
            ThingDef meal = DefDatabase<ThingDef>.GetNamed("MealSimple");
            foreach (Building counter in map.listerBuildings.AllBuildingsColonistOfDef(D("RP_ServingCounter")).ToList())
                foreach (IntVec3 c in counter.OccupiedRect())
                {
                    Thing m = ThingMaker.MakeThing(meal);
                    m.stackCount = 10;
                    GenPlace.TryPlaceThing(m, c, map, ThingPlaceMode.Direct);
                }
            Stockpile(Rect(34, 29, 40, 39), StorageSettingsPreset.DefaultStockpile, food: true);
            Stockpile(Rect(26, 29, 31, 39), StorageSettingsPreset.DefaultStockpile, food: true);
            // (A day of raw food as well as the meals: 0.05 nutrition a potato, and a day of meals for forty-odd inmates is about 65. It used to be one stack of each, 7 nutrition, which is 15 meals.)
            foreach ((string def, int count, int x, int z) in new[] { ("RawPotatoes", 600, 37, 34), ("RawRice", 600, 37, 31), ("MealSimple", 40, 28, 33) })
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
                if (d == null) continue;
                for (int left = count; left > 0;)
                {
                    Thing t = ThingMaker.MakeThing(d);
                    t.stackCount = Math.Min(left, d.stackLimit);
                    left -= t.stackCount;
                    GenPlace.TryPlaceThing(t, At(x, z), map, ThingPlaceMode.Near);
                }
            }
        }

        // ---- people --------------------------------------------------------------------------------------------------------------

        /// <summary>Funds the penitentiary starts with (after its staff are on the payroll: they came with the facility).</summary>
        public const int PenStartFunds = 60000;

        /// <summary>The staff, besides the six the scenario's colonists become (cook, doctor, two guards, workman, janitor).</summary>
        private static readonly (string role, int count)[] PenStaff =
        {
            ("RP_Cook", 5), ("RP_Doctor", 5), ("RP_Guard", 12), ("RP_ArmedGuard", 4), ("RP_DogHandler", 1), ("RP_Workman", 1), ("RP_Janitor", 2),
            ("RP_Gardener", 1), ("RP_Administrator", 2), ("RP_Counsellor", 2), ("RP_Psychologist", 1), ("RP_Chaplain", 1),
        };

        /// <summary>Inmates at the start, by security level: each wing keeps a few beds free for the shuttle's first arrivals.</summary>
        private static readonly (string level, int count)[] PenPopulation =
        {
            ("RP_Minimum", 14), ("RP_Medium", 10), ("RP_Protective", 6), ("RP_Maximum", 7), ("RP_DeathRow", 3),
        };

        /// <summary>Where the guards are posted (their patrol zones, set in the Deployment tab), in the order the guards are listed.</summary>
        private static readonly string[] PenPosts =
        {
            "RP_Yard", "RP_Yard", "RP_Canteen", "RP_Shower", "RP_WingMinimum", "RP_WingMedium", "RP_WingMaximum", "RP_WingProtective", "RP_Visitation", "RP_Holding", "RP_Yard", "RP_Workshop",
        };

        /// <summary>Hires the staff, posts the guards, and admits the inmates into their wings (they start in their cells).</summary>
        private static void Populate(Map m)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return;
            foreach ((string role, int count) in PenStaff)
            {
                StaffRoleDef def = DefDatabase<StaffRoleDef>.GetNamed(role);
                for (int i = 0; i < count; i++)
                    if (!StaffUtility.Hire(m, def, free: true)) Errors.Add($"could not hire {role}");
            }
            // (The funds are the scenario's: the difficulty's level of PenStartFunds, which the new prison dialog shows. A start without the scenario, a test's, has the neutral level's.)
            if (Find.Scenario?.AllParts.OfType<ScenPart_PrisonStart>().Any() != true) game.ledger.funds = System.Math.Max(game.ledger.funds, PenStartFunds);
            int post = 0;
            foreach (Pawn p in m.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard))
                if (p.GetComp<CompStaff>() is { } sc && sc.role.defName == "RP_Guard") sc.patrolZone = Z(PenPosts[post++ % PenPosts.Length]);

            List<Pawn> arrived = AdmitStarting(m, game, PenPopulation);
            RPLog.Message($"penitentiary populated: {m.mapPawns.FreeColonistsSpawnedCount} staff, {arrived.Count} inmates");
        }

        /// <summary>
        /// One first fill with the numbers wanted at each level, then the daily intake goes back to what policy says. The inmates start in their cells, fed, rested, washed and calm (they have
        /// been inside a while, not the freshly generated state of an arrival).
        /// </summary>
        private static List<Pawn> AdmitStarting(Map m, PrisonGame game, (string level, int count)[] population)
        {
            var saved = new Dictionary<SecurityLevelDef, int>(game.intake.perDay);
            foreach ((string level, int count) in population) game.intake.perDay[DefDatabase<SecurityLevelDef>.GetNamed(level)] = count;
            List<Pawn> arrived = IntakeSystem.RunIntake(game, m, silent: true);
            foreach (KeyValuePair<SecurityLevelDef, int> kv in saved) game.intake.perDay[kv.Key] = kv.Value;
            foreach (Pawn p in arrived)
            {
                Building_Bed bed = p.ownership?.OwnedBed;
                if (bed == null) { Errors.Add($"{p.LabelShort} was admitted without a bed"); continue; }
                p.pather?.StopDead();
                p.Position = bed.Position;
                p.Notify_Teleported(false);
                CompInmate c = p.Inmate();
                c.awaitingSearch = false;
                c.processed = true;
                if (p.needs != null)
                {
                    if (p.needs.food != null) p.needs.food.CurLevelPercentage = 0.85f;
                    if (p.needs.rest != null) p.needs.rest.CurLevelPercentage = 0.9f;
                    if (p.needs.joy != null) p.needs.joy.CurLevelPercentage = 0.75f;
                    if (p.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { } hygiene) hygiene.CurLevel = 0.85f;
                    if (p.needs.TryGetNeed(RPDefOf.RP_Bladder) is { } bladder) bladder.CurLevel = 0.8f;
                }
                c.tension = System.Math.Min(c.tension, 5f);
            }
            return arrived;
        }
    }
}
