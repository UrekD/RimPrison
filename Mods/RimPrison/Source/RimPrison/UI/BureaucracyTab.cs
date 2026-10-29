using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Bureaucracy tab: the administrators' projects.</summary>
    public static class BureaucracyTab
    {
        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            BureaucracySystem bur = game.bureaucracy;
            Concepts.Teach(Concepts.Bureaucracy);
            int admins = BureaucracySystem.Administrators(map), desks = BureaucracySystem.Desks(map);
            // (Nobody to do the paperwork: starting a project would take the money and make no progress, so the Start buttons are off and say why.)
            string stalled = BureaucracySystem.StalledReason(map);
            string help = "RP_BureaucracyHelp".Translate(admins, desks);
            if (stalled != null) help += " " + (bur.active != null ? "RP_BureaucracyStalled".Translate().Resolve() : stalled);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 44f), help);
            List<BureaucracyDef> defs = bur.All.ToList();
            var outer = new Rect(rect.x, rect.y + 48f, rect.width, rect.height - 48f);
            var view = new Rect(0f, 0f, outer.width - 16f, defs.Count * 76f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            foreach (BureaucracyDef def in defs)
            {
                var row = new Rect(0f, y, view.width, 72f);
                Widgets.DrawMenuSection(row);
                bool done = bur.IsDone(def);
                if (done) GUI.color = new Color(0.6f, 0.9f, 0.6f);
                else if (!bur.Available(def) && bur.active != def) GUI.color = Color.gray;
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(8f, y + 4f, 420f, 30f), def.LabelCap);
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(8f, y + 34f, view.width - 230f, 36f), "RP_BureaucracyUnlocks".Translate(string.Join(", ", def.Unlocks.Select(d => d.label).Concat(def.features.Select(f => ("RP_Feature_" + f).Translate().ToString())))));
                TooltipHandler.TipRegion(row, def.description);
                GUI.color = Color.white;
                var right = new Rect(view.width - 215f, y + 8f, 205f, 28f);
                if (done) Widgets.Label(right, "RP_BureaucracyDone".Translate());
                else if (bur.active == def)
                {
                    Widgets.FillableBar(right, Mathf.Clamp01(bur.progress / def.work));
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(right, "RP_BureaucracyProgress".Translate(bur.progress.ToString("F1"), def.work.ToString("F0")));
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                else if (bur.Available(def) && bur.active != null) Widgets.Label(right, "RP_BureaucracyBusy".Translate());
                else if (bur.Available(def))
                {
                    if (stalled != null)
                    {
                        Widgets.ButtonText(right, "RP_BureaucracyStart".Translate(Money.Format(def.cost)), active: false);
                        TooltipHandler.TipRegion(right, stalled);
                    }
                    else if (Widgets.ButtonText(right, "RP_BureaucracyStart".Translate(Money.Format(def.cost)))) bur.TryStart(def);
                }
                else Widgets.Label(right, "RP_LockedBy".Translate(string.Join(", ", def.prerequisites.Where(pr => !bur.IsDone(pr)).Select(pr => pr.label))));
                Widgets.Label(new Rect(view.width - 215f, y + 40f, 205f, 28f), "RP_BureaucracyWork".Translate(def.work.ToString("0")));
                y += 76f;
            }
            Widgets.EndScrollView();
        }
    }
}
