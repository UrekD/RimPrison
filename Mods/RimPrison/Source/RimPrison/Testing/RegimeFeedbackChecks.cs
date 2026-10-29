using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The regime pulled (Regime/RegimeFeedback.cs, Regime/RegimeOrders.cs, Events/Demands.cs, Story/Inspector.cs): the numbers of the strip from a seeded day, the demands that name the timetable and their judgement on the schedule,
    /// each of today's orders applied and reverted at midnight, and the inspector's line on a level locked up too long. Self-contained, on throwaway inmates and, for the levels' timetables, a level nobody is in (the
    /// timetable, the orders, the demand and the strip's counts put back by the rollback). The round trip of the saved fields (today's orders, the timetable promise's slot) is in the saveload plan
    /// (<see cref="SeedRegimeForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void RgmFail(string check, string what) => problems0.Add($"regime ({check}): {what}");

        private static void CheckRegimeFeedback(Map map)
        {
            CheckRegimeStrip(map);
            CheckRegimeStripHooks(map);
            CheckRegimeTimetableDemand(map);
            CheckRegimeOrders(map);
            CheckRegimeOrderHabit(map);
            CheckRegimeOrderOutbound(map);
            CheckRegimeInspector(map);
        }

        /// <summary>A level an ordinary timetable can be set on with nobody in it (the live inmates keep their wings); null when every level has an inmate.</summary>
        private static SecurityLevelDef RegimeEmptyLevel(Map map) =>
            DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(l => l.uiOrder).FirstOrDefault(l => !l.escortOnly && !l.cellMeals && !map.mapPawns.PrisonersOfColonySpawned.Any(p => InmateUtility.IsActiveInmate(p) && p.Inmate().security == l));

        /// <summary>The level's timetable put back by the rollback.</summary>
        private static RegimeSchedule RegimeSetAside(PrisonGame g, SecurityLevelDef level, Rollback r)
        {
            RegimeSchedule sched = g.RegimeFor(level);
            List<RegimeActivityDef> slots0 = sched.slots.ToList();
            r.Add(() =>
            {
                sched.slots.Clear();
                sched.slots.AddRange(slots0);
            });
            return sched;
        }

        /// <summary>Today's orders (and the days they were given on) swapped for none, the game's put back by the rollback.</summary>
        private static void RegimeOrdersSetAside(PrisonGame g, Rollback r)
        {
            Dictionary<SecurityLevelDef, DayOrders> orders0 = g.dayOrders;
            Dictionary<SecurityLevelDef, OrderDays> days0 = g.orderDays;
            g.dayOrders = new Dictionary<SecurityLevelDef, DayOrders>();
            g.orderDays = new Dictionary<SecurityLevelDef, OrderDays>();
            r.Add(() =>
            {
                g.dayOrders = orders0;
                g.orderDays = days0;
            });
        }

        /// <summary>The prison's demand swapped for an empty one, the game's put back by the rollback.</summary>
        private static void RegimeDemandSetAside(PrisonGame g, Rollback r)
        {
            InmateDemand demand0 = g.demand;
            g.demand = new InmateDemand();
            r.Add(() => g.demand = demand0);
        }

        /// <summary>A timetable of the test's own: sleep to six, a shower, a meal, work, a meal at noon, free time, a meal at seven, an hour of free time, a lockup, sleep.</summary>
        private static void RegimeSetTimetable(RegimeSchedule sched)
        {
            for (int h = 0; h < 24; h++)
                sched.Set(h, h < 6 || h >= 22 ? RPDefOf.RP_Sleep : h == 6 ? RPDefOf.RP_ShowerTime : h == 7 || h == 12 || h == 19 ? RPRegimeDefOf.RP_Eat : h < 12 ? RPLaborDefOf.RP_Work : h == 21 ? RPDefOf.RP_Lockup : RPDefOf.RP_FreeTime);
        }

        // ---- the strip's numbers ----------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRegimeStrip(Map map)
        {
            Guarded("regime: the strip's numbers from a seeded day", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                SecurityLevelDef level = RegimeEmptyLevel(map);
                if (level == null)
                {
                    RPLog.Message("[RimPrison:test] regime strip: every ordinary level has an inmate in it, the strip's numbers are not checked");
                    return;
                }
                RegimeFeedback fb0 = pm.regime;
                pm.regime = new RegimeFeedback();
                RegimeFeedback fb = pm.regime;
                r.Add(() =>
                {
                    pm.regime = fb0;
                    RegimeFeedback.TestPlaced = null;
                });
                RegimeSchedule sched = RegimeSetAside(g, level, r);
                RegimeOrdersSetAside(g, r);
                RegimeSetTimetable(sched);
                Pawn[] inmates = { Throwaway(map, r, level), Throwaway(map, r, level), Throwaway(map, r, level), Throwaway(map, r, level) };
                // The fourth is never where the slot wants them; the third is not hungry.
                RegimeFeedback.TestPlaced = p => p != inmates[3];
                foreach (Pawn p in inmates) p.needs.food.CurLevel = 0f;
                inmates[2].needs.food.CurLevel = inmates[2].needs.food.MaxLevel;
                // 13:00 (free time): four in sample, three in place, two incidents.
                fb.NoteIncident(level, 13);
                fb.NoteIncident(level, 13);
                fb.Sample(map, pm, g, 13);
                // The meal at noon is one hour: the first of the inmates ate, the second did not, the third was not hungry, the fourth did not.
                fb.NoteMeal(inmates[0]);
                fb.Sample(map, pm, g, 12);
                // Two meals in a row (a run): the count of those who missed it is for its last hour.
                sched.Set(18, RPRegimeDefOf.RP_Eat);
                sched.Set(19, RPRegimeDefOf.RP_Eat);
                fb.Sample(map, pm, g, 18);
                fb.Sample(map, pm, g, 19);
                // The day's last hour closes the day.
                fb.Sample(map, pm, g, 23);
                if (!fb.Yesterday.TryGetValue(level, out RegimeDay day))
                {
                    RgmFail("strip", $"a day sampled with four inmates in {level.defName} has no row for it in yesterday's numbers");
                    return;
                }
                if (day.sample[13] != 4 || day.share[13] != 75 || day.incidents[13] != 2 || day.missed[13] != 0 || day.ran[13] != RPDefOf.RP_FreeTime)
                    RgmFail("strip", $"13:00 (free time, four in sample, one out of place, two incidents) reads {day.sample[13]} in sample, {day.share[13]}% in place, {day.incidents[13]} incidents, {day.missed[13]} missed, ran {day.ran[13]?.defName} (wanted 4, 75%, 2, 0, RP_FreeTime)");
                // Missed meals: the hungry who had not eaten (the second and the fourth); the one who ate and the one who was not hungry do not count.
                if (day.missed[12] != 2 || day.sample[12] != 4 || day.ran[12] != RPRegimeDefOf.RP_Eat)
                    RgmFail("strip", $"12:00 (a meal, one inmate ate, one was not hungry) reads {day.missed[12]} missed of {day.sample[12]} (wanted 2 of 4)");
                if (day.missed[18] != 0 || day.missed[19] != 2)
                    RgmFail("strip", $"a two-hour meal reads {day.missed[18]} missed in its first hour and {day.missed[19]} in its last (wanted 0 and 2: the missed meal is counted when the run is over)");
                // No numbers where nobody was sampled, and a slot with none says so.
                if (day.Has(3) || day.sample[3] != 0) RgmFail("strip", "an hour nobody was sampled in has numbers");
                // The tooltips: the numbers, and for an empty hour that the numbers are not saved.
                string tip = fb.Tip(level, 13), tipMissed = fb.Tip(level, 12), none = fb.Tip(level, 3);
                if (!tip.Contains("75%") || !tip.Contains("RP_RegimeFeedbackIncidents".Translate(2).Resolve()) || !tip.Contains(RPDefOf.RP_FreeTime.label))
                    RgmFail("strip", $"the tooltip of the 13:00 hour reads \"{tip}\" (wanted the slot, 75% and 2 incidents)");
                if (!tipMissed.Contains("RP_RegimeFeedbackMissed".Translate(2).Resolve())) RgmFail("strip", $"the tooltip of the noon meal reads \"{tipMissed}\" (wanted 2 missed the meal)");
                if (none != "RP_RegimeFeedbackNone".Translate(3, level.label).Resolve() || !none.Contains("loaded")) RgmFail("strip", $"the tooltip of an hour with no numbers reads \"{none}\" (wanted the one that says a load loses a day)");
                // The next day starts empty: yesterday's numbers are replaced, not added to.
                fb.Sample(map, pm, g, 23);
                if (fb.Yesterday.TryGetValue(level, out RegimeDay next) && next.Has(13)) RgmFail("strip", "a second day's numbers still hold the first day's 13:00 hour");
                RPLog.Message($"[RimPrison:test] regime strip: {level.defName} 13:00 reads \"{tip}\", noon \"{tipMissed}\", the 3:00 hour \"{none}\"");
            });
        }

        /// <summary>The misconduct and the meal reach the counts (the two hooks in the game's own code).</summary>
        private static void CheckRegimeStripHooks(Map map)
        {
            Guarded("regime: a misconduct and a meal reach the strip's counts", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                RegimeFeedback fb0 = pm.regime;
                pm.regime = new RegimeFeedback();
                RegimeFeedback fb = pm.regime;
                r.Add(() => pm.regime = fb0);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                SecurityLevelDef level = c.security ?? InmateUtility.DefaultSecurity;
                int hour = GenLocalDate.HourOfDay(map);
                MisconductUtility.Record(p, c, pm, DefDatabase<MisconductDef>.GetNamed("RP_Fight"), chronicle: false);
                fb.Sample(map, pm, g, hour);
                if (hour != 23) fb.Sample(map, pm, g, 23);
                if (!fb.Yesterday.TryGetValue(level, out RegimeDay day) || day.incidents[hour] != 1)
                    RgmFail("hooks", $"a misconduct recorded at {hour}:00 reads {(day == null ? "no row" : day.incidents[hour] + " incidents")} for {level.defName} (wanted 1)");
                Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                int eaten0 = g.today.mealsEaten, now = Find.TickManager.TicksGame;
                if (fb.AteSince(p, now - 1)) RgmFail("hooks", "an inmate who has eaten nothing is down as having eaten");
                MealTally.Eaten(g, p, meal);
                if (!fb.AteSince(p, now - 1) || g.today.mealsEaten != eaten0 + 1) RgmFail("hooks", $"a meal eaten is not noted ({(fb.AteSince(p, now - 1) ? "noted" : "not noted")}, the day's count {eaten0} -> {g.today.mealsEaten})");
                meal.Destroy();
            });
        }

        // ---- demands that name the timetable ---------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRegimeTimetableDemand(Map map)
        {
            Guarded("regime: a demand that names the timetable is judged on the schedule", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = RegimeEmptyLevel(map);
                if (level == null)
                {
                    RPLog.Message("[RimPrison:test] regime demand: every ordinary level has an inmate in it, the timetable demand is not checked");
                    return;
                }
                if (!DemandSystem.CandidateNeeds().Contains(RPVanillaDefOf.Outdoors) || !DemandSystem.CandidateNeeds().Contains(RPVanillaDefOf.Joy) || !DemandSystem.CandidateNeeds().Contains(NeedDefOf.Food))
                    RgmFail("demand", "the demands' candidate needs have lost the outdoors, joy or food");
                RegimeDemandSetAside(g, r);
                RegimeOrdersSetAside(g, r);
                RegimeSchedule sched = RegimeSetAside(g, level, r);
                RegimeSetTimetable(sched);
                // (Hours of free time 13-18 and 20: seven. Cut to one, at 14.)
                for (int h = 0; h < 24; h++)
                    if (sched.At(h) == RPDefOf.RP_FreeTime) sched.Set(h, RPDefOf.RP_Lockup);
                sched.Set(14, RPDefOf.RP_FreeTime);
                Pawn[] inmates = { Throwaway(map, r, level), Throwaway(map, r, level), Throwaway(map, r, level) };
                foreach (Pawn p in inmates)
                    if (p.needs.TryGetNeed(RPVanillaDefOf.Outdoors) is { } outdoors) outdoors.CurLevel = 0f;
                if (inmates[0].needs.TryGetNeed(RPVanillaDefOf.Outdoors) == null)
                {
                    RPLog.Message("[RimPrison:test] regime demand: inmates have no outdoors need in this game, the timetable demand is not checked");
                    return;
                }
                if (DemandSystem.SlotCount(g, level, DemandSystem.SlotFor(level)) != 1) RgmFail("demand", "the check's own premise: the level has one hour of free time");
                // The level asked for is the one worst off (this one has every inmate low and others cannot have more than all).
                SecurityLevelDef worst = DemandSystem.WorstLevel(map, g, RPVanillaDefOf.Outdoors);
                float worstShare = worst == null ? 0f : LevelSurvey(map, worst, RPVanillaDefOf.Outdoors).lowShare;
                if (worst == null || worstShare < 0.999f) RgmFail("demand", $"with every inmate of {level.defName} low on the outdoors the level asked for is {worst?.defName ?? "none"} (low share {worstShare:P0}, wanted a level with all of its inmates low)");
                // The letter names the slot, the level and the number, and carries them.
                g.demand.cooldownUntilTick = -1;
                DemandSystem.Raise(map, g, RPVanillaDefOf.Outdoors, level);
                var letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_InmateDemand"));
                if (letter == null)
                {
                    RgmFail("demand", "a demand about the outdoors put no letter to the warden");
                    return;
                }
                string text = letter.Text.ToString();
                if (letter.need != RPVanillaDefOf.Outdoors || letter.note != level.defName || letter.amount != 2 || !text.Contains(DemandSystem.AskedSlot(level, 1)) || !text.Contains(level.label))
                    RgmFail("demand", $"the letter for a level with one free hour carries need {letter.need?.defName}, level {letter.note}, number {letter.amount} and reads \"{text}\" (wanted the outdoors, {level.defName}, 2, naming \"{DemandSystem.AskedSlot(level, 1)}\" and \"{level.label}\")");
                letter.answered = true;
                PrisonDecisions.Accept(letter);
                Find.LetterStack.RemoveLetter(letter);
                InmateDemand d = g.demand;
                if (!d.Active || !d.Timetable || d.slotLevel != level || d.slotActivity != RPDefOf.RP_FreeTime || d.slotTarget != 2 || d.slotRan != 0)
                    RgmFail("demand", $"a promised timetable demand holds level {d.slotLevel?.defName}, slot {d.slotActivity?.defName}, target {d.slotTarget}, ran {d.slotRan} (wanted {level.defName}, RP_FreeTime, 2, 0)");
                int met0 = g.stats.demandsMet, broken0 = g.stats.demandsBroken;

                // Kept: a second hour on the timetable is judged on the schedule, though the need is at nought.
                sched.Set(15, RPDefOf.RP_FreeTime);
                RegimeDayTally(g, d);
                if (d.slotRan != 2) RgmFail("demand", $"a day of a timetable with two free hours counts {d.slotRan} hours of the slot (wanted 2)");
                d.deadlineTick = Find.TickManager.TicksGame;
                DemandSystem.Hourly(map, g);
                if (d.Active || g.stats.demandsMet != met0 + 1 || g.stats.demandsBroken != broken0)
                    RgmFail("demand", $"a promise of a second yard hour with the timetable giving two was not kept (met {met0} -> {g.stats.demandsMet}, broken {broken0} -> {g.stats.demandsBroken}, still active {d.Active}) while the need's average is nought");
                if (d.slotLevel != null || d.slotRan != 0) RgmFail("demand", "a judged promise keeps its slot");

                // Broken: the same promise with only one free hour.
                sched.Set(15, RPDefOf.RP_Lockup);
                DemandSystem.Accept(g, RPVanillaDefOf.Outdoors, null, level, 2);
                RegimeDayTally(g, g.demand);
                g.demand.deadlineTick = Find.TickManager.TicksGame;
                DemandSystem.Hourly(map, g);
                if (g.demand.Active || g.stats.demandsBroken != broken0 + 1) RgmFail("demand", $"a promise of a second hour with one on the timetable was not broken (broken {broken0} -> {g.stats.demandsBroken})");

                // Broken by an order: the timetable has the hours, the warden called the yard off for the day.
                sched.Set(15, RPDefOf.RP_FreeTime);
                DemandSystem.Accept(g, RPVanillaDefOf.Outdoors, null, level, 2);
                RegimeOrders.Give(g, map, level, DayOrder.YardOff);
                RegimeDayTally(g, g.demand);
                if (g.demand.slotRan != 0) RgmFail("demand", $"a day with the yard called off counts {g.demand.slotRan} hours of free time (wanted 0)");
                g.demand.deadlineTick = Find.TickManager.TicksGame;
                DemandSystem.Hourly(map, g);
                if (g.demand.Active || g.stats.demandsBroken != broken0 + 2) RgmFail("demand", "a promise of more yard hours was kept on a day the yard was called off");
                g.dayOrders.Clear();

                // Broken by a lockdown: the hours are on the timetable and run on paper, nobody leaves their cell.
                DemandSystem.Accept(g, RPVanillaDefOf.Outdoors, null, level, 2);
                g.wingLockdowns[level] = Find.TickManager.TicksGame + 2 * GenDate.TicksPerDay;
                r.Add(() => g.wingLockdowns.Remove(level));
                RegimeDayTally(g, g.demand);
                if (g.demand.slotRan != 0) RgmFail("demand", $"a day with {level.defName} under a wing lockdown counts {g.demand.slotRan} hours of free time (wanted 0)");
                g.wingLockdowns.Remove(level);
                int lockdown0 = g.lockdownUntilTick;
                g.lockdownUntilTick = Find.TickManager.TicksGame + 2 * GenDate.TicksPerDay;
                r.Add(() => g.lockdownUntilTick = lockdown0);
                RegimeDayTally(g, g.demand);
                if (g.demand.slotRan != 0) RgmFail("demand", $"a day under a prison-wide lockdown counts {g.demand.slotRan} hours of free time (wanted 0)");
                g.lockdownUntilTick = -1;
                RegimeDayTally(g, g.demand);
                if (g.demand.slotRan != 2) RgmFail("demand", $"the same day with the lockdown over counts {g.demand.slotRan} hours of free time (wanted 2)");
                g.demand.deadlineTick = -1;
                g.demand.need = null;

                // A letter that named no level (none qualified when it was written, or it is from an older save) is answered as the letter said: a promise on the need's average, not on a level found now.
                g.demand.cooldownUntilTick = -1;
                DemandSystem.Raise(map, g, RPVanillaDefOf.Outdoors, level);
                var bare = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_InmateDemand") && l != letter);
                if (bare == null) RgmFail("demand", "a second demand about the outdoors put no letter to the warden");
                else
                {
                    bare.note = null;
                    bare.amount = 0;
                    bare.answered = true;
                    PrisonDecisions.Accept(bare);
                    Find.LetterStack.RemoveLetter(bare);
                    if (!g.demand.Active || g.demand.Timetable || g.demand.slotLevel != null) RgmFail("demand", $"a letter that named no level made a promise that holds level {g.demand.slotLevel?.defName} (wanted none: judged on the average)");
                    g.demand.deadlineTick = -1;
                    g.demand.need = null;
                }

                // The count of the last day only: a promise answered with a second hour put in at the last, the slot is counted by the hourly pass inside its window and not before it.
                DemandSystem.Accept(g, RPVanillaDefOf.Outdoors, null, level, 2);
                g.demand.deadlineTick = Find.TickManager.TicksGame + DemandSystem.SlotWindowHours * GenDate.TicksPerHour + 5;
                DemandSystem.Hourly(map, g);
                if (g.demand.slotRan != 0) RgmFail("demand", $"an hour before the last day of the promise is counted ({g.demand.slotRan})");
                g.demand.deadlineTick = Find.TickManager.TicksGame + DemandSystem.SlotWindowHours * GenDate.TicksPerHour - 5;
                DemandSystem.Hourly(map, g);
                int hourNow = GenLocalDate.HourOfDay(map);
                if (g.demand.slotRan != (g.SlotAt(level, hourNow) == RPDefOf.RP_FreeTime ? 1 : 0)) RgmFail("demand", $"the hour in the last day of the promise is counted {g.demand.slotRan} times for a slot of {g.SlotAt(level, hourNow)?.defName}");
                g.demand.deadlineTick = -1;
                g.demand.need = null;

                // Joy asks for the timetable too; a need-based demand (hygiene) is still judged on the need's average.
                DemandSystem.Accept(g, RPVanillaDefOf.Joy, null, level, 2);
                if (!g.demand.Timetable) RgmFail("demand", "a promise about joy names no slot");
                g.demand.deadlineTick = -1;
                g.demand.need = null;
                DemandSystem.Accept(g, RPDefOf.RP_Hygiene);
                if (g.demand.Timetable || g.demand.slotLevel != null) RgmFail("demand", "a promise about hygiene names a slot");
                var hygiene = map.mapPawns.PrisonersOfColonySpawned.Select(x => x.needs.TryGetNeed(RPDefOf.RP_Hygiene)).Where(n => n != null).Select(n => (n, n.CurLevel)).ToList();
                r.Add(() =>
                {
                    foreach ((Need hn, float hv) in hygiene) hn.CurLevel = hv;
                });
                foreach ((Need hn, float _) in hygiene) hn.CurLevel = hn.MaxLevel;
                int met1 = g.stats.demandsMet;
                g.demand.deadlineTick = Find.TickManager.TicksGame;
                DemandSystem.Hourly(map, g);
                if (g.demand.Active || g.stats.demandsMet != met1 + 1) RgmFail("demand", "a need-based promise (hygiene) with the need at its best was not kept");
                RPLog.Message($"[RimPrison:test] regime demand: \"{DemandSystem.AskedSlot(level, 1)}\" for {level.defName}, kept on a schedule of two hours, broken on one and on a day with the yard called off");
            });
        }

        /// <summary>The hour pass's count for the whole day (one call for each hour of it).</summary>
        private static void RegimeDayTally(PrisonGame g, InmateDemand d)
        {
            for (int h = 0; h < 24; h++) DemandSystem.TallySlotHour(g, d, h);
        }

        private static (float avg, float lowShare, int n) LevelSurvey(Map map, SecurityLevelDef level, NeedDef need)
        {
            List<Pawn> here = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate().security == level && p.needs?.TryGetNeed(need) != null).ToList();
            return here.Count == 0 ? (1f, 0f, 0) : (here.Average(p => p.needs.TryGetNeed(need).CurLevelPercentage), here.Count(p => p.needs.TryGetNeed(need).CurLevelPercentage < DemandSystem.LowLevel) / (float)here.Count, here.Count);
        }

        // ---- today's orders ------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRegimeOrders(Map map)
        {
            Guarded("regime: each of today's orders applies and reverts at midnight", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = RegimeEmptyLevel(map) ?? InmateUtility.DefaultSecurity;
                RegimeOrdersSetAside(g, r);
                RegimeSchedule sched = RegimeSetAside(g, level, r);
                RegimeSetTimetable(sched);
                int hour = GenLocalDate.HourOfDay(map);
                RegimeActivityDef Base(int h) => sched.At(h);

                // The pure part: what an order makes of a slot.
                var yard = new DayOrders { yardOff = true };
                if (RegimeOrders.Apply(RPDefOf.RP_FreeTime, 14, yard) != RPDefOf.RP_Lockup || RegimeOrders.Apply(RPSupermaxDefOf.RP_YardAlone, 14, yard) != RPDefOf.RP_Lockup || RegimeOrders.Apply(RPRegimeDefOf.RP_Eat, 12, yard) != RPRegimeDefOf.RP_Eat)
                    RgmFail("orders", "calling the yard off does not make free time and the yard hour alone a lockup and leave a meal be");
                var early = new DayOrders { lockupFrom = 17 };
                if (RegimeOrders.Apply(RPDefOf.RP_FreeTime, 18, early) != RPDefOf.RP_Lockup || RegimeOrders.Apply(RPDefOf.RP_FreeTime, 16, early) != RPDefOf.RP_FreeTime
                    || RegimeOrders.Apply(RPLaborDefOf.RP_Work, 17, early) != RPDefOf.RP_Lockup || RegimeOrders.Apply(RPRegimeDefOf.RP_Eat, 19, early) != RPRegimeDefOf.RP_Eat
                    || RegimeOrders.Apply(RPDefOf.RP_Sleep, 22, early) != RPDefOf.RP_Sleep || RegimeOrders.Apply(RPDefOf.RP_ShowerTime, 20, early) != RPDefOf.RP_ShowerTime)
                    RgmFail("orders", "an early lockup from 17:00 does not lock up free time and work from then and leave the hours before it, the meals, the showers and sleep be");
                var shower = new DayOrders { showerHour = 15 };
                if (RegimeOrders.Apply(RPDefOf.RP_FreeTime, 15, shower) != RPDefOf.RP_ShowerTime || RegimeOrders.Apply(RPDefOf.RP_FreeTime, 16, shower) != RPDefOf.RP_FreeTime)
                    RgmFail("orders", "a second shower at 15:00 does not make that hour, and only that hour, a shower");
                if (RegimeOrders.ShowerHourFor(g, level, 20) != 21 || RegimeOrders.ShowerHourFor(g, level, 21) != -1 || RegimeOrders.ShowerHourFor(g, level, 6) != 8)
                    RgmFail("orders", $"the second shower's hour is {RegimeOrders.ShowerHourFor(g, level, 20)} after 20:00 (wanted 21), {RegimeOrders.ShowerHourFor(g, level, 21)} after 21:00 (wanted none: sleep follows) and {RegimeOrders.ShowerHourFor(g, level, 6)} after 6:00 (wanted 8: a meal at 7)");

                // Each order given: the slots of the level change as the order says, the chronicle says so, and the timetable itself is untouched.
                var inmate = Throwaway(map, r, level);
                CompInmate c = inmate.Inmate();
                List<RegimeActivityDef> before = sched.slots.ToList();
                int entries = r.NewEntries.Count();
                if (!RegimeOrders.Give(g, map, level, DayOrder.YardOff)) RgmFail("orders", "the yard could not be called off");
                for (int h = 0; h < 24; h++)
                    if (g.SlotAt(level, h) != (Base(h) == RPDefOf.RP_FreeTime ? RPDefOf.RP_Lockup : Base(h))) RgmFail("orders", $"with the yard called off the slot at {h}:00 is {g.SlotAt(level, h)?.defName} for a timetable of {Base(h).defName}");
                if (g.ActivityAt(c, 15) != RPDefOf.RP_Lockup || g.ConfinedAlone(c, 15)) RgmFail("orders", $"an inmate of the level has {g.ActivityAt(c, 15)?.defName} at 15:00 with the yard called off (wanted lockup, the whole level's)");
                if (r.NewEntries.Count() != entries + 1 || !r.NewEntries.Last().text.Contains(level.label)) RgmFail("orders", "the yard called off is not in the chronicle, with the level's name");
                if (!sched.slots.SequenceEqual(before)) RgmFail("orders", "an order changed the timetable itself");
                if (!RegimeOrders.Give(g, map, level, DayOrder.EarlyLockup)) RgmFail("orders", "the level could not be locked up early");
                int from = System.Math.Max(RegimeOrders.EarlyLockupHour, hour);
                if (g.dayOrders[level].lockupFrom != from) RgmFail("orders", $"an early lockup at {hour}:00 starts at {g.dayOrders[level].lockupFrom}:00 (wanted {from}:00)");
                for (int h = 0; h < 24; h++)
                {
                    RegimeActivityDef want = Base(h) == RPDefOf.RP_FreeTime ? RPDefOf.RP_Lockup : Base(h);
                    if (h >= from && want != RPDefOf.RP_Sleep && want != RPRegimeDefOf.RP_Eat && want != RPDefOf.RP_ShowerTime) want = RPDefOf.RP_Lockup;
                    if (g.SlotAt(level, h) != want) RgmFail("orders", $"with the yard off and an early lockup from {from}:00 the slot at {h}:00 is {g.SlotAt(level, h)?.defName} (wanted {want.defName})");
                }
                int shower2 = RegimeOrders.ShowerHourFor(g, level, hour);
                if (shower2 < 0)
                {
                    if (RegimeOrders.Give(g, map, level, DayOrder.DoubleShower) || RegimeOrders.Unavailable(g, map, level, DayOrder.DoubleShower) == null) RgmFail("orders", $"a second shower is given at {hour}:00 with no hour of the day left for it");
                }
                else
                {
                    if (!RegimeOrders.Give(g, map, level, DayOrder.DoubleShower) || g.dayOrders[level].showerHour != shower2 || g.SlotAt(level, shower2) != RPDefOf.RP_ShowerTime)
                        RgmFail("orders", $"a second shower at {hour}:00 is not at {shower2}:00 (order {g.dayOrders[level].showerHour}, slot {g.SlotAt(level, shower2)?.defName})");
                }
                if (r.NewEntries.Count() < entries + (shower2 < 0 ? 2 : 3)) RgmFail("orders", "an order given is not written to the chronicle");

                // Withdrawn: the order goes, the slots come back, with a line of its own.
                int lines = r.NewEntries.Count();
                RegimeOrders.Toggle(g, map, level, DayOrder.YardOff);
                if (g.dayOrders[level].yardOff || g.SlotAt(level, 14) != Base(14)) RgmFail("orders", "withdrawing the yard order left it in force");
                if (r.NewEntries.Count() != lines + 1) RgmFail("orders", "withdrawing an order is not written to the chronicle");

                // Midnight: an order stamped with yesterday goes at the next pass, with its line, and the timetable is back as it was.
                RegimeOrders.Give(g, map, level, DayOrder.YardOff);
                foreach (DayOrders o in g.dayOrders.Values) o.day = Ledger.CurrentDay - 1;
                int atMidnight = r.NewEntries.Count();
                int queued = PrisonMap.Of(map).PendingInterrupts;
                RegimeOrders.Sweep(g, map);
                if (PrisonMap.Of(map).PendingInterrupts != queued) RgmFail("orders", "the midnight pass queues inmates to be sent to a new job itself (the hour pass does, by what each one's slot was and is)");
                if (g.dayOrders.Count != 0 || RegimeOrders.Of(g, level) != null) RgmFail("orders", "the orders of yesterday are still in force after the midnight pass");
                for (int h = 0; h < 24; h++)
                    if (g.SlotAt(level, h) != Base(h)) RgmFail("orders", $"after midnight the slot at {h}:00 is {g.SlotAt(level, h)?.defName}, the timetable has {Base(h).defName}");
                if (r.NewEntries.Count() != atMidnight + 1 || !r.NewEntries.Last().text.Contains(level.label)) RgmFail("orders", "the orders that lapse at midnight are not in the chronicle, with the level's name");
                // And an order of today is not touched by the pass.
                RegimeOrders.Give(g, map, level, DayOrder.EarlyLockup);
                RegimeOrders.Sweep(g, map);
                if (RegimeOrders.Of(g, level) == null) RgmFail("orders", "the midnight pass took an order given today");
                // The count's hour is not given to a second shower.
                bool countOn0 = CountSystem.TestOn;
                int perDay0 = g.countsPerDay;
                r.Add(() =>
                {
                    CountSystem.TestOn = countOn0;
                    g.countsPerDay = perDay0;
                });
                (CountSystem.TestOn, g.countsPerDay) = (true, 1);
                g.dayOrders.Clear();
                int countHour = CountSystem.LockupHour(g);
                if (Enumerable.Range(0, 24).Any(h => RegimeOrders.ShowerHourFor(g, level, h) == countHour)) RgmFail("orders", $"a second shower is given the hour the count is called at ({countHour}:00)");
                RPLog.Message($"[RimPrison:test] regime orders: {level.defName} from {hour}:00 - the yard off, locked up early from {from}:00, a second shower at {shower2}:00, all gone at the pass after midnight");
            });
        }

        // ---- orders given every day ------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRegimeOrderHabit(Map map)
        {
            Guarded("regime: an order given on more than three of the last seven days is a finding", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                RegimeOrdersSetAside(g, r);
                SecurityLevelDef level = RegimeEmptyLevel(map) ?? InmateUtility.DefaultSecurity;
                RegimeSchedule sched = RegimeSetAside(g, level, r);
                RegimeSetTimetable(sched);
                Throwaway(map, r, level);
                int today = Ledger.CurrentDay;
                string key = InspectionReport.OrdersPrefix + level.defName;
                void Days(params int[] ago) => g.orderDays[level] = new OrderDays { days = ago.Select(a => today - a).ToList() };

                // Given today: noted once, however many orders; a second shower is not one that keeps them in.
                if (!RegimeOrders.Give(g, map, level, DayOrder.YardOff) || !RegimeOrders.Give(g, map, level, DayOrder.EarlyLockup) || RegimeOrders.DaysOrdered(g, level) != 1)
                    RgmFail("habit", $"two orders given today are {RegimeOrders.DaysOrdered(g, level)} day(s) of orders (wanted 1)");
                g.orderDays.Clear();
                g.dayOrders.Clear();
                if (RegimeOrders.Give(g, map, level, DayOrder.DoubleShower) && RegimeOrders.DaysOrdered(g, level) != 0) RgmFail("habit", "a second shower is counted as an order that keeps the level in");
                g.dayOrders.Clear();

                // The window: today and the six days before it; three days is the limit, four is a habit.
                Days(0, 1, 2);
                if (RegimeOrders.Habit(g, level) || InspectorCandidate(g, map, key) != null) RgmFail("habit", "orders on three of the last seven days are written down");
                Days(0, 1, 2, 7);
                if (RegimeOrders.DaysOrdered(g, level) != 3) RgmFail("habit", $"a day a week ago is counted in the last seven ({RegimeOrders.DaysOrdered(g, level)} days, wanted 3)");
                Days(0, 1, 2, 6);
                InspectionFinding f = InspectorCandidate(g, map, key);
                if (!RegimeOrders.Habit(g, level) || f == null || f.id != 4) RgmFail("habit", $"orders on four of the last seven days are {(f == null ? "not written down" : "written with " + f.id + " days")}");
                else
                {
                    string words = InspectionReport.Describe(f, map);
                    if (!words.Contains(level.label) || !words.Contains("4")) RgmFail("habit", $"the finding reads \"{words}\" (wanted the level and its 4 days)");
                    if (InspectionReport.TabOf(key) != MainTabWindow_Prison.Tab.Regime) RgmFail("habit", "the finding's to-do line does not open the Regime tab");
                    if (!InspectionReport.Present(f, g, map, null)) RgmFail("habit", "a level ordered in on four days is not present");
                    Days(0, 1, 2);
                    if (InspectionReport.Present(f, g, map, null)) RgmFail("habit", "a level whose orders fell to three days is still present");
                    Days(0, 1, 2, 6);
                }
                if (!RegimeOrders.HabitLevels(g, map).Any(h => h.level == level && h.days == 4)) RgmFail("habit", "the weekly report's list of levels ordered in does not have the level");
                // Today's orders are not counted by the hours of the timetable (the finding for hours locked up reads the timetable alone).
                for (int h = 0; h < 24; h++) sched.Set(h, h < InspectionReport.MaxLockedHours ? RPDefOf.RP_Lockup : RPLaborDefOf.RP_Work);
                RegimeOrders.Give(g, map, level, DayOrder.EarlyLockup);
                if (InspectorCandidate(g, map, InspectionReport.LockedPrefix + level.defName) != null) RgmFail("habit", "an early lockup given today is counted in the timetable's hours locked up");
                RPLog.Message($"[RimPrison:test] regime orders: four days of orders in seven for {level.defName} is a finding, three is not");
            });
        }

        private static void CheckRegimeOrderOutbound(Map map)
        {
            Guarded("regime: an order given leaves an inmate on the way out waiting, and sends the others by the queue", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                RegimeOrdersSetAside(g, r);
                SecurityLevelDef level = RegimeEmptyLevel(map) ?? InmateUtility.DefaultSecurity;
                RegimeSchedule sched = RegimeSetAside(g, level, r);
                // (Free time all day: calling the yard off changes the slot of every inmate of the level, at any hour the check runs.)
                for (int h = 0; h < 24; h++) sched.Set(h, RPDefOf.RP_FreeTime);
                Pawn leaving = Throwaway(map, r, level), staying = Throwaway(map, r, level);
                var entry = new OutboundEntry { pawn = leaving, kind = OutboundKind.Transferred, sinceTick = Find.TickManager.TicksGame };
                g.shuttle.outbound.Add(entry);
                r.Add(() => g.shuttle.outbound.Remove(entry));
                Job Wait(Pawn p)
                {
                    p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 6000), JobCondition.InterruptForced);
                    return p.CurJob;
                }
                Job leavingJob = Wait(leaving), stayingJob = Wait(staying);
                int queued = pm.PendingInterrupts;
                // Given and withdrawn over and over in one tick (a warden's clicks; the vanilla guard on a pawn that starts ten jobs in a tick is an error in the log).
                for (int i = 0; i < 6; i++)
                {
                    if (!RegimeOrders.Give(g, map, level, DayOrder.YardOff)) RgmFail("outbound", "the yard could not be called off");
                    if (i == 0)
                    {
                        if (stayingJob != staying.CurJob || leavingJob != leaving.CurJob) RgmFail("outbound", "an order given ended a job at once (the inmates are sent through the queue)");
                        if (pm.PendingInterrupts <= queued) RgmFail("outbound", "an order that locks the level up queued nobody to be sent to a new job");
                    }
                    RegimeOrders.Lift(g, level, DayOrder.YardOff);
                }
                if (leavingJob != leaving.CurJob || leaving.CurJob?.def != JobDefOf.Wait) RgmFail("outbound", $"an inmate waiting for the shuttle was interrupted by an order (now {leaving.CurJob?.def?.defName ?? "no job"})");
                if (pm.PendingInterrupts - queued > map.mapPawns.PrisonersOfColonySpawned.Count) RgmFail("outbound", $"{pm.PendingInterrupts - queued} interrupts queued by six orders and lifts in a tick (an inmate is queued once)");
                RPLog.Message("[RimPrison:test] regime orders: an inmate on the way out is left waiting, the rest are queued once");
            });
        }

        // ---- the inspector ---------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRegimeInspector(Map map)
        {
            Guarded("regime: the inspector writes down a level that is locked up too long", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                RegimeSchedule sched = RegimeSetAside(g, level, r);
                Throwaway(map, r, level);
                string key = InspectionReport.LockedPrefix + level.defName;
                void Locked(int hours)
                {
                    for (int h = 0; h < 24; h++) sched.Set(h, h < hours ? RPDefOf.RP_Lockup : RPLaborDefOf.RP_Work);
                }
                Locked(21);
                InspectionFinding f = InspectorCandidate(g, map, key);
                if (f == null || f.id != 21) RgmFail("inspector", $"a timetable that locks {level.defName} up 21 hours of the day is {(f == null ? "not written down" : "written with " + f.id + " hours")}");
                else
                {
                    if (!InspectionReport.Present(f, g, map, null)) RgmFail("inspector", "a level locked up 21 hours is not present");
                    string words = InspectionReport.Describe(f, map);
                    if (!words.Contains(level.label) || !words.Contains("21")) RgmFail("inspector", $"the finding reads \"{words}\" (wanted the level and its 21 hours)");
                    if (InspectionReport.TabOf(key) != MainTabWindow_Prison.Tab.Regime) RgmFail("inspector", "the finding's to-do line does not open the Regime tab");
                }
                Locked(InspectionReport.MaxLockedHours);
                if (InspectorCandidate(g, map, key) != null) RgmFail("inspector", $"{InspectionReport.MaxLockedHours} hours locked up (the norm) is written down");
                Locked(21);
                if (f != null)
                {
                    Locked(15);
                    if (InspectionReport.Present(f, g, map, null)) RgmFail("inspector", "a timetable put right is still present");
                }
                // A level kept in its cells by design, or with nobody in it, is not written down however long its day is locked.
                SecurityLevelDef empty = RegimeEmptyLevel(map);
                if (empty != null)
                {
                    RegimeSchedule other = RegimeSetAside(g, empty, r);
                    for (int h = 0; h < 24; h++) other.Set(h, RPDefOf.RP_Lockup);
                    if (InspectorCandidate(g, map, InspectionReport.LockedPrefix + empty.defName) != null) RgmFail("inspector", "a level with nobody in it is written down");
                }
                else RPLog.Message("[RimPrison:test] regime inspector: every ordinary level has an inmate in it, a level with nobody in it is not checked");
                foreach (SecurityLevelDef design in DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(l => l.escortOnly || l.cellMeals))
                    if (!InspectionReport.LockedByDesign(design)) RgmFail("inspector", $"{design.defName} is kept in its cells by design and the inspector does not know it");
                // The finding is in the book of the visit: written with the others, one to a level.
                Locked(22);
                if (InspectionReport.Read(g, map).Count(x => x.key == key) != 1) RgmFail("inspector", "a visit does not write the locked-up level down once");
            });
        }

        // ---- the saveload plan -------------------------------------------------------------------------------------------------------------------------------------------------

        private static string RegimeDescribe(PrisonGame g) =>
            $"orders=[{string.Join(",", g.dayOrders.OrderBy(kv => kv.Key.defName).Select(kv => $"{kv.Key.defName}={kv.Value.Describe()}"))}] slot={g.demand.slotLevel?.defName}:{g.demand.slotActivity?.defName}:{g.demand.slotTarget}:{g.demand.slotRan} days=[{string.Join(",", g.orderDays.OrderBy(kv => kv.Key.defName).Select(kv => $"{kv.Key.defName}={string.Join("/", kv.Value.days)}"))}]";

        private static Dictionary<SecurityLevelDef, DayOrders> regimeOrders0;
        private static Dictionary<SecurityLevelDef, OrderDays> regimeOrderDays0;
        private static InmateDemand regimeDemand0;
        private static readonly List<SecurityLevelDef> regimeSeeded = new List<SecurityLevelDef>();

        /// <summary>Saveload plan, before the snapshot: today's orders on two levels (every kind, off the defaults, one of them a second shower at an hour that is not a round one of the timetable, one a restriction carried to a later day, with the tick its day's hold counts from) and a promise of a timetable demand with a slot, a target and a count of hours.</summary>
        private static void SeedRegimeForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            regimeOrders0 = g.dayOrders;
            regimeOrderDays0 = g.orderDays;
            regimeDemand0 = g.demand;
            int day = Ledger.CurrentDay;
            List<SecurityLevelDef> levels = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(s => s.uiOrder).Take(2).ToList();
            g.dayOrders = new Dictionary<SecurityLevelDef, DayOrders>
            {
                [levels[0]] = new DayOrders { day = day, yardOff = true, lockupFrom = 19, showerHour = 21, untilDay = day + 2, creditFromTick = Find.TickManager.TicksGame - 1234 },
                [levels[1]] = new DayOrders { day = day, showerHour = 15 },
            };
            g.orderDays = new Dictionary<SecurityLevelDef, OrderDays> { [levels[0]] = new OrderDays { days = new List<int> { day - 5, day - 1, day } } };
            regimeSeeded.AddRange(levels);
            g.demand = new InmateDemand
            {
                need = RPVanillaDefOf.Outdoors,
                deadlineTick = Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay,
                slotLevel = levels[1],
                slotActivity = RPDefOf.RP_FreeTime,
                slotTarget = 3,
                slotRan = 5,
            };
            RPLog.Message($"[RimPrison:test] saveload regime: {RegimeDescribe(g)}");
        }

        /// <summary>After the reload: the orders and the promise's slot came back whole (the snapshot compared them) and still act; then the game's own are put back.</summary>
        private static void CheckRegimeAfterReload()
        {
            PrisonGame g = PrisonGame.Instance;
            if (regimeOrders0 == null) return;
            SecurityLevelDef a = regimeSeeded[0], b = regimeSeeded[1];
            if (g.dayOrders.Count != 2 || RegimeOrders.Of(g, a) is not { yardOff: true, lockupFrom: 19, showerHour: 21 } oa || oa.untilDay != Ledger.CurrentDay + 2 || oa.creditFromTick < 0 || RegimeOrders.Of(g, b) is not { showerHour: 15, untilDay: -1, creditFromTick: -1 })
                reloadProblems.Add($"saveload: today's orders did not come back from the save ({RegimeDescribe(g)})");
            else if (g.SlotAt(a, 21) != RPDefOf.RP_ShowerTime || g.SlotAt(b, 15) != RPDefOf.RP_ShowerTime)
                reloadProblems.Add($"saveload: an order that came back from the save does not act (the slots at 21:00 and 15:00 are {g.SlotAt(a, 21)?.defName} and {g.SlotAt(b, 15)?.defName})");
            else if (!g.orderDays.TryGetValue(a, out OrderDays od) || od.days.Count != 3 || od.days[0] != Ledger.CurrentDay - 5 || od.days[2] != Ledger.CurrentDay)
                reloadProblems.Add($"saveload: the days the orders were given on did not come back from the save ({RegimeDescribe(g)})");
            else if (!g.demand.Timetable || g.demand.slotLevel != b || g.demand.slotTarget != 3 || g.demand.slotRan != 5)
                reloadProblems.Add($"saveload: the timetable promise did not come back from the save ({RegimeDescribe(g)})");
            else RPLog.Message("[RimPrison:test] saveload regime: today's orders and the timetable promise's slot are intact");
            g.dayOrders = regimeOrders0;
            g.orderDays = regimeOrderDays0;
            g.demand = regimeDemand0;
            regimeOrders0 = null;
            regimeOrderDays0 = null;
            regimeDemand0 = null;
            regimeSeeded.Clear();
        }
    }
}
