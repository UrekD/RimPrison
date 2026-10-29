using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Guard shifts (policy, default on): a third of the staff with guard duties work nights (sleep 10-18), the rest
    /// days (sleep 22-06), so the prison is never left unwatched at night. Other staff keep a day schedule. Every
    /// shift has two staggered one-hour breaks. The warden can pin a guard to days or nights (the roster: the balance leaves a pinned guard where
    /// they are and fills the third from the rest), and set a handover of 0-3 hours that keeps the day guards on past 22:00 at overtime pay.
    /// </summary>
    public static class ShiftSystem
    {
        private static readonly List<Pawn> tmpGuards = new List<Pawn>(), tmpMedics = new List<Pawn>();

        /// <summary>A doctor (not a guard, who has the work as well): a third of them work nights too, so that the injured are rescued, tended and fed after the day staff have gone home (the night guards are four, with the patrol, the fights and the searches).</summary>
        public static bool IsMedic(CompStaff sc) => sc.role is { guardDuties: false } role && role.workTypes.Contains(WorkTypeDefOf.Doctor);

        public static bool NightShift(Pawn p) => p.GetComp<CompStaff>() is { nightShift: true };

        /// <summary>The prison is writing a timetable itself (<see cref="Apply"/>): the patch on the vanilla setter leaves it to the pass to tell the roster once.</summary>
        public static bool Writing { get; private set; }

        /// <summary>The hour a day shift ends (its sleep begins): the handover keeps the day guards on from here.</summary>
        public const int DayShiftEnd = 22;

        /// <summary>The handover in force (Policy, 0-3 hours past <see cref="DayShiftEnd"/>).</summary>
        public static int HandoverHours => Mathf.Clamp(PrisonGame.Instance?.handoverHours ?? 0, 0, Roster.MaxHandover);

        /// <summary>A guard's pin, as the balance reads it: only a guard on the roster has one (a doctor, a cook or the warden has none).</summary>
        public static ShiftPin PinOf(CompStaff sc) => sc.role is { guardDuties: true, warden: false } ? sc.shiftPin : ShiftPin.Auto;

        /// <summary>The shift a pin sets (null: auto, the balance decides). With the shift policy off nobody works nights, pinned or not.</summary>
        public static bool? PinnedShift(CompStaff sc, bool policy) => PinOf(sc) switch { ShiftPin.Nights => policy, ShiftPin.Days => false, _ => null };

        /// <summary>A day guard whom the handover keeps past the end of the shift (a guard on the day shift, here permanently: not the night shift, not riot police, not the warden).</summary>
        public static bool StaysForHandover(CompStaff sc) => sc.role is { guardDuties: true, warden: false } && !sc.nightShift && sc.leaveAtTick < 0;

        /// <summary>Is this hour one of the handover's, for this guard: the hours from <see cref="DayShiftEnd"/> on, as many as the handover is set to.</summary>
        public static bool InHandover(CompStaff sc, int hour) => HandoverHours is > 0 and var hours && StaysForHandover(sc) && ((hour - DayShiftEnd) % 24 + 24) % 24 < hours;

        /// <summary>A guard's run of nights in a row as it stands on <paramref name="day"/>: the last night worked was last night or tonight's (a night missed - leave, a suspension, a day off - broke the run, whatever the count says).</summary>
        public static int NightRun(CompStaff sc, int day) => sc.lastNightDay >= day - 1 ? sc.nightsRun : 0;

        /// <summary>Puts a guard on a shift: going off nights after a run of them resented (<see cref="Roster.NightsAgainAfter"/>) is a fair rota (for a run that was current, not one an absence broke), and a run begins again.</summary>
        public static void SetShift(Pawn p, CompStaff sc, bool night) => SetShift(p, sc, night, Ledger.CurrentDay);

        /// <summary>As above, on <paramref name="day"/> (the day the run is judged on: the one the nights were tallied by).</summary>
        public static void SetShift(Pawn p, CompStaff sc, bool night, int day)
        {
            if (sc.nightShift == night) return;
            sc.nightShift = night;
            if (night) return;
            if (NightRun(sc, day) >= Roster.NightsAgainAfter) StaffMorale.FairRota(p);
            (sc.nightsRun, sc.lastNightDay) = (0, -1);
        }

        /// <summary>Once a day, for everyone employed: a night guard's run of nights is one longer if they worked the night before (resented from the <see cref="Roster.NightsAgainAfter"/>th), a day guard's is none.</summary>
        public static void Daily(Map map) => Daily(map, Ledger.CurrentDay);

        public static void Daily(Map map, int day)
        {
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>() is { } sc && Roster.OnRoster(sc) && StaffCareers.Available(sc)) TallyNight(p, sc, day);
        }

        /// <summary>The night just worked (<paramref name="day"/> is the day that begins at the tally) counts to the run if it follows the last one worked; after a night missed the run is one.</summary>
        public static void TallyNight(Pawn p, CompStaff sc, int day)
        {
            if (!sc.nightShift)
            {
                (sc.nightsRun, sc.lastNightDay) = (0, -1);
                return;
            }
            sc.nightsRun = sc.lastNightDay == day - 1 ? sc.nightsRun + 1 : 1;
            sc.lastNightDay = day;
            if (sc.nightsRun >= Roster.NightsAgainAfter) StaffMorale.NightsAgain(p);
        }

        /// <summary>
        /// A guard of the day shift who is working an hour of the handover: kept on past the end of the shift and on site, by the commute's own test (<see cref="StaffCommute.ShouldBeHome"/>, so not
        /// suspended or on leave) and not a hostage (held on site, not working).
        /// </summary>
        public static bool WorksHandoverHour(Pawn p, CompStaff sc, int hour) =>
            p.Spawned && InHandover(sc, hour) && StaffCareers.Available(sc) && !StaffCommute.ShouldBeHome(p, sc, hour) && !HostageCrisis.IsHeld(p);

        /// <summary>
        /// Hourly, after the commute: in a handover hour, each day guard who is working it earns an hour of overtime, added to the one total the midnight settlement pays and clears
        /// (<see cref="PrisonGame.overtimeOwed"/>: one total, not per guard, so a guard fired or sent home before midnight is still paid for the hours they worked). The hour is paid as it begins, at the
        /// guard's wage then; an hour the settings or a guard's state do not make a handover hour earns nothing, and the settings at midnight do not matter.
        /// </summary>
        public static void AccrueOvertime(Map map, PrisonGame game, int hour)
        {
            if (HandoverHours == 0 || ((hour - DayShiftEnd) % 24 + 24) % 24 >= HandoverHours) return;
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                if (p.GetComp<CompStaff>() is not { } sc || !WorksHandoverHour(p, sc, hour)) continue;
                game.overtimeOwed += StaffCareers.OvertimeHour(sc);
                game.overtimeHours++;
            }
        }

        /// <summary>A guard who can work a shift now: on the guard roster, not suspended, not on leave (burnout rest or a course).</summary>
        public static bool AvailableGuard(Pawn p) => GuardUtility.IsGuard(p) && p.GetComp<CompStaff>() is { } sc && StaffCareers.Available(sc);

        /// <summary>The balance runs once a day (the first hour of each day, and again at once after the policy is switched), so timetables do not flap as guards fall ill, are suspended and come back.</summary>
        public static bool BalanceDue(PrisonGame game)
        {
            int day = Ledger.CurrentDay;
            if (game.lastShiftBalanceDay == day) return false;
            game.lastShiftBalanceDay = day;
            return true;
        }

        /// <summary>Hourly: write the shifts into the timetables; once a day, balance the guards across them.</summary>
        public static void Balance(Map map, PrisonGame game)
        {
            bool due = BalanceDue(game);
            tmpGuards.Clear();
            tmpMedics.Clear();
            // Everyone employed, on site or at home (guards at home still count for the split).
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                if (sc.leaveAtTick >= 0) continue;
                // The warden keeps their own hours: a day, no night shift, never rebalanced.
                if (sc.role.warden)
                {
                    SetShift(p, sc, false);
                    Apply(p, false);
                    continue;
                }
                if (IsMedic(sc))
                {
                    if (StaffCareers.Available(sc)) tmpMedics.Add(p);
                    else Apply(p, sc.nightShift); // away: keeps their shift for when they are back, as a guard does
                }
                else if (!sc.role.guardDuties)
                {
                    // Not a guard: days (and back to days if they used to be one).
                    SetShift(p, sc, false);
                    Apply(p, false);
                }
                else if (AvailableGuard(p)) tmpGuards.Add(p);
                else Pinned(p, sc, game); // away: keeps their shift for when they are back (a pin is kept at once: it never flaps)
            }
            if (due)
            {
                Rebalance(tmpGuards, game.guardNightShift);
                Rebalance(tmpMedics, game.guardNightShift, MedicsForANightDoctor);
            }
            else
            {
                foreach (Pawn p in tmpGuards) Pinned(p, p.GetComp<CompStaff>(), game);
                foreach (Pawn p in tmpMedics) Apply(p, NightShift(p));
            }
            // The timetables are written: the roster's strip is worked out from them again.
            Roster.Invalidate();
        }

        /// <summary>Between balances, a pinned guard is where the pin puts them (a pin the warden has just set, or a guard back from leave); the timetable is written as it is.</summary>
        private static void Pinned(Pawn p, CompStaff sc, PrisonGame game)
        {
            if (PinnedShift(sc, game.guardNightShift) is { } pinned) SetShift(p, sc, pinned);
            Apply(p, sc.nightShift);
        }

        /// <summary>Doctors needed before one of them works nights: with two, half would, and the day (the busier half) would have one.</summary>
        public const int MedicsForANightDoctor = 3;

        /// <summary>
        /// Puts the available guards (or doctors) on shifts: the pinned ones where they are pinned (with the policy off, on days), and a third of all of them on nights, the rest of the third filled from the
        /// unpinned (stable: only when the split is uneven).
        /// </summary>
        /// <param name="minimum">How many there must be for one of them to work nights.</param>
        public static void Rebalance(List<Pawn> guards, bool nightShiftPolicy, int minimum = 2)
        {
            // A third of the guards on nights (at least one once there are two: staff go home, so nobody else is
            // there at night); days are busier. A guard pinned to nights is one of the third.
            int wanted = !nightShiftPolicy || guards.Count < minimum ? 0 : System.Math.Max(1, guards.Count / 3);
            var unpinned = new List<Pawn>(guards.Count);
            foreach (Pawn p in guards)
            {
                CompStaff sc = p.GetComp<CompStaff>();
                if (PinnedShift(sc, nightShiftPolicy) is { } pinned)
                {
                    SetShift(p, sc, pinned);
                    if (pinned) wanted--;
                    Apply(p, pinned);
                }
                else unpinned.Add(p);
            }
            wanted = System.Math.Max(0, wanted);
            int night = unpinned.Count(NightShift);
            foreach (Pawn p in unpinned.OrderBy(x => x.thingIDNumber))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                if (night > wanted && sc.nightShift)
                {
                    SetShift(p, sc, false);
                    night--;
                }
                else if (night < wanted && !sc.nightShift)
                {
                    SetShift(p, sc, true);
                    night++;
                }
                Apply(p, sc.nightShift);
            }
        }

        /// <summary>
        /// Writes the shift into the vanilla timetable: 8 hours' sleep, and two one-hour breaks in the 16-hour shift
        /// (recreation; without them staff end up recreation-starved and break down). Breaks are staggered by person
        /// so the whole shift is never on break at once. A day guard works the handover's hours past the end of the shift (sleep begins after them).
        /// </summary>
        public static void Apply(Pawn p, bool night)
        {
            if (p.timetable == null) return;
            int start = night ? 18 : 6;
            CompStaff sc = p.GetComp<CompStaff>();
            int handover = !night && sc != null && StaysForHandover(sc) ? HandoverHours : 0;
            int k = p.thingIDNumber % 3;
            int break1 = (start + 5 + k) % 24, break2 = (start + 10 + k) % 24;
            // The last hour before the shift is the commute in (they arrive and settle in before it starts).
            int commute = (start + 23) % 24;
            bool changed = false;
            Writing = true;
            try
            {
                for (int h = 0; h < 24; h++)
                {
                    bool late = ((h - DayShiftEnd) % 24 + 24) % 24 < handover;
                    bool asleep = night ? h >= 10 && h < 18 : !late && (h >= DayShiftEnd || h < 6);
                    TimeAssignmentDef want = h == commute ? TimeAssignmentDefOf.Anything
                        : asleep ? TimeAssignmentDefOf.Sleep : h == break1 || h == break2 ? TimeAssignmentDefOf.Joy : TimeAssignmentDefOf.Work;
                    if (p.timetable.GetAssignment(h) == want) continue;
                    p.timetable.SetAssignment(h, want);
                    changed = true;
                }
            }
            finally
            {
                Writing = false;
            }
            // (Once for the pass, not for each hour it wrote: the patch on the vanilla setter is for the warden's hand.)
            if (changed) Roster.Invalidate();
        }
    }
}
