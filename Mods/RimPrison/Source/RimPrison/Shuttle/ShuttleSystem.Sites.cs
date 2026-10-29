using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    // Sites: the pads, clear ground without one, and the reception where the released wait.
    public partial class ShuttleSystem
    {
        public static IEnumerable<Building> Pads(Map map)
        {
            List<Thing> pads = map.listerThings.ThingsOfDef(RPShuttleDefOf.RP_ShuttlePad);
            for (int i = 0; i < pads.Count; i++)
                if (pads[i] is Building pad && pad.Faction == Faction.OfPlayer) yield return pad;
        }

        /// <summary>A pad that could take the shuttle (not roofed, not cut off from holding), clear or not.</summary>
        public static bool PadAvailable(Map map)
        {
            if (!HasPad(map)) return false;
            IntVec3 anchor = Anchor(map, out bool zone);
            foreach (Building pad in Pads(map))
                if (ProblemOf(map, pad, anchor, zone) == PadProblem.None) return true;
            return false;
        }

        public static bool HasPad(Map map) => map != null && map.listerThings.ThingsOfDef(RPShuttleDefOf.RP_ShuttlePad).Count > 0;

        public static IntVec3 Footprint(IntVec3 center, out CellRect rect)
        {
            rect = GenAdj.OccupiedRect(center, Rot4.North, RPShuttleDefOf.RP_StateShuttle.size);
            return ThingUtility.InteractionCellWhenAt(RPShuttleDefOf.RP_StateShuttle, center, Rot4.North, null);
        }

        /// <summary>The tests' switch: there is no clear ground for a shuttle without a pad (the search finds nothing).</summary>
        public static bool NoOpenGroundForTests;

        // ---- sites ----------------------------------------------------------------------------------------------

        public static bool AnyPawnOn(Map map, IntVec3 center)
        {
            IntVec3 ramp = Footprint(center, out CellRect rect);
            foreach (IntVec3 c in rect)
                if (c.InBounds(map) && c.GetFirstPawn(map) != null) return true;
            return ramp.InBounds(map) && ramp.GetFirstPawn(map) != null;
        }

        /// <summary>
        /// Makes ready for the hull: blueprints on it are cancelled, items moved aside, pawns stepped out (nobody is crushed). A pawn is set down only where they could have
        /// walked from where they stood (through the doors they may pass): never through a wall, never into a locked block.
        /// </summary>
        public static void ClearSite(Map map, IntVec3 center, ThingDef def)
        {
            CellRect rect = GenAdj.OccupiedRect(center, Rot4.North, def.size);
            IntVec3 ramp = Footprint(center, out _);
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map)) continue;
                foreach (Thing t in c.GetThingList(map).ToList())
                    if (t.def.IsBlueprint || t.def.IsFrame) t.Destroy(DestroyMode.Cancel);
            }
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map)) continue;
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t is not Pawn p || p.Dead) continue;
                    IntVec3 from = p.Position;
                    TraverseParms parms = TraverseParms.For(p, Danger.Deadly);
                    // One of the nearest eight cells they could walk to (the nearest ring first: a pawn is not flung across the yard).
                    IntVec3 away = IntVec3.Invalid;
                    int found = 0;
                    foreach (IntVec3 x in GenRadial.RadialCellsAround(center, 16f, true))
                    {
                        if (!x.InBounds(map) || !x.Standable(map) || rect.Contains(x) || x == ramp || x.GetFirstPawn(map) != null || !map.reachability.CanReach(from, x, PathEndMode.OnCell, parms)) continue;
                        if (Rand.RangeInclusive(0, found++) == 0) away = x;
                        if (found >= 8) break;
                    }
                    if (!away.IsValid) continue;
                    p.pather?.StopDead();
                    p.Position = away;
                    p.Notify_Teleported(true, true);
                }
            }
            GenSpawn.CheckMoveItemsAside(center, Rot4.North, def, map);
        }

        /// <summary>The first pawn, item or building (not the pad) on this cell, else null.</summary>
        private static Thing BlockerAt(Map map, IntVec3 c)
        {
            if (!c.InBounds(map)) return null;
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t is Pawn || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building && t.def != RPShuttleDefOf.RP_ShuttlePad && !t.def.IsBlueprint && !t.def.IsFrame)
                    return t;
            }
            return null;
        }

        /// <summary>Anything on the shuttle's footprint or at its ramp that would have to be moved: pawns, items, buildings that are not the pad (the first found, else null).</summary>
        public static Thing FirstBlocker(Map map, IntVec3 center)
        {
            IntVec3 ramp = Footprint(center, out CellRect rect);
            foreach (IntVec3 c in rect)
                if (BlockerAt(map, c) is { } t) return t;
            return BlockerAt(map, ramp);
        }

        /// <summary>What is in the way on a pad that could take the shuttle (not one that is roofed or cut off from holding, where moving a blocker would not help): the first found, else null.</summary>
        public static Thing PadBlocker(Map map)
        {
            IntVec3 anchor = Anchor(map, out bool zone);
            foreach (Building pad in Pads(map))
                if (ProblemOf(map, pad, anchor, zone) == PadProblem.None && FirstBlocker(map, pad.Position) is { } blocker) return blocker;
            return null;
        }

        public static bool SiteBlocked(Map map, IntVec3 center)
        {
            IntVec3 ramp = Footprint(center, out CellRect rect);
            return !ramp.InBounds(map) || !rect.InBounds(map) || FirstBlocker(map, center) != null;
        }

        /// <summary>Why a pad cannot take the shuttle at all (not just for now, with something on it).</summary>
        public enum PadProblem : byte
        {
            None,
            /// <summary>A roof over the hull, or the cell round it: a skyfaller brings the roof down (and what is under it).</summary>
            Roofed,
            /// <summary>No way from holding to the pad (walled off): the released could not get to it, nor the new inmates from it.</summary>
            CutOff,
        }

        public static PadProblem ProblemOf(Map map, Building pad, IntVec3 anchor, bool anchorIsZone)
        {
            IntVec3 ramp = Footprint(pad.Position, out CellRect rect);
            CellRect area = rect.ExpandedBy(1);
            area.Encapsulate(ramp);
            foreach (IntVec3 c in area)
                if (c.InBounds(map) && c.Roofed(map)) return PadProblem.Roofed;
            // (From holding, when the prison has one: without a zone the middle of the map is no place to measure from.)
            if (anchorIsZone && ramp.InBounds(map) && !map.reachability.CanReach(anchor, ramp, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly))) return PadProblem.CutOff;
            return PadProblem.None;
        }

        /// <summary>The first problem of the map's pads when none can take the shuttle (there is a pad, and none is usable), else None.</summary>
        public static PadProblem PadsProblem(Map map)
        {
            if (!HasPad(map)) return PadProblem.None;
            IntVec3 anchor = Anchor(map, out bool zone);
            PadProblem first = PadProblem.None;
            foreach (Building pad in Pads(map))
            {
                PadProblem p = ProblemOf(map, pad, anchor, zone);
                if (p == PadProblem.None) return PadProblem.None;
                if (first == PadProblem.None) first = p;
            }
            return first;
        }

        /// <summary>The nearest usable free pad to the prison's gate, else null; <paramref name="usable"/> says whether any pad could take it were it clear.</summary>
        private static Building BestFreePad(Map map, out bool usable)
        {
            usable = false;
            IntVec3 anchor = Anchor(map, out bool zone);
            Building best = null;
            float bestD = float.MaxValue;
            foreach (Building pad in Pads(map))
            {
                if (ProblemOf(map, pad, anchor, zone) != PadProblem.None) continue;
                usable = true;
                float d = pad.Position.DistanceToSquared(anchor);
                if (d >= bestD || SiteBlocked(map, pad.Position)) continue;
                bestD = d;
                best = pad;
            }
            return best;
        }

        public static IntVec3 Anchor(Map map) => Anchor(map, out _);

        /// <summary>Where the shuttle should come down near: the holding zone, else any painted zone, else the middle of the map (<paramref name="isZone"/>: it is a zone's cell).</summary>
        public static IntVec3 Anchor(Map map, out bool isZone)
        {
            PrisonMap pm = PrisonMap.Of(map);
            isZone = true;
            List<IntVec3> holding = pm?.zones.CellsOf(RPDefOf.RP_Holding);
            if (holding != null && holding.Count > 0) return Centroid(holding);
            List<IntVec3> deliveries = pm?.zones.CellsOf(RPDefOf.RP_Deliveries);
            if (deliveries != null && deliveries.Count > 0) return Centroid(deliveries);
            isZone = false;
            return map.Center;
        }

        private static IntVec3 Centroid(List<IntVec3> cells)
        {
            long x = 0, z = 0;
            foreach (IntVec3 c in cells)
            {
                x += c.x;
                z += c.z;
            }
            IntVec3 mid = new IntVec3((int)(x / cells.Count), 0, (int)(z / cells.Count));
            IntVec3 best = cells[0];
            foreach (IntVec3 c in cells)
                if (c.DistanceToSquared(mid) < best.DistanceToSquared(mid)) best = c;
            return best;
        }

        /// <summary>Sweeps of the map for clear ground made this session (a diagnostic: the tests count what a hold costs).</summary>
        public static int SiteSweeps;

        /// <summary>When the last sweep for clear ground found none (this session): the next one is an hour later, whoever asks.</summary>
        private int noSiteSince = -999999;

        /// <summary>Forgets the site and the failed search (the tests, and a pad built or taken away).</summary>
        public void ForgetSite()
        {
            fallbackSite = IntVec3.Invalid;
            noSiteSince = -999999;
        }

        /// <summary>
        /// The nearest clear spot near holding or the gate where the hull (and a margin for the ramp) fits, outside the painted zones if possible: on the prison's grounds first, and when nothing
        /// there is clear, on the nearest clear ground just outside them (the state's shuttle is not the warden's building: the pad's alert says what is wrong). Once chosen it stays the
        /// site while the ground is still open (pawns or things standing on it are the landing's to move aside): reception does not move round the waiting. With none
        /// found, the sweep is not repeated for an hour.
        /// </summary>
        public IntVec3 FallbackSite(Map map)
        {
            if (KnownFallbackSite(map).IsValid) return fallbackSite;
            if (Now - noSiteSince < SiteSearchEveryTicks) return IntVec3.Invalid;
            SiteSweeps++;
            fallbackSite = SearchSite(map, Anchor(map));
            noSiteSince = fallbackSite.IsValid ? -999999 : Now;
            return fallbackSite;
        }

        /// <summary>The sweep itself: the grounds (outside the painted zones, then anywhere on them), then the ground beside them.</summary>
        public static IntVec3 SearchSite(Map map, IntVec3 anchor)
        {
            IntVec3 site = FindOpenSite(map, anchor, allowZones: false);
            if (!site.IsValid) site = FindOpenSite(map, anchor, allowZones: true);
            if (!site.IsValid) site = FindSiteBesideGrounds(map, anchor);
            return site;
        }

        /// <summary>The site found before, if it still fits (kept while the shuttle itself stands on it: the hull is not in its own way); never sweeps. What the readers (reception, a transfer's way out) use.</summary>
        public IntVec3 KnownFallbackSite(Map map) =>
            fallbackSite.IsValid && fallbackSite.InBounds(map) && (visit?.shuttle is { Spawned: true } down && down.Position == fallbackSite || SiteFits(map, fallbackSite, allowZones: true, onGroundsOnly: false)) ? fallbackSite : IntVec3.Invalid;

        /// <summary>The nearest site on the prison's own land.</summary>
        public static IntVec3 FindOpenSite(Map map, IntVec3 anchor, bool allowZones)
        {
            if (NoOpenGroundForTests) return IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 60f, true))
                if (SiteFits(map, c, allowZones) && !SiteBlocked(map, c) && map.reachability.CanReach(c, anchor, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly)))
                    return c;
            return IntVec3.Invalid;
        }

        /// <summary>How far from the anchor (or from the grounds' edge) the sweep for ground beside the grounds goes: the farthest the game's radial pattern reaches, less a margin.</summary>
        private const float BesideSearchRadius = 75f;

        /// <summary>
        /// The nearest clear ground off the grounds: a prison whose land is full, or whose pad is not usable, still gets its shuttle (landing is not building). Looked for outward from the anchor, and
        /// when the grounds are so large that nothing off them is within reach of it, from the point of their edge nearest to it. Unpainted ground first. Invalid on a map without grounds (the sweep
        /// of the grounds has covered all of it).
        /// </summary>
        public static IntVec3 FindSiteBesideGrounds(Map map, IntVec3 anchor)
        {
            if (NoOpenGroundForTests || PrisonGrounds.Of(map) is not { Limited: true } grounds) return IntVec3.Invalid;
            CellRect land = grounds.Rect;
            // The point of the plot's edge nearest the anchor (the anchor, pulled to the nearest side when it is inside).
            IntVec3 edge = new IntVec3(Mathf.Clamp(anchor.x, land.minX, land.maxX), 0, Mathf.Clamp(anchor.z, land.minZ, land.maxZ));
            if (land.Contains(anchor))
            {
                int toWest = anchor.x - land.minX, toEast = land.maxX - anchor.x, toSouth = anchor.z - land.minZ, toNorth = land.maxZ - anchor.z;
                int nearest = Mathf.Min(Mathf.Min(toWest, toEast), Mathf.Min(toSouth, toNorth));
                if (nearest == toWest) edge.x = land.minX;
                else if (nearest == toEast) edge.x = land.maxX;
                else if (nearest == toSouth) edge.z = land.minZ;
                else edge.z = land.maxZ;
            }
            foreach (IntVec3 from in new[] { anchor, edge })
                foreach (bool allowZones in new[] { false, true })
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(from, BesideSearchRadius, true))
                        if (!land.Contains(c) && SiteFits(map, c, allowZones, onGroundsOnly: false) && !SiteBlocked(map, c) && map.reachability.CanReach(c, anchor, PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly)))
                            return c;
            return IntVec3.Invalid;
        }

        /// <summary>
        /// The hull plus a ring around it is open ground: standable, unroofed, not fogged, no water, nothing built in the way (plants and filth do not count: the landing clears them, as a skyfaller's does).
        /// <paramref name="onGroundsOnly"/>: the hull, its ring and the ramp must also be inside the prison's grounds (the first choice; a site found beside them is kept as it is).
        /// </summary>
        public static bool SiteFits(Map map, IntVec3 center, bool allowZones, bool onGroundsOnly = true)
        {
            IntVec3 ramp = Footprint(center, out CellRect rect);
            CellRect area = rect.ExpandedBy(1);
            area.Encapsulate(ramp);
            PrisonMap pm = PrisonMap.Of(map);
            foreach (IntVec3 c in area)
            {
                if (!c.InBounds(map) || !c.Standable(map) || c.Roofed(map) || c.Fogged(map)) return false;
                TerrainDef terrain = c.GetTerrain(map);
                if (terrain.IsWater || terrain.passability == Traversability.Impassable || c.GetEdifice(map) != null) return false;
                if (!allowZones && pm != null && pm.zones.ZoneAt(c) != null) return false;
                if (onGroundsOnly && pm?.grounds != null && !pm.grounds.Contains(c)) return false;
            }
            return true;
        }

        // ---- reception: where the released wait ---------------------------------------------------------------

        /// <summary>How near the ramp's spot the released wait.</summary>
        public const float ReceptionRadius = 7f;

        /// <summary>The place by the pad (or where the shuttle sets down without one) where the released wait for it: the cell at its ramp; invalid if the map has no place for a shuttle.</summary>
        public IntVec3 ReceptionAnchor(Map map)
        {
            IntVec3 center = IntVec3.Invalid;
            float best = float.MaxValue;
            IntVec3 anchor = Anchor(map, out bool zone);
            foreach (Building pad in Pads(map))
                if (pad.Position.DistanceToSquared(anchor) < best && ProblemOf(map, pad, anchor, zone) == PadProblem.None)
                {
                    best = pad.Position.DistanceToSquared(anchor);
                    center = pad.Position;
                }
            if (!center.IsValid) center = KnownFallbackSite(map);
            return center.IsValid ? Footprint(center, out _) : IntVec3.Invalid;
        }

        /// <summary>The ground the released keep off while they wait, and where crates are not set down: the hull's footprint, the way down and a margin round them (they would hold the shuttle up).</summary>
        public static CellRect KeepClear(IntVec3 ramp) => new CellRect(ramp.x - 3, ramp.z - 1, 7, 6);

        /// <summary>A spot in reception, outside the hull's landing margin: standable and reachable; invalid when there is none to walk to.</summary>
        public IntVec3 ReceptionSpot(Pawn p, IntVec3 ramp)
        {
            if (!ramp.IsValid) return IntVec3.Invalid;
            CellRect keepClear = KeepClear(ramp);
            return CellFinder.TryFindRandomCellNear(ramp, p.Map, 5, c => c.Standable(p.Map) && !keepClear.Contains(c) && p.CanReach(c, PathEndMode.OnCell, Danger.Deadly), out IntVec3 spot) ? spot : IntVec3.Invalid;
        }
    }
}
