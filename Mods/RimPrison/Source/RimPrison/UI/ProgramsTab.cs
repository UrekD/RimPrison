using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Programs tab: one row per program with where it runs and its switch.</summary>
    public static class ProgramsTab
    {
        /// <summary>A Programs screen row's text, kept for <see cref="ProgramRowCacheTicks"/> (the room and teacher searches behind it are not cheap).</summary>
        private class ProgramRow
        {
            public string line, status;
            public ProgramStatusKind kind;
            public bool hasStatus;
            /// <summary>What the program is booked for, the latest miss, and whether a booking has missed enough hours in a row to be warned of (see <see cref="ClassBookings"/>).</summary>
            public string booked, missed;
            public bool warn;
            public int bookedHours;
            public float Height => 48f + (booked != null ? 16f : 0f) + (missed != null ? 16f : 0f);
        }

        private const int ProgramRowCacheTicks = 60;
        private static readonly Dictionary<ProgramDef, ProgramRow> programRows = new Dictionary<ProgramDef, ProgramRow>();
        private static int programRowsTick;
        private static float programRowsRealtime;
        private static Map programRowsMap;
        private static int programRowsBookings = -1;
        private static readonly List<ClassBooking> rowBookings = new List<ClassBooking>();

        private static ProgramRow RowOf(ProgramDef prog, PrisonGame game, Map map, PrisonMap pm)
        {
            int now = Find.TickManager.TicksGame;
            // Game time while it runs; real time too while paused (a change made then should still show).
            // (A booking made or changed shows at once.)
            int bookingsVersion = pm?.bookings.version ?? -1;
            bool stale = map != programRowsMap || now - programRowsTick >= ProgramRowCacheTicks || now < programRowsTick || bookingsVersion != programRowsBookings
                || (Find.TickManager.Paused && Time.realtimeSinceStartup - programRowsRealtime >= 1f);
            if (stale)
            {
                programRows.Clear();
                programRowsMap = map;
                programRowsBookings = bookingsVersion;
                programRowsTick = now;
                programRowsRealtime = Time.realtimeSinceStartup;
            }
            if (programRows.TryGetValue(prog, out ProgramRow row)) return row;
            ProgramSystem.Count(map, prog, out int eligible, out int graduated, out int referred);
            row = new ProgramRow { line = "RP_ProgramRow".Translate(prog.LabelCap, prog.sessions, Money.Format(prog.costPerSession), eligible, graduated) };
            if (referred > 0) row.line += "  " + "RP_ProgramReferred".Translate(referred);
            // Where it runs and with whom, or why it does not.
            if (pm != null)
            {
                row.status = ProgramSystem.Status(map, pm, game, prog, eligible, referred, out row.kind);
                row.hasStatus = true;
                BookingLines(pm, prog, row);
            }
            programRows[prog] = row;
            return row;
        }

        /// <summary>The booking lines of a row: the hours (and rooms) booked, the latest miss with its reason, and the warning when a booking has missed three hours in a row.</summary>
        private static void BookingLines(PrisonMap pm, ProgramDef prog, ProgramRow row)
        {
            pm.bookings.For(prog, rowBookings);
            if (rowBookings.Count == 0) return;
            var parts = new List<string>();
            ClassBooking latest = null;
            int missStreak = 0;
            foreach (ClassBooking b in rowBookings)
            {
                row.bookedHours += b.HourCount;
                string hours = ClassBookings.HoursLabel(b.hours);
                parts.Add(b.anchor.IsValid ? "RP_BookRowRoom".Translate(hours, $"{b.anchor.x}, {b.anchor.z}").ToString() : "RP_BookRow".Translate(hours).ToString());
                if (b.LastMiss != null && (latest == null || b.LastMiss.tick > latest.LastMiss.tick)) latest = b;
                missStreak = Mathf.Max(missStreak, b.missStreak);
            }
            row.booked = string.Join("; ", parts);
            if (missStreak >= ClassBookings.WarnAfter)
            {
                row.warn = true;
                row.missed = "RP_BookWarn".Translate(missStreak).ToString() + (latest != null ? "  " + ClassBookings.MissText(prog, latest.LastMiss) : "");
            }
            else if (latest != null) row.missed = ClassBookings.MissText(prog, latest.LastMiss);
        }

        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            string help = "RP_ProgramsHelp".Translate();
            float helpHeight = Text.CalcHeight(help, rect.width);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, helpHeight), help);
            PrisonMap pm = PrisonMap.Of(map);
            List<ProgramDef> programs = DefDatabase<ProgramDef>.AllDefsListForReading.OrderBy(p => p.uiOrder).ToList();
            // (A scroll view: the help and a row for every program do not always fit the window.)
            var outer = new Rect(rect.x, rect.y + helpHeight + 8f, rect.width, rect.height - helpHeight - 8f);
            // (The rows' text is cached: a row's height is read from it, with a line more for what is booked and one for a miss.)
            // (Read once: the scroll height and the drawing below take the same rows, and each row is drawn from its own top.)
            var rows = new List<ProgramRow>(programs.Count);
            float total = 0f;
            foreach (ProgramDef prog in programs)
            {
                ProgramRow r = RowOf(prog, game, map, pm);
                rows.Add(r);
                total += r.Height;
            }
            var view = new Rect(0f, 0f, outer.width - 16f, total);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < programs.Count; i++)
            {
                ProgramDef prog = programs[i];
                ProgramRow cached = rows[i];
                var row = new Rect(0f, y, view.width, cached.Height - 2f);
                y += cached.Height;
                Widgets.Label(new Rect(row.x, row.y + 2f, row.width - 260f, 24f), cached.line);
                if (cached.hasStatus)
                {
                    GUI.color = cached.kind == ProgramStatusKind.Running ? PrisonHUD.Good : cached.kind == ProgramStatusKind.Blocked ? PrisonHUD.Warn : PrisonHUD.Muted;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(row.x + 12f, row.y + 24f, row.width - 272f, 20f), cached.status);
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                }
                float lineY = row.y + 44f;
                Text.Font = GameFont.Tiny;
                if (cached.booked != null)
                {
                    GUI.color = PrisonHUD.Muted;
                    Widgets.Label(new Rect(row.x + 12f, lineY, row.width - 142f, 18f), cached.booked);
                    lineY += 16f;
                }
                if (cached.missed != null)
                {
                    GUI.color = cached.warn ? PrisonHUD.Warn : PrisonHUD.Muted;
                    Widgets.Label(new Rect(row.x + 12f, lineY, row.width - 142f, 18f), cached.missed);
                }
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (pm != null)
                {
                    var book = new Rect(row.xMax - 250f, row.y, 124f, 28f);
                    TooltipHandler.TipRegion(book, "RP_BookButtonTip".Translate());
                    if (Widgets.ButtonText(book, cached.bookedHours > 0 ? "RP_BookButtonN".Translate(cached.bookedHours) : "RP_BookButton".Translate())) ClassBookingUI.Open(prog, map);
                }
                TooltipHandler.TipRegion(row, prog.description);
                bool running = game.programs.IsRunning(prog);
                if (Widgets.ButtonText(new Rect(row.xMax - 120f, row.y, 120f, 28f), running ? "RP_ProgramRunning".Translate() : "RP_ProgramStopped".Translate()))
                {
                    if (running) game.programs.disabled.Add(prog);
                    else game.programs.disabled.Remove(prog);
                    // The status lines follow the switch at once.
                    programRowsMap = null;
                }
            }
            Widgets.EndScrollView();
        }
    }
}
