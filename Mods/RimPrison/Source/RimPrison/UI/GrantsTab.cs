using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Grants tab: the state's grants on offer and under way.</summary>
    public static class GrantsTab
    {
        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            var rows = new List<(GrantDef def, GrantState state)>();
            foreach (GrantState s in game.grants.active) rows.Add((s.def, s));
            foreach (GrantDef d in game.grants.Available) rows.Add((d, null));
            float h = 0f;
            foreach ((GrantDef def, GrantState state) r in rows) h += 70f + (r.state != null ? 22f * r.def.conditions.Count : 0f);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 40f), "RP_GrantsHelp".Translate(game.grants.completed.Count));
            var outer = new Rect(rect.x, rect.y + 44f, rect.width, rect.height - 44f);
            var view = new Rect(0f, 0f, outer.width - 16f, h);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            foreach ((GrantDef def, GrantState state) in rows)
            {
                float rowH = 66f + (state != null ? 22f * def.conditions.Count : 0f);
                var row = new Rect(0f, y, view.width, rowH);
                Widgets.DrawMenuSection(row);
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(8f, y + 4f, 500f, 30f), def.LabelCap);
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(8f, y + 34f, view.width - 220f, 30f), def.description);
                Widgets.Label(new Rect(view.width - 205f, y + 8f, 200f, 24f), "RP_GrantMoney".Translate(Money.Format(def.advance), Money.Format(SpecialMeasures.GrantReward(def.reward))));
                if (state == null)
                {
                    var acceptRect = new Rect(view.width - 140f, y + 34f, 130f, 28f);
                    TooltipHandler.TipRegion(acceptRect, "RP_GrantAcceptTip".Translate(Money.Format(def.advance), Money.Format(SpecialMeasures.GrantReward(def.reward))));
                    if (Widgets.ButtonText(acceptRect, "RP_GrantAccept".Translate())) game.grants.Accept(def, map);
                }
                else
                {
                    var abandonRect = new Rect(view.width - 140f, y + 34f, 130f, 28f);
                    TooltipHandler.TipRegion(abandonRect, "RP_GrantAbandonTip".Translate(Money.Format(def.advance)));
                    if (Widgets.ButtonText(abandonRect, "RP_GrantAbandon".Translate()))
                    {
                        GrantState dropped = state;
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_GrantAbandonConfirm".Translate(def.LabelCap, Money.Format(def.advance)),
                            () => game.grants.Drop(dropped, failed: false), destructive: true));
                    }
                    float cy = y + 64f;
                    foreach (GrantCondition c in def.conditions)
                    {
                        bool met = c.Met(map, game, state);
                        GUI.color = met ? new Color(0.5f, 0.9f, 0.5f) : Color.white;
                        Widgets.Label(new Rect(24f, cy, view.width - 30f, 22f), (met ? "✓ " : "• ") + c.Progress(map, game, state));
                        GUI.color = Color.white;
                        cy += 22f;
                    }
                }
                y += rowH + 4f;
            }
            Widgets.EndScrollView();
        }
    }
}
