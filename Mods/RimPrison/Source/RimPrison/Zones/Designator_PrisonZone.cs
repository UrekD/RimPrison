using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Paints one prison zone type (or clears zones when <see cref="zone"/> is null).</summary>
    public class Designator_PrisonZone : Designator_Cells
    {
        public readonly PrisonZoneDef zone;

        public override bool DragDrawMeasurements => true;

        public override bool Visible => zone == null || Unlocks.IsUnlocked(zone);
        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Areas;

        public Designator_PrisonZone(PrisonZoneDef zone)
        {
            this.zone = zone;
            defaultLabel = zone != null ? zone.LabelCap.ToString() : "RP_ClearZone".Translate().ToString();
            defaultDesc = zone != null ? zone.description : "RP_ClearZoneDesc".Translate().ToString();
            icon = ZoneIcons.For(zone);
            if (zone != null) defaultIconColor = zone.color;
            soundDragSustain = SoundDefOf.Designate_DragAreaAdd;
            soundDragChanged = SoundDefOf.Designate_DragZone_Changed;
            soundSucceeded = SoundDefOf.Designate_ZoneAdd;
            useMouseIcon = true;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map) || c.Fogged(Map)) return false;
            // (Painting a room is for the prison's own land; taking a room's paint off is always allowed.)
            if (zone != null && !DebugSettings.godMode && PrisonGrounds.Of(Map) is { Limited: true } grounds && !grounds.Contains(c)) return new AcceptanceReport(PrisonGrounds.OutsideText);
            PrisonZoneDef cur = PrisonMap.Of(Map)?.zones.ZoneAt(c);
            return cur != zone;
        }

        public override void DesignateSingleCell(IntVec3 c) => PrisonMap.Of(Map)?.zones.Set(c, zone);

        public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
        {
            base.DesignateMultiCell(cells);
            PrisonMap.Of(Map)?.Notify_DoorsChanged();
            // (A kitchen painted under stoves already built: they cook from now on.)
            if (zone == RPLaborDefOf.RP_Kitchen) KitchenBills.Ensure(Map);
            Concepts.Painted(zone);
        }

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
            PrisonMap.Of(Map)?.zones.MarkForDraw(strong: true);
        }
    }

    /// <summary>One designator per zone def in the Rim Prison architect category.</summary>
    [HarmonyPatch(typeof(DesignationCategoryDef), "ResolveDesignators")]
    public static class Patch_DesignationCategoryDef_ResolveDesignators
    {
        public static void Postfix(DesignationCategoryDef __instance, List<Designator> ___resolvedDesignators)
        {
            if (__instance == RPDefOf.RP_PrisonZones) AddTo(___resolvedDesignators);
        }

        /// <summary>Startup may run after the category resolved its designators; add them then.</summary>
        public static void EnsureAdded()
        {
            DesignationCategoryDef cat = RPDefOf.RP_PrisonZones;
            if (!cat.AllResolvedDesignators.Any(d => d is Designator_PrisonZone)) AddTo(cat.AllResolvedDesignators);
        }

        private static void AddTo(List<Designator> ___resolvedDesignators)
        {
            var defs = new List<PrisonZoneDef>(DefDatabase<PrisonZoneDef>.AllDefsListForReading);
            defs.SortBy(d => d.uiOrder);
            foreach (PrisonZoneDef d in defs) ___resolvedDesignators.Add(new Designator_PrisonZone(d));
            ___resolvedDesignators.Add(new Designator_PrisonZone(null));
        }
    }
}
