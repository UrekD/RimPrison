using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Regime tab: a palette and a grid of 24 hours per security level, with yesterday's numbers in a strip under each row and today's orders beside it. One instance per window (the palette's pick and a drag in progress are the window's).</summary>
    public class RegimeTab
    {
        private RegimeActivityDef paintActivity;

        private bool paintingRegime;

        /// <summary>
        /// The regime, Prison Architect style: a palette, then one row of 24 coloured hours per security level. Runs of
        /// the same activity are labelled once; a line marks the current time; hatching marks an activity whose zone is
        /// not painted yet (it falls back). Drag across hours to paint (only drags that start in the grid). Under each row: the strip of yesterday (<see cref="RegimeFeedback"/>) and the buttons for today's orders
        /// (<see cref="RegimeOrders"/>); an hour an order has changed is marked on its cell.
        /// </summary>
        public void Draw(Rect rect, PrisonGame game)
        {
            Map map = Find.CurrentMap;
            PrisonMap pm = PrisonMap.Of(map);
            List<RegimeActivityDef> acts = DefDatabase<RegimeActivityDef>.AllDefsListForReading.Where(a => a.paintable).ToList();
            paintActivity ??= acts.FirstOrDefault();
            // Palette: swatch + name, the selected one outlined in white.
            float x = rect.x;
            Text.Font = GameFont.Small;
            foreach (RegimeActivityDef a in acts)
            {
                var r = new Rect(x, rect.y, 116f, 28f);
                Widgets.DrawBoxSolid(r, new Color(0.13f, 0.14f, 0.16f));
                Widgets.DrawBoxSolid(new Rect(r.x + 6f, r.y + 7f, 14f, 14f), a.color);
                if (paintActivity == a)
                {
                    GUI.color = Color.white;
                    Widgets.DrawBox(r, 2);
                }
                else if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(r.x + 26f, r.y, r.width - 28f, r.height), a.LabelCap);
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(r, a.description);
                if (Widgets.ButtonInvisible(r)) paintActivity = a;
                x += 120f;
            }
            float labelW = 250f, gridX = rect.x + labelW, cellW = (rect.width - labelW) / 24f, y = rect.y + 44f;
            int nowHour = GenLocalDate.HourOfDay(map);
            Text.Font = GameFont.Tiny;
            for (int h = 0; h < 24; h++)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = h == nowHour ? Color.white : PrisonHUD.Muted;
                Widgets.Label(new Rect(gridX + h * cellW, y, cellW, 20f), h.ToString());
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            y += 22f;
            float gridTop = y;
            if (Event.current.type == EventType.MouseUp) paintingRegime = false;
            Text.Font = GameFont.Small;
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(s => s.uiOrder))
            {
                if (!Unlocks.IsUnlocked(sec)) continue;
                RegimeSchedule sched = game.RegimeFor(sec);
                DayOrders orders = RegimeOrders.Of(game, sec);
                Widgets.DrawBoxSolid(new Rect(rect.x, y + 4f, 5f, 28f), sec.color);
                float nameW = Mathf.Min(Text.CalcSize(sec.LabelCap).x, 128f);
                Widgets.Label(new Rect(rect.x + 10f, y + 1f, nameW + 2f, 22f), sec.LabelCap.Truncate(128f));
                DrawOrderTag(game, map, sec, orders, new Rect(rect.x + 14f + nameW, y + 4f, labelW - 40f - nameW - 8f, 16f));
                int count = map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate()?.security == sec);
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x + 10f, y + 20f, labelW - 40f, 16f), "RP_RegimeInmates".Translate(count));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                // Lock this level down for four hours (the panel button locks everyone down).
                var lockRect = new Rect(rect.x + labelW - 28f, y + 8f, 22f, 22f);
                bool wingLocked = game.wingLockdowns.TryGetValue(sec, out int until) && until > Find.TickManager.TicksGame;
                GUI.color = wingLocked ? PrisonHUD.Bad : Mouse.IsOver(lockRect) ? Color.white : PrisonHUD.Muted;
                if (RegimePadlock != null) GUI.DrawTexture(lockRect, RegimePadlock);
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(lockRect)) game.StartWingLockdown(sec, 4);
                TooltipHandler.TipRegion(lockRect, wingLocked ? "RP_WingLockdownActive".Translate(sec.label) : "RP_WingLockdown".Translate(sec.label));
                for (int h = 0; h < 24; h++)
                {
                    var cell = new Rect(gridX + h * cellW, y, cellW - 1f, 36f);
                    RegimeActivityDef a = sched.At(h);
                    Widgets.DrawBoxSolid(cell, a.color);
                    // An order of today has changed this hour: a bar on top of the cell, and the tip says what for what.
                    RegimeActivityDef ordered = orders == null ? a : RegimeOrders.Apply(a, h, orders);
                    if (ordered != a)
                    {
                        Widgets.DrawBoxSolid(new Rect(cell.x, cell.y, cell.width, 4f), ordered.color);
                        Widgets.DrawBox(new Rect(cell.x, cell.y, cell.width, 4f), 1);
                    }
                    // Its zone is missing: it falls back to something else (hatched).
                    if (pm != null && a.zones.Count > 0 && !a.zones.Any(z => pm.zones.Any(z)))
                    {
                        GUI.BeginGroup(cell);
                        for (float d = -cell.height; d < cell.width; d += 7f)
                            Widgets.DrawLine(new Vector2(d, cell.height), new Vector2(d + cell.height, 0f), new Color(0f, 0f, 0f, 0.45f), 1f);
                        GUI.EndGroup();
                        GUI.color = Color.white;
                        TooltipHandler.TipRegion(cell, "RP_RegimeMissingZone".Translate(a.label, a.zones[0].label, a.fallback?.label ?? RPDefOf.RP_Lockup.label));
                    }
                    else if (ordered != a) TooltipHandler.TipRegion(cell, $"{h}:00 {a.LabelCap}\n" + "RP_RegimeOrderCell".Translate(ordered.label, a.label));
                    else TooltipHandler.TipRegion(cell, $"{h}:00 {a.LabelCap}");
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(cell)) paintingRegime = true;
                    if (paintingRegime && Mouse.IsOver(cell) && paintActivity != null && sched.At(h) != paintActivity)
                    {
                        var was = RegimeOrders.PlacedBefore(sec);
                        sched.Set(h, paintActivity);
                        RegimeOrders.Changed(sec, was);
                        SoundDefOf.Designate_DragStandard_Changed.PlayOneShotOnCamera();
                    }
                }
                // Runs of the same activity are labelled once, centred.
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                for (int h = 0; h < 24;)
                {
                    RegimeActivityDef a = sched.At(h);
                    int end = h + 1;
                    while (end < 24 && sched.At(end) == a) end++;
                    var run = new Rect(gridX + h * cellW, y, (end - h) * cellW - 1f, 36f);
                    if (end - h >= 2 && Text.CalcSize(a.LabelCap).x < run.width - 4f)
                    {
                        GUI.color = new Color(0f, 0f, 0f, 0.6f);
                        Widgets.Label(new Rect(run.x + 1f, run.y + 1f, run.width, run.height), a.LabelCap);
                        GUI.color = Color.white;
                        Widgets.Label(run, a.LabelCap);
                    }
                    h = end;
                }
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                DrawStrip(pm, sec, gridX, y + 38f, cellW);
                DrawOrdersButton(game, map, sec, orders, new Rect(rect.x + 10f, y + 35f, 96f, 20f));
                y += 40f + StripHeight + 4f;
            }
            // Now: a line across all rows at the current time of day.
            float nowX = gridX + GenLocalDate.DayPercent(map) * 24f * cellW;
            Widgets.DrawBoxSolid(new Rect(nowX - 1f, gridTop - 2f, 2f, y - gridTop), Color.white);
            y += 10f;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(rect.x, y, rect.width, 76f), "RP_RegimeHelp".Translate() + "\n" + "RP_RegimeFeedbackHelp".Translate());
            GUI.color = Color.white;
            var resetRect = new Rect(rect.x, y + 80f, 200f, 30f);
            TooltipHandler.TipRegion(resetRect, "RP_RegimeResetTip".Translate());
            if (Widgets.ButtonText(resetRect, "RP_RegimeReset".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_RegimeResetConfirm".Translate(), () =>
                {
                    foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
                        game.regimes[sec] = RegimeSchedule.FromDefault(sec);
                }, destructive: true));
            }
        }

        private static Texture2D regimePadlock;
        private static Texture2D RegimePadlock => regimePadlock ??= ContentFinder<Texture2D>.Get("UI/Icons/Padlock", reportFailure: false);

        private const float StripHeight = 12f;
        private static readonly Color StripBack = new Color(0.13f, 0.14f, 0.16f), StripMissed = new Color(0.95f, 0.6f, 0.15f);

        /// <summary>
        /// Yesterday's numbers for the row, one thin bar per hour: its height is the share of the level in place, a dot marks incidents and another the meal missed (see <see cref="RegimeFeedback"/>; bytes read as they
        /// are, nothing built here). The tooltip's text is made only for the hour under the mouse.
        /// </summary>
        private static void DrawStrip(PrisonMap pm, SecurityLevelDef sec, float gridX, float y, float cellW)
        {
            RegimeDay day = null;
            pm?.regime.Yesterday.TryGetValue(sec, out day);
            for (int h = 0; h < 24; h++)
            {
                var bar = new Rect(gridX + h * cellW, y, cellW - 1f, StripHeight);
                Widgets.DrawBoxSolid(bar, StripBack);
                if (day != null && day.Has(h))
                {
                    float share = day.share[h] / 100f, fill = Mathf.Max(1f, StripHeight * share);
                    Widgets.DrawBoxSolid(new Rect(bar.x, bar.yMax - fill, bar.width, fill), share >= 0.85f ? PrisonHUD.Good : share >= 0.6f ? PrisonHUD.Warn : PrisonHUD.Bad);
                    if (day.incidents[h] > 0) Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, 3f, 3f), PrisonHUD.Bad);
                    if (day.missed[h] > 0) Widgets.DrawBoxSolid(new Rect(bar.xMax - 3f, bar.y, 3f, 3f), StripMissed);
                }
                if (pm != null && Mouse.IsOver(bar)) TooltipHandler.TipRegion(bar, pm.regime.Tip(sec, h));
            }
        }

        /// <summary>
        /// The short tag beside the level's name for the orders in force ("No yard", "Early lockup"; "2 orders" when the words do not fit), in the level's colour; its tooltip names them in full, with the hours, and says when they lapse
        /// (<see cref="RegimeOrders.Words"/>). Nothing when there are none.
        /// </summary>
        private static void DrawOrderTag(PrisonGame game, Map map, SecurityLevelDef sec, DayOrders orders, Rect room)
        {
            if (orders == null) return;
            var tags = new List<string>();
            foreach (DayOrder order in System.Enum.GetValues(typeof(DayOrder)))
                if (orders.Has(order)) tags.Add(("RP_OrderTag_" + order).Translate().Resolve());
            Text.Font = GameFont.Tiny;
            string text = string.Join(", ", tags);
            if (Text.CalcSize(text).x + 8f > room.width) text = "RP_OrderTagMany".Translate(tags.Count).Resolve();
            var pill = new Rect(room.x, room.y, Mathf.Min(room.width, Text.CalcSize(text).x + 8f), room.height);
            Widgets.DrawBoxSolid(pill, StripBack);
            GUI.color = sec.color;
            Widgets.DrawBox(pill, 1);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(pill, text);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(pill, "RP_OrderTagTip".Translate(sec.label, RegimeOrders.Words(orders, withHours: true), RegimeOrders.HoursToMidnight(map)).Resolve());
        }

        /// <summary>The "Today..." button of the row: a menu of the three orders (<see cref="OpenOrdersMenu"/>). Outlined in the level's colour while any order is in force.</summary>
        private static void DrawOrdersButton(PrisonGame game, Map map, SecurityLevelDef sec, DayOrders orders, Rect btn)
        {
            Widgets.DrawBoxSolid(btn, StripBack);
            if (Mouse.IsOver(btn)) Widgets.DrawHighlight(btn);
            if (orders != null)
            {
                GUI.color = sec.color;
                Widgets.DrawBox(btn, 2);
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(btn, "RP_OrdersBtn".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(btn, "RP_OrdersBtnTip".Translate(sec.label));
            if (Widgets.ButtonInvisible(btn)) OpenOrdersMenu(game, map, sec);
        }

        /// <summary>
        /// The menu of today's orders for the level, in full words (an order in force reads "ordered, click to withdraw"; a click gives it or withdraws it, see <see cref="RegimeOrders.Toggle"/>). One that cannot be given
        /// now is greyed with the reason; each has its tooltip.
        /// </summary>
        public static void OpenOrdersMenu(PrisonGame game, Map map, SecurityLevelDef sec)
        {
            var options = new List<FloatMenuOption>();
            DayOrders orders = RegimeOrders.Of(game, sec);
            foreach (DayOrder order in System.Enum.GetValues(typeof(DayOrder)))
            {
                string label = ("RP_OrderMenu_" + order).Translate().Resolve();
                bool on = orders?.Has(order) ?? false;
                string why = RegimeOrders.Unavailable(game, map, sec, order);
                FloatMenuOption option = why != null ? new FloatMenuOption(label + " (" + why + ")", null)
                    : new FloatMenuOption(on ? "RP_OrderMenuOn".Translate(label).Resolve() : label, () =>
                    {
                        RegimeOrders.Toggle(game, map, sec, order);
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    });
                option.tooltip = new TipSignal(on ? "RP_OrderTipOn".Translate(sec.label, ("RP_Order_" + order).Translate()).Resolve() : ("RP_OrderTip_" + order).Translate(sec.label, RegimeOrders.EarlyLockupHour).Resolve());
                options.Add(option);
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
