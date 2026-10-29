using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Prison Architect-style bottom toolbar: each prison button opens a focused window (the office's tabs, split by
    /// topic) and shows an icon next to its label. Icons: Textures/UI/MainButtons/{defName}.
    /// </summary>
    public class MainButtonWorker_PrisonTab : MainButtonWorker_ToggleTab
    {
        private Texture2D icon;
        private bool iconLooked;

        public override void DoButton(Rect rect)
        {
            if (!iconLooked)
            {
                iconLooked = true;
                icon = ContentFinder<Texture2D>.Get("UI/MainButtons/" + def.defName, reportFailure: false);
            }
            Text.Font = GameFont.Small;
            if (Widgets.ButtonTextSubtle(rect, "", ButtonBarPercent, -1f, SoundDefOf.Mouseover_Category)) InterfaceTryActivate();
            // (The learning helper's notes point at the screen they are about.)
            if (Find.MainTabsRoot.OpenTab != def) UIHighlighter.HighlightOpportunity(rect, def.cachedHighlightTagClosed);
            float iconSize = Mathf.Min(22f, rect.height - 8f);
            float labelW = Text.CalcSize(def.LabelCap).x;
            bool fits = labelW + iconSize + 10f <= rect.width;
            float x = rect.center.x - ((fits ? labelW + iconSize + 6f : iconSize) / 2f);
            if (Mouse.IsOver(rect)) x += 1f;
            if (icon != null) GUI.DrawTexture(new Rect(x, rect.center.y - iconSize / 2f, iconSize, iconSize), icon);
            if (fits)
            {
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x + iconSize + 6f, rect.y, labelW + 4f, rect.height), def.LabelCap);
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (Mouse.IsOver(rect) && !def.description.NullOrEmpty())
                TooltipHandler.TipRegion(rect, def.LabelCap.Colorize(ColorLibrary.Yellow) + "\n\n" + def.description);
        }
    }

    public class MainTabWindow_Reports : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Overview, Tab.Chronicle, Tab.Statistics, Tab.Inmates, Tab.Finance };
    }

    public class MainTabWindow_Regime : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Regime };
        // Short, so the prison stays visible while the timetable changes.
        public override Vector2 RequestedTabSize => new Vector2(1010f, 440f);
    }

    public class MainTabWindow_Policy : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Intake, Tab.Policy, Tab.Programs };
    }

    public class MainTabWindow_Staff : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Staff };
    }

    public class MainTabWindow_Logistics : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Supply };
    }

    public class MainTabWindow_Deployment : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Deployment };
    }

    public class MainTabWindow_Intel : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Intel };
    }

    public class MainTabWindow_Office : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Grants, Tab.Bureaucracy, Tab.Land };
    }

    /// <summary>Rooms: a palette of zones to paint, like PA's Rooms menu; stays at the left while painting.</summary>
    public class MainTabWindow_Rooms : MainTabWindow_Prison
    {
        protected override Tab[] Tabs => new[] { Tab.Rooms };
        public override Vector2 RequestedTabSize => new Vector2(440f, 640f);
        public override MainTabWindowAnchor Anchor => MainTabWindowAnchor.Left;
    }

    /// <summary>Zone icons (white masks tinted with the zone colour); the stockpile icon when a zone has none.</summary>
    [StaticConstructorOnStartup]
    public static class ZoneIcons
    {
        private static readonly Dictionary<PrisonZoneDef, Texture2D> cache = new Dictionary<PrisonZoneDef, Texture2D>();

        public static Texture2D For(PrisonZoneDef zone)
        {
            if (zone == null) return ContentFinder<Texture2D>.Get("UI/Designators/ZoneDelete");
            if (!cache.TryGetValue(zone, out Texture2D tex))
                cache[zone] = tex = ContentFinder<Texture2D>.Get("UI/Zones/" + zone.defName, reportFailure: false)
                                    ?? ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Stockpile");
            return tex;
        }
    }

    /// <summary>The Rooms palette: every room zone and wing with its icon, size, and what it still needs.</summary>
    public static class RoomsTab
    {
        private const float RowH = 44f;

        public static void Draw(Rect rect, Map map, ref Vector2 scroll)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            var top = new Rect(rect.x, rect.y, rect.width, 28f);
            Widgets.CheckboxLabeled(top, "RP_RoomsShowZones".Translate(), ref RimPrisonMod.Settings.showZoneOverlay);
            List<Designator_PrisonZone> designators = RPDefOf.RP_PrisonZones.AllResolvedDesignators.OfType<Designator_PrisonZone>().ToList();
            var rooms = designators.Where(d => d.zone != null && d.zone.security == null).ToList();
            var wings = designators.Where(d => d.zone != null && d.zone.security != null).ToList();
            Designator_PrisonZone eraser = designators.FirstOrDefault(d => d.zone == null);
            var outer = new Rect(rect.x, rect.y + 32f, rect.width, rect.height - 32f);
            List<Cellmates.CellInfo> cells = Cellmates.Cells(map);
            HashSet<SecurityLevelDef> living = KeptRooms.LevelsInPrison(map);
            float height = (rooms.Count + wings.Count + 1 + cells.Count) * RowH + 3 * 30f + rooms.Sum(d => PatchRowsHeight(pm, d.zone, living));
            var view = new Rect(0f, 0f, outer.width - 16f, height);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            Header(ref y, view.width, "RP_RoomsRooms");
            foreach (Designator_PrisonZone d in rooms)
            {
                Row(ref y, view.width, d, pm, map);
                PatchRows(ref y, view.width, d.zone, pm, map, living);
            }
            Header(ref y, view.width, "RP_RoomsWings");
            foreach (Designator_PrisonZone d in wings) Row(ref y, view.width, d, pm, map);
            if (eraser != null) Row(ref y, view.width, eraser, pm, map);
            if (cells.Count > 0) Header(ref y, view.width, "RP_RoomsCellBlock");
            // (Only the rows in view are drawn; the cells themselves are worked out once a second, not per event.)
            foreach (Cellmates.CellInfo cell in cells)
            {
                if (y + RowH < scroll.y || y > scroll.y + outer.height) y += RowH;
                else CellRow(ref y, view.width, cell, map);
            }
            Widgets.EndScrollView();
        }

        /// <summary>One cell: its number, wing and free beds, who lives there and how they get on (the worst of them).</summary>
        private static void CellRow(ref float y, float width, Cellmates.CellInfo cell, Map map)
        {
            var row = new Rect(0f, y, width, RowH - 4f);
            y += RowH;
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            Cellmates.CellFit fit = cell.worst;
            Widgets.DrawBoxSolid(new Rect(row.x + 4f, row.y + 4f, 6f, row.height - 8f), cell.occupants.Count == 0 ? PrisonHUD.Muted : fit.ColorOf());
            Widgets.Label(new Rect(row.x + 16f, row.y + 2f, 218f, 22f), "RP_RoomsCellRow".Translate(cell.number));
            // (The grade, in the corner of the first line: what the cell is worth to inmates who have earned it.)
            if (cell.grade >= 0)
            {
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(row.x + 16f, row.y + 4f, 212f, 18f), "RP_RoomsCellGrade".Translate(cell.grade));
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(row.x + 16f, row.y + 21f, 218f, 18f), "RP_RoomsCellSub".Translate(cell.wing?.label ?? "RP_MoveCellNoWing".Translate().ToString(), cell.Free, cell.slots));
            Text.Anchor = TextAnchor.MiddleRight;
            var who = new Rect(row.x + 236f, row.y + 2f, row.width - 240f, row.height - 4f);
            GUI.color = cell.occupants.Count == 0 ? PrisonHUD.Muted : fit.ColorOf();
            // (One line, the verdict kept: a dormitory's list of names would wrap over the rows above; the tooltip names everyone.)
            string verdict = " - " + fit.Key().Translate();
            string names = cell.occupants.Count == 0 ? "RP_RoomsCellEmpty".Translate().ToString() : string.Join(", ", cell.occupants.Select(o => o.LabelShort)).Truncate(who.width - Text.CalcSize(verdict).x) + verdict;
            Widgets.Label(who, names);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            // Each occupant with how they get on with the others (their own bed's verdict) and the hourly tension it brings
            // (worked out with the cell, and the text only built while the mouse is over the row).
            TooltipHandler.TipRegion(row, () => (cell.grade >= 0 ? "RP_RoomsCellGradeTip".Translate(cell.grade, CellQuality.MaxGrade).ToString() + "\n\n" : "") + (cell.rows.Count == 0 ? "RP_RoomsCellTip".Translate().ToString()
                : string.Join("\n", cell.rows.Select(o => $"{o.pawn.LabelShort} - {o.fit.Key().Translate()} ({o.knownTension:+0.#;-0.#;0}/h)")) + "\n\n" + "RP_RoomsCellTip".Translate()), 8_410_000 + cell.number);
            if (Widgets.ButtonInvisible(row) && cell.beds.Count > 0) CameraJumper.TryJump(cell.beds[0].Position, map);
        }

        private const float PatchRowH = 26f, PatchNoteH = 16f;

        /// <summary>The patches of a kind inmates share, which can each be kept for some levels (none for any other zone).</summary>
        private static IEnumerable<int> KeepablePatches(PrisonMap pm, PrisonZoneDef zone)
        {
            if (zone == null || !zone.keepable || !Unlocks.IsUnlocked(zone)) yield break;
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].zone == zone) yield return i;
        }

        private static float PatchRowsHeight(PrisonMap pm, PrisonZoneDef zone, HashSet<SecurityLevelDef> living)
        {
            float h = 0f;
            foreach (int i in KeepablePatches(pm, zone)) h += PatchRowH + (KeptRooms.NobodyMayUse(living, pm.zones.KeptFor(i)) ? PatchNoteH : 0f);
            return h;
        }

        /// <summary>One line per patch of the zone: its name (click to look at it) and who it is kept for, with a menu of levels; a mild note when none of those has an inmate living in the prison.</summary>
        private static void PatchRows(ref float y, float width, PrisonZoneDef zone, PrisonMap pm, Map map, HashSet<SecurityLevelDef> living)
        {
            foreach (int i in KeepablePatches(pm, zone).ToList())
            {
                ZonePatch patch = pm.zones.Patches[i];
                IReadOnlyList<SecurityLevelDef> keptFor = pm.zones.KeptFor(i);
                var row = new Rect(24f, y, width - 24f, PatchRowH - 2f);
                y += PatchRowH;
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                var name = new Rect(row.x, row.y + 4f, 120f, 18f);
                Widgets.Label(name, GangTurf.PlaceName(pm.zones, i).CapitalizeFirst().Truncate(name.width));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(name, "RP_KeptJumpTip".Translate());
                if (Widgets.ButtonInvisible(name)) CameraJumper.TryJump(new IntVec3((int)patch.center.x, 0, (int)patch.center.z), map);
                var button = new Rect(row.x + 126f, row.y, row.width - 126f, row.height);
                int anchor = patch.anchor;
                if (Widgets.ButtonText(button, KeptRooms.ForLabel(keptFor).Truncate(button.width - 12f))) OpenKeptMenu(pm, anchor, map);
                TooltipHandler.TipRegion(button, "RP_KeptTip".Translate());
                if (!KeptRooms.NobodyMayUse(living, keptFor)) continue;
                GUI.color = PrisonHUD.Warn;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 126f, y - 2f, row.width - 126f, PatchNoteH), "RP_KeptNobody".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += PatchNoteH;
            }
        }

        /// <summary>The menu of levels for a patch (named by its first cell: a repaint may have renumbered it): "all" clears, a level toggles. It opens again after each pick, so several can be set in a row.</summary>
        private static void OpenKeptMenu(PrisonMap pm, int anchor, Map map)
        {
            int patch = pm.zones.PatchAt(anchor);
            if (patch < 0) return;
            IReadOnlyList<SecurityLevelDef> keptFor = pm.zones.KeptFor(patch);
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption((keptFor == null ? "✓ " : "") + "RP_KeptAll".Translate(), () =>
                {
                    int now = pm.zones.PatchAt(anchor);
                    if (now >= 0) pm.zones.SetKept(now, null);
                })
            };
            foreach (SecurityLevelDef level in KeptRooms.MenuLevels(keptFor))
            {
                SecurityLevelDef chosen = level;
                bool on = keptFor != null && keptFor.Contains(level);
                options.Add(new FloatMenuOption((on ? "✓ " : "") + level.LabelCap, () =>
                {
                    int now = pm.zones.PatchAt(anchor);
                    if (now < 0) return;
                    var set = new List<SecurityLevelDef>(pm.zones.KeptFor(now) ?? new List<SecurityLevelDef>());
                    if (!set.Remove(chosen)) set.Add(chosen);
                    pm.zones.SetKept(now, set);
                    OpenKeptMenu(pm, anchor, map);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void Header(ref float y, float width, string key)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, width, 30f), key.Translate());
            Text.Font = GameFont.Small;
            y += 30f;
        }

        private static void Row(ref float y, float width, Designator_PrisonZone d, PrisonMap pm, Map map)
        {
            var row = new Rect(0f, y, width, RowH - 4f);
            y += RowH;
            PrisonZoneDef z = d.zone;
            bool locked = z != null && !Unlocks.IsUnlocked(z);
            bool selected = Find.DesignatorManager.SelectedDesignator == d;
            if (selected) Widgets.DrawBox(row, 2);
            else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            var iconRect = new Rect(row.x + 4f, row.y + 4f, 32f, 32f);
            if (z != null) Widgets.DrawBoxSolid(iconRect, new Color(z.color.r * 0.35f, z.color.g * 0.35f, z.color.b * 0.35f));
            GUI.color = locked ? Color.gray : z?.color ?? Color.white;
            GUI.DrawTexture(iconRect.ContractedBy(3f), ZoneIcons.For(z));
            GUI.color = locked ? Color.gray : Color.white;
            Widgets.Label(new Rect(row.x + 44f, row.y + 2f, 190f, 22f), z?.LabelCap ?? "RP_ClearZone".Translate());
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            // (Who holds it, if the warden knows: a hidden gang's turf is not told.)
            string turf = z == null ? null : GangTurf.ZoneLine(map, z);
            if (z != null)
            {
                string cells = "RP_RoomsCells".Translate(pm.zones.CellsOf(z).Count);
                Widgets.Label(new Rect(row.x + 44f, row.y + 21f, 190f, 18f), (turf == null ? cells : cells + " - " + turf).Truncate(190f));
            }
            Text.Font = GameFont.Small;
            // Status: locked (bureaucracy), missing furniture, or ready.
            var status = new Rect(row.x + 236f, row.y + 2f, row.width - 240f, row.height - 4f);
            if (z != null)
            {
                Text.Anchor = TextAnchor.MiddleRight;
                Text.Font = GameFont.Tiny;
                if (locked)
                {
                    GUI.color = PrisonHUD.Muted;
                    Widgets.Label(status, "RP_RoomsLocked".Translate(Unlocks.GateOf(z)?.label ?? "?"));
                }
                else if (pm.zones.CellsOf(z).Count > 0)
                {
                    ThingDef missing = z.requiredThings.FirstOrDefault(t => !map.listerBuildings.AllBuildingsColonistOfDef(t).Any(b => pm.zones.ZoneAt(b.Position) == z));
                    GUI.color = missing == null ? PrisonHUD.Good : PrisonHUD.Warn;
                    Widgets.Label(status, missing == null ? "RP_RoomsReady".Translate() : "RP_RoomsNeeds".Translate(missing.label));
                }
                else
                {
                    // Not painted: an essential room says so in colour (the prison does not run without it), an optional one only in grey.
                    bool essential = PrisonDiagnostics.IsEssential(z);
                    GUI.color = essential ? PrisonHUD.Warn : PrisonHUD.Muted;
                    Widgets.Label(status, (essential ? "RP_RoomsNotPaintedEssential" : "RP_RoomsNotPainted").Translate());
                }
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            GUI.color = Color.white;
            TooltipHandler.TipRegion(row, turf == null ? d.Desc : d.Desc + "\n\n" + turf);
            if (!locked && Widgets.ButtonInvisible(row)) Find.DesignatorManager.Select(d);
        }
    }
}
