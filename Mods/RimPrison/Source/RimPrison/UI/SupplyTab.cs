using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Supply tab: the catalogue, with orders and the auto-stock targets.</summary>
    public static class SupplyTab
    {
        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            game.supply.EnsureDefaults();
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 48f), "RP_SupplyHelp".Translate());
            var outer = new Rect(rect.x, rect.y + 52f, rect.width, rect.height - 52f);
            List<SupplyCatalogEntry> entries = SupplySystem.Catalog.entries;
            var view = new Rect(0f, 0f, outer.width - 16f, entries.Count * 32f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            foreach (SupplyCatalogEntry e in entries)
            {
                if (e.thing == null) continue;
                var row = new Rect(0f, y, view.width, 30f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.ThingIcon(new Rect(2f, y + 2f, 26f, 26f), e.thing);
                Widgets.Label(new Rect(34f, y + 5f, 200f, 26f), e.thing.LabelCap);
                int have = map.resourceCounter.GetCount(e.thing), pending = game.supply.PendingCount(e.thing);
                Widgets.Label(new Rect(240f, y + 5f, 170f, 26f), "RP_StockLine".Translate(have, pending));
                int price = SupplySystem.Price(e.thing, e.packSize);
                if (Widgets.ButtonText(new Rect(420f, y, 170f, 28f), "RP_BuyPack".Translate(e.packSize, Money.Format(price))))
                    if (game.supply.TryOrder(e.thing, e.packSize, RPDefOf.RP_Supplies)) SoundDefOf.ExecuteTrade.PlayOneShotOnCamera();
                game.supply.stockTargets.TryGetValue(e.thing, out int target);
                int effective = game.supply.EffectiveTarget(e.thing, map);
                var autoRect = new Rect(610f, y + 5f, 150f, 26f);
                Widgets.Label(autoRect, "RP_AutoStock".Translate(target) + (effective > target ? $" ({effective})" : ""));
                if (e.stockPerInmate > 0f) TooltipHandler.TipRegion(autoRect, "RP_AutoStockPerInmate".Translate(e.stockPerInmate.ToString("0.#")));
                var lessRect = new Rect(770f, y, 30f, 28f);
                var moreRect = new Rect(804f, y, 30f, 28f);
                TooltipHandler.TipRegion(lessRect, "RP_StockLessTip".Translate(e.packSize, e.thing.label));
                TooltipHandler.TipRegion(moreRect, "RP_StockMoreTip".Translate(e.packSize, e.thing.label));
                if (Widgets.ButtonText(lessRect, "-")) game.supply.stockTargets[e.thing] = Mathf.Max(0, target - e.packSize);
                if (Widgets.ButtonText(moreRect, "+")) game.supply.stockTargets[e.thing] = target + e.packSize;
                y += 32f;
            }
            Widgets.EndScrollView();
        }
    }
}
