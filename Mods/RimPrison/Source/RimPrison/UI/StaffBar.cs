using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>The groups the prison's colonist bar is split into, in the order they are drawn; a role says which it is in (<see cref="StaffRoleDef.barGroup"/>).</summary>
    public enum StaffBarGroup { Warden, Security, Medical, Programs, Upkeep, Other }

    /// <summary>A label under a staff group of the bar: its category, how many staff it holds on the map, and whether the player has collapsed it.</summary>
    public sealed class StaffBarLabel
    {
        public StaffBarGroup category;
        /// <summary>The group's index on the bar (<see cref="ColonistBar.Entry.group"/>) and the range of its entries (first to last, inclusive).</summary>
        public int group, first, last;
        /// <summary>Staff of the category on the map, whether the bar shows them or not.</summary>
        public int count;
        public bool collapsed;
        public string text;
        /// <summary>The text without the count, for a group too narrow for the whole of it (a collapsed group of one portrait).</summary>
        public string brief;
        public Rect rect;
        /// <summary>Where the label goes when weapon icons are drawn under the group's last row (vanilla's <see cref="Prefs.ShowWeaponsUnderPortraitMode"/>): below them.</summary>
        public float belowWeaponsY;
        public string tip;
        /// <summary>The text as drawn and where, found at the first draw after the bar was rebuilt: a group of one portrait is narrower than its text, so the label takes room on either side up to
        /// its neighbours' text, and what still does not fit is cut.</summary>
        public string shown;
        public Rect drawn;
    }

    /// <summary>
    /// The colonist bar of a prison with forty staff: one group a role (the warden, security, medical, programs and office, upkeep, the rest), each a group of vanilla's bar so that vanilla
    /// draws the gaps and the frames, and a compact bar with only the warden, the pinned staff and whoever needs the player now. Vanilla builds its entries in hire order and one group a map
    /// (<c>ColonistBar.CheckRecacheEntries</c>); the patch below rewrites the home map's after it has, and lays the bar out again.
    /// </summary>
    public static class StaffBar
    {
        private static readonly AccessTools.FieldRef<ColonistBar, bool> EntriesDirty = AccessTools.FieldRefAccess<ColonistBar, bool>("entriesDirty");
        private static readonly AccessTools.FieldRef<ColonistBar, List<ColonistBar.Entry>> CachedEntries = AccessTools.FieldRefAccess<ColonistBar, List<ColonistBar.Entry>>("cachedEntries");
        private static readonly AccessTools.FieldRef<ColonistBar, List<int>> CachedReorderableGroups = AccessTools.FieldRefAccess<ColonistBar, List<int>>("cachedReorderableGroups");
        private static readonly AccessTools.FieldRef<ColonistBar, List<UnityEngine.Vector2>> CachedDrawLocs = AccessTools.FieldRefAccess<ColonistBar, List<UnityEngine.Vector2>>("cachedDrawLocs");
        private static readonly AccessTools.FieldRef<ColonistBar, float> CachedScale = AccessTools.FieldRefAccess<ColonistBar, float>("cachedScale");
        private static readonly AccessTools.FieldRef<ColonistBar, ColonistBarDrawLocsFinder> DrawLocsFinder = AccessTools.FieldRefAccess<ColonistBar, ColonistBarDrawLocsFinder>("drawLocsFinder");

        /// <summary>Who needed the player's attention at the last look, by id (the compact bar is rebuilt when the set changes, not every frame; a sum of ids let two sets look alike).</summary>
        private static readonly HashSet<int> attention = new HashSet<int>(), attentionNow = new HashSet<int>();

        private static readonly Func<ColonistBar, bool> BarVisible = AccessTools.MethodDelegate<Func<ColonistBar, bool>>(AccessTools.PropertyGetter(typeof(ColonistBar), "Visible"));

            /// <summary>The labels under the home map's groups, made when the bar is rebuilt (<see cref="Regroup"/>) and read by the draw after vanilla's bar.</summary>
        private static readonly List<StaffBarLabel> labels = new List<StaffBarLabel>();

        public static IReadOnlyList<StaffBarLabel> Labels => labels;

        /// <summary>A collapsed group's label, set apart from the open ones.</summary>
        private static readonly Color CollapsedColor = new Color(0.6f, 0.8f, 1f);

        public static void ClearLabels() => labels.Clear();

        /// <summary>A prison game that is being played (the bar of any other game is vanilla's).</summary>
        public static bool Active => PrisonGame.Instance != null && Current.ProgramState == ProgramState.Playing && Find.UIRoot is UIRoot_Play;

        public static bool Grouping => RimPrisonMod.Settings?.groupColonistBar ?? false;

        public static StaffBarGroup GroupOf(Pawn p) => p?.GetComp<CompStaff>()?.role?.barGroup ?? StaffBarGroup.Other;

        public static bool IsPinned(Pawn p) => PrisonGame.Instance?.staffBarPins.Contains(p) ?? false;

        /// <summary>The bar is rebuilt on its next look.</summary>
        public static void MarkDirty()
        {
            if (Current.ProgramState == ProgramState.Playing && Find.UIRoot is UIRoot_Play) Find.ColonistBar?.MarkColonistsDirty();
        }

        public static void SetCompact(PrisonGame game, bool on)
        {
            game.staffBarCompact = on;
            Attention(Find.AnyPlayerHomeMap, attention);
            MarkDirty();
        }

        public static void SetPinned(PrisonGame game, Pawn p, bool pinned)
        {
            if (pinned == game.staffBarPins.Contains(p)) return;
            if (pinned) game.staffBarPins.Add(p);
            else game.staffBarPins.Remove(p);
            MarkDirty();
        }

        public static bool IsCollapsed(PrisonGame game, StaffBarGroup category) => CanCollapse(category) && game.staffBarCollapsed.Contains(category);

        /// <summary>The warden's group is one person who is always on the bar: there is nothing to collapse.</summary>
        public static bool CanCollapse(StaffBarGroup category) => category != StaffBarGroup.Warden;

        public static void SetCollapsed(PrisonGame game, StaffBarGroup category, bool collapsed)
        {
            if (!CanCollapse(category) || collapsed == game.staffBarCollapsed.Contains(category)) return;
            if (collapsed) game.staffBarCollapsed.Add(category);
            else game.staffBarCollapsed.RemoveAll(c => c == category);
            MarkDirty();
        }

        /// <summary>Down, out of their mind, burning, bleeding or in want of tending, or held hostage: what the compact bar shows without a pin.</summary>
        public static bool NeedsAttention(Pawn p) =>
            p.health != null && !p.Dead && (p.Downed || p.InMentalState || p.IsBurning() || HostageCrisis.IsHeld(p) || p.health.hediffSet.BleedRateTotal > 0.001f || HealthAIUtility.ShouldBeTendedNowByPlayer(p));

        /// <summary>Whether the compact bar shows them: the warden and the state's appointees (who take no pin), the pinned, and whoever needs the player now.</summary>
        public static bool ShownOnCompactBar(PrisonGame game, Pawn p) => StaffUtility.IsProtected(p) || game.staffBarPins.Contains(p) || NeedsAttention(p);

        /// <summary>The ids of the map's colonists who need the player now (the ones the bar is built from: spawned or carried, as vanilla's bar takes them).</summary>
        private static void Attention(Map map, HashSet<int> into)
        {
            into.Clear();
            if (map == null) return;
            foreach (Pawn p in map.mapPawns.FreeColonists)
                if (NeedsAttention(p)) into.Add(p.thingIDNumber);
        }

        /// <summary>Every 250 ticks: pins of staff who are gone are dropped, and a compact bar is rebuilt when someone has come to need the player (or stopped).</summary>
        public static void Rare(PrisonGame game, Map home)
        {
            if (game.staffBarPins.RemoveAll(p => p == null || p.Destroyed || p.Dead || p.GetComp<CompStaff>()?.role == null) > 0) MarkDirty();
            if (!game.staffBarCompact) return;
            Attention(home, attentionNow);
            if (attentionNow.SetEquals(attention)) return;
            attention.Clear();
            attention.UnionWith(attentionNow);
            MarkDirty();
        }

        public static Command_Action PinGizmo(Pawn p)
        {
            bool pinned = IsPinned(p);
            return new Command_Action
            {
                defaultLabel = (pinned ? "RP_UnpinStaff" : "RP_PinStaff").Translate(),
                defaultDesc = (pinned ? "RP_UnpinStaffDesc" : "RP_PinStaffDesc").Translate(),
                icon = pinned ? TexCommand.ForbidOff : TexCommand.ForbidOn,
                action = () => SetPinned(PrisonGame.Instance, p, !pinned),
            };
        }

        /// <summary>
        /// Rewrites the entries vanilla has just built: the home map's staff are filtered for the compact bar and put in a group a category (in vanilla's order within it, so the drag to reorder works inside a
        /// group), the groups after it are numbered on, and the bar's reorder groups and draw places are made again for the new count of groups.
        /// </summary>
        public static void Regroup(ColonistBar bar)
        {
            PrisonGame game = PrisonGame.Instance;
            bool group = Grouping, compact = game.staffBarCompact;
            Map home = Find.AnyPlayerHomeMap;
            labels.Clear();
            if (home == null || !group && !compact) return;
            List<ColonistBar.Entry> entries = CachedEntries(bar);
            var result = new List<ColonistBar.Entry>(entries.Count);
            int next = 0;
            for (int i = 0; i < entries.Count;)
            {
                int end = i;
                bool pawns = false;
                while (end < entries.Count && entries[end].group == entries[i].group)
                    pawns |= entries[end++].pawn != null;
                if (entries[i].map != home || !pawns)
                {
                    for (; i < end; i++) result.Add(new ColonistBar.Entry(entries[i].pawn, entries[i].map, next));
                    next++;
                    continue;
                }
                var all = new List<Pawn>();
                for (; i < end; i++)
                    if (entries[i].pawn != null) all.Add(entries[i].pawn);
                List<Pawn> staff = compact ? all.FindAll(p => ShownOnCompactBar(game, p)) : all;
                int homeFrom = result.Count;
                if (!group)
                {
                    foreach (Pawn p in staff) result.Add(Entry(p, home, next, compact));
                    if (staff.Count > 0) next++;
                }
                else
                    foreach (StaffBarGroup category in Enum.GetValues(typeof(StaffBarGroup)))
                    {
                        int before = result.Count;
                        // (A collapsed group shows the compact bar's own test, the pinned, the needy and the state's appointees; the compact bar has filtered by it already, so there the collapse changes nothing.)
                        bool collapsed = IsCollapsed(game, category), filter = collapsed && !compact;
                        foreach (Pawn p in staff)
                            if (GroupOf(p) == category && (!filter || ShownOnCompactBar(game, p))) result.Add(Entry(p, home, next, compact));
                        // (A collapsed group with nobody to show keeps a slot, as an empty map does, so that its label has a place.)
                        if (filter && result.Count == before && all.Exists(p => GroupOf(p) == category)) result.Add(new ColonistBar.Entry(null, home, next));
                        if (result.Count == before) continue;
                        labels.Add(new StaffBarLabel
                        {
                            category = category, group = next, first = before, last = result.Count - 1,
                            count = all.FindAll(p => !p.Dead && GroupOf(p) == category).Count, collapsed = collapsed,
                        });
                        next++;
                    }
                // (Nobody of the map shown: the map keeps its empty entry, as vanilla gives an empty map, so its frame still takes the player home from another map.)
                if (result.Count == homeFrom)
                {
                    result.Add(new ColonistBar.Entry(null, home, next));
                    next++;
                }
            }
            entries.Clear();
            entries.AddRange(result);
            List<int> reorderable = CachedReorderableGroups(bar);
            reorderable.Clear();
            for (int i = 0; i < entries.Count; i++) reorderable.Add(-1);
            bar.drawer.Notify_RecachedEntries();
            DrawLocsFinder(bar).CalculateDrawLocs(CachedDrawLocs(bar), out CachedScale(bar), next);
            PlaceLabels(bar);
        }

        /// <summary>
        /// Each label's text and place: centred under the group's frame (vanilla's frame reaches 12 scaled pixels below the last row), so that a click on it is outside the frame's own click (which takes
        /// the player to the map).
        /// </summary>
        private static void PlaceLabels(ColonistBar bar)
        {
            List<Vector2> locs = CachedDrawLocs(bar);
            Vector2 size = bar.Size;
            float margin = 12f * bar.Scale;
            // (Clear of the frame and of the pawns' name tags, which vanilla draws from 4 scaled pixels above a portrait's foot and which reach out of the frame on a bar of small portraits.)
            float below = Mathf.Max(margin, (Prefs.DisableTinyText ? 16f : 12f) - 4f * bar.Scale + 1f);
            Text.Font = GameFont.Tiny;
            float height = Text.LineHeight;
            Text.Font = GameFont.Small;
            foreach (StaffBarLabel label in labels)
            {
                float left = float.MaxValue, right = 0f, bottom = 0f;
                for (int i = label.first; i <= label.last && i < locs.Count; i++)
                {
                    left = Mathf.Min(left, locs[i].x);
                    right = Mathf.Max(right, locs[i].x + size.x);
                    bottom = Mathf.Max(bottom, locs[i].y + size.y);
                }
                label.rect = new Rect(left - margin, bottom + below, right - left + 2f * margin, height);
                // (Vanilla's weapon icon: the portrait's rect moved down by 1.05 of its height and scaled by 0.75, so it ends 0.925 of a portrait below the foot.)
                label.belowWeaponsY = Mathf.Max(label.rect.y, bottom + 0.925f * size.y + 1f);
                string name = ("RP_BarGroup_" + label.category).Translate();
                label.text = label.category == StaffBarGroup.Warden ? name
                    : (label.collapsed ? "RP_BarLabelCollapsed" : "RP_BarLabel").Translate(name, label.count).Resolve();
                label.brief = label.category == StaffBarGroup.Warden || !label.collapsed ? name : "RP_BarLabelCollapsedShort".Translate(name).Resolve();
                label.tip = "RP_BarLabelTip".Translate(label.text).Resolve();
            }
        }

        /// <summary>The labels after vanilla's bar: text under each group, a highlight and a tip on the one the mouse is over, and a click that collapses or expands the group.</summary>
        public static void DrawLabels(ColonistBar bar)
        {
            if (labels.Count == 0 || !Active || !Grouping || !BarVisible(bar) || Event.current.type == EventType.Layout) return;
            PrisonGame game = PrisonGame.Instance;
            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;
            bool wrap = Text.WordWrap;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperCenter;
            Text.WordWrap = false;
            StaffBarLabel clicked = null;
            if (Event.current.type == EventType.Repaint && labels[0].shown == null) FitLabels();
            foreach (StaffBarLabel label in labels)
            {
                if (label.shown == null) continue;
                Rect rect = WeaponsUnder(bar, label) ? label.drawn with { y = label.belowWeaponsY } : label.drawn;
                bool over = Mouse.IsOver(rect);
                if (Event.current.type == EventType.Repaint)
                {
                    if (over && CanCollapse(label.category)) Widgets.DrawHighlight(rect);
                    if (label.collapsed) GUI.color = CollapsedColor;
                    Widgets.Label(rect, label.shown);
                    GUI.color = Color.white;
                    if (over && CanCollapse(label.category)) TooltipHandler.TipRegion(rect, label.tip);
                }
                else if (over && Event.current.type == EventType.MouseDown)
                {
                    // (Any button is used here, as vanilla's bar uses the clicks on its portraits and frames, so that the map under the label neither starts a selection box nor takes a right-click
                    // order; a left click, once a press, toggles the group.)
                    if (Event.current.button == 0 && Event.current.clickCount == 1 && CanCollapse(label.category)) clicked = label;
                    Event.current.Use();
                }
            }
            Text.Font = font;
            Text.Anchor = anchor;
            Text.WordWrap = wrap;
            if (clicked != null) SetCollapsed(game, clicked.category, !IsCollapsed(game, clicked.category));
        }

        /// <summary>
        /// Each label's room and text (in the tiny font): as wide as its text, centred under its group, but not into the text of the labels beside it (of any group beside it, whatever its height); when even that is too narrow the count goes first, so that the name and a collapsed group's mark stay, and then the name is cut.
        /// </summary>
        private static void FitLabels()
        {
            var want = new float[labels.Count];
            for (int i = 0; i < labels.Count; i++) want[i] = Text.CalcSize(labels[i].text).x + 6f;
            for (int i = 0; i < labels.Count; i++)
            {
                StaffBarLabel label = labels[i];
                float centre = label.rect.center.x, left = 0f, right = UI.screenWidth;
                // (The neighbours keep at least their own group's width, or their text if that is less.)
                for (int j = 0; j < labels.Count; j++)
                {
                    if (j == i) continue;
                    float half = Mathf.Min(want[j], labels[j].rect.width) / 2f;
                    float other = labels[j].rect.center.x;
                    if (other < centre) left = Mathf.Max(left, other + half + 2f);
                    else right = Mathf.Min(right, other - half - 2f);
                }
                float width = Mathf.Max(label.rect.width, Mathf.Min(want[i], 2f * Mathf.Min(centre - left, right - centre)));
                label.drawn = new Rect(centre - width / 2f, label.rect.y, width, label.rect.height);
                label.shown = want[i] - 6f <= width ? label.text : label.brief.Truncate(width);
            }
        }

        /// <summary>Whether vanilla draws a weapon icon under a portrait of the group's last row (always, or while drafted, as the player's option says), where the label would sit on it.</summary>
        private static bool WeaponsUnder(ColonistBar bar, StaffBarLabel label)
        {
            ShowWeaponsUnderPortraitMode mode = Prefs.ShowWeaponsUnderPortraitMode;
            if (mode == ShowWeaponsUnderPortraitMode.Never) return false;
            List<ColonistBar.Entry> entries = CachedEntries(bar);
            List<Vector2> locs = CachedDrawLocs(bar);
            float lastRow = float.MinValue;
            for (int i = label.first; i <= label.last && i < locs.Count; i++) lastRow = Mathf.Max(lastRow, locs[i].y);
            for (int i = label.first; i <= label.last && i < locs.Count && i < entries.Count; i++)
                if (locs[i].y >= lastRow - 0.5f && entries[i].pawn is { } p && (mode == ShowWeaponsUnderPortraitMode.Always || p.Drafted) && p.equipment?.Primary is { } weapon && weapon.def.IsWeapon)
                    return true;
            return false;
        }

        /// <summary>
        /// An entry of the home map. On the compact bar a drag does nothing: vanilla's reorder renumbers every pawn of the bar's entries, and with the hidden ones not among them a drag moved staff
        /// nobody had touched once the full bar came back.
        /// </summary>
        private static ColonistBar.Entry Entry(Pawn p, Map map, int group, bool compact)
        {
            var entry = new ColonistBar.Entry(p, map, group);
            if (compact) entry.reorderAction = delegate { };
            return entry;
        }

        /// <summary>Whether the bar is about to be rebuilt (read before the method runs: it clears the flag first thing).</summary>
        public static bool Dirty(ColonistBar bar) => EntriesDirty(bar);
    }

    /// <summary>The colonist bar of a prison game: the home map's staff by role, or only those the compact bar shows (<see cref="StaffBar"/>).</summary>
    [HarmonyPatch(typeof(ColonistBar), "CheckRecacheEntries")]
    public static class Patch_ColonistBar_CheckRecacheEntries
    {
        public static void Prefix(ColonistBar __instance, out bool __state) => __state = StaffBar.Dirty(__instance);

        public static void Postfix(ColonistBar __instance, bool __state)
        {
            if (!__state) return;
            // (A bar rebuilt out of play, while a save loads, has no labels: those of before would point at entries they were not made from.)
            if (StaffBar.Active) StaffBar.Regroup(__instance);
            else StaffBar.ClearLabels();
        }
    }

    /// <summary>The labels of the staff groups, drawn after vanilla's bar (<see cref="StaffBar.DrawLabels"/>).</summary>
    [HarmonyPatch(typeof(ColonistBar), nameof(ColonistBar.ColonistBarOnGUI))]
    public static class Patch_ColonistBar_ColonistBarOnGUI
    {
        public static void Postfix(ColonistBar __instance) => StaffBar.DrawLabels(__instance);
    }
}
