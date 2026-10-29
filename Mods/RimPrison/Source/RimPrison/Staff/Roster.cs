using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>The shift a guard is pinned to (the roster, Staff tab): auto leaves it to the daily balance.</summary>
    public enum ShiftPin : byte { Auto, Days, Nights }

    [DefOf]
    public static class RPRosterDefOf
    {
        public static ThoughtDef RP_NightsAgain;
        public static ThoughtDef RP_FairRota;

        static RPRosterDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRosterDefOf));
    }

    /// <summary>
    /// The roster: who works nights (a pin per guard), the handover (the day shift stays 0-3 hours past 22:00 at overtime pay, see <see cref="ShiftSystem"/>) and the 24-hour strip of guards on duty the Staff
    /// and Deployment tabs show. The strip is the guards SCHEDULED on duty: the head count of the same test <see cref="StaffCommute"/> sends people home by (<see cref="StaffCommute.ShouldBeHome"/>),
    /// worked out for every hour of the day from the timetables (a forecast cannot be anything else for the hours to come), once an hour and again whenever <see cref="Invalidate"/> says what it depends on
    /// changed (a suspension, a leave, a hostage, a role, a pin, the handover, the shift policy, a timetable edited by hand in the vanilla Schedule tab) and when a suspension, stand-down, discipline or leave
    /// runs out (those end at a tick, not at a hook: <see cref="nextExpiry"/>), never per frame. Who is actually on site is another thing: the commute keeps one off-shift member of staff
    /// on site overnight, and the strip says so beside it (<see cref="KeptOnSite"/>) rather than counting them in a schedule that sends them home.
    /// </summary>
    public static class Roster
    {
        /// <summary>Nights in a row after which a night guard resents it, the hours the handover can be set to, the hours of a day shift (the overtime hour is paid on a sixteenth of the day's wage) and the overtime rate.</summary>
        public const int NightsAgainAfter = 7, MaxHandover = 3, DayShiftHours = 16;
        public const float OvertimeRate = 1.5f;

        /// <summary>Bumped (by <see cref="Invalidate"/>) when something the strip depends on changes: the strip is worked out again.</summary>
        public static int Version;

        /// <summary>
        /// Something the strip's answer depends on changed that its cache key does not see: a suspension, stand-down or discipline starting or ending, a leave starting or ending, a hostage taken or freed, a pin, a
        /// write to a member of staff's timetable (<see cref="ShiftSystem.Apply"/> once for each pass that changes one, and the warden's hand in the vanilla Schedule tab: <see cref="Patch_Pawn_TimetableTracker_SetAssignment"/>).
        /// A hire, a firing and a role change bump <see cref="StaffCommute.StaffVersion"/>; the handover, the shift policy and the headcount are in the key, and a deadline passing is <see cref="nextExpiry"/>.
        /// </summary>
        public static void Invalidate() => Version++;

        /// <summary>For tests: how many times the strip has been worked out.</summary>
        public static int Computations;

        private static readonly int[] counts = new int[24];
        private static int nights, days, pinned;
        private static (Map map, int hour, int staffVersion, int version, int handover, bool policy, int staff, PrisonGame game) key;

        /// <summary>
        /// The earliest tick at which a suspension, stand-down, discipline or leave of a guard on the roster runs out (int.MaxValue: none pending). Those predicates compare against the clock
        /// (<see cref="CorruptionSystem.Suspended(CompStaff)"/>, <see cref="StaffLives.OnLeave"/>), so the strip changes at that tick with no hook to say so: once the clock is there the strip is worked out again.
        /// </summary>
        private static int nextExpiry = int.MaxValue;

        // What the strip draws, kept with the counts (and the language) so that drawing it formats nothing: the counts and the axis as text, the hour names and the bars' tooltips (made on hover only).
        private const int BarTipId = 71230500, StripTipId = 71230470;
        private static readonly string[] countText = new string[24], axisText = MakeAxis(), hourName = new string[24], barTip = new string[24];
        private static readonly System.Func<string>[] barTipGetters = MakeBarTips();
        private static readonly System.Func<string> stripTipGetter = () => StripTip(tipMap);
        private static string stripTip, heading;
        private static int stripTipKept = -1, headingKept = -1;
        private static LoadedLanguage language;
        private static Map tipMap;
        private static (Map map, int bucket, int version, int staffVersion, int home) keptKey;
        private static int kept;

        private static string[] MakeAxis()
        {
            var axis = new string[24];
            for (int h = 0; h < 24; h++) axis[h] = h.ToString("00");
            return axis;
        }

        private static System.Func<string>[] MakeBarTips()
        {
            var getters = new System.Func<string>[24];
            for (int h = 0; h < 24; h++)
            {
                int hour = h;
                getters[h] = () => BarTip(hour);
            }
            return getters;
        }

        /// <summary>A guard who is on the roster: a permanent member of staff with guard duties, not the warden on the floor (riot police are called in, not rostered).</summary>
        public static bool OnRoster(CompStaff sc) => sc.role is { guardDuties: true, warden: false } && sc.leaveAtTick < 0;

        /// <summary>Guards on duty in each hour of the day (0-23), as the timetables are now. The array is the roster's own: read it, never keep it.</summary>
        public static int[] Strip(Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            List<Pawn> staff = StaffCommute.AllStaff(map);
            var now = (map, GenLocalDate.DayOfYear(map) * 24 + GenLocalDate.HourOfDay(map), StaffCommute.StaffVersion, Version, ShiftSystem.HandoverHours, game?.guardNightShift ?? true, staff.Count, game);
            if (key == now && Find.TickManager.TicksGame < nextExpiry) return counts;
            key = now;
            Computations++;
            Compute(staff);
            return counts;
        }

        /// <summary>
        /// Staff who are off shift by the timetable (or suspended, or on leave) and are still on site now (not on the way out): the commute keeps one overnight so that the prison is never left empty, and
        /// others it could not send (busy at something that cannot be cut short). The strip counts a schedule; this is what the schedule does not show. Worked out once in 250 ticks and when somebody goes home, comes back or the roster changes.
        /// </summary>
        public static int KeptOnSite(Map map)
        {
            var now = (map, Find.TickManager.TicksGame / 250, Version, StaffCommute.StaffVersion, PrisonGame.Instance?.home?.Count ?? 0);
            if (keptKey == now) return kept;
            keptKey = now;
            kept = 0;
            int hour = GenLocalDate.HourOfDay(map);
            List<Pawn> staff = StaffCommute.AllStaff(map);
            for (int i = 0; i < staff.Count; i++)
                if (staff[i].GetComp<CompStaff>() is { } sc && StaffCommute.OffShiftOnSite(staff[i], sc, hour)) kept++;
            return kept;
        }

        private static int Earliest(int earliest, int tick, int a, int b, int c, int d)
        {
            foreach (int until in stackalloc int[] { a, b, c, d })
                if (until > tick && until < earliest) earliest = until;
            return earliest;
        }

        private static void Compute(List<Pawn> staff)
        {
            System.Array.Clear(counts, 0, counts.Length);
            (nights, days, pinned) = (0, 0, 0);
            nextExpiry = int.MaxValue;
            int tick = Find.TickManager.TicksGame;
            for (int i = 0; i < staff.Count; i++)
            {
                CompStaff sc = staff[i].GetComp<CompStaff>();
                if (sc == null || !OnRoster(sc)) continue;
                nextExpiry = Earliest(nextExpiry, tick, sc.suspendedUntilTick, sc.disciplineUntilTick, sc.standDownUntilTick, sc.leaveUntilTick);
                if (sc.nightShift) nights++;
                else days++;
                if (ShiftSystem.PinOf(sc) != ShiftPin.Auto) pinned++;
                for (int h = 0; h < 24; h++)
                    if (!StaffCommute.ShouldBeHome(staff[i], sc, h)) counts[h]++;
            }
            for (int h = 0; h < 24; h++) countText[h] = counts[h].ToString();
            System.Array.Clear(barTip, 0, 24);
        }

        /// <summary>The guards on the roster who work nights and days, and how many of them are pinned (as of the strip).</summary>
        public static (int nights, int days, int pinned) Split(Map map)
        {
            Strip(map);
            return (nights, days, pinned);
        }

        /// <summary>The fewest guards on duty in any hour a post is manned (<paramref name="from"/> to <paramref name="to"/>, the same hour in both is all day).</summary>
        public static int Fewest(int[] strip, int from, int to)
        {
            int fewest = int.MaxValue;
            bool all = from == to;
            for (int h = 0; h < 24; h++)
                if (all || ((h - from) % 24 + 24) % 24 < ((to - from) % 24 + 24) % 24) fewest = Mathf.Min(fewest, strip[h]);
            return fewest;
        }

        public static string PinLabel(ShiftPin pin) => ("RP_ShiftPin_" + pin).Translate();

        /// <summary>The pin's next value on a click: auto, days, nights, auto.</summary>
        public static ShiftPin NextPin(ShiftPin pin) => (ShiftPin)(((int)pin + 1) % 3);

        /// <summary>A guard's pin is changed (by the warden): the strip is worked out again and the shifts are balanced at the next hour, as when the policy is switched.</summary>
        public static void SetPin(CompStaff sc, ShiftPin pin)
        {
            if (sc.shiftPin == pin) return;
            sc.shiftPin = pin;
            Invalidate();
            if (PrisonGame.Instance is { } game) game.lastShiftBalanceDay = -1;
        }

        /// <summary>The language changed: the strings kept with the counts are made again.</summary>
        private static void CheckLanguage()
        {
            if (language == LanguageDatabase.activeLanguage) return;
            language = LanguageDatabase.activeLanguage;
            System.Array.Clear(hourName, 0, 24);
            System.Array.Clear(barTip, 0, 24);
            (stripTip, heading) = (null, null);
        }

        /// <summary>The tooltip of a bar (made on the first hover after the counts or the language changed): the hour and the guards scheduled in it.</summary>
        internal static string BarTip(int h) => barTip[h] ??= "RP_RosterBarTip".Translate(hourName[h] ??= GuardPosts.HourLabel(h), counts[h]).Resolve();

        /// <summary>What the strip is, and what it does not show (on hover).</summary>
        internal static string StripTip(Map map)
        {
            int keptNow = KeptOnSite(map);
            if (stripTip == null || stripTipKept != keptNow)
            {
                stripTipKept = keptNow;
                string tip = "RP_RosterTip".Translate().Resolve();
                stripTip = keptNow > 0 ? tip + "\n\n" + "RP_RosterKeptTip".Translate(keptNow).Resolve() : tip;
            }
            return stripTip;
        }

        /// <summary>The heading beside the strip: what it shows, and the off-shift staff still on site if there are any.</summary>
        public static string Heading(Map map)
        {
            CheckLanguage();
            int keptNow = KeptOnSite(map);
            if (heading == null || headingKept != keptNow)
            {
                headingKept = keptNow;
                heading = (keptNow > 0 ? "RP_RosterHeadingKept".Translate(keptNow) : "RP_RosterHeading".Translate()).Resolve();
            }
            return heading;
        }

        /// <summary>The strip: a bar per hour, as high as the guards scheduled on duty in it, the present hour boxed. Its text is cached with the counts and its tooltips are made on hover.</summary>
        public static void DrawStrip(Rect rect, Map map)
        {
            int[] strip = Strip(map);
            CheckLanguage();
            tipMap = map;
            int max = 1;
            for (int h = 0; h < 24; h++) max = Mathf.Max(max, strip[h]);
            float labelH = 14f, barW = rect.width / 24f, top = rect.y, usable = rect.height - labelH - 2f;
            int hour = GenLocalDate.HourOfDay(map);
            for (int h = 0; h < 24; h++)
            {
                var slot = new Rect(rect.x + h * barW, top, barW, usable);
                Widgets.DrawBoxSolid(slot.ContractedBy(1f), new Color(1f, 1f, 1f, 0.05f));
                if (strip[h] > 0)
                {
                    float height = Mathf.Max(3f, usable * strip[h] / max);
                    Color c = strip[h] == 1 ? PrisonHUD.Warn : PrisonHUD.Good;
                    Widgets.DrawBoxSolid(new Rect(slot.x + 1f, slot.yMax - height, barW - 2f, height), c);
                }
                else Widgets.DrawBoxSolid(new Rect(slot.x + 1f, slot.yMax - 3f, barW - 2f, 3f), PrisonHUD.Bad);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(slot.x, slot.y + 1f, slot.width, 14f), countText[h]);
                GUI.color = h == hour ? Color.white : PrisonHUD.Muted;
                if (h % 3 == 0 || h == hour) Widgets.Label(new Rect(slot.x - 6f, slot.yMax + 1f, slot.width + 12f, labelH), axisText[h]);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                TooltipHandler.TipRegion(slot, barTipGetters[h], BarTipId + h);
            }
            Widgets.DrawBox(new Rect(rect.x + hour * barW, top, barW, usable));
            TooltipHandler.TipRegion(rect, stripTipGetter, StripTipId);
        }
    }

    /// <summary>
    /// The warden's hand in the vanilla Schedule tab (the only vanilla caller of <c>SetAssignment</c>, once for each cell clicked or dragged over; the paste button is patched below) changes a member of staff's timetable, and with it who the commute
    /// sends home: the strip is told (<see cref="Roster.Invalidate"/>). The prison's own hourly write is not told this way, <see cref="ShiftSystem.Apply"/> tells it once for the pass.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment))]
    public static class Patch_Pawn_TimetableTracker_SetAssignment
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (!ShiftSystem.Writing && ___pawn?.GetComp<CompStaff>() != null) Roster.Invalidate();
        }
    }

    /// <summary>The Schedule tab's paste button writes the 24 hours straight into <c>times</c>, past <c>SetAssignment</c>: the strip is told the same way.</summary>
    [HarmonyPatch(typeof(PawnColumnWorker_CopyPasteTimetable), "PasteTo")]
    public static class Patch_PawnColumnWorker_CopyPasteTimetable_PasteTo
    {
        public static void Postfix(Pawn p)
        {
            if (p?.GetComp<CompStaff>() != null) Roster.Invalidate();
        }
    }
}
