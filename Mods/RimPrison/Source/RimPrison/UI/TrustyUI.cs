using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The trusties' block of the Deployment tab (<see cref="Trusties"/>): whether the outside work party is out (or why not), the guard ratio the warden sets, the guards near the zone, the gates that open to it and the
    /// trusties with what they do.
    /// </summary>
    public static class TrustyUI
    {
        /// <summary>Where the block began in the left column the last time it was drawn (the tour scrolls to it).</summary>
        public static float blockY;

        /// <summary>Set by the tour: the next time the block is drawn the left column is scrolled to it.</summary>
        public static bool scrollToBlock;

        /// <summary>The most trusties the block lists (the rest are counted).</summary>
        public const int ListedMax = 8;

        private static readonly List<Pawn> listed = new List<Pawn>();
        private static int listFrame = -15, listMapId = -1;

        public static void Draw(Rect left, ref float y, Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            TrustyParty party = pm.party;
            blockY = y;
            if (scrollToBlock)
            {
                scrollToBlock = false;
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, y - 20f));
            }
            DeploymentTab.Header(left.x, ref y, left.width, "RP_DeployTrusties");
            TooltipHandler.TipRegion(new Rect(left.x, y - 32f, left.width, 30f), "RP_DeployTrustiesTip".Translate());
            // (The trusties in order: the window asks on every GUI event.)
            if (Time.frameCount - listFrame >= 15 || Time.frameCount < listFrame || listMapId != map.uniqueID)
            {
                listFrame = Time.frameCount;
                listMapId = map.uniqueID;
                listed.Clear();
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { IsTrusty: true }) listed.Add(p);
                listed.Sort((a, b) => a.Inmate().trustySinceTick.CompareTo(b.Inmate().trustySinceTick));
            }
            bool anyZone = pm.zones.Any(RPTrustyDefOf.RP_OutsideWork);
            if (listed.Count == 0 && !anyZone)
            {
                Note(left, ref y, "RP_DeployTrustiesNone".Translate(), PrisonHUD.Muted, 50f);
                y += 10f;
                return;
            }
            // The party: out, or why not.
            string line = Trusties.StateText(pm);
            Note(left, ref y, line, party.isOut ? PrisonHUD.Good : party.state == PartyState.Idle ? PrisonHUD.Muted : PrisonHUD.Warn, 22f);
            // The ratio: one guard near the zone for this many trusties on the job.
            var ratioRow = new Rect(left.x, y, left.width, 26f);
            TooltipHandler.TipRegion(ratioRow, "RP_DeployTrustyRatioTip".Translate());
            // (The buttons follow the text, so they sit clear of it however long the translation is; the text is cut to what is left of the row.)
            string ratioText = "RP_DeployTrustyRatio".Translate(party.ratio).Resolve();
            float textWidth = Mathf.Min(Text.CalcSize(ratioText).x + 4f, ratioRow.width - 80f);
            Widgets.Label(new Rect(ratioRow.x, ratioRow.y + 3f, textWidth, 22f), ratioText.Truncate(textWidth));
            if (Widgets.ButtonText(new Rect(ratioRow.x + textWidth + 8f, ratioRow.y, 30f, 24f), "-") && party.ratio > Trusties.MinRatio) party.ratio--;
            if (Widgets.ButtonText(new Rect(ratioRow.x + textWidth + 42f, ratioRow.y, 30f, 24f), "+") && party.ratio < Trusties.MaxRatio) party.ratio++;
            y += 28f;
            Note(left, ref y, "RP_DeployTrustyGuards".Translate(Trusties.GuardCount(party.near), Trusties.TrustyCount(party.holders), Trusties.GuardCount(party.needed)), PrisonHUD.Muted, 20f, tiny: true);
            if (party.hours > 0) Note(left, ref y, "RP_DeployTrustyContract".Translate(party.hours, Money.Format(party.hours * Trusties.PayPerHour)), PrisonHUD.Muted, 20f, tiny: true);
            // The gates: the doors between the minimum wing's space and the zone, in the mode they are set (only an Auto one opens to the party; a locked one stays locked for everyone).
            int gates = 0;
            foreach (CompPrisonDoor d in pm.Doors)
            {
                if (!d.trustyGate || !d.Door.Spawned) continue;
                gates++;
                var row = new Rect(left.x, y, left.width, 24f);
                Widgets.DrawLightHighlight(row);
                GUI.color = DeploymentTab.ModeColor(d.mode);
                Widgets.Label(new Rect(row.x + 4f, row.y + 2f, row.width - 34f, 22f), "RP_DeployTrustyGate".Translate(d.Door.Position.x, d.Door.Position.z, ("RP_DoorMode_" + d.mode).Translate()).Resolve().Truncate(row.width - 36f));
                GUI.color = Color.white;
                if (Widgets.ButtonImage(new Rect(row.xMax - 26f, row.y, 24f, 24f), TexButton.Search)) CameraJumper.TryJumpAndSelect(d.Door);
                y += 26f;
                if (d.mode is PrisonDoorMode.Locked or PrisonDoorMode.StaffOnly) Note(left, ref y, "RP_DeployTrustyGateShut".Translate(("RP_DoorMode_" + d.mode).Translate()), PrisonHUD.Warn, 34f, tiny: true);
            }
            if (gates == 0) Note(left, ref y, "RP_DeployTrustyNoGate".Translate(), PrisonHUD.Warn, 34f, tiny: true);
            // The trusties.
            for (int i = 0; i < listed.Count && i < ListedMax; i++)
            {
                Pawn p = listed[i];
                CompInmate c = p.Inmate();
                if (c == null) continue;
                var row = new Rect(left.x, y, left.width, 24f);
                string job = c.job == RPTrustyDefOf.RP_JobOutside ? (Trusties.IsWorkingOutside(p) ? "RP_DeployTrustyWorking" : p.Spawned && pm.IsOutsideSpace(p.Position) ? "RP_DeployTrustyOutside" : "RP_DeployTrustyInside").Translate().Resolve() : "RP_DeployTrustyNoJob".Translate().Resolve();
                Widgets.Label(new Rect(row.x + 4f, row.y + 2f, row.width - 34f, 22f), "RP_DeployTrustyRow".Translate(p.LabelShortCap, job).Resolve().Truncate(row.width - 36f));
                if (Widgets.ButtonImage(new Rect(row.xMax - 26f, row.y, 24f, 24f), TexButton.Search)) CameraJumper.TryJumpAndSelect(p);
                y += 26f;
            }
            if (listed.Count > ListedMax) Note(left, ref y, "RP_DeployTrustyMore".Translate(listed.Count - ListedMax), PrisonHUD.Muted, 20f, tiny: true);
            y += 10f;
        }

        private static void Note(Rect left, ref float y, string text, Color color, float height, bool tiny = false)
        {
            GUI.color = color;
            if (tiny) Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(left.x, y, left.width, height), text);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += height + 2f;
        }
    }
}
