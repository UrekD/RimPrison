using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The Land section of the Office screen: the grounds as a small plan of the plot, a parcel to buy along each side (its size, price and why it cannot be bought now; pointing at one shows it on
    /// the map), and in dev mode the sides moved for nothing. The rules are <see cref="PrisonGrounds"/>.
    /// </summary>
    public static class LandTab
    {
        public static readonly GroundsSide[] Sides = { GroundsSide.North, GroundsSide.East, GroundsSide.South, GroundsSide.West };

        private const float PlanSize = 330f, RowHeight = 62f;
        private static readonly Color PlotColor = new Color(0.09f, 0.1f, 0.12f), OwnedColor = new Color(1f, 0.78f, 0.2f, 0.45f), ParcelColor = new Color(0.4f, 0.85f, 0.45f, 0.7f);

        public static void Draw(Rect rect, PrisonGame game, Map map)
        {
            PrisonGrounds g = PrisonGrounds.Of(map);
            if (g == null) return;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 44f), "RP_LandHelp".Translate(PrisonGrounds.ParcelDepth));
            float top = rect.y + 50f;
            var plan = new Rect(rect.x, top, PlanSize, PlanSize);
            float rightX = plan.xMax + 24f;
            var right = new Rect(rightX, top, rect.xMax - rightX, rect.height - 50f);

            // Right: a parcel for each side.
            GroundsSide? hovered = null;
            float y = right.y;
            foreach (GroundsSide side in Sides)
            {
                var row = new Rect(right.x, y, right.width, RowHeight);
                if (DoParcel(row, g, side)) hovered = side;
                y += RowHeight + 4f;
            }
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(right.x, y, right.width, 34f), "RP_LandPriceNote".Translate(g.LandFactor.ToStringPercent("F0")));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 36f;
            if (Prefs.DevMode) DoDev(new Rect(right.x, y, right.width, right.yMax - y), g);

            // Left: the plan, and what it says.
            DoPlan(plan, g, map, hovered);
            float ly = plan.yMax + 6f;
            Widgets.Label(new Rect(plan.x, ly, plan.width, 44f), g.Limited ? "RP_LandSize".Translate(g.Rect.Width, g.Rect.Height, g.Area.ToString("N0"), ((float)g.Area / (map.Size.x * map.Size.z)).ToStringPercent("F0")) : "RP_LandWholeMapSize".Translate(map.Size.x, map.Size.z));
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(plan.x, ly + 46f, plan.width, 22f), "RP_LandBoughtSoFar".Translate(game.stats.landParcels, game.stats.landCells.ToString("N0")));
            GUI.color = Color.white;
        }

        /// <summary>One side's parcel: what it is, what it costs and the button; true while it is pointed at.</summary>
        private static bool DoParcel(Rect row, PrisonGrounds g, GroundsSide side)
        {
            Widgets.DrawMenuSection(row);
            Parcel p = g.ParcelFor(side);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(row.x + 8f, row.y + 4f, 200f, 30f), ("RP_Side_" + side).Translate().CapitalizeFirst());
            Text.Font = GameFont.Small;
            string detail = p.Exists ? "RP_LandParcelSize".Translate(p.cells.Width, p.cells.Height, p.cells.Area.ToString("N0"), Money.Format(p.price)) : "RP_LandNoParcel".Translate();
            Widgets.Label(new Rect(row.x + 8f, row.y + 34f, row.width - 230f, 26f), detail);
            var buy = new Rect(row.xMax - 210f, row.y + 8f, 200f, 28f);
            bool can = p.Exists && p.blocked == null;
            if (p.Exists)
            {
                if (Widgets.ButtonText(buy, "RP_LandBuy".Translate(Money.Format(p.price)), active: can) && can) g.TryBuy(side);
                TooltipHandler.TipRegion(buy, p.blocked ?? "RP_LandBuyTip".Translate(p.cells.Area.ToString("N0"), Money.Format(p.price), ("RP_Side_" + side).Translate()).Resolve());
            }
            if (p.blocked != null)
            {
                GUI.color = PrisonHUD.Warn;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(row.xMax - 330f, row.y + 38f, 322f, 22f), p.blocked.Truncate(322f));
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            bool over = Mouse.IsOver(row);
            if (over && p.Exists) GroundsOverlay.ShowPreview(p.cells);
            return over && p.Exists;
        }

        /// <summary>The plot as a small plan: the map, the land the grounds may reach, the grounds, and the parcel pointed at.</summary>
        private static void DoPlan(Rect plan, PrisonGrounds g, Map map, GroundsSide? hovered)
        {
            Widgets.DrawBoxSolid(plan, PlotColor);
            Widgets.DrawBox(plan);
            Rect Cells(CellRect r) => new Rect(plan.x + r.minX / (float)map.Size.x * plan.width, plan.yMax - (r.maxZ + 1) / (float)map.Size.z * plan.height,
                r.Width / (float)map.Size.x * plan.width, r.Height / (float)map.Size.z * plan.height);
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            Widgets.DrawBox(Cells(g.Whole));
            GUI.color = Color.white;
            if (g.Limited)
            {
                Rect owned = Cells(g.Rect);
                Widgets.DrawBoxSolid(owned, OwnedColor);
                Widgets.DrawBoxSolidWithOutline(owned, Color.clear, new Color(1f, 0.78f, 0.2f), 2);
                if (hovered is { } side && g.ParcelFor(side) is { Exists: true } parcel) Widgets.DrawBoxSolid(Cells(parcel.cells), ParcelColor);
            }
            else Widgets.DrawBoxSolid(plan.ContractedBy(1f), OwnedColor);
            Text.Font = GameFont.Tiny;
            GUI.color = PrisonHUD.Muted;
            Text.Anchor = TextAnchor.UpperCenter;
            Widgets.Label(new Rect(plan.x, plan.y + 2f, plan.width, 18f), ("RP_Side_North").Translate().CapitalizeFirst());
            Text.Anchor = TextAnchor.LowerCenter;
            Widgets.Label(new Rect(plan.x, plan.yMax - 20f, plan.width, 18f), ("RP_Side_South").Translate().CapitalizeFirst());
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        /// <summary>Dev mode: each side moved by 1, 5 or 10 for nothing, the whole map, the starting grounds. The plan and the map follow as a button is pressed.</summary>
        private static void DoDev(Rect rect, PrisonGrounds g)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(8f);
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 34f), "RP_LandDev".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            float y = inner.y + 36f;
            int[] steps = { -10, -5, -1, 1, 5, 10 };
            foreach (GroundsSide side in Sides)
            {
                Widgets.Label(new Rect(inner.x, y + 2f, 80f, 24f), ("RP_Side_" + side).Translate().CapitalizeFirst());
                for (int i = 0; i < steps.Length; i++)
                {
                    var b = new Rect(inner.x + 90f + i * 58f, y, 54f, 24f);
                    if (Widgets.ButtonText(b, steps[i] > 0 ? "+" + steps[i] : steps[i].ToString())) g.MoveSide(side, steps[i]);
                }
                y += 28f;
            }
            if (Widgets.ButtonText(new Rect(inner.x, y + 2f, 170f, 26f), "RP_LandWholeMapButton".Translate())) g.SetWholeMap();
            if (Widgets.ButtonText(new Rect(inner.x + 180f, y + 2f, 190f, 26f), "RP_LandStartingButton".Translate())) g.SetBackToStart();
        }
    }
}
