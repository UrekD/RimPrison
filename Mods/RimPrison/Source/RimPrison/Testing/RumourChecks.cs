using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The rumour mill (<see cref="Rumours"/>, gap analysis 7, item 5). The headless proof: the yard called off for a throwaway wing (the regime order's hook starts a true rumour, the start forced) and a false "supermax"
    /// rumour seeded on another; two hourly passes over the scene (the patch count and the spread): every inmate of the second wing carries the cause and none of the first carries the false one; an informant's tip names the
    /// origin; a calm address in the second wing's canteen ends the false one at once; on the first wing the true one survives a calm address (halved), a firm one makes it stick a day longer; the walk names its origin; the
    /// riot's line. Then one live rumour per wing (a worse thing replaces a lesser one, the one it is about never starts it, a guard's kill in the same words true or not), the life of two days (the clock moved forward and back,
    /// never before 0), the texts; the riot reckoning's report (the rumour kept as it stood); a restriction's one rumour, an order lifted or lapsed, given again; an address heard by one of the wing and by two, firm twice; a
    /// shakedown's one roll a wing; the levels whose inmates never meet; two wings in one patch, a wing with nobody left. The saveload plan's seed is at the end. Every other check runs with the dice quiet (see Rollback).
    /// </summary>
    public static partial class AutoTest
    {
        private static void RumourFail(string check, string what) => problems0.Add($"rumours ({check}): {what}");

        private static void CheckRumours(Map map)
        {
            CheckRumourProof(map);
            CheckRumourOnePerWing(map);
            CheckRumourLife(map);
            CheckRumourWords(map);
            CheckRumourRiotReport(map);
            CheckRumourOrders(map);
            CheckRumourAddressShare(map);
            CheckRumourShakedown(map);
            CheckRumourShutLevels(map);
            CheckRumourMeetings(map);
        }

        /// <summary>The map's rumours set aside for the check (a live one of the real prison would answer the scene's address or tip), the forced dice put back after; both by the rollback.</summary>
        private static PrisonMap RumourIsolate(Map map, Rollback r)
        {
            PrisonMap pm = PrisonMap.Of(map);
            List<Rumour> was = pm.rumours;
            pm.rumours = new List<Rumour>();
            r.Add(() =>
            {
                pm.rumours = was;
                (Rumours.ForceStart, Rumours.ForceDistort, Rumours.ForceTip) = (null, null, null);
            });
            return pm;
        }

        /// <summary>Levels a rumour can be told on (not supermax's walks, not the padded cells or a referral's ward), the ones with the fewest real inmates first: a throwaway wing where there is one.</summary>
        private static List<SecurityLevelDef> RumourLevels(Map map)
        {
            var real = new Dictionary<SecurityLevelDef, int>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate()?.security is { } l) real[l] = real.TryGetValue(l, out int n) ? n + 1 : 1;
            return DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(l => !l.escortOnly && !l.paddedOnly && !l.referralOnly)
                .OrderBy(l => real.TryGetValue(l, out int n) ? n : 0).ThenBy(l => l.uiOrder).ToList();
        }

        private static int RealOn(Map map, SecurityLevelDef level, ICollection<Pawn> scene) =>
            map.mapPawns.PrisonersOfColonySpawned.Count(p => !scene.Contains(p) && InmateUtility.IsActiveInmate(p) && p.Inmate()?.security == level);

        /// <summary>A made-up inmate of the scene on <paramref name="level"/>, clean of anything a tip could name (gang, tunnel, contraband, reputations).</summary>
        private static Pawn RumourInmate(WardenScene s, SecurityLevelDef level, IntVec3 at, List<Pawn> cast)
        {
            Pawn p = TaskClean(s.Inmate(at, 40f));
            CompInmate c = p.Inmate();
            c.security = level;
            c.activeFlag = true;
            cast.Add(p);
            return p;
        }

        /// <summary>
        /// The level's orders of today and its habit days, as they were, put back by the rollback (and none in force for the check). The orders object itself is kept aside, every field of it (a restriction's last day and the
        /// tick its day is credited from too): the check's orders are new ones, so nothing touches it meanwhile.
        /// </summary>
        private static void RumourKeepOrders(Map map, Rollback r, SecurityLevelDef level)
        {
            PrisonGame g = PrisonGame.Instance;
            DayOrders o0 = g.dayOrders.TryGetValue(level, out DayOrders o) ? o : null;
            List<int> days0 = g.orderDays.TryGetValue(level, out OrderDays d) ? d.days.ToList() : null;
            g.dayOrders.Remove(level);
            r.Add(() =>
            {
                if (o0 == null) g.dayOrders.Remove(level);
                else g.dayOrders[level] = o0;
                if (days0 == null) g.orderDays.Remove(level);
                else g.orderDays[level] = new OrderDays { days = days0 };
                PrisonMap.Of(map)?.Notify_DoorsChanged();
            });
        }

        private static void RumourPasses(Map map, PrisonMap pm, int hours)
        {
            for (int h = 0; h < hours; h++)
            {
                GangTurf.Sample(map, pm, turf: false);
                Rumours.Hourly(map, pm);
            }
        }

        // ---- the headless proof ---------------------------------------------------------------------------------------------------------------------

        private static void CheckRumourProof(Map map)
        {
            const string check = "the proof";
            Guarded("rumours: the headless proof", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                PrisonGame g = PrisonGame.Instance;
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2)
                {
                    RumourFail(check, $"premise: fewer than two levels a rumour can be told on ({levels.Count})");
                    return;
                }
                (SecurityLevelDef yardWing, SecurityLevelDef falseWing) = (levels[0], levels[1]);
                Pawn w = s.Warden();
                // (One canteen: the biggest, with seven free cells the warden can reach.)
                List<IntVec3> block = WardenSystem.Canteens(map).OrderByDescending(b => b.Count).FirstOrDefault();
                List<IntVec3> cells = block?.Where(c => c.Standable(map) && c.GetFirstPawn(map) == null && w.CanReach(c, Verse.AI.PathEndMode.OnCell, Danger.Deadly)).ToList() ?? new List<IntVec3>();
                if (cells.Count < 8)
                {
                    RPLog.Message($"[RimPrison:test] rumours: the proof skipped - no canteen with eight free cells the warden can reach ({cells.Count})");
                    return;
                }
                // (The cells of the canteen's biggest patch first: those who stand in one patch are the ones who meet.)
                cells = cells.GroupBy(c => pm.zones.PatchAt(c)).OrderByDescending(grp => grp.Key >= 0).ThenByDescending(grp => grp.Count()).SelectMany(grp => grp).ToList();
                IntVec3 outside = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> yard = Enumerable.Range(0, 3).Select(i => RumourInmate(s, yardWing, outside, cast)).ToList();
                List<Pawn> told = Enumerable.Range(0, 3).Select(i => RumourInmate(s, falseWing, cells[i], cast)).ToList();
                int realYard = RealOn(map, yardWing, cast), realFalse = RealOn(map, falseWing, cast);

                // The true one: the yard called off for the first wing (the regime order's own hook, the start forced, the version the thing as it was).
                RumourKeepOrders(map, r, yardWing);
                (Rumours.ForceStart, Rumours.ForceDistort) = (true, false);
                bool given = RegimeOrders.Give(g, map, yardWing, DayOrder.YardOff);
                Rumour ra = Rumours.LiveOn(pm, yardWing);
                if (!given || ra == null || ra.kind != RumourKind.YardOff || !ra.isTrue || !cast.Any(p => p.thingIDNumber == ra.originId) && realYard == 0)
                {
                    RumourFail(check, $"the yard called off for {yardWing.defName} started {(ra == null ? "no rumour" : ra.Describe())} (order given {given}; a true yard rumour started by one of the wing expected)");
                    return;
                }
                // The false one: seeded on the second wing, started by one of the scene.
                (Rumours.ForceStart, Rumours.ForceDistort) = (false, null);
                Rumour rb = Rumours.Start(pm, falseWing, RumourKind.Supermax, isTrue: false, subject: null, origin: told[0]);
                if (rb == null || Rumours.LiveOn(pm, falseWing) != rb)
                {
                    RumourFail(check, "the false supermax rumour could not be seeded on the second wing");
                    return;
                }

                // Two hourly passes: the second wing meets in the canteen, the first stands at the intake (no shared place).
                int canteenPatch = pm.zones.PatchAt(cells[0]);
                bool canteenShared = canteenPatch >= 0 && pm.zones.Patches[canteenPatch].zone.gangTurf && cells.Take(3).All(c => pm.zones.PatchAt(c) == canteenPatch);
                bool outsideShared = pm.zones.PatchAt(outside) is int po && po >= 0 && pm.zones.Patches[po].zone.gangTurf;
                RumourPasses(map, pm, 2);
                if (canteenShared && rb.reach <= Rumours.StartReach + 0.01f) RumourFail(check, $"the second wing met in the canteen for two hours and the rumour reached no further ({rb.reach:F3}, started at {Rumours.StartReach})");
                if (!outsideShared && realYard == 0 && Mathf.Abs(ra.reach - Rumours.StartReach) > 0.001f) RumourFail(check, $"the first wing met nowhere and its rumour spread all the same ({ra.reach:F3})");
                RPLog.Message($"[RimPrison:test] rumours: after two passes the false one reaches {rb.reach:F3} (canteen shared {canteenShared}, real on the wing {realFalse}), the true one {ra.reach:F3} (intake shared {outsideShared}, real {realYard})");

                // Every inmate of the second wing carries the cause, scaled by the strength; none of the first wing carries the false one.
                int now = Find.TickManager.TicksGame;
                foreach (Pawn p in told)
                {
                    CompInmate c = p.Inmate();
                    TensionUtility.HourlyUpdate(p, c, pm, 0f);
                    float got = TensionUtility.LastContribution("RP_Cause_Rumour"), want = Rumours.Strength(rb, now) * Rumours.DistortedTensionPerHour;
                    if (Rumours.LiveFor(c) != rb || got <= 0f || Mathf.Abs(got - want) > 0.01f) RumourFail(check, $"{p.LabelShort} of the second wing carries the rumour cause {got:F3} (expected {want:F3}, the rumour theirs {Rumours.LiveFor(c) == rb})");
                    c.mainCause = "RP_Cause_Rumour";
                    string shown = TensionUtility.CauseText(c).ToString();
                    if (!shown.Contains(Rumours.Text(rb))) RumourFail(check, $"the cause as shown is \"{shown}\", not naming \"{Rumours.Text(rb)}\"");
                }
                foreach (Pawn p in yard)
                {
                    CompInmate c = p.Inmate();
                    TensionUtility.HourlyUpdate(p, c, pm, 0f);
                    float got = TensionUtility.LastContribution("RP_Cause_Rumour"), want = Rumours.Strength(ra, now) * Rumours.TensionPerHour;
                    if (Rumours.LiveFor(c) == rb || Rumours.LiveFor(c) != ra || Mathf.Abs(got - want) > 0.01f) RumourFail(check, $"{p.LabelShort} of the first wing carries {Rumours.LiveFor(c)?.kind.ToString() ?? "no rumour"} at {got:F3} (expected the true yard rumour at {want:F3}, never the false one)");
                }
                if (Rumours.FileLine(told[1], told[1].Inmate(), now) is not { } fileLine || !fileLine.Item1.Contains(Rumours.Text(rb))) RumourFail(check, "the file of an inmate of the second wing does not say what the wing is saying");

                // An informant on the second wing names the origin (the tip's pool is the scene; their own wing; the dice forced).
                Pawn snitch = RumourInmate(s, falseWing, cells[3], cast);
                TaskMakeInformant(snitch.Inmate());
                Rumours.ForceTip = true;
                string tip = InformantSystem.TryTip(snitch, snitch.Inmate());
                Rumours.ForceTip = false;
                string expected = "RP_TipRumourFalse".Translate(told[0].LabelShort, Rumours.Text(rb), Rumours.WingName(falseWing));
                if (tip != expected || !rb.originKnown || !rb.truthKnown) RumourFail(check, $"the informant's tip was \"{tip}\" (origin known {rb.originKnown}, truth known {rb.truthKnown}), not \"{expected}\"");

                // A calm address in the second wing's canteen ends the false one at once (the first wing is not there: theirs is untouched).
                s.Place(w, cells[4]);
                float yardReach = ra.reach;
                int heard = WardenSystem.Address(w, firm: false);
                if (heard < 3 || rb.Live || rb.end != RumourEnd.Calmed || Rumours.LiveOn(pm, falseWing) != null) RumourFail(check, $"a calm address heard by {heard} left the false rumour {rb.Describe()} (ended by the calm word expected)");
                // (Unless real inmates of the first wing happened to be in that canteen, enough of them to answer for it: then they heard it too.)
                var canteenCells = new HashSet<IntVec3>(block);
                bool realYardThere = map.mapPawns.PrisonersOfColonySpawned.Count(p => !cast.Contains(p) && p.Inmate()?.security == yardWing && canteenCells.Contains(p.Position)) >= Rumours.AddressMinHeard;
                if (!realYardThere && (!ra.Live || Mathf.Abs(ra.reach - yardReach) > 0.0001f || ra.truthKnown)) RumourFail(check, $"the calm address on the second wing touched the first wing's rumour ({ra.Describe()})");
                yardReach = ra.reach;
                foreach (Pawn p in told)
                {
                    TensionUtility.HourlyUpdate(p, p.Inmate(), pm, 0f);
                    if (TensionUtility.LastContribution("RP_Cause_Rumour") != 0f) RumourFail(check, $"{p.LabelShort} still carries the rumour cause after the calm address ended it");
                }

                // On the first wing the true one survives a calm address: halved, and the warden knows it is true.
                foreach (Pawn p in told) s.Place(p, outside);
                for (int i = 0; i < yard.Count; i++) s.Place(yard[i], cells[i]);
                w.GetComp<CompStaff>().addressReadyTick = -1;
                heard = WardenSystem.Address(w, firm: false);
                if (heard < 3 || !ra.Live || Mathf.Abs(ra.reach - yardReach * 0.5f) > 0.0001f || !ra.truthKnown) RumourFail(check, $"a calm address to the first wing (heard by {heard}) left the true rumour {ra.Describe()} (live, its reach halved from {yardReach:F3}, known true expected)");
                // A firm one makes it stick a day longer; a second firm one adds nothing (firm words do not stack).
                int expire = ra.expireTick;
                w.GetComp<CompStaff>().addressReadyTick = -1;
                WardenSystem.Address(w, firm: true);
                if (!ra.Live || ra.expireTick != expire + Rumours.FirmDays * GenDate.TicksPerDay) RumourFail(check, $"a firm address moved the true rumour's end by {ra.expireTick - expire} ticks (a day expected)");
                w.GetComp<CompStaff>().addressReadyTick = -1;
                WardenSystem.Address(w, firm: true);
                if (!ra.Live || ra.expireTick != expire + Rumours.FirmDays * GenDate.TicksPerDay) RumourFail(check, $"two firm addresses moved the true rumour's end by {ra.expireTick - expire} ticks (one day expected: they do not stack)");

                // The walk names the origin: not a walk that met nobody of the wing, then one that did.
                if (Rumours.Notify_Walked(map, yardWing, told) || ra.originKnown) RumourFail(check, "a walk that met nobody of the first wing named who started its rumour");
                if (!Rumours.Notify_Walked(map, yardWing, yard) || !ra.originKnown || ra.originName.NullOrEmpty()) RumourFail(check, $"the walk of the first wing did not name who started its rumour ({ra.Describe()})");
                if (Rumours.Notify_Walked(map, falseWing, told)) RumourFail(check, "a walk named the origin of a rumour that had ended");

                // The riot's line: what the wing was saying, true or false as known; none for a wing that says nothing.
                string riot = Rumours.RiotLine(map, yardWing);
                if (riot == null || !riot.Contains(Rumours.Text(ra)) || !riot.Contains("RP_Rumour_True".Translate().ToString())) RumourFail(check, $"the riot's line for the first wing is \"{riot}\"");
                if (Rumours.RiotLine(map, falseWing) != null) RumourFail(check, "the riot's line names a rumour on a wing whose rumour ended");
                RPLog.Message($"[RimPrison:test] rumours: proof - tip \"{tip}\", riot line \"{riot}\", the true one {ra.Describe()}, the false one {rb.Describe()}");
            });
        }

        // ---- one live rumour per wing ---------------------------------------------------------------------------------------------------------------

        private static void CheckRumourOnePerWing(Map map)
        {
            const string check = "one per wing";
            Guarded("rumours: one live rumour per wing", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                SecurityLevelDef level = RumourLevels(map).FirstOrDefault();
                if (level == null) return;
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> wing = Enumerable.Range(0, 3).Select(i => RumourInmate(s, level, at, cast)).ToList();
                (Rumours.ForceStart, Rumours.ForceDistort) = (true, false);
                Rumour search = Rumours.Notify_Source(map, level, RumourKind.CellSearch, wing[0].LabelShort);
                Rumour transfer = Rumours.Notify_Source(map, level, RumourKind.Transfer, wing[1].LabelShort, wing[1]);
                Rumour again = Rumours.Notify_Source(map, level, RumourKind.CellSearch, wing[2].LabelShort);
                int live = pm.rumours.Count(x => x.Live && x.level == level);
                if (search == null || transfer == null || again != null || search.Live || search.end != RumourEnd.Replaced || !transfer.Live || live != 1)
                    RumourFail(check, $"a cell search, then a transfer, then a search: started {search != null}/{transfer != null}/{again != null} (yes, yes, no), the search replaced {search?.end}, live on the wing {live} (1)");
                if (transfer != null && transfer.originId == wing[1].thingIDNumber) RumourFail(check, "the one transferred started the rumour about their own transfer");
                // A death outranks a transfer; killed by a guard, the distorted version is the truth - shown in its own words ("the guards killed X"), the same words as when it is not true, so they give nothing away; the
                // dead never start it.
                Rumours.ForceDistort = true;
                string killedWords = "RP_Rumour_Death_False".Translate(wing[0].LabelShort).Resolve(), diedWords = "RP_Rumour_Death_True".Translate(wing[0].LabelShort).Resolve();
                for (int i = 0; i < 6; i++)
                {
                    Rumour death = Rumours.Notify_Death(wing[i % 3], map, byStaff: true);
                    if (i == 0 && (death == null || !death.isTrue || !death.distorted || death.kind != RumourKind.Death || Rumours.Text(death) != killedWords))
                        RumourFail(check, $"a death by a guard started {death?.Describe() ?? "nothing"} saying \"{(death != null ? Rumours.Text(death) : "")}\" (the distorted version, true, saying \"{killedWords}\" expected)");
                    if (death != null && death.originId == wing[i % 3].thingIDNumber) RumourFail(check, "the dead one started the rumour of their own death");
                    // (Cleared for the next: the same kind does not replace itself.)
                    pm.rumours.Clear();
                }
                Rumour twisted = Rumours.Notify_Death(wing[0], map, byStaff: false);
                if (twisted == null || twisted.isTrue || !twisted.distorted || Rumours.Text(twisted) != killedWords)
                    RumourFail(check, $"a death nobody of the staff caused, distorted, started {twisted?.Describe() ?? "nothing"} (false, in the same words \"{killedWords}\" as the true one, expected)");
                int heatNow = Find.TickManager.TicksGame;
                if (twisted != null && Mathf.Abs(Rumours.TensionFor(wing[1], wing[1].Inmate(), pm) - Rumours.Strength(twisted, heatNow) * Rumours.DistortedTensionPerHour) > 0.001f)
                    RumourFail(check, "a distorted rumour that does not hold winds the wing up differently from one that does (the size of the cause would give the truth away)");
                pm.rumours.Clear();
                Rumours.ForceDistort = false;
                Rumour plain = Rumours.Notify_Death(wing[0], map, byStaff: true);
                if (plain == null || !plain.isTrue || plain.distorted || Rumours.Text(plain) != diedWords) RumourFail(check, $"a death by a guard told plainly started {plain?.Describe() ?? "nothing"} (true, saying \"{diedWords}\", expected)");
                pm.rumours.Clear();
                Rumours.ForceDistort = null;
                // A wing of one tells nobody (only when no real inmate shares the level).
                if (RealOn(map, level, cast) == 0)
                {
                    wing[1].Inmate().security = wing[2].Inmate().security = RumourLevels(map).First(l => l != level);
                    if (Rumours.Notify_Source(map, level, RumourKind.Transfer) != null) RumourFail(check, "a rumour started on a wing of one inmate");
                    wing[1].Inmate().security = wing[2].Inmate().security = level;
                }
                // Without the forced start, a cell search starts one at its stated chance (about 15 of 100), never always.
                Rumours.ForceStart = null;
                int started = 0;
                for (int i = 0; i < 100; i++)
                {
                    pm.rumours.Clear();
                    if (Rumours.Notify_Source(map, level, RumourKind.CellSearch) != null) started++;
                }
                if (started == 0 || started > 40) RumourFail(check, $"100 cell searches started {started} rumours (about {Rumours.StartChance(RumourKind.CellSearch) * 100f:0} expected)");
                RPLog.Message($"[RimPrison:test] rumours: one per wing - 100 searches started {started}");
            });
        }

        // ---- the life: two days, a day more for a firm word -----------------------------------------------------------------------------------------

        private static void CheckRumourLife(Map map)
        {
            const string check = "the life";
            Guarded("rumours: two days and burnt out", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2) return;
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> a = Enumerable.Range(0, 2).Select(i => RumourInmate(s, levels[0], at, cast)).ToList();
                List<Pawn> b = Enumerable.Range(0, 2).Select(i => RumourInmate(s, levels[1], at, cast)).ToList();
                Rumour plain = Rumours.Start(pm, levels[0], RumourKind.YardOff, true, null, a[0]);
                Rumour held = Rumours.Start(pm, levels[1], RumourKind.EarlyLockup, false, null, b[0]);
                held.expireTick += Rumours.FirmDays * GenDate.TicksPerDay;
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                // Half way: half the strength.
                Find.TickManager.DebugSetTicksGame(clock0 + Rumours.LifeTicks / 2);
                float half = Rumours.Strength(plain, Find.TickManager.TicksGame);
                if (Mathf.Abs(half - plain.reach * 0.5f) > 0.01f) RumourFail(check, $"a day into two the strength is {half:F3} (half of {plain.reach:F3} expected)");
                // Two days and an hour: the plain one is burnt out, the one a firm word held is not.
                Find.TickManager.DebugSetTicksGame(clock0 + Rumours.LifeTicks + GenDate.TicksPerHour);
                Rumours.Hourly(map, pm);
                if (plain.Live || plain.end != RumourEnd.BurntOut || !held.Live) RumourFail(check, $"after two days the plain one is {plain.end} (BurntOut), the held one live {held.Live} (yes)");
                TensionUtility.HourlyUpdate(a[1], a[1].Inmate(), pm, 0f);
                if (TensionUtility.LastContribution("RP_Cause_Rumour") != 0f) RumourFail(check, "a burnt-out rumour still winds its wing up");
                // Three days and an hour: that one too.
                Find.TickManager.DebugSetTicksGame(clock0 + Rumours.LifeTicks + Rumours.FirmDays * GenDate.TicksPerDay + GenDate.TicksPerHour);
                Rumours.Hourly(map, pm);
                if (held.Live || held.end != RumourEnd.BurntOut) RumourFail(check, $"a day after the firm word's extra day the held one is {held.end} (BurntOut)");
                // Ended ones are kept for Intel for a while, then go.
                if (!pm.rumours.Contains(plain)) RumourFail(check, "a rumour that burnt out a day ago is no longer kept");
                Find.TickManager.DebugSetTicksGame(clock0 + Rumours.LifeTicks + (Rumours.KeepEndedDays + 2) * GenDate.TicksPerDay);
                Rumours.Hourly(map, pm);
                if (pm.rumours.Count != 0) RumourFail(check, $"{pm.rumours.Count} rumours kept long after they ended");
            });
        }

        // ---- the words and the numbers --------------------------------------------------------------------------------------------------------------

        private static void CheckRumourWords(Map map)
        {
            const string check = "the words";
            Guarded("rumours: texts and chances", map, r =>
            {
                foreach (RumourKind kind in System.Enum.GetValues(typeof(RumourKind)))
                {
                    if (!("RP_Rumour_" + kind + "_True").CanTranslate() || !("RP_Rumour_" + kind + "_False").CanTranslate()) RumourFail(check, $"{kind} has no words for one of its versions");
                    if (Rumours.StartChance(kind) <= 0f) RumourFail(check, $"{kind} never starts a rumour");
                }
                foreach (RumourEnd end in System.Enum.GetValues(typeof(RumourEnd)))
                    if (!("RP_Rumour_Ended" + end).CanTranslate()) RumourFail(check, $"no words for a rumour ended {end}");
                (float calm, float mid, float tense) = (Rumours.DistortChance(20f), Rumours.DistortChance(55f), Rumours.DistortChance(90f));
                if (Mathf.Abs(calm - Rumours.DistortLow) > 0.001f || Mathf.Abs(mid - 0.5f) > 0.001f || Mathf.Abs(tense - Rumours.DistortHigh) > 0.001f)
                    RumourFail(check, $"the distortion chance at tension 20/55/90 is {calm:F2}/{mid:F2}/{tense:F2} ({Rumours.DistortLow}/0.5/{Rumours.DistortHigh} expected)");
                if (Rumours.StartChance(RumourKind.Death) <= Rumours.StartChance(RumourKind.CellSearch)) RumourFail(check, "a death ranks no higher than a cell search");
            });
        }

        // ---- the riot reckoning's report ------------------------------------------------------------------------------------------------------------

        private static void CheckRumourRiotReport(Map map)
        {
            const string check = "the riot report";
            Guarded("rumours: the riot reckoning's report names the rumour the wing was telling when it began", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2) return;
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> wing = Enumerable.Range(0, 3).Select(i => RumourInmate(s, levels[0], at, cast)).ToList();
                Pawn quiet = RumourInmate(s, levels[1], at, cast);
                // The wing tells the distorted version that holds (a guard did kill them): the report gives its words and "true or false?", as the warden knew it when the riot began.
                Rumour talk = Rumours.Start(pm, levels[0], RumourKind.Death, isTrue: true, subject: "Zhale", origin: wing[1], distorted: true);
                if (talk == null)
                {
                    RumourFail(check, "no rumour could be started on the wing");
                    return;
                }
                string words = "RP_Rumour_Death_False".Translate("Zhale").Resolve(), line = RiotReview.RumourLine(talk);
                RiotRecord rec = RiotReview.Open(wing[0], wing[0].Inmate(), new List<Pawn> { wing[0], wing[2] }, null);
                if (rec?.rumour == null || rec.rumour == talk || Rumours.Text(rec.rumour) != words || !RiotReview.ReportLines(rec).Contains(line) || !line.Contains(words)
                    || !line.Contains("RP_Rumour_Unknown".Translate().ToString()))
                    RumourFail(check, $"the record of a riot on a wing saying \"{words}\" kept {rec?.rumour?.Describe() ?? "no rumour"} (a copy expected); its report: {(rec == null ? "none" : string.Join(" / ", RiotReview.ReportLines(rec)))}");
                // By the morning the talk is over (calmed, and the warden knows the truth now): the report still names it, as it stood when the riot began.
                Rumours.End(talk, RumourEnd.Calmed, Find.TickManager.TicksGame);
                talk.truthKnown = true;
                pm.rumours.Clear();
                if (rec != null && (!RiotReview.ReportLines(rec).Contains(line) || !RiotReview.LetterText(rec, map).Contains(words)))
                    RumourFail(check, $"once the rumour was over the report no longer gives \"{line}\": {string.Join(" / ", RiotReview.ReportLines(rec))}");
                // A riot on a wing that told nothing: no such line.
                RiotRecord none = RiotReview.Open(quiet, quiet.Inmate(), new List<Pawn> { quiet }, null);
                if (none == null || none.rumour != null || RiotReview.ReportLines(none).Any(x => x.Contains(words)))
                    RumourFail(check, $"a riot on a wing that told no rumour has {none?.rumour?.Describe() ?? "none"} in its record");
            });
        }

        // ---- the regime orders: a restriction, an order lifted, given again ---------------------------------------------------------------------------

        private static void CheckRumourOrders(Map map)
        {
            const string check = "the orders";
            Guarded("rumours: a restriction starts one restriction rumour; an order lifted or lapsed makes its rumour false; given again, no second roll", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                // (Today's orders of every wing set aside, and the doors told after they are back.)
                r.Add(() => PrisonMap.Of(map)?.Notify_DoorsChanged());
                RegimeOrdersSetAside(g, r);
                SecurityLevelDef level = RumourLevels(map).FirstOrDefault();
                if (level == null) return;
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> wing = Enumerable.Range(0, 3).Select(i => RumourInmate(s, level, at, cast)).ToList();
                (Rumours.ForceStart, Rumours.ForceDistort) = (true, false);
                // A restriction (the riot reckoning's answer): one rumour, of the restriction, not of the yard order or the lockup it is made of.
                bool restricted = RegimeOrders.Restrict(g, map, level, 2);
                Rumour rr = Rumours.LiveOn(pm, level);
                string restrictWords = "RP_Rumour_Restriction_True".Translate().Resolve();
                if (!restricted || rr == null || rr.kind != RumourKind.Restriction || pm.rumours.Count != 1 || Rumours.Text(rr) != restrictWords)
                    RumourFail(check, $"a restriction (given {restricted}) started {Rumours.Describe(pm)} (one rumour saying \"{restrictWords}\" expected)");
                // Lifting its yard order ends the restriction: the rumour goes on, false now and known to be.
                RegimeOrders.Lift(g, level, DayOrder.YardOff);
                if (rr != null && (!rr.Live || rr.isTrue || !rr.truthKnown)) RumourFail(check, $"the restriction lifted left its rumour {rr.Describe()} (live, false and known false expected)");
                RegimeOrders.Lift(g, level, DayOrder.EarlyLockup);
                pm.rumours.Clear();
                // The yard order: given, the plain version; lifted, false; given again the same day, no second roll and the plain words hold again.
                RegimeOrders.Give(g, map, level, DayOrder.YardOff);
                Rumour yard = Rumours.LiveOn(pm, level);
                if (yard == null || yard.kind != RumourKind.YardOff || !yard.isTrue)
                {
                    RumourFail(check, $"the yard called off started {yard?.Describe() ?? "nothing"} (the plain yard rumour expected)");
                    return;
                }
                RegimeOrders.Toggle(g, map, level, DayOrder.YardOff);
                if (!yard.Live || yard.isTrue || !yard.truthKnown) RumourFail(check, $"the yard order withdrawn left its rumour {yard.Describe()} (live, false and known false expected: a calm word can end it now)");
                RegimeOrders.Toggle(g, map, level, DayOrder.YardOff);
                if (pm.rumours.Count != 1 || Rumours.LiveOn(pm, level) != yard || !yard.isTrue)
                    RumourFail(check, $"the yard called off again the same day gave {Rumours.Describe(pm)} (the one rumour, true again, expected: no second roll)");
                // Midnight: the day's order lapses, and what the wing says of it no longer holds.
                if (g.dayOrders.TryGetValue(level, out DayOrders o)) o.day = Ledger.CurrentDay - 1;
                RegimeOrders.Sweep(g, map);
                if (RegimeOrders.Of(g, level) != null || !yard.Live || yard.isTrue) RumourFail(check, $"the yard order lapsed at midnight and left its rumour {yard.Describe()} (live and false expected)");
            });
        }

        // ---- the address: enough of the wing, firm once ---------------------------------------------------------------------------------------------

        private static void CheckRumourAddressShare(Map map)
        {
            const string check = "the address";
            Guarded("rumours: an address answers a wing's rumour only when two of the wing heard it; firm words do not stack", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                SecurityLevelDef level = RumourLevels(map).FirstOrDefault();
                if (level == null) return;
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> wing = Enumerable.Range(0, 3).Select(i => RumourInmate(s, level, at, cast)).ToList();
                Rumour lie = Rumours.Start(pm, level, RumourKind.Supermax, isTrue: false, subject: null, origin: wing[0]);
                if (lie == null || Rumours.AddressNeeds(map, level) != Rumours.AddressMinHeard)
                {
                    RumourFail(check, $"premise: no rumour, or the wing of {cast.Count} needs {Rumours.AddressNeeds(map, level)} to hear an address ({Rumours.AddressMinHeard} expected)");
                    return;
                }
                // One stray inmate of the wing (in another wing's canteen, say) does not answer for it; two do.
                int one = Rumours.Notify_Address(map, new List<Pawn> { wing[1] }, firm: false);
                if (one != 0 || !lie.Live || lie.truthKnown) RumourFail(check, $"a calm address heard by one of the wing answered {one} rumour(s), leaving {lie.Describe()} (untouched expected)");
                int two = Rumours.Notify_Address(map, new List<Pawn> { wing[1], wing[2] }, firm: false);
                if (two != 1 || lie.Live || lie.end != RumourEnd.Calmed) RumourFail(check, $"a calm address heard by two of the wing answered {two}, leaving {lie.Describe()} (ended expected)");
                // Firm, twice: one day longer, not two.
                Rumour held = Rumours.Start(pm, level, RumourKind.YardOff, isTrue: true, subject: null, origin: wing[0]);
                if (held == null) return;
                int expire = held.expireTick;
                Rumours.Notify_Address(map, new List<Pawn> { wing[1], wing[2] }, firm: true);
                Rumours.Notify_Address(map, new List<Pawn> { wing[1], wing[2] }, firm: true);
                if (held.expireTick != expire + Rumours.FirmDays * GenDate.TicksPerDay || held.expireTick > held.startTick + Rumours.MaxLifeTicks)
                    RumourFail(check, $"two firm addresses moved the end by {held.expireTick - expire} ticks ({Rumours.FirmDays * GenDate.TicksPerDay}, one firm word's day, expected)");
            });
        }

        // ---- a shakedown: one roll a wing -------------------------------------------------------------------------------------------------------------

        private static void CheckRumourShakedown(Map map)
        {
            const string check = "the shakedown";
            Guarded("rumours: a shakedown rolls once for each wing, not once for each cell", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                PrisonGame g = PrisonGame.Instance;
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2) return;
                (int until0, int last0) = (g.shakedownUntilTick, g.lastShakedownTick);
                var rolled0 = new Dictionary<SecurityLevelDef, int>(Rumours.shakedownRolled);
                r.Add(() =>
                {
                    (g.shakedownUntilTick, g.lastShakedownTick) = (until0, last0);
                    Rumours.shakedownRolled.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in rolled0) Rumours.shakedownRolled[kv.Key] = kv.Value;
                });
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> a = Enumerable.Range(0, 3).Select(i => RumourInmate(s, levels[0], at, cast)).ToList();
                List<Pawn> b = Enumerable.Range(0, 2).Select(i => RumourInmate(s, levels[1], at, cast)).ToList();
                int now = Find.TickManager.TicksGame;
                (g.lastShakedownTick, g.shakedownUntilTick) = (now, now + PrisonGame.ShakedownHours * GenDate.TicksPerHour);
                (Rumours.ForceStart, Rumours.ForceDistort) = (true, false);
                Rumour first = Rumours.Notify_CellSearch(a[0]);
                pm.rumours.Clear();
                Rumour second = Rumours.Notify_CellSearch(a[1]);
                Rumour other = Rumours.Notify_CellSearch(b[0]);
                if (first == null || second != null || other == null)
                    RumourFail(check, $"in one shakedown the first wing's two cell searches started {(first != null ? "one" : "none")} and {(second != null ? "one" : "none")} (one, then none), the second wing's {(other != null ? "one" : "none")} (one)");
                // Out of the shakedown, each search rolls again.
                g.shakedownUntilTick = -1;
                pm.rumours.Clear();
                if (Rumours.Notify_CellSearch(a[2]) == null) RumourFail(check, "after the shakedown a cell search on the wing rolled for nothing");
            });
        }

        // ---- the levels whose inmates never meet ------------------------------------------------------------------------------------------------------

        private static void CheckRumourShutLevels(Map map)
        {
            const string check = "shut levels";
            Guarded("rumours: none starts on supermax's walks, the padded cells or a referral's ward", map, r =>
            {
                List<SecurityLevelDef> shut = DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(l => !Rumours.CanTell(l)).ToList();
                if (shut.Count == 0)
                {
                    RPLog.Message("[RimPrison:test] rumours: no level a rumour cannot be told on (no supermax, padded or referral level): not checked");
                    return;
                }
                var s = new WardenScene(map, r);
                PrisonMap pm = RumourIsolate(map, r);
                IntVec3 at = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                var cast = new List<Pawn>();
                List<Pawn> two = Enumerable.Range(0, 2).Select(i => RumourInmate(s, shut[0], at, cast)).ToList();
                Rumours.ForceStart = true;
                foreach (SecurityLevelDef level in shut)
                {
                    foreach (Pawn p in two) p.Inmate().security = level;
                    if (Rumours.Notify_Source(map, level, RumourKind.Transfer) is { } told) RumourFail(check, $"a rumour started on {level.defName}, whose inmates never meet ({told.Describe()})");
                }
            });
        }

        // ---- the meetings: two wings in one place, a wing with nobody left ----------------------------------------------------------------------------

        private static void CheckRumourMeetings(Map map)
        {
            const string check = "the meetings";
            Guarded("rumours: two wings in one patch do not meet each other; a wing with nobody left ends its rumour", map, r =>
            {
                PrisonMap pm = RumourIsolate(map, r);
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2) return;
                Rumour live = Rumours.Start(pm, levels[0], RumourKind.YardOff, isTrue: true, subject: null, origin: null);
                if (live == null || !Rumours.BeginSample(pm))
                {
                    RumourFail(check, "the hour's count did not start with a live rumour on the map");
                    return;
                }
                // One of each wing in one patch: nobody of the first wing met another of it.
                Rumours.Saw(levels[0], 7);
                Rumours.Saw(levels[1], 7);
                float apart = Rumours.MetShare(levels[0]);
                Rumours.Saw(levels[0], 7);
                float together = Rumours.MetShare(levels[0]);
                if (apart != 0f || Mathf.Abs(together - 1f) > 0.001f) RumourFail(check, $"the first wing's share that met is {apart:F2} with one of it beside one of another wing (0 expected), {together:F2} with two of it (1 expected)");
                // (The count is the check's: the hour's pass ends it.)
                Rumours.Hourly(map, pm);
                // A level nobody is on: the hour's pass finds nobody left to tell it.
                var occupied = new HashSet<SecurityLevelDef>(map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null).Select(p => p.Inmate().security));
                SecurityLevelDef empty = levels.FirstOrDefault(l => !occupied.Contains(l));
                if (empty == null)
                {
                    RPLog.Message("[RimPrison:test] rumours: every level has an inmate, the emptied wing is not checked");
                    return;
                }
                pm.rumours.Clear();
                Rumour gone = Rumours.Start(pm, empty, RumourKind.Transfer, isTrue: true, subject: "Zgone", origin: null);
                RumourPasses(map, pm, 1);
                if (gone == null || gone.Live || gone.end != RumourEnd.Emptied) RumourFail(check, $"a rumour on {empty.defName}, with nobody of it on the map, is {gone?.Describe() ?? "none"} after an hour (ended Emptied expected)");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------

        private static string rumourSeed;
        private static List<Rumour> rumoursBeforeSeed;
        private static bool? rumourStartBeforeSeed;

        /// <summary>
        /// Two rumours with every field off its default: a live one (reach 1, so an hour's pass between the snapshot and the save changes nothing) on a wing with inmates, in the plain words but false (an order lifted), and one
        /// ended, distorted and true (a guard's kill); the words' default is the opposite of the truth, so both are written. No rumour starts until the check after the load (a cell search between the snapshot and the save
        /// would change the list).
        /// </summary>
        private static void SeedRumoursForReload(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            Pawn teller = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate()?.security != null).OrderBy(p => p.thingIDNumber).FirstOrDefault();
            if (pm == null || teller == null)
            {
                problems0.Add("saveload: no inmate to seed a rumour with");
                return;
            }
            rumoursBeforeSeed = pm.rumours;
            int now = Find.TickManager.TicksGame;
            SecurityLevelDef level = teller.Inmate().security, other = DefDatabase<SecurityLevelDef>.AllDefsListForReading.First(l => l != level);
            pm.rumours = new List<Rumour>
            {
                new Rumour
                {
                    kind = RumourKind.Supermax, level = level, originId = teller.thingIDNumber, originName = teller.LabelShort, isTrue = false, distorted = false, reach = 1f, startTick = Mathf.Max(1, now - 1234),
                    expireTick = now + GenDate.TicksPerDay + 777, subject = "Seedsubject", truthKnown = true, originKnown = true,
                },
                new Rumour
                {
                    kind = RumourKind.Death, level = other, originId = 4242, originName = "Seedorigin", isTrue = true, distorted = true, reach = 0.375f, startTick = Mathf.Max(1, now - 3000),
                    expireTick = now + 5000, endTick = Mathf.Max(1, now - 100), end = RumourEnd.Replaced, subject = "Seeddead", truthKnown = true,
                },
            };
            rumourSeed = Rumours.Describe(pm);
            rumourStartBeforeSeed = Rumours.ForceStart;
            Rumours.ForceStart = false;
            RPLog.Message($"[RimPrison:test] saveload rumours: {rumourSeed}");
        }

        /// <summary>The seeded rumours came back whole; then the map has its own again.</summary>
        private static void CheckRumoursAfterReload(Map map)
        {
            if (rumourSeed == null) return;
            PrisonMap pm = PrisonMap.Of(map);
            string after = Rumours.Describe(pm);
            RPLog.Message($"[RimPrison:test] saveload rumours: {(after == rumourSeed ? "intact" : "changed")} ({after})");
            if (after != rumourSeed) reloadProblems.Add($"the rumour list changed in the round trip: {rumourSeed} -> {after}");
            // (The list the game had: the seeded ones were never told. A rumour holds no pawn, only a level and numbers, so the old game's are copied over as they were.)
            pm.rumours = rumoursBeforeSeed?.Select(x => x.Copy()).ToList() ?? new List<Rumour>();
            rumoursBeforeSeed = null;
            rumourSeed = null;
            Rumours.ForceStart = rumourStartBeforeSeed;
            rumourStartBeforeSeed = null;
        }
    }
}
