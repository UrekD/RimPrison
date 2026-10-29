using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The window of a program's Book button: each of its bookings as a row of the 24 hours (click to book or free one), the room it is booked in, its priority, and what each hour looks like - whether inmates who could attend
    /// have the program's slot in it, and whether another booked class wants the same room or teacher. What is shown is worked out when a booking is edited and when the hour turns, not every frame.
    /// </summary>
    public class Dialog_ClassBooking : Window
    {
        private readonly ProgramDef prog;
        private readonly Map map;
        private readonly string title, info, help;
        private float helpHeight = -1f;
        private Vector2 scroll;
        // What is shown, and what it was worked out at.
        private int shownVersion = -1, shownStamp = -1;
        private ClassHourInfo[] hours;
        private readonly Dictionary<ClassBooking, string> roomNotes = new Dictionary<ClassBooking, string>();
        private readonly List<ClassBooking> mine = new List<ClassBooking>();
        private readonly List<(IntVec3 anchor, int places)> venues = new List<(IntVec3, int)>();

        private const float HourH = 26f, BlockGap = 12f;
        private static readonly Color Open = new Color(0.17f, 0.30f, 0.20f), Blocked = new Color(0.36f, 0.17f, 0.17f), Idle = new Color(0.16f, 0.16f, 0.18f), Booked = new Color(0.25f, 0.62f, 0.34f), Held = new Color(0.10f, 0.10f, 0.11f);

        public Dialog_ClassBooking(ProgramDef prog, Map map)
        {
            this.prog = prog;
            this.map = map;
            title = "RP_BookTitle".Translate(prog.LabelCap);
            info = "RP_BookInfo".Translate(prog.TeacherLabel, prog.Zone.label, prog.Slot.label);
            help = "RP_BookHelp".Translate();
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize => new Vector2(740f, 560f);

        private static string Cell(IntVec3 c) => $"{c.x}, {c.z}";

        /// <summary>The numbers behind the window, again when a booking changed or the game hour moved on (inmates come and go and the regime may be edited, but not every frame).</summary>
        private void Refresh(PrisonMap pm, PrisonGame game)
        {
            int stamp = Find.TickManager.TicksGame / GenDate.TicksPerHour;
            if (shownVersion == pm.bookings.version && shownStamp == stamp) return;
            shownVersion = pm.bookings.version;
            shownStamp = stamp;
            hours = ClassBookings.Analyse(map, pm, game, prog);
            pm.bookings.For(prog, mine);
            var eligible = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && prog.Eligible(p, c)) eligible.Add(p);
            roomNotes.Clear();
            foreach (ClassBooking b in mine)
            {
                IntVec3? at = b.anchor.IsValid ? b.anchor : null;
                int admits = ProgramSystem.VenueAdmits(map, pm, prog, at, eligible, out int places, out bool exists);
                roomNotes[b] = !exists ? (at != null ? "RP_BookRoomGone".Translate(Cell(b.anchor)) : "RP_BookRoomNoteNone".Translate()).ToString()
                    : "RP_BookRoomNote".Translate(places, admits, eligible.Count).ToString();
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            PrisonMap pm = PrisonMap.Of(map);
            PrisonGame game = PrisonGame.Instance;
            if (pm == null || game == null || !Find.Maps.Contains(map))
            {
                Close();
                return;
            }
            Refresh(pm, game);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), title);
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(0f, 34f, inRect.width, 24f), info);
            if (helpHeight < 0f) helpHeight = Text.CalcHeight(help, inRect.width);
            Widgets.Label(new Rect(0f, 58f, inRect.width, helpHeight), help);
            GUI.color = Color.white;
            float top = 64f + helpHeight;
            var addRect = new Rect(0f, inRect.height - 30f, 180f, 28f);
            bool full = (pm.bookings.TakenHours(prog) & ClassBookings.AllHours) == ClassBookings.AllHours;
            if (Widgets.ButtonText(addRect, full ? "RP_BookNewFull".Translate() : "RP_BookNew".Translate(), active: !full)) AddBooking(pm);
            var list = new Rect(0f, top, inRect.width, inRect.height - top - 36f);
            float blockH = 28f + 18f + HourH + 4f + 18f + 18f + BlockGap;
            var view = new Rect(0f, 0f, list.width - 16f, mine.Count * blockH);
            Widgets.BeginScrollView(list, ref scroll, view);
            float y = 0f;
            // (A booking edited away is taken out of what is shown on the next pass of Refresh; this frame draws what it had.)
            for (int i = 0; i < mine.Count; i++)
            {
                DrawBooking(pm, mine[i], new Rect(0f, y, view.width, blockH - BlockGap));
                y += blockH;
            }
            Widgets.EndScrollView();
        }

        private void AddBooking(PrisonMap pm)
        {
            // The first free hour after this one, so that a new booking is for something still to come today.
            int now = GenLocalDate.HourOfDay(map), taken = pm.bookings.TakenHours(prog);
            for (int i = 1; i <= 24; i++)
            {
                int h = (now + i) % 24;
                if ((taken >> h & 1) != 0) continue;
                pm.bookings.Add(prog, 1 << h);
                return;
            }
        }

        private void DrawBooking(PrisonMap pm, ClassBooking b, Rect block)
        {
            ClassBookings bk = pm.bookings;
            // Header: the room, the priority, remove.
            var head = new Rect(block.x, block.y, block.width, 28f);
            IntVec3? at = b.anchor.IsValid ? b.anchor : null;
            string roomLabel = at == null ? "RP_BookAnyRoom".Translate(prog.Zone.label).ToString() : "RP_BookRoomAt".Translate(Cell(b.anchor)).ToString();
            if (Widgets.ButtonText(new Rect(head.x, head.y, 300f, 26f), roomLabel.Truncate(296f)))
                Find.WindowStack.Add(new FloatMenu(RoomOptions(bk, b)));
            if (Widgets.ButtonText(new Rect(head.x + 310f, head.y, 26f, 26f), "-")) bk.SetPriority(b, b.priority - 1);
            var priority = new Rect(head.x + 338f, head.y, 100f, 26f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(priority, "RP_BookPriority".Translate(b.priority));
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(priority, "RP_BookPriorityTip".Translate());
            if (Widgets.ButtonText(new Rect(head.x + 440f, head.y, 26f, 26f), "+")) bk.SetPriority(b, b.priority + 1);
            if (Widgets.ButtonText(new Rect(head.xMax - 100f, head.y, 100f, 26f), "RP_BookRemove".Translate()))
            {
                bk.Remove(b);
                return;
            }
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(block.x, block.y + 28f, block.width, 18f), roomNotes.TryGetValue(b, out string note) ? note : "");
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            // The 24 hours.
            float cellW = block.width / 24f, gridY = block.y + 28f + 18f;
            int nowHour = GenLocalDate.HourOfDay(map), held = bk.TakenHours(prog, b);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            for (int h = 0; h < 24; h++)
            {
                var cell = new Rect(block.x + h * cellW, gridY, cellW - 1f, HourH);
                ClassHourInfo hi = hours[h];
                bool mineHere = b.Covers(h), other = (held >> h & 1) != 0;
                Widgets.DrawBoxSolid(cell, mineHere ? Booked : other ? Held : hi.eligible == 0 ? Idle : hi.attendable > 0 ? Open : Blocked);
                if (hi.clash != null) Widgets.DrawBoxSolid(new Rect(cell.x, cell.y, cell.width, 3f), PrisonHUD.Warn);
                if (h == nowHour) Widgets.DrawBox(cell, 1);
                GUI.color = other ? PrisonHUD.Muted : Color.white;
                Widgets.Label(cell, h.ToString());
                GUI.color = Color.white;
                if (Mouse.IsOver(cell)) TooltipHandler.TipRegion(cell, new TipSignal(HourTip(h, hi, other, h == nowHour), b.id * 100 + h + 700000));
                if (Widgets.ButtonInvisible(cell)) bk.Toggle(b, h);
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            // Where it has missed.
            float lineY = gridY + HourH + 4f;
            Text.Font = GameFont.Tiny;
            if (b.Warns)
            {
                GUI.color = PrisonHUD.Warn;
                Widgets.Label(new Rect(block.x, lineY, block.width, 18f), "RP_BookWarn".Translate(b.missStreak));
                lineY += 18f;
            }
            if (b.LastMiss is { } miss)
            {
                GUI.color = PrisonHUD.Muted;
                var missRect = new Rect(block.x, lineY, block.width, 18f);
                Widgets.Label(missRect, ClassBookings.MissText(prog, miss));
                TooltipHandler.TipRegion(missRect, "RP_BookMissTip".Translate() + "\n" + string.Join("\n", b.misses.Select(m => ClassBookings.MissText(prog, m))));
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private string HourTip(int hour, ClassHourInfo hi, bool heldByOther, bool thisHour)
        {
            string hourLabel = ClassBookings.HourLabel(hour);
            string tip;
            if (heldByOther) tip = "RP_BookHourHeld".Translate(hourLabel);
            else if (hi.eligible == 0) tip = "RP_BookHourNobody".Translate(hourLabel);
            else if (hi.attendable > 0) tip = "RP_BookHourSome".Translate(hourLabel, hi.attendable, hi.eligible, prog.Slot.label);
            else tip = "RP_BookHourNone".Translate(hourLabel, hi.eligible, prog.Slot.label, hi.instead?.label ?? "-");
            if (hi.clash != null) tip += "\n" + "RP_BookHourClash".Translate(hi.clash.label);
            // (This hour's pass has run: a booking of it is first tried tomorrow.)
            if (thisHour && !heldByOther) tip += "\n" + "RP_BookHourNow".Translate();
            return tip;
        }

        private List<FloatMenuOption> RoomOptions(ClassBookings bk, ClassBooking b)
        {
            var options = new List<FloatMenuOption> { new FloatMenuOption("RP_BookAnyRoom".Translate(prog.Zone.label), () => bk.SetAnchor(b, IntVec3.Invalid)) };
            ProgramSystem.ListVenues(map, PrisonMap.Of(map), prog, venues);
            foreach ((IntVec3 anchor, int places) in venues)
            {
                IntVec3 chosen = anchor;
                options.Add(new FloatMenuOption("RP_BookRoomOption".Translate(Cell(anchor), places), () => bk.SetAnchor(b, chosen)));
            }
            return options;
        }
    }

    public static class ClassBookingUI
    {
        public static void Open(ProgramDef prog, Map map) => Find.WindowStack.Add(new Dialog_ClassBooking(prog, map));
    }
}
