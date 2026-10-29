using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Office overview panel: average inmate needs (with how many are low) and the most common complaints, so the
    /// warden can see what is driving tension without clicking through inmates. Recomputed every couple of seconds.
    /// </summary>
    public static class NeedsReport
    {
        private class NeedRow
        {
            public NeedDef def;
            public float sum;
            public int count, low;
        }

        private static readonly List<NeedRow> needs = new List<NeedRow>();
        private static readonly List<(string label, float offset, int count)> complaints = new List<(string, float, int)>();
        private static int computedFrame = -9999;
        private static readonly List<Thought> tmpThoughts = new List<Thought>();

        private static void Compute(Map map)
        {
            computedFrame = Time.frameCount;
            needs.Clear();
            complaints.Clear();
            var byDef = new Dictionary<NeedDef, NeedRow>();
            var byThought = new Dictionary<string, (float offset, int count)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || p.needs == null) continue;
                foreach (Need n in p.needs.AllNeeds)
                {
                    // Addictions belong to individual inmates, not to the prison's services.
                    if (n is Need_Mood || n is Need_Chemical || n is Need_Chemical_Any || !n.def.showOnNeedList) continue;
                    if (!byDef.TryGetValue(n.def, out NeedRow row)) byDef[n.def] = row = new NeedRow { def = n.def };
                    row.sum += n.CurLevelPercentage;
                    row.count++;
                    if (n.CurLevelPercentage < 0.3f) row.low++;
                }
                if (p.needs.mood == null) continue;
                tmpThoughts.Clear();
                p.needs.mood.thoughts.GetDistinctMoodThoughtGroups(tmpThoughts);
                foreach (Thought t in tmpThoughts)
                {
                    float o = p.needs.mood.thoughts.MoodOffsetOfGroup(t);
                    if (o >= 0f) continue;
                    string label = t.LabelCap;
                    byThought.TryGetValue(label, out var acc);
                    byThought[label] = (acc.offset + o, acc.count + 1);
                }
            }
            needs.AddRange(byDef.Values.OrderBy(r => r.def.listPriority * -1));
            complaints.AddRange(byThought.OrderBy(kv => kv.Value.offset).Take(7).Select(kv => (kv.Key, kv.Value.offset, kv.Value.count)));
        }

        /// <summary>Two columns: needs (worst first, a tick at the 30% "low" line) and complaints as bars.</summary>
        public static void Draw(Rect rect, Map map)
        {
            if (Time.frameCount - computedFrame > 120) Compute(map);
            var left = new Rect(rect.x, rect.y, rect.width / 2f - 10f, rect.height);
            var right = new Rect(rect.x + rect.width / 2f + 10f, rect.y, rect.width / 2f - 10f, rect.height);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(left.x, left.y, left.width, 30f), "RP_NeedsReport".Translate());
            Widgets.Label(new Rect(right.x, right.y, right.width, 30f), "RP_TopComplaints".Translate());
            Text.Font = GameFont.Small;
            float y = left.y + 32f;
            if (needs.Count == 0) Widgets.Label(new Rect(left.x, y, left.width, 22f), "RP_NoInmates".Translate());
            foreach (NeedRow row in needs.OrderBy(n => n.sum / Mathf.Max(1, n.count)))
            {
                if (y + 18f > left.yMax) break;
                var r = new Rect(left.x, y, left.width, 18f);
                float avg = row.sum / Mathf.Max(1, row.count);
                Widgets.Label(new Rect(r.x, r.y - 2f, 130f, 22f), row.def.LabelCap.Truncate(128f));
                var bar = new Rect(r.x + 134f, r.y + 2f, r.width - 134f - 90f, 14f);
                Widgets.FillableBar(bar, avg, avg < 0.3f ? BarLow : avg < 0.5f ? BarMid : BarOk);
                Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * 0.3f, bar.y, 1f, bar.height), Color.white);
                GUI.color = row.low > 0 ? PrisonHUD.Bad : PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(bar.xMax + 6f, r.y, 84f, 18f), row.low > 0 ? "RP_NeedsLow".Translate(avg.ToStringPercent(), row.low).ToString() : avg.ToStringPercent());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(r, "RP_NeedsReportTip".Translate(row.def.LabelCap, row.count, row.low));
                y += 20f;
            }
            y = right.y + 32f;
            float worst = complaints.Count > 0 ? complaints.Max(c => Mathf.Abs(c.offset)) : 1f;
            foreach ((string label, float offset, int count) in complaints)
            {
                if (y + 18f > right.yMax) break;
                Widgets.Label(new Rect(right.x, y - 2f, 170f, 22f), label.Truncate(168f));
                var bar = new Rect(right.x + 174f, y + 2f, right.width - 174f - 70f, 14f);
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * Mathf.Abs(offset) / Mathf.Max(1f, worst), bar.height), PrisonHUD.Bad);
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(bar.xMax + 6f, y, 64f, 18f), $"{offset:F0} ({count})");
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 20f;
            }
        }

        private static readonly Texture2D BarLow = SolidColorMaterials.NewSolidColorTexture(new Color(0.75f, 0.25f, 0.2f));
        private static readonly Texture2D BarMid = SolidColorMaterials.NewSolidColorTexture(new Color(0.8f, 0.65f, 0.25f));
        private static readonly Texture2D BarOk = SolidColorMaterials.NewSolidColorTexture(new Color(0.35f, 0.65f, 0.35f));
    }
}
