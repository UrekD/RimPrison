using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Overview tab: the report card, the stat tiles, the grade history, the to-do band and the chronicle's latest.</summary>
    public static class OverviewTab
    {
        private static ReportCard overviewCard;
        /// <summary>The grade tooltip's per-diem line (<see cref="PerDiem.DeskLine"/>), built with the card.</summary>
        private static string overviewPerDiem = "";
        /// <summary>The lower band shows the per-wing block instead of the needs and complaints (the window has room for one of them at 1280 x 720).</summary>
        public static bool showWings;
        /// <summary>The lower band shows the desk (the open and held letters, the day's budget): ahead of <see cref="showWings"/>.</summary>
        public static bool showDesk;

        /// <summary>The panel's letters count opens the overview on the desk.</summary>
        public static void ShowDesk() => showDesk = true;
        private static readonly OverviewCache overviewCache = new OverviewCache();
        private static readonly List<PrisonDiagnostics.Item> overviewProblems = new List<PrisonDiagnostics.Item>();
        /// <summary>The desk as the card was worked out (null: work it out at the next draw).</summary>
        private static DeskBoard.DeskState overviewDesk;

        /// <summary>The ids of the six area tiles' tooltips.</summary>
        private const int TileTipId = 8_510_000;

        /// <summary>Lines of the to-do band at most (the rest is counted).</summary>
        private const int ProblemLines = 4;

        /// <summary>
        /// What the warden should fix, as a band of lines each opening the screen where it is fixed (the same list the alerts and the autotest read): a new player sees what to do next without
        /// knowing the alerts' names. Nothing wrong: one quiet line.
        /// </summary>
        private static float DoProblems(Rect rect)
        {
            var band = new Rect(rect.x, rect.y, rect.width, 22f);
            Text.Font = GameFont.Small;
            Widgets.Label(band, "RP_Problems".Translate());
            float lx = band.x + Text.CalcSize("RP_Problems".Translate()).x + 8f;
            if (overviewProblems.Count == 0)
            {
                GUI.color = PrisonHUD.Good;
                Widgets.Label(new Rect(lx, band.y, band.width - lx, 22f), "RP_NoProblems".Translate());
                GUI.color = Color.white;
                return 26f;
            }
            float y = rect.y + 22f;
            for (int i = 0; i < overviewProblems.Count && i < ProblemLines; i++)
            {
                PrisonDiagnostics.Item item = overviewProblems[i];
                var row = new Rect(rect.x, y, rect.width, 20f);
                Widgets.DrawBoxSolid(new Rect(row.x + 2f, row.y + 6f, 8f, 8f), PrisonHUD.Warn);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.Label(new Rect(row.x + 16f, row.y - 1f, row.width - 16f, 22f), item.text.Truncate(row.width - 20f));
                TooltipHandler.TipRegion(row, item.tab == null ? item.text : item.text + "\n\n" + "RP_ProblemOpenTip".Translate(("RP_Tab_" + item.tab).Translate()));
                if (item.tab is { } tab && Widgets.ButtonInvisible(row)) MainTabWindow_Prison.Open(tab);
                y += 20f;
            }
            if (overviewProblems.Count > ProblemLines)
            {
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x + 16f, y - 1f, rect.width - 16f, 18f), "RP_ProblemsMore".Translate(overviewProblems.Count - ProblemLines));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 18f;
            }
            return y - rect.y + 4f;
        }

        /// <summary>
        /// The report card, Prison Architect style: today's grade in a big tile, the six graded areas as tiles (letter,
        /// score bar, weight), the prison's numbers, inmate needs and complaints, and the latest from the chronicle.
        /// </summary>
        /// <summary>One bar per day (height and colour by grade), today's on the right; a tooltip names each day.</summary>
        private static void DrawGradeHistory(Rect r, PrisonGame game)
        {
            List<float> history = game.gradeHistory;
            const int days = 14;
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x, r.y, 150f, r.height), "RP_OverviewGradeHistory".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            var plot = new Rect(r.x + 150f, r.y, r.width - 150f, r.height);
            Widgets.DrawBoxSolid(plot, new Color(0.11f, 0.12f, 0.14f));
            int first = Mathf.Max(0, history.Count - days);
            float w = plot.width / days;
            for (int i = first; i < history.Count; i++)
            {
                float g = history[i];
                int slot = days - (history.Count - i);
                var bar = new Rect(plot.x + slot * w + 2f, plot.yMax - 2f - (plot.height - 4f) * Mathf.Clamp01(g / 100f), w - 4f, (plot.height - 4f) * Mathf.Clamp01(g / 100f));
                Widgets.DrawBoxSolid(bar, PrisonHUD.GradeColor(g));
                var cell = new Rect(plot.x + slot * w, plot.y, w, plot.height);
                // "Yesterday", "3 days ago": the entries carry no date, and local and global day numbers differ.
                if (Mouse.IsOver(cell))
                {
                    int daysAgo = history.Count - i;
                    string day = (daysAgo == 1 ? "RP_OverviewGradeYesterday" : "RP_OverviewGradeDaysAgo").Translate(daysAgo, PrisonGrading.Letter(g), g.ToString("F0"));
                    // (The areas of that day's card, or the word that the card kept no detail: a save from before the cards were kept.)
                    TooltipHandler.TipRegion(cell, game.CardAt(i) is { } card ? day + "\n\n" + GradeDrivers.Past(card) : day);
                }
            }
        }

        /// <summary>Today's meals from the stoves to the inmates' plates, in one line: cooked, eaten, waiting on the serving counters, inmates hungry now and the cooks at work.</summary>
        private static void DrawKitchenLine(Rect r, DeskBoard.KitchenLine k)
        {
            Widgets.DrawBoxSolid(r, new Color(0.11f, 0.12f, 0.14f));
            Widgets.Label(new Rect(r.x + 8f, r.y, r.width - 16f, r.height), "RP_DeskKitchen".Translate(k.cooked, k.eaten, k.onCounters, k.hungry, k.cooks).Resolve().Truncate(r.width - 18f));
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            TooltipHandler.TipRegion(r, "RP_DeskKitchenTip".Translate(DeskBoard.HungryBelow.ToStringPercent()));
        }

        private const float WingNameW = 190f, WingColW = 100f;

        /// <summary>One row per security level that holds inmates: how many, their mean tension, how many are tense, how many are held down (suppression over 50), the day's misconducts and the guards in the wing now.</summary>
        private static void DrawWings(Rect rect, List<DeskBoard.WingRow> wings)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 300f, 30f), "RP_DeskWings".Translate());
            Text.Font = GameFont.Small;
            float y = rect.y + 32f;
            if (wings.Count == 0)
            {
                Widgets.Label(new Rect(rect.x, y, rect.width, 22f), "RP_NoInmates".Translate());
                return;
            }
            string[] heads = { "RP_DeskColInmates", "RP_DeskColTension", "RP_DeskColTense", "RP_DeskColThumb", "RP_DeskColMisconduct", "RP_DeskColGuards" };
            Text.Font = GameFont.Tiny;
            GUI.color = PrisonHUD.Muted;
            Text.Anchor = TextAnchor.UpperCenter;
            for (int c = 0; c < heads.Length; c++) Widgets.Label(new Rect(rect.x + WingNameW + c * WingColW, y, WingColW, 18f), heads[c].Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 20f;
            for (int i = 0; i < wings.Count; i++)
            {
                if (y + 20f > rect.yMax)
                {
                    GUI.color = PrisonHUD.Muted;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(rect.x, y - 2f, rect.width, 18f), "RP_DeskWingsMore".Translate(wings.Count - i));
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    break;
                }
                DeskBoard.WingRow w = wings[i];
                var row = new Rect(rect.x, y, WingColW * heads.Length + WingNameW, 20f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.DrawBoxSolid(new Rect(row.x + 2f, row.y + 4f, 10f, 12f), w.level.color);
                Widgets.Label(new Rect(row.x + 18f, row.y - 2f, WingNameW - 18f, 22f), w.level.LabelCap.Truncate(WingNameW - 20f));
                Text.Anchor = TextAnchor.UpperCenter;
                string[] cells = { w.inmates.ToString(), w.meanTension.ToString("F0"), w.tense.ToString(), w.shut.ToString(), w.misconduct.ToString(), w.guards.ToString() };
                for (int c = 0; c < cells.Length; c++)
                {
                    // (A tense inmate is a warning; a wing with inmates and no guard in it is one too.)
                    GUI.color = c == 2 && w.tense > 0 || c == 5 && w.guards == 0 ? PrisonHUD.Warn : Color.white;
                    Widgets.Label(new Rect(row.x + WingNameW + c * WingColW, row.y - 2f, WingColW, 22f), cells[c]);
                }
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(row, "RP_DeskWingTip".Translate(w.level.LabelCap, w.inmates, w.meanTension.ToString("F0"), w.tense, DeskBoard.TenseAt.ToString("F0"), w.misconduct, w.guards)
                    + "\n\n" + "RP_DeskWingThumbTip".Translate(w.shut, Suppression.Line.ToString("F0"), w.meanSuppressed.ToString("F0")));
                y += 20f;
            }
        }

        /// <summary>The big grade's tooltip: what the grade is (or that there is none today), then what the week's grades are worth to the state's per-diem.</summary>
        public static string GradeTip(bool graded, string perDiem) => (graded ? "RP_OverviewGradeTip".Translate() : "RP_OverviewGradeNoneTip".Translate()) + "\n\n" + perDiem;

        /// <summary>
        /// The desk: the day's budget of decisions (used and left) under the heading, then two columns - the open letters (soonest deadline first, a click opens one) and the letters held for
        /// midnight (with the answer time they will get and when they lapse unanswered; a click reads one), the letters standing orders answered lately under them with their review links.
        /// </summary>
        private static void DrawDesk(Rect rect, PrisonGame game, DeskBoard.DeskState desk)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 300f, 30f), "RP_DeskHeading".Translate());
            Text.Font = GameFont.Small;
            float y = rect.y + 32f;
            var head = new Rect(rect.x, y, rect.width, 22f);
            if (desk.budgetOn && desk.budget > 0)
            {
                var bar = new Rect(head.x, head.y + 5f, 120f, 12f);
                Widgets.FillableBar(bar, Mathf.Clamp01(desk.used / (float)desk.budget), PrisonHUD.Solid(desk.Left > 0 ? PrisonHUD.Good : PrisonHUD.Warn));
                Widgets.Label(new Rect(bar.xMax + 8f, head.y - 1f, head.width - bar.width - 8f, 24f), desk.HeadLine().Truncate(head.width - bar.width - 10f));
            }
            else Widgets.Label(new Rect(head.x, head.y - 1f, head.width, 24f), desk.HeadLine().Truncate(head.width));
            TooltipHandler.TipRegion(head, "RP_DeskBudgetTip".Translate(StoryLetters.MaxHeldDays, StoryLetters.UrgentHours));
            y += 24f;
            var left = new Rect(rect.x, y, rect.width / 2f - 10f, rect.yMax - y);
            var right = new Rect(rect.x + rect.width / 2f + 10f, y, rect.width / 2f - 10f, rect.yMax - y);
            DeskColumn(left, "RP_DeskOpen", desk.open, "RP_DeskOpenNone");
            float ry = DeskColumn(right, "RP_DeskHeld", desk.held, "RP_DeskHeldNone");
            // (Under the held letters, while there is room: what the standing orders answered lately, each with its review link.)
            if (game.standingAnswered.Count == 0 || ry + 40f > right.yMax) return;
            ry += 4f;
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(right.x, ry, right.width, 18f), "RP_DeskStanding".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            ry += 18f;
            for (int i = game.standingAnswered.Count - 1; i >= 0 && ry + 20f <= right.yMax; i--)
            {
                ChoiceLetter_PrisonDecision l = game.standingAnswered[i];
                var row = new Rect(right.x, ry, right.width, 20f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.Label(new Rect(row.x + 4f, row.y - 1f, row.width - 84f, 22f), (Story.When(l.arrivalTick) + "  " + l.Label.Resolve()).Truncate(row.width - 88f));
                if (Widgets.ButtonText(new Rect(row.xMax - 78f, row.y, 78f, 20f), "RP_StandingOrderReview".Translate())) StandingOrders.Review(l);
                ry += 20f;
            }
        }

        /// <summary>The ids of the desk rows' tooltips (one per letter).</summary>
        private const int DeskTipId = 8_530_000;

        private static string DeskRowTip(DeskBoard.DeskLetter d)
        {
            string standing = StandingOrders.Offered(d.letter) && StandingOrders.OrderFor(d.letter.decision) is { } key ? "\n\n" + "RP_DeskRowStanding".Translate(key.Translate()).Resolve() : "";
            return (d.held
                ? "RP_DeskHeldTip".Translate(d.letter.Label, d.hoursLeft >= 0 ? "RP_DeskAnswerTime".Translate(d.hoursLeft).Resolve() : "RP_DeskNoDeadline".Translate().Resolve(), StoryLetters.MaxHeldDays)
                : "RP_DeskOpenTip".Translate(d.letter.Label)).Resolve() + standing;
        }

        /// <summary>One column of the desk: its heading and a row per letter (as many as fit, the rest counted). Returns where it ended.</summary>
        private static float DeskColumn(Rect col, string headKey, List<DeskBoard.DeskLetter> letters, string noneKey)
        {
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(col.x, col.y, col.width, 18f), headKey.Translate(letters.Count));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            float y = col.y + 18f;
            if (letters.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(col.x + 4f, y - 1f, col.width - 4f, 22f), noneKey.Translate());
                GUI.color = Color.white;
                return y + 20f;
            }
            const float deadlineW = 150f;
            for (int i = 0; i < letters.Count; i++)
            {
                if (y + 20f > col.yMax)
                {
                    GUI.color = PrisonHUD.Muted;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(col.x + 4f, y - 2f, col.width, 18f), "RP_DeskMore".Translate(letters.Count - i));
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    return y + 18f;
                }
                DeskBoard.DeskLetter d = letters[i];
                var row = new Rect(col.x, y, col.width, 20f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.DrawBoxSolid(new Rect(row.x + 2f, row.y + 6f, 8f, 8f), d.urgent ? PrisonHUD.Bad : d.held ? PrisonHUD.Muted : PrisonHUD.Warn);
                Widgets.Label(new Rect(row.x + 16f, row.y - 1f, row.width - 16f - deadlineW, 22f), d.letter.Label.Resolve().Truncate(row.width - 20f - deadlineW));
                string deadline = d.deadline ??= d.held
                    ? d.lapseHours >= 0 ? "RP_DeskLapses".Translate(d.lapseHours).Resolve() : "RP_DeskHeldShort".Translate().Resolve()
                    : d.hoursLeft >= 0 ? "RP_DeskHoursLeft".Translate(d.hoursLeft).Resolve() : "RP_DeskNoDeadline".Translate().Resolve();
                GUI.color = !d.held && d.hoursLeft >= 0 && d.hoursLeft < StoryLetters.UrgentHours ? PrisonHUD.Warn : PrisonHUD.Muted;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(row.xMax - deadlineW, row.y - 1f, deadlineW - 4f, 22f), deadline);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                // (Built when shown, one id a letter.)
                TooltipHandler.TipRegion(row, () => DeskRowTip(d), DeskTipId + d.letter.ID);
                if (Widgets.ButtonInvisible(row))
                {
                    // (Open: the letter itself, to answer. Held: read-only - it is not on the stack yet, so only "close" is offered.)
                    d.letter.OpenLetter();
                    overviewDesk = null;
                }
                y += 20f;
            }
            return y;
        }

        public static void Draw(Rect rect, PrisonGame game, Map map)
        {
            // (Per map: another map's card and to-do list must not stand for two seconds after the switch.)
            if (overviewCard == null || overviewCache.Stale(map, Time.frameCount))
            {
                overviewCard = PrisonGrading.Compute(map, game);
                overviewPerDiem = PerDiem.DeskLine(game, map);
                overviewCache.Stamp(map, Time.frameCount);
                overviewProblems.Clear();
                overviewProblems.AddRange(PrisonDiagnostics.Items(map));
                overviewDesk = null;
            }
            ReportCard card = overviewCard;
            // Header band: grade + six areas.
            var big = new Rect(rect.x, rect.y, 84f, 84f);
            // (No inmates: nothing to grade yet, an empty prison is not a failing one. A dash, in the neutral colour.)
            bool graded = card.Graded;
            Color bigColor = graded ? PrisonHUD.GradeColor(card.Overall) : PrisonHUD.Muted;
            Widgets.DrawBoxSolidWithOutline(big, bigColor * 0.55f, bigColor, 2);
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(big.x, big.y + 6f, big.width, 40f), graded ? PrisonGrading.Letter(card.Overall) : "–");
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(big.x, big.y + 46f, big.width, 32f), graded ? "RP_OverviewGradeToday".Translate(card.Overall.ToString("F0")) : "RP_OverviewGradeNone".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(big, GradeTip(graded, overviewPerDiem));
            (string key, float v, int weight)[] areas =
            {
                ("RP_Grade_Safety", card.safety, 25), ("RP_Grade_Security", card.security, 20), ("RP_Grade_Health", card.health, 15),
                ("RP_Grade_Conditions", card.conditions, 15), ("RP_Grade_Order", card.order, 15), ("RP_Grade_Reform", card.reform, 10),
            };
            float tileW = (rect.width - 94f - 5 * 6f) / 6f;
            for (int i = 0; i < areas.Length; i++)
            {
                var t = new Rect(big.xMax + 10f + i * (tileW + 6f), rect.y, tileW, 84f);
                Color c = graded ? PrisonHUD.GradeColor(areas[i].v) : PrisonHUD.Muted;
                Widgets.DrawBoxSolidWithOutline(t, new Color(0.11f, 0.12f, 0.14f), c);
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(t.x + 8f, t.y + 4f, 40f, 30f), graded ? PrisonGrading.Letter(areas[i].v) : "–");
                Text.Font = GameFont.Tiny;
                GUI.color = PrisonHUD.Muted;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(t.x, t.y + 6f, t.width - 8f, 18f), graded ? areas[i].v.ToString("F0") : "–");
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(t.x + 8f, t.y + 36f, t.width - 10f, 22f), areas[i].key.Translate());
                Widgets.FillableBar(new Rect(t.x + 8f, t.y + 62f, t.width - 16f, 6f), graded ? Mathf.Clamp01(areas[i].v / 100f) : 0f, PrisonHUD.Solid(c));
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(t.x + 8f, t.y + 68f, t.width - 10f, 16f), "RP_OverviewWeight".Translate(areas[i].weight));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                // (What the area is, then today's drivers and how far it moved since the previous card: built when the tooltip is shown, not every frame.)
                string tipKey = areas[i].key.Replace("RP_Grade_", "RP_GradeTip_"), area = GradeDrivers.Areas[i];
                if (graded) TooltipHandler.TipRegion(t, () => tipKey.Translate().Resolve() + "\n\n" + GradeDrivers.Line(area, card, game.CardAt(game.gradeHistory.Count - 1)), TileTipId + i);
                else TooltipHandler.TipRegion(t, tipKey.Translate().Resolve() + "\n\n" + "RP_OverviewGradeNoneTip".Translate().Resolve());
            }
            float y = rect.y + 96f;
            // Numbers as stat tiles.
            PrisonStats s = game.stats;
            (string key, int n)[] stats =
            {
                ("RP_OverviewAdmitted", s.admitted), ("RP_OverviewReleased", s.released), ("RP_OverviewEscaped", s.escaped), ("RP_OverviewDied", s.deaths),
                ("RP_OverviewMisconduct", s.misconducts), ("RP_OverviewRiots", s.riots), ("RP_OverviewContraband", s.contrabandFound + s.visitorContrabandFound + s.mailContrabandFound), ("RP_OverviewVisits", s.visits),
            };
            float statW = (rect.width - 7 * 6f) / 8f;
            for (int i = 0; i < stats.Length; i++)
            {
                var t = new Rect(rect.x + i * (statW + 6f), y, statW, 46f);
                Widgets.DrawBoxSolid(t, new Color(0.11f, 0.12f, 0.14f));
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(t.x, t.y + 2f, t.width, 26f), stats[i].n.ToString());
                Text.Font = GameFont.Tiny;
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(t.x, t.y + 28f, t.width, 16f), stats[i].key.Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            y += 56f;
            // The grade over the last two weeks: is the prison getting better or sliding?
            if (game.gradeHistory.Count > 0)
            {
                DrawGradeHistory(new Rect(rect.x, y, rect.width, 40f), game);
                y += 48f;
            }
            (List<DeskBoard.WingRow> wings, DeskBoard.KitchenLine kitchen) = DeskBoard.Current(game, map);
            DrawKitchenLine(new Rect(rect.x, y, rect.width, 22f), kitchen);
            y += 26f;
            // What to do next, then needs and complaints (bars), and the chronicle's latest at the bottom.
            y += DoProblems(new Rect(rect.x, y, rect.width, 100f));
            float bottomH = 84f;
            var lower = new Rect(rect.x, y, rect.width, rect.yMax - y - bottomH);
            DeskBoard.DeskState desk = overviewDesk ??= DeskBoard.Desk(game, map);
            if (showDesk) DrawDesk(lower, game, desk);
            else if (showWings) DrawWings(lower, wings);
            else NeedsReport.Draw(lower, map);
            // (On the heading's row at the right: the right column's heading is short. A button for each of the other two views.)
            float bx = lower.xMax;
            void Switch(string label, string tipKey, System.Action pick)
            {
                float w = Text.CalcSize(label).x + 24f;
                bx -= w;
                var b = new Rect(bx, lower.y + 2f, w, 24f);
                bx -= 6f;
                if (Widgets.ButtonText(b, label)) pick();
                TooltipHandler.TipRegion(b, tipKey.Translate());
            }
            if (showDesk || showWings) Switch("RP_DeskShowNeeds".Translate(), "RP_DeskWingsTip", () => showDesk = showWings = false);
            if (showDesk || !showWings) Switch("RP_DeskShowWings".Translate(), "RP_DeskWingsTip", () => (showDesk, showWings) = (false, true));
            if (!showDesk) Switch("RP_DeskShowDesk".Translate(desk.open.Count + desk.held.Count), "RP_DeskShowDeskTip", () => showDesk = true);
            var latest = new Rect(rect.x, rect.yMax - bottomH + 6f, rect.width, bottomH - 6f);
            Widgets.DrawLineHorizontal(latest.x, latest.y - 3f, latest.width);
            Widgets.Label(new Rect(latest.x, latest.y, latest.width, 22f), "RP_ChronicleLatest".Translate());
            // The button sits in the same band as the prison's name, to the left of it, so it overlaps neither that nor the list below.
            float allStatsRight = latest.xMax;
            if (!game.prisonName.NullOrEmpty())
            {
                TaggedString title = "RP_OverviewPrisonName".Translate(game.prisonName, GenDate.DaysPassed + 1);
                allStatsRight -= Text.CalcSize(title).x + 12f;
                Text.Anchor = TextAnchor.UpperRight;
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(latest.x, latest.y, latest.width, 22f), title);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (Widgets.ButtonText(new Rect(allStatsRight - 130f, latest.y, 130f, 22f), "RP_OverviewAllStats".Translate())) MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Statistics);
            float ly = latest.y + 22f;
            foreach (ChronicleEntry e in ChronicleTab.Latest(game, 3))
            {
                Widgets.DrawBoxSolid(new Rect(latest.x, ly + 4f, 6f, 14f), e.cat.color);
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(latest.x + 12f, ly, 110f, 22f), Story.When(e.tick));
                GUI.color = Color.white;
                Widgets.Label(new Rect(latest.x + 124f, ly, latest.width - 124f, 22f), e.text.Truncate(latest.width - 126f));
                ly += 20f;
            }
        }
    }
}
