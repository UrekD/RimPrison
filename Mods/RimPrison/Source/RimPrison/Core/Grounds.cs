using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public enum GroundsSide : byte { North, East, South, West }

    /// <summary>The strip of land the state offers along one side of the grounds, what it costs, and why it cannot be bought now (null: it can).</summary>
    public readonly struct Parcel
    {
        public readonly GroundsSide side;
        public readonly CellRect cells;
        public readonly int price;
        public readonly string blocked;

        public Parcel(GroundsSide side, CellRect cells, int price, string blocked)
        {
            this.side = side;
            this.cells = cells;
            this.price = price;
            this.blocked = blocked;
        }

        public bool Exists => cells.Area > 0;
    }

    /// <summary>
    /// The prison's grounds, as in Prison Architect: a rectangle of the map the prison owns. Building (every blueprint, floor and install), painting zones (Rooms and vanilla's) and the
    /// shuttle pad are allowed only inside it; pawns walk anywhere. A new game owns a plot round its facility (<see cref="SetStarting"/>) and buys more with <see cref="TryBuy"/>, a strip
    /// along a side at a time; dev mode moves the sides for nothing. A save from before land has no grounds (<see cref="defined"/> false): the whole map is the warden's, as it was.
    /// Shrinking never removes anything: what stands outside stays and works, only new building there is refused. Kept per map (<see cref="PrisonMap.grounds"/>) and scribed with it.
    /// </summary>
    public class PrisonGrounds : IExposable
    {
        /// <summary>Cells kept clear along the map edge: vanilla refuses a blueprint within this many cells of it (<see cref="GenGrid.InNoBuildEdgeArea"/>), so land there could be bought and never built on; the grounds never reach it (staff, visitors and the shuttle's crew still come and go).</summary>
        public const int EdgeMargin = GenGrid.NoBuildEdgeWidth;
        /// <summary>How deep a parcel is, along the side it is bought on.</summary>
        public const int ParcelDepth = 15;
        /// <summary>The land a built facility owns beyond its footprint, on each side (the penitentiary's road, pad and deliveries are inside the footprint already).</summary>
        public const int StartMargin = 12;
        /// <summary>The footprint a new facility is given, centred (an empty plot has no walls to go by): with the margin it owns 80 x 80 cells.</summary>
        public const int NewFacilitySide = 56;
        /// <summary>Dev mode cannot shrink a side past this (the grounds stay a place).</summary>
        public const int MinSide = 10;
        /// <summary>What a cell costs at the neutral difficulty with no land owned, and how much dearer each cell gets with the land already owned (the price rises by half for every <see cref="GrowthCells"/> cells owned).</summary>
        public const float CellPrice = 2f, GrowthCells = 12000f;
        /// <summary>How long a refused try to build outside stays on the overview's list (a day).</summary>
        public const int TriedOutsideTicks = GenDate.TicksPerDay;

        private readonly Map map;

        /// <summary>False in a save from before land: the whole map is owned and nothing is refused.</summary>
        public bool defined;
        public int minX, minZ, maxX, maxZ;
        public int startMinX, startMinZ, startMaxX, startMaxZ;
        /// <summary>Parcels the prison has bought (each one dearer than the last through the land already owned).</summary>
        public int parcelsBought;
        /// <summary>When the warden last tried to build or paint outside the grounds (not saved: a to-do line for the day).</summary>
        public int triedOutsideTick = -999999;
        /// <summary>Counts every change of the grounds: what is drawn from them and what is cached on them looks at it.</summary>
        public int version;
        /// <summary>The difficulty's land lever as it was when the game started (saved: a later change of difficulty leaves the price alone). Zero until set.</summary>
        public float landFactor;

        public PrisonGrounds(Map map) => this.map = map;

        /// <summary>The map has grounds to keep to (a save from before land has none).</summary>
        public bool Limited => defined;

        /// <summary>The margin the map keeps clear: vanilla's no-build edge, or only its no-zone edge where vanilla allows building up to the edge (pocket maps, layers that ignore it).</summary>
        public int Margin => map.IsPocketMap || map.Tile.LayerDef.ignoreNoBuildArea ? GenGrid.NoZoneEdgeWidth : EdgeMargin;

        /// <summary>The land the map allows the grounds: the whole map less the margin along its edge (all of it can be built on).</summary>
        public CellRect Whole => CellRect.FromLimits(Margin, Margin, map.Size.x - 1 - Margin, map.Size.z - 1 - Margin);

        /// <summary>The owned land (the whole map without grounds).</summary>
        public CellRect Rect => defined ? CellRect.FromLimits(minX, minZ, maxX, maxZ) : CellRect.WholeMap(map);

        public CellRect Start => defined ? CellRect.FromLimits(startMinX, startMinZ, startMaxX, startMaxZ) : CellRect.WholeMap(map);

        public int Area => Rect.Area;

        public bool Contains(IntVec3 c) => !defined || (c.x >= minX && c.x <= maxX && c.z >= minZ && c.z <= maxZ);

        public bool Contains(CellRect r) => !defined || (r.minX >= minX && r.maxX <= maxX && r.minZ >= minZ && r.maxZ <= maxZ);

        public static PrisonGrounds Of(Map map) => PrisonMap.Of(map)?.grounds;

        /// <summary>The cell is inside the grounds of its map (true on a map without any, and without a prison).</summary>
        public static bool Allows(Map map, IntVec3 c) => Of(map)?.Contains(c) ?? true;

        // ---- the start ------------------------------------------------------------------------------------------------------------------

        /// <summary>The grounds a new game starts with: the facility's footprint (a built one's, else a centred square) and the margin on each side (north, east, south, west; dev mode may choose them), clamped to the map.</summary>
        public static CellRect StartingRect(Map map, bool built, int[] margins = null)
        {
            CellRect core = built ? PrisonBuilder.Footprint(map) : CellRect.CenteredOn(map.Center, NewFacilitySide, NewFacilitySide);
            int n = margins?[0] ?? StartMargin, e = margins?[1] ?? StartMargin, s = margins?[2] ?? StartMargin, w = margins?[3] ?? StartMargin;
            return CellRect.FromLimits(core.minX - w, core.minZ - s, core.maxX + e, core.maxZ + n);
        }

        /// <summary>The margins the next new game starts with, set in the New prison dialog in dev mode (null: the usual); read and cleared when the game starts.</summary>
        public static int[] PendingMargins;

        /// <summary>A new game's grounds: <see cref="StartingRect"/>, and the same to go back to.</summary>
        public void SetStarting(bool built)
        {
            int[] margins = PendingMargins;
            PendingMargins = null;
            Set(StartingRect(map, built, margins), asStart: true);
        }

        // ---- changing the grounds ---------------------------------------------------------------------------------------------------------

        /// <summary>Sets the owned land (clamped to <see cref="Whole"/>, never smaller than <see cref="MinSide"/>): <paramref name="asStart"/> makes it the one to go back to as well.</summary>
        public void Set(CellRect r, bool asStart = false)
        {
            CellRect whole = Whole;
            int x0 = Mathf.Clamp(r.minX, whole.minX, whole.maxX), x1 = Mathf.Clamp(r.maxX, whole.minX, whole.maxX);
            int z0 = Mathf.Clamp(r.minZ, whole.minZ, whole.maxZ), z1 = Mathf.Clamp(r.maxZ, whole.minZ, whole.maxZ);
            if (x1 - x0 + 1 < MinSide)
            {
                x1 = Mathf.Min(whole.maxX, x0 + MinSide - 1);
                x0 = x1 - MinSide + 1;
            }
            if (z1 - z0 + 1 < MinSide)
            {
                z1 = Mathf.Min(whole.maxZ, z0 + MinSide - 1);
                z0 = z1 - MinSide + 1;
            }
            (minX, maxX, minZ, maxZ) = (x0, x1, z0, z1);
            if (asStart || !defined) (startMinX, startMaxX, startMinZ, startMaxZ) = (x0, x1, z0, z1);
            defined = true;
            FixLandFactor();
            version++;
        }

        /// <summary>Takes the difficulty's land lever as it is now, once: the price of land stays what the game started with (Options, Change storyteller cannot cheapen it).</summary>
        public void FixLandFactor()
        {
            if (landFactor <= 0f) landFactor = PrisonDifficulty.Current.land;
        }

        /// <summary>The lever the price is scaled by (what the game started with; the current difficulty's until the grounds have fixed it).</summary>
        public float LandFactor => landFactor > 0f ? landFactor : PrisonDifficulty.Current.land;

        /// <summary>Moves one side out (positive) or in (negative) by up to <paramref name="cells"/> cells, as far as the map or <see cref="MinSide"/> allows; the cells it moved. No money: this is buying's last step and dev mode's tool.</summary>
        public int MoveSide(GroundsSide side, int cells)
        {
            if (!defined) Set(Whole, asStart: true);
            CellRect r = Rect, whole = Whole;
            int moved;
            switch (side)
            {
                case GroundsSide.North:
                    moved = Mathf.Clamp(cells, -(r.Height - MinSide), whole.maxZ - r.maxZ);
                    r.maxZ += moved;
                    break;
                case GroundsSide.South:
                    moved = Mathf.Clamp(cells, -(r.Height - MinSide), r.minZ - whole.minZ);
                    r.minZ -= moved;
                    break;
                case GroundsSide.East:
                    moved = Mathf.Clamp(cells, -(r.Width - MinSide), whole.maxX - r.maxX);
                    r.maxX += moved;
                    break;
                default:
                    moved = Mathf.Clamp(cells, -(r.Width - MinSide), r.minX - whole.minX);
                    r.minX -= moved;
                    break;
            }
            if (moved != 0) Set(r);
            return moved;
        }

        /// <summary>Dev mode: all of the map (less the edge margin).</summary>
        public void SetWholeMap()
        {
            if (!defined) Set(Whole, asStart: true);
            else Set(Whole);
        }

        /// <summary>Dev mode: back to the grounds the game started with (a save from before land never had any: the whole map). The plot is the start again, so its count of parcels is zero; what was paid stays paid (the ledger and the statistics are history).</summary>
        public void SetBackToStart()
        {
            if (!defined) Set(Whole, asStart: true);
            else
            {
                Set(Start);
                parcelsBought = 0;
            }
        }

        // ---- buying -----------------------------------------------------------------------------------------------------------------------

        /// <summary>What the state asks for <paramref name="cells"/> cells now: dearer with the land already owned, and by the difficulty the game started on (<see cref="LandFactor"/>). Rounded to ten.</summary>
        public int PriceOf(int cells)
        {
            float perCell = CellPrice * (1f + Area / GrowthCells) * LandFactor;
            return Mathf.Max(10, Mathf.RoundToInt(cells * perCell / 10f) * 10);
        }

        /// <summary>The strip along a side that is for sale (cut short where the map's edge margin is near; empty at it) with its price and what stops it being bought.</summary>
        public Parcel ParcelFor(GroundsSide side)
        {
            if (!defined) return new Parcel(side, CellRect.Empty, 0, "RP_LandWholeMap".Translate());
            CellRect r = Rect, whole = Whole;
            int room = side switch
            {
                GroundsSide.North => whole.maxZ - r.maxZ,
                GroundsSide.South => r.minZ - whole.minZ,
                GroundsSide.East => whole.maxX - r.maxX,
                _ => r.minX - whole.minX,
            };
            int depth = Mathf.Min(ParcelDepth, room);
            if (depth <= 0) return new Parcel(side, CellRect.Empty, 0, "RP_LandEdge".Translate(Margin));
            CellRect strip = side switch
            {
                GroundsSide.North => new CellRect(r.minX, r.maxZ + 1, r.Width, depth),
                GroundsSide.South => new CellRect(r.minX, r.minZ - depth, r.Width, depth),
                GroundsSide.East => new CellRect(r.maxX + 1, r.minZ, depth, r.Height),
                _ => new CellRect(r.minX - depth, r.minZ, depth, r.Height),
            };
            int price = PriceOf(strip.Area);
            return new Parcel(side, strip, price, BlockedFor(price));
        }

        /// <summary>Why a parcel at this price cannot be bought now (null: it can): the measures that are running, the money, or the wages the night's settlement pays (a purchase that left less would put the prison in debt).</summary>
        private string BlockedFor(int price)
        {
            if (SpecialMeasures.Running) return "RP_LandMeasures".Translate();
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return null;
            if (!game.ledger.CanAfford(price)) return "RP_CannotAfford".Translate(Money.Format(price));
            int wages = StaffCareers.Payroll(StaffCommute.AllStaff(map));
            if (game.ledger.funds - price < wages) return "RP_LandWages".Translate(Money.Format(price), Money.Format(wages));
            return null;
        }

        /// <summary>Some side still has a strip for sale (the grounds do not reach the edge all round).</summary>
        public bool AnyForSale
        {
            get
            {
                if (!defined) return false;
                foreach (GroundsSide side in System.Enum.GetValues(typeof(GroundsSide)))
                    if (ParcelFor(side).Exists) return true;
                return false;
            }
        }

        /// <summary>Buys the parcel along a side: spends from the ledger, extends the grounds at once, writes it in the chronicle and counts it. False (and a message) when it cannot be bought.</summary>
        public bool TryBuy(GroundsSide side)
        {
            PrisonGame game = PrisonGame.Instance;
            Parcel p = ParcelFor(side);
            if (game == null || p.blocked != null || !p.Exists)
            {
                if (p.blocked != null) Messages.Message(p.blocked, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            string sideLabel = ("RP_Side_" + side).Translate();
            if (!game.ledger.TrySpend(RPDefOf.RP_Land, p.price, "RP_LandNote".Translate(sideLabel, p.cells.Area)))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(p.price)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            int depth = side is GroundsSide.North or GroundsSide.South ? p.cells.Height : p.cells.Width;
            MoveSide(side, depth);
            parcelsBought++;
            triedOutsideTick = -999999;
            game.stats.landParcels++;
            game.stats.landCells += p.cells.Area;
            TaggedString line = "RP_Chr_LandBought".Translate(sideLabel, p.cells.Area, Money.Format(p.price), Rect.Width, Rect.Height);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, line);
            Messages.Message(line, MessageTypeDefOf.PositiveEvent, historical: false);
            Concepts.Demonstrated(Concepts.Grounds);
            return true;
        }

        // ---- refusing ---------------------------------------------------------------------------------------------------------------------

        public static string OutsideText => "RP_OutsideGrounds".Translate();

        /// <summary>Why a thing of this footprint cannot go here (null: it can).</summary>
        public AcceptanceReport Refusal(CellRect footprint) => Contains(footprint) ? AcceptanceReport.WasAccepted : new AcceptanceReport(OutsideText);

        /// <summary>The warden tried to build or paint outside: a line on the overview for a day, and the learning helper's note.</summary>
        public void Notify_TriedOutside()
        {
            if (!defined) return;
            triedOutsideTick = Find.TickManager.TicksGame;
            Concepts.Teach(Concepts.Grounds);
        }

        public bool TriedOutsideRecently => defined && Find.TickManager.TicksGame - triedOutsideTick < TriedOutsideTicks;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defined, "defined");
            Scribe_Values.Look(ref minX, "minX");
            Scribe_Values.Look(ref minZ, "minZ");
            Scribe_Values.Look(ref maxX, "maxX");
            Scribe_Values.Look(ref maxZ, "maxZ");
            Scribe_Values.Look(ref startMinX, "startMinX");
            Scribe_Values.Look(ref startMinZ, "startMinZ");
            Scribe_Values.Look(ref startMaxX, "startMaxX");
            Scribe_Values.Look(ref startMaxZ, "startMaxZ");
            Scribe_Values.Look(ref parcelsBought, "parcelsBought");
            Scribe_Values.Look(ref landFactor, "landFactor");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // (Grounds saved before the lever was kept price land at the difficulty now in force, from now on for good.)
                if (defined) FixLandFactor();
                version++;
            }
        }

        /// <summary>One line of the grounds, for the saveload snapshot.</summary>
        public string Describe() => $"{defined}:{minX},{minZ}-{maxX},{maxZ}:{startMinX},{startMinZ}-{startMaxX},{startMaxZ}:{parcelsBought}:{landFactor:F2}";
    }

    /// <summary>
    /// The refusals: a blueprint (a building, a floor, a reinstalled thing, the shuttle pad with its ramp) or a zone outside the grounds is not allowed; god mode is exempt (it builds anywhere, as it
    /// builds in the edge area). Only the warden's own placements are refused: what a build designator or an install asks while it is asked (<see cref="Patch_Designator_Place_CanDesignateCell_Grounds"/>),
    /// not a raider's siege, a mech cluster, a prefab, a pod launcher or a trap put back (they ask the same placement test, and are not the prison's to keep off the land). The designators ask on every
    /// frame the cursor moves, so these only look; the warden's refused click is told to the grounds by <see cref="Patch_Designator_FinalizeDesignationFailed"/>.
    /// </summary>
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    public static class Patch_GenConstruct_CanPlaceBlueprintAt_Grounds
    {
        /// <summary>A build or install designator is asking (nested asks count).</summary>
        public static int AskedByDesignator;

        public static void Postfix(BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, ref AcceptanceReport __result)
        {
            // (DebugSettings.godMode, not the caller's flag: Designator_Install always passes false.)
            if (AskedByDesignator <= 0 || !__result.Accepted || DebugSettings.godMode || PrisonGame.Instance == null) return;
            PrisonGrounds grounds = PrisonGrounds.Of(map);
            if (grounds is not { Limited: true }) return;
            AcceptanceReport refusal = grounds.Refusal(GenAdj.OccupiedRect(center, rot, entDef.Size));
            // (The pad is where the shuttle sets down: its hull and its ramp must be inside, not only the pad's own cell.)
            if (refusal.Accepted && entDef == RPShuttleDefOf.RP_ShuttlePad)
            {
                IntVec3 ramp = ShuttleSystem.Footprint(center, out CellRect hull);
                if (!grounds.Contains(hull) || !grounds.Contains(ramp)) refusal = new AcceptanceReport(PrisonGrounds.OutsideText);
            }
            if (!refusal.Accepted) __result = refusal;
        }
    }

    /// <summary>The warden's own placements: the architect's build tools (walls, floors, furniture, copy, replace) and installing or reinstalling. Marks the placement test asked from them (see the refusal above).</summary>
    [HarmonyPatch]
    public static class Patch_Designator_Place_CanDesignateCell_Grounds
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Designator_Build), nameof(Designator_Build.CanDesignateCell));
            yield return AccessTools.Method(typeof(Designator_Install), nameof(Designator_Install.CanDesignateCell));
        }

        public static void Prefix() => Patch_GenConstruct_CanPlaceBlueprintAt_Grounds.AskedByDesignator++;

        // (A finalizer: a designator that throws must not leave every later placement refused.)
        public static void Finalizer() => Patch_GenConstruct_CanPlaceBlueprintAt_Grounds.AskedByDesignator--;
    }

    /// <summary>
    /// A game being started or loaded begins with no dev margins pending: <see cref="PrisonGrounds.PendingMargins"/> is set by the New prison dialog after it makes its game, so margins left by a start that
    /// never reached <c>PostGameStart</c> cannot leak into the next game (the vanilla flow's, a loaded one) and be used there.
    /// </summary>
    [HarmonyPatch(typeof(Game), MethodType.Constructor, new System.Type[0])]
    public static class Patch_Game_Constructor_ClearPendingMargins
    {
        public static void Postfix() => PrisonGrounds.PendingMargins = null;
    }

    /// <summary>Vanilla's growing zones, stockpiles and fishing zones: every one asks <see cref="Designator_ZoneAdd.IsZoneableCell"/>.</summary>
    [HarmonyPatch(typeof(Designator_ZoneAdd), nameof(Designator_ZoneAdd.IsZoneableCell))]
    public static class Patch_Designator_ZoneAdd_IsZoneableCell_Grounds
    {
        public static void Postfix(IntVec3 c, Map map, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || PrisonGame.Instance == null || DebugSettings.godMode) return;
            if (PrisonGrounds.Of(map) is { Limited: true } grounds && !grounds.Contains(c)) __result = new AcceptanceReport(PrisonGrounds.OutsideText);
        }
    }

    /// <summary>A designation the warden dragged or clicked came to nothing because the land is not theirs: the overview's list says so for a day, and the learning helper explains.</summary>
    [HarmonyPatch(typeof(Designator), "FinalizeDesignationFailed")]
    public static class Patch_Designator_FinalizeDesignationFailed
    {
        /// <summary>For tests: the cell the mouse is on (the game's own cell is wherever the headless cursor is).</summary>
        public static IntVec3? MouseCellForTests;

        public static void Postfix(Designator __instance)
        {
            if (PrisonGame.Instance == null || !RefusedForGrounds(__instance, MouseCellForTests ?? UI.MouseCell())) return;
            PrisonGrounds.Of(Find.CurrentMap)?.Notify_TriedOutside();
        }

        /// <summary>
        /// The failed designation was refused because the cell is outside the grounds. A dragged one (walls, floors, zones) has it as the dragger's reason; a designator with no draw style (a bed,
        /// the pad, an install) is clicked once and vanilla only shows the reason of the cell, so the cell under the mouse is asked again.
        /// </summary>
        public static bool RefusedForGrounds(Designator designator, IntVec3 mouse)
        {
            DesignatorManager manager = Find.DesignatorManager;
            if (manager?.Dragger?.FailureReason == PrisonGrounds.OutsideText) return true;
            return manager is { SelectedStyle: null } && mouse.InBounds(Find.CurrentMap) && designator.CanDesignateCell(mouse).Reason == PrisonGrounds.OutsideText;
        }
    }
}
