using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The grounds on the map: the border (the outermost ring of owned cells, in yellow), the land outside dimmed while something is being built or painted, and the parcel the warden points at in the
    /// Land section. The border and the dimming are <see cref="CellBoolDrawer"/>s, whose mesh is built once and again only when the grounds change; the parcel is the outline of its cells
    /// (<see cref="GenDraw.DrawFieldEdges"/> of a list kept while the same one is pointed at: pointing at another does not make a pass over the map). Nothing else is worked out per frame. The toggle hides the border only: the dimming is feedback for the tool in hand.
    /// </summary>
    public class GroundsOverlay
    {
        public static bool Show = true;

        private static readonly Color BorderColor = new Color(1f, 0.88f, 0.2f), DimColor = new Color(0.03f, 0.03f, 0.05f), PreviewColor = new Color(0.4f, 0.9f, 0.45f);

        private readonly Map map;
        private CellBoolDrawer border, dim;
        private PrisonGrounds current;
        private int builtVersion = -1;
        private readonly List<IntVec3> previewCells = new List<IntVec3>();
        private CellRect shownPreview;

        private static CellRect preview;
        private static int previewFrame = -99;

        public GroundsOverlay(Map map) => this.map = map;

        /// <summary>The parcel the warden points at (drawn while it keeps being asked for); <paramref name="pin"/> keeps it drawn until <see cref="ClearPreview"/> (the UI tour's pictures).</summary>
        public static void ShowPreview(CellRect cells, bool pin = false)
        {
            preview = cells;
            previewFrame = pin ? int.MaxValue : Time.frameCount;
        }

        public static void ClearPreview() => previewFrame = -99;

        /// <summary>Whether the land outside is dimmed now: the Architect or the Rooms screen is open, or a building or zone tool is in hand.</summary>
        public static bool Dimming()
        {
            MainButtonDef open = Find.MainTabsRoot?.OpenTab;
            if (open == MainButtonDefOf.Architect || open?.tabWindowClass == typeof(MainTabWindow_Rooms)) return true;
            Designator tool = Find.DesignatorManager?.SelectedDesignator;
            return tool is Designator_Place || tool is Designator_Zone || tool is Designator_PrisonZone;
        }

        public void Update(PrisonGrounds g)
        {
            if (Time.frameCount - previewFrame <= 1 && preview.Area > 0)
            {
                if (shownPreview != preview)
                {
                    shownPreview = preview;
                    previewCells.Clear();
                    previewCells.AddRange(preview.Cells);
                }
                GenDraw.DrawFieldEdges(previewCells, PreviewColor);
            }
            bool showBorder = ShowsBorder(g), showDim = ShowsDim(g);
            if (!showBorder && !showDim) return;
            if (current != g || builtVersion != g.version)
            {
                current = g;
                builtVersion = g.version;
                border?.SetDirty();
                dim?.SetDirty();
            }
            if (showBorder)
            {
                border ??= new CellBoolDrawer(i => OnBorder(current, map.cellIndices.IndexToCell(i)), () => BorderColor, i => BorderColor, map.Size.x, map.Size.z, 3613, 0.8f);
                border.MarkForDraw();
                border.CellBoolDrawerUpdate();
            }
            if (showDim)
            {
                dim ??= new CellBoolDrawer(i => !current.Contains(map.cellIndices.IndexToCell(i)), () => DimColor, i => DimColor, map.Size.x, map.Size.z, 3611, 0.4f);
                dim.MarkForDraw();
                dim.CellBoolDrawerUpdate();
            }
        }

        /// <summary>The yellow line is drawn: the grounds are limited and the warden has not turned the line off.</summary>
        public static bool ShowsBorder(PrisonGrounds g) => Show && g is { Limited: true };

        /// <summary>The land outside is dimmed: the grounds are limited and a building or zone tool is in hand (whatever the line's toggle says).</summary>
        public static bool ShowsDim(PrisonGrounds g) => g is { Limited: true } && Dimming();

        private static bool OnBorder(PrisonGrounds g, IntVec3 c) =>
            g.Contains(c) && (c.x == g.minX || c.x == g.maxX || c.z == g.minZ || c.z == g.maxZ);
    }
}
