using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>A connected patch of one zone: its first cell's index names it for as long as the patch keeps that cell.</summary>
    public readonly struct ZonePatch
    {
        public readonly PrisonZoneDef zone;
        public readonly int anchor, cells;
        public readonly Vector3 center;
        /// <summary>The patch's bounding box (cells, inclusive).</summary>
        public readonly CellRect bounds;

        public ZonePatch(PrisonZoneDef zone, int anchor, int cells, Vector3 center, CellRect bounds = default)
        {
            this.zone = zone;
            this.anchor = anchor;
            this.cells = cells;
            this.center = center;
            this.bounds = bounds;
        }
    }

    /// <summary>A kept patch as saved: the zone and the patch's first cell (its name) with the levels it is kept for.</summary>
    public class KeptPatch : IExposable
    {
        public PrisonZoneDef zone;
        public int anchor;
        public List<SecurityLevelDef> levels = new List<SecurityLevelDef>();

        public void ExposeData()
        {
            Scribe_Defs.Look(ref zone, "zone");
            Scribe_Values.Look(ref anchor, "anchor");
            Scribe_Collections.Look(ref levels, "levels", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) levels ??= new List<SecurityLevelDef>();
        }
    }

    /// <summary>
    /// Per-cell prison zone types for one map (byte per cell, 0 = none) plus lazily rebuilt cell lists per zone type.
    /// Painting is rare, so cell lists are rebuilt from the grid on first use after a change instead of being kept
    /// incrementally. <see cref="Version"/> lets other caches (door sides, room zones) notice changes.
    /// </summary>
    public class ZoneGrid : IExposable
    {
        private readonly Map map;
        private byte[] grid;
        private readonly Dictionary<PrisonZoneDef, List<IntVec3>> cells = new Dictionary<PrisonZoneDef, List<IntVec3>>();
        private bool cellsDirty = true;
        /// <summary>Faint always-on overlay, and a clear one while a prison zone tool is selected.</summary>
        private CellBoolDrawer drawer, drawerStrong;

        public int Version { get; private set; }

        public ZoneGrid(Map map)
        {
            this.map = map;
            grid = new byte[map.cellIndices.NumGridCells];
        }

        public PrisonZoneDef ZoneAt(IntVec3 c) => c.InBounds(map) ? PrisonZoneDef.FromGridIndex(grid[map.cellIndices.CellToIndex(c)]) : null;

        public PrisonZoneDef ZoneAt(int index) => PrisonZoneDef.FromGridIndex(grid[index]);

        public void Set(IntVec3 c, PrisonZoneDef zone)
        {
            int i = map.cellIndices.CellToIndex(c);
            byte v = zone?.gridIndex ?? 0;
            if (grid[i] == v) return;
            grid[i] = v;
            cellsDirty = true;
            Version++;
            drawer?.SetDirty();
            drawerStrong?.SetDirty();
        }

        /// <summary>Cells of a zone type; the list is shared, do not modify.</summary>
        public List<IntVec3> CellsOf(PrisonZoneDef zone)
        {
            if (cellsDirty) RebuildCells();
            return cells.TryGetValue(zone, out List<IntVec3> list) ? list : EmptyCells;
        }

        public bool Any(PrisonZoneDef zone) => CellsOf(zone).Count > 0;

        private static readonly List<IntVec3> EmptyCells = new List<IntVec3>();

        private void RebuildCells()
        {
            foreach (List<IntVec3> list in cells.Values) list.Clear();
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] == 0) continue;
                PrisonZoneDef def = PrisonZoneDef.FromGridIndex(grid[i]);
                if (def == null) continue;
                if (!cells.TryGetValue(def, out List<IntVec3> list)) cells[def] = list = new List<IntVec3>();
                list.Add(map.cellIndices.IndexToCell(i));
            }
            cellsDirty = false;
        }

        private readonly List<(PrisonZoneDef zone, Vector3 center)> labels = new List<(PrisonZoneDef, Vector3)>();
        private readonly List<ZonePatch> patches = new List<ZonePatch>();
        /// <summary>The patch (index into <see cref="Patches"/>) each cell belongs to, -1 for none; allocated on first use.</summary>
        private int[] patchOf;
        private int labelsVersion = -1;

        /// <summary>One label per connected patch of a zone (recomputed when the grid changes).</summary>
        public List<(PrisonZoneDef zone, Vector3 center)> Labels
        {
            get
            {
                if (labelsVersion != Version) RebuildLabels();
                return labels;
            }
        }

        /// <summary>Every connected patch of a zone (four cells or more), in the order of their first cell; shared, do not modify.</summary>
        public List<ZonePatch> Patches
        {
            get
            {
                if (labelsVersion != Version) RebuildLabels();
                return patches;
            }
        }

        // ---- patches kept for security levels ------------------------------------------------------------------------------------------------

        /// <summary>Per patch (parallel to <see cref="Patches"/>) the levels it is kept for; null = every level (the default).</summary>
        private SecurityLevelDef[][] keptOf = System.Array.Empty<SecurityLevelDef[]>();
        private int keptCount;
        /// <summary>Per patch (parallel to <see cref="Patches"/>) its cells; kept with the patches, so a pick inside one patch does not walk the whole zone.</summary>
        private readonly List<List<IntVec3>> patchCells = new List<List<IntVec3>>();
        /// <summary>Per zone the cells in none of its patches (painted in bits too small to be one).</summary>
        private readonly Dictionary<PrisonZoneDef, List<IntVec3>> looseCells = new Dictionary<PrisonZoneDef, List<IntVec3>>();
        /// <summary>Per zone, how many patches it has and how many cells those hold (a zone whose cells are all in one patch needs no search for the nearest).</summary>
        private readonly Dictionary<PrisonZoneDef, (int patches, int cells)> patchedOf = new Dictionary<PrisonZoneDef, (int, int)>();
        /// <summary>Kept patches read from a save, waiting for the first walk of the patches (which names them by their first cell).</summary>
        private List<KeptPatch> loadedKept;

        /// <summary>Is any patch kept for some levels? When not, every cell is open to everyone and nothing here needs to be asked.</summary>
        public bool AnyKept
        {
            get
            {
                if (labelsVersion != Version) RebuildLabels();
                return keptCount > 0;
            }
        }

        /// <summary>The cells of the patch (a cached list that stays valid until the zones are repainted; shared, do not modify), empty for no patch at all.</summary>
        public List<IntVec3> CellsOfPatch(int patch)
        {
            if (labelsVersion != Version) RebuildLabels();
            return patch >= 0 && patch < patchCells.Count ? patchCells[patch] : EmptyCells;
        }

        /// <summary>The zone's cells that are in none of its patches (shared, do not modify).</summary>
        public List<IntVec3> LooseCellsOf(PrisonZoneDef zone)
        {
            if (labelsVersion != Version) RebuildLabels();
            return looseCells.TryGetValue(zone, out List<IntVec3> list) ? list : EmptyCells;
        }

        /// <summary>The levels the patch is kept for, null for every level (or for no patch at all).</summary>
        public IReadOnlyList<SecurityLevelDef> KeptFor(int patch)
        {
            if (labelsVersion != Version) RebuildLabels();
            return patch >= 0 && patch < keptOf.Length ? keptOf[patch] : null;
        }

        /// <summary>May an inmate of this level be in this patch? Always, for a cell in no patch, a patch kept for none, and a null level (not an inmate).</summary>
        public bool Allows(int patch, SecurityLevelDef level)
        {
            if (level == null || patch < 0) return true;
            if (labelsVersion != Version) RebuildLabels();
            if (keptCount == 0 || patch >= keptOf.Length) return true;
            SecurityLevelDef[] kept = keptOf[patch];
            return kept == null || System.Array.IndexOf(kept, level) >= 0;
        }

        public bool Allows(IntVec3 c, SecurityLevelDef level) => level == null || !AnyKept || Allows(PatchAt(c), level);

        /// <summary>Keeps the patch for these levels (none: for every level again). Only a zone of a kind inmates share can be kept; returns false for any other.</summary>
        public bool SetKept(int patch, IEnumerable<SecurityLevelDef> levels)
        {
            if (labelsVersion != Version) RebuildLabels();
            if (patch < 0 || patch >= patches.Count || !patches[patch].zone.keepable) return false;
            var set = new List<SecurityLevelDef>();
            if (levels != null)
                foreach (SecurityLevelDef l in levels)
                    if (l != null && !set.Contains(l)) set.Add(l);
            set.Sort((a, b) => a.uiOrder.CompareTo(b.uiOrder));
            keptOf[patch] = set.Count == 0 ? null : set.ToArray();
            keptCount = 0;
            foreach (SecurityLevelDef[] k in keptOf)
                if (k != null) keptCount++;
            return true;
        }

        /// <summary>Does this zone have a cell an inmate of the level may be in (a cell in no patch, or in one that is not kept from them)?</summary>
        public bool AnyFor(PrisonZoneDef zone, SecurityLevelDef level)
        {
            List<IntVec3> all = CellsOf(zone);
            if (all.Count == 0) return false;
            if (level == null || !AnyKept) return true;
            // (Cells in no patch, a few of them painted apart, are open to all.)
            patchedOf.TryGetValue(zone, out (int patches, int cells) known);
            if (known.cells < all.Count) return true;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].zone == zone && Allows(i, level)) return true;
            return false;
        }

        /// <summary>How many patches the zone has, and how many cells they hold between them (the zone's other cells are painted in bits too small to be a patch).</summary>
        public int PatchesOf(PrisonZoneDef zone, out int patchedCells)
        {
            if (labelsVersion != Version) RebuildLabels();
            patchedOf.TryGetValue(zone, out (int patches, int cells) known);
            patchedCells = known.cells;
            return known.patches;
        }

        /// <summary>The index in <see cref="Patches"/> of the patch a cell is in, -1 when it is in none.</summary>
        public int PatchAt(IntVec3 c)
        {
            if (!c.InBounds(map)) return -1;
            if (labelsVersion != Version) RebuildLabels();
            return patchOf[map.cellIndices.CellToIndex(c)];
        }

        public int PatchAt(int index)
        {
            if (labelsVersion != Version) RebuildLabels();
            return index >= 0 && index < patchOf.Length ? patchOf[index] : -1;
        }

        private void RebuildLabels()
        {
            labelsVersion = Version;
            // (What was kept, and which patch each cell was in: a repaint hands a patch's levels on to the patch that has most of its cells.)
            SecurityLevelDef[][] prevKept = keptOf;
            int[] prevOf = keptCount > 0 && patchOf != null ? (int[])patchOf.Clone() : null;
            var prevZones = prevOf == null ? null : patches.ConvertAll(p => p.zone);
            var bestCount = new Dictionary<int, int>();
            var keptNow = new List<SecurityLevelDef[]>();
            labels.Clear();
            patches.Clear();
            patchedOf.Clear();
            patchCells.Clear();
            foreach (List<IntVec3> loose in looseCells.Values) loose.Clear();
            patchOf ??= new int[grid.Length];
            System.Array.Fill(patchOf, -1);
            var seen = new bool[grid.Length];
            var queue = new Queue<int>();
            var members = new List<int>();
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] == 0 || seen[i]) continue;
                byte z = grid[i];
                long sx = 0, sz = 0;
                int n = 0;
                int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                seen[i] = true;
                queue.Enqueue(i);
                members.Clear();
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    members.Add(idx);
                    IntVec3 c = map.cellIndices.IndexToCell(idx);
                    sx += c.x;
                    sz += c.z;
                    n++;
                    if (c.x < minX) minX = c.x;
                    if (c.x > maxX) maxX = c.x;
                    if (c.z < minZ) minZ = c.z;
                    if (c.z > maxZ) maxZ = c.z;
                    for (int k = 0; k < 4; k++)
                    {
                        IntVec3 nb = c + GenAdj.CardinalDirections[k];
                        if (!nb.InBounds(map)) continue;
                        int ni = map.cellIndices.CellToIndex(nb);
                        if (seen[ni] || grid[ni] != z) continue;
                        seen[ni] = true;
                        queue.Enqueue(ni);
                    }
                }
                if (n < 4) continue;
                PrisonZoneDef zone = PrisonZoneDef.FromGridIndex(z);
                keptNow.Add(zone == null ? null : KeptAfterRepaint(zone, i, members, prevOf, prevKept, prevZones, bestCount));
                var center = new Vector3(sx / (float)n + 0.5f, 0f, sz / (float)n + 0.5f);
                labels.Add((zone, center));
                // (The scan meets a patch at its lowest cell index, so that cell names the patch.)
                var memberCells = new List<IntVec3>(n);
                foreach (int idx in members)
                {
                    patchOf[idx] = patches.Count;
                    memberCells.Add(map.cellIndices.IndexToCell(idx));
                }
                patchCells.Add(memberCells);
                patches.Add(new ZonePatch(zone, i, n, center, CellRect.FromLimits(minX, minZ, maxX, maxZ)));
                if (zone != null)
                {
                    patchedOf.TryGetValue(zone, out (int patches, int cells) sofar);
                    patchedOf[zone] = (sofar.patches + 1, sofar.cells + n);
                }
            }
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] == 0 || patchOf[i] >= 0 || PrisonZoneDef.FromGridIndex(grid[i]) is not { } loose) continue;
                if (!looseCells.TryGetValue(loose, out List<IntVec3> list)) looseCells[loose] = list = new List<IntVec3>();
                list.Add(map.cellIndices.IndexToCell(i));
            }
            keptOf = keptNow.ToArray();
            keptCount = 0;
            foreach (SecurityLevelDef[] k in keptOf)
                if (k != null) keptCount++;
            loadedKept = null;
        }

        /// <summary>
        /// The levels a patch is kept for after the zones were painted: the old patch of the same zone that holds most of its cells gives its own (a split patch's parts all keep
        /// them, a merged one has its biggest part's, a patch with no old patch under it is open to all). The first walk after a load matches the saved patches by their first cell.
        /// </summary>
        private SecurityLevelDef[] KeptAfterRepaint(PrisonZoneDef zone, int anchor, List<int> members, int[] prevOf, SecurityLevelDef[][] prevKept, List<PrisonZoneDef> prevZones, Dictionary<int, int> counts)
        {
            if (!zone.keepable) return null;
            if (prevOf == null)
            {
                if (loadedKept == null) return null;
                foreach (KeptPatch k in loadedKept)
                    if (k.anchor == anchor && k.zone == zone && k.levels.Count > 0) return k.levels.ToArray();
                return null;
            }
            counts.Clear();
            foreach (int idx in members)
            {
                int old = prevOf[idx];
                if (old >= 0 && old < prevZones.Count && prevZones[old] == zone) counts[old] = (counts.TryGetValue(old, out int n) ? n : 0) + 1;
            }
            int best = -1, bestN = 0;
            foreach (KeyValuePair<int, int> kv in counts)
                if (kv.Value > bestN || kv.Value == bestN && kv.Key < best) (best, bestN) = (kv.Key, kv.Value);
            return best >= 0 && best < prevKept.Length ? prevKept[best] : null;
        }

        public void MarkForDraw(bool strong)
        {
            if (strong)
            {
                drawerStrong ??= new CellBoolDrawer(i => grid[i] != 0, () => Color.white, i => PrisonZoneDef.FromGridIndex(grid[i])?.color ?? Color.white,
                    map.Size.x, map.Size.z, 3600, 0.3f);
                drawerStrong.MarkForDraw();
                return;
            }
            drawer ??= new CellBoolDrawer(i => grid[i] != 0, () => Color.white, i => PrisonZoneDef.FromGridIndex(grid[i])?.color ?? Color.white,
                map.Size.x, map.Size.z, 3600, 0.07f);
            drawer.MarkForDraw();
        }

        public void DrawerUpdate()
        {
            drawer?.CellBoolDrawerUpdate();
            drawerStrong?.CellBoolDrawerUpdate();
        }

        public void ExposeData()
        {
            // Saved by defName so adding or removing zone defs does not scramble old saves.
            var names = new List<string>();
            var indices = new List<int>();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                foreach (PrisonZoneDef def in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                {
                    names.Add(def.defName);
                    indices.Add(def.gridIndex);
                }
            }
            // (The patches kept for some levels, named by their first cell; none in a save from before, and then every patch is open to all.)
            var kept = new List<KeptPatch>();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                List<ZonePatch> now = Patches;
                for (int i = 0; i < now.Count; i++)
                    if (KeptFor(i) is { Count: > 0 } levels) kept.Add(new KeptPatch { zone = now[i].zone, anchor = now[i].anchor, levels = new List<SecurityLevelDef>(levels) });
            }
            Scribe_Collections.Look(ref kept, "keptPatches", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                loadedKept = kept?.FindAll(k => k?.zone != null);
                keptOf = System.Array.Empty<SecurityLevelDef[]>();
                keptCount = 0;
                patchOf = null;
                patches.Clear();
            }
            Scribe_Collections.Look(ref names, "zoneNames", LookMode.Value);
            Scribe_Collections.Look(ref indices, "zoneIndices", LookMode.Value);
            byte[] data = grid;
            DataExposeUtility.LookByteArray(ref data, "grid");
            if (Scribe.mode == LoadSaveMode.LoadingVars && data != null && names != null && indices != null)
            {
                var remap = new byte[256];
                for (int k = 0; k < names.Count && k < indices.Count; k++)
                {
                    PrisonZoneDef def = DefDatabase<PrisonZoneDef>.GetNamedSilentFail(names[k]);
                    if (def != null && indices[k] is >= 0 and < 256) remap[indices[k]] = def.gridIndex;
                }
                if (data.Length == grid.Length)
                    for (int i = 0; i < data.Length; i++) grid[i] = remap[data[i]];
                cellsDirty = true;
                Version++;
            }
        }
    }
}
