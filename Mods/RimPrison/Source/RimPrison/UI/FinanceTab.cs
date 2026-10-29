using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Finance tab: funds, today by category, the last 14 days and the recent entries.</summary>
    public static class FinanceTab
    {
        public static void Draw(Rect rect, PrisonGame game, ref Vector2 scroll)
        {
            // Header: funds and today's net.
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 300f, 32f), Money.Format(game.ledger.funds));
            Text.Font = GameFont.Small;
            DayBook today = game.ledger.Today;
            int net = today.Income - today.Expenses;
            GUI.color = net >= 0 ? PrisonHUD.Good : PrisonHUD.Bad;
            Widgets.Label(new Rect(rect.x + 160f, rect.y + 7f, 200f, 24f), "RP_HudToday".Translate(Money.FormatSigned(net)));
            GUI.color = Color.white;
            if (game.story.loanOwed > 0)
            {
                var loanRect = new Rect(rect.x + 370f, rect.y + 7f, 300f, 24f);
                GUI.color = PrisonHUD.Warn;
                Widgets.Label(loanRect, "RP_FinanceLoan".Translate(Money.Format(game.story.loanOwed)));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(loanRect, "RP_FinanceLoanTip".Translate(DebtCrisis.RepaymentShare.ToStringPercent(), Money.Format(DebtCrisis.MinRepayment)));
            }
            if (game.story.contractTarget > 0)
            {
                var contractRect = new Rect(rect.x + 680f, rect.y + 7f, rect.width - 680f, 24f);
                Widgets.Label(contractRect, "RP_FinanceContract".Translate(game.story.contractDelivered, game.story.contractTarget, game.story.contractRunsLeft));
                TooltipHandler.TipRegion(contractRect, "RP_FinanceContractTip".Translate(Money.Format(ExportContracts.Bonus(game.story.contractTarget)),
                    Money.Format(ExportContracts.Penalty(game.story.contractTarget))));
            }
            float top = rect.y + 40f;
            var left = new Rect(rect.x, top, 300f, rect.yMax - top);
            var chart = new Rect(left.xMax + 16f, top, 330f, 220f);
            var right = new Rect(chart.xMax + 16f, top, rect.xMax - chart.xMax - 16f, rect.yMax - top);
            // Today by category: income and expenses, bars relative to the largest.
            var cats = DefDatabase<LedgerCategoryDef>.AllDefsListForReading.OrderBy(c => c.uiOrder)
                .Where(c => today.totals.TryGetValue(c, out int v) && v != 0).ToList();
            int largest = cats.Count == 0 ? 1 : cats.Max(c => Mathf.Abs(today.totals[c]));
            float y = left.y;
            foreach (bool income in new[] { true, false })
            {
                Widgets.Label(new Rect(left.x, y, left.width, 22f), (income ? "RP_FinanceIncome" : "RP_FinanceExpenses").Translate());
                y += 22f;
                foreach (LedgerCategoryDef c in cats)
                {
                    int v = today.totals[c];
                    if (v > 0 != income) continue;
                    var catRect = new Rect(left.x, y, 130f, 20f);
                    Widgets.Label(catRect, c.LabelCap.Truncate(128f));
                    TooltipHandler.TipRegion(catRect, c.LabelCap); // the full name when it is cut short
                    var bar = new Rect(left.x + 132f, y + 4f, 90f, 12f);
                    Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * Mathf.Abs(v) / (float)largest, bar.height), income ? PrisonHUD.Good : PrisonHUD.Bad);
                    Text.Anchor = TextAnchor.UpperRight;
                    Widgets.Label(new Rect(left.x, y, left.width, 20f), Money.FormatSigned(v));
                    Text.Anchor = TextAnchor.UpperLeft;
                    y += 20f;
                }
                y += 8f;
            }
            // Last 14 days: income up (green), expenses down (red), net as a white mark.
            Widgets.Label(new Rect(chart.x, chart.y - 2f, chart.width, 22f), "RP_LastDays".Translate());
            var plot = new Rect(chart.x, chart.y + 22f, chart.width, chart.height - 40f);
            Widgets.DrawBoxSolid(plot, new Color(0.1f, 0.11f, 0.12f));
            List<DayBook> days = game.ledger.days.Skip(Mathf.Max(0, game.ledger.days.Count - 14)).ToList();
            int scale = Mathf.Max(1, days.Count == 0 ? 1 : days.Max(d => Mathf.Max(d.Income, d.Expenses)));
            float mid = plot.y + plot.height / 2f, half = plot.height / 2f - 4f, colW = plot.width / 14f;
            Widgets.DrawLineHorizontal(plot.x, mid, plot.width);
            int dayOffset = Ledger.CurrentDay - Find.TickManager.TicksGame / GenDate.TicksPerDay;
            for (int i = 0; i < days.Count; i++)
            {
                DayBook d = days[i];
                float x = plot.x + i * colW + 3f, w = colW - 6f;
                float up = half * d.Income / scale, down = half * d.Expenses / scale;
                Widgets.DrawBoxSolid(new Rect(x, mid - up, w, up), PrisonHUD.Good);
                Widgets.DrawBoxSolid(new Rect(x, mid, w, down), PrisonHUD.Bad);
                float netY = mid - half * (d.Income - d.Expenses) / scale;
                Widgets.DrawBoxSolid(new Rect(x + w / 2f - 2f, netY - 2f, 4f, 4f), Color.white);
                var col = new Rect(plot.x + i * colW, plot.y, colW, plot.height);
                TooltipHandler.TipRegion(col, "RP_FinanceDayTip".Translate(d.day - dayOffset + 1, Money.Format(d.Income), Money.Format(d.Expenses), Money.FormatSigned(d.Income - d.Expenses)));
                Text.Font = GameFont.Tiny;
                GUI.color = PrisonHUD.Muted;
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(col.x, plot.yMax + 2f, col.width, 16f), (d.day - dayOffset + 1).ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
            List<LedgerEntry> recent = game.ledger.recent;
            var view = new Rect(0f, 0f, right.width - 16f, recent.Count * 24f);
            Widgets.BeginScrollView(right, ref scroll, view);
            float ly = 0f;
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                LedgerEntry e = recent[i];
                // Fixed columns (a proportional font cannot be aligned with padding).
                var row = new Rect(0f, ly, view.width, 24f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = e.amount >= 0 ? PrisonHUD.Good : PrisonHUD.Bad;
                Widgets.Label(new Rect(row.x, row.y, 80f, row.height), Money.FormatSigned(e.amount));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 90f, row.y, 120f, row.height), e.category.LabelCap.Truncate(118f));
                float noteW = row.width - 90f - 120f - 10f - 80f;
                Widgets.Label(new Rect(row.x + 215f, row.y, noteW, row.height), (e.note ?? "").Truncate(noteW));
                TooltipHandler.TipRegion(row, e.note.NullOrEmpty() ? e.category.LabelCap.ToString() : e.category.LabelCap + ": " + e.note);
                GUI.color = PrisonHUD.Muted;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(row.xMax - 80f, row.y, 80f, row.height), Story.When(e.tick));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                ly += 24f;
            }
            Widgets.EndScrollView();
        }
    }
}
