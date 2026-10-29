using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for the roster (<see cref="Roster"/>, gap analysis 6, item 2): a pinned guard is where the pin puts them after the next balance (and the unpinned keep the one-third rule), the handover keeps the
    /// day shift on to the hour set and pays the hours worked (accrued as they are worked, not read off the settings at midnight), the strip is the schedule the commute goes by (worked out once, again on each
    /// change it depends on, and its texts are kept, not made per frame) and the commute's kept-on-site staff are told beside it, and a guard on nights for seven nights running has the thought, a fair rota
    /// the other (a night missed breaks the run). Each is self-contained and rolled back (<see cref="Rollback"/>): the pins and the runs are on the check's own throwaway guards, and the real staff are put on
    /// leave or have their timetables put back. The saveload plan's seed and snapshot are at the end.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckRoster(Map map)
        {
            // (Staff at home count as there before: the checks tick the clock on, and one whose shift begins comes in through the gate by the prison's own pass.)
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns.Concat(PrisonGame.Instance.OffSite));
            CheckRosterPins(map);
            CheckRosterHandover(map);
            CheckRosterOvertimePay(map);
            for (int pre = 0; pre <= Roster.MaxHandover; pre++) CheckRosterStrip(map, pre);
            CheckRosterStripCache(map);
            CheckRosterPresence(map);
            CheckRosterThoughts(map);
            Pawn[] left = LeftBehind(map, pawnsBefore).ToArray();
            if (left.Length > 0) problems0.Add($"roster checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void RosterFail(string check, string what) => problems0.Add($"roster ({check}): {what}");

        private static List<Pawn> RosterGuards(Map map, Rollback r, int count)
        {
            var guards = new List<Pawn>();
            for (int i = 0; i < count; i++) guards.Add(ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard));
            guards = guards.OrderBy(x => x.thingIDNumber).ToList();
            foreach (Pawn p in guards) (p.GetComp<CompStaff>().nightShift, p.GetComp<CompStaff>().shiftPin, p.GetComp<CompStaff>().nightsRun, p.GetComp<CompStaff>().lastNightDay) = (false, ShiftPin.Auto, 0, -1);
            return guards;
        }

        private static string RosterNights(List<Pawn> guards) => string.Join("", guards.Select(x => ShiftSystem.NightShift(x) ? "N" : "d"));

        /// <summary>
        /// The prison's real staff (the warden aside) go on leave for the rest of the check, so that what reads the whole staff - the balance, the overtime - sees only the check's own. Call it before the check makes
        /// its guards; the leaves are put back by the rollback.
        /// </summary>
        private static void RosterRealStaffAway(Map map, Rollback r)
        {
            List<(CompStaff sc, int until)> real = StaffCommute.AllStaff(map).Select(p => p.GetComp<CompStaff>()).Where(sc => !sc.role.warden).Select(sc => (sc, sc.leaveUntilTick)).ToList();
            int until = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay;
            foreach ((CompStaff sc, _) in real) sc.leaveUntilTick = until;
            Roster.Invalidate();
            r.Add(() =>
            {
                foreach ((CompStaff sc, int was) in real) sc.leaveUntilTick = was;
                Roster.Invalidate();
            });
        }

        /// <summary>
        /// The prison's real staff (the warden aside) set aside for a check that runs the commute, without anyone leaving the map: those on site are made temporary callout staff (<see cref="CompStaff.leaveAtTick"/>, which the
        /// commute, the roster's strip and the roster's kept-on-site count all leave out, as they do riot police), those at home are put on leave so that the commute's pass does not bring them in. Nobody is sent home or
        /// brought back, so who is spawned, and in what order the map lists them, is what it was (a staff member sent home and back is listed last, which put the warden first among the staff, the one the careers check
        /// then picked to promote). The flags are put back by the rollback. While set aside, the on-site ones are callout staff for the presence check's ticks: they skip their hourly corruption roll and the selectors that leave callouts out (<c>Story/Kin.cs</c>, <c>Events/Unrest.cs</c>, <c>Story/Hostage.cs</c>), a few hundred ticks of that; nothing persists.
        /// </summary>
        private static void RosterRealStaffAside(Map map, Rollback r)
        {
            var real = StaffCommute.AllStaff(map).Select(p => (p, sc: p.GetComp<CompStaff>())).Where(x => !x.sc.role.warden && x.sc.leaveAtTick < 0).Select(x => (x.sc, onSite: x.p.Spawned, until: x.sc.leaveUntilTick)).ToList();
            int far = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay;
            foreach ((CompStaff sc, bool onSite, _) in real)
            {
                if (onSite) sc.leaveAtTick = far;
                else sc.leaveUntilTick = far;
            }
            Roster.Invalidate();
            StaffCareers.InvalidateLeads();
            r.Add(() =>
            {
                foreach ((CompStaff sc, bool onSite, int until) in real)
                {
                    if (onSite) sc.leaveAtTick = -1;
                    else sc.leaveUntilTick = until;
                }
                Roster.Invalidate();
                StaffCareers.InvalidateLeads();
            });
        }

        /// <summary>The real guards' timetables written as the hourly pass writes them, at the handover now set (<see cref="ShiftSystem.Apply"/>).</summary>
        private static void RosterRewriteTimetables(Map map)
        {
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>() is { } sc && Roster.OnRoster(sc)) ShiftSystem.Apply(p, sc.nightShift);
            Roster.Invalidate();
        }

        /// <summary>The real guards' timetables and the handover as they are, and the undo that puts both back.</summary>
        private static void RosterKeepTimetables(Map map, Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            int hours0 = g.handoverHours;
            var saved = StaffCommute.AllStaff(map).Where(p => p.timetable != null).Select(p => (p, Enumerable.Range(0, 24).Select(h => p.timetable.GetAssignment(h)).ToArray())).ToList();
            r.Add(() =>
            {
                g.handoverHours = hours0;
                foreach ((Pawn p, TimeAssignmentDef[] slots) in saved)
                    for (int h = 0; h < 24; h++)
                        if (p.timetable != null && p.timetable.GetAssignment(h) != slots[h]) p.timetable.SetAssignment(h, slots[h]);
                Roster.Invalidate();
            });
        }

        // ---- the pins ---------------------------------------------------------------------------------------------------

        private static void CheckRosterPins(Map map)
        {
            Guarded("roster: a pinned guard stays where they are pinned", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                // (The prison's own staff are on leave: the balance below sees six guards, the check's, and a third of them is two.)
                RosterRealStaffAway(map, r);
                List<Pawn> gs = RosterGuards(map, r, 6);
                CompStaff Sc(int i) => gs[i].GetComp<CompStaff>();
                const string check = "pins";
                (bool policy0, int day0) = (g.guardNightShift, g.lastShiftBalanceDay);
                r.Add(() => (g.guardNightShift, g.lastShiftBalanceDay) = (policy0, day0));

                // Nobody pinned: a third of six on nights (two), and a second balance changes nothing (the old behaviour, which an old save has).
                ShiftSystem.Rebalance(gs, true);
                string first = RosterNights(gs);
                ShiftSystem.Rebalance(gs, true);
                if (first.Count(c => c == 'N') != 2 || RosterNights(gs) != first) RosterFail(check, $"six unpinned guards were put on {first} and then {RosterNights(gs)} (two on nights, and stable)");

                // The lowest guard pinned to days (they would have been the first on nights) and the highest to nights: the highest is one of the third, the lowest of the unpinned fills the other place.
                for (int i = 0; i < 6; i++) Sc(i).nightShift = false;
                Sc(0).shiftPin = ShiftPin.Days;
                Sc(5).shiftPin = ShiftPin.Nights;
                ShiftSystem.Rebalance(gs, true);
                if (RosterNights(gs) != "dNdddN") RosterFail(check, $"with the first pinned to days and the last to nights the guards are {RosterNights(gs)} (want dNdddN: the pinned night guard is one of the third, the lowest unpinned the other)");

                // Daily balance after daily balance, the pins hold, even from the other shift and with the unpinned on nights already.
                for (int i = 0; i < 6; i++) Sc(i).nightShift = i is >= 1 and <= 4;
                ShiftSystem.Rebalance(gs, true);
                ShiftSystem.Rebalance(gs, true);
                string held = RosterNights(gs);
                if (held[0] != 'd' || held[5] != 'N' || held.Count(c => c == 'N') != 2) RosterFail(check, $"after two balances from a lopsided start the guards are {held} (the first pinned to days, the last to nights, two on nights in all)");

                // Everyone pinned to days: nobody on nights (what the checkbox already allows); a pin to nights with the policy off: nobody either (the warden's switch beats a pin).
                foreach (Pawn p in gs) (p.GetComp<CompStaff>().shiftPin, p.GetComp<CompStaff>().nightShift) = (ShiftPin.Days, true);
                ShiftSystem.Rebalance(gs, true);
                if (gs.Any(ShiftSystem.NightShift)) RosterFail(check, $"with every guard pinned to days {RosterNights(gs)} are on nights");
                foreach (Pawn p in gs) p.GetComp<CompStaff>().shiftPin = ShiftPin.Nights;
                ShiftSystem.Rebalance(gs, false);
                if (gs.Any(ShiftSystem.NightShift)) RosterFail(check, $"with the shift policy off and every guard pinned to nights {RosterNights(gs)} are on nights");
                ShiftSystem.Rebalance(gs, true);
                if (gs.Any(x => !ShiftSystem.NightShift(x))) RosterFail(check, $"with the policy on and every guard pinned to nights {RosterNights(gs)} are on nights");

                // The public path: SetPin, then the next real balance (the hourly pass, which SetPin asks for). Six guards with two automatic night guards; pinning one of the day guards to nights puts a third
                // on nights unless the others are balanced again, which only the balance does: two on nights, the pinned one of them.
                foreach (Pawn p in gs) (p.GetComp<CompStaff>().shiftPin, p.GetComp<CompStaff>().nightShift) = (ShiftPin.Auto, false);
                g.guardNightShift = true;
                ShiftSystem.Rebalance(gs, true);
                ShiftSystem.Balance(map, g); // (today's balance, if it is due: the guards are balanced already, and it is spent after it)
                if (g.lastShiftBalanceDay == -1 || RosterNights(gs).Count(c => c == 'N') != 2) RosterFail(check, $"before the pin the guards are {RosterNights(gs)} and the balance day {g.lastShiftBalanceDay} (want two on nights, today's balance spent)");
                int pinned0 = Roster.Split(map).pinned;
                int dayGuard = Enumerable.Range(0, 6).First(i => !Sc(i).nightShift), otherDay = Enumerable.Range(0, 6).Last(i => !Sc(i).nightShift);
                Roster.SetPin(Sc(dayGuard), ShiftPin.Nights);
                Roster.SetPin(Sc(otherDay), ShiftPin.Days);
                if (g.lastShiftBalanceDay != -1) RosterFail(check, "pinning a guard did not ask for a balance at the next hour");
                int version = Roster.Version;
                ShiftSystem.Balance(map, g);
                if (RosterNights(gs).Count(c => c == 'N') != 2 || !Sc(dayGuard).nightShift || Sc(otherDay).nightShift)
                    RosterFail(check, $"after a day guard was pinned to nights and another to days, the next balance has the guards on {RosterNights(gs)} (want two on nights in all, the pinned one {dayGuard} of them; the other pinned to days)");
                if (Roster.Version == version) RosterFail(check, "the hourly write of the timetables did not tell the roster (a timetable edited by hand is seen at the next hourly pass)");
                (int nights, int days, int pinned) = Roster.Split(map);
                if (pinned != pinned0 + 2) RosterFail(check, $"two guards were pinned, the pinned count went from {pinned0} to {pinned} (want {pinned0 + 2}); {nights} on nights, {days} on days");
                Roster.SetPin(Sc(dayGuard), ShiftPin.Auto);
                Roster.SetPin(Sc(otherDay), ShiftPin.Auto);
                if (Roster.Split(map).pinned != pinned0) RosterFail(check, $"both pins taken off, the pinned count is {Roster.Split(map).pinned} (want {pinned0})");

                // Between balances (the hourly pass, not due): a pin set now is where the guard is, in the timetable too, and a guard away keeps it.
                foreach (Pawn p in gs) (p.GetComp<CompStaff>().shiftPin, p.GetComp<CompStaff>().nightShift) = (ShiftPin.Auto, false);
                if (g.lastShiftBalanceDay == -1) ShiftSystem.Balance(map, g); // (spend the balance the unpinning asked for)
                Sc(0).shiftPin = ShiftPin.Nights; // (the field, not SetPin: SetPin asks for a balance, and this is the pass without one)
                CompStaff away = Sc(1);
                away.shiftPin = ShiftPin.Nights;
                (away.leaveUntilTick, away.restPending) = (Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay, true);
                ShiftSystem.Balance(map, g);
                if (!Sc(0).nightShift || gs[0].timetable.GetAssignment(12) != TimeAssignmentDefOf.Sleep || gs[0].timetable.GetAssignment(3) == TimeAssignmentDefOf.Sleep)
                    RosterFail(check, $"a guard pinned to nights between balances is on nights {Sc(0).nightShift}, asleep at noon {gs[0].timetable.GetAssignment(12)?.defName}, at 03:00 {gs[0].timetable.GetAssignment(3)?.defName}");
                if (!away.nightShift) RosterFail(check, "a guard on leave, pinned to nights, was not on nights (they keep their shift for when they are back)");
                if (g.lastShiftBalanceDay == -1) RosterFail(check, "the balance between balances asked for a balance");

                // A pin on a guard who is not a guard (a doctor, a cook) is nothing: ShiftSystem.PinOf.
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                cook.GetComp<CompStaff>().shiftPin = ShiftPin.Nights;
                if (ShiftSystem.PinOf(cook.GetComp<CompStaff>()) != ShiftPin.Auto) RosterFail(check, "a cook's pin counted");
                RPLog.Message("[RimPrison:test] roster pins: the third is held with and without pins (through SetPin and the next balance), an all-days roster has no night, the policy off beats a pin, a pin between balances is kept in the timetable, the pinned count is exact");
            });
        }

        // ---- the handover ----------------------------------------------------------------------------------------------------

        private static void CheckRosterHandover(Map map)
        {
            Guarded("roster: the handover keeps the day shift on to the hour set", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                const string check = "handover";
                int hours0 = g.handoverHours;
                r.Add(() => g.handoverHours = hours0);
                Pawn day = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), night = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                CompStaff dsc = day.GetComp<CompStaff>(), nsc = night.GetComp<CompStaff>(), csc = cook.GetComp<CompStaff>();
                (dsc.nightShift, nsc.nightShift) = (false, true);
                TimeAssignmentDef Slot(Pawn p, int h) => p.timetable.GetAssignment(h);

                for (int hours = 0; hours <= Roster.MaxHandover; hours++)
                {
                    g.handoverHours = hours;
                    ShiftSystem.Apply(day, false);
                    ShiftSystem.Apply(night, true);
                    ShiftSystem.Apply(cook, false);
                    for (int hour = 0; hour < 24; hour++)
                    {
                        // The day shift works 05:00 (the commute in) to 22:00, and stays for the handover's hours after it.
                        bool onDuty = hour is >= 5 and < 22 || ((hour - 22) % 24 + 24) % 24 < hours;
                        bool home = StaffCommute.ShouldBeHome(day, dsc, hour);
                        if (home == onDuty) RosterFail(check, $"with the handover at {hours} the day guard {(home ? "goes" : "stays")} at {hour:00}:00 (want {(onDuty ? "on" : "home")}); timetable {Slot(day, hour)?.defName}");
                        if ((Slot(day, hour) != TimeAssignmentDefOf.Sleep) != onDuty) RosterFail(check, $"with the handover at {hours} the day guard's timetable at {hour:00}:00 says {Slot(day, hour)?.defName} (on duty {onDuty})");
                        // The cook (not a guard) is never kept: the cook's day ends at 22:00 whatever the handover says. Nor is the night guard in the handover.
                        if (StaffCommute.ShouldBeHome(cook, csc, hour) != !(hour is >= 5 and < 22)) RosterFail(check, $"with the handover at {hours} the cook's shift is {(StaffCommute.ShouldBeHome(cook, csc, hour) ? "over" : "on")} at {hour:00}:00 (the handover is for the guards)");
                        if (ShiftSystem.InHandover(nsc, hour)) RosterFail(check, "a night guard is in the handover");
                    }
                }

                // A day guard who is suspended is home whatever the hour; the warden never goes (ShouldBeHome, as before).
                g.handoverHours = 2;
                (nsc.nightShift, dsc.nightShift) = (false, false);
                CorruptionSystem.Suspend(day, dsc);
                if (!StaffCommute.ShouldBeHome(day, dsc, 22)) RosterFail(check, "a suspended guard was kept on for the handover");
                RPLog.Message("[RimPrison:test] roster handover: 0 to 3 hours past 22:00 for the day guard (timetable and ShouldBeHome), none for a night guard, a cook or a suspended guard");
            });
        }

        // ---- the overtime ----------------------------------------------------------------------------------------------------

        private static void CheckRosterOvertimePay(Map map)
        {
            Guarded("roster: the handover's overtime is paid for the hours worked", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                const string check = "overtime";
                (int hours0, int owed0, int owedHours0) = (g.handoverHours, g.overtimeOwed, g.overtimeHours);
                r.Add(() => (g.handoverHours, g.overtimeOwed, g.overtimeHours) = (hours0, owed0, owedHours0));
                // (The prison's own staff are on leave: the hours accrued below are the check's guards' alone.)
                RosterRealStaffAway(map, r);
                Pawn working = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), sacked = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), resting = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn night = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), gone = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                CompStaff wsc = working.GetComp<CompStaff>(), ssc = sacked.GetComp<CompStaff>();
                foreach (Pawn p in new[] { working, sacked, resting, gone, cook }) p.GetComp<CompStaff>().nightShift = false;
                night.GetComp<CompStaff>().nightShift = true;
                (resting.GetComp<CompStaff>().leaveUntilTick, resting.GetComp<CompStaff>().restPending) = (Find.TickManager.TicksGame + GenDate.TicksPerDay, true);
                g.handoverHours = 2;
                foreach (Pawn p in new[] { working, sacked, resting, gone, cook, night }) ShiftSystem.Apply(p, p.GetComp<CompStaff>().nightShift);
                var three = new List<Pawn> { working, night, cook };

                // The forecast (the tabs and the alert): two hours for the day guard, none for the night guard or the cook.
                g.handoverHours = 0;
                int base0 = StaffCareers.Payroll(three);
                if (StaffCareers.OvertimePay(three) != 0 || base0 != StaffCareers.BasePay(three)) RosterFail(check, "forecast overtime with no handover");
                g.handoverHours = 2;
                int wage = StaffCareers.Wage(wsc);
                int hourly = (int)Math.Ceiling(wage * 1.5 / 16.0);
                int forecast = StaffCareers.Payroll(three) - base0;
                if (forecast != 2 * hourly || hourly <= 0) RosterFail(check, $"two hours of handover for a wage of {wage} are forecast at {forecast} (want {2 * hourly}: 1.5x a sixteenth of the day's wage an hour, only for the day guard)");
                if (StaffCareers.Overtime(night.GetComp<CompStaff>()) != 0 || StaffCareers.Overtime(cook.GetComp<CompStaff>()) != 0) RosterFail(check, "a night guard or a cook was forecast overtime");

                // Worked as it happens: nothing before the handover's hours, the guards working an hour of it earn it (not the cook, the night guard, the one on leave or the one at home), a guard suspended
                // after the first hour earns the first only, and the hour past the handover's two nothing.
                (g.overtimeOwed, g.overtimeHours) = (0, 0);
                ShiftSystem.AccrueOvertime(map, g, 21);
                if (g.overtimeOwed != 0) RosterFail(check, $"overtime {g.overtimeOwed} accrued at 21:00, before the handover");
                ShiftSystem.AccrueOvertime(map, g, 22);
                int firstHour = StaffCareers.OvertimeHour(wsc) + StaffCareers.OvertimeHour(ssc);
                if (g.overtimeOwed != firstHour || g.overtimeHours != 2) RosterFail(check, $"the first handover hour accrued {g.overtimeOwed} for {g.overtimeHours} guard-hours (want {firstHour}, two guards: not the cook, the night guard, the guard on leave or the guard at home)");
                CorruptionSystem.Suspend(sacked, ssc);
                ShiftSystem.AccrueOvertime(map, g, 23);
                ShiftSystem.AccrueOvertime(map, g, 0);
                int worked = firstHour + StaffCareers.OvertimeHour(wsc);
                if (g.overtimeOwed != worked || g.overtimeHours != 3) RosterFail(check, $"after a suspension in the second hour and a third hour past a two-hour handover the overtime owed is {g.overtimeOwed} for {g.overtimeHours} guard-hours (want {worked}, 3)");

                // The handover is turned off at 23:59: the settlement still pays what was worked, as a line of its own on the wages account, and the funds go down by the wages and it.
                g.handoverHours = 0;
                List<Pawn> everyone = StaffCommute.AllStaff(map);
                int wages = StaffCareers.BasePay(everyone), funds = g.ledger.funds;
                List<LedgerEntry> Lines() => r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_Wages).ToList();
                int paidOut = DailySettlement.PayWages(g, map);
                List<LedgerEntry> lines = Lines();
                if (paidOut != wages + worked || funds - g.ledger.funds != wages + worked) RosterFail(check, $"with the handover turned off before midnight the settlement paid {paidOut} and the funds went down by {funds - g.ledger.funds} (want {wages} in wages and {worked} worked)");
                if (lines.Count != 2 || lines.Sum(e => e.amount) != -(wages + worked) || lines.All(e => e.amount != -worked))
                    RosterFail(check, $"the wages account has {lines.Count} line(s) of {string.Join(", ", lines.Select(e => e.amount))} (want two, the overtime worked {worked} one of them, {-(wages + worked)} in all)");
                if (g.overtimeOwed != 0 || g.overtimeHours != 0) RosterFail(check, $"the overtime was paid and {g.overtimeOwed} for {g.overtimeHours} guard-hours is still owed");
                // Paid once.
                int again = DailySettlement.PayWages(g, map);
                if (again != wages || Lines().Count != 3) RosterFail(check, $"the next settlement paid {again} on {Lines().Count - 2} line(s) (want the wages {wages} alone, on one line)");

                // The handover is turned on at 23:59, after the day guards have gone home: nothing is owed for hours not worked.
                (g.handoverHours, g.overtimeOwed, g.overtimeHours) = (0, 0, 0);
                ShiftSystem.AccrueOvertime(map, g, 22);
                ShiftSystem.AccrueOvertime(map, g, 23);
                ShiftSystem.AccrueOvertime(map, g, 0);
                g.handoverHours = 3;
                int late = DailySettlement.PayWages(g, map);
                if (late != wages || g.overtimeOwed != 0 || Lines().Count != 4) RosterFail(check, $"a handover turned on at 23:59 was charged {late - wages} (want nothing until worked)");
                RPLog.Message($"[RimPrison:test] roster overtime: {worked} paid for {3} guard-hours worked (handover turned off before midnight), nothing for a handover turned on at 23:59, paid once, on a line of its own");
            });
        }

        // ---- the strip -------------------------------------------------------------------------------------------------------

        /// <summary>Guards on duty in an hour, counted by hand from the staff and the commute's own test.</summary>
        private static int RosterManualCount(Map map, int hour) =>
            StaffCommute.AllStaff(map).Count(p => p.GetComp<CompStaff>() is { } sc && sc.role is { guardDuties: true, warden: false } && sc.leaveAtTick < 0 && !StaffCommute.ShouldBeHome(p, sc, hour));

        /// <summary>
        /// The handover in the strip, with a handover of <paramref name="pre"/> hours already set and the real guards' timetables written for it (as in a game that has had one): both are put back to none before the
        /// baseline is taken, and put back as they were after.
        /// </summary>
        private static void CheckRosterStrip(Map map, int pre)
        {
            Guarded($"roster: the strip counts the guards the commute has on site, with a handover of {pre} hour(s) already set", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                string check = $"strip, handover {pre}";
                RosterKeepTimetables(map, r);
                g.handoverHours = pre;
                RosterRewriteTimetables(map);
                g.handoverHours = 0;
                RosterRewriteTimetables(map);
                int[] before = (int[])Roster.Strip(map).Clone();
                int passes0 = Roster.Computations;

                Pawn day = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), night = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                (day.GetComp<CompStaff>().nightShift, night.GetComp<CompStaff>().nightShift) = (false, true);
                ShiftSystem.Apply(day, false);
                ShiftSystem.Apply(night, true);
                int[] with = (int[])Roster.Strip(map).Clone();
                if (Roster.Computations == passes0) RosterFail(check, "two new guards did not make the strip be worked out again");
                // The night guard is in at 03:00 and 23:00, the day guard at noon and not at 23:00 or 03:00.
                if (with[3] != before[3] + 1 || with[12] != before[12] + 1 || with[23] != before[23] + 1 || with[22] != before[22] + 1)
                    RosterFail(check, $"a day guard and a night guard changed the strip at 03:00 {before[3]}->{with[3]}, 12:00 {before[12]}->{with[12]}, 22:00 {before[22]}->{with[22]}, 23:00 {before[23]}->{with[23]} (+1 each: nights at 03, 22 and 23, days at noon)");
                for (int h = 0; h < 24; h++)
                    if (with[h] != RosterManualCount(map, h)) RosterFail(check, $"the strip says {with[h]} on duty at {h:00}:00 and counting the staff by the commute's test gives {RosterManualCount(map, h)}");

                // The handover is in the strip: every day guard (the live prison's and the check's, all timetabled for none) stays 22, 23 and 00 with three hours, and not 01: the same rise in each of the three hours.
                g.handoverHours = 3;
                int[] late = (int[])Roster.Strip(map).Clone();
                int rise = late[22] - with[22];
                if (rise < 1 || late[23] - with[23] != rise || late[0] - with[0] != rise || late[1] != with[1] || late[21] != with[21])
                    RosterFail(check, $"a handover of three hours changed the strip at 22:00 {with[22]}->{late[22]}, 23:00 {with[23]}->{late[23]}, 00:00 {with[0]}->{late[0]}, 01:00 {with[1]}->{late[1]} (the same rise of at least one at 22, 23 and 00, none at 01)");
                for (int h = 0; h < 24; h++)
                    if (late[h] != RosterManualCount(map, h)) RosterFail(check, $"with a handover of three hours the strip says {late[h]} on duty at {h:00}:00 and counting the staff by the commute's test gives {RosterManualCount(map, h)}");
                // And the posts' read of it: the fewest on duty in a post's hours.
                if (Roster.Fewest(late, 22, 2) != Math.Min(Math.Min(late[22], late[23]), Math.Min(late[0], late[1])) || Roster.Fewest(late, 5, 5) != late.Min())
                    RosterFail(check, "the fewest on duty in a post's hours (across midnight, and all day) is not the minimum of the strip over them");
                var (nights, days, pinned) = Roster.Split(map);
                if (nights < 1 || days < 1) RosterFail(check, $"the split of the roster is {nights} on nights, {days} on days, {pinned} pinned (the check's two guards are one of each)");
                RPLog.Message($"[RimPrison:test] roster strip (handover of {pre} already set): 03:00 {with[3]} on duty, noon {with[12]}, midnight {with[0]} ({late[0]} with a 3 hour handover); {nights} on nights, {days} on days");
            });
        }

        /// <summary>The strip is worked out once, again on every change its answer depends on, and what it draws is kept (not made per frame).</summary>
        private static void CheckRosterStripCache(Map map)
        {
            Guarded("roster: the strip is worked out once, and again on every change it depends on", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                const string check = "strip cache";
                // (The clock is moved a few hundred ticks below: not across the hour's change, whose pass would write the timetables and be what tells the strip.)
                ClearOfTheHourChange(map, 600);
                RosterKeepTimetables(map, r);
                g.handoverHours = 0;
                RosterRewriteTimetables(map);
                // (A pin asks for a balance at the next hour: not the real one's to have early.)
                int balanceDay0 = g.lastShiftBalanceDay;
                r.Add(() => g.lastShiftBalanceDay = balanceDay0);
                // The funds, for the course below.
                g.ledger.funds = Math.Max(g.ledger.funds, 5 * StaffTraining.Cost);

                // Worked out once: the same hour and the same staff give the same array without another pass.
                int[] strip = Roster.Strip(map);
                int passes0 = Roster.Computations;
                for (int i = 0; i < 5; i++) Roster.Strip(map);
                if (Roster.Computations != passes0) RosterFail(check, $"the strip was worked out {Roster.Computations - passes0} more time(s) in five reads of the same hour (once an hour, or on a change)");

                Pawn day = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff dsc = day.GetComp<CompStaff>();
                (dsc.nightShift, dsc.shiftPin, dsc.nightsRun) = (false, ShiftPin.Auto, 0);
                ShiftSystem.Apply(day, false);
                int[] Read() => (int[])Roster.Strip(map).Clone();
                // (Noon: the day guard is on duty; 03:00: asleep, unless a hostage, who is on site whatever the timetable says; 22:00: on duty under a handover.)
                int[] without = (int[])strip.Clone();
                int[] with = Read();
                if (with[12] != without[12] + 1 || with[3] != without[3]) RosterFail(check, $"a hire changed noon {without[12]}->{with[12]} and 03:00 {without[3]}->{with[3]} (+1 at noon, none at 03:00)");

                // Suspended, stood down or disciplined within the hour: in no hour, the strip told at once (CorruptionSystem.SendHome).
                CorruptionSystem.Suspend(day, dsc);
                if (Read()[12] != without[12]) RosterFail(check, $"a guard suspended in the middle of the hour is still in the strip at noon ({Read()[12]}, want {without[12]})");
                dsc.suspendedUntilTick = -1;
                Roster.Invalidate();
                if (Read()[12] != with[12]) RosterFail(check, "the strip did not have the guard back once the suspension was lifted");
                CorruptionSystem.StandDown(day, dsc, Find.TickManager.TicksGame + 1000);
                if (Read()[12] != without[12]) RosterFail(check, "a guard stood down in the middle of the hour is still in the strip at noon");
                dsc.standDownUntilTick = Find.TickManager.TicksGame - 1;
                CorruptionSystem.TickSuspensions(day, dsc);
                if (Read()[12] != with[12]) RosterFail(check, "a stand-down that ran out did not put the guard back in the strip");

                // A leave started and ended (a course here, ended in-house: StaffTraining.Send and CheckInHouse).
                SetMelee(day, 3);
                bool sent = StaffTraining.Send(day, dsc);
                if (!sent || Read()[12] != without[12]) RosterFail(check, $"a guard sent on a course (sent {sent}, {StaffTraining.CannotGo(day, dsc)}) is still in the strip at noon ({Read()[12]}, want {without[12]})");
                dsc.leaveUntilTick = Find.TickManager.TicksGame - 1;
                StaffTraining.CheckInHouse(day, dsc);
                if (Read()[12] != with[12]) RosterFail(check, "a leave that ended did not put the guard back in the strip");

                // A timetable edited by hand in the vanilla Schedule tab (SetAssignment, which the column calls and nothing tells the roster of but the patch on it): the strip follows at once. Noon goes to Sleep and back.
                TimeAssignmentDef atNoon = day.timetable.GetAssignment(12);
                day.timetable.SetAssignment(12, TimeAssignmentDefOf.Sleep);
                if (Read()[12] != without[12]) RosterFail(check, $"a day guard's noon put to Sleep by hand in the Schedule tab is still in the strip at noon ({Read()[12]}, want {without[12]}): nothing told the strip");
                day.timetable.SetAssignment(12, atNoon);
                if (Read()[12] != with[12]) RosterFail(check, "a day guard's noon put back by hand is not in the strip at noon");
                // The prison's own write (ShiftSystem.Apply) tells it once for the pass, not once for each hour slot it wrote, and not at all when it wrote nothing.
                foreach (int h in new[] { 2, 3, 4 }) day.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                int version = Roster.Version;
                ShiftSystem.Apply(day, false);
                int pass = Roster.Version - version;
                ShiftSystem.Apply(day, false);
                if (pass != 1 || Roster.Version - version != 1) RosterFail(check, $"a write of three changed hours bumped the roster's version {pass} time(s), and one that changed nothing {Roster.Version - version - pass} (want once, then none)");

                // A suspension, a stand-down, a discipline and a leave end at a tick, with no hook to say so (a guard at home runs no tick of the corruption pass, and a leave is cleared when they arrive at the next hour):
                // the clock goes past the deadline - it is not the check that ends it, no TickSuspensions - and the strip has them back. (Clear of the hour, so that a change of hour is not what tells it.)
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                foreach ((string what, Action<int> set, Action clear) in new (string, Action<int>, Action)[]
                {
                    ("suspension", t => dsc.suspendedUntilTick = t, () => dsc.suspendedUntilTick = -1),
                    ("stand-down", t => dsc.standDownUntilTick = t, () => dsc.standDownUntilTick = -1),
                    ("discipline", t => dsc.disciplineUntilTick = t, () => dsc.disciplineUntilTick = -1),
                    ("leave", t => dsc.leaveUntilTick = t, () => dsc.leaveUntilTick = -1),
                })
                {
                    Find.TickManager.DebugSetTicksGame(clock0);
                    set(clock0 + 300);
                    Roster.Invalidate(); // (the start's hook)
                    if (Read()[12] != without[12]) RosterFail(check, $"a guard with a {what} pending is still in the strip at noon ({Read()[12]}, want {without[12]})");
                    Find.TickManager.DebugSetTicksGame(clock0 + 150);
                    if (Read()[12] != without[12]) RosterFail(check, $"a guard with a {what} still running 150 ticks on is in the strip at noon ({Read()[12]}, want {without[12]})");
                    Find.TickManager.DebugSetTicksGame(clock0 + 301);
                    if (Read()[12] != with[12]) RosterFail(check, $"a {what} that ran out with the clock (no hook, no change of hour: {GenLocalDate.HourOfDay(map):00}:00 throughout) did not put the guard back in the strip at noon ({Read()[12]}, want {with[12]})");
                    clear();
                    Find.TickManager.DebugSetTicksGame(clock0);
                    Roster.Invalidate();
                }

                // A hostage taken and freed: held on site whatever the timetable says (in the strip at 03:00) and out of it again when freed (HostageCrisis.Release).
                day.health.AddHediff(RPHostageDefOf.RP_Hostage);
                Roster.Invalidate(); // (the taking is the crisis's: the check does not stage one)
                if (Read()[3] != without[3] + 1) RosterFail(check, $"a hostage is not on duty in the strip at 03:00 ({Read()[3]}, want {without[3] + 1})");
                HostageCrisis.Release(day);
                if (Read()[3] != without[3]) RosterFail(check, "a hostage let go is still in the strip at 03:00");

                // The handover is in the key; so is the headcount (a firing is a hire the other way round).
                g.handoverHours = 2;
                if (Read()[22] < with[22] + 1) RosterFail(check, "the handover being set did not change the strip at 22:00");
                g.handoverHours = 0;

                // The pin: the count of pinned guards is exact and follows the pin at once.
                int pinned0 = Roster.Split(map).pinned;
                Roster.SetPin(day.GetComp<CompStaff>(), ShiftPin.Nights);
                if (Roster.Split(map).pinned != pinned0 + 1) RosterFail(check, $"a guard pinned to nights did not make the pinned count {pinned0 + 1} ({Roster.Split(map).pinned})");
                dsc.shiftPin = ShiftPin.Auto;
                Roster.Invalidate();

                // What it draws is kept with the counts: the same text, the same string, until the counts or the language change.
                int at12 = Roster.Strip(map)[12];
                string heading = Roster.Heading(map), tip12 = Roster.BarTip(12), stripTip = Roster.StripTip(map);
                if (!ReferenceEquals(heading, Roster.Heading(map)) || !ReferenceEquals(tip12, Roster.BarTip(12)) || !ReferenceEquals(Roster.StripTip(map), stripTip))
                    RosterFail(check, "the heading, a bar's tooltip or the strip's tooltip was made again without a change (made per frame)");
                if (!tip12.Contains(at12.ToString())) RosterFail(check, $"a bar's tooltip \"{tip12}\" does not say the {at12} guards scheduled at noon");
                Pawn second = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                ShiftSystem.Apply(second, false);
                int at12After = Roster.Strip(map)[12];
                string tipAfter = Roster.BarTip(12);
                if (tipAfter == tip12 || !tipAfter.Contains(at12After.ToString())) RosterFail(check, $"a bar's tooltip \"{tipAfter}\" did not follow a new guard's {at12After} at noon");
                RPLog.Message("[RimPrison:test] roster strip cache: once for five reads, again for a hire, a suspension, a stand-down, a leave, a hostage, the handover and a pin; the heading and the tooltips kept");
            });
        }

        private static void SetMelee(Pawn p, int level) => p.skills.GetSkill(SkillDefOf.Melee).Level = level;

        /// <summary>
        /// The strip is the schedule, and the commute's retained staff are told beside it. A controlled scenario with the prison's own staff out of it (set aside, not sent home: <see cref="RosterRealStaffAside"/>): four
        /// healthy throwaway guards of the day shift, each given a job the commute may cut short, have this hour made off shift. The commute's pass must send all but the one it keeps (the last in its own order,
        /// <see cref="StaffCommute.SendHomeOrder"/>): each of the others must have the walk home the moment the pass is done (the kept one none), and, put a few cells short of the cell they were sent to (the walk is at the
        /// walking pace, at least 50 ticks a cell, so a real edge is hours away), must be off the map and held at home by the deadline, none still walking, the kept one on site and the only one the roster says is kept, and
        /// the strip (the schedule) at none. Then the same hour is made an on-duty one for them: the very pawns who went must be on the map again, the kept one where they were, and the strip at four, none kept.
        /// A commute that sent nobody (<c>TrySendHome</c> false for everyone) fails the first part (the pass gave them no walk), one that left them walking fails it too (unfinished departures),
        /// and one that did not bring them in fails the second.
        /// </summary>
        private static void CheckRosterPresence(Map map)
        {
            Guarded("roster: the strip is the schedule; the staff the commute keeps on site are told beside it", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                const string check = "presence";
                // (The prison's hour pass would rewrite the timetables mid-check, and the last steps of the walks take up to the bound below: all of it within one hour.)
                const int bound = 600;
                ClearOfTheHourChange(map, bound + 300);
                int now = GenLocalDate.HourOfDay(map);
                RosterKeepTimetables(map, r);
                g.handoverHours = 0;
                // (The prison's own staff are set aside, not sent home: nobody of theirs leaves the map or comes back to it, so the next check finds them where and as they were.)
                RosterRealStaffAside(map, r);

                bool Walking(Pawn p) => p.Spawned && p.CurJobDef == RPCommuteDefOf.RP_GoHome;
                List<Pawn> Present() => StaffCommute.AllStaff(map).Where(p => p.Spawned && p.GetComp<CompStaff>() is { role.warden: false, leaveAtTick: < 0 } && !Walking(p) && !HostageCrisis.IsHeld(p)).ToList();
                var crew = new List<Pawn>();
                for (int i = 0; i < 4; i++) crew.Add(ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard));
                foreach (Pawn p in crew)
                {
                    CompStaff sc = p.GetComp<CompStaff>();
                    (sc.nightShift, sc.shiftPin) = (false, ShiftPin.Auto);
                    ShiftSystem.Apply(p, false);
                    // Healthy, awake, free, with a way out, and at a job the commute may cut short (a wait).
                    p.jobs.StopAll();
                    Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                    wait.expiryInterval = 3000;
                    p.jobs.StartJob(wait, JobCondition.InterruptForced);
                    if (p.Downed || !p.Awake() || p.InMentalState || p.Drafted || p.IsBurning() || p.CurJobDef != JobDefOf.Wait || StaffCommute.GoHomeJob(p) == null)
                        RosterFail(check, $"the fixture guard {p.LabelShort} is not a healthy one with a way out and a wait (downed {p.Downed}, awake {p.Awake()}, mental state {p.InMentalState}, job {p.CurJobDef?.defName}, exit {StaffCommute.GoHomeJob(p) != null})");
                }
                if (Present().Count != crew.Count) RosterFail(check, $"{Present().Count} members of staff are on site with the prison's own set aside, want the {crew.Count} fixture guards");

                // Everyone off shift this hour. The commute keeps the last of its own order.
                foreach (Pawn p in crew) p.timetable.SetAssignment(now, TimeAssignmentDefOf.Sleep);
                Roster.Invalidate();
                Pawn kept = StaffCommute.SendHomeOrder(map.mapPawns.FreeColonistsSpawned.Where(crew.Contains).ToList()).Last();
                List<Pawn> goers = crew.Where(p => p != kept).ToList();
                StaffCommute.Hourly(map, g, now);
                // The pass sends them: each of the others is given the walk home at once (TrySendHome starts the job there and then), the kept one none. That is what a commute that sent nobody fails.
                int sent = goers.Count(Walking);
                foreach (Pawn p in goers)
                    if (!Walking(p)) RosterFail(check, $"{p.LabelShort} was to be sent home with everyone off shift at {now:00}:00 and was not: the pass gave them no walk (job {p.CurJobDef?.defName ?? "none"}; it sent {sent} of {goers.Count})");
                if (Walking(kept)) RosterFail(check, $"{kept.LabelShort}, the one the commute keeps, was sent home as well");
                // (The walk is at the walking pace, at least 50 ticks a cell, to a random cell of the map's edge: a hundred cells is over an hour. What is judged is the leaving, not the length of the road, so each goer is put a few
                // cells short of the cell they were sent to, and its walk, the arrival at it and the going home are the game's own, on real ticks.)
                foreach (Pawn p in goers.Where(Walking))
                {
                    IntVec3 exit = p.CurJob.targetA.Cell;
                    IntVec3? near = GenRadial.RadialCellsAround(exit, 3f, false).Cast<IntVec3?>().FirstOrDefault(c => c.Value.InBounds(map) && c.Value.Standable(map) && c.Value.GetFirstPawn(map) == null && map.reachability.CanReach(c.Value, exit, PathEndMode.OnCell, TraverseParms.For(p, Danger.Deadly)));
                    if (near == null)
                    {
                        RosterFail(check, $"{p.LabelShort}'s way out at {exit} has no free cell within three of it to walk the last of it from");
                        continue;
                    }
                    p.pather.StopDead();
                    p.Position = near.Value;
                    p.Notify_Teleported(endCurrentJob: false);
                    p.pather.StartPath(exit, PathEndMode.OnCell);
                }
                int ticked = 0;
                while (ticked < bound && crew.Any(Walking))
                {
                    Find.TickManager.DoSingleTick();
                    ticked++;
                }
                Roster.Invalidate();
                foreach (Pawn p in goers)
                    if (p.Spawned || !StaffCommute.AtHome(p)) RosterFail(check, $"{p.LabelShort} was to go home with everyone off shift at {now:00}:00 and {ticked} ticks after the last cells of the walk is {(p.Spawned ? $"on the map ({(Walking(p) ? "still walking" : p.CurJobDef?.defName ?? "no job")})" : "off the map but not held at home")} (the pass sent {sent} of {goers.Count}, {goers.Count(x => !x.Spawned)} are off the map)");
                if (crew.Count(Walking) > 0) RosterFail(check, $"{crew.Count(Walking)} departure(s) still unfinished {bound} ticks after the last cells of the walk");
                if (!kept.Spawned || Walking(kept) || StaffCommute.AtHome(kept)) RosterFail(check, $"{kept.LabelShort}, the one the commute keeps, is not on site (spawned {kept.Spawned}, walking {Walking(kept)}, at home {StaffCommute.AtHome(kept)})");
                else if (!StaffCommute.OffShiftOnSite(kept, kept.GetComp<CompStaff>(), now)) RosterFail(check, $"{kept.LabelShort} is on site but not counted off shift and on site");
                if (Present().Count != 1) RosterFail(check, $"{Present().Count} members of staff are on site with everyone off shift, the commute keeps exactly one");
                int scheduled = Roster.Strip(map)[now];
                if (scheduled != 0 || RosterManualCount(map, now) != 0) RosterFail(check, $"with everyone off shift the strip has {scheduled} scheduled at {now:00}:00, by hand {RosterManualCount(map, now)} (the schedule: none, whoever is kept)");
                if (Roster.KeptOnSite(map) != 1) RosterFail(check, $"the roster says {Roster.KeptOnSite(map)} off-shift staff are kept on site (want exactly the one)");
                if (Roster.Heading(map) != "RP_RosterHeadingKept".Translate(1).Resolve()) RosterFail(check, $"the heading beside the strip is \"{Roster.Heading(map)}\", it does not say one off-shift member of staff is on site");

                // The shift begins: the same hour on duty (a worked hour in the timetable), the commute's pass at it. Those who went are back through the gate, the kept one where they were.
                foreach (Pawn p in crew) p.timetable.SetAssignment(now, TimeAssignmentDefOf.Work);
                Roster.Invalidate();
                StaffCommute.Hourly(map, g, now);
                Roster.Invalidate();
                foreach (Pawn p in goers)
                    if (!p.Spawned || p.Map != map || StaffCommute.AtHome(p)) RosterFail(check, $"{p.LabelShort}, who went home, is not back on the map with their shift on at {now:00}:00 (spawned {p.Spawned}, at home {StaffCommute.AtHome(p)})");
                if (!kept.Spawned || StaffCommute.AtHome(kept)) RosterFail(check, $"{kept.LabelShort}, who was kept, is not on site after the shift began");
                if (Present().Count != crew.Count) RosterFail(check, $"{Present().Count} on site with the shift on at {now:00}:00, want {crew.Count}");
                int back = Roster.Strip(map)[now];
                if (back != crew.Count || RosterManualCount(map, now) != crew.Count) RosterFail(check, $"with the shift on the strip has {back} scheduled at {now:00}:00, by hand {RosterManualCount(map, now)} (want {crew.Count})");
                if (Roster.KeptOnSite(map) != 0) RosterFail(check, $"with the shift on the roster says {Roster.KeptOnSite(map)} off-shift staff are kept on site (want none)");
                RPLog.Message($"[RimPrison:test] roster presence at {now:00}:00: all {crew.Count} off shift - {sent} of {goers.Count} sent, {goers.Count(x => !x.Spawned && StaffCommute.AtHome(x))} home {ticked} ticks after the last cells of the walk, {kept.LabelShort} kept; on duty again - {Present().Count} on site, the strip {back}");
            });
        }

        // ---- the thoughts ----------------------------------------------------------------------------------------------------

        private static bool HasMemory(Pawn p, ThoughtDef def) => p.needs?.mood?.thoughts.memories.Memories.Any(m => m.def == def) == true;

        private static void CheckRosterThoughts(Map map)
        {
            Guarded("roster: seven nights in a row is resented, a fair rota is not, and a night missed breaks the run", map, r =>
            {
                const string check = "thoughts";
                int today = Ledger.CurrentDay;
                // (The prison's own staff are on leave for the check, so that the daily pass below tallies the check's guards alone and no real guard is given a thought.)
                RosterRealStaffAway(map, r);
                Pawn six = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), seven = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), dayGuard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff ssc = six.GetComp<CompStaff>(), vsc = seven.GetComp<CompStaff>(), dsc = dayGuard.GetComp<CompStaff>();
                (ssc.nightShift, vsc.nightShift, dsc.nightShift) = (true, true, false);
                (ssc.nightsRun, vsc.nightsRun, dsc.nightsRun) = (5, Roster.NightsAgainAfter - 1, 4);
                (ssc.lastNightDay, vsc.lastNightDay) = (today - 1, today - 1);
                ShiftSystem.Daily(map, today);
                if (ssc.nightsRun != 6 || HasMemory(six, RPRosterDefOf.RP_NightsAgain)) RosterFail(check, $"a guard on nights for six days is on run {ssc.nightsRun} with the thought {HasMemory(six, RPRosterDefOf.RP_NightsAgain)} (want 6, none)");
                if (vsc.nightsRun != Roster.NightsAgainAfter || !HasMemory(seven, RPRosterDefOf.RP_NightsAgain)) RosterFail(check, $"a guard on nights for seven days is on run {vsc.nightsRun} with the thought {HasMemory(seven, RPRosterDefOf.RP_NightsAgain)} (want {Roster.NightsAgainAfter}, the thought)");
                if (dsc.nightsRun != 0 || HasMemory(dayGuard, RPRosterDefOf.RP_NightsAgain)) RosterFail(check, "a day guard has a run of nights or the thought");

                // Taken off nights after a run of them: a fair rota for a run that was current (and the run begins again), none for one taken off early or for a stale one - an absence broke it.
                Pawn stale = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff tsc = stale.GetComp<CompStaff>();
                (tsc.nightShift, tsc.nightsRun, tsc.lastNightDay) = (true, Roster.NightsAgainAfter + 2, today - 5);
                if (ShiftSystem.NightRun(tsc, today) != 0 || ShiftSystem.NightRun(vsc, today) != Roster.NightsAgainAfter) RosterFail(check, $"the run as it stands: stale {ShiftSystem.NightRun(tsc, today)} (want 0), current {ShiftSystem.NightRun(vsc, today)} (want {Roster.NightsAgainAfter})");
                if (HasMemory(seven, RPRosterDefOf.RP_FairRota)) RosterFail(check, "the fair rota before anyone was swapped");
                vsc.shiftPin = ShiftPin.Days;
                ssc.shiftPin = ShiftPin.Days;
                tsc.shiftPin = ShiftPin.Days;
                ShiftSystem.Rebalance(new List<Pawn> { six, seven, dayGuard, stale }, true);
                if (vsc.nightShift || vsc.nightsRun != 0 || !HasMemory(seven, RPRosterDefOf.RP_FairRota)) RosterFail(check, $"a guard taken off nights after seven is on nights {vsc.nightShift}, run {vsc.nightsRun}, the fair rota {HasMemory(seven, RPRosterDefOf.RP_FairRota)} (want off, 0, the thought)");
                if (ssc.nightShift || ssc.nightsRun != 0 || HasMemory(six, RPRosterDefOf.RP_FairRota)) RosterFail(check, $"a guard taken off nights after six: the fair rota {HasMemory(six, RPRosterDefOf.RP_FairRota)} (want none), run {ssc.nightsRun}");
                if (tsc.nightShift || tsc.nightsRun != 0 || tsc.lastNightDay != -1 || HasMemory(stale, RPRosterDefOf.RP_FairRota)) RosterFail(check, $"a guard taken off nights whose long run was broken by an absence: the fair rota {HasMemory(stale, RPRosterDefOf.RP_FairRota)} (want none), run {tsc.nightsRun}");

                // A night missed breaks the run: six nights, three days of leave, back on nights is a run of one and no thought (and a suspension, a pin to days for a day, the same). This is the last scenario, and the guards of the
                // ones above are put on days for it (the daily pass resets a day guard's run and gives a thought to nobody: they are on the roster, and tallied with these). The days tallied and the days the shifts are changed on are one run of days.
                foreach (Pawn p in new[] { six, seven, dayGuard, stale }) p.GetComp<CompStaff>().nightShift = false;
                Pawn leave = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), suspended = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), swapped = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), steady = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn[] nights = { leave, suspended, swapped, steady };
                foreach (Pawn p in nights) (p.GetComp<CompStaff>().nightShift, p.GetComp<CompStaff>().nightsRun, p.GetComp<CompStaff>().lastNightDay) = (true, 0, -1);
                int d0 = today + 100;
                for (int d = d0; d < d0 + 6; d++) ShiftSystem.Daily(map, d);
                if (nights.Any(p => p.GetComp<CompStaff>().nightsRun != 6 || HasMemory(p, RPRosterDefOf.RP_NightsAgain))) RosterFail(check, "six nights in a row are not a run of six without the thought");
                CompStaff lsc = leave.GetComp<CompStaff>(), usc = suspended.GetComp<CompStaff>(), wsc = swapped.GetComp<CompStaff>();
                (lsc.leaveUntilTick, lsc.restPending) = (Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay, true);
                CorruptionSystem.Suspend(suspended, usc);
                // (A guard taken off nights for three days and put back: a pin to days and off it again, as the balance does it.)
                ShiftSystem.SetShift(swapped, wsc, false, d0 + 6);
                for (int d = d0 + 6; d < d0 + 9; d++) ShiftSystem.Daily(map, d);
                lsc.leaveUntilTick = -1;
                usc.suspendedUntilTick = -1;
                ShiftSystem.SetShift(swapped, wsc, true, d0 + 9);
                for (int d = d0 + 9; d < d0 + 10; d++) ShiftSystem.Daily(map, d);
                foreach (Pawn p in new[] { leave, suspended, swapped })
                    if (p.GetComp<CompStaff>().nightsRun != 1 || HasMemory(p, RPRosterDefOf.RP_NightsAgain)) RosterFail(check, $"{p.LabelShort}, back on nights after a night missed, is on run {p.GetComp<CompStaff>().nightsRun} with the thought {HasMemory(p, RPRosterDefOf.RP_NightsAgain)} (want 1, none)");
                if (steady.GetComp<CompStaff>().nightsRun != 10) RosterFail(check, $"a guard who worked every night for ten is on run {steady.GetComp<CompStaff>().nightsRun} (want 10)");
                ShiftSystem.Daily(map, d0 + 10);
                if (lsc.nightsRun != 2) RosterFail(check, $"the night after the first night back is run {lsc.nightsRun} (want 2)");
                // The thoughts are real moods: the nights one pulls down and the rota one lifts.
                if (RPRosterDefOf.RP_NightsAgain.stages[0].baseMoodEffect >= 0f || RPRosterDefOf.RP_FairRota.stages[0].baseMoodEffect <= 0f) RosterFail(check, "the nights thought is not a low or the rota thought not a high");
                RPLog.Message("[RimPrison:test] roster thoughts: nights again from the seventh night in a row, a night missed (leave, suspension, a day off) begins the run again, a fair rota for one taken off after a current run, nothing for six or a stale run");
            });
        }

        // ---- the saveload plan -------------------------------------------------------------------------------------------

        /// <summary>Everything the roster keeps in a save as one comparable string: the handover and the overtime owed, and every guard's pin, run, last night and shift (those that are off their defaults).</summary>
        private static string RosterDescribe(PrisonGame g, Map map)
        {
            string guards = string.Join(",", StaffCommute.AllStaff(map).Where(s => s.GetComp<CompStaff>() is { shiftPin: not ShiftPin.Auto } or { nightsRun: > 0 } or { lastNightDay: >= 0 }).OrderBy(s => s.thingIDNumber)
                .Select(s => $"{s.thingIDNumber}:{s.GetComp<CompStaff>().shiftPin}:{s.GetComp<CompStaff>().nightsRun}:{s.GetComp<CompStaff>().lastNightDay}:{s.GetComp<CompStaff>().nightShift}"));
            return $"{g.handoverHours}/{g.overtimeOwed}:{g.overtimeHours}/{guards}";
        }

        /// <summary>A handover of two hours with overtime owed, one guard pinned to nights with a run of five (the last of them last night) and another pinned to days.</summary>
        private static void SeedRosterForReload(Map map)
        {
            List<Pawn> guards = StaffCommute.AllStaff(map).Where(s => Roster.OnRoster(s.GetComp<CompStaff>()) && !s.GetComp<CompStaff>().statePosted).OrderBy(s => s.thingIDNumber).ToList();
            if (guards.Count < 2)
            {
                problems0.Add("saveload: fewer than two guards to pin");
                return;
            }
            PrisonGame.Instance.handoverHours = 2;
            (PrisonGame.Instance.overtimeOwed, PrisonGame.Instance.overtimeHours) = (37, 5);
            CompStaff nights = guards[0].GetComp<CompStaff>(), days = guards[1].GetComp<CompStaff>();
            (nights.shiftPin, nights.nightsRun, nights.lastNightDay, nights.nightShift) = (ShiftPin.Nights, 5, Ledger.CurrentDay - 1, true);
            (days.shiftPin, days.nightsRun, days.lastNightDay, days.nightShift) = (ShiftPin.Days, 0, -1, false);
        }
    }
}
