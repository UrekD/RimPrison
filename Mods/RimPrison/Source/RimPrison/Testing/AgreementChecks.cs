using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Checks for staff agreements (<see cref="StaffAgreements"/>, gap analysis 6, item 6): an understaffing promise is kept by real cover over the hours (and not by cover added for the last one, nor by the
    /// day staff for a nights promise), a safety promise is broken by a blow from an inmate, a pay promise is paid at each settlement and broken by taking the rise back in time, a promise reloaded just before it is due is
    /// judged once, one whose representative left stands, and a promise kept buys fifteen days of peace while one broken takes the bonus off the next letter. Each is self-contained and rolled back: the prison's real staff are
    /// made callout staff for the check (as the roster's are, <see cref="RosterRealStaffAside"/>) so that the cover is the check's own, the book is a fresh one put back after, and the clock is moved, never ticked, so
    /// that an hour's pass is the check's own call (<see cref="StaffAgreements.Hourly"/> is given the hour). Every comparison names what the check saw and how it would have failed.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckAgreements(Map map)
        {
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns.Concat(PrisonGame.Instance.OffSite));
            CheckAgreementUnderstaffing(map);
            CheckAgreementShareEdge(map);
            CheckAgreementNights(map);
            CheckAgreementSafety(map);
            CheckAgreementPay(map);
            CheckAgreementReload(map);
            CheckAgreementRepresentative(map);
            CheckAgreementPeaceAndNoBonus(map);
            Pawn[] left = LeftBehind(map, pawnsBefore).ToArray();
            if (left.Length > 0) problems0.Add($"agreement checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void AgreementFail(string check, string what) => problems0.Add($"agreement ({check}): {what}");

        /// <summary>
        /// A fresh book (the real one put back), the real staff aside, the handover off and the clock at noon of a day (put back): the stage every check here is played on. Returns the tick of the noon, the clock the
        /// hours are counted from.
        /// </summary>
        private static int AgreementStage(Map map, Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            StaffAgreementBook book0 = g.agreements;
            (int owed0, int hours0) = (g.overtimeOwed, g.overtimeHours);
            g.agreements = new StaffAgreementBook();
            (g.overtimeOwed, g.overtimeHours) = (0, 0);
            r.Add(() =>
            {
                g.agreements = book0;
                (g.overtimeOwed, g.overtimeHours) = (owed0, hours0);
                Roster.Invalidate();
            });
            RosterRealStaffAside(map, r);
            // (The roster's set-aside leaves the real staff at home to the leave that ends in five days; a real member of staff at home would still be a representative, a pay role and a judge's party here, and the
            // clock is moved days ahead. Callout staff are none of them: every real member of staff but the warden is out of the agreement logic for the check.)
            List<CompStaff> atHome = StaffCommute.AllStaff(map).Select(p => p.GetComp<CompStaff>()).Where(sc => !sc.role.warden && sc.leaveAtTick < 0).ToList();
            foreach (CompStaff sc in atHome) sc.leaveAtTick = int.MaxValue;
            r.Add(() =>
            {
                foreach (CompStaff sc in atHome) sc.leaveAtTick = -1;
            });
            RosterKeepTimetables(map, r);
            g.handoverHours = 0;
            int clock0 = Find.TickManager.TicksGame;
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            // (Forward to the next noon, the hour the cover of the checks is worked out for.)
            int noon = clock0 + (12 - GenLocalDate.HourOfDay(map) + 24) % 24 * GenDate.TicksPerHour;
            Find.TickManager.DebugSetTicksGame(noon);
            Roster.Invalidate();
            return noon;
        }

        /// <summary>A guard of the check's own, on days or on nights, on site.</summary>
        private static Pawn AgreementGuard(Map map, Rollback r, bool night)
        {
            Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
            CompStaff sc = p.GetComp<CompStaff>();
            (sc.nightShift, sc.shiftPin, sc.nightsRun, sc.lastNightDay) = (night, night ? ShiftPin.Nights : ShiftPin.Days, 0, -1);
            ShiftSystem.Apply(p, night);
            Roster.Invalidate();
            return p;
        }

        /// <summary>
        /// The hourly pass, <paramref name="hours"/> times, the clock moved on an hour before each (from the noon it starts at); <paramref name="before"/> is given the number of the pass and the hour of the day before it
        /// is measured, to change the cover in the middle of the term. Stops when the promise is judged.
        /// </summary>
        private static void AgreementRun(Map map, int noon, int hours, Action<int, int> before = null, bool commute = false, Action<int, int> afterCommute = null)
        {
            PrisonGame g = PrisonGame.Instance;
            for (int i = 1; i <= hours && g.agreements.active != null; i++)
            {
                Find.TickManager.DebugSetTicksGame(noon + i * GenDate.TicksPerHour);
                int hour = (12 + i) % 24;
                before?.Invoke(i, hour);
                if (commute)
                {
                    // The hour as the prison's pass has it (PrisonGame orders the commute before the agreements): the off-shift are sent home, the next shift comes in.
                    StaffCommute.Hourly(map, g, hour);
                    // (The walk home is the game's own on real ticks, hours of them here: the walkers are taken to the end of it, as the last toil of the walk does.)
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(p => p.CurJobDef == RPCommuteDefOf.RP_GoHome).ToList()) StaffCommute.Leave(p);
                    afterCommute?.Invoke(i, hour);
                }
                StaffAgreements.Hourly(map, g, hour);
            }
        }

        private static AgreementRecord AgreementLast(StaffGrievance grievance) => PrisonGame.Instance.agreements.history.LastOrDefault(h => h.grievance == grievance);

        // ---- understaffing -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Two day guards and three night guards, a letter over understaffing at noon (its baseline: the guards the roster has on duty in each hour): a day guard and a night guard added after the letter and before the answer
        /// keep it, in every hour of the term (the baseline is the letter's, not what the roster is by the time they answer), and the cover of the last hour does not.
        /// </summary>
        private static void CheckAgreementUnderstaffing(Map map)
        {
            Guarded("agreement - understaffing: real added cover over the hours keeps it, cover in the last hour does not", map, r =>
            {
                const string check = "understaffing";
                PrisonGame g = PrisonGame.Instance;
                foreach (bool lastHourOnly in new[] { false, true })
                {
                    int noon = AgreementStage(map, r);
                    var staff = new List<Pawn> { AgreementGuard(map, r, false), AgreementGuard(map, r, false), AgreementGuard(map, r, true), AgreementGuard(map, r, true), AgreementGuard(map, r, true) };
                    string run = lastHourOnly ? "cover added in the last hour only" : "cover added on the day of the promise";
                    int onDuty = StaffCommute.GuardsOnDuty(map, 12);
                    if (onDuty != 2) AgreementFail(check, $"{run}: at noon with two day guards and three night guards the commute counts {onDuty} on duty (want 2): the baseline of the check is wrong");
                    Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Understaffed);
                    if (rep == null || !staff.Contains(rep)) AgreementFail(check, $"{run}: the representative is {rep?.LabelShort ?? "nobody"}, not one of the check's own guards");
                    // The letter is made now (its baseline taken); the cover is added, and the warden answers after it.
                    int[] letter = StaffAgreements.RosterBaseline(map);
                    var added = new List<Pawn>();
                    if (!lastHourOnly) added.AddRange(new[] { AgreementGuard(map, r, false), AgreementGuard(map, r, true) });
                    if (!StaffAgreements.Agree(map, g, StaffGrievance.Understaffed, rep, letter)) { AgreementFail(check, $"{run}: the promise was not made"); continue; }
                    StaffAgreement a = g.agreements.active;
                    if (a.baselines == null || !a.baselines.SequenceEqual(letter) || a.baselines[12] != 2 || a.TargetAt(12) != 3 || a.hoursNeeded != 96)
                        AgreementFail(check, $"{run}: the term is baseline [{(a.baselines == null ? "none" : string.Join(",", a.baselines))}], {a.hoursNeeded} hours needed (want the letter's [{string.Join(",", letter)}], 2 on duty at noon so 3 wanted, 96 of 120)");
                    if (!lastHourOnly && a.baselines != null && a.baselines.SequenceEqual(StaffAgreements.RosterBaseline(map))) AgreementFail(check, $"{run}: the baseline is what the roster is now, not what the letter had (the cover was added after it)");
                    AgreementRun(map, noon, StaffAgreements.TermDays * 24, (i, hour) =>
                    {
                        if (lastHourOnly && i == StaffAgreements.TermDays * 24) added.AddRange(new[] { AgreementGuard(map, r, false), AgreementGuard(map, r, true) });
                    });
                    AgreementRecord last = AgreementLast(StaffGrievance.Understaffed);
                    if (g.agreements.active != null || last == null)
                    {
                        AgreementFail(check, $"{run}: the promise was not judged at its deadline (active {g.agreements.active != null}, record {last?.outcome.ToString() ?? "none"})");
                        continue;
                    }
                    AgreementOutcome want = lastHourOnly ? AgreementOutcome.Broken : AgreementOutcome.Fulfilled;
                    if (last.outcome != want)
                        AgreementFail(check, $"{run}: judged {last.outcome}, want {want} (a guard more on each shift over the real hours keeps it; cover that came with the last hour of five days does not)");
                    if (!lastHourOnly && !g.agreements.peaceUntil.ContainsKey(StaffGrievance.Understaffed)) AgreementFail(check, "a kept promise bought no peace");
                    if (staff.Concat(added).Any(p => p.GetComp<CompStaff>() is { } sc && StaffAgreements.Grieves(sc, StaffGrievance.Understaffed, null) && p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(lastHourOnly ? RPAgreementDefOf.RP_BrokenPromise : RPAgreementDefOf.RP_KeptTheirWord) == null))
                        AgreementFail(check, $"{run}: a guard has no memory of the promise being {(lastHourOnly ? "broken" : "kept")}");
                    RPLog.Message($"[RimPrison:test] agreement understaffing ({run}): {last.outcome}, {(lastHourOnly ? "last hour only" : "all five days")} of cover, {a.compliantHours} of {a.hoursSeen} hours at target (needed {a.hoursNeeded})");
                    g.agreements = new StaffAgreementBook();
                    foreach (Pawn p in staff.Concat(added).ToList()) if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    Roster.Invalidate();
                }
                if (RPAgreementDefOf.RP_BrokenPromise.stages[0].baseMoodEffect >= -RPAgreementDefOf.RP_KeptTheirWord.stages[0].baseMoodEffect || RPAgreementDefOf.RP_KeptTheirWord.stages[0].baseMoodEffect <= 0f)
                    AgreementFail(check, "the broken promise is not a stronger low than the kept word is a high");
            });
        }

        /// <summary>The 80%: 96 of 120 hours keeps it, 95 does not (the day guard added after the letter is taken away for 24 or 25 of the hours the day guards alone cover, while the night guard added with it stays).</summary>
        private static void CheckAgreementShareEdge(Map map)
        {
            Guarded("agreement - 96 of 120 hours keeps the promise, 95 does not", map, r =>
            {
                const string check = "share";
                PrisonGame g = PrisonGame.Instance;
                foreach (int lost in new[] { 24, 25 })
                {
                    int noon = AgreementStage(map, r);
                    var base0 = new List<Pawn> { AgreementGuard(map, r, false), AgreementGuard(map, r, false), AgreementGuard(map, r, true), AgreementGuard(map, r, true), AgreementGuard(map, r, true) };
                    Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Understaffed);
                    int[] letter = StaffAgreements.RosterBaseline(map);
                    Pawn extra = AgreementGuard(map, r, false), extraNight = AgreementGuard(map, r, true);
                    if (!StaffAgreements.Agree(map, g, StaffGrievance.Understaffed, rep, letter)) { AgreementFail(check, "the promise was not made"); continue; }
                    CompStaff esc = extra.GetComp<CompStaff>();
                    int removed = 0;
                    // (The extra day guard is on leave for the first of the hours 10:00 to 17:00, when the night guards sleep and the day guards are the whole of the cover: those hours are two on duty, short of the three.
                    // Every other hour is up to its target without them: the extra night guard is there, and the shifts overlap 05:00-10:00 and 17:00-22:00 (the commute's hour before a shift counts as on duty).)
                    AgreementRun(map, noon, StaffAgreements.TermDays * 24, (i, hour) =>
                    {
                        esc.leaveUntilTick = -1;
                        if (removed < lost && hour is >= 10 and < 17 && !StaffCommute.ShouldBeHome(extra, esc, hour))
                        {
                            esc.leaveUntilTick = int.MaxValue;
                            removed++;
                        }
                        Roster.Invalidate();
                    });
                    AgreementRecord last = AgreementLast(StaffGrievance.Understaffed);
                    AgreementOutcome want = lost <= 24 ? AgreementOutcome.Fulfilled : AgreementOutcome.Broken;
                    if (last == null || last.outcome != want)
                        AgreementFail(check, $"with {120 - lost} of 120 hours at target (the third guard away for {removed} of the hours the day guards alone cover) the promise was judged {last?.outcome.ToString() ?? "not at all"}, want {want} (80% of 120 is 96)");
                    RPLog.Message($"[RimPrison:test] agreement share: {120 - lost} of 120 hours -> {last?.outcome}");
                    g.agreements = new StaffAgreementBook();
                    foreach (Pawn p in base0.Concat(new[] { extra, extraNight })) if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    Roster.Invalidate();
                }
            });
        }

        // ---- nights --------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Three night guards and one day guard (three of the four on nights: the grievance itself): another night guard does nothing for it, hiring day guards or moving guards to days ends it. It is the grievance's own condition
        /// (<see cref="IncidentWorker_StaffStrike.TooManyOnNights"/>) that is looked at, every hour of the five days, over everyone employed: the days run with the commute in them (the day staff really are at home from 22:00
        /// to 05:00, the night staff in the afternoon), where the staff on the map are a different share every hour. A measure over the staff on the map has the night shift alone on it from 23:00 to 04:00 and fails the
        /// day-staff answers at those hours (six of the 120 are enough to break the promise: it needs 96).
        /// </summary>
        private static void CheckAgreementNights(Map map)
        {
            Guarded("agreement - nights: another night guard does not meet it, day staff or guards moved to days do", map, r =>
            {
                const string check = "nights";
                PrisonGame g = PrisonGame.Instance;
                foreach (string answer in new[] { "a night guard hired", "day guards hired", "guards moved to days" })
                {
                    int noon = AgreementStage(map, r);
                    var staff = new List<Pawn> { AgreementGuard(map, r, true), AgreementGuard(map, r, true), AgreementGuard(map, r, true), AgreementGuard(map, r, false) };
                    string run = answer;
                    if (!IncidentWorker_StaffStrike.TooManyOnNights(map)) AgreementFail(check, $"{run}: three of four guards on nights is not the nights grievance (the check's own premise)");
                    Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Nights);
                    if (!StaffAgreements.Agree(map, g, StaffGrievance.Nights, rep)) { AgreementFail(check, $"{run}: the promise was not made"); continue; }
                    StaffAgreement a = g.agreements.active;
                    if (a.hoursNeeded != 96 || a.baselines != null) AgreementFail(check, $"{run}: the term is {a.hoursNeeded} hours needed and a baseline of {a.baselines?.Count.ToString() ?? "none"} (want 96 of the 120 hourly looks, and no baseline: the grievance itself is the term)");
                    // Done the minute it was agreed.
                    if (answer == "a night guard hired") staff.Add(AgreementGuard(map, r, true));
                    else if (answer == "day guards hired") for (int i = 0; i < 3; i++) staff.Add(AgreementGuard(map, r, false));
                    else foreach (Pawn p in staff.Take(2))
                    {
                        // (Pinned to days, as a warden does it: the next balance would put a guard pinned to nights straight back.)
                        CompStaff psc = p.GetComp<CompStaff>();
                        (psc.nightShift, psc.shiftPin) = (false, ShiftPin.Days);
                        ShiftSystem.Apply(p, false);
                    }
                    Roster.Invalidate();
                    // The five days with the commute: at each hour the staff on the map are those whose shift it is. At night (23:00 to 04:00) the day staff must really be at home, so that with day staff hired the share of the
                    // staff on the map that are on nights (the measure this check was written to catch) reads "too many" at every one of those hours.
                    int nightHours = 0, mapOnlyWouldCall = 0;
                    bool dayStaffOut = false;
                    AgreementRun(map, noon, StaffAgreements.TermDays * 24, commute: true, afterCommute: (i, hour) =>
                    {
                        if (hour >= 5 && hour < 23) return;
                        nightHours++;
                        List<Pawn> onMap = IncidentWorker_StaffStrike.Staff(map);
                        if (onMap.Any(p => !p.GetComp<CompStaff>().nightShift && !p.GetComp<CompStaff>().role.warden)) dayStaffOut = true;
                        if (onMap.Count(p => p.GetComp<CompStaff>().nightShift) * 2 > onMap.Count) mapOnlyWouldCall++;
                    });
                    if (dayStaffOut) AgreementFail(check, $"{run}: day staff were still on the map between 23:00 and 04:00 (the commute did not send them home: the check's premise)");
                    if (nightHours < 6 || answer == "day guards hired" && mapOnlyWouldCall != nightHours)
                        AgreementFail(check, $"{run}: {nightHours} night hours were looked at, a count of the staff on the map would have called {mapOnlyWouldCall} of them too many on nights (want all of them with the day staff hired: only the night shift is on the map then)");
                    AgreementRecord last = AgreementLast(StaffGrievance.Nights);
                    AgreementOutcome want = answer == "a night guard hired" ? AgreementOutcome.Broken : AgreementOutcome.Fulfilled;
                    if (last == null || last.outcome != want)
                        AgreementFail(check, $"{run}: judged {last?.outcome.ToString() ?? "not at all"}, want {want} (the promise is that too many of the staff are not on nights any more: a guard more on nights is more of them)");
                    RPLog.Message($"[RimPrison:test] agreement nights ({run}): {last?.outcome}, {a.compliantHours} of {a.hoursSeen} hourly looks met it (needed {a.hoursNeeded})");
                    if (a.hoursSeen != 120) AgreementFail(check, $"{run}: {a.hoursSeen} hours were looked at in five days (want 120: every hour)");
                    if (want == AgreementOutcome.Broken && a.compliantHours != 0) AgreementFail(check, $"{run}: {a.compliantHours} hours met it with a night guard more (want 0)");
                    if (want == AgreementOutcome.Fulfilled && a.compliantHours != 120) AgreementFail(check, $"{run}: {a.compliantHours} of 120 hours met it with the nights share fixed at the start (want 120)");
                    g.agreements = new StaffAgreementBook();
                    foreach (Pawn p in staff)
                    {
                        g.home.Remove(p);
                        if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    }
                    StaffCareers.InvalidateLeads();
                    Roster.Invalidate();
                }
            });
        }

        // ---- safety --------------------------------------------------------------------------------------------------------

        /// <summary>No blow from an inmate keeps it; one breaks it at the next hour (a blow from anyone else is not an inmate's).</summary>
        private static void CheckAgreementSafety(Map map)
        {
            Guarded("agreement - safety: a member of staff hurt by an inmate breaks it", map, r =>
            {
                const string check = "safety";
                PrisonGame g = PrisonGame.Instance;
                int noon = AgreementStage(map, r);
                Pawn guard = AgreementGuard(map, r, false), other = AgreementGuard(map, r, false);
                Pawn inmate = Throwaway(map, r);
                if (!StaffAgreements.Agree(map, g, StaffGrievance.Safety, StaffAgreements.Representative(map, StaffGrievance.Safety))) { AgreementFail(check, "the promise was not made"); return; }
                // A blow from a colleague is not an inmate's.
                // (Armour-piercing, on the torso: a vest cannot turn the blow aside.)
                guard.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 2f, 1f, -1f, other, guard.RaceProps.body.corePart));
                AgreementRun(map, noon, 30);
                if (g.agreements.active == null) AgreementFail(check, "a blow from a colleague broke it");
                guard.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 2f, 1f, -1f, inmate, guard.RaceProps.body.corePart));
                if (g.agreements.active is not { hurtTick: >= 0 }) AgreementFail(check, "the inmate's blow was not noted (the damage patch does not tell the agreement)");
                // At the next pass, not at the deadline.
                AgreementRun(map, noon + 30 * GenDate.TicksPerHour, 1, (i, hour) => { });
                AgreementRecord last = AgreementLast(StaffGrievance.Safety);
                if (g.agreements.active != null || last is not { outcome: AgreementOutcome.Broken }) AgreementFail(check, $"a guard hurt by an inmate left it {(g.agreements.active != null ? "running" : last?.outcome.ToString())} (want broken at the next hour)");
                if (g.agreements.peaceUntil.ContainsKey(StaffGrievance.Safety)) AgreementFail(check, "a broken promise bought peace");
                if (!StaffAgreements.BrokenBefore(StaffGrievance.Safety)) AgreementFail(check, "a broken promise is not remembered by the next demand (BrokenBefore)");
                if (guard.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPAgreementDefOf.RP_BrokenPromise) == null) AgreementFail(check, "the guards have no memory of the broken promise");

                // Five quiet days keep it.
                g.agreements = new StaffAgreementBook();
                int noon2 = Find.TickManager.TicksGame;
                if (!StaffAgreements.Agree(map, g, StaffGrievance.Safety, StaffAgreements.Representative(map, StaffGrievance.Safety))) { AgreementFail(check, "the second promise was not made"); return; }
                for (int i = 1; i <= StaffAgreements.TermDays * 24 && g.agreements.active != null; i++)
                {
                    Find.TickManager.DebugSetTicksGame(noon2 + i * GenDate.TicksPerHour);
                    // A blow that lands after the deadline, before the pass that judges it, is not in the term.
                    if (i == StaffAgreements.TermDays * 24) g.agreements.active.hurtTick = g.agreements.active.deadlineTick + 1;
                    StaffAgreements.Hourly(map, g, GenLocalDate.HourOfDay(map));
                }
                if (AgreementLast(StaffGrievance.Safety) is not { outcome: AgreementOutcome.Fulfilled }) AgreementFail(check, $"five days with nobody hurt (and a blow a tick after the deadline) did not keep it ({AgreementLast(StaffGrievance.Safety)?.outcome.ToString() ?? "not judged"})");
                RPLog.Message("[RimPrison:test] agreement safety: a colleague's blow is nothing, an inmate's breaks it at the next hour, five quiet days keep it (a blow after the deadline is not in the term)");
            });
        }

        // ---- pay -----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The rise is in the wage the settlement pays; five settlements keep the promise; the role has one rise, not two; taking it back in the term, or within the term and the peace after it (twenty days of agreeing),
        /// breaks the promise, and after that does not; and taking it back whenever it is ends the peace over pay.
        /// </summary>
        private static void CheckAgreementPay(Map map)
        {
            Guarded("agreement - pay: the rise is paid at each settlement and taking it back breaks it", map, r =>
            {
                const string check = "pay";
                PrisonGame g = PrisonGame.Instance;
                StaffRoleDef guardRole = RPSecurityDefOf.RP_Guard;
                StaffRoleDef cookRole = StaffUtility.Roles.FirstOrDefault(x => x != guardRole && !x.warden && x.dailyWage > 0 && !x.visitor);
                foreach (string scenario in new[] { "kept, then taken back in time", "taken back in the term", "kept, then taken back inside the peace", "kept, then taken back after the peace" })
                {
                    int noon = AgreementStage(map, r);
                    Pawn guard = AgreementGuard(map, r, false), colleague = AgreementGuard(map, r, false);
                    CompStaff sc = guard.GetComp<CompStaff>();
                    int wage0 = StaffCareers.Wage(sc), payroll0 = StaffCareers.BasePay(StaffCommute.AllStaff(map)), cook0 = cookRole == null ? 0 : StaffCareers.WageAt(cookRole, 0);
                    Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Pay);
                    if (rep == null || !StaffAgreements.Agree(map, g, StaffGrievance.Pay, rep)) { AgreementFail(check, $"{scenario}: the promise was not made"); continue; }
                    int wage1 = StaffCareers.Wage(sc), want1 = (int)Math.Ceiling(wage0 * 1.1 - 1e-6);
                    if (wage1 != want1 || wage1 <= wage0) AgreementFail(check, $"{scenario}: a guard's wage went {wage0} -> {wage1} with the rise (want {want1}, a tenth more rounded up)");
                    if (cookRole != null && StaffCareers.WageAt(cookRole, 0) != cook0) AgreementFail(check, $"{scenario}: another role's wage moved {cook0} -> {StaffCareers.WageAt(cookRole, 0)} (the rise is for the grieving role only)");
                    if (StaffAgreements.CannotAgree(map, StaffGrievance.Pay, rep) == null) AgreementFail(check, $"{scenario}: a second promise could be made with one running");
                    if (scenario == "taken back in the term")
                    {
                        // Two settlements, then the warden takes it back: broken now, the wage back, no peace.
                        for (int i = 0; i < 2; i++) DailySettlement.PayWages(g, map);
                        if (g.agreements.active is not { paidSettlements: 2 }) AgreementFail(check, $"{scenario}: {g.agreements.active?.paidSettlements} settlements counted after two (want 2)");
                        StaffAgreements.TakeBack(map, g, g.agreements.rises.First());
                        if (g.agreements.active != null || AgreementLast(StaffGrievance.Pay) is not { outcome: AgreementOutcome.Broken }) AgreementFail(check, $"{scenario}: taking the rise back in the term left the promise {(g.agreements.active != null ? "running" : AgreementLast(StaffGrievance.Pay)?.outcome.ToString())} (want broken)");
                        if (StaffCareers.Wage(sc) != wage0 || StaffAgreements.RiseStands(guardRole)) AgreementFail(check, $"{scenario}: the wage is {StaffCareers.Wage(sc)} after the rise was taken back (want {wage0})");
                        if (g.agreements.peaceUntil.ContainsKey(StaffGrievance.Pay) || !StaffAgreements.BrokenBefore(StaffGrievance.Pay)) AgreementFail(check, $"{scenario}: no broken-promise consequence (peace {g.agreements.peaceUntil.ContainsKey(StaffGrievance.Pay)}, broken before {StaffAgreements.BrokenBefore(StaffGrievance.Pay)})");
                        if (guard.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPAgreementDefOf.RP_BrokenPromise) == null) AgreementFail(check, $"{scenario}: the guards have no memory of the broken promise");
                    }
                    else
                    {
                        int funds0 = g.ledger.funds;
                        for (int i = 0; i < StaffAgreements.TermDays; i++)
                        {
                            int before = g.ledger.funds;
                            int paid = DailySettlement.PayWages(g, map);
                            int expected = StaffCareers.BasePay(StaffCommute.AllStaff(map));
                            if (paid != expected || before - g.ledger.funds != expected) AgreementFail(check, $"{scenario}: settlement {i + 1} paid {paid} (funds {before} -> {g.ledger.funds}), the staff's wages with the rise are {expected}");
                        }
                        if (StaffCareers.BasePay(StaffCommute.AllStaff(map)) - payroll0 < wage1 - wage0) AgreementFail(check, $"{scenario}: the payroll rose {StaffCareers.BasePay(StaffCommute.AllStaff(map)) - payroll0} for a guard's rise of {wage1 - wage0}");
                        if (g.agreements.active is not { paidSettlements: StaffAgreements.TermDays }) AgreementFail(check, $"{scenario}: {g.agreements.active?.paidSettlements} settlements counted after {StaffAgreements.TermDays} (want {StaffAgreements.TermDays})");
                        Find.TickManager.DebugSetTicksGame(noon + GenDate.TicksPerHour);
                        StaffAgreements.Hourly(map, g, 13);
                        if (AgreementLast(StaffGrievance.Pay) is not { outcome: AgreementOutcome.Fulfilled } || !StaffAgreements.RiseStands(guardRole))
                            AgreementFail(check, $"{scenario}: five settlements with the rise left it {AgreementLast(StaffGrievance.Pay)?.outcome.ToString() ?? "not judged"}, rise standing {StaffAgreements.RiseStands(guardRole)} (want kept, and the rise stays)");
                        PayRise rise = g.agreements.rises.FirstOrDefault();
                        if (rise == null) { AgreementFail(check, $"{scenario}: no rise stands after the promise was kept"); continue; }
                        if (!StaffAgreements.InPeace(StaffGrievance.Pay)) AgreementFail(check, $"{scenario}: a kept promise bought no peace over pay");
                        // One rise to a role: a second promise over pay for the guards is refused while theirs stands (the letter's own representative, or any other).
                        if (StaffAgreements.CannotAgree(map, StaffGrievance.Pay, rep) == null || StaffAgreements.Agree(map, g, StaffGrievance.Pay, rep) || g.agreements.rises.Count != 1)
                            AgreementFail(check, $"{scenario}: a second rise for the guards was possible ({g.agreements.rises.Count} rises)");
                        bool inTime = scenario is "kept, then taken back in time" or "kept, then taken back inside the peace";
                        // (Inside the peace: a dozen days after agreeing, past the term and short of the twenty. After it: the rise is the warden's.)
                        if (scenario == "kept, then taken back inside the peace") Find.TickManager.DebugSetTicksGame(rise.agreedTick + 12 * GenDate.TicksPerDay);
                        else if (!inTime) Find.TickManager.DebugSetTicksGame(rise.agreedTick + (StaffAgreements.TakeBackDays + 1) * GenDate.TicksPerDay);
                        if (scenario == "kept, then taken back after the peace" && StaffAgreements.TakingBackBreaks(rise)) AgreementFail(check, $"{scenario}: taking the rise back after {StaffAgreements.TakeBackDays} days is said to break the promise");
                        if (scenario == "kept, then taken back inside the peace" && !StaffAgreements.TakingBackBreaks(rise)) AgreementFail(check, $"{scenario}: taking the rise back twelve days after agreeing is not said to break the promise (the term and the peace are {StaffAgreements.TakeBackDays} days)");
                        StaffAgreements.TakeBack(map, g, rise);
                        AgreementOutcome wantOutcome = inTime ? AgreementOutcome.Broken : AgreementOutcome.Fulfilled;
                        if (AgreementLast(StaffGrievance.Pay) is not { } rec || rec.outcome != wantOutcome || g.agreements.history.Count(h => h.grievance == StaffGrievance.Pay) != 1)
                            AgreementFail(check, $"{scenario}: after the rise was taken back the record says {AgreementLast(StaffGrievance.Pay)?.outcome.ToString()} (want {wantOutcome}, one record)");
                        if (StaffCareers.Wage(sc) != wage0) AgreementFail(check, $"{scenario}: the wage is {StaffCareers.Wage(sc)} after the rise was taken back (want {wage0})");
                        if (StaffAgreements.InPeace(StaffGrievance.Pay)) AgreementFail(check, $"{scenario}: peace over pay is still running after the rise was taken back (taking it back ends it whenever it is)");
                    }
                    RPLog.Message($"[RimPrison:test] agreement pay ({scenario}): ok");
                    g.agreements = new StaffAgreementBook();
                    foreach (Pawn p in new[] { guard, colleague }) if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    Roster.Invalidate();
                }
            });
        }

        // ---- a reload just before judgment -----------------------------------------------------------------------------

        /// <summary>The book saved and loaded as a game does it (Scribe), through a file of its own in the save-data folder.</summary>
        private static StaffAgreementBook AgreementRoundTrip(StaffAgreementBook book)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "rp_agreement_roundtrip.xml");
            StaffAgreementBook loaded = null;
            try
            {
                SafeSaver.Save(path, "agreementtest", () => Scribe_Deep.Look(ref book, "book"));
                Scribe.loader.InitLoading(path);
                try
                {
                    Scribe_Deep.Look(ref loaded, "book");
                    Scribe.loader.FinalizeLoading();
                }
                catch
                {
                    Scribe.ForceStop();
                    throw;
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
            return loaded;
        }

        /// <summary>A promise saved and loaded one hour before it is due is judged once at its deadline; the hour of the load is not counted twice; and the same hour asked for twice counts once.</summary>
        private static void CheckAgreementReload(Map map)
        {
            Guarded("agreement - a reload just before judgment gives one settlement", map, r =>
            {
                const string check = "reload";
                PrisonGame g = PrisonGame.Instance;
                foreach (bool broken in new[] { false, true })
                {
                    int noon = AgreementStage(map, r);
                    var staff = new List<Pawn> { AgreementGuard(map, r, false), AgreementGuard(map, r, false), AgreementGuard(map, r, true), AgreementGuard(map, r, true), AgreementGuard(map, r, true) };
                    string run = broken ? "a promise that will be broken" : "a promise that will be kept";
                    if (!StaffAgreements.Agree(map, g, StaffGrievance.Understaffed, StaffAgreements.Representative(map, StaffGrievance.Understaffed))) { AgreementFail(check, $"{run}: the promise was not made"); continue; }
                    // (The cover of the kept one comes after it is agreed: a guard more on each shift.)
                    if (!broken) staff.AddRange(new[] { AgreementGuard(map, r, false), AgreementGuard(map, r, true) });
                    AgreementRun(map, noon, StaffAgreements.TermDays * 24 - 1);
                    if (g.agreements.active == null) { AgreementFail(check, $"{run}: judged before its deadline"); continue; }
                    // The same hour asked for twice (a load puts the clock back on the hour it saved in) counts once.
                    int seen = g.agreements.active.hoursSeen;
                    StaffAgreements.Hourly(map, g, (12 + StaffAgreements.TermDays * 24 - 1) % 24);
                    if (g.agreements.active.hoursSeen != seen) AgreementFail(check, $"{run}: the hour already measured was counted again ({seen} -> {g.agreements.active.hoursSeen})");
                    string before = StaffAgreements.Describe(g.agreements);
                    StaffAgreementBook loaded = AgreementRoundTrip(g.agreements);
                    string after = loaded == null ? "(nothing loaded)" : StaffAgreements.Describe(loaded);
                    if (before != after) { AgreementFail(check, $"{run}: the book changed across a save and a load: {before} became {after}"); continue; }
                    g.agreements = loaded;
                    int funds0 = g.ledger.funds, records0 = g.agreements.history.Count;
                    // The load, and the hour it was saved in asked for again: nothing happens. Then the deadline's hour: one judgment.
                    StaffAgreements.Hourly(map, g, (12 + StaffAgreements.TermDays * 24 - 1) % 24);
                    if (g.agreements.active == null) AgreementFail(check, $"{run}: the load's own hour judged it early");
                    for (int i = 0; i < 4; i++)
                    {
                        // (The deadline's own hour, then three more: the promise is gone from the book and the extra hours judge nothing.)
                        Find.TickManager.DebugSetTicksGame(noon + (StaffAgreements.TermDays * 24 + i) * GenDate.TicksPerHour);
                        StaffAgreements.Hourly(map, g, (12 + i) % 24);
                    }
                    AgreementOutcome want = broken ? AgreementOutcome.Broken : AgreementOutcome.Fulfilled;
                    int judged = g.agreements.history.Count - records0;
                    if (judged != 1 || g.agreements.history.Last().outcome != want || g.agreements.active != null)
                        AgreementFail(check, $"{run}: after the load {judged} judgment(s), the last {g.agreements.history.LastOrDefault()?.outcome.ToString() ?? "none"} (want one, {want})");
                    if (g.ledger.funds != funds0) AgreementFail(check, $"{run}: judging moved funds {funds0} -> {g.ledger.funds} (a promise over cover pays nothing)");
                    RPLog.Message($"[RimPrison:test] agreement reload ({run}): one judgment, {g.agreements.history.Last().outcome}");
                    g.agreements = new StaffAgreementBook();
                    foreach (Pawn p in staff) if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    Roster.Invalidate();
                }
            });
        }

        // ---- the representative -----------------------------------------------------------------------------------------

        /// <summary>The most senior speaks, never a state appointee; if they leave the promise stands under the name saved.</summary>
        private static void CheckAgreementRepresentative(Map map)
        {
            Guarded("agreement - the representative: the most senior, never a state appointee; the promise stands when they leave", map, r =>
            {
                const string check = "representative";
                PrisonGame g = PrisonGame.Instance;
                int noon = AgreementStage(map, r);
                Pawn junior = AgreementGuard(map, r, false), senior = AgreementGuard(map, r, false), chief = AgreementGuard(map, r, false), third = AgreementGuard(map, r, true);
                junior.GetComp<CompStaff>().hiredTick = Find.TickManager.TicksGame - 3 * GenDate.TicksPerDay;
                senior.GetComp<CompStaff>().rank = 1;
                (chief.GetComp<CompStaff>().rank, chief.GetComp<CompStaff>().statePosted) = (2, true);
                third.GetComp<CompStaff>().hiredTick = Find.TickManager.TicksGame - 20 * GenDate.TicksPerDay;
                Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Understaffed);
                if (rep != senior) AgreementFail(check, $"the representative is {rep?.LabelShort ?? "nobody"} (rank {rep?.GetComp<CompStaff>().rank}), want {senior.LabelShort}, the one of the highest rank ({chief.LabelShort}, a state appointee of rank 2, is never one)");
                chief.GetComp<CompStaff>().statePosted = false;
                if (StaffAgreements.Representative(map, StaffGrievance.Understaffed) != chief) AgreementFail(check, "the highest rank is not the representative once they are not the state's");
                chief.GetComp<CompStaff>().statePosted = true;
                // The warden is no one's representative either; only appointees left: nobody, and no promise.
                foreach (Pawn p in new[] { junior, senior, third }) p.GetComp<CompStaff>().leaveAtTick = int.MaxValue;
                if (StaffAgreements.Representative(map, StaffGrievance.Understaffed) != null || StaffAgreements.CannotAgree(map, StaffGrievance.Understaffed, StaffAgreements.Representative(map, StaffGrievance.Understaffed)) == null)
                    AgreementFail(check, "with only a state appointee left to speak for the guards a promise could still be made");
                foreach (Pawn p in new[] { junior, senior, third }) p.GetComp<CompStaff>().leaveAtTick = -1;

                if (!StaffAgreements.Agree(map, g, StaffGrievance.Understaffed, rep)) { AgreementFail(check, "the promise was not made"); return; }
                string name = rep.LabelShort;
                int id = rep.thingIDNumber;
                // They go (fired, quit, dead: gone from the map).
                rep.Destroy(DestroyMode.Vanish);
                Roster.Invalidate();
                if (g.agreements.active == null || g.agreements.active.repName != name || g.agreements.active.repId != id)
                    AgreementFail(check, $"the promise did not stand when {name} left (active {g.agreements.active != null}, named {g.agreements.active?.repName})");
                string line = g.agreements.active == null ? "" : StaffAgreements.ActiveLine(g.agreements.active);
                if (!line.Contains(name)) AgreementFail(check, $"the panel's line does not name {name} once they have left: {line}");
                // The cover is added (two day guards and three night guards: more than the baseline in every hour, the representative's own shift included): kept, still under their name.
                for (int i = 0; i < 5; i++) AgreementGuard(map, r, i >= 2);
                AgreementRun(map, noon, StaffAgreements.TermDays * 24);
                AgreementRecord last = AgreementLast(StaffGrievance.Understaffed);
                if (last is not { outcome: AgreementOutcome.Fulfilled }) AgreementFail(check, $"the promise was {last?.outcome.ToString() ?? "never judged"} after its representative left (want kept: more than the baseline on duty in every hour)");
                if (!r.NewEntries.Any(e => e.text.Contains(name))) AgreementFail(check, $"the chronicle's line for the outcome does not name {name}, who had left");
                RPLog.Message($"[RimPrison:test] agreement representative: {name} left, the promise stood and was {last?.outcome} (most senior {senior.LabelShort} of rank 1, the chief of rank 2 is the state's)");
            });
        }

        // ---- peace, and the bonus that is not offered -------------------------------------------------------------------

        /// <summary>A kept promise is fifteen days without a demand over it (the others unaffected); a broken one is no peace and no bonus on the next letter, until one is kept; the third answer works through the letter.</summary>
        private static void CheckAgreementPeaceAndNoBonus(Map map)
        {
            Guarded("agreement - the peace after a kept promise, no bonus after a broken one, the letter's third answer", map, r =>
            {
                const string check = "peace";
                PrisonGame g = PrisonGame.Instance;
                AgreementStage(map, r);
                // (No hazard demand in the last three days: that is a grievance of its own (Safety) which the worker reads at the time it fires, and the clock below is moved fifteen days on. A run in which a guard
                // went down earlier made the check's grievance Safety and the demand after the peace a Pay one: "no demand came once the peace was over (fired True)", twice.)
                int hazard0 = g.story.lastHazardDemandTick;
                g.story.lastHazardDemandTick = -999999;
                r.Add(() => g.story.lastHazardDemandTick = hazard0);
                var staff = new List<Pawn> { AgreementGuard(map, r, false), AgreementGuard(map, r, false), AgreementGuard(map, r, true) };
                StaffGrievance grievance = IncidentWorker_StaffStrike.Grievance(map, IncidentWorker_StaffStrike.Staff(map));
                ChoiceLetter_PrisonDecision Letter()
                {
                    ChoiceLetter_PrisonDecision l = ChoiceLetter_PrisonDecision.Make("RP_Decision_StaffStrike", "test", "test", map);
                    l.note = grievance.ToString();
                    l.money = 100;
                    return l;
                }
                PrisonDecisionWorker worker = PrisonDecisionDef.Named("RP_Decision_StaffStrike").Worker;

                // Offered all three at first; not agreeing to anything is the last answer, which is the walk-out.
                ChoiceLetter_PrisonDecision first = Letter();
                IReadOnlyList<string> options = worker.Options(first);
                if (options.Count != 3 || options[0] != "RP_StaffStrike_Pay" || options[1] != "RP_StaffStrike_Agree" || options[2] != "RP_StaffStrike_Refuse")
                    AgreementFail(check, $"the letter offers {string.Join(", ", options)} (want the bonus, agree, refuse)");
                if (worker.TimeoutOption(first) != options.Count - 1) AgreementFail(check, $"an unanswered letter takes option {worker.TimeoutOption(first)} (want the last, refuse)");
                if (worker.Disabled(first, 1) != null) AgreementFail(check, $"agreeing is disabled with nothing running: {worker.Disabled(first, 1)}");

                // The third answer, through the letter: the promise is made; a second letter cannot make another.
                worker.Choose(first, map, 1);
                if (g.agreements.active == null || g.agreements.active.grievance != grievance) AgreementFail(check, $"choosing 'agree to fix it' made {g.agreements.active?.grievance.ToString() ?? "no promise"} (want {grievance})");
                if (worker.Disabled(Letter(), 1) == null) AgreementFail(check, "a second promise was offered with one running");
                if (!StaffAgreements.Blocked(grievance)) AgreementFail(check, "a demand over the grievance is not blocked while its promise runs");
                StaffAgreements.Judge(map, g, AgreementOutcome.Fulfilled);
                g.agreements.rises.Clear(); // (One rise to a role: a pay promise made again, below, needs the first one's rise gone.)

                // Kept: fifteen days of peace over it, none over the others, and the incident says no.
                int now = Find.TickManager.TicksGame;
                if (!StaffAgreements.InPeace(grievance) || g.agreements.peaceUntil[grievance] != now + StaffAgreements.PeaceDays * GenDate.TicksPerDay)
                    AgreementFail(check, $"after a kept promise the peace runs to {(g.agreements.peaceUntil.TryGetValue(grievance, out int u) ? (u - now) / (float)GenDate.TicksPerDay : -1f):F1} days (want {StaffAgreements.PeaceDays})");
                foreach (StaffGrievance other in Enum.GetValues(typeof(StaffGrievance)))
                    if (other != grievance && StaffAgreements.Blocked(other)) AgreementFail(check, $"peace over {grievance} blocked a demand over {other}");
                IncidentDef def = DefDatabase<IncidentDef>.GetNamed("RP_StaffStrike");
                int lettersBefore = Find.LetterStack.LettersListForReading.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_StaffStrike"));
                bool fired = def.Worker.TryExecute(new IncidentParms { target = map, forced = true });
                if (fired) AgreementFail(check, "a demand came over a grievance the staff are at peace over");
                Find.TickManager.DebugSetTicksGame(now + StaffAgreements.PeaceDays * GenDate.TicksPerDay + 1);
                if (StaffAgreements.InPeace(grievance)) AgreementFail(check, "the peace did not end after fifteen days");
                int lettersAfter = Find.LetterStack.LettersListForReading.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_StaffStrike"));
                if (lettersAfter != lettersBefore) AgreementFail(check, "a letter was sent while the staff were at peace");
                bool firedAfter = def.Worker.TryExecute(new IncidentParms { target = map, forced = true });
                ChoiceLetter_PrisonDecision real = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_StaffStrike") && StaffAgreements.ParseLetterNote(l.note, out StaffGrievance lg, out _) && lg == grievance);
                if (!firedAfter || real == null)
                {
                    // (Which grievance the worker took instead, for the line.)
                    string sent = string.Join(", ", Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_StaffStrike")).Select(l => StaffAgreements.ParseLetterNote(l.note, out StaffGrievance lg, out _) ? lg.ToString() : l.note));
                    AgreementFail(check, $"no demand came once the peace was over (fired {firedAfter}; the check's grievance {grievance}, the worker's now {IncidentWorker_StaffStrike.Grievance(map, IncidentWorker_StaffStrike.Staff(map))}, demands sent: {sent})");
                }
                else if (real.pawns.Count == 0 || !staff.Contains(real.pawns[0])) AgreementFail(check, "the demand does not name a member of the check's staff as the one speaking for the rest");
                if (real != null && (!StaffAgreements.ParseLetterNote(real.note, out StaffGrievance noted, out int[] carried) || noted != grievance || (carried != null) != (grievance == StaffGrievance.Understaffed) || (carried != null && carried.Length != 24)))
                    AgreementFail(check, $"the demand's note {real.note} does not carry the grievance {grievance} (and, for understaffing and only that, the roster's baseline by the hour)");
                if (real != null) Find.LetterStack.RemoveLetter(real);

                // Broken: no peace, and no bonus in the next letter - agree again or refuse - until one is kept.
                if (!StaffAgreements.Agree(map, g, grievance, StaffAgreements.Representative(map, grievance))) { AgreementFail(check, "the second promise was not made"); return; }
                StaffAgreements.Judge(map, g, AgreementOutcome.Broken);
                g.agreements.rises.Clear();
                if (StaffAgreements.InPeace(grievance)) AgreementFail(check, "a broken promise left the peace running");
                if (!StaffAgreements.BrokenBefore(grievance)) AgreementFail(check, "a broken promise is not remembered");
                IReadOnlyList<string> noBonus = worker.Options(Letter());
                if (noBonus.Count != 2 || noBonus[0] != "RP_StaffStrike_Agree" || noBonus[1] != "RP_StaffStrike_Refuse") AgreementFail(check, $"after a broken promise the letter offers {string.Join(", ", noBonus)} (want agree or refuse)");
                ChoiceLetter_PrisonDecision again = Letter();
                if (worker.TimeoutOption(again) != 1) AgreementFail(check, "without the bonus an unanswered letter is not the second answer, refuse");
                // Another grievance's letter still has its bonus.
                foreach (StaffGrievance other in Enum.GetValues(typeof(StaffGrievance)))
                    if (other != grievance)
                    {
                        ChoiceLetter_PrisonDecision l = Letter();
                        l.note = other.ToString();
                        if (worker.Options(l).Count != 3) AgreementFail(check, $"a broken promise over {grievance} took the bonus off a demand over {other}");
                    }
                // Refused (or timed out): the walk-out, as it always was.
                CompStaff gsc = staff[0].GetComp<CompStaff>();
                r.Add(() => { foreach (Pawn p in staff) p.GetComp<CompStaff>().strikeUntilTick = -1; });
                worker.Choose(again, map, worker.TimeoutOption(again));
                if (gsc.strikeUntilTick <= Find.TickManager.TicksGame) AgreementFail(check, "refusing did not send the staff out on strike");
                // A promise kept after that puts the bonus back.
                foreach (Pawn p in staff) p.GetComp<CompStaff>().strikeUntilTick = -1;
                if (!StaffAgreements.Agree(map, g, grievance, StaffAgreements.Representative(map, grievance))) { AgreementFail(check, "the third promise was not made"); return; }
                StaffAgreements.Judge(map, g, AgreementOutcome.Fulfilled);
                if (worker.Options(Letter()).Count != 3) AgreementFail(check, "a promise kept did not bring the bonus back into the letter");
                // A letter from before agreements (no note): worked out again, and answered.
                ChoiceLetter_PrisonDecision old = ChoiceLetter_PrisonDecision.Make("RP_Decision_StaffStrike", "test", "test", map);
                if (worker.Options(old).Count is < 2 or > 3) AgreementFail(check, "a letter with no grievance noted (from an old save) offers nothing");
                RPLog.Message("[RimPrison:test] agreement peace: the third answer through the letter, fifteen days of peace after a kept promise, no bonus after a broken one until one is kept");
            });
        }

        // ---- the saveload plan -----------------------------------------------------------------------------------------------

        /// <summary>Everything agreements keep in a save as one comparable string.</summary>
        private static string AgreementDescribe(PrisonGame g) => StaffAgreements.Describe(g.agreements);

        /// <summary>The book the game had before the seed (put back after the compare: the rest of the run plays with its own).</summary>
        private static StaffAgreementBook agreementsBeforeSeed;

        /// <summary>An understaffing promise part-way (every field off its default), three records in the history, peace over two grievances, a rise that stands and the last hour measured.</summary>
        private static void SeedAgreementForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            agreementsBeforeSeed = g.agreements;
            StaffRoleDef guard = RPSecurityDefOf.RP_Guard;
            Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Understaffed);
            int now = Find.TickManager.TicksGame;
            g.agreements = new StaffAgreementBook
            {
                active = new StaffAgreement
                {
                    grievance = StaffGrievance.Understaffed, role = guard, repId = rep?.thingIDNumber ?? 12345, repName = rep?.LabelShort ?? "Seed", agreedTick = now - 2 * GenDate.TicksPerDay, deadlineTick = now + 3 * GenDate.TicksPerDay,
                    baselines = Enumerable.Range(0, 24).Select(h => 2 + h % 3).ToList(), hoursSeen = 57, compliantHours = 41, hoursNeeded = 96, hurtTick = now - 1000, paidSettlements = 2,
                },
                history = new List<AgreementRecord>
                {
                    new AgreementRecord { grievance = StaffGrievance.Understaffed, outcome = AgreementOutcome.Fulfilled, day = Ledger.CurrentDay - 30 },
                    new AgreementRecord { grievance = StaffGrievance.Safety, outcome = AgreementOutcome.Broken, day = Ledger.CurrentDay - 20 },
                    new AgreementRecord { grievance = StaffGrievance.Pay, outcome = AgreementOutcome.Fulfilled, day = Ledger.CurrentDay - 12 },
                },
                peaceUntil = new Dictionary<StaffGrievance, int> { [StaffGrievance.Understaffed] = now + 4 * GenDate.TicksPerDay, [StaffGrievance.Pay] = now + 9 * GenDate.TicksPerDay },
                rises = new List<PayRise> { new PayRise { role = guard, agreedTick = now - 12 * GenDate.TicksPerDay, keptDay = Ledger.CurrentDay - 12 } },
                lastHourStamp = now / GenDate.TicksPerHour,
            };
        }
    }
}
