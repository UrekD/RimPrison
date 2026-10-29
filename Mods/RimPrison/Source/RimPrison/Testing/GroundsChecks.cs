using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Checks for the grounds and buying land (<see cref="PrisonGrounds"/>): what is refused outside them (a build designation, a floor, a zone, the shuttle pad and its ramp, the fallback landing site), what is
    /// accepted inside, the start of each facility, buying (the ledger, the rect, the edge, the money, special measures, the difficulty), the dev moves, what shrinking leaves standing, the to-do line and the
    /// learning helper's note. Called once from FireAllEvents; each works on the live grounds and puts them back (the check's own undo) with everything else <see cref="Rollback"/> restores. The
    /// round trip is in the saveload plan's snapshot (<see cref="SeedGroundsForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void GroundsFail(string text) => problems0.Add("grounds: " + text);

        /// <summary>Puts the grounds back as they were (fields, not the object: the map keeps its own).</summary>
        private static Action RememberGrounds(PrisonGrounds g)
        {
            (bool defined, int a, int b, int c, int d, int e, int f, int h, int i, int parcels) = (g.defined, g.minX, g.minZ, g.maxX, g.maxZ, g.startMinX, g.startMinZ, g.startMaxX, g.startMaxZ, g.parcelsBought);
            int tried = g.triedOutsideTick;
            return () =>
            {
                (g.defined, g.minX, g.minZ, g.maxX, g.maxZ, g.startMinX, g.startMinZ, g.startMaxX, g.startMaxZ, g.parcelsBought) = (defined, a, b, c, d, e, f, h, i, parcels);
                g.triedOutsideTick = tried;
                g.version++;
            };
        }

        private static IntVec3 FirstCell(IEnumerable<IntVec3> cells, Func<IntVec3, bool> where)
        {
            foreach (IntVec3 c in cells)
                if (where(c)) return c;
            return IntVec3.Invalid;
        }

        private static void CheckGrounds(Map map)
        {
            // (Buying marks the grounds' note learned: put back as it was.)
            ConceptDef note = ConceptDef.Named(Concepts.Grounds);
            float knowledge = PlayerKnowledgeDatabase.GetKnowledge(note);
            try
            {
                CheckGroundsStart(map);
                CheckGroundsRefuse(map);
                CheckGroundsBuying(map);
                CheckGroundsDev(map);
                CheckGroundsTodoAndNote(map);
                CheckGroundsOverlay(map);
            }
            finally
            {
                PlayerKnowledgeDatabase.SetKnowledge(note, knowledge);
                Concepts.Reset();
            }
        }

        /// <summary>The shuttle's hull, the ring round it and its ramp are all inside the grounds (what a landing site needs).</summary>
        private static bool SiteInside(PrisonGrounds g, IntVec3 center)
        {
            IntVec3 ramp = ShuttleSystem.Footprint(center, out CellRect hull);
            return g.Contains(hull.ExpandedBy(1)) && g.Contains(ramp);
        }

        /// <summary>RIMPRISON_MARGINS=north,east,south,west starts the game the way the New prison dialog's dev row does (null when unset): the path from the dialog's options to the starting grounds, end to end.</summary>
        private static int[] GroundsMarginsFromEnvironment() =>
            Environment.GetEnvironmentVariable("RIMPRISON_MARGINS")?.Split(',').Select(int.Parse).ToArray() is { Length: 4 } margins ? margins : null;

        /// <summary>Every plan, once, at the start: the live facility (the inherited one or the penitentiary) is inside its grounds, and the grounds are inside the map less its edge margin.</summary>
        private static void CheckStarterGrounds(Map map)
        {
            PrisonGrounds g = PrisonGrounds.Of(map);
            if (g is not { Limited: true })
            {
                GroundsFail("the game started without grounds");
                return;
            }
            CellRect footprint = PrisonBuilder.Footprint(map), whole = g.Whole, rect = g.Rect;
            if (!g.Contains(footprint)) GroundsFail($"the starting grounds {rect} do not contain the {PrisonBuilder.Kind} facility's footprint {footprint}");
            if (rect.minX < whole.minX || rect.minZ < whole.minZ || rect.maxX > whole.maxX || rect.maxZ > whole.maxZ) GroundsFail($"the starting grounds {rect} go past the edge margin ({whole})");
            // (A start with chosen margins, RIMPRISON_MARGINS: the grounds are the footprint and those margins, as far as the map allows.)
            if (GroundsMarginsFromEnvironment() is { } chosen)
            {
                CellRect want = PrisonGrounds.StartingRect(map, built: true, chosen);
                want = CellRect.FromLimits(Math.Max(whole.minX, want.minX), Math.Max(whole.minZ, want.minZ), Math.Min(whole.maxX, want.maxX), Math.Min(whole.maxZ, want.maxZ));
                if (rect != want) GroundsFail($"a start with the margins {string.Join("/", chosen)} owns {rect}, expected {want}");
                else RPLog.Message($"[RimPrison:test] grounds: the chosen margins {string.Join("/", chosen)} gave {rect}");
            }
            RPLog.Message($"[RimPrison:test] grounds: {PrisonBuilder.Kind} starts with {rect.Width} x {rect.Height} cells of the {map.Size.x} x {map.Size.z} plot ({footprint.Width} x {footprint.Height} of them the facility)");
        }

        // ---- where the grounds start -------------------------------------------------------------------------------------------------------

        private static void CheckGroundsStart(Map map)
        {
            Guarded("grounds: the starting grounds hold the facility", map, r =>
            {
                PrisonGrounds g = PrisonGrounds.Of(map);
                if (g is not { Limited: true })
                {
                    GroundsFail("the game started without grounds");
                    return;
                }
                CellRect whole = g.Whole;
                if (whole.minX != PrisonGrounds.EdgeMargin || whole.maxX != map.Size.x - 1 - PrisonGrounds.EdgeMargin) GroundsFail($"the land the grounds may reach is {whole}, not the map less {PrisonGrounds.EdgeMargin} cells at each edge");
                // The land the grounds may reach is the land vanilla lets the warden build on, no less (the state sells none that cannot be built) and no more.
                if (!map.IsPocketMap && !map.Tile.LayerDef.ignoreNoBuildArea)
                {
                    foreach (IntVec3 corner in new[] { new IntVec3(whole.minX, 0, whole.minZ), new IntVec3(whole.maxX, 0, whole.minZ), new IntVec3(whole.minX, 0, whole.maxZ), new IntVec3(whole.maxX, 0, whole.maxZ) })
                        if (corner.InNoBuildEdgeArea(map)) GroundsFail($"{corner}, a corner of the land the grounds may reach ({whole}), is inside vanilla's no-build edge: land the state sells and nobody can build on");
                    foreach (IntVec3 beyond in new[] { new IntVec3(whole.minX - 1, 0, whole.CenterCell.z), new IntVec3(whole.maxX + 1, 0, whole.CenterCell.z), new IntVec3(whole.CenterCell.x, 0, whole.minZ - 1), new IntVec3(whole.CenterCell.x, 0, whole.maxZ + 1) })
                        if (!beyond.InNoBuildEdgeArea(map)) GroundsFail($"{beyond}, just past the land the grounds may reach ({whole}), is buildable: the margin is wider than vanilla's no-build edge");
                    if (g.Margin != GenGrid.NoBuildEdgeWidth) GroundsFail($"the margin is {g.Margin}, vanilla's no-build edge is {GenGrid.NoBuildEdgeWidth}");
                }
                CellRect start = g.Start;
                if (start.minX < whole.minX || start.minZ < whole.minZ || start.maxX > whole.maxX || start.maxZ > whole.maxZ) GroundsFail($"the starting grounds {start} go past the edge margin ({whole})");
                // The live facility: its footprint (the walls, the road to the gate, the pad and the deliveries) and the margin are inside.
                CellRect footprint = PrisonBuilder.Footprint(map);
                if (!g.Contains(footprint)) GroundsFail($"the starting grounds {g.Rect} do not contain the {PrisonBuilder.Kind} facility's footprint {footprint}");
                if (g.Rect != start || g.parcelsBought != 0) GroundsFail($"a new game's grounds are {g.Rect} with {g.parcelsBought} parcels bought, expected the start {start} and none");
                if (g.Contains(IntVec3.Zero)) GroundsFail("the map's corner is inside the grounds");
                if (g.Rect != PrisonGrounds.StartingRect(map, built: true)) GroundsFail($"the live start {g.Rect} is not the footprint with a margin of {PrisonGrounds.StartMargin} ({PrisonGrounds.StartingRect(map, built: true)})");

                // Both built facilities and the empty plot, worked out for this map (the penitentiary only on a map it fits, as the builder does).
                Facility kind0 = PrisonBuilder.Kind;
                r.Add(() => PrisonBuilder.Kind = kind0);
                foreach (Facility kind in new[] { Facility.Inherited, Facility.Penitentiary })
                {
                    PrisonBuilder.Kind = kind;
                    if (kind == Facility.Penitentiary && (map.Size.x < PrisonBuilder.PenWidth + 20 || map.Size.z < PrisonBuilder.PenDepth + PrisonBuilder.PenSouthMargin + 20)) continue;
                    CellRect rect = PrisonGrounds.StartingRect(map, built: true), fp = PrisonBuilder.Footprint(map);
                    CellRect clamped = CellRect.FromLimits(Math.Max(whole.minX, rect.minX), Math.Max(whole.minZ, rect.minZ), Math.Min(whole.maxX, rect.maxX), Math.Min(whole.maxZ, rect.maxZ));
                    if (clamped.minX > fp.minX || clamped.minZ > fp.minZ || clamped.maxX < fp.maxX || clamped.maxZ < fp.maxZ) GroundsFail($"the {kind}'s starting grounds {clamped} do not contain its footprint {fp}");
                    // (A margin of 10 to 15 cells: room to build against the wall and to put up a pad or a block beside it.)
                    int margin = fp.minX - rect.minX;
                    if (margin < 10 || margin > 15 || rect.maxZ - fp.maxZ != margin || rect.maxX - fp.maxX != margin || fp.minZ - rect.minZ != margin) GroundsFail($"the {kind}'s grounds are not its footprint and an even margin of 10 to 15 cells ({rect} for {fp})");
                }
                PrisonBuilder.Kind = kind0;
                CellRect fresh = PrisonGrounds.StartingRect(map, built: false);
                int side = PrisonGrounds.NewFacilitySide + 2 * PrisonGrounds.StartMargin;
                if (fresh.Width != side || fresh.Height != side || fresh.CenterCell.DistanceTo(map.Center) > 1.5f) GroundsFail($"a new facility owns {fresh} ({fresh.Width} x {fresh.Height}), expected a centred {side} x {side} square");

                // Dev mode's own margins, and the pending ones being used up by the start.
                var scratch = new PrisonGrounds(map);
                PrisonGrounds.PendingMargins = new[] { 0, 20, 3, 5 };
                scratch.SetStarting(built: true);
                CellRect want = CellRect.FromLimits(Math.Max(whole.minX, PrisonBuilder.Footprint(map).minX - 5), Math.Max(whole.minZ, PrisonBuilder.Footprint(map).minZ - 3), Math.Min(whole.maxX, PrisonBuilder.Footprint(map).maxX + 20), Math.Min(whole.maxZ, PrisonBuilder.Footprint(map).maxZ + 0));
                if (scratch.Rect != want || PrisonGrounds.PendingMargins != null) GroundsFail($"dev margins (N0 E20 S3 W5) gave {scratch.Rect}, expected {want}, pending left {PrisonGrounds.PendingMargins != null}");

                // A save from before land has none: the whole map, nothing refused, nothing for sale.
                var old = new PrisonGrounds(map);
                if (old.Limited || !old.Contains(IntVec3.Zero) || !old.Contains(new IntVec3(map.Size.x - 1, 0, map.Size.z - 1)) || old.ParcelFor(GroundsSide.North).blocked == null)
                    GroundsFail("a map without grounds (an old save) is limited, refuses a cell or has land for sale");
            });
        }

        // ---- what is refused ----------------------------------------------------------------------------------------------------------------

        private static void CheckGroundsRefuse(Map map)
        {
            Guarded("grounds: building, floors, zones, the pad and the fallback site stay inside", map, r =>
            {
                PrisonGrounds g = PrisonGrounds.Of(map);
                r.Add(RememberGrounds(g));
                bool god = DebugSettings.godMode;
                r.Add(() => DebugSettings.godMode = god);
                DebugSettings.godMode = false;
                string outsideText = PrisonGrounds.OutsideText;
                CellRect rect = g.Rect;
                ThingDef wall = ThingDefOf.Wall;
                bool Vanilla(BuildableDef def, IntVec3 c) => GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map, godMode: true, null, null, def is ThingDef { MadeFromStuff: true } ? ThingDefOf.BlocksGranite : null).Accepted;
                IntVec3 outside = FirstCell(rect.ExpandedBy(6).Cells, c => !g.Contains(c) && !c.InNoBuildEdgeArea(map) && Vanilla(wall, c) && Vanilla(TerrainDefOf.Concrete, c));
                IntVec3 inside = FirstCell(rect.Cells, c => !c.InNoBuildEdgeArea(map) && c.GetFirstThing<Thing>(map) == null && c.GetEdifice(map) == null && Vanilla(wall, c) && Vanilla(TerrainDefOf.Concrete, c));
                if (!outside.IsValid || !inside.IsValid)
                {
                    GroundsFail($"no clear ground to try (outside {outside}, inside {inside})");
                    return;
                }
                // A build designation: refused outside with the reason, accepted inside (the real designator, as the architect menu asks it).
                var wallTool = new Designator_Build(wall);
                AcceptanceReport out1 = wallTool.CanDesignateCell(outside), in1 = wallTool.CanDesignateCell(inside);
                if (out1.Accepted || out1.Reason != outsideText) GroundsFail($"a wall outside the grounds at {outside}: accepted {out1.Accepted}, reason \"{out1.Reason}\"");
                if (!in1.Accepted) GroundsFail($"a wall inside the grounds at {inside} was refused: \"{in1.Reason}\"");
                // A floor.
                var floorTool = new Designator_Build(TerrainDefOf.Concrete);
                AcceptanceReport out2 = floorTool.CanDesignateCell(outside), in2 = floorTool.CanDesignateCell(inside);
                if (out2.Accepted || out2.Reason != outsideText || !in2.Accepted) GroundsFail($"a floor: outside accepted {out2.Accepted} (\"{out2.Reason}\"), inside accepted {in2.Accepted} (\"{in2.Reason}\")");
                // A building standing across the line is refused whole (a 2 x 2 table half in).
                ThingDef table = DefDatabase<ThingDef>.GetNamed("Table2x2c");
                IntVec3 across = FirstCell(rect.Cells, c => c.x == rect.maxX && !c.InNoBuildEdgeArea(map) && Vanilla(table, c));
                if (across.IsValid)
                {
                    AcceptanceReport tableAcross = new Designator_Build(table).CanDesignateCell(across);
                    if (tableAcross.Accepted || tableAcross.Reason != outsideText) GroundsFail($"a table across the east line at {across} was accepted ({tableAcross.Accepted}, \"{tableAcross.Reason}\")");
                }
                // Painting a room.
                PrisonMap pm = PrisonMap.Of(map);
                IntVec3 paintOut = FirstCell(rect.ExpandedBy(6).Cells, c => !g.Contains(c) && c.InBounds(map) && !c.Fogged(map));
                IntVec3 paintIn = FirstCell(rect.Cells, c => pm.zones.ZoneAt(c) != RPDefOf.RP_Yard && !c.Fogged(map));
                var yardTool = new Designator_PrisonZone(RPDefOf.RP_Yard);
                AcceptanceReport zOut = yardTool.CanDesignateCell(paintOut), zIn = yardTool.CanDesignateCell(paintIn);
                if (zOut.Accepted || zOut.Reason != outsideText || !zIn.Accepted) GroundsFail($"painting a yard: outside accepted {zOut.Accepted} (\"{zOut.Reason}\"), inside accepted {zIn.Accepted}");
                // Taking paint off outside is allowed (a room left outside after the grounds shrank can be cleared).
                PrisonZoneDef was = pm.zones.ZoneAt(paintOut);
                r.Add(() => pm.zones.Set(paintOut, was));
                pm.zones.Set(paintOut, RPDefOf.RP_Yard);
                if (!new Designator_PrisonZone(null).CanDesignateCell(paintOut).Accepted) GroundsFail("clearing a room outside the grounds is refused");
                // Vanilla's stockpile and growing zones (all of them ask the same cell test).
                IntVec3 zoneCellOut = FirstCell(rect.ExpandedBy(8).Cells, c => !g.Contains(c) && WithoutGrounds(g, () => Designator_ZoneAdd.IsZoneableCell(c, map).Accepted));
                IntVec3 zoneCellIn = FirstCell(rect.Cells, c => Designator_ZoneAdd.IsZoneableCell(c, map).Accepted);
                AcceptanceReport zoneOut = zoneCellOut.IsValid ? Designator_ZoneAdd.IsZoneableCell(zoneCellOut, map) : AcceptanceReport.WasAccepted;
                if (!zoneCellOut.IsValid || !zoneCellIn.IsValid || zoneOut.Accepted || zoneOut.Reason != outsideText) GroundsFail($"a vanilla zone: outside {zoneCellOut} accepted {zoneOut.Accepted} (\"{zoneOut.Reason}\"), inside {zoneCellIn}");
                // Only the warden's own placements are refused: the placement test asked with no designator (a siege's sandbags, a mech cluster, a prefab, a trap put back) is not.
                if (!GenConstruct.CanPlaceBlueprintAt(wall, outside, Rot4.North, map, godMode: false, null, null, ThingDefOf.BlocksGranite).Accepted) GroundsFail($"a placement outside the grounds at {outside} that no designator asked for (a raid's, an event's) was refused");
                if (!GenConstruct.CanPlaceBlueprintAt_NewTemp(wall, outside, Rot4.North, map, godMode: false, null, null, ThingDefOf.BlocksGranite, ignoreEdgeArea: true, ignoreInteractionSpots: true, ignoreClearableFreeBuildings: true).Accepted)
                    GroundsFail($"a siege's placement outside the grounds at {outside} was refused");
                if (Patch_GenConstruct_CanPlaceBlueprintAt_Grounds.AskedByDesignator != 0) GroundsFail($"{Patch_GenConstruct_CanPlaceBlueprintAt_Grounds.AskedByDesignator} designator asks are left open: later placements would be refused");
                // God mode builds anywhere; a map without grounds refuses nothing.
                DebugSettings.godMode = true;
                if (!wallTool.CanDesignateCell(outside).Accepted) GroundsFail("god mode was refused outside the grounds");
                DebugSettings.godMode = false;
                // Installing a minified thing: refused outside like building (the designator passes no god mode of its own to vanilla's test), and god mode is exempt there too.
                ThingDef tableDef = DefDatabase<ThingDef>.GetNamed("Table2x2c");
                IntVec3 installOut = FirstCell(rect.ExpandedBy(6).Cells, c => !g.Contains(GenAdj.OccupiedRect(c, Rot4.North, tableDef.size)) && !c.InNoBuildEdgeArea(map) && Vanilla(tableDef, c));
                IntVec3 installIn = FirstCell(rect.Cells, c => g.Contains(GenAdj.OccupiedRect(c, Rot4.North, tableDef.size)) && !c.InNoBuildEdgeArea(map) && c.GetFirstThing<Thing>(map) == null && c.GetEdifice(map) == null && Vanilla(tableDef, c));
                if (!installOut.IsValid || !installIn.IsValid) RPLog.Message($"[RimPrison:test] grounds: no clear ground to try an install on (outside {installOut}, inside {installIn})");
                else
                {
                    MinifiedThing mini = MinifyUtility.MakeMinified(ThingMaker.MakeThing(tableDef, ThingDefOf.WoodLog));
                    GenSpawn.Spawn(mini, installIn, map);
                    r.Add(() =>
                    {
                        Find.Selector.ClearSelection();
                        if (!mini.Destroyed) mini.Destroy(DestroyMode.Vanish);
                    });
                    Find.Selector.ClearSelection();
                    Find.Selector.Select(mini, playSound: false, forceDesignatorDeselect: false);
                    var install = new Designator_Install();
                    AcceptanceReport installRefused = install.CanDesignateCell(installOut), installAllowed = install.CanDesignateCell(installIn);
                    if (installRefused.Accepted || installRefused.Reason != outsideText || !installAllowed.Accepted) GroundsFail($"installing a table: outside {installOut} accepted {installRefused.Accepted} (\"{installRefused.Reason}\"), inside {installIn} accepted {installAllowed.Accepted} (\"{installAllowed.Reason}\")");
                    DebugSettings.godMode = true;
                    if (!install.CanDesignateCell(installOut).Accepted) GroundsFail("god mode was refused an install outside the grounds");
                    DebugSettings.godMode = false;
                    Find.Selector.ClearSelection();
                }
                bool defined = g.defined;
                g.defined = false;
                if (!wallTool.CanDesignateCell(outside).Accepted) GroundsFail("a map without grounds refused a wall");
                g.defined = defined;

                // The shuttle pad: its hull and its ramp are the prison's, not only the cell it stands on.
                ThingDef pad = RPShuttleDefOf.RP_ShuttlePad;
                bool PadInside(IntVec3 c)
                {
                    IntVec3 ramp = ShuttleSystem.Footprint(c, out CellRect hull);
                    return g.Contains(hull) && g.Contains(ramp);
                }
                IntVec3 padIn = FirstCell(rect.Cells, c => g.Contains(GenAdj.OccupiedRect(c, Rot4.North, pad.size)) && PadInside(c) && !c.InNoBuildEdgeArea(map) && Vanilla(pad, c));
                IntVec3 padHalf = FirstCell(rect.Cells, c => g.Contains(GenAdj.OccupiedRect(c, Rot4.North, pad.size)) && !PadInside(c) && !c.InNoBuildEdgeArea(map) && Vanilla(pad, c));
                IntVec3 padOut = FirstCell(rect.ExpandedBy(12).Cells, c => !g.Contains(GenAdj.OccupiedRect(c, Rot4.North, pad.size)) && !c.InNoBuildEdgeArea(map) && Vanilla(pad, c));
                var padTool = new Designator_Build(pad);
                if (!padIn.IsValid || padTool.CanDesignateCell(padIn).Accepted == false) GroundsFail($"a pad inside the grounds ({padIn}) was refused or there was no room for one");
                if (padOut.IsValid && (padTool.CanDesignateCell(padOut).Accepted || padTool.CanDesignateCell(padOut).Reason != outsideText)) GroundsFail($"a pad outside the grounds at {padOut} was accepted");
                if (padHalf.IsValid && (padTool.CanDesignateCell(padHalf).Accepted || padTool.CanDesignateCell(padHalf).Reason != outsideText)) GroundsFail($"a pad whose ramp is outside the grounds at {padHalf} was accepted");
                if (!padHalf.IsValid) RPLog.Message("[RimPrison:test] grounds: no cell inside the grounds puts only the pad's ramp outside on this map; the ramp rule was not tried");

                // The shuttle's fallback landing site (no pad): clear ground the grounds do not hold is no site.
                IntVec3 siteOut = FirstCell(rect.ExpandedBy(20).Cells, c => !SiteInside(g, c) && WithoutGrounds(g, () => ShuttleSystem.SiteFits(map, c, allowZones: true)));
                if (!siteOut.IsValid) RPLog.Message("[RimPrison:test] grounds: no open ground outside the grounds to try a landing site on");
                else if (ShuttleSystem.SiteFits(map, siteOut, allowZones: true)) GroundsFail($"clear ground at {siteOut} outside the grounds was taken for a landing site");
                IntVec3 anchor = ShuttleSystem.Anchor(map);
                IntVec3 found = ShuttleSystem.FindOpenSite(map, anchor, allowZones: true);
                if (found.IsValid && !SiteInside(g, found)) GroundsFail($"the fallback site {found} is not inside the grounds");
                // With only a patch too small for the hull owned, the grounds have no site: the shuttle comes down on the nearest clear ground beside them (it is never turned away for want of land).
                bool baseline = WithoutGrounds(g, () => ShuttleSystem.FindOpenSite(map, anchor, allowZones: true).IsValid);
                g.Set(CellRect.CenteredOn(anchor, PrisonGrounds.MinSide, PrisonGrounds.MinSide));
                if (baseline)
                {
                    if (ShuttleSystem.FindOpenSite(map, anchor, allowZones: true).IsValid) GroundsFail("a landing site was found on a patch of the grounds too small to hold the hull");
                    IntVec3 beside = ShuttleSystem.SearchSite(map, anchor);
                    if (!beside.IsValid) GroundsFail("the grounds have no room for the hull and the shuttle found nowhere else to land (it would be cancelled every day)");
                    else if (SiteInside(g, beside)) GroundsFail($"the site {beside} beside a full plot is inside it");
                    else
                    {
                        // The search the visit makes, and what the readers of the site (reception, a transfer's way out) see: the site beside the grounds stays the site.
                        ShuttleSystem sys = PrisonGame.Instance.shuttle;
                        r.Add(sys.ForgetSite);
                        sys.ForgetSite();
                        IntVec3 chosen = sys.FallbackSite(map);
                        if (!chosen.IsValid || sys.KnownFallbackSite(map) != chosen || !sys.ReceptionAnchor(map).IsValid) GroundsFail($"the site beside the grounds ({chosen}) was not kept for the readers of the site (known {sys.KnownFallbackSite(map)})");
                    }
                    // Once the grounds hold a site again it is the first choice.
                    g.Set(rect);
                    IntVec3 first = ShuttleSystem.SearchSite(map, anchor);
                    if (first.IsValid && ShuttleSystem.FindOpenSite(map, anchor, allowZones: true).IsValid && !SiteInside(g, first)) GroundsFail($"with room on the grounds the site {first} is outside them");
                }
                else RPLog.Message("[RimPrison:test] grounds: the map has no fallback landing site even without grounds; the small-grounds case was not tried");
            });
        }

        private static T WithoutGrounds<T>(PrisonGrounds g, Func<T> ask)
        {
            bool defined = g.defined;
            g.defined = false;
            try
            {
                return ask();
            }
            finally
            {
                g.defined = defined;
            }
        }

        // ---- buying ---------------------------------------------------------------------------------------------------------------------------

        private static void CheckGroundsBuying(Map map)
        {
            Guarded("grounds: buying a parcel", map, r =>
            {
                PrisonGame game = PrisonGame.Instance;
                PrisonGrounds g = PrisonGrounds.Of(map);
                r.Add(RememberGrounds(g));
                // A parcel is the strip along a side: the side's length by the depth, outside, touching, priced by area.
                CellRect rect = g.Rect;
                foreach (GroundsSide side in LandTab.Sides)
                {
                    Parcel p = g.ParcelFor(side);
                    CellRect strip = p.cells;
                    bool touches = side switch
                    {
                        GroundsSide.North => strip.minZ == rect.maxZ + 1 && strip.minX == rect.minX && strip.Width == rect.Width,
                        GroundsSide.South => strip.maxZ == rect.minZ - 1 && strip.minX == rect.minX && strip.Width == rect.Width,
                        GroundsSide.East => strip.minX == rect.maxX + 1 && strip.minZ == rect.minZ && strip.Height == rect.Height,
                        _ => strip.maxX == rect.minX - 1 && strip.minZ == rect.minZ && strip.Height == rect.Height,
                    };
                    int depth = side is GroundsSide.North or GroundsSide.South ? strip.Height : strip.Width;
                    if (!p.Exists || !touches || depth != PrisonGrounds.ParcelDepth || p.price != g.PriceOf(strip.Area) || g.Contains(strip.CenterCell)) GroundsFail($"the {side} parcel is {strip} for {p.price} (grounds {rect}), not a strip of {PrisonGrounds.ParcelDepth} cells along the side");
                }
                // Every cell the state sells can be built on: bought up to the last strip on each side, none of it is in vanilla's no-build edge.
                if (!map.IsPocketMap && !map.Tile.LayerDef.ignoreNoBuildArea)
                {
                    g.SetWholeMap();
                    CellRect all = g.Rect;
                    foreach (GroundsSide side in LandTab.Sides)
                    {
                        g.Set(CellRect.FromLimits(all.minX + 20, all.minZ + 20, all.maxX - 20, all.maxZ - 20));
                        for (int k = 0; k < 40 && g.ParcelFor(side).Exists; k++)
                        {
                            Parcel strip = g.ParcelFor(side);
                            IntVec3 bad = FirstCell(strip.cells.Cells, c => c.InNoBuildEdgeArea(map));
                            if (bad.IsValid)
                            {
                                GroundsFail($"the {side} parcel {strip.cells} for {strip.price} holds {bad}, in vanilla's no-build edge: land for sale that cannot be built on");
                                break;
                            }
                            g.MoveSide(side, side is GroundsSide.North or GroundsSide.South ? strip.cells.Height : strip.cells.Width);
                        }
                    }
                    g.Set(rect);
                }
                // Cheaper at an easy difficulty and dearer at a hard one, by the level the game started on: changing the difficulty afterwards (Options, Change storyteller) leaves the price alone.
                int perThousand = g.PriceOf(1000);
                DifficultyDef difficulty0 = Find.Storyteller.difficultyDef;
                r.Add(() => Find.Storyteller.difficultyDef = difficulty0);
                Find.Storyteller.difficultyDef = DefDatabase<DifficultyDef>.GetNamed("Peaceful");
                int easyNow = g.PriceOf(1000);
                Find.Storyteller.difficultyDef = DefDatabase<DifficultyDef>.GetNamed("Extreme");
                int hardNow = g.PriceOf(1000);
                float shown = g.LandFactor;
                Find.Storyteller.difficultyDef = difficulty0;
                if (easyNow != perThousand || hardNow != perThousand || shown != g.LandFactor) GroundsFail($"1000 cells cost {easyNow} / {perThousand} / {hardNow} (peaceful / game's / extreme, set mid-game): the price follows the difficulty now in force");
                var started = new PrisonGrounds(map);
                Find.Storyteller.difficultyDef = DefDatabase<DifficultyDef>.GetNamed("Peaceful");
                started.SetStarting(built: false);
                int easy = started.PriceOf(1000);
                var startedHard = new PrisonGrounds(map);
                Find.Storyteller.difficultyDef = DefDatabase<DifficultyDef>.GetNamed("Extreme");
                startedHard.SetStarting(built: false);
                int hard = startedHard.PriceOf(1000);
                Find.Storyteller.difficultyDef = difficulty0;
                if (!(easy < hard) || started.landFactor != PrisonDifficulty.LeversOf(DefDatabase<DifficultyDef>.GetNamed("Peaceful")).land || startedHard.landFactor != PrisonDifficulty.LeversOf(DefDatabase<DifficultyDef>.GetNamed("Extreme")).land)
                    GroundsFail($"a game started on peaceful fixed the land lever at {started.landFactor} (1000 cells {easy}), on extreme {startedHard.landFactor} ({hard}): not dearer with the difficulty it started on");
                // Grounds saved before the lever was kept: the difficulty in force when they load is fixed for good.
                var loaded = new PrisonGrounds(map) { defined = true, minX = 20, minZ = 20, maxX = 60, maxZ = 60 };
                Find.Storyteller.difficultyDef = DefDatabase<DifficultyDef>.GetNamed("Hard");
                loaded.FixLandFactor();
                int loadedPrice = loaded.PriceOf(1000);
                Find.Storyteller.difficultyDef = DefDatabase<DifficultyDef>.GetNamed("Peaceful");
                if (loaded.landFactor != PrisonDifficulty.LeversOf(DefDatabase<DifficultyDef>.GetNamed("Hard")).land || loaded.PriceOf(1000) != loadedPrice) GroundsFail($"grounds loaded without a lever took {loaded.landFactor}, not the difficulty then in force, or were priced by the one now ({loaded.PriceOf(1000)} for {loadedPrice})");
                Find.Storyteller.difficultyDef = difficulty0;
                // Buy: the ledger is charged, the rect grows by the depth at once, the chronicle and the counters say so.
                game.ledger.funds = 500000;
                Parcel east = g.ParcelFor(GroundsSide.East);
                int funds0 = game.ledger.funds, landEntries0 = game.ledger.recent.Count(e => e.category == RPDefOf.RP_Land), parcels0 = game.stats.landParcels, cells0 = game.stats.landCells, bought0 = g.parcelsBought;
                int entries0 = game.story.entries.Count;
                if (!g.TryBuy(GroundsSide.East)) GroundsFail("buying the east parcel with money in hand failed");
                CellRect after = g.Rect;
                if (game.ledger.funds != funds0 - east.price) GroundsFail($"buying charged {funds0 - game.ledger.funds}, not the price {east.price}");
                if (game.ledger.recent.Count(e => e.category == RPDefOf.RP_Land) != landEntries0 + 1) GroundsFail("the purchase is not in the ledger under land");
                if (after.maxX != rect.maxX + PrisonGrounds.ParcelDepth || after.minX != rect.minX || after.minZ != rect.minZ || after.maxZ != rect.maxZ) GroundsFail($"the grounds are {after} after buying east from {rect}");
                if (game.stats.landParcels != parcels0 + 1 || game.stats.landCells != cells0 + east.cells.Area || g.parcelsBought != bought0 + 1) GroundsFail($"the counters read {game.stats.landParcels}/{game.stats.landCells}/{g.parcelsBought} after one parcel of {east.cells.Area} cells");
                if (game.story.entries.Count != entries0 + 1) GroundsFail($"the purchase wrote {game.story.entries.Count - entries0} chronicle lines, expected one");
                if (g.PriceOf(1000) <= perThousand) GroundsFail("land is not dearer after buying some");
                if (!g.Contains(east.cells)) GroundsFail("the bought strip is not inside the grounds");
                // Without the money: refused, nothing changes.
                Parcel north = g.ParcelFor(GroundsSide.North);
                game.ledger.funds = north.price - 1;
                CellRect before = g.Rect;
                int ledgerCount = game.ledger.recent.Count;
                if (g.ParcelFor(GroundsSide.North).blocked == null || g.TryBuy(GroundsSide.North) || g.Rect != before || game.ledger.funds != north.price - 1 || game.ledger.recent.Count != ledgerCount) GroundsFail("a parcel was bought without the money, or the refusal had no reason");
                // Special measures: no sale.
                game.ledger.funds = 500000;
                StateOversight oversight = game.story.oversight;
                bool active0 = oversight.active;
                r.Add(() => oversight.active = active0);
                oversight.active = true;
                if (g.ParcelFor(GroundsSide.North).blocked == null || g.TryBuy(GroundsSide.North) || g.Rect != before) GroundsFail("the state sold land during special measures");
                oversight.active = active0;
                // ...but only while they run: the state's chief in post after they failed (underChief) does not close the sale, and the Land tab's reason is not the measures'.
                bool under0 = oversight.underChief;
                r.Add(() => oversight.underChief = under0);
                oversight.active = false;
                oversight.underChief = true;
                if (!SpecialMeasures.Restricted || SpecialMeasures.Running || g.ParcelFor(GroundsSide.North).blocked != null) GroundsFail($"the sale is closed under the state's chief (running {SpecialMeasures.Running}, blocked \"{g.ParcelFor(GroundsSide.North).blocked}\")");
                oversight.underChief = under0;
                oversight.active = active0;
                // The wages the night's settlement pays stay in hand: a purchase that left less is off, with the reason, at the edge of it and not before.
                int wages = StaffCareers.Payroll(StaffCommute.AllStaff(map));
                if (wages <= 0) RPLog.Message("[RimPrison:test] grounds: the prison has no payroll; the wages reserve was not tried");
                else
                {
                    Parcel pn = g.ParcelFor(GroundsSide.North);
                    game.ledger.funds = pn.price + wages - 1;
                    Parcel short1 = g.ParcelFor(GroundsSide.North);
                    int fundsShort = game.ledger.funds;
                    CellRect rectShort = g.Rect;
                    if (short1.blocked == null || g.TryBuy(GroundsSide.North) || game.ledger.funds != fundsShort || g.Rect != rectShort) GroundsFail($"a purchase of {pn.price} that leaves {wages - 1} of the {wages} in wages went through (blocked \"{short1.blocked}\")");
                    game.ledger.funds = pn.price + wages;
                    if (g.ParcelFor(GroundsSide.North).blocked != null) GroundsFail($"a purchase that leaves exactly the wages ({wages}) was refused: \"{g.ParcelFor(GroundsSide.North).blocked}\"");
                    game.ledger.funds = 500000;
                }
                // The edge of the map: a parcel is cut short where the margin begins, and then there is none.
                CellRect whole = g.Whole;
                g.Set(CellRect.FromLimits(g.Rect.minX, g.Rect.minZ, g.Rect.maxX, whole.maxZ - 7));
                Parcel nearEdge = g.ParcelFor(GroundsSide.North);
                if (nearEdge.cells.Height != 7 || nearEdge.price != g.PriceOf(nearEdge.cells.Area)) GroundsFail($"the parcel 7 cells from the edge is {nearEdge.cells.Height} deep for {nearEdge.price}");
                if (!g.TryBuy(GroundsSide.North) || g.Rect.maxZ != whole.maxZ) GroundsFail($"the short parcel was not bought up to the edge ({g.Rect.maxZ} of {whole.maxZ})");
                Parcel none = g.ParcelFor(GroundsSide.North);
                int fundsAtEdge = game.ledger.funds;
                if (none.Exists || none.blocked == null || g.TryBuy(GroundsSide.North) || game.ledger.funds != fundsAtEdge || g.Rect.maxZ != whole.maxZ) GroundsFail("land was sold past the edge of the map");
            });
        }

        // ---- dev mode ------------------------------------------------------------------------------------------------------------------------

        private static void CheckGroundsDev(Map map)
        {
            Guarded("grounds: dev moves and what shrinking leaves", map, r =>
            {
                PrisonGrounds g = PrisonGrounds.Of(map);
                r.Add(RememberGrounds(g));
                CellRect whole = g.Whole, start = g.Start;
                CellRect rect = g.Rect;
                int funds0 = PrisonGame.Instance.ledger.funds;
                // Each side by 1, 5 and 10, out and in, for nothing.
                foreach (GroundsSide side in LandTab.Sides)
                    foreach (int n in new[] { 1, 5, 10 })
                    {
                        g.Set(rect);
                        int moved = g.MoveSide(side, n);
                        CellRect expanded = g.Rect;
                        int growth = side switch { GroundsSide.North => expanded.Height - rect.Height, GroundsSide.South => expanded.Height - rect.Height, GroundsSide.East => expanded.Width - rect.Width, _ => expanded.Width - rect.Width };
                        int shrunk = g.MoveSide(side, -n);
                        if (moved != n || growth != n || shrunk != -n || g.Rect != rect) GroundsFail($"{side} by {n}: moved {moved}, grew {growth}, back {shrunk}, grounds {g.Rect} (was {rect})");
                    }
                if (PrisonGame.Instance.ledger.funds != funds0) GroundsFail("the dev moves cost money");
                // The edge of the map stops an expansion, the minimum stops a shrink.
                foreach (GroundsSide side in LandTab.Sides)
                {
                    g.Set(rect);
                    int far = g.MoveSide(side, 9999);
                    CellRect now = g.Rect;
                    bool atEdge = side switch { GroundsSide.North => now.maxZ == whole.maxZ, GroundsSide.South => now.minZ == whole.minZ, GroundsSide.East => now.maxX == whole.maxX, _ => now.minX == whole.minX };
                    if (!atEdge || far <= 0) GroundsFail($"{side} by 9999 gave {now} ({far} cells), expected the edge of {whole}");
                    int more = g.MoveSide(side, 5);
                    if (more != 0) GroundsFail($"{side} moved {more} cells past the edge");
                    g.Set(rect);
                    int least = g.MoveSide(side, -9999);
                    int length = side is GroundsSide.North or GroundsSide.South ? g.Rect.Height : g.Rect.Width;
                    if (length != PrisonGrounds.MinSide) GroundsFail($"{side} by -9999 left the grounds {length} cells across, expected {PrisonGrounds.MinSide} (moved {least})");
                }
                g.SetWholeMap();
                if (g.Rect != whole) GroundsFail($"whole map gave {g.Rect}, not {whole}");
                g.parcelsBought = 3;
                g.SetBackToStart();
                if (g.Rect != start) GroundsFail($"back to the start gave {g.Rect}, not {start}");
                if (g.parcelsBought != 0) GroundsFail($"back to the start left {g.parcelsBought} parcels counted on a plot that has none");
                // Dev margins for the next new game do not outlive the game they were meant for: a game made or loaded starts with none pending.
                int[] pending0 = PrisonGrounds.PendingMargins;
                r.Add(() => PrisonGrounds.PendingMargins = pending0);
                PrisonGrounds.PendingMargins = new[] { 7, 7, 7, 7 };
                var gameCtor = AccessTools.Constructor(typeof(Game), new Type[0]);
                if (Harmony.GetPatchInfo(gameCtor)?.Postfixes.Any(x => x.PatchMethod.DeclaringType == typeof(Patch_Game_Constructor_ClearPendingMargins)) != true) GroundsFail("the Game constructor does not clear the pending dev margins");
                else
                {
                    // (Not a real new Game: its constructor makes a PrisonGame, which would take the place of the live one.)
                    Patch_Game_Constructor_ClearPendingMargins.Postfix();
                    if (PrisonGrounds.PendingMargins != null) GroundsFail("margins pending from a start that never used them survived a new Game");
                }
                // The debug actions menu (dev mode, "Rim Prison"): each side out and in by ten, every side, the whole map, the starting grounds.
                MethodInfo[] actions = typeof(GroundsDebugActions).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Where(m => m.GetCustomAttribute<LudeonTK.DebugActionAttribute>() is { category: "Rim Prison" }).ToArray();
                MethodInfo Action(string name) => actions.FirstOrDefault(m => m.GetCustomAttribute<LudeonTK.DebugActionAttribute>().name == name);
                if (actions.Length != 12) GroundsFail($"{actions.Length} debug actions in the Rim Prison category, expected twelve");
                else
                {
                    g.Set(rect);
                    foreach (GroundsSide side in LandTab.Sides)
                    {
                        string label = side.ToString().ToLowerInvariant();
                        Action($"Grounds: {label} +10")?.Invoke(null, null);
                        CellRect out10 = g.Rect;
                        Action($"Grounds: {label} -10")?.Invoke(null, null);
                        if (out10 == rect || g.Rect != rect) GroundsFail($"the debug actions for {label} gave {out10} and back {g.Rect} (was {rect})");
                    }
                    Action("Grounds: every side +10")?.Invoke(null, null);
                    CellRect all = g.Rect;
                    Action("Grounds: every side -10")?.Invoke(null, null);
                    if (all.Width != rect.Width + 20 || all.Height != rect.Height + 20 || g.Rect != rect) GroundsFail($"every side +10 gave {all}, and -10 {g.Rect} (was {rect})");
                    Action("Grounds: whole map")?.Invoke(null, null);
                    bool whole1 = g.Rect == whole;
                    Action("Grounds: back to the starting grounds")?.Invoke(null, null);
                    if (!whole1 || g.Rect != start) GroundsFail($"the debug actions whole map ({whole1}) and back to the start ({g.Rect}, expected {start}) did not work");
                }
                // Shrinking takes nothing away: a thing left outside stays and is not destroyed; only a new one beside it is refused.
                g.Set(rect);
                bool god = DebugSettings.godMode;
                r.Add(() => DebugSettings.godMode = god);
                DebugSettings.godMode = false;
                IntVec3 spot = FirstCell(rect.Cells, c => c.x == rect.maxX && !c.InNoBuildEdgeArea(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null && c.Standable(map)
                    && GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, c, Rot4.North, map, godMode: true, null, null, ThingDefOf.BlocksGranite).Accepted);
                if (!spot.IsValid)
                {
                    RPLog.Message("[RimPrison:test] grounds: no clear cell on the east edge to leave a wall outside on; the shrink check was not tried");
                    return;
                }
                Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, spot, map);
                r.Add(() =>
                {
                    if (!wall.Destroyed) wall.Destroy(DestroyMode.Vanish);
                });
                g.MoveSide(GroundsSide.East, -10);
                if (g.Contains(spot) || !wall.Spawned || wall.Destroyed) GroundsFail("shrinking the grounds removed a wall that was left outside them");
                IntVec3 next = spot + IntVec3.North;
                if (new Designator_Build(ThingDefOf.Wall).CanDesignateCell(next).Accepted) GroundsFail($"a new wall beside the left-over one, outside the grounds, was accepted at {next}");
            });
        }

        // ---- the to-do line and the learning helper's note --------------------------------------------------------------------------------

        private static void CheckGroundsTodoAndNote(Map map)
        {
            Guarded("grounds: the to-do line and the learning helper's note", map, r =>
            {
                PrisonGame game = PrisonGame.Instance;
                PrisonGrounds g = PrisonGrounds.Of(map);
                r.Add(RememberGrounds(g));
                bool adaptive = Prefs.AdaptiveTrainingEnabled;
                r.Add(() => Prefs.AdaptiveTrainingEnabled = adaptive);
                Prefs.AdaptiveTrainingEnabled = true;
                LearningReadout readout = Find.Tutor.learningReadout;
                ConceptDef note = ConceptDef.Named(Concepts.Grounds);
                float knowledge = PlayerKnowledgeDatabase.GetKnowledge(note);
                r.Add(() =>
                {
                    PlayerKnowledgeDatabase.SetKnowledge(note, knowledge);
                    readout.Notify_ConceptNewlyLearned(note);
                    Concepts.Reset();
                });
                PlayerKnowledgeDatabase.SetKnowledge(note, 0f);
                readout.Notify_ConceptNewlyLearned(note);
                Concepts.Reset();
                g.triedOutsideTick = -999999;
                bool Listed() => PrisonDiagnostics.Items(map).Any(i => i.tab == MainTabWindow_Prison.Tab.Land);
                if (Listed()) GroundsFail("the overview lists the grounds before anybody tried to build outside them");
                // The designator that came to nothing because of the grounds tells them (what the click does: the dragger's reason is ours).
                var dragger = Find.DesignatorManager.Dragger;
                FieldInfo reasonField = AccessTools.Field(typeof(DesignationDragger), "failureReasonInt"), frameField = AccessTools.Field(typeof(DesignationDragger), "lastUpdateFrame");
                object reason0 = reasonField.GetValue(dragger), frame0 = frameField.GetValue(dragger);
                r.Add(() =>
                {
                    reasonField.SetValue(dragger, reason0);
                    frameField.SetValue(dragger, frame0);
                });
                // (The cell under the mouse: inside the grounds, wherever the headless cursor is.)
                Patch_Designator_FinalizeDesignationFailed.MouseCellForTests = g.Rect.CenterCell;
                r.Add(() => Patch_Designator_FinalizeDesignationFailed.MouseCellForTests = null);
                reasonField.SetValue(dragger, "something else");
                frameField.SetValue(dragger, UnityEngine.Time.frameCount);
                AccessTools.Method(typeof(Designator), "FinalizeDesignationFailed").Invoke(new Designator_Build(ThingDefOf.Wall), null);
                if (Listed() || g.TriedOutsideRecently) GroundsFail("a refusal for another reason was taken for a try to build outside");
                reasonField.SetValue(dragger, PrisonGrounds.OutsideText);
                frameField.SetValue(dragger, UnityEngine.Time.frameCount);
                AccessTools.Method(typeof(Designator), "FinalizeDesignationFailed").Invoke(new Designator_Build(ThingDefOf.Wall), null);
                if (!Listed() || !g.TriedOutsideRecently) GroundsFail("a refused try to build outside the grounds is not on the overview's list");
                if (!readout.IsActive(note)) GroundsFail("a refused try to build outside the grounds did not teach the grounds' note");
                // A designator with no draw style (a bed, the shuttle pad, an install) is clicked once and vanilla keeps the reason with the cell, not the dragger: the cell under the mouse is asked again.
                CellRect rect = g.Rect;
                bool god = DebugSettings.godMode;
                r.Add(() => DebugSettings.godMode = god);
                DebugSettings.godMode = false;
                ThingDef bed = DefDatabase<ThingDef>.GetNamed("Bed");
                ThingDef pad = RPShuttleDefOf.RP_ShuttlePad;
                bool Vanilla(BuildableDef def, IntVec3 c) => GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map, godMode: true, null, null, def is ThingDef { MadeFromStuff: true } ? ThingDefOf.WoodLog : null).Accepted;
                bool InsideWhole(PrisonGrounds gg, BuildableDef def, IntVec3 c) => gg.Contains(GenAdj.OccupiedRect(c, Rot4.North, def.Size));
                IntVec3 bedOut = FirstCell(rect.ExpandedBy(6).Cells, c => !InsideWhole(g, bed, c) && !c.InNoBuildEdgeArea(map) && Vanilla(bed, c));
                IntVec3 bedIn = FirstCell(rect.Cells, c => InsideWhole(g, bed, c) && !c.InNoBuildEdgeArea(map) && c.GetFirstThing<Thing>(map) == null && c.GetEdifice(map) == null && Vanilla(bed, c));
                IntVec3 padOut = FirstCell(rect.ExpandedBy(12).Cells, c => !InsideWhole(g, pad, c) && !c.InNoBuildEdgeArea(map) && Vanilla(pad, c));
                Patch_Designator_FinalizeDesignationFailed.MouseCellForTests = null;
                r.Add(() => Patch_Designator_FinalizeDesignationFailed.MouseCellForTests = null);
                void Click(Designator tool, IntVec3 cell)
                {
                    g.triedOutsideTick = -999999;
                    Patch_Designator_FinalizeDesignationFailed.MouseCellForTests = cell;
                    reasonField.SetValue(dragger, null);
                    frameField.SetValue(dragger, UnityEngine.Time.frameCount);
                    AccessTools.Method(typeof(Designator), "FinalizeDesignationFailed").Invoke(tool, null);
                }
                if (!bedOut.IsValid || !bedIn.IsValid || !padOut.IsValid) RPLog.Message($"[RimPrison:test] grounds: no clear ground to click a bed or the pad on (bed outside {bedOut}, inside {bedIn}, pad outside {padOut})");
                else
                {
                    Click(new Designator_Build(bed), bedOut);
                    if (!Listed() || !g.TriedOutsideRecently) GroundsFail($"a bed clicked outside the grounds at {bedOut} (a click, not a drag) is not on the overview's list");
                    Click(new Designator_Build(pad), padOut);
                    if (!Listed()) GroundsFail($"the shuttle pad clicked outside the grounds at {padOut} is not on the overview's list");
                    Click(new Designator_Build(bed), bedIn);
                    if (Listed() || g.TriedOutsideRecently) GroundsFail($"a bed refused inside the grounds ({bedIn}) was taken for a try to build outside");
                }
                Patch_Designator_FinalizeDesignationFailed.MouseCellForTests = null;
                g.triedOutsideTick = -999999;
                // The warden's list does not ask for land that cannot be bought now: the measures' reason while they run, nothing at all with no strip to sell.
                StateOversight oversight = game.story.oversight;
                bool active0 = oversight.active;
                r.Add(() => oversight.active = active0);
                g.Notify_TriedOutside();
                string BuyLine() => PrisonDiagnostics.Items(map).FirstOrDefault(i => i.tab == MainTabWindow_Prison.Tab.Land).text;
                oversight.active = false;
                if (BuyLine() != "RP_Problem_OutsideGrounds".Translate().Resolve()) GroundsFail($"the line after a refused try is \"{BuyLine()}\", not the one that points to the Land tab");
                oversight.active = true;
                if (BuyLine() != "RP_Problem_OutsideGroundsMeasures".Translate().Resolve()) GroundsFail($"while special measures run the line is \"{BuyLine()}\", not the one that says the state sells nothing");
                oversight.active = active0;
                CellRect rectLine = g.Rect;
                g.Set(g.Whole);
                if (g.AnyForSale || Listed()) GroundsFail($"with the whole land owned the overview still asks the warden to buy more (for sale {g.AnyForSale}, listed {Listed()})");
                g.Set(rectLine);
                // It stays for a day, and buying land takes it off.
                g.triedOutsideTick = Find.TickManager.TicksGame - PrisonGrounds.TriedOutsideTicks - 1;
                if (Listed()) GroundsFail("the try to build outside is still on the list a day later");
                g.Notify_TriedOutside();
                game.ledger.funds = 500000;
                if (!g.TryBuy(GroundsSide.South) || Listed()) GroundsFail("buying land did not take the line off the list");
                if (!PlayerKnowledgeDatabase.IsComplete(note)) GroundsFail("buying land did not mark the grounds' note learned");
            });
        }

        // ---- the map overlay -------------------------------------------------------------------------------------------------------------

        private static void CheckGroundsOverlay(Map map)
        {
            Guarded("grounds: the overlay's toggle hides the line, not the dimming", map, r =>
            {
                PrisonGrounds g = PrisonGrounds.Of(map);
                bool show0 = GroundsOverlay.Show;
                Designator tool0 = Find.DesignatorManager.SelectedDesignator;
                r.Add(() =>
                {
                    GroundsOverlay.Show = show0;
                    Find.DesignatorManager.Deselect();
                    if (tool0 != null) Find.DesignatorManager.Select(tool0);
                });
                Find.DesignatorManager.Select(new Designator_Build(ThingDefOf.Wall));
                GroundsOverlay.Show = true;
                if (!GroundsOverlay.ShowsBorder(g) || !GroundsOverlay.ShowsDim(g)) GroundsFail($"with the line on and a wall tool in hand the border is {GroundsOverlay.ShowsBorder(g)} and the dimming {GroundsOverlay.ShowsDim(g)}");
                GroundsOverlay.Show = false;
                if (GroundsOverlay.ShowsBorder(g)) GroundsFail("the line is drawn with the toggle off");
                if (!GroundsOverlay.ShowsDim(g)) GroundsFail("turning the line off took the dimming with it: the warden building gets no sign of where the land ends");
            });
        }

        /// <summary>Saveload plan, before the snapshot: grounds that are not the start (a side moved in, one parcel counted), which the snapshot reads back after the reload.</summary>
        private static void SeedGroundsForReload(Map map)
        {
            PrisonGrounds g = PrisonGrounds.Of(map);
            if (g == null) return;
            g.MoveSide(GroundsSide.East, -3);
            g.MoveSide(GroundsSide.North, 6);
            g.parcelsBought = 2;
            // (A lever that is not any level's: a game that did not save it would come back with a level's.)
            g.landFactor = 1.37f;
            PrisonGame.Instance.stats.landParcels = 2;
            PrisonGame.Instance.stats.landCells = 1234;
        }
    }
}
