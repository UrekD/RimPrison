using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The evacuation block of the Deployment tab (<see cref="EvacuationSystem"/>): the assembly points (at most two: a yard, a kept room or the holding patch, each with the levels it takes), the buttons that start an evacuation of a wing that
    /// burns, or a drill of any wing, and while one runs the accountability list (arrived, being carried, waiting for an escort, route blocked, missing) with the all-clear.
    /// </summary>
    public static class EvacuationUI
    {
        /// <summary>Where the block began in the left column the last time it was drawn (the tour scrolls to it).</summary>
        public static float blockY;

        /// <summary>Set by the tour: the next time the block is drawn the left column is scrolled to it (where it begins then, not where it began the last time the tab was open: the rows above it may have grown since).</summary>
        public static bool scrollToBlock;

        /// <summary>The most names the roll call lists under the counts.</summary>
        public const int ListedMax = 10;

        public static void Draw(Rect left, ref float y, Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            EvacuationState s = pm.evac;
            blockY = y;
            if (scrollToBlock)
            {
                scrollToBlock = false;
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, y - 20f));
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(left.x, y, left.width, 30f), "RP_DeployEvac".Translate());
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(new Rect(left.x, y, left.width, 30f), "RP_DeployEvacTip".Translate());
            y += 32f;
            DrawPoints(left, ref y, map, pm, s);
            if (s.Active) DrawRunning(left, ref y, map, pm, s);
            // (A drill gives way to a real fire: the wings that burn keep their button while it runs.)
            if (!s.Active || s.drill) DrawStarters(left, ref y, map, pm, drill: !s.Active);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 10f;
        }

        private static string LevelsText(AssemblyPoint p) => p.levels.Count == 0 ? "RP_EvacPointNoLevels".Translate().ToString() : string.Join(", ", p.levels.Select(l => l.LabelCap.ToString()));

        private static void DrawPoints(Rect left, ref float y, Map map, PrisonMap pm, EvacuationState s)
        {
            if (s.points.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 34f), "RP_EvacPointsNone".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 36f;
            }
            for (int i = 0; i < s.points.Count; i++)
            {
                AssemblyPoint point = s.points[i];
                int patch = EvacuationSystem.PatchOf(pm, point);
                var row = new Rect(left.x, y, left.width, 52f);
                Widgets.DrawLightHighlight(row);
                string title = patch < 0
                    ? "RP_EvacPointGone".Translate(point.zone.label).ToString()
                    : "RP_EvacPointRow".Translate(i + 1, GangTurf.PlaceName(pm.zones, patch).CapitalizeFirst(), pm.zones.Patches[patch].cells).ToString();
                GUI.color = patch < 0 ? PrisonHUD.Bad : EvacuationSystem.PatchBurning(pm, patch) ? PrisonHUD.Warn : Color.white;
                Widgets.Label(new Rect(row.x + 4f, row.y + 1f, row.width - 62f, 22f), title.Truncate(row.width - 64f));
                GUI.color = Color.white;
                if (patch >= 0 && Widgets.ButtonImage(new Rect(row.xMax - 54f, row.y + 1f, 24f, 24f), TexButton.Search)) CameraJumper.TryJump(pm.zones.Patches[patch].center.ToIntVec3(), map);
                if (Widgets.ButtonImage(new Rect(row.xMax - 26f, row.y + 1f, 24f, 24f), TexButton.Delete)) EvacuationSystem.Unmark(pm, point);
                var levels = new Rect(row.x + 4f, row.y + 26f, row.width - 8f, 24f);
                AssemblyPoint chosen = point;
                if (Widgets.ButtonText(levels, ("RP_EvacPointLevels".Translate(LevelsText(point))).ToString().Truncate(levels.width - 12f))) Find.WindowStack.Add(new FloatMenu(LevelOptions(chosen)));
                TooltipHandler.TipRegion(levels, "RP_EvacPointLevelsTip".Translate());
                y += 56f;
            }
            if (s.points.Count < EvacuationState.MaxPoints)
            {
                var mark = new Rect(left.x, y, left.width, 26f);
                TooltipHandler.TipRegion(mark, (s.points.Count == 0 ? "RP_EvacMarkTip" : "RP_EvacMarkSecondTip").Translate());
                if (Widgets.ButtonText(mark, (s.points.Count == 0 ? "RP_EvacMark" : "RP_EvacMarkSecond").Translate())) Find.WindowStack.Add(new FloatMenu(MarkOptions(map, pm)));
                y += 30f;
            }
        }

        /// <summary>The levels as a menu: a click takes a level in or out of the point, and the menu opens again, so several can be set in a row.</summary>
        private static List<FloatMenuOption> LevelOptions(AssemblyPoint point)
        {
            var options = new List<FloatMenuOption>();
            foreach (SecurityLevelDef level in DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(l => Unlocks.IsUnlocked(l) || point.levels.Contains(l)).OrderBy(l => l.uiOrder))
            {
                SecurityLevelDef chosen = level;
                options.Add(new FloatMenuOption((point.levels.Contains(level) ? "✓ " : "") + level.LabelCap, () =>
                {
                    EvacuationSystem.ToggleLevel(point, chosen);
                    Find.WindowStack.Add(new FloatMenu(LevelOptions(point)));
                }));
            }
            return options;
        }

        private static List<FloatMenuOption> MarkOptions(Map map, PrisonMap pm)
        {
            var options = new List<FloatMenuOption>();
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
            {
                if (!EvacuationSystem.MarkableZone(patches[i].zone) || options.Count >= 40) continue;
                int patch = i;
                bool marked = pm.evac.points.Exists(p => EvacuationSystem.PatchOf(pm, p) == patch);
                if (marked) continue;
                string label = "RP_EvacMarkMenu".Translate(GangTurf.PlaceName(pm.zones, patch).CapitalizeFirst(), patches[i].cells, KeptRooms.ForLabel(pm.zones.KeptFor(patch))).ToString();
                options.Add(new FloatMenuOption(label, () =>
                {
                    int now = patch;
                    if (!EvacuationSystem.Mark(pm, now)) Messages.Message("RP_EvacMarkFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }));
            }
            if (options.Count == 0) options.Add(new FloatMenuOption("RP_EvacMarkNone".Translate(), null));
            return options;
        }

        /// <summary>Not running (or only a drill): a button for each wing that has a fire in it, and the drill.</summary>
        private static void DrawStarters(Rect left, ref float y, Map map, PrisonMap pm, bool drill)
        {
            foreach (PrisonZoneDef wing in EvacuationSystem.BurningWings(map, pm))
            {
                var row = new Rect(left.x, y, left.width, 26f);
                string why = EvacuationSystem.CannotStart(map, wing, drill: false);
                GUI.color = PrisonHUD.Bad;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x, row.y + 6f, 110f, 20f), "RP_EvacBurning".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                var button = new Rect(row.x + 112f, row.y, row.width - 112f, 26f);
                if (why != null)
                {
                    GUI.color = Color.gray;
                    Widgets.ButtonText(button, "RP_EvacStart".Translate(wing.label).ToString().Truncate(button.width - 12f));
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(button, why);
                }
                else
                {
                    TooltipHandler.TipRegion(button, "RP_EvacStartTip".Translate(wing.label));
                    if (Widgets.ButtonText(button, "RP_EvacStart".Translate(wing.label).ToString().Truncate(button.width - 12f))) EvacuationSystem.Start(map, wing, drill: false);
                }
                y += 30f;
            }
            if (!drill) return;
            var drillRect = new Rect(left.x, y, left.width, 26f);
            string drillBar = pm.evac.DrilledWithinADay(Find.TickManager.TicksGame) ? "RP_EvacDrilledToday".Translate().ToString() : null;
            TooltipHandler.TipRegion(drillRect, drillBar ?? "RP_EvacDrillTip".Translate().ToString());
            if (drillBar != null)
            {
                GUI.color = Color.gray;
                Widgets.ButtonText(drillRect, "RP_EvacDrill".Translate());
                GUI.color = Color.white;
            }
            else if (Widgets.ButtonText(drillRect, "RP_EvacDrill".Translate())) Find.WindowStack.Add(new FloatMenu(DrillOptions(map, pm)));
            y += 30f;
        }

        private static List<FloatMenuOption> DrillOptions(Map map, PrisonMap pm)
        {
            var options = new List<FloatMenuOption>();
            foreach (PrisonZoneDef wing in DefDatabase<PrisonZoneDef>.AllDefsListForReading.Where(z => z.security != null && pm.zones.Any(z) && Unlocks.IsUnlocked(z)).OrderBy(z => z.uiOrder))
            {
                PrisonZoneDef chosen = wing;
                string why = EvacuationSystem.CannotStart(map, wing, drill: true);
                string label = wing.LabelCap.ToString();
                options.Add(new FloatMenuOption(why == null ? label : "RP_EvacDrillMenuWhy".Translate(label, why).ToString(), why == null ? () => EvacuationSystem.Start(map, chosen, drill: true) : null));
            }
            if (options.Count == 0) options.Add(new FloatMenuOption("RP_EvacDrillNoWing".Translate(), null));
            return options;
        }

        private static string StatusText(EvacEntry e)
        {
            string text = ("RP_EvacStatus_" + (e.status == EvacStatus.Escorted && e.pawn?.CarriedBy != null ? "Carried" : e.status.ToString())).Translate().ToString();
            return e.fallback && e.status == EvacStatus.Arrived ? "RP_EvacStatusFallback".Translate(text).ToString() : text;
        }

        private static int Rank(EvacEntry e) => e.status switch
        {
            EvacStatus.Missing => 0,
            EvacStatus.Blocked => 1,
            EvacStatus.WaitingEscort => 2,
            EvacStatus.Stayed => 3,
            EvacStatus.Escorted => 4,
            EvacStatus.Planning => 5,
            EvacStatus.Walking => 5,
            _ => 9,
        };

        private static void Line(Rect left, ref float y, string text, Color color)
        {
            GUI.color = color;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(left.x + 4f, y, left.width - 8f, 18f), text);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 18f;
        }

        /// <summary>Running: what is going on, the roll call's counts, the names of those not at a place yet, the separation that failed, and the all-clear.</summary>
        private static void DrawRunning(Rect left, ref float y, Map map, PrisonMap pm, EvacuationState s)
        {
            bool back = s.phase == EvacPhase.Back;
            GUI.color = back ? PrisonHUD.Good : PrisonHUD.Bad;
            Widgets.Label(new Rect(left.x, y, left.width, 24f), (s.drill ? (back ? "RP_EvacRunningDrillBack" : "RP_EvacRunningDrill") : back ? "RP_EvacRunningBack" : "RP_EvacRunning").Translate(s.wing.label).ToString().Truncate(left.width - 4f));
            GUI.color = Color.white;
            y += 26f;
            EvacuationSystem.Counts n = EvacuationSystem.Tally(s);
            if (!back)
            {
                Line(left, ref y, "RP_EvacCountArrived".Translate(n.arrived + n.outside, n.outside, n.fallback), PrisonHUD.Good);
                if (n.rescued > 0) Line(left, ref y, "RP_EvacCountRescued".Translate(n.rescued), PrisonHUD.Good);
                Line(left, ref y, "RP_EvacCountCarried".Translate(n.carried), n.carried > 0 ? PrisonHUD.Warn : PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountWaiting".Translate(n.waiting), n.waiting > 0 ? PrisonHUD.Warn : PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountOnTheWay".Translate(n.onTheWay), PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountBlocked".Translate(n.blocked), n.blocked > 0 ? PrisonHUD.Bad : PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountMissing".Translate(n.missing), n.missing > 0 ? PrisonHUD.Bad : PrisonHUD.Muted);
            }
            else
            {
                Line(left, ref y, "RP_EvacCountHome".Translate(n.home, n.Total - n.outside - n.missing - n.dropped - n.rescued), PrisonHUD.Good);
                Line(left, ref y, "RP_EvacCountBackWaiting".Translate(n.waiting + n.onTheWay + n.carried), PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountStayed".Translate(n.stayed), n.stayed > 0 ? PrisonHUD.Warn : PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountBlocked".Translate(n.blocked), n.blocked > 0 ? PrisonHUD.Bad : PrisonHUD.Muted);
                Line(left, ref y, "RP_EvacCountMissing".Translate(n.missing), n.missing > 0 ? PrisonHUD.Bad : PrisonHUD.Muted);
            }
            int together = EvacuationSystem.SeparationFailures(s);
            if (together > 0) Line(left, ref y, "RP_EvacTogether".Translate(together), PrisonHUD.Warn);
            var open = s.entries.Where(e => e.Moving && e.status != EvacStatus.Arrived || e.status == EvacStatus.Missing).OrderBy(Rank).ThenBy(e => e.id).ToList();
            foreach (EvacEntry e in open.Take(ListedMax))
            {
                var row = new Rect(left.x + 4f, y, left.width - 8f, 20f);
                Pawn p = e.pawn;
                if (p != null && Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Text.Font = GameFont.Tiny;
                GUI.color = e.status is EvacStatus.Missing or EvacStatus.Blocked ? PrisonHUD.Bad : e.status is EvacStatus.WaitingEscort or EvacStatus.Stayed ? PrisonHUD.Warn : PrisonHUD.Muted;
                Widgets.Label(row, ("RP_EvacListRow".Translate(e.name, StatusText(e))).ToString().Truncate(row.width));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (p is { Spawned: true } && Widgets.ButtonInvisible(row)) CameraJumper.TryJump(p);
                y += 20f;
            }
            if (open.Count > ListedMax) Line(left, ref y, "RP_EvacListMore".Translate(open.Count - ListedMax), PrisonHUD.Muted);
            if (!back)
            {
                var clear = new Rect(left.x, y + 2f, left.width, 26f);
                // (Not while the wing burns: a fire in it on the way back would turn everyone out again at once.)
                if (EvacuationSystem.FireInWing(map, pm, s.wing))
                {
                    TooltipHandler.TipRegion(clear, "RP_EvacAllClearBurning".Translate(s.wing.label));
                    GUI.color = Color.gray;
                    Widgets.ButtonText(clear, "RP_EvacAllClearButton".Translate());
                    GUI.color = Color.white;
                }
                else
                {
                    TooltipHandler.TipRegion(clear, "RP_EvacAllClearTip".Translate());
                    if (Widgets.ButtonText(clear, "RP_EvacAllClearButton".Translate())) EvacuationSystem.AllClear(map, byWarden: true);
                }
                y += 32f;
            }
        }
    }
}
