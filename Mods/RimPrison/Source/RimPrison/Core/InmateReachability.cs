using System.Collections.Generic;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Flood fill over walkable cells where doors count only if open to inmates right now. For layout checks and
    /// diagnostics (not for AI: pawns use vanilla reachability with the door patch).
    /// </summary>
    public static class InmateReachability
    {
        public static bool CanInmateWalk(Map map, IntVec3 from, IntVec3 to)
        {
            if (from == to) return true;
            var seen = new HashSet<IntVec3> { from };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                IntVec3 c = queue.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[k];
                    if (!n.InBounds(map) || seen.Contains(n)) continue;
                    if (n == to) return true;
                    if (!n.Walkable(map)) continue;
                    if (n.GetEdifice(map) is RimWorld.Building_Door door && door.GetComp<CompPrisonDoor>() is { inmatePass: false }) continue;
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
            return false;
        }
    }
}
