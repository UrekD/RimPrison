using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The always-visible prison panel, top left like Prison Architect's: funds and today's balance, population against
    /// capacity (coloured by security level), staff on duty, the current regime activity, the grade, the shuttle (the hours to the next one, or where it is now), and
    /// the emergency buttons. Rows are clickable shortcuts into the office.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PrisonHUD
    {
        public const float Width = 300f;
        private const float Pad = 6f, Row = 24f, ButtonsH = 30f, BarRowH = 26f, ClockRowH = 38f, ClockGap = 4f;
        public static readonly Color Good = new Color(0.37f, 0.75f, 0.39f), Warn = new Color(0.88f, 0.66f, 0.22f), Bad = new Color(0.85f, 0.33f, 0.31f);
        public static readonly Color Muted = new Color(0.6f, 0.62f, 0.65f);

        private static int cacheFrame = -1;
        private static Snapshot snap;

        /// <summary>Drops the cached figures (the next draw builds them again): for a state changed outside the game's own flow, as the UI tour does.</summary>
        public static void Invalidate() => snap = null;

        private class Snapshot
        {
            public int funds, net, inmates, capacity, staff, onDuty, onBreak, atHome, striking, freeBeds, toShuttle, perDay;
            /// <summary>Inmates at tension 60 and over, and the prison decision letters on the stack that nobody has answered.</summary>
            public int tense, openLetters;
            /// <summary>The letters count's tip: what it counts, then the held letters and the day's budget (the desk's head line).</summary>
            public string lettersTip;
            /// <summary>The shuttle's state when it is on its way or down (what the corner shows instead of the countdown).</summary>
            public string shuttleNow;
            public RegimeActivityDef now, next;
            public int nextHour;
            /// <summary>The count's line (cleared at, missed, who is unaccounted, or when the next is), whether it is a warning, and what the tip says.</summary>
            public string countLine, countTip;
            public bool countWarn;
            /// <summary>How many inmates ask to see the warden, and the line's tip (who and why: see <see cref="WardenAsks"/>).</summary>
            public int asks;
            public string asksTip;
            public float grade;
            /// <summary>There are inmates to grade (without any the corner shows a dash).</summary>
            public bool graded;
            public bool riot;
            /// <summary>A riot, an escape or a hostage crisis is on.</summary>
            public bool emergency;
            /// <summary>Cells exist and all their beds are taken (an empty plot has no beds to run out of: nothing the warden can fix before the first shuttle).</summary>
            public bool NoFreeBeds => capacity > 0 && freeBeds <= 0;
            public readonly List<(Color color, int count)> bySecurity = new List<(Color, int)>();
        }

        /// <summary>
        /// The Lockdown button: with a riot, an escape or a hostage crisis running it is the answer and starts at once; with nothing happening it is an expensive way to spend six hours
        /// (nobody works, eats or showers) and asks first.
        /// </summary>
        public static void RequestLockdown(PrisonGame game, bool emergencyRunning)
        {
            if (!LockdownNeedsConfirmation(emergencyRunning)) game.StartLockdown(6);
            else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_LockdownConfirm".Translate(), () => game.StartLockdown(6), destructive: true));
        }

        /// <summary>A whole-prison lockdown asks first unless a riot, an escape, a hostage crisis, an assault or a gang war is running.</summary>
        public static bool LockdownNeedsConfirmation(bool emergencyRunning) => !emergencyRunning;

        /// <summary>The Shakedown button: four hours of lockdown, tension up on everyone and two days before the next, so it asks first.</summary>
        public static void RequestShakedown(PrisonGame game) =>
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_ShakedownConfirm".Translate(PrisonGame.ShakedownHours, PrisonGame.ShakedownCooldownTicks / GenDate.TicksPerDay), game.StartShakedown, destructive: true));

        public static float Height => 30f + Row * 6 + ButtonsH + Pad * 3 + BarRowH + (ShowsClock(Find.CurrentMap) ? ClockRowH + ClockGap : 0f);

        /// <summary>
        /// The panel draws the clock and the speed buttons (and vanilla's, bottom right, are hidden): in a prison game, on the prison's own (home) map, with the map on screen
        /// (not the world map). One test for both sides, read from the same state in the same frame, so the buttons (and the pause and speed keys
        /// <c>TimeControls.DoTimeControlsGUI</c> handles) are drawn and run in exactly one place.
        /// </summary>
        public static bool ShowsClock(Map map) =>
            PrisonGame.Instance != null && map != null && map.IsPlayerHome && Current.ProgramState == ProgramState.Playing && Find.CurrentMap == map
            && WorldRendererUtility.DrawingMap && !Find.UIRoot.screenshotMode.FiltersCurrentEvent;

        public static void DoHUD(Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || Find.UIRoot.screenshotMode.FiltersCurrentEvent) return;
            bool clock = ShowsClock(map);
            var panel = new Rect(8f, 8f, Width, Height);
            Widgets.DrawWindowBackground(panel);
            Rect inner = panel.ContractedBy(Pad);
            float y = inner.y;
            // (The clock first: whatever the rest of the panel throws, the pause and speed keys are handled.)
            if (clock)
            {
                DoClock(new Rect(inner.x, y, inner.width, ClockRowH), map);
                y += ClockRowH + ClockGap;
            }
            if (snap == null || Time.frameCount - cacheFrame > 60 || Time.frameCount < cacheFrame)
            {
                cacheFrame = Time.frameCount;
                snap = Build(game, map);
            }

            // Funds and today's balance
            var funds = new Rect(inner.x, y, inner.width, 30f);
            Text.Font = GameFont.Medium;
            Widgets.Label(funds, Money.Format(snap.funds));
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = snap.net >= 0 ? Good : Bad;
            Widgets.Label(funds, "RP_HudToday".Translate(Money.FormatSigned(snap.net)));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Clickable(funds, "RP_HudFundsTip", MainTabWindow_Prison.Tab.Finance);
            y += 30f;

            // Population, stacked by security level
            var pop = new Rect(inner.x, y, inner.width, Row);
            Widgets.Label(new Rect(pop.x, pop.y + 2f, 110f, Row), "RP_HudInmates".Translate(snap.inmates, snap.capacity));
            var bar = new Rect(pop.x + 112f, pop.y + 6f, pop.width - 112f, 12f);
            Widgets.DrawBoxSolid(bar, new Color(0.15f, 0.15f, 0.17f));
            float x = bar.x;
            foreach ((Color color, int count) in snap.bySecurity)
            {
                if (snap.capacity <= 0 || count <= 0) continue;
                float w = Mathf.Min(bar.xMax - x, bar.width * count / snap.capacity);
                Widgets.DrawBoxSolid(new Rect(x, bar.y, w, bar.height), color);
                x += w;
            }
            if (snap.freeBeds <= 0 && snap.capacity > 0) Widgets.DrawBox(bar, 1);
            Clickable(pop, "RP_HudInmatesTip", MainTabWindow_Prison.Tab.Inmates);
            y += Row;

            // Staff
            var staff = new Rect(inner.x, y, inner.width, Row);
            GUI.color = Muted;
            string line = "RP_HudStaff".Translate(snap.staff, snap.onDuty, snap.onBreak, snap.atHome);
            Widgets.Label(new Rect(staff.x, staff.y + 2f, staff.width, Row), line);
            GUI.color = Color.white;
            if (snap.striking > 0)
            {
                GUI.color = Bad;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(staff.x, staff.y + 2f, staff.width, Row), "RP_HudStriking".Translate(snap.striking));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            Clickable(staff, "RP_HudStaffTip", MainTabWindow_Prison.Tab.Staff);
            y += Row;

            // Regime now / next
            var regime = new Rect(inner.x, y, inner.width, Row);
            if (snap.now != null)
            {
                Widgets.DrawBoxSolid(new Rect(regime.x, regime.y + 5f, 14f, 14f), game.LockdownActive ? Bad : snap.now.color);
                string nowLabel = game.LockdownActive ? "RP_LockdownActive".Translate().ToString() : snap.now.LabelCap.ToString();
                Widgets.Label(new Rect(regime.x + 20f, regime.y + 2f, 150f, Row), $"{GenLocalDate.HourOfDay(map):00}:00  {nowLabel}");
                if (snap.next != null)
                {
                    GUI.color = Muted;
                    Widgets.Label(new Rect(regime.x + 170f, regime.y + 2f, regime.width - 170f, Row), "RP_HudNext".Translate(snap.next.label, snap.nextHour.ToString("00")));
                    GUI.color = Color.white;
                }
            }
            Clickable(regime, "RP_HudRegimeTip", MainTabWindow_Prison.Tab.Regime);
            y += Row;

            // The count: cleared at, or who is unaccounted, or when the next is
            var countRow = new Rect(inner.x, y, inner.width, Row);
            GUI.color = snap.countWarn ? Bad : Muted;
            Widgets.Label(new Rect(countRow.x, countRow.y + 2f, countRow.width, Row), snap.countLine);
            GUI.color = Color.white;
            if (Mouse.IsOver(countRow)) Widgets.DrawHighlight(countRow);
            TooltipHandler.TipRegion(countRow, snap.countTip);
            if (Widgets.ButtonInvisible(countRow)) MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            y += Row;

            // Who asks to see the warden ("3 ask to see you"): a click lists them on the Intel screen
            var asksRow = new Rect(inner.x, y, inner.width, Row);
            GUI.color = snap.asks > 0 ? Warn : Muted;
            Widgets.Label(new Rect(asksRow.x, asksRow.y + 2f, asksRow.width, Row), WardenAsks.HudLine(snap.asks));
            GUI.color = Color.white;
            if (Mouse.IsOver(asksRow)) Widgets.DrawHighlight(asksRow);
            TooltipHandler.TipRegion(asksRow, snap.asksTip);
            if (Widgets.ButtonInvisible(asksRow)) MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            y += Row;

            // Grade, reputation, the shuttle
            var grade = new Rect(inner.x, y, inner.width, Row);
            var tile = new Rect(grade.x, grade.y + 2f, 20f, 20f);
            Widgets.DrawBoxSolid(tile, snap.graded ? GradeColor(snap.grade) : Muted * 0.6f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(tile, snap.graded ? PrisonGrading.Letter(snap.grade) : "–");
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(grade.x + 26f, grade.y + 2f, 140f, Row), "RP_HudReputation".Translate(game.reputation.ToString("F0"), game.PaymentMultiplier.ToString("0.00")));
            if (snap.shuttleNow != null || snap.perDay > 0)
            {
                GUI.color = snap.shuttleNow != null ? Color.white : snap.NoFreeBeds ? Bad : Muted;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(grade.x, grade.y + 2f, grade.width, Row), snap.shuttleNow ?? (snap.NoFreeBeds ? "RP_HudShuttleNoBeds".Translate(snap.toShuttle) : "RP_HudShuttle".Translate(snap.toShuttle)).ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            Clickable(grade, "RP_HudGradeTip", MainTabWindow_Prison.Tab.Overview);
            y += Row + Pad;

            // Emergency buttons
            float bw = (inner.width - 8f) / 3f;
            var lockRect = new Rect(inner.x, y, bw, 28f);
            if (game.LockdownActive)
            {
                GUI.color = Bad;
                if (Widgets.ButtonText(lockRect, "RP_LockdownEnd".Translate())) game.lockdownUntilTick = -1;
                GUI.color = Color.white;
            }
            else if (Widgets.ButtonText(lockRect, "RP_Lockdown".Translate())) RequestLockdown(game, snap.emergency);
            TooltipHandler.TipRegion(lockRect, (game.LockdownActive ? "RP_LockdownEndDesc" : "RP_LockdownDesc").Translate());
            var shakeRect = new Rect(lockRect.xMax + 4f, y, bw, 28f);
            if (game.ShakedownReady)
            {
                if (Widgets.ButtonText(shakeRect, "RP_Shakedown".Translate())) RequestShakedown(game);
                TooltipHandler.TipRegion(shakeRect, "RP_ShakedownDesc".Translate());
            }
            else
            {
                float hoursLeft = (PrisonGame.ShakedownCooldownTicks - (Find.TickManager.TicksGame - game.lastShakedownTick)) / (float)GenDate.TicksPerHour;
                Widgets.ButtonText(shakeRect, "RP_ShakedownCooldown".Translate(hoursLeft.ToString("0")), active: false);
            }
            var policeRect = new Rect(shakeRect.xMax + 4f, y, bw, 28f);
            if (snap.riot)
            {
                GUI.color = Color.Lerp(Warn, Bad, Mathf.PingPong(Time.realtimeSinceStartup, 1f));
                if (Widgets.ButtonText(policeRect, "RP_HudRiotPolice".Translate(Money.Format(RiotPolice.Cost)))) RiotPolice.Call(map);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(policeRect, "RP_CallRiotPoliceDesc".Translate());
            }
            else
            {
                Widgets.ButtonText(policeRect, "RP_HudRiotPoliceIdle".Translate(), active: false);
                TooltipHandler.TipRegion(policeRect, "RP_CallRiotPoliceDesc".Translate());
            }

            // The colonist bar: every member of staff by role, or only the warden, the pinned and those who need the warden now
            var barRect = new Rect(inner.x, y + 32f, bw, 22f);
            if (Widgets.ButtonText(barRect, (game.staffBarCompact ? "RP_HudBarPinned" : "RP_HudBarAll").Translate())) StaffBar.SetCompact(game, !game.staffBarCompact);
            TooltipHandler.TipRegion(barRect, "RP_HudBarTip".Translate());

            // Beside the bar button: how many inmates are tense (a click lists them on the Intel screen) and how many decision letters wait for an answer.
            float cx = barRect.xMax + 8f, cw = (inner.xMax - cx) / 2f;
            CountCell(new Rect(cx, barRect.y, cw, 22f), "RP_HudTense", snap.tense, "RP_HudTenseTip".Translate(DeskBoard.TenseAt.ToString("F0")), MainTabWindow_Prison.Tab.Intel);
            CountCell(new Rect(cx + cw, barRect.y, cw, 22f), "RP_HudLetters", snap.openLetters, snap.lettersTip, MainTabWindow_Prison.Tab.Overview, OverviewTab.ShowDesk);
        }

        // The clock row's strings: the time by the minute shown (a table filled as the minutes come: nothing built per frame), the date and its tip by the day.
        private static readonly string[] clockTimes = new string[24 * 60];
        private static bool clockTwelve;
        private static LoadedLanguage clockLanguage;
        private static int clockTile = -1, clockDayKey = -1;
        private static Vector2 clockLongLat;
        private static string clockDayFull, clockSeasonFull, clockDayLine, clockSeasonLine, clockTip, clockTimeNow, clockTimeSuffix;
        private static int clockTimeMinute = -1;
        private static GameFont clockDayFont = GameFont.Small, clockSeasonFont = GameFont.Small;
        // The widths the layout rests on, measured when the language, the clock mode or the room changes (see MeasureClock): the time's number, its AM/PM, the date column.
        private static float clockNumberWidth, clockSuffixWidth, clockTimeColumn, clockDateWidth, clockMeasuredRoom = -1f;
        private static bool clockSmallSuffix;

        /// <summary>The time of day large, the day and season beside it, vanilla's speed buttons to its right (their keys with them).</summary>
        private static void DoClock(Rect row, Map map)
        {
            Rect buttons = new Rect(row.xMax - 4f * TimeControls.TimeButSize.x, row.y + (row.height - TimeControls.TimeButSize.y) / 2f, 4f * TimeControls.TimeButSize.x, TimeControls.TimeButSize.y);
            float room = buttons.x - row.x - 2f * ClockGap2;
            UpdateClock(map, room);
            var timeRect = new Rect(row.x, row.y, clockNumberWidth, row.height);
            var dateRect = new Rect(row.x + clockTimeColumn + ClockGap2, row.y, Mathf.Max(0f, buttons.x - ClockGap2 - (row.x + clockTimeColumn + ClockGap2)), row.height);
            Rect hover = new Rect(row.x, row.y, dateRect.xMax - row.x, row.height);
            if (Mouse.IsOver(hover)) Widgets.DrawHighlight(hover);
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.Font = GameFont.Medium;
            Widgets.Label(timeRect, clockTimeNow);
            if (clockTimeSuffix != null)
            {
                // (The 12-hour mode's AM/PM, small when the large one would not leave the date its room: a little lower, so it sits on the number's line.)
                Text.Font = clockSmallSuffix ? GameFont.Small : GameFont.Medium;
                Widgets.Label(new Rect(timeRect.xMax, row.y + (clockSmallSuffix ? 2f : 0f), clockSuffixWidth, row.height), clockTimeSuffix);
            }
            GUI.color = Muted;
            Text.Font = clockDayFont;
            Widgets.Label(new Rect(dateRect.x, dateRect.y, dateRect.width, dateRect.height / 2f + 1f), clockDayLine);
            Text.Font = clockSeasonFont;
            Widgets.Label(new Rect(dateRect.x, dateRect.y + dateRect.height / 2f - 1f, dateRect.width, dateRect.height / 2f + 1f), clockSeasonLine);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(hover, new TipSignal(clockTip, 7340211));
            // (Vanilla's own, as at the bottom right: it draws the buttons, handles their clicks and the pause and speed keys, and skips the layout events as the global controls do.)
            if (Event.current.type != EventType.Layout) TimeControls.DoTimeControlsGUI(buttons);
        }

        private const float ClockGap2 = 8f;

        /// <summary>
        /// The room the date needs and the time's two sizes, measured from the widest strings the language gives: "Day 60", the four seasons, every digit pair
        /// as a time, the AM/PM. The date column is that wide and never cut; the time takes the rest, and when the 12-hour time's large AM/PM would not leave it,
        /// the AM/PM is small. Done when the language, the clock mode or the room changes, not per frame.
        /// </summary>
        private static void MeasureClock(float room, bool twelve)
        {
            clockMeasuredRoom = room;
            GameFont keep = Text.Font;
            Text.Font = GameFont.Small;
            float need = Text.CalcSize("RP_HudClockDay".Translate(60)).x;
            for (int i = 0; i < 4; i++) need = Mathf.Max(need, Text.CalcSize(((Quadrum)i).GetSeason(clockLongLat.y).LabelCap()).x);
            need += 2f;
            Text.Font = GameFont.Medium;
            float number = 0f;
            for (int d = 0; d < 10; d++) number = Mathf.Max(number, Text.CalcSize($"{d}{d}:{d}{d}").x);
            number += 2f;
            clockSmallSuffix = false;
            float suffix = 0f;
            if (twelve)
            {
                string am = " " + "AM".Translate(), pm = " " + "PM".Translate();
                suffix = Mathf.Max(Text.CalcSize(am).x, Text.CalcSize(pm).x);
                if (number + suffix + need > room)
                {
                    clockSmallSuffix = true;
                    Text.Font = GameFont.Small;
                    suffix = Mathf.Max(Text.CalcSize(am).x, Text.CalcSize(pm).x);
                }
            }
            clockSuffixWidth = suffix;
            clockTimeColumn = number + suffix;
            clockDateWidth = Mathf.Max(need, room - clockTimeColumn);
            Text.Font = keep;
            clockDayKey = -1;
        }

        /// <summary>
        /// A line that fits the width without an ellipsis: as it is, in the tiny font, then (a long label, like "permanent summer") its last word in the small and the tiny
        /// font; the cut is only the last resort. The tooltip has the full date whatever is shown.
        /// </summary>
        private static string FitLine(string text, float width, out GameFont font)
        {
            string last = text.Substring(text.LastIndexOf(' ') + 1);
            foreach (string candidate in new[] { text, last })
                foreach (GameFont f in new[] { GameFont.Small, GameFont.Tiny })
                {
                    Text.Font = f;
                    if (Text.CalcSize(candidate).x <= width)
                    {
                        font = f;
                        return candidate;
                    }
                }
            Text.Font = font = GameFont.Small;
            return text.Truncate(width);
        }

        private static void UpdateClock(Map map, float room)
        {
            long ticks = GenTicks.TicksAbs;
            if (map.Tile != clockTile)
            {
                clockTile = map.Tile;
                clockLongLat = Find.WorldGrid.LongLatOf(clockTile);
                clockDayKey = -1;
            }
            bool twelve = Prefs.TwelveHourClockMode;
            if (twelve != clockTwelve || clockLanguage != LanguageDatabase.activeLanguage)
            {
                clockTwelve = twelve;
                clockLanguage = LanguageDatabase.activeLanguage;
                System.Array.Clear(clockTimes, 0, clockTimes.Length);
                clockTimeMinute = clockDayKey = -1;
                clockMeasuredRoom = -1f;
            }
            if (Mathf.Abs(room - clockMeasuredRoom) > 0.5f || clockMeasuredRoom < 0f) MeasureClock(room, twelve);
            int minute = Mathf.Clamp(Mathf.FloorToInt(GenDate.HourFloat(ticks, clockLongLat.x) * 60f), 0, 24 * 60 - 1);
            if (minute != clockTimeMinute)
            {
                clockTimeMinute = minute;
                int hour = minute / 60;
                clockTimeNow = clockTimes[minute] ??= twelve ? $"{(hour % 12 == 0 ? 12 : hour % 12)}:{minute % 60:00}" : $"{hour:00}:{minute % 60:00}";
                clockTimeSuffix = twelve ? " " + (hour >= 12 ? "PM" : "AM").Translate() : null;
                Text.Font = GameFont.Medium;
                clockNumberWidth = Text.CalcSize(clockTimeNow).x + 2f;
                Text.Font = GameFont.Small;
            }
            // (Rebuilt when the day, the quadrum or the year turns, as vanilla's readout does.)
            int dayKey = (GenDate.Year(ticks, clockLongLat.x) * 4 + (int)GenDate.Quadrum(ticks, clockLongLat.x)) * 32 + GenDate.DayOfSeason(ticks, clockLongLat.x);
            if (dayKey != clockDayKey)
            {
                clockDayKey = dayKey;
                Season season = GenDate.Season(ticks, clockLongLat);
                Quadrum quadrum = GenDate.Quadrum(ticks, clockLongLat.x);
                int day = GenDate.DayOfSeason(ticks, clockLongLat.x) + 1;
                clockDayFull = "RP_HudClockDay".Translate(day);
                clockSeasonFull = season.LabelCap();
                clockDayLine = null;
                var seasons = new System.Text.StringBuilder();
                for (int i = 0; i < 4; i++)
                    seasons.AppendLine(((Quadrum)i).Label() + " - " + ((Quadrum)i).GetSeason(clockLongLat.y).LabelCap());
                clockTip = GenDate.DateReadoutStringAt(ticks, clockLongLat) + "\n\n" + "DateReadoutTip".Translate(GenDate.DaysPassed, 15, season.LabelCap(), 15, quadrum.Label(), seasons.ToString());
            }
            if (clockDayLine == null)
            {
                clockDayLine = FitLine(clockDayFull, clockDateWidth, out clockDayFont);
                clockSeasonLine = FitLine(clockSeasonFull, clockDateWidth, out clockSeasonFont);
                Text.Font = GameFont.Small;
            }
        }

        /// <summary>A small count in the panel: muted at nothing, a warning colour otherwise; with a tab it is a shortcut to it (<paramref name="before"/> run first: the view the tab opens on).</summary>
        private static void CountCell(Rect r, string labelKey, int n, string tip, MainTabWindow_Prison.Tab? tab, System.Action before = null)
        {
            GUI.color = n > 0 ? Warn : Muted;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(r, labelKey.Translate(n));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (tab != null && Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            TooltipHandler.TipRegion(r, tip);
            if (tab is { } t && Widgets.ButtonInvisible(r))
            {
                before?.Invoke();
                MainTabWindow_Prison.Open(t);
            }
        }

        private static void Clickable(Rect r, string tipKey, MainTabWindow_Prison.Tab tab)
        {
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            TooltipHandler.TipRegion(r, tipKey.Translate());
            if (Widgets.ButtonInvisible(r)) MainTabWindow_Prison.Open(tab);
        }

        private static readonly Dictionary<Color, Texture2D> solid = new Dictionary<Color, Texture2D>();

        /// <summary>A cached solid texture for bars (never create one per frame).</summary>
        public static Texture2D Solid(Color c)
        {
            if (!solid.TryGetValue(c, out Texture2D t)) solid[c] = t = SolidColorMaterials.NewSolidColorTexture(c);
            return t;
        }

        public static Color GradeColor(float score) =>
            score >= 85f ? new Color(0.30f, 0.69f, 0.31f) : score >= 70f ? new Color(0.55f, 0.76f, 0.29f) : score >= 55f ? new Color(1f, 0.76f, 0.03f)
            : score >= 40f ? new Color(1f, 0.6f, 0f) : new Color(0.96f, 0.26f, 0.21f);

        /// <summary>The row of who asks to see the warden: how many, and its tip naming them (see <see cref="WardenAsks"/>).</summary>
        internal static (int count, string tip) AsksLine(Map map)
        {
            List<(WardenAsk ask, Pawn inmate)> live = WardenAsks.Live(map);
            return (live.Count, WardenAsks.HudTip(map, live));
        }

        private static Snapshot Build(PrisonGame game, Map map)
        {
            var s = new Snapshot { funds = game.ledger.funds };
            DayBook today = game.ledger.Today;
            s.net = today.Income - today.Expenses;
            s.inmates = map.mapPawns.PrisonersOfColonySpawnedCount;
            s.capacity = IntakeSystem.Capacity(map);
            s.freeBeds = IntakeSystem.FreeBedCount(map);
            // (One pass over the inmates, not one per level.)
            List<SecurityLevelDef> levels = DefDatabase<SecurityLevelDef>.AllDefsListForReading;
            var perLevel = new int[levels.Count];
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate()?.security is { } level)
                {
                    int at = levels.IndexOf(level);
                    if (at >= 0) perLevel[at]++;
                }
            for (int i = 0; i < levels.Count; i++)
                if (perLevel[i] > 0) s.bySecurity.Add((levels[i].color, perLevel[i]));
            int hour = GenLocalDate.HourOfDay(map);
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                s.staff++;
                if (StaffCommute.AtHome(p)) s.atHome++;
                else if (IncidentWorker_StaffStrike.Striking(p)) s.striking++;
                else if (p.timetable?.GetAssignment(hour) == TimeAssignmentDefOf.Joy) s.onBreak++;
                else s.onDuty++;
            }
            s.now = game.SlotAt(null, hour);
            for (int i = 1; i < 24; i++)
            {
                RegimeActivityDef a = game.SlotAt(null, (hour + i) % 24);
                if (a == s.now) continue;
                s.next = a;
                s.nextHour = (hour + i) % 24;
                break;
            }
            if (PrisonMap.Of(map) is { } pm)
            {
                s.countLine = CountSystem.HudLine(map, pm, game, out s.countWarn);
                List<string> missing = CountSystem.UnaccountedLines(map, pm);
                s.countTip = game.countsPerDay <= 0 ? "RP_HudCountTipOff".Translate().ToString()
                    : "RP_HudCountTip".Translate(CountSystem.HoursText(game)).ToString() + (missing.Count > 0 ? "\n\n" + string.Join("\n", missing) : "");
            }
            else (s.countLine, s.countTip) = ("", "");
            (s.tense, s.openLetters) = DeskCounts(map);
            (s.asks, s.asksTip) = AsksLine(map);
            s.lettersTip = "RP_HudLettersTip".Translate() + "\n\n" + DeskBoard.Desk(game, map).HeadLine() + "\n\n" + "RP_HudLettersClick".Translate();
            ReportCard card = PrisonGrading.Compute(map, game);
            s.grade = card.Overall;
            s.graded = card.Graded;
            // (The hours to the next one: it has just come at the hour itself.)
            s.toShuttle = ShuttleSystem.HoursToDaily(game, map);
            if (s.toShuttle == 0) s.toShuttle = 24;
            foreach (int v in game.intake.perDay.Values) s.perDay += v;
            if (game.shuttle.visit is { } visit)
                s.shuttleNow = visit.phase switch
                {
                    ShuttlePhase.Landed when ShuttleSystem.InOvertime(visit) => "RP_HudShuttleUnloading".Translate().ToString(),
                    ShuttlePhase.Landed => "RP_HudShuttleDown".Translate(System.Math.Max(0f, (visit.departTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("0.0")).ToString(),
                    ShuttlePhase.Inbound => "RP_HudShuttleInbound".Translate().ToString(),
                    ShuttlePhase.Leaving => "RP_HudShuttleLeaving".Translate().ToString(),
                    _ => "RP_HudShuttleHeld".Translate().ToString(),
                };
            s.riot = RiotPolice.RiotOngoing(map);
            PrisonAlerts.Refresh();
            s.emergency = s.riot || PrisonAlerts.Rioters.Count > 0 || PrisonAlerts.Escaping.Count > 0 || PrisonAlerts.Hostages.Count > 0 || FightingOn(game, map);
            return s;
        }

        /// <summary>The two counts beside the colonist bar button: inmates at tension 60 and over, and unanswered prison decision letters on the letter stack.</summary>
        public static (int tense, int openLetters) DeskCounts(Map map) => (DeskBoard.TenseCount(map), DeskBoard.OpenLetters());

        /// <summary>Blows are being struck: an inmate is assaulting somebody (a guard, another inmate) or a gang war has begun (after its declaration). A lockdown is no "nothing is happening" then.</summary>
        public static bool FightingOn(PrisonGame game, Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.MentalStateDef == RPMentalStateDefOf.RP_Assaulting || p.MentalStateDef == RPMentalStateDefOf.RP_AttackingInmate) return true;
            foreach (StoryArc arc in game.story.arcs)
                if (!arc.done && arc.def == RPGangWarDefOf.RP_Arc_GangWar && arc.stage >= GangWarArc.TitForTat) return true;
            return false;
        }
    }

    /// <summary>The prison has no use for the colony resource list in the top-left corner: the prison panel lives there.</summary>
    [HarmonyPatch(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI))]
    public static class Patch_ResourceReadout_Hide
    {
        public static bool Prefix() => PrisonGame.Instance == null;
    }

    /// <summary>Vanilla's live messages start at the top left too: in a prison game they start below the panel.</summary>
    /// <summary>
    /// Vanilla's colonist bar may take all but 260 pixels each side and is centred, so with the penitentiary's forty-odd staff its first portraits sat on the panel (the
    /// day's money cut to "+$18,890 tc"). In a prison it leaves the panel's width, and the same on the other side to stay centred: more staff, smaller portraits (never under 200 pixels: vanilla's scale search would not end with no width at all).
    /// </summary>
    [HarmonyPatch(typeof(ColonistBarDrawLocsFinder), "MaxColonistBarWidth", MethodType.Getter)]
    public static class Patch_ColonistBarDrawLocsFinder_ClearOfPanel
    {
        public static void Postfix(ref float __result)
        {
            if (PrisonGame.Instance != null && Current.ProgramState == ProgramState.Playing) __result = Mathf.Max(Mathf.Min(__result, UI.screenWidth - 2f * (8f + PrisonHUD.Width + 12f)), 200f);
        }
    }

    [HarmonyPatch(typeof(Messages), nameof(Messages.MessagesDoGUI))]
    public static class Patch_Messages_BelowPanel
    {
        private static readonly AccessTools.FieldRef<List<Message>> Live = AccessTools.StaticFieldRefAccess<List<Message>>(AccessTools.Field(typeof(Messages), "liveMessages"));

        public static bool Prefix()
        {
            if (PrisonGame.Instance == null || Current.ProgramState != ProgramState.Playing) return true;
            Text.Font = GameFont.Small;
            int y = (int)(8f + PrisonHUD.Height + 8f);
            List<Message> live = Live();
            for (int i = live.Count - 1; i >= 0; i--)
            {
                live[i].Draw(12, y);
                // (A letter a standing order answered: its "Review" link beside the message.)
                StandingOrders.DrawReview(live[i]);
                y += 26;
            }
            return false;
        }
    }

    /// <summary>
    /// The date and the time-speed buttons are in the prison panel (<see cref="PrisonHUD.ShowsClock"/>): vanilla's at the bottom right are not drawn, and as they are not,
    /// <c>curBaseY</c> stays where it is and the weather, the temperature and the rest above move down into the space. The world map and any map the panel does not
    /// show them on keep vanilla's.
    /// </summary>
    [HarmonyPatch(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DoDate))]
    public static class Patch_GlobalControlsUtility_DoDate_InPanel
    {
        public static bool Prefix() => !PrisonHUD.ShowsClock(Find.CurrentMap);
    }

    /// <summary>The speed buttons (and, with them, the pause and speed keys: <c>TimeControls.DoTimeControlsGUI</c> reads those too) are drawn by the panel, see <see cref="Patch_GlobalControlsUtility_DoDate_InPanel"/>.</summary>
    [HarmonyPatch(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DoTimespeedControls))]
    public static class Patch_GlobalControlsUtility_DoTimespeedControls_InPanel
    {
        public static bool Prefix() => !PrisonHUD.ShowsClock(Find.CurrentMap);
    }
}
