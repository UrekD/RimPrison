using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Intel overlay: every room with inmates is tinted by their average tension (green calm → red about to blow).
    /// Recomputed every 250 ticks while shown.
    /// </summary>
    public class TensionOverlay
    {
        private readonly Map map;
        private readonly Color[] cellColors;
        private readonly bool[] cellOn;
        private CellBoolDrawer drawer;
        private int lastTick = -999;

        public static bool Show;

        public TensionOverlay(Map map)
        {
            this.map = map;
            cellColors = new Color[map.cellIndices.NumGridCells];
            cellOn = new bool[map.cellIndices.NumGridCells];
        }

        public void Update()
        {
            if (!Show) return;
            if (Find.TickManager.TicksGame - lastTick >= 250 || drawer == null)
            {
                lastTick = Find.TickManager.TicksGame;
                Rebuild();
            }
            drawer.MarkForDraw();
            drawer.CellBoolDrawerUpdate();
        }

        private void Rebuild()
        {
            System.Array.Clear(cellOn, 0, cellOn.Length);
            var sums = new Dictionary<Room, (float sum, int n)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                Room r = p.GetRoom();
                if (r == null || r.IsHuge) continue;
                CompInmate c = p.Inmate();
                if (c == null) continue;
                sums.TryGetValue(r, out (float sum, int n) v);
                sums[r] = (v.sum + c.tension, v.n + 1);
            }
            foreach (KeyValuePair<Room, (float sum, int n)> kv in sums)
            {
                float t = kv.Value.sum / kv.Value.n / 100f;
                Color col = t < 0.5f ? Color.Lerp(new Color(0.2f, 0.8f, 0.3f), new Color(0.95f, 0.85f, 0.2f), t * 2f)
                                     : Color.Lerp(new Color(0.95f, 0.85f, 0.2f), new Color(0.95f, 0.15f, 0.1f), (t - 0.5f) * 2f);
                foreach (IntVec3 c in kv.Key.Cells)
                {
                    int i = map.cellIndices.CellToIndex(c);
                    cellOn[i] = true;
                    cellColors[i] = col;
                }
            }
            drawer ??= new CellBoolDrawer(i => cellOn[i], () => Color.white, i => cellColors[i], map.Size.x, map.Size.z, 3610, 0.35f);
            drawer.SetDirty();
        }
    }

    /// <summary>
    /// Gang turf overlay: every patch held by a gang the warden knows is tinted in that gang's colour (and labelled). A hidden gang's turf is
    /// not shown. Rebuilt, over the turf zones' cells only, when what it shows changes (the zones painted, a claim, a gang becoming known),
    /// checked every frame while shown so that it also follows changes made while the game is paused.
    /// </summary>
    public class TurfOverlay
    {
        private readonly Map map;
        private readonly Color[] cellColors;
        private readonly bool[] cellOn;
        private readonly HashSet<GangDef> known = new HashSet<GangDef>();
        private CellBoolDrawer drawer;
        private int shownFingerprint;

        public static bool Show;

        /// <summary>How many times it has been rebuilt (for tests).</summary>
        public int Rebuilds { get; private set; }

        public TurfOverlay(Map map)
        {
            this.map = map;
            cellColors = new Color[map.cellIndices.NumGridCells];
            cellOn = new bool[map.cellIndices.NumGridCells];
        }

        public void Update()
        {
            if (!Show) return;
            Refresh();
            drawer.MarkForDraw();
            drawer.CellBoolDrawerUpdate();
        }

        /// <summary>Rebuilds the tint if what it shows has changed since it was built (or it never was); says whether it did.</summary>
        public bool Refresh()
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (drawer != null && Fingerprint(pm) == shownFingerprint) return false;
            Rebuild();
            return true;
        }

        /// <summary>Everything the tint depends on, in one number: the painted zones, who holds each patch, and which of those gangs the warden knows.</summary>
        private int Fingerprint(PrisonMap pm)
        {
            if (pm == null) return 0;
            GangTurf.Reconcile(pm);
            known.Clear();
            GangTurf.KnownGangs(map, known);
            var h = new System.HashCode();
            h.Add(pm.zones.Version);
            foreach (TurfClaim claim in pm.turf.claims)
            {
                h.Add(claim.anchor);
                h.Add(claim.gang);
                h.Add(known.Contains(claim.gang));
            }
            return h.ToHashCode();
        }

        /// <summary>The colour a cell is tinted in, if it is (for tests).</summary>
        public Color? At(IntVec3 c) => c.InBounds(map) && cellOn[map.cellIndices.CellToIndex(c)] ? cellColors[map.cellIndices.CellToIndex(c)] : null;

        public void Rebuild()
        {
            Rebuilds++;
            System.Array.Clear(cellOn, 0, cellOn.Length);
            PrisonMap pm = PrisonMap.Of(map);
            shownFingerprint = Fingerprint(pm);
            if (pm != null)
            {
                // (Which gang each shown patch has, worked out once per patch, not per cell.)
                var gangOf = new Dictionary<int, GangDef>();
                foreach ((int pi, TurfClaim claim) in GangTurf.KnownTurf(map, pm)) gangOf[pi] = claim.gang;
                if (gangOf.Count > 0)
                    foreach (PrisonZoneDef zone in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                    {
                        if (!zone.gangTurf) continue;
                        foreach (IntVec3 c in pm.zones.CellsOf(zone))
                        {
                            if (!gangOf.TryGetValue(pm.zones.PatchAt(c), out GangDef gang)) continue;
                            int i = map.cellIndices.CellToIndex(c);
                            cellOn[i] = true;
                            cellColors[i] = gang.color;
                        }
                    }
            }
            drawer ??= new CellBoolDrawer(i => cellOn[i], () => Color.white, i => cellColors[i], map.Size.x, map.Size.z, 3612, 0.4f);
            drawer.SetDirty();
        }

        public void DrawLabels(PrisonMap pm)
        {
            foreach ((int pi, TurfClaim claim) in GangTurf.KnownTurf(map, pm))
                GenMapUI.DrawThingLabel(pm.zones.Patches[pi].center.MapToUIPosition(), "RP_TurfLabel".Translate(claim.gang.label, claim.zone.label), claim.gang.color);
        }
    }

    /// <summary>
    /// Security coverage: every cell inmates may use, coloured by who can see it (as the tension model counts watchers):
    /// a guard within 10 cells in line of sight 1, a manned tower 1, a working camera 0.5. Red nobody, amber a camera
    /// only, green watched, deeper green by two or more. Blind spots are where dealing, tool theft and fights go unseen.
    /// A dark cell (ground glow under 0.3 in a free-time zone or a corridor, see <see cref="TensionUtility.Dark"/>) is counted as the tension model counts it (a guard on foot sees 3 cells, a camera nothing, a tower's searchlight its whole radius) and shaded blue. Only the zones are drawn (a corridor is not, so a dark corridor is not shown); it is rebuilt when a watcher has moved or a device switched, else every <see cref="MaxTicks"/> ticks, so the dark can show up to that many ticks late.
    /// Each guard, camera and tower scans only its own radius (never every cell against every watcher).
    /// </summary>
    public class CoverageOverlay
    {
        private readonly Map map;
        private readonly float[] watch;
        private readonly Color[] cellColors;
        private readonly bool[] cellOn;
        /// <summary>Per cell while rebuilding: 0 not asked, 1 lit or not one the dark rule applies to, 2 dark (see <see cref="TensionUtility.Dark"/>).</summary>
        private readonly byte[] dark;
        private CellBoolDrawer drawer;
        private int lastTick = -999, lastCheckTick = -999;
        private static readonly Color DarkShade = new Color(0.1f, 0.15f, 0.6f);

        /// <summary>Rebuilding scans every guard's, camera's and tower's radius for sight lines (some nineteen thousand checks in a big prison), so it is redone only when what it shows
        /// has moved (a guard further than <see cref="MovedCells"/> from where they stood, one more or one less, a camera or a tower switched on or off), at most every <see cref="MinTicks"/>,
        /// and in any case every <see cref="MaxTicks"/>.</summary>
        public const int MinTicks = 250, MaxTicks = 1000, CheckTicks = 60;
        public const float MovedCells = 3f;

        private readonly Dictionary<Pawn, IntVec3> builtGuards = new Dictionary<Pawn, IntVec3>();
        private int builtDevices;

        public static bool Show;

        public CoverageOverlay(Map map)
        {
            this.map = map;
            watch = new float[map.cellIndices.NumGridCells];
            cellColors = new Color[map.cellIndices.NumGridCells];
            cellOn = new bool[map.cellIndices.NumGridCells];
            dark = new byte[map.cellIndices.NumGridCells];
        }

        public void Update()
        {
            if (!Show) return;
            int now = Find.TickManager.TicksGame;
            if (drawer == null || now < lastTick || now - lastTick >= MaxTicks || (now - lastTick >= MinTicks && Moved(now)))
            {
                lastTick = now;
                Rebuild();
            }
            drawer.MarkForDraw();
            drawer.CellBoolDrawerUpdate();
        }

        /// <summary>Whether the watchers are not where they were when it was built (looked at every <see cref="CheckTicks"/>).</summary>
        private bool Moved(int now)
        {
            if (now - lastCheckTick < CheckTicks && now >= lastCheckTick) return false;
            lastCheckTick = now;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return false;
            if (DeviceState(pm) != builtDevices) return true;
            int count = 0;
            foreach (Pawn g in pm.Guards)
            {
                if (!Watching(g)) continue;
                count++;
                if (!builtGuards.TryGetValue(g, out IntVec3 was) || !g.Position.InHorDistOf(was, MovedCells)) return true;
            }
            return count != builtGuards.Count;
        }

        private static bool Watching(Pawn g) => g.Spawned && !g.Downed && g.Awake();

        /// <summary>Which cameras are working and which towers manned, folded into one number.</summary>
        private static int DeviceState(PrisonMap pm)
        {
            var h = new System.HashCode();
            foreach (CompSecurityCamera cam in pm.Cameras) h.Add(cam.Working);
            foreach (CompGuardTower tower in pm.Towers) h.Add(tower.Manned);
            h.Add(pm.Cameras.Count);
            h.Add(pm.Towers.Count);
            return h.ToHashCode();
        }

        /// <summary>Watchers on a cell (also for tests).</summary>
        public float At(IntVec3 c) => c.InBounds(map) ? watch[map.cellIndices.CellToIndex(c)] : 0f;

        /// <summary>Whether the overlay shades a cell as dark (as built; also for tests).</summary>
        public bool DarkAt(IntVec3 c) => c.InBounds(map) && cellOn[map.cellIndices.CellToIndex(c)] && dark[map.cellIndices.CellToIndex(c)] == 2;

        public void Rebuild()
        {
            System.Array.Clear(watch, 0, watch.Length);
            System.Array.Clear(cellOn, 0, cellOn.Length);
            PrisonMap pm = PrisonMap.Of(map);
            builtGuards.Clear();
            if (pm == null) return;
            builtDevices = DeviceState(pm);
            lastTick = Find.TickManager.TicksGame;
            // (The dark rule as the tension model counts it, once per cell: a guard on foot sees only close by in the dark, a camera not at all, a tower as by day.)
            System.Array.Clear(dark, 0, dark.Length);
            bool IsDark(IntVec3 c)
            {
                int i = map.cellIndices.CellToIndex(c);
                if (dark[i] == 0) dark[i] = TensionUtility.Dark(c, map, pm) ? (byte)2 : (byte)1;
                return dark[i] == 2;
            }
            foreach (Pawn g in pm.Guards)
            {
                if (!Watching(g)) continue;
                builtGuards[g] = g.Position;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(g.Position, TensionUtility.GuardRadius, true))
                    if (c.InBounds(map) && (!IsDark(c) || c.InHorDistOf(g.Position, TensionUtility.DarkSight)) && GenSight.LineOfSight(g.Position, c, map, skipFirstCell: true)) watch[map.cellIndices.CellToIndex(c)] += 1f;
            }
            foreach (CompSecurityCamera cam in pm.Cameras)
                foreach (IntVec3 c in GenRadial.RadialCellsAround(cam.parent.Position, cam.Props.radius, true))
                    if (c.InBounds(map) && cam.Covers(c) && !IsDark(c)) watch[map.cellIndices.CellToIndex(c)] += 0.5f;
            foreach (CompGuardTower tower in pm.Towers)
                foreach (IntVec3 c in GenRadial.RadialCellsAround(tower.parent.Position, tower.Props.radius, true))
                    if (c.InBounds(map) && TensionUtility.TowerSees(tower, c)) watch[map.cellIndices.CellToIndex(c)] += 1f;
            int n = map.cellIndices.NumGridCells;
            for (int i = 0; i < n; i++)
            {
                // Every zone inmates are held in or sent to (solitary too); not staff-only rooms.
                if (pm.zones.ZoneAt(i) is not { staffOnly: false }) continue;
                IntVec3 cell = map.cellIndices.IndexToCell(i);
                if (cell.Impassable(map)) continue;
                float w = watch[i];
                cellOn[i] = true;
                Color col = w <= 0f ? new Color(0.95f, 0.15f, 0.1f) : w < 1f ? new Color(0.95f, 0.65f, 0.15f)
                    : w < 2f ? new Color(0.35f, 0.8f, 0.35f) : new Color(0.15f, 0.6f, 0.25f);
                // Dark cells are shaded blue: the colour is still who sees it, as little as a guard close by.
                cellColors[i] = IsDark(cell) ? Color.Lerp(col, DarkShade, 0.55f) : col;
            }
            drawer ??= new CellBoolDrawer(i => cellOn[i], () => Color.white, i => cellColors[i], map.Size.x, map.Size.z, 3611, 0.3f);
            drawer.SetDirty();
        }
    }

    /// <summary>Overlay toggles next to vanilla's (bottom right).</summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_PlaySettings_Toggles
    {
        private static Texture2D zonesIcon, tensionIcon, coverageIcon, turfIcon, groundsIcon, dangerIcon;

        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || PrisonGame.Instance == null) return;
            zonesIcon ??= ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Stockpile");
            tensionIcon ??= ContentFinder<Texture2D>.Get("UI/Commands/Attack", reportFailure: false) ?? BaseContent.BadTex;
            row.ToggleableIcon(ref RimPrisonMod.Settings.showZoneOverlay, zonesIcon, "RP_ToggleZones".Translate());
            row.ToggleableIcon(ref TensionOverlay.Show, tensionIcon, "RP_ToggleTension".Translate());
            coverageIcon ??= RPDefOf.RP_SecurityCamera.uiIcon ?? BaseContent.BadTex;
            row.ToggleableIcon(ref CoverageOverlay.Show, coverageIcon, "RP_ToggleCoverage".Translate() + "\n\n" + "RP_ToggleCoverageDark".Translate());
            turfIcon ??= ContentFinder<Texture2D>.Get("UI/Designators/Claim", reportFailure: false) ?? BaseContent.BadTex;
            row.ToggleableIcon(ref TurfOverlay.Show, turfIcon, "RP_ToggleTurf".Translate());
            dangerIcon ??= TexCommand.AttackMelee;
            row.ToggleableIcon(ref DangerOverlay.Show, dangerIcon, "RP_ToggleDanger".Translate());
            groundsIcon ??= ContentFinder<Texture2D>.Get("UI/Designators/HomeAreaOn", reportFailure: false) ?? BaseContent.BadTex;
            row.ToggleableIcon(ref GroundsOverlay.Show, groundsIcon, "RP_ToggleGrounds".Translate());
        }
    }
}
