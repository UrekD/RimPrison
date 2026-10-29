using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// What the murder trials (<see cref="Trials"/>) and the complaints against officers (<see cref="ComplaintSystem"/>) both do: find the people a case names, see whether a camera or an eye
    /// was on a spot, and write a list into a letter. One place, so that the two cannot drift apart.
    /// </summary>
    public static class CourtUtil
    {
        /// <summary>Reputation a case the warden lost (a lawsuit, a murder case lost to tampering or closed by shipping the accused out) costs.</summary>
        public const float ReputationHit = 3f;

        // ---- lookups (by id: the people outlive the pawns) ----------------------------------------------------------------

        /// <summary>The prisoner with this id if they are alive and still here (active or not: carried to a bed, in the hole).</summary>
        internal static Pawn FindInmate(Map map, int id)
        {
            if (id < 0) return null;
            List<Pawn> list = map.mapPawns.PrisonersOfColony;
            for (int i = 0; i < list.Count; i++)
                if (list[i].thingIDNumber == id) return list[i].Dead || list[i].Destroyed || list[i].guest == null || list[i].guest.Released ? null : list[i];
            return null;
        }

        /// <summary>The member of staff with this id if they are alive and still employed here (on site or at home).</summary>
        internal static Pawn FindStaff(Map map, int id)
        {
            if (id < 0) return null;
            foreach (Pawn p in map.mapPawns.FreeColonists)
                if (p.thingIDNumber == id) return Employed(p);
            if (PrisonGame.Instance != null)
                foreach (Pawn p in PrisonGame.Instance.OffSite)
                    if (p.thingIDNumber == id) return Employed(p);
            return null;
        }

        private static Pawn Employed(Pawn p) => p.Dead || p.Destroyed || p.GetComp<CompStaff>()?.role == null ? null : p;

        /// <summary>The prisoner is on another of the player's home maps (that map's pass looks after their case, not this one's).</summary>
        internal static bool OnAnotherHomeMap(Map map, int id)
        {
            foreach (Map m in Find.Maps)
                if (m != map && m.IsPlayerHome && FindInmate(m, id) != null) return true;
            return false;
        }

        // ---- what was seen --------------------------------------------------------------------------------------------------

        /// <summary>A camera of the prison covers this cell (and sees it: in the dark a camera sees nothing, see <see cref="TensionUtility.Dark"/>).</summary>
        public static bool CameraOver(Map map, IntVec3 cell)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || TensionUtility.Dark(cell, map, pm)) return false;
            for (int i = 0; i < pm.Cameras.Count; i++)
                if (pm.Cameras[i].Covers(cell)) return true;
            return false;
        }

        /// <summary>This pawn is near enough to a cell, and has a line of sight to it (staff in the dark see only <see cref="TensionUtility.DarkSight"/> cells at most, as a guard does; an inmate is no watcher and sees as before).</summary>
        public static bool InSight(Pawn p, IntVec3 cell, Map map, float radius)
        {
            if (!p.IsPrisonerOfColony && PrisonMap.Of(map) is { } pm && TensionUtility.Dark(cell, map, pm)) radius = Mathf.Min(radius, TensionUtility.DarkSight);
            return p.Position.InHorDistOf(cell, radius) && GenSight.LineOfSight(p.Position, cell, map);
        }

        // ---- letters ------------------------------------------------------------------------------------------------------------

        /// <summary>One line a fact, as a list in a letter.</summary>
        public static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "  - " + l));
    }
}
