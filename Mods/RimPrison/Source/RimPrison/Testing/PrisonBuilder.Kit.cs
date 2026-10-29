using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>The building blocks of the layouts: rooms with walls, floor and roof, doors, strict furniture placement, painting, a preview dump.</summary>
    public static partial class PrisonBuilder
    {
        /// <summary>A rectangle in local coordinates, corners inclusive, walls on its outer ring (rooms that touch share the wall between them).</summary>
        private readonly struct R
        {
            public readonly int X0, Z0, X1, Z1;
            public R(int x0, int z0, int x1, int z1) { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; }
            public int W => X1 - X0 + 1;
            public int H => Z1 - Z0 + 1;
            /// <summary>First and last interior cell.</summary>
            public int IX0 => X0 + 1;
            public int IZ0 => Z0 + 1;
            public int IX1 => X1 - 1;
            public int IZ1 => Z1 - 1;
            public int CX => (X0 + X1) / 2;
            public int CZ => (Z0 + Z1) / 2;
        }

        private static void ResetKit()
        {
            defCache.Clear();
            specs.Clear();
            builtRooms.Clear();
            canteenCell = workshopCell = corridorCell = default;
            gateOutside = new IntVec3(Width / 2, 0, -2);
        }

        // ---- defs ------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, ThingDef> defCache = new Dictionary<string, ThingDef>();

        private static ThingDef D(string defName)
        {
            if (!defCache.TryGetValue(defName, out ThingDef def)) defCache[defName] = def = DefDatabase<ThingDef>.GetNamed(defName);
            return def;
        }

        private static PrisonZoneDef Z(string defName) => DefDatabase<PrisonZoneDef>.GetNamed(defName);

        /// <summary>The first terrain def that exists of these names (carpets are generated per colour, so their names are tried in turn).</summary>
        private static TerrainDef Terrain(params string[] names)
        {
            foreach (string n in names)
                if (DefDatabase<TerrainDef>.GetNamedSilentFail(n) is { } t) return t;
            if (names.Any(n => n.StartsWith("Carpet")))
                if (DefDatabase<TerrainDef>.AllDefsListForReading.FirstOrDefault(t => t.defName.StartsWith("Carpet")) is { } carpet) return carpet;
            Errors.Add("no terrain among " + string.Join("/", names));
            return TerrainDefOf.Concrete;
        }

        // ---- primitives ------------------------------------------------------------------------------------------

        private static IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

        /// <summary>Local inclusive corners as a map rect.</summary>
        private static CellRect Rect(int x0, int z0, int x1, int z1) => new CellRect(origin.x + x0, origin.z + z0, x1 - x0 + 1, z1 - z0 + 1);

        private static void ClearArea(CellRect rect)
        {
            var doomed = new List<Thing>();
            foreach (IntVec3 c in rect.ClipInsideMap(map))
                foreach (Thing t in c.GetThingList(map))
                    if ((t.def.category is ThingCategory.Building or ThingCategory.Plant or ThingCategory.Filth || t.def.IsWithinCategory(ThingCategoryDefOf.Chunks)) && !doomed.Contains(t)) doomed.Add(t);
            foreach (Thing t in doomed)
            {
                if (t.Destroyed || !t.Spawned) continue;
                if (t.def.destroyable) t.Destroy();
                else t.DeSpawn();
            }
            foreach (IntVec3 c in rect.ClipInsideMap(map))
            {
                if (!c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)) map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
            }
        }

        private static void Floor(CellRect rect, TerrainDef terrain)
        {
            foreach (IntVec3 c in rect.ClipInsideMap(map)) map.terrainGrid.SetTerrain(c, terrain);
        }

        private static void Floor(int x0, int z0, int x1, int z1, TerrainDef terrain) => Floor(Rect(x0, z0, x1, z1), terrain);

        private static void Roof(CellRect rect)
        {
            foreach (IntVec3 c in rect.ClipInsideMap(map)) map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
        }

        private static void WallLine(int x0, int z0, int x1, int z1)
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (At(x, z).GetEdifice(map) == null) Spawn(ThingDefOf.Wall, x, z, Rot4.North, wallStuff);
        }

        private static void WallRect(int x0, int z0, int x1, int z1)
        {
            WallLine(x0, z0, x1, z0);
            WallLine(x0, z1, x1, z1);
            WallLine(x0, z0, x0, z1);
            WallLine(x1, z0, x1, z1);
        }

        /// <summary>A room: walls on the ring, a floor under the interior (and the ring), a roof over all of it.</summary>
        private static R Shell(int x0, int z0, int x1, int z1, TerrainDef floor, bool roof = true)
        {
            var r = new R(x0, z0, x1, z1);
            WallRect(x0, z0, x1, z1);
            if (floor != null) Floor(x0, z0, x1, z1, floor);
            if (roof) Roof(Rect(x0, z0, x1, z1));
            return r;
        }

        /// <summary>A door in a wall (the wall cell is replaced). Security doors where inmates must not get through (staff areas, perimeter) and on cells.</summary>
        private static Thing Door(int x, int z, PrisonDoorMode mode, bool security = false)
        {
            IntVec3 c = At(x, z);
            Building edifice = c.GetEdifice(map);
            if (edifice != null) edifice.Destroy();
            bool heavy = security || mode == PrisonDoorMode.StaffOnly;
            Thing door = heavy ? Spawn(D("RP_SecurityDoor"), x, z, Rot4.North) : Spawn(ThingDefOf.Door, x, z, Rot4.North, doorStuff);
            CompPrisonDoor comp = door?.TryGetComp<CompPrisonDoor>();
            if (comp != null) comp.mode = mode;
            return door;
        }

        /// <summary>Spawns a building with its anchor cell (the game's: the centre of odd sizes), refusing to overwrite another building (an error is recorded instead).</summary>
        private static Thing Spawn(ThingDef def, int x, int z, Rot4 rot, ThingDef stuff = null)
        {
            if (def == null) return null;
            IntVec3 c = At(x, z);
            if (def.MadeFromStuff && stuff == null) stuff = GenStuff.DefaultStuffFor(def);
            if (!def.MadeFromStuff) stuff = null;
            CellRect rect = GenAdj.OccupiedRect(c, rot, def.size);
            if (!rect.InBounds(map))
            {
                Errors.Add($"{def.defName} out of bounds at ({x},{z})");
                return null;
            }
            if (def.category == ThingCategory.Building)
                foreach (IntVec3 cell in rect)
                    foreach (Thing other in cell.GetThingList(map))
                        if (other.def.category == ThingCategory.Building)
                        {
                            Errors.Add($"{def.defName} at ({x},{z}) overlaps {other.def.defName} at ({cell.x - origin.x},{cell.z - origin.z})");
                            return null;
                        }
            Thing t = ThingMaker.MakeThing(def, stuff);
            t.SetFactionDirect(Faction.OfPlayer);
            // A shelf stores anything by default: here they are lockers and racks, and take nothing the hauling jobs would carry in (a food shelf is set by FoodShelf).
            if (t is Building_Storage { settings: { } shelfSettings } && def.defName == "Shelf") shelfSettings.filter.SetDisallowAll();
            return GenSpawn.Spawn(t, c, map, rot, WipeMode.VanishOrMoveAside);
        }

        /// <summary>A shelf that keeps the kitchen's ingredients (raw food and ingredients, no cooked meals: those go to the counters).</summary>
        private static void FoodShelf(int minX, int minZ)
        {
            if (Put("Shelf", minX, minZ) is Building_Storage { settings: { } settings })
            {
                settings.filter.SetAllow(ThingCategoryDefOf.Foods, true);
                settings.filter.SetAllow(DefDatabase<ThingCategoryDef>.GetNamed("FoodMeals"), false);
                settings.Priority = StoragePriority.Preferred;
            }
        }

        /// <summary>Spawns a building so its footprint starts at the given south-west cell (whatever its size and rotation).</summary>
        private static Thing Put(string defName, int minX, int minZ, Rot4 rot = default, ThingDef stuff = null)
        {
            ThingDef def = D(defName);
            // (The anchor of a big building, the shuttle pad's 9 x 7 among them, is up to half its size in from the corner.)
            int reach = System.Math.Max(2, System.Math.Max(def.size.x, def.size.z));
            for (int dx = 0; dx <= reach; dx++)
                for (int dz = 0; dz <= reach; dz++)
                {
                    CellRect r = GenAdj.OccupiedRect(At(minX + dx, minZ + dz), rot, def.size);
                    if (r.minX == origin.x + minX && r.minZ == origin.z + minZ) return Spawn(def, minX + dx, minZ + dz, rot, stuff);
                }
            Errors.Add($"{defName}: no anchor for ({minX},{minZ})");
            return null;
        }

        /// <summary>Same, in wood (or the furniture material).</summary>
        private static Thing PutF(string defName, int minX, int minZ, Rot4 rot = default) => Put(defName, minX, minZ, rot, furnitureStuff);

        /// <summary>A line of the same building, <paramref name="count"/> of them, <paramref name="step"/> cells apart along x (or z when <paramref name="alongZ"/>).</summary>
        private static void Row(string defName, int minX, int minZ, int count, int step, Rot4 rot = default, bool alongZ = false, bool wood = false)
        {
            for (int i = 0; i < count; i++)
            {
                int x = alongZ ? minX : minX + i * step, z = alongZ ? minZ + i * step : minZ;
                if (wood) PutF(defName, x, z, rot);
                else Put(defName, x, z, rot);
            }
        }

        private static void Lamp(int x, int z) => Spawn(D("StandingLamp"), x, z, Rot4.North, ThingDefOf.Steel);
        private static void LampIfFree(int x, int z)
        {
            if (FreeCell(x, z)) Lamp(x, z);
        }

        /// <summary>How far a lamp lights a cell in its line of sight for this pass (a standing lamp's glow is over 0.3 out to about ten cells; eight leaves room for the flood's path being longer than the line).</summary>
        internal const float LampReach = 8f;

        /// <summary>
        /// The finishing pass of the lamps: every cell where the dark rule applies (<see cref="TensionUtility.DarkMatters"/>: a free-time zone or a corridor, not a cell or a staff room) that no lamp reaches gets one, on the nearest free
        /// cell that sees it, so that a fight there is seen at night as it is by day (the layouts' own lamps cover most of it; the yards and the long corridors are what is left). Counts the lamps added.
        /// </summary>
        private static int LightTheFreeTime(PrisonMap pm)
        {
            ThingDef lampDef = D("StandingLamp");
            var lamps = new List<IntVec3>();
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
                if (b.def == lampDef) lamps.Add(b.Position);
            int added = 0;
            foreach (IntVec3 c in Footprint(map).ClipInsideMap(map))
            {
                if (c.Impassable(map) || !TensionUtility.DarkMatters(c, map, pm)) continue;
                if (lamps.Any(l => l.InHorDistOf(c, LampReach) && GenSight.LineOfSight(l, c, map, skipFirstCell: false))) continue;
                // (Against a wall first: a lamp in the middle of a corridor is a cell nobody can stand on and everybody walks round - eighty of them made a guard's walk to a post take over 1500 ticks.
                // In a corridor only against a wall; in a painted room or yard an open cell when no wall is near.)
                IntVec3 spot = IntVec3.Invalid;
                bool corridor = pm.zones.ZoneAt(c) == null;
                for (int pass = 0; pass < 2 && !spot.IsValid; pass++)
                {
                    if (pass == 1 && corridor) break;
                    foreach (IntVec3 x in GenRadial.RadialCellsAround(c, 4f, true))
                        if (x.InBounds(map) && !x.Impassable(map) && FreeCell(x.x - origin.x, x.z - origin.z) && TensionUtility.DarkMatters(x, map, pm) && GenSight.LineOfSight(x, c, map, skipFirstCell: false)
                            && (pass == 1 || GenAdj.CardinalDirections.Any(d => (x + d).InBounds(map) && (x + d).Impassable(map))))
                        {
                            spot = x;
                            break;
                        }
                }
                if (!spot.IsValid) continue;
                if (Spawn(lampDef, spot.x - origin.x, spot.z - origin.z, Rot4.North, ThingDefOf.Steel) == null) continue;
                lamps.Add(spot);
                added++;
            }
            return added;
        }

        private static void Heat(int x, int z) => Spawn(D("Heater"), x, z, Rot4.North);
        private static void Chair(int x, int z, Rot4 facing) => Spawn(D("DiningChair"), x, z, facing, furnitureStuff);

        /// <summary>A 2x2 table with its south-west cell at (x, z) and chairs facing it: any of west, east (two each, one with <paramref name="sides"/> = "w"/"e"), south and north (two each: "s"/"n").</summary>
        private static void DiningTable(int x, int z, string sides = "we")
        {
            PutF("Table2x2c", x, z, Rot4.North);
            foreach (char side in sides)
                for (int i = 0; i < 2; i++)
                    switch (side)
                    {
                        case 'w': Chair(x - 1, z + i, Rot4.East); break;
                        case 'e': Chair(x + 2, z + i, Rot4.West); break;
                        case 's': Chair(x + i, z - 1, Rot4.North); break;
                        case 'n': Chair(x + i, z + 2, Rot4.South); break;
                    }
        }

        private static void Paint(CellRect rect, PrisonZoneDef zone)
        {
            PrisonMap pm = PrisonMap.Of(map);
            foreach (IntVec3 c in rect.ClipInsideMap(map)) pm.zones.Set(c, zone);
        }

        /// <summary>Paints local inclusive corners.</summary>
        private static void Paint(int x0, int z0, int x1, int z1, PrisonZoneDef zone) => Paint(Rect(x0, z0, x1, z1), zone);

        /// <summary>Paints the interior of a room.</summary>
        private static void PaintIn(R r, PrisonZoneDef zone) => Paint(r.IX0, r.IZ0, r.IX1, r.IZ1, zone);

        private static Zone_Stockpile Stockpile(CellRect rect, StorageSettingsPreset preset, bool food, ThingDef only = null)
        {
            var zone = new Zone_Stockpile(preset, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            if (only != null)
            {
                zone.settings.filter.SetDisallowAll();
                zone.settings.filter.SetAllow(only, true);
                zone.settings.Priority = StoragePriority.Important;
            }
            else if (food)
            {
                zone.settings.filter.SetDisallowAll();
                zone.settings.filter.SetAllow(ThingCategoryDefOf.Foods, true);
                zone.settings.filter.SetAllow(DefDatabase<ThingCategoryDef>.GetNamed("FoodMeals"), false);
                zone.settings.Priority = StoragePriority.Preferred;
            }
            foreach (IntVec3 c in rect.ClipInsideMap(map))
                if (c.GetEdifice(map) == null) zone.AddCell(c);
            return zone;
        }

        // ---- rooms as a table ------------------------------------------------------------------------------------

        private enum Side : byte { S, N, W, E }

        /// <summary>One room of a layout table: where, what floor and zone, its doors (on the wall, at an x or z coordinate), and what goes in it.</summary>
        private sealed class Spec
        {
            public string Name;
            public R Rect;
            public TerrainDef Floor;
            public PrisonZoneDef Zone;
            public bool Roof = true;
            public readonly List<(Side side, int at, PrisonDoorMode mode, bool security)> Doors = new List<(Side, int, PrisonDoorMode, bool)>();
            public Action<R> Furnish;

            /// <summary>A door in the wall on <paramref name="side"/>; <paramref name="at"/> is the x (south, north walls) or z (west, east walls) of the door cell.</summary>
            public Spec Door(Side side, int at, PrisonDoorMode mode, bool security = false)
            {
                Doors.Add((side, at, mode, security));
                return this;
            }

            public Spec With(Action<R> furnish)
            {
                Furnish = furnish;
                return this;
            }
        }

        private static readonly List<Spec> specs = new List<Spec>();

        /// <summary>Adds a room to the table (walls on the outer ring, shared with a neighbour's); built by <see cref="BuildSpecs"/>.</summary>
        private static Spec Room(string name, int x0, int z0, int x1, int z1, TerrainDef floor, string zone = null, bool roof = true)
        {
            var spec = new Spec { Name = name, Rect = new R(x0, z0, x1, z1), Floor = floor, Zone = zone == null ? null : Z(zone), Roof = roof };
            specs.Add(spec);
            return spec;
        }

        /// <summary>Walls, floors and roofs of every room first, then the doors (they replace wall cells), the zones, and the furniture.</summary>
        private static void BuildSpecs()
        {
            foreach (Spec s in specs) Shell(s.Rect.X0, s.Rect.Z0, s.Rect.X1, s.Rect.Z1, s.Floor, s.Roof);
            foreach (Spec s in specs)
                foreach ((Side side, int at, PrisonDoorMode mode, bool sec) in s.Doors)
                {
                    int x = side == Side.W ? s.Rect.X0 : side == Side.E ? s.Rect.X1 : at, z = side == Side.S ? s.Rect.Z0 : side == Side.N ? s.Rect.Z1 : at;
                    if (x < s.Rect.X0 || x > s.Rect.X1 || z < s.Rect.Z0 || z > s.Rect.Z1) Errors.Add($"{s.Name}: door {side} {at} is off its wall");
                    else Door(x, z, mode, sec);
                }
            foreach (Spec s in specs)
                if (s.Zone != null) PaintIn(s.Rect, s.Zone);
            foreach (Spec s in specs) s.Furnish?.Invoke(s.Rect);
            foreach (Spec s in specs)
                builtRooms.Add(new BuiltRoom(s.Name, Rect(s.Rect.IX0, s.Rect.IZ0, s.Rect.IX1, s.Rect.IZ1), s.Zone, s.Floor?.defName));
        }

        /// <summary>A finished room of a layout table (interior in map coordinates), for the checks and the room table in the summary.</summary>
        public readonly struct BuiltRoom
        {
            public readonly string Name, Floor;
            public readonly CellRect Interior;
            public readonly PrisonZoneDef Zone;
            public BuiltRoom(string name, CellRect interior, PrisonZoneDef zone, string floor) { Name = name; Interior = interior; Zone = zone; Floor = floor; }
        }

        private static readonly List<BuiltRoom> builtRooms = new List<BuiltRoom>();
        /// <summary>The rooms the last layout table built (the penitentiary's; empty for the inherited facility).</summary>
        public static IReadOnlyList<BuiltRoom> Rooms => builtRooms;

        /// <summary>Whether nothing is built on the cell and no door is next to it (lamps and plants go where nobody walks through).</summary>
        private static bool FreeCell(int x, int z)
        {
            IntVec3 c = At(x, z);
            if (!c.InBounds(map)) return false;
            foreach (Thing t in c.GetThingList(map))
                if (t.def.category == ThingCategory.Building) return false;
            foreach (IntVec3 d in GenAdj.CardinalDirections)
                if ((c + d).GetEdifice(map) is Building_Door) return false;
            return true;
        }

        /// <summary>Lamps (and heaters) in the inner corners of a room, where they are free.</summary>
        private static void Corners(R r, bool lamps = true, bool heaters = false)
        {
            foreach ((int x, int z) in new[] { (r.IX0, r.IZ0), (r.IX1, r.IZ0), (r.IX0, r.IZ1), (r.IX1, r.IZ1) })
                if (FreeCell(x, z)) { if (lamps) Lamp(x, z); }
            // A big room is lit in the middle too: a lamp every eight cells or so wherever one is free.
            if (lamps && (r.W - 2) * (r.H - 2) >= 150)
                for (int x = r.IX0 + 5; x < r.IX1 - 2; x += 8)
                    for (int z = r.IZ0 + 4; z < r.IZ1 - 2; z += 7)
                        if (FreeCell(x, z)) Lamp(x, z);
            if (!heaters) return;
            foreach ((int x, int z) in new[] { (r.IX0, r.IZ1 - 1), (r.IX1, r.IZ0 + 1) })
                if (FreeCell(x, z)) Heat(x, z);
        }

        /// <summary>A building set into a wall (it replaces the wall cells it covers).</summary>
        private static Thing InWall(string defName, int minX, int minZ, Rot4 rot, ThingDef stuff = null)
        {
            ThingDef def = D(defName);
            IntVec2 size = rot.IsHorizontal ? new IntVec2(def.size.z, def.size.x) : def.size;
            for (int x = minX; x < minX + size.x; x++)
                for (int z = minZ; z < minZ + size.z; z++)
                    if (At(x, z).GetEdifice(map) is { } e && e.def == ThingDefOf.Wall) e.Destroy();
            return Put(defName, minX, minZ, rot, stuff);
        }

        /// <summary>A security camera where it is free (cameras are single-cell buildings, not wall-mounted).</summary>
        private static void Cam(int x, int z)
        {
            if (FreeCell(x, z)) Spawn(D("RP_SecurityCamera"), x, z, Rot4.North);
        }

        /// <summary>Plant pots and the like where free.</summary>
        private static void Decor(string defName, int x, int z, bool wood = false)
        {
            if (FreeCell(x, z)) Spawn(D(defName), x, z, Rot4.North, wood ? furnitureStuff : null);
        }

        // ---- preview ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Writes the finished layout to the log as text rows (north first), three characters a cell (thing, zone, floor), for
        /// tools/layout-png.py to draw. Only when asked: the layout and showcase plans, or RIMPRISON_LAYOUT=1.
        /// </summary>
        private static void DumpLayout()
        {
            if (AutoTest.Plan != "layout" && AutoTest.Plan != "showcase" && Environment.GetEnvironmentVariable("RIMPRISON_LAYOUT") != "1") return;
            PrisonMap pm = PrisonMap.Of(map);
            var zones = DefDatabase<PrisonZoneDef>.AllDefsListForReading;
            Log.Message($"[RimPrison:layout] size {Width} {Depth + SouthMargin} margin {SouthMargin} kind {Kind}");
            for (int i = 0; i < zones.Count; i++)
                Log.Message($"[RimPrison:layout] zone {ZoneChar(i)} {zones[i].defName} {zones[i].color.r:0.00} {zones[i].color.g:0.00} {zones[i].color.b:0.00}");
            for (int z = Depth - 1; z >= -SouthMargin; z--)
            {
                var sb = new StringBuilder(Width * 3);
                for (int x = 0; x < Width; x++)
                {
                    IntVec3 c = At(x, z);
                    PrisonZoneDef zone = pm.zones.ZoneAt(c);
                    sb.Append(GlyphAt(c)).Append(zone == null ? '.' : ZoneChar(zones.IndexOf(zone))).Append(FloorChar(c.GetTerrain(map)));
                }
                Log.Message($"[RimPrison:layout] z={z + SouthMargin:000} {sb}");
            }
        }

        private static char ZoneChar(int i) => i < 26 ? (char)('A' + i) : (char)('a' + (i - 26));

        private static char FloorChar(TerrainDef t)
        {
            string n = t.defName;
            if (n.StartsWith("Soil") || n.Contains("Grass")) return 's';
            if (n == "Concrete") return 'c';
            if (n == "PavedTile") return 'p';
            if (n == "SterileTile") return 'h';
            if (n.StartsWith("Carpet")) return 'r';
            if (n.StartsWith("Tile")) return 't';
            if (n.StartsWith("Wood")) return 'w';
            if (n == "Gravel" || n.StartsWith("Packed")) return 'g';
            return 'o';
        }

        private static char GlyphAt(IntVec3 c)
        {
            char best = ' ';
            int bestRank = -1;
            foreach (Thing t in c.GetThingList(map))
            {
                if (t.def.category != ThingCategory.Building) continue;
                (char g, int rank) = Glyph(t);
                if (rank > bestRank) (best, bestRank) = (g, rank);
            }
            return best;
        }

        private static (char, int) Glyph(Thing t)
        {
            ThingDef d = t.def;
            if (t is Building_Door)
                return (t.TryGetComp<CompPrisonDoor>()?.mode switch { PrisonDoorMode.Auto => 'a', PrisonDoorMode.Inmates => 'i', PrisonDoorMode.StaffOnly => 'S', _ => 'L' }, 9);
            if (d == ThingDefOf.Wall) return ('#', 8);
            if (d.defName == "Fence" || d.defName == "FenceGate") return ('f', 8);
            if (d.defName.Contains("Bed") && d.defName != "Bedroll") return (t is Building_Bed { Medical: true } ? 'H' : 'b', 5);
            if (d.defName == "RP_Toilet") return ('t', 5);
            if (d.defName == "RP_Shower") return ('w', 4);
            if (d.defName == "RP_SecurityCamera") return ('o', 7);
            if (d.defName == "RP_GuardTower") return ('X', 7);
            if (d.defName == "RP_MetalDetector") return ('m', 6);
            if (d.defName.Contains("Lamp") || d.defName == "FloodLight") return ('l', 2);
            if (d.defName == "Heater" || d.defName == "Cooler") return ('h', 2);
            if (d.building?.isSittable == true) return ('c', 3);
            if (d.surfaceType == SurfaceType.Eat || d.defName.StartsWith("Table")) return ('T', 4);
            if (d.defName.Contains("Stove")) return ('S', 4);
            if (d.defName == "RP_ServingCounter" || d.defName == "RP_CommissaryCounter") return ('C', 4);
            if (d.defName.Contains("Shelf") || d.defName.Contains("Bookcase") || d.defName == "RP_BookShelf" || d.defName == "Dresser" || d.defName == "ToolCabinet") return ('B', 4);
            if (d.defName == "RP_OfficeDesk") return ('D', 4);
            if (d.defName == "RP_SchoolDesk") return ('d', 4);
            if (d.defName == "RP_WeightBench") return ('W', 4);
            if (d.defName == "RP_LicensePlatePress" || d.defName == "RP_WashingMachine") return ('P', 4);
            if (d.defName == "RP_PhoneBooth" || d.defName == "RP_VisitationBooth") return ('p', 4);
            if (d.defName == "PlantPot" || d.defName.StartsWith("Sculpture")) return ('v', 3);
            return ('x', 1);
        }
    }
}
