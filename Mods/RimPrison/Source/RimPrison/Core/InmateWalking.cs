using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Walking distance for inmates, in cells, over a flood fill of the walkable cells where doors count only if an
    /// inmate may pass them. Vanilla's "closest thing" search falls back to straight-line distance, which picks a
    /// counter behind a wall (a 300-cell walk) over one a few doors away. The fill moves the way pawns do: diagonally too
    /// (a diagonal step costs 1.4 of a straight one, as in the pathfinder, and is not made around a corner). Main thread only
    /// (shared scratch buffers).
    /// </summary>
    public static class InmateWalking
    {
        /// <summary>Cost of a straight and a diagonal step, in tenths of a cell (as the pathfinder weighs them).</summary>
        private const int Straight = 10, Diagonal = 14;

        private static int[] stamp = new int[0];
        private static int[] dist = new int[0];
        private static int generation;
        // Dial's algorithm: a bucket per distance, as many as the longest step is long (+1), used round the clock.
        private static readonly List<int>[] buckets = CreateBuckets();
        private static int queued;

        /// <summary>Fills run since the game started (a diagnostic: the tests count how many a decision cost).</summary>
        public static int Fills { get; private set; }

        private static List<int>[] CreateBuckets()
        {
            var b = new List<int>[Diagonal + 1];
            for (int i = 0; i < b.Length; i++) b[i] = new List<int>();
            return b;
        }

        /// <summary>
        /// Doors an inmate can use: <paramref name="layout"/> counts a cell door as open whatever its mode (a lockup, or a door the
        /// warden has locked, is not a wall of the layout).
        /// </summary>
        private static bool Blocked(Map map, IntVec3 c, bool layout)
        {
            if (c.GetEdifice(map) is not Building_Door door || door.GetComp<CompPrisonDoor>() is not { } d) return false;
            if (layout && d.cellRoom != null) return false;
            if (d.mode is PrisonDoorMode.StaffOnly or PrisonDoorMode.Locked) return true;
            return !d.inmatePass;
        }

        private static bool Passable(Map map, IntVec3 c, bool layout) => c.InBounds(map) && c.Walkable(map) && !Blocked(map, c, layout);

        private static void Begin(Map map)
        {
            int n = map.cellIndices.NumGridCells;
            if (stamp.Length != n)
            {
                stamp = new int[n];
                dist = new int[n];
                generation = 0;
            }
            generation++;
            Fills++;
            foreach (List<int> bucket in buckets) bucket.Clear();
            queued = 0;
        }

        private static void Seed(CellIndices idx, IntVec3 cell)
        {
            int i = idx.CellToIndex(cell);
            if (stamp[i] == generation) return;
            stamp[i] = generation;
            dist[i] = 0;
            buckets[0].Add(i);
            queued++;
        }

        /// <summary>
        /// Spreads from the seeded cells in order of distance until <paramref name="goal"/> holds at a cell (when there is one), more than
        /// <paramref name="maxCells"/> cells have been taken in, or nothing is left. The distances are in <see cref="dist"/>, in tenths of a cell.
        /// </summary>
        private static void Flood(Map map, bool layout, Func<IntVec3, bool> goal, int maxCells, out IntVec3 found, out int steps)
        {
            found = IntVec3.Invalid;
            steps = -1;
            CellIndices idx = map.cellIndices;
            int visited = 0;
            for (int level = 0; queued > 0; level++)
            {
                List<int> bucket = buckets[level % buckets.Length];
                for (int b = 0; b < bucket.Count; b++)
                {
                    int i = bucket[b];
                    queued--;
                    if (dist[i] != level) continue; // reached sooner some other way
                    if (visited++ >= maxCells) return;
                    IntVec3 c = idx.IndexToCell(i);
                    if (goal != null && goal(c))
                    {
                        found = c;
                        steps = Cells(level);
                        return;
                    }
                    for (int k = 0; k < 8; k++)
                    {
                        IntVec3 n = c + GenAdj.AdjacentCells[k];
                        if (!Passable(map, n, layout)) continue;
                        bool diagonal = k >= 4;
                        // (Not round a corner: the pathfinder refuses a diagonal step when either of the cells it cuts across is blocked.)
                        if (diagonal && (!Passable(map, new IntVec3(n.x, c.y, c.z), layout) || !Passable(map, new IntVec3(c.x, c.y, n.z), layout))) continue;
                        int ni = idx.CellToIndex(n);
                        int nd = level + (diagonal ? Diagonal : Straight);
                        if (stamp[ni] == generation && dist[ni] <= nd) continue;
                        stamp[ni] = generation;
                        dist[ni] = nd;
                        buckets[nd % buckets.Length].Add(ni);
                        queued++;
                    }
                }
                bucket.Clear();
            }
        }

        /// <summary>Tenths of a cell to whole cells, rounded.</summary>
        private static int Cells(int tenths) => (tenths + Straight / 2) / Straight;

        /// <summary>The nearest cell by walking for which <paramref name="goal"/> holds (false when none lies within <paramref name="maxCells"/> cells explored).</summary>
        public static bool TryFindNearest(Map map, IntVec3 from, Func<IntVec3, bool> goal, int maxCells, out IntVec3 found, out int steps, bool layout = false)
        {
            Begin(map);
            Seed(map.cellIndices, from);
            Flood(map, layout, goal, maxCells, out found, out steps);
            return steps >= 0;
        }

        /// <summary>
        /// Walking distance from every source (a multi-source flood fill): for each cell in <paramref name="targets"/>, the cells walked
        /// to the nearest source (int.MaxValue when none can be reached). Layout view: cell doors count as open.
        /// </summary>
        public static int[] DistancesTo(Map map, IEnumerable<IntVec3> sources, IList<IntVec3> targets)
        {
            Begin(map);
            CellIndices idx = map.cellIndices;
            foreach (IntVec3 s in sources) Seed(idx, s);
            Flood(map, layout: true, null, int.MaxValue, out _, out _);
            var result = new int[targets.Count];
            for (int t = 0; t < result.Length; t++)
            {
                int ti = idx.CellToIndex(targets[t]);
                result[t] = stamp[ti] == generation ? Cells(dist[ti]) : int.MaxValue;
            }
            return result;
        }
    }
}
