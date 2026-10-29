using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPRegimeDefOf
    {
        public static RegimeActivityDef RP_Eat;

        static RPRegimeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRegimeDefOf));
    }

    /// <summary>What the warden can order for one level for one day, on top of the timetable (see <see cref="RegimeOrders"/>).</summary>
    public enum DayOrder
    {
        /// <summary>The yard is called off: the day's free time (and the supermax yard hour) is spent in the cell.</summary>
        YardOff,
        /// <summary>Early lockup: from <see cref="RegimeOrders.EarlyLockupHour"/> (or now, if later) everything but sleep, meals and showers is a lockup.</summary>
        EarlyLockup,
        /// <summary>A double shower: one more hour of the day is a shower (see <see cref="RegimeOrders.ShowerHourFor"/>).</summary>
        DoubleShower,
    }

    /// <summary>One level's orders for one day. Saved (a load inside the day keeps them), with defaults; they go at midnight (<see cref="RegimeOrders.Sweep"/>).</summary>
    public class DayOrders : IExposable
    {
        /// <summary>The day they were given for (<see cref="Ledger.CurrentDay"/>).</summary>
        public int day = -1;
        public bool yardOff;
        /// <summary>The hour the early lockup starts at (-1: none).</summary>
        public int lockupFrom = -1;
        /// <summary>The hour that is a second shower (-1: none).</summary>
        public int showerHour = -1;
        /// <summary>A restriction (<see cref="RegimeOrders.Restrict"/>): the last day the orders are carried to (-1: they are today's only). Midnight carries them over until then.</summary>
        public int untilDay = -1;
        /// <summary>A restriction: the tick the day being served counts from for its suppression (-1: none). The day is credited when it ends, or pro rata when the restriction is lifted (<see cref="RegimeOrders.Credit"/>).</summary>
        public int creditFromTick = -1;

        public bool Any => yardOff || lockupFrom >= 0 || showerHour >= 0;

        /// <summary>The orders keep the level in its cells (the yard called off or an early lockup: a restriction is these two, a second shower is not).</summary>
        public bool Confining => yardOff || lockupFrom >= 0;

        public bool Has(DayOrder order) => order switch
        {
            DayOrder.YardOff => yardOff,
            DayOrder.EarlyLockup => lockupFrom >= 0,
            _ => showerHour >= 0,
        };

        public void ExposeData()
        {
            Scribe_Values.Look(ref day, "day", -1);
            Scribe_Values.Look(ref yardOff, "yardOff");
            Scribe_Values.Look(ref lockupFrom, "lockupFrom", -1);
            Scribe_Values.Look(ref showerHour, "showerHour", -1);
            Scribe_Values.Look(ref untilDay, "untilDay", -1);
            Scribe_Values.Look(ref creditFromTick, "creditFromTick", -1);
        }

        public string Describe() => $"{day}:{yardOff}:{lockupFrom}:{showerHour}:{untilDay}:{creditFromTick}";
    }

    /// <summary>The days a level was given an order that keeps it in (see <see cref="RegimeOrders.Confines"/>), the last <see cref="RegimeOrders.HabitDays"/> kept. Saved.</summary>
    public class OrderDays : IExposable
    {
        /// <summary>The days (<see cref="Ledger.CurrentDay"/>), each once.</summary>
        public List<int> days = new List<int>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref days, "days", LookMode.Value);
            days ??= new List<int>();
        }
    }

    /// <summary>
    /// Today's orders: between "nothing" and a lockdown, the warden can call the yard off, lock a level up early or give it a second shower for the rest of the day. An order is a one-day override of the level's timetable
    /// (<see cref="PrisonGame.SlotAt"/> reads it), written to the chronicle when it is given and when it goes (at midnight: <see cref="Sweep"/>, run by the hour pass with the day each order was stamped with, so a day
    /// that is skipped over still ends them). The timetable itself is never touched.
    /// </summary>
    public static class RegimeOrders
    {
        /// <summary>The hour an early lockup starts at when it is ordered earlier in the day.</summary>
        public const int EarlyLockupHour = 17;

        /// <summary>The slot the timetable's <paramref name="scheduled"/> becomes under the orders (the same one with no orders).</summary>
        public static RegimeActivityDef Apply(RegimeActivityDef scheduled, int hour, DayOrders o)
        {
            if (o == null) return scheduled;
            RegimeActivityDef act = scheduled;
            if (o.yardOff && (act == RPDefOf.RP_FreeTime || act == RPSupermaxDefOf.RP_YardAlone)) act = RPDefOf.RP_Lockup;
            // (Sleep, the meals and the showers the timetable has stay: a lockup is not the cell with nothing to eat or wash.)
            if (o.lockupFrom >= 0 && hour >= o.lockupFrom && act != RPDefOf.RP_Sleep && act != RPRegimeDefOf.RP_Eat && act != RPDefOf.RP_ShowerTime) act = RPDefOf.RP_Lockup;
            if (o.showerHour == hour) act = RPDefOf.RP_ShowerTime;
            return act;
        }

        /// <summary>An order given on more than <see cref="HabitOver"/> of the last <see cref="HabitDays"/> days for a level is a habit, not an emergency: the inspector writes it down and the weekly report says it.</summary>
        public const int HabitDays = 7, HabitOver = 3;

        /// <summary>Whether the order keeps the level in its cells (the yard called off, an early lockup), the ones a habit is counted of: a second shower is not.</summary>
        public static bool Confines(DayOrder order) => order != DayOrder.DoubleShower;

        /// <summary>On how many of the last <see cref="HabitDays"/> days (today's included) the level was given an order that keeps it in.</summary>
        public static int DaysOrdered(PrisonGame game, SecurityLevelDef level)
        {
            if (level == null || !game.orderDays.TryGetValue(level, out OrderDays h)) return 0;
            int today = Ledger.CurrentDay;
            return h.days.Count(d => d > today - HabitDays && d <= today);
        }

        public static bool Habit(PrisonGame game, SecurityLevelDef level) => DaysOrdered(game, level) > HabitOver;

        /// <summary>The levels with inmates in them (not one that is kept in by design) that were given an order that keeps them in on more than <see cref="HabitOver"/> of the last <see cref="HabitDays"/> days, the most days first, with the days.</summary>
        public static List<(SecurityLevelDef level, int days)> HabitLevels(PrisonGame game, Map map)
        {
            var found = new List<(SecurityLevelDef, int)>();
            if (game.orderDays.Count == 0) return found;
            foreach (SecurityLevelDef level in game.orderDays.Keys.OrderBy(l => l.uiOrder))
            {
                int days = DaysOrdered(game, level);
                if (days <= HabitOver || InspectionReport.LockedByDesign(level)) continue;
                if (map.mapPawns.PrisonersOfColonySpawned.Any(p => InmateUtility.IsActiveInmate(p) && p.Inmate().security == level)) found.Add((level, days));
            }
            return found.OrderByDescending(f => f.Item2).ToList();
        }

        private static void NoteGiven(PrisonGame game, SecurityLevelDef level)
        {
            int today = Ledger.CurrentDay;
            if (!game.orderDays.TryGetValue(level, out OrderDays h)) game.orderDays[level] = h = new OrderDays();
            h.days.RemoveAll(d => d <= today - HabitDays);
            if (!h.days.Contains(today)) h.days.Add(today);
        }

        /// <summary>The orders in force for the level now (null: none).</summary>
        public static DayOrders Of(PrisonGame game, SecurityLevelDef level) =>
            level != null && game.dayOrders.Count > 0 && game.dayOrders.TryGetValue(level, out DayOrders o) && o.Any ? o : null;

        /// <summary>
        /// The hour the second shower takes: the first one after the current hour that the day has for something other than sleep, a meal or a shower (as the orders in force already make it), or -1 when the rest of
        /// the day has none. Never an hour a count is called at (the level would be in the showers for it).
        /// </summary>
        public static int ShowerHourFor(PrisonGame game, SecurityLevelDef level, int hour)
        {
            RegimeSchedule sched = game.RegimeFor(level);
            DayOrders o = Of(game, level);
            List<int> counts = CountSystem.Enabled(game) ? CountSystem.Hours(game) : null;
            for (int h = hour + 1; h < 24; h++)
            {
                if (counts != null && counts.Contains(h)) continue;
                RegimeActivityDef act = Apply(sched.At(h), h, o);
                if (act != RPDefOf.RP_Sleep && act != RPRegimeDefOf.RP_Eat && act != RPDefOf.RP_ShowerTime) return h;
            }
            return -1;
        }

        /// <summary>Why the order cannot be given now (null: it can). Only the second shower can be turned away: a day that has no hour left for it.</summary>
        public static string Unavailable(PrisonGame game, Map map, SecurityLevelDef level, DayOrder order) =>
            order == DayOrder.DoubleShower && !(Of(game, level)?.Has(order) ?? false) && ShowerHourFor(game, level, GenLocalDate.HourOfDay(map)) < 0 ? "RP_OrderNoHourLeft".Translate().Resolve() : null;

        /// <summary>Gives (or, when it is already given, withdraws) the order for the level: the chronicle line, and the inmates of the level are sent where the slot now says.</summary>
        public static bool Toggle(PrisonGame game, Map map, SecurityLevelDef level, DayOrder order)
        {
            if (Of(game, level)?.Has(order) ?? false)
            {
                Lift(game, level, order);
                return false;
            }
            return Give(game, map, level, order);
        }

        /// <summary>Gives the order for the level for the rest of today. <paramref name="talk"/>: whether the wing may start a rumour of it (a restriction's two orders do not: it rolls once for itself, see <see cref="Restrict"/>).</summary>
        public static bool Give(PrisonGame game, Map map, SecurityLevelDef level, DayOrder order, bool talk = true)
        {
            if (level == null || (Of(game, level)?.Has(order) ?? false) || Unavailable(game, map, level, order) != null) return false;
            int day = Ledger.CurrentDay, hour = GenLocalDate.HourOfDay(map);
            var was = PlacedBefore(level);
            if (!game.dayOrders.TryGetValue(level, out DayOrders o) || o.day != day) game.dayOrders[level] = o = new DayOrders { day = day };
            int shower = order == DayOrder.DoubleShower ? ShowerHourFor(game, level, hour) : -1;
            switch (order)
            {
                case DayOrder.YardOff: o.yardOff = true; break;
                case DayOrder.EarlyLockup: o.lockupFrom = Mathf.Max(EarlyLockupHour, hour); break;
                default: o.showerHour = shower; break;
            }
            if (Confines(order)) NoteGiven(game, level);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, ("RP_Chr_Order_" + order).Translate(level.label, order == DayOrder.EarlyLockup ? o.lockupFrom : shower));
            Changed(level, was);
            // (An order that keeps the wing in is talked about on it: the rumour mill.)
            if (talk && order == DayOrder.YardOff) Rumours.Notify_Order(map, level, RumourKind.YardOff);
            else if (talk && order == DayOrder.EarlyLockup) Rumours.Notify_Order(map, level, RumourKind.EarlyLockup);
            return true;
        }

        public static void Lift(PrisonGame game, SecurityLevelDef level, DayOrder order)
        {
            if (!game.dayOrders.TryGetValue(level, out DayOrders o)) return;
            var was = PlacedBefore(level);
            // (A restriction is the yard off and the early lockup together: lifting either ends it, and what is left of the two is today's order only.)
            bool restriction = o.untilDay >= 0 && Confines(order) && o.Has(order);
            switch (order)
            {
                case DayOrder.YardOff: o.yardOff = false; break;
                case DayOrder.EarlyLockup: o.lockupFrom = -1; break;
                default: o.showerHour = -1; break;
            }
            if (restriction)
            {
                Credit(o, Find.TickManager.TicksGame, level);
                (o.untilDay, o.creditFromTick) = (-1, -1);
            }
            if (!o.Any) game.dayOrders.Remove(level);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, ("RP_Chr_OrderLifted_" + order).Translate(level.label));
            Changed(level, was);
            // (What the wing says of the order no longer holds: the rumour goes on, false now, and a calm word can end it.)
            Rumours.Notify_OrderOver(level, order == DayOrder.YardOff, order == DayOrder.EarlyLockup, restriction);
            // (A restriction the warden lifts before its last day is over now: what it held down lands now, for the days it ran.)
            if (restriction) RiotReview.Notify_RestrictionEnded(game, level, early: true);
        }

        /// <summary>The level is under a restriction now (orders carried over day by day, see <see cref="Restrict"/>): the yard off or the early lockup still given with it. One lookup.</summary>
        public static bool Restricted(PrisonGame game, SecurityLevelDef level) => Of(game, level) is { untilDay: >= 0, Confining: true } o && o.untilDay >= Ledger.CurrentDay;

        /// <summary>
        /// A restriction for <paramref name="days"/> days, today the first (the riot review's answer): the yard called off and an early lockup, carried over at each midnight until the last day (<see cref="Sweep"/>). Each day of it
        /// counts as a day ordered in (the habit the inspector writes down) and, once it has been served, holds the wing down a little more (<see cref="Credit"/>): nothing is held down up front.
        /// </summary>
        public static bool Restrict(PrisonGame game, Map map, SecurityLevelDef level, int days)
        {
            if (level == null || days < 1) return false;
            if (!(Of(game, level)?.yardOff ?? false)) Give(game, map, level, DayOrder.YardOff, talk: false);
            if (!(Of(game, level)?.Has(DayOrder.EarlyLockup) ?? false)) Give(game, map, level, DayOrder.EarlyLockup, talk: false);
            if (Of(game, level) is not { yardOff: true } o || o.lockupFrom < 0) return false;
            o.untilDay = Mathf.Max(o.untilDay, Ledger.CurrentDay + days - 1);
            if (o.creditFromTick < 0) o.creditFromTick = Find.TickManager.TicksGame;
            // (The wing talks of the restriction, once: not of its yard order and its lockup each.)
            Rumours.Notify_Restriction(map, level);
            return true;
        }

        /// <summary>
        /// A restriction's suppression for the time served since <see cref="DayOrders.creditFromTick"/> (a whole day's worth for a whole day, pro rata for less: a restriction given at 23:00 or lifted at once holds the wing
        /// down for the hour it ran), and the next stretch counts from <paramref name="now"/>.
        /// </summary>
        internal static void Credit(DayOrders o, int now, SecurityLevelDef level)
        {
            if (o.creditFromTick < 0) return;
            float served = Mathf.Clamp01((now - o.creditFromTick) / (float)GenDate.TicksPerDay);
            o.creditFromTick = now;
            if (served > 0f) RiotReview.Notify_RestrictionServed(level, served);
        }

        /// <summary>
        /// The orders of a day that is over go (a chronicle line for each level: "back to the regime"). Run by the hour pass, after the day's last hour has been counted. It does not interrupt anyone (the hour pass
        /// does, through the queue, from what each inmate's slot was under the orders and is now).
        /// </summary>
        public static void Sweep(PrisonGame game, Map map)
        {
            if (game.dayOrders.Count == 0) return;
            int day = Ledger.CurrentDay, now = Find.TickManager.TicksGame;
            // A restriction runs on to its last day: the day served is credited (the wing held down for it), and the new day takes its yard order and its lockup (from the usual hour), not a second shower, and is counted
            // as a day ordered in. (Only while one of the two that confine is still given: a restriction left with a second shower alone is over, see Lift.)
            List<SecurityLevelDef> carried = game.dayOrders.Where(kv => kv.Value.day != day && kv.Value.Confining && kv.Value.untilDay >= day).Select(kv => kv.Key).ToList();
            foreach (SecurityLevelDef level in carried)
            {
                DayOrders o = game.dayOrders[level];
                Credit(o, now, level);
                o.day = day;
                o.showerHour = -1;
                if (o.lockupFrom >= 0) o.lockupFrom = EarlyLockupHour;
                NoteGiven(game, level);
                Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_RestrictionGoesOn".Translate(level.label, Words(o), o.untilDay - day + 1));
            }
            List<SecurityLevelDef> over = game.dayOrders.Where(kv => kv.Value.day != day || !kv.Value.Any).Select(kv => kv.Key).ToList();
            var ended = new List<SecurityLevelDef>();
            foreach (SecurityLevelDef level in over)
            {
                DayOrders o = game.dayOrders[level];
                game.dayOrders.Remove(level);
                if (o.Any) Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_OrdersLapsed".Translate(level.label, Words(o)));
                // (The talk of today's yard order or lockup, or of a restriction now over, no longer holds.)
                Rumours.Notify_OrderOver(level, o.yardOff, o.lockupFrom >= 0, o.untilDay >= 0);
                // (A restriction's last day is served: credited, and then it is over.)
                if (o.untilDay < 0) continue;
                Credit(o, now, level);
                ended.Add(level);
            }
            // (The tension a restriction held down lands when it is over, as a lid's does.)
            foreach (SecurityLevelDef level in ended) RiotReview.Notify_RestrictionEnded(game, level, early: false);
            // (Nobody is sent anywhere here: the hour pass compares each inmate's slot before and after, and queues the ones it moved, a few to a tick.)
            if (over.Count > 0 || carried.Count > 0) DoorsChanged();
        }

        private static void DoorsChanged()
        {
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome) PrisonMap.Of(m)?.Notify_DoorsChanged();
        }

        /// <summary>The orders in a few words, as the chronicle gives them ("the yard called off, early lockup").</summary>
        public static string Words(DayOrders o, bool withHours = false)
        {
            var parts = new List<string>();
            if (o.yardOff) parts.Add("RP_Order_YardOff".Translate().Resolve());
            if (o.lockupFrom >= 0) parts.Add((withHours ? "RP_OrderAt_EarlyLockup".Translate(o.lockupFrom) : "RP_Order_EarlyLockup".Translate()).Resolve());
            if (o.showerHour >= 0) parts.Add((withHours ? "RP_OrderAt_DoubleShower".Translate(o.showerHour) : "RP_Order_DoubleShower".Translate()).Resolve());
            return string.Join(", ", parts);
        }

        /// <summary>The whole hours left of the day, when the orders lapse (at least 1).</summary>
        public static int HoursToMidnight(Map map) => Mathf.Max(1, Mathf.CeilToInt((1f - GenLocalDate.DayPercent(map)) * 24f));

        /// <summary>
        /// What each inmate the regime places in the level is doing the slot of, now (taken before an edit, for <see cref="Changed"/>). Not the ones on their way out (a transfer or a release waiting for the shuttle is placed
        /// by the shuttle, not by the timetable); the ones the regime does not move (the hole, a punishment in the cell, a supermax walk, a lockdown) read the same slot after any edit, and carried ones are not on the map.
        /// </summary>
        public static Dictionary<Pawn, RegimeActivityDef> PlacedBefore(SecurityLevelDef level)
        {
            var was = new Dictionary<Pawn, RegimeActivityDef>();
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return was;
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                int hour = GenLocalDate.HourOfDay(m);
                foreach (Pawn p in m.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate()?.security == level && InmateUtility.IsActiveInmate(p) && !ShuttleSystem.OnTheWayOut(p)) was[p] = game.ActivityAt(p.Inmate(), hour);
            }
            return was;
        }

        /// <summary>
        /// Edits to the current hour take effect: the inmates whose activity now is not the one in <paramref name="was"/> (<see cref="PlacedBefore"/>, taken before the edit) are queued to look for what the slot says, the way
        /// the hour pass does it (a few to a tick, once each, and only if what they are doing is something a regime change ends), and the doors follow. An edit that changes nothing for an inmate does not touch them.
        /// </summary>
        public static void Changed(SecurityLevelDef level, Dictionary<Pawn, RegimeActivityDef> was)
        {
            PrisonGame game = PrisonGame.Instance;
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                PrisonMap pm = PrisonMap.Of(m);
                if (pm != null && game != null)
                {
                    int hour = GenLocalDate.HourOfDay(m);
                    foreach (KeyValuePair<Pawn, RegimeActivityDef> kv in was)
                    {
                        Pawn p = kv.Key;
                        if (p.Map == m && InmateUtility.IsActiveInmate(p) && !ShuttleSystem.OnTheWayOut(p) && game.ActivityAt(p.Inmate(), hour) != kv.Value) pm.QueueRegimeInterrupt(p);
                    }
                }
                pm?.Notify_DoorsChanged();
            }
        }
    }
}
