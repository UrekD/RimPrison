using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>The office's Chronicle tab: the prison's history, newest first, filtered by kind, weight and person.</summary>
    public static class ChronicleTab
    {
        private static readonly HashSet<ChronicleCategoryDef> hidden = new HashSet<ChronicleCategoryDef>();
        private static int minImportance = 1;
        private static bool selectedOnly;
        private static float lastHeight = 1000f;
        private static readonly List<ChronicleEntry> shown = new List<ChronicleEntry>();
        private static readonly Dictionary<ChronicleEntry, float> heights = new Dictionary<ChronicleEntry, float>();
        private static readonly Dictionary<int, Pawn> live = new Dictionary<int, Pawn>();
        private static float heightsWidth;

        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            var left = new Rect(rect.x, rect.y, 190f, rect.height);
            var right = new Rect(rect.x + 200f, rect.y, rect.width - 200f, rect.height);
            DrawFilters(left);
            Pawn focus = selectedOnly ? Find.Selector.SingleSelectedThing as Pawn : null;
            shown.Clear();
            List<ChronicleEntry> all = game.story.entries;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                ChronicleEntry e = all[i];
                if (e.importance < minImportance || hidden.Contains(e.cat)) continue;
                if (focus != null && !e.ids.Contains(focus.thingIDNumber)) continue;
                shown.Add(e);
            }
            var view = new Rect(0f, 0f, right.width - 16f, lastHeight);
            if (view.width != heightsWidth)
            {
                heights.Clear();
                heightsWidth = view.width;
            }
            live.Clear();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned) live[p.thingIDNumber] = p;
            Widgets.BeginScrollView(right, ref scroll, view);
            float y = 0f, top = scroll.y - 40f, bottom = scroll.y + right.height + 40f;
            int day = int.MinValue;
            if (shown.Count == 0) Widgets.Label(new Rect(0f, 0f, view.width, 30f), "RP_ChronicleEmpty".Translate());
            foreach (ChronicleEntry e in shown)
            {
                // (Everything before the game is one heading: the old warden's last weeks, see Handover.)
                int entryDay = Mathf.Max(e.Day, -1);
                if (entryDay != day)
                {
                    day = entryDay;
                    if (y >= top && y <= bottom)
                    {
                        Text.Font = GameFont.Medium;
                        Widgets.Label(new Rect(0f, y, view.width, 30f), day < 0 ? "RP_ChronicleBefore".Translate() : "RP_ChronicleDay".Translate(day + 1));
                        Text.Font = GameFont.Small;
                    }
                    y += 32f;
                }
                // Only rows in view are drawn; heights are measured once per entry and width.
                float h = RowHeight(e, view.width);
                if (y + h >= top && y <= bottom) DrawEntry(new Rect(0f, y, view.width, h), e);
                y += h;
            }
            lastHeight = Mathf.Max(y + 10f, right.height);
            Widgets.EndScrollView();
        }

        private const float Chip = 6f, HourW = 44f, JumpW = 24f;

        private static string RowText(ChronicleEntry e) => e.importance >= 3 ? "★ " + e.text : e.text;

        private static float RowHeight(ChronicleEntry e, float width)
        {
            if (heights.TryGetValue(e, out float h)) return h;
            h = Mathf.Max(24f, Text.CalcHeight(RowText(e), width - Chip - HourW - JumpW - 12f)) + 4f;
            heights[e] = h;
            return h;
        }

        private static void DrawEntry(Rect row, ChronicleEntry e)
        {
            const float chip = Chip, hourW = HourW, jumpW = JumpW;
            float textW = row.width - chip - hourW - jumpW - 12f;
            string text = RowText(e);
            float h = row.height - 4f;
            Widgets.DrawBoxSolid(new Rect(row.x, row.y + 2f, chip, h - 4f), e.cat.color);
            GUI.color = Color.gray;
            Widgets.Label(new Rect(row.x + chip + 6f, row.y, hourW, 24f), (e.Day < 0 ? e.Day + "d" : e.Hour.ToString("00") + "h"));
            GUI.color = e.importance >= 3 ? new Color(1f, 0.85f, 0.4f) : e.importance == 2 ? Color.white : new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(row.x + chip + 6f + hourW, row.y, textW, h), text);
            GUI.color = Color.white;
            Pawn who = null;
            foreach (int id in e.ids)
                if (live.TryGetValue(id, out who)) break;
            if (who != null)
            {
                var jump = new Rect(row.xMax - jumpW, row.y, jumpW, 24f);
                if (Widgets.ButtonImage(jump, TexButton.Search)) CameraJumper.TryJumpAndSelect(who);
                TooltipHandler.TipRegion(jump, "RP_ChronicleJump".Translate(who.LabelShort));
            }
        }

        private static void DrawFilters(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);
            list.Label("RP_ChronicleShow".Translate());
            if (list.RadioButton("RP_ChronicleAll".Translate(), minImportance == 1)) minImportance = 1;
            if (list.RadioButton("RP_ChronicleNotable".Translate(), minImportance == 2)) minImportance = 2;
            if (list.RadioButton("RP_ChronicleMilestones".Translate(), minImportance == 3)) minImportance = 3;
            list.CheckboxLabeled("RP_ChronicleSelected".Translate(), ref selectedOnly, "RP_ChronicleSelectedDesc".Translate());
            list.GapLine();
            foreach (ChronicleCategoryDef cat in DefDatabase<ChronicleCategoryDef>.AllDefsListForReading)
            {
                bool on = !hidden.Contains(cat);
                bool was = on;
                Rect r = list.GetRect(24f);
                Widgets.DrawBoxSolid(new Rect(r.x, r.y + 5f, 10f, 14f), cat.color);
                var box = new Rect(r.x + 16f, r.y, r.width - 16f, r.height);
                Widgets.CheckboxLabeled(box, cat.LabelCap, ref on);
                TooltipHandler.TipRegion(box, "RP_ChronicleCategoryTip".Translate(cat.label));
                if (on == was) continue;
                if (on) hidden.Remove(cat);
                else hidden.Add(cat);
            }
            list.End();
        }

        /// <summary>The latest notable entries (Overview).</summary>
        public static IEnumerable<ChronicleEntry> Latest(PrisonGame game, int count)
        {
            List<ChronicleEntry> all = game.story.entries;
            for (int i = all.Count - 1; i >= 0 && count > 0; i--)
                if (all[i].importance >= 2)
                {
                    count--;
                    yield return all[i];
                }
        }
    }

    /// <summary>A member of staff's service record: role, standing, internal affairs, and their history here.</summary>
    public class ITab_StaffRecord : ITab
    {
        private Vector2 scroll;
        private float lastHeight = 400f;

        public ITab_StaffRecord()
        {
            size = new Vector2(420f, 420f);
            labelKey = "RP_TabStaffRecord";
        }

        private Pawn Staff => SelPawn is { } p && p.Faction == Faction.OfPlayer && p.GetComp<CompStaff>()?.role != null ? p : null;

        public override bool IsVisible => Staff != null;

        protected override void FillTab()
        {
            Pawn p = Staff;
            if (p == null) return;
            CompStaff sc = p.GetComp<CompStaff>();
            Rect outer = new Rect(0f, 0f, size.x, size.y).ContractedBy(12f);
            var view = new Rect(0f, 0f, outer.width - 16f, lastHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            // (One column, as the Intake tab's: the view is last frame's height, and a wrapped listing would report only its last column's.)
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(view);
            Text.Font = GameFont.Medium;
            list.Label(p.LabelShortCap);
            Text.Font = GameFont.Small;
            list.Label("RP_StaffRecordRole".Translate(StaffCareers.TitleCap(sc), Money.Format(StaffCareers.Wage(sc)),
                sc.hiredTick == CompStaff.NotHired ? "?" : ((Find.TickManager.TicksGame - sc.hiredTick) / (float)GenDate.TicksPerDay).ToString("0.0")));
            string status = sc.StatusLines();
            if (GuardUtility.IsGuard(p) && StaffMorale.StatusLine(p) is { } morale) status += "\n" + morale;
            if (StaffWariness.StatusLine(sc) is { } wary) status += "\n" + wary;
            status += "\n" + PostLine(p, sc);
            (int hatedBy, int trustedBy) = StaffMemories.Standing(p);
            if (hatedBy + trustedBy > 0) status += "\n" + "RP_StaffStanding".Translate(hatedBy, trustedBy);
            if (status.Length > 0) list.Label(status.TrimStart('\n'));
            // A training course: what it improves, or why not now.
            if (StaffTraining.SkillFor(sc.role) is { } skill)
            {
                string why = StaffTraining.CannotGo(p, sc);
                Rect row = list.GetRect(28f);
                if (why == null)
                {
                    if (Widgets.ButtonText(row.LeftPart(0.62f), "RP_TrainingButton".Translate(skill.label, Money.Format(StaffTraining.Cost)))) StaffTraining.Send(p, sc);
                    TooltipHandler.TipRegion(row, "RP_TrainingTip".Translate(skill.label, StaffTraining.Levels, StaffTraining.Days, StaffTraining.CooldownDays));
                }
                else
                {
                    GUI.color = PrisonHUD.Muted;
                    Widgets.Label(row, "RP_TrainingUnavailable".Translate(skill.label, why));
                    GUI.color = Color.white;
                }
            }
            list.GapLine();
            StaffLifeSection(list, sc.life);
            lastHeight = list.CurHeight + 12f;
            list.End();
            Widgets.EndScrollView();
        }

        /// <summary>Shift, post and where they are: on site, home, on leave, on a course, suspended.</summary>
        private static string PostLine(Pawn p, CompStaff sc)
        {
            int now = Find.TickManager.TicksGame;
            string where = CorruptionSystem.Suspended(sc) ? (CorruptionSystem.StoodDownOnly(sc) ? "RP_StaffWhereStoodDown" : "RP_StaffWhereSuspended").Translate().ToString()
                : sc.trainingPending ? "RP_StaffWhereCourse".Translate().ToString()
                : StaffLives.OnLeave(sc) ? "RP_StaffWhereLeave".Translate(((sc.leaveUntilTick - now) / (float)GenDate.TicksPerDay).ToString("0.0")).ToString()
                : p.Spawned ? "RP_StaffWhereOnSite".Translate().ToString() : "RP_StaffWhereHome".Translate().ToString();
            string line = "RP_StaffPost".Translate((sc.nightShift ? "RP_ShiftNight" : "RP_ShiftDay").Translate(), where);
            if (GuardUtility.IsGuard(p)) line += " " + "RP_StaffPatrol".Translate(sc.patrolZone?.label ?? "RP_StaffPatrolAnywhere".Translate().ToString());
            if (sc.dog != null) line += " " + "RP_StaffDog".Translate(sc.dog.LabelShort);
            return line;
        }

        public static void StaffLifeSection(Listing_Standard list, List<LifeEntry> life, string headerKey = "RP_StaffRecordHistory")
        {
            list.Label(headerKey.Translate());
            if (life.Count == 0) list.Label("   " + "RP_FileNone".Translate());
            for (int i = life.Count - 1; i >= 0; i--)
                list.Label("   " + Story.When(life[i].tick) + ": " + life[i].text);
        }
    }
}
