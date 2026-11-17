using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The last day on death row (<see cref="LastDay"/>): the case waits for the warden's authority (no restraint, no carry, no per-diem, one fine a day), authorising sets the night and the last evening and the procedure follow by
    /// real ticks, the one stay and its hearing, the named detail and a guard with kin on the row refusing, a commutation mid-carry stopping the final action, the body and its letter, an old save's cases and a resurrected one. Each is
    /// rolled back (<see cref="Rollback"/>: the table, the pawns and their bodies too). The saveload plan's seeds and checks are at the end (the fields off their defaults, and real cases mid-carry and mid-wait through a save). Each fails without its change: the notes say how.
    /// </summary>
    public static partial class AutoTest
    {
        private static void LdFail(string what) => problems0.Add("last day: " + what);

        /// <summary>An execution leaves the body where it fell: the checks that call it directly do not.</summary>
        private static void ExecutedBodyGone(Pawn p)
        {
            if (p?.Corpse is { Destroyed: false } body) body.Destroy();
        }

        /// <summary>The checks of no real ticks, in the events burst (a tick here moves the clock the burst's later checks and the stove fire left to the staff wait on).</summary>
        private static void CheckLastDay(Map map)
        {
            CheckLastDayAwaiting(map);
            CheckLastDayAuthorise(map);
            CheckLastDayStay(map);
            CheckLastDayEvening(map);
            CheckLastDayDetail(map);
            CheckLastDayRefusal(map);
            CheckLastDayBodyAndLetter(map);
            CheckLastDayBodyOnTable(map);
            CheckLastDayLegacy(map);
        }

        private static bool lastDayTickedRan;

        /// <summary>
        /// The events plan's last day checks that run on real ticks (the night by the named detail, the procedure stopped at the last moment), once, from the plan's own loop after the burst and after the trusty checks: their ticks (a
        /// procedure alone is nine hundred) are hours of the game's clock together, which the burst's other checks and the stove fire left to the staff must not lose (the events run of last-day-l6a). The checks are self-contained
        /// (their own table, pawns and guards, put back); the end of the run fails when they never ran.
        /// </summary>
        private static void CheckLastDayTickedOnce(Map map)
        {
            if (lastDayTickedRan || !trustiesRan) return;
            lastDayTickedRan = true;
            int ticks0 = Find.TickManager.TicksGame;
            CheckLastDayProcedure(map);
            CheckLastDayStopped(map);
            RPLog.Message($"[RimPrison:test] the last day's checks on real ticks took {Find.TickManager.TicksGame - ticks0} ticks ({(Find.TickManager.TicksGame - ticks0) / (float)GenDate.TicksPerHour:F1} hours of the game's clock)");
        }

        // ---- the scene ------------------------------------------------------------------------------------------------------------------------

        /// <summary>The death row level, and (when asked) an execution table of the check's own near the intake, which a guard can reach.</summary>
        private sealed class LdScene
        {
            public Map map;
            public Rollback r;
            public readonly PrisonGame g = PrisonGame.Instance;
            public SecurityLevelDef row;
            public DeathRowExtension ext;
            public Building table;

            public static int Now => Find.TickManager.TicksGame;

            public static LdScene Build(Map map, Rollback r, string check, bool withTable)
            {
                SecurityLevelDef row = DefDatabase<SecurityLevelDef>.GetNamedSilentFail("RP_DeathRow");
                DeathRowExtension ext = row?.GetModExtension<DeathRowExtension>();
                if (ext == null)
                {
                    LdFail($"{check}: premise: no death row level with its extension");
                    return null;
                }
                var s = new LdScene { map = map, r = r, row = row, ext = ext };
                if (withTable && (s.table = SpawnTable(map, r)) == null)
                {
                    LdFail($"{check}: premise: no room for an execution table near the intake (the check could not run)");
                    return null;
                }
                return s;
            }

            public static Building SpawnTable(Map map, Rollback r)
            {
                ThingDef def = RPDeathRowDefOf.RP_ExecutionTable;
                IntVec3 origin = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                foreach (IntVec3 c in GenRadial.RadialCellsAround(origin, 10f, true))
                {
                    if (c.DistanceTo(origin) < 3f) continue;
                    CellRect rect = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                    if (!rect.All(x => x.InBounds(map) && x.Standable(map) && x.GetThingList(map).Count == 0 && x.GetDoor(map) == null)) continue;
                    Thing b = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    b.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(b, c, map, Rot4.North);
                    r.Add(() =>
                    {
                        if (!b.Destroyed) b.Destroy(DestroyMode.Vanish);
                    });
                    map.reachability.ClearCache();
                    return (Building)b;
                }
                return null;
            }

            /// <summary>A condemned throwaway whose date has passed (appeals out, no plea pending), still at stage none.</summary>
            public Pawn Condemned()
            {
                Pawn p = Throwaway(map, r, row);
                CompInmate c = p.Inmate();
                c.activeFlag = true;
                c.appealsLeft = 0;
                c.innocent = false;
                c.clemencyResolveTick = -1;
                c.executionTick = Now - 1;
                c.lastOverdueTick = Now;
                r.Add(() => ExecutedBodyGone(p));
                return p;
            }

            public Pawn Guard(bool atHome = false)
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome);
                ldGuards.Add(guard);
                return guard;
            }

            /// <summary>The condemned as the warden's authority is awaited (the hourly pass brings them there from their date).</summary>
            public Pawn Awaiting()
            {
                Pawn p = Condemned();
                DeathRowSystem.Hourly(map);
                return p;
            }

            /// <summary>An authorised condemned whose night has begun, the evening already held.</summary>
            public Pawn Night()
            {
                Pawn p = Condemned();
                CompInmate c = p.Inmate();
                (c.execStage, c.executionTick, c.lastEveningDone) = (ExecStage.Authorised, Now - 1, true);
                return p;
            }
        }

        private static IEnumerable<ChronicleEntry> LdLines(Rollback r, string text) => r.NewEntries.Where(e => e.text == text);

        /// <summary>The check's guards, kept on shift while it ticks (see <see cref="LdTickUntil"/>).</summary>
        private static readonly List<Pawn> ldGuards = new List<Pawn>();

        /// <summary>Ticks until <paramref name="done"/> or <paramref name="max"/> ticks, the check's guards on shift all the way (a shift change would send the one at the procedure home).</summary>
        private static int LdTickUntil(Func<bool> done, int max)
        {
            ldGuards.RemoveAll(g => g.Destroyed);
            int ticked = 0;
            while (ticked < max && !done())
            {
                Find.TickManager.DoSingleTick();
                foreach (Pawn g in ldGuards)
                    if (g.timetable != null && g.Spawned)
                        for (int h = 0; h < 24; h++)
                            if (g.timetable.GetAssignment(h) != TimeAssignmentDefOf.Work) g.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                ticked++;
            }
            return ticked;
        }

        /// <summary>The last day's guards kept on shift by the other checks' ticks (<see cref="ShuttleTicks"/>): the saveload plan's ticked checks that run before <see cref="FinishLastDayCaseAfterReload"/> would let an hourly pass send them home mid-procedure.</summary>
        private static void KeepLastDayGuardsOnShift()
        {
            foreach (Pawn g in ldGuards)
                if (g.timetable != null && g.Spawned)
                    for (int h = 0; h < 24; h++)
                        if (g.timetable.GetAssignment(h) != TimeAssignmentDefOf.Work) g.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
        }

        /// <summary>
        /// The game's clock moved on when it is early in the game, so that a date <paramref name="days"/> days back is not before tick 0 (which is how the game says "no date": a case with such a date is in its appeals). Put back by the rollback.
        /// </summary>
        private static void LdLateClock(Rollback r, int days)
        {
            int now = Find.TickManager.TicksGame;
            if (now > (days + 1) * GenDate.TicksPerDay) return;
            Find.TickManager.DebugSetTicksGame(now + (days + 1) * GenDate.TicksPerDay);
            r.Add(() => Find.TickManager.DebugSetTicksGame(now));
        }

        /// <summary>The cells round a point taken out of the home area until the rollback (vanilla forbids a body that falls outside it, which the morgue's haulers then pass by: what an execution's body must not be left as).</summary>
        private static void LdOutsideHome(Map map, Rollback r, IntVec3 around, float radius)
        {
            Area home = map.areaManager.Home;
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(around, radius, true))
            {
                if (!cell.InBounds(map) || !home[cell]) continue;
                IntVec3 c0 = cell;
                home[c0] = false;
                r.Add(() => home[c0] = true);
            }
        }

        /// <summary>The prison's chaplains set aside (no role, as far as the staff list goes) until the rollback: the evening of one with nobody can be had on a live staff.</summary>
        private static void LdChaplainsAside(Map map, Rollback r)
        {
            foreach (Pawn chaplain in StaffCommute.AllStaff(map).Where(x => x.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain).ToList())
            {
                CompStaff sc = chaplain.GetComp<CompStaff>();
                StaffRoleDef role = sc.role;
                r.Add(() =>
                {
                    sc.role = role;
                    StaffCommute.StaffVersion++;
                });
                sc.role = null;
            }
            StaffCommute.StaffVersion++;
        }

        /// <summary>A pawn stood on a cell at least <paramref name="minDist"/> cells from <paramref name="from"/>, in the open (a spawned one is moved, not made again).</summary>
        private static bool LdPlaceFar(Map map, Pawn p, IntVec3 from, float minDist)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(from, minDist + 40f, true))
            {
                if (cell.DistanceTo(from) < minDist || !cell.InBounds(map) || !cell.Standable(map) || cell.GetThingList(map).Count != 0 || cell.GetDoor(map) != null) continue;
                if (p.Spawned) p.DeSpawn();
                GenSpawn.Spawn(p, cell, map);
                return true;
            }
            return false;
        }

        // ---- the date reached: nothing happens until the warden acts ------------------------------------------------------------------------

        /// <summary>
        /// Appeals out, the date passed, a table and a free guard there: no restraint and no carry, the state stops paying and fines the prison once a day (the first day's fine, more every day, at most the cap; none twice in a
        /// day), the alerts and the file say so. (Without the change the condemned is restrained and carried off at once, paid for for a day, and fined a flat sum.) A date still ahead stays at none.
        /// </summary>
        private static void CheckLastDayAwaiting(Map map)
        {
            Guarded("last day: the case waits for the warden", map, r =>
            {
                const string check = "awaiting";
                // (Dates weeks back are used below: the clock is moved on, if it is early in the game, so that none of them is before tick 0.)
                LdLateClock(r, 45);
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard();
                Pawn p = s.Condemned();
                CompInmate c = p.Inmate();
                DailySettlement.DailyPayments(g, map, out int paidBefore);
                DeathRowSystem.Hourly(map);
                if (c.execStage != ExecStage.Awaiting) { LdFail($"{check}: the date passed and the case is {c.execStage}, not awaiting"); return; }
                DailySettlement.DailyPayments(g, map, out int paidAfter);
                if (paidAfter != paidBefore - 1) LdFail($"{check}: the state pays for {paidAfter} inmates with the case awaiting, {paidBefore} before (one fewer expected)");
                if (!r.NewLetters.Any(l => l.Label.ToString() == "RP_AwaitingLabel".Translate().ToString())) LdFail($"{check}: no letter telling the warden the case awaits them");
                if (DeathRowSystem.ExecutionDue(c) || WorkGiver_CarryOutExecution.Candidate(p)) LdFail($"{check}: an awaiting case is due for the table");
                DeathRowSystem.Restrain(p);
                InmateCandidates.Shared.Invalidate();
                if (new WorkGiver_CarryOutExecution().HasJobOnThing(guard, p, true)) LdFail($"{check}: a free guard is offered the carry of a case nobody authorised");
                DeathRowSystem.RemoveRestraint(p);
                DeathRowSystem.Hourly(map);
                if (p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) LdFail($"{check}: the hourly pass restrained an awaiting case");

                // The fine: none at once, one a day, the first day's, then more, then the cap.
                List<LedgerEntry> FinesOf(Pawn x) => r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_Fines && e.amount < 0 && e.note == "RP_ExecutionOverdueNote".Translate(x.LabelShort).ToString()).ToList();
                List<LedgerEntry> Fines() => FinesOf(p);
                if (Fines().Count != 0) LdFail($"{check}: fined on the first hour of waiting ({Fines().Count})");
                int now = LdScene.Now, day = GenDate.TicksPerDay, step = s.ext.overduePenaltyPerDay;
                c.lastOverdueTick = now - day;
                DeathRowSystem.Hourly(map);
                DeathRowSystem.Hourly(map);
                if (Fines().Count != 1 || Fines()[0].amount != -step) LdFail($"{check}: a day waiting booked {Fines().Count} fine(s) of {string.Join("/", Fines().Select(f => f.amount))} (one of -{step} expected)");
                (c.executionTick, c.lastOverdueTick) = (now - 3 * day - 5, now - day);
                int n0 = Fines().Count;
                DeathRowSystem.Hourly(map);
                if (Fines().Count != n0 + 1 || Fines().Last().amount != -3 * step) LdFail($"{check}: the third day's fine was {Fines().Last().amount} (-{3 * step} expected)");
                (c.executionTick, c.lastOverdueTick) = (now - 40 * day, now - day);
                DeathRowSystem.Hourly(map);
                if (Fines().Last().amount != -s.ext.overdueDaysMax * step) LdFail($"{check}: a month waiting is fined {Fines().Last().amount} a day (the cap, -{s.ext.overdueDaysMax * step}, expected)");

                // A case long past its date (an old save's, or a recaptured one's) pays the first day's fine on its first day of waiting, not the cap: the days count from the day the waiting began.
                Pawn longPast = s.Condemned();
                CompInmate lc = longPast.Inmate();
                lc.executionTick = now - 40 * day;
                DeathRowSystem.Hourly(map);
                if (lc.execStage != ExecStage.Awaiting || Math.Abs(lc.executionTick - now) > 5) LdFail($"{check}: a case 40 days past its date is {LastDay.Describe(lc)} (awaiting, the waiting begun now expected)");
                lc.lastOverdueTick = now - day;
                DeathRowSystem.Hourly(map);
                if (FinesOf(longPast).Count != 1 || FinesOf(longPast)[0].amount != -step) LdFail($"{check}: the first fine of a case long past its date was {string.Join("/", FinesOf(longPast).Select(f => f.amount))} (one of -{step} expected)");

                // The alerts, the to-do list and the file say it.
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (PrisonAlerts.Map == map && !PrisonAlerts.ExecutionAwaiting.Contains(p)) LdFail($"{check}: the alert's list does not hold the condemned");
                if (!ITab_InmateFile.StatusLines(p, c, now).Any(l => l.Item1.StartsWith(LastDay.Line(c, s.ext)))) LdFail($"{check}: the file's status lines do not say the case awaits the warden");
                if (DeathRowSystem.Status(c) != "RP_DeathRowAwaiting".Translate().ToString()) LdFail($"{check}: the sentence column says \"{DeathRowSystem.Status(c)}\"");

                // A date still ahead stays at none.
                Pawn later = s.Condemned();
                later.Inmate().executionTick = now + 10 * GenDate.TicksPerHour;
                DeathRowSystem.Hourly(map);
                if (later.Inmate().execStage != ExecStage.None) LdFail($"{check}: a date ten hours ahead is already {later.Inmate().execStage}");
            });
        }

        // ---- authorising ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Authorise: the night is the next 22:00 at least twelve hours ahead (and no more than a day and a half), no fine and no per-diem after it, no restraint before the night, the answer is checked again (a stale button does
        /// nothing: a second authorisation, one for a case that is not waiting).
        /// </summary>
        private static void CheckLastDayAuthorise(Map map)
        {
            Guarded("last day: authorising sets the night", map, r =>
            {
                const string check = "authorise";
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard();
                Pawn p = s.Awaiting();
                CompInmate c = p.Inmate();
                Pawn notWaiting = s.Condemned();
                if (LastDay.AuthoriseWhy(notWaiting) == null || LastDay.TryAuthorise(notWaiting)) LdFail($"{check}: a case that is not waiting could be authorised");
                if (LastDay.AuthoriseWhy(p) != null) { LdFail($"{check}: the awaiting case cannot be authorised: {LastDay.AuthoriseWhy(p)}"); return; }
                int now = LdScene.Now;
                if (!LastDay.TryAuthorise(p) || c.execStage != ExecStage.Authorised) { LdFail($"{check}: authorising did not set the case to authorised ({c.execStage})"); return; }
                int lead = c.executionTick - now;
                int hour = GenDate.HourOfDay(Find.TickManager.TicksAbs + lead, Find.WorldGrid.LongLatOf(map.Tile).x);
                if (lead < LastDay.MinLeadHours * GenDate.TicksPerHour || lead > (LastDay.MinLeadHours + 24) * GenDate.TicksPerHour + 1 || hour != LastDay.NightHour)
                    LdFail($"{check}: the night is {lead / (float)GenDate.TicksPerHour:F1} hours ahead at {hour}:00 (twelve to thirty-six hours, at {LastDay.NightHour}:00 expected)");
                if (LastDay.TryAuthorise(p) || LastDay.AuthoriseWhy(p) == null) LdFail($"{check}: the same case was authorised twice");
                if (LdLines(r, "RP_Chr_Authorised".Translate(p.LabelShort, (lead / (float)GenDate.TicksPerHour).ToString("0")).Resolve()).Count() != 1) LdFail($"{check}: no chronicle line for the authorisation, or more than one");
                // Nothing at the hourly pass before the night: no fine, no restraint, nothing for the guard, and the state still pays nothing.
                c.lastOverdueTick = now - 2 * GenDate.TicksPerDay;
                int fines0 = r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Fines);
                DeathRowSystem.Hourly(map);
                InmateCandidates.Shared.Invalidate();
                DeathRowSystem.Restrain(p);
                if (r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Fines) != fines0) LdFail($"{check}: an authorised case is still fined");
                if (new WorkGiver_CarryOutExecution().HasJobOnThing(guard, p, true)) LdFail($"{check}: a guard is offered the carry before the night");
                DeathRowSystem.RemoveRestraint(p);
                DeathRowSystem.Hourly(map);
                if (p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) LdFail($"{check}: the hourly pass restrained an authorised case before the night");
                DailySettlement.DailyPayments(g, map, out int paidNow);
                c.execStage = ExecStage.None;
                c.executionTick = -1;
                DailySettlement.DailyPayments(g, map, out int paidWithout);
                if (paidNow != paidWithout - 1) LdFail($"{check}: the per-diem pays for an authorised case ({paidNow} with, {paidWithout} without)");
            });
        }

        // ---- the stay -------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The stay: once per case, in waiting and in an authorised case before the night; it costs three reputation, clears the night and holds one more hearing in three days at the usual odds - lost, the case is back to waiting
        /// (the fine restarts, no second stay); won, it is commuted. (Without it there is no way off the calendar but the appeals.)
        /// </summary>
        private static void CheckLastDayStay(Map map)
        {
            Guarded("last day: the one stay", map, r =>
            {
                const string check = "stay";
                LdScene s = LdScene.Build(map, r, check, withTable: false);
                if (s == null) return;
                PrisonGame g = s.g;
                float overturn0 = s.ext.overturnChance, overturnInnocent0 = s.ext.overturnChanceInnocent;
                r.Add(() => (s.ext.overturnChance, s.ext.overturnChanceInnocent) = (overturn0, overturnInnocent0));
                g.reputation = 50f;
                Pawn p = s.Awaiting();
                CompInmate c = p.Inmate();
                if (LastDay.StayWhy(p) != null) { LdFail($"{check}: the awaiting case cannot be given a stay: {LastDay.StayWhy(p)}"); return; }
                int now = LdScene.Now;
                if (!LastDay.TryStay(p)) { LdFail($"{check}: the stay was not granted"); return; }
                if (c.execStage != ExecStage.None || c.executionTick != -1 || !c.stayUsed || c.appealsLeft != 1 || Math.Abs(c.nextAppealTick - (now + LastDay.StayHearingDays * GenDate.TicksPerDay)) > 5)
                    LdFail($"{check}: the stay left the case {LastDay.Describe(c)}, {c.appealsLeft} appeal(s), the hearing at {c.nextAppealTick - now} ticks (none, no date, used, one hearing in {LastDay.StayHearingDays} days expected)");
                if (Math.Abs(g.reputation - (50f - LastDay.StayReputation)) > 0.01f) LdFail($"{check}: the reputation is {g.reputation} after a stay (47 expected)");
                if (LastDay.StayWhy(p) == null || LastDay.TryStay(p) || Math.Abs(g.reputation - 47f) > 0.01f) LdFail($"{check}: a second stay was granted, or cost something");
                // The hearing, lost: back to waiting with the fine starting over, and no second stay.
                s.ext.overturnChance = s.ext.overturnChanceInnocent = 0f;
                c.nextAppealTick = now - 1;
                DeathRowSystem.Hourly(map);
                if (c.execStage != ExecStage.Awaiting || c.lastOverdueTick < now || c.appealsLeft != 0) LdFail($"{check}: a lost hearing left the case {LastDay.Describe(c)} (awaiting, the fine restarted expected)");
                if (!r.NewLetters.Any(l => l.Label.ToString() == "RP_StayLostLabel".Translate().ToString())) LdFail($"{check}: no letter for the lost hearing");
                if (LastDay.StayWhy(p) == null) LdFail($"{check}: a second stay is on offer after the first was lost");
                // The hearing, won: commuted (the existing path), and the case is over.
                s.ext.overturnChance = s.ext.overturnChanceInnocent = 1f;
                Pawn lucky = s.Awaiting();
                CompInmate lc = lucky.Inmate();
                int commuted0 = g.stats.commuted;
                if (!LastDay.TryStay(lucky)) { LdFail($"{check}: no stay for the second case"); return; }
                lc.nextAppealTick = LdScene.Now - 1;
                DeathRowSystem.Hourly(map);
                if (DeathRowSystem.Ext(lc) != null || lc.execStage != ExecStage.None || g.stats.commuted != commuted0 + 1) LdFail($"{check}: a won hearing left them {LastDay.Describe(lc)} on {lc.security?.defName}, commuted {g.stats.commuted - commuted0}");
                // An escape (the story lets go of them) and a recapture: the one stay is not given back, the date is theirs still, and the case is waiting again from the day they are back.
                s.ext.overturnChance = s.ext.overturnChanceInnocent = 0f;
                g.story.Notify_Leaving(p);
                if (c.stayUsed != true || c.execStage != ExecStage.None || c.executionTick < 0) LdFail($"{check}: after an escape the case is {LastDay.Describe(c)} (the stay kept, no stage, the date kept expected)");
                DeathRowSystem.Hourly(map);
                if (c.execStage != ExecStage.Awaiting || LastDay.StayWhy(p) == null) LdFail($"{check}: a recaptured one is {LastDay.Describe(c)}, a stay on offer {LastDay.StayWhy(p) == null} (waiting, no second stay expected)");
                // Escaped during the hearing the stay won: the hearing, lost after they are back, still goes to the waiting case (not to a new date and a last request).
                Pawn runner = s.Awaiting();
                CompInmate rc = runner.Inmate();
                if (!LastDay.TryStay(runner)) { LdFail($"{check}: no stay for the one who escapes during the hearing"); return; }
                g.story.Notify_Leaving(runner);
                rc.nextAppealTick = LdScene.Now - 1;
                DeathRowSystem.Hourly(map);
                if (rc.execStage != ExecStage.Awaiting || !rc.stayUsed) LdFail($"{check}: the hearing lost after an escape left the case {LastDay.Describe(rc)} (awaiting, the stay used expected)");
                // Before the night, and not after it.
                Pawn set = s.Condemned();
                (set.Inmate().execStage, set.Inmate().executionTick) = (ExecStage.Authorised, LdScene.Now + 2 * GenDate.TicksPerHour);
                if (LastDay.StayWhy(set) != null) LdFail($"{check}: no stay for an authorised case two hours before the night: {LastDay.StayWhy(set)}");
                set.Inmate().executionTick = LdScene.Now - 1;
                if (LastDay.StayWhy(set) == null || LastDay.TryStay(set)) LdFail($"{check}: a stay once the night has begun");
            });
        }

        // ---- the last evening -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The evening before the night (from 18:00, once): a visit by the family they have (a memory, calmer, a line), the chaplain's words if one is on the staff (the same), neither skipped and said; not before 18:00 and not
        /// twice. (Without it nothing marks the night.)
        /// </summary>
        private static void CheckLastDayEvening(Map map)
        {
            Guarded("last day: the last evening", map, r =>
            {
                const string check = "evening";
                LdScene s = LdScene.Build(map, r, check, withTable: false);
                if (s == null) return;
                int hour = GenDate.TicksPerHour;
                void Authorised(Pawn p, int ahead)
                {
                    CompInmate c = p.Inmate();
                    (c.execStage, c.executionTick, c.lastEveningDone, c.tension) = (ExecStage.Authorised, LdScene.Now + ahead, false, 50f);
                }
                // Alone first (the live staff's chaplains set aside, if it has any, so that the branch is always run).
                LdChaplainsAside(map, r);
                if (StaffCommute.AllStaff(map).Any(x => x.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain)) { LdFail($"{check}: premise: a chaplain is still on the staff list"); return; }
                Pawn alone = s.Condemned();
                alone.Inmate().relatives.Clear();
                alone.Inmate().familyCount = 0;
                Authorised(alone, 3 * hour);
                DeathRowSystem.Hourly(map);
                string aloneLine = "RP_Chr_LastEveningAlone".Translate(alone.LabelShort).Resolve();
                if (!alone.Inmate().lastEveningDone || LdLines(r, aloneLine).Count() != 1) LdFail($"{check}: the evening of one with no family and no chaplain was not said (done {alone.Inmate().lastEveningDone}, lines {LdLines(r, aloneLine).Count()})");
                // With family: nothing before 18:00, the visit at 18:00, once.
                Pawn kin = s.Condemned();
                CompInmate kc = kin.Inmate();
                kc.relatives.Clear();
                kc.relatives.Add(Relatives.Make("RP_Kin_Mother"));
                kc.familyCount = 1;
                Authorised(kin, 5 * hour);
                DeathRowSystem.Hourly(map);
                if (kc.lastEveningDone) LdFail($"{check}: the evening came five hours before the night");
                kc.executionTick = LdScene.Now + 3 * hour;
                DeathRowSystem.Hourly(map);
                DeathRowSystem.Hourly(map);
                string visit = "RP_Chr_LastVisit".Translate(kin.LabelShort, kc.relatives[0].Label).Resolve();
                if (!kc.lastEveningDone || LdLines(r, visit).Count() != 1) LdFail($"{check}: the last visit was written {LdLines(r, visit).Count()} times (once expected, done {kc.lastEveningDone})");
                if (kin.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLastDayDefOf.RP_LastVisit) == null) LdFail($"{check}: the visit left no memory");
                if (kc.tension > 36f) LdFail($"{check}: the visit left the tension at {kc.tension:F0} (50 before)");
                // With the chaplain on the staff, and no family.
                ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Chaplain);
                Pawn faith = s.Condemned();
                CompInmate fc = faith.Inmate();
                fc.relatives.Clear();
                fc.familyCount = 0;
                Authorised(faith, 3 * hour);
                DeathRowSystem.Hourly(map);
                string chaplain = "RP_Chr_LastChaplain".Translate(faith.LabelShort).Resolve();
                if (!fc.lastEveningDone || LdLines(r, chaplain).Count() != 1)
                    LdFail($"{check}: the chaplain's words were written {LdLines(r, chaplain).Count()} times for one with no family (once expected)");
                if (faith.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLastDayDefOf.RP_LastVisit) != null) LdFail($"{check}: one with no family had a visit from them");
                if (faith.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLastDayDefOf.RP_ChaplainAtTheDoor) == null) LdFail($"{check}: the chaplain left no memory");
            });
        }

        // ---- the detail and the procedure (real ticks) -----------------------------------------------------------------------------------

        /// <summary>
        /// The night, by real ticks: only the named guard is offered the carry (a third guard is not, until the third hour); the detail is kept through the hourly pass whoever of it is off site; the named guard carries them to the table (the
        /// carry is longer than the sixty ticks after which the regime's cache of an unseen pawn is false) and the procedure runs, effects once (the sentence, the last words, the statistic), no "stopped" line for a sentence carried out, the
        /// body is on the table after and keeps the next from it until it is moved - and the next is carried out on the same table by real ticks. The keep-apart list is kept for the letter, and the memory goes to the executioner and every
        /// named guard on site however far off (not to one at home, not to one nobody named). (Without the custody test on the pawn held the carry stopped itself in a minute; with the old 20-cell radius the far guard had none.)
        /// </summary>
        private static void CheckLastDayProcedure(Map map)
        {
            Guarded("last day: the night, by the named detail", map, r =>
            {
                const string check = "procedure";
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                // (The table's surroundings are outside the home area, where vanilla forbids the body that falls there.)
                LdOutsideHome(map, r, s.table.Position, 12f);
                PrisonGame g = s.g;
                Pawn named = s.Guard(), farNamed = s.Guard(), homeNamed = s.Guard(atHome: true), free = s.Guard();
                Pawn p = s.Night();
                Pawn apart = Throwaway(map, r);
                CompInmate c = p.Inmate();
                if (!LdPlaceFar(map, farNamed, s.table.Position, 22f)) { LdFail($"{check}: premise: no open cell 22 cells from the table for the far guard"); return; }
                if (!LastDay.TryName(p, named) || !LastDay.TryName(p, farNamed) || !LastDay.TryName(p, homeNamed) || LastDay.TryName(p, named)) { LdFail($"{check}: the detail could not be named (or a guard was named twice, or a fourth was)"); return; }
                bool funerals0 = Funerals.TestOn;
                Funerals.TestOn = true;
                r.Add(() => Funerals.TestOn = funerals0);
                List<int> funeralApart0 = g.story.funeralApart.ToList();
                r.Add(() =>
                {
                    g.story.funeralApart.Clear();
                    g.story.funeralApart.AddRange(funeralApart0);
                });
                if (KeepApart.Add(p, apart, "warden", silent: true) != KeepApartResult.Added) LdFail($"{check}: premise: the keep-apart order was not written");
                // An hour passes: the guard at home and the one far off are still on the detail (off shift is not off the detail).
                DeathRowSystem.Hourly(map);
                if (c.detailIds.Count != 3) LdFail($"{check}: the hourly pass left {c.detailIds.Count} of the 3 named guards (one at home, one far off: all three expected)");
                DeathRowSystem.Restrain(p);
                InmateCandidates.Shared.Invalidate();
                map.reachability.ClearCache();
                var giver = new WorkGiver_CarryOutExecution();
                if (giver.HasJobOnThing(free, p, true)) LdFail($"{check}: a free guard who was not named is offered the carry in the first hour of the night");
                int night = c.executionTick;
                c.executionTick = LdScene.Now - LastDay.FallbackHours * GenDate.TicksPerHour - 1;
                if (!giver.HasJobOnThing(free, p, true)) LdFail($"{check}: no free guard is offered the carry after the third hour of the night");
                c.executionTick = night;
                if (!giver.HasJobOnThing(named, p, true) || giver.JobOnThing(named, p) is not { } job) { LdFail($"{check}: the named guard is not offered the carry"); return; }
                int executions0 = g.stats.executions;
                // The guard at home stays at home for the real ticks (the commute would bring them in for the shift, and a named guard on site then is owed the memory: that is the rule, not what this asks).
                bool noEntry0 = StaffCommute.ForceNoEntry;
                StaffCommute.ForceNoEntry = true;
                r.Add(() => StaffCommute.ForceNoEntry = noEntry0);
                named.jobs.StartJob(job, JobCondition.InterruptForced);
                int ticked = LdTickUntil(() => p.Dead || named.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution, 5000);
                StaffCommute.ForceNoEntry = noEntry0;
                if (!StaffCommute.AtHome(homeNamed)) { LdFail($"{check}: premise: the named guard sent home was not at home when the sentence was carried out (spawned {homeNamed.Spawned})"); return; }
                if (!p.Dead) { LdFail($"{check}: the named guard did not carry out the sentence in {ticked} ticks (the guard does {named.CurJobDef?.defName}, the condemned is at {p.Position}, the table at {s.table.Position}, stopped: {string.Join(" / ", r.NewEntries.Select(e => e.text))})"); return; }
                if (g.stats.executions != executions0 + 1) LdFail($"{check}: the executions counted {g.stats.executions - executions0} for one");
                string name = p.LabelShort;
                if (LdLines(r, "RP_Chr_Executed".Translate(name).Resolve()).Count() != 1) LdFail($"{check}: the execution was written {LdLines(r, "RP_Chr_Executed".Translate(name).Resolve()).Count()} times (once expected)");
                string words = "RP_Chr_LastWords".Translate(name, LastDay.WordsKey(p, c).Translate()).Resolve();
                if (LdLines(r, words).Count() != 1) LdFail($"{check}: the last words \"{words}\" were written {LdLines(r, words).Count()} times (once expected)");
                string stopHead = LdStoppedHead(name);
                if (r.NewEntries.Any(e => e.text.StartsWith(stopHead))) LdFail($"{check}: a stop was written for a sentence that was carried out");
                if (p.Corpse is not { Destroyed: false, Spawned: true } body) { LdFail($"{check}: no body on the map after the execution"); return; }
                if (c.execStage != ExecStage.Executed) LdFail($"{check}: the condemned is {c.execStage} after the execution");
                // Not forbidden, though it fell outside the home area (vanilla forbids it there): the staff would pass it by.
                if (body.IsForbidden(Faction.OfPlayer)) LdFail($"{check}: the executed body is forbidden (it fell outside the home area: {!map.areaManager.Home[body.Position]})");
                // The memory: the executioner and every named guard on site (the far one too); not the named guard at home, not the guard nobody named.
                bool Has(Pawn x) => x.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLastDayDefOf.RP_ExecutionDetail) != null;
                if (!Has(named) || !Has(farNamed) || Has(homeNamed) || Has(free)) LdFail($"{check}: the detail memory: executioner {Has(named)}, the named guard 22 cells away {Has(farNamed)}, the named guard at home {Has(homeNamed)}, the guard nobody named {Has(free)} (yes, yes, no, no expected)");
                // The keep-apart list was kept for the letter.
                List<int> pairs = g.story.funeralApart;
                bool kept = false;
                for (int i = 0; i + 2 < pairs.Count; i += 3) kept |= pairs[i] == p.thingIDNumber && pairs[i + 1] == apart.thingIDNumber;
                if (!kept) LdFail($"{check}: the keep-apart list was not kept for the body's letter ({string.Join("+", pairs)})");
                // The body is on the table at the moment of the sentence, which will not take the next one until it is moved.
                if (!DeathRowSystem.HoldsBody(s.table) || DeathRowSystem.FindTable(map, null) == s.table) LdFail($"{check}: a table with a body on it is still offered for the next execution (holds {DeathRowSystem.HoldsBody(s.table)})");
                // The executioner moves it off at once, by real ticks: to a free morgue cell they can reach if there is one, else to a cell beside the table. Nothing is allowed or moved by the check.
                PrisonMap pm = PrisonMap.Of(map);
                bool morgueFree = pm != null && Morgue.FreeCell(named, pm).IsValid;
                bool Beside(Corpse x) => x.Spawned && Math.Abs(x.Position.x - s.table.Position.x) <= 1 && Math.Abs(x.Position.z - s.table.Position.z) <= 1 && x.Position != s.table.Position;
                int moved = LdTickUntil(() => body.Spawned && !DeathRowSystem.HoldsBody(s.table), 900);
                if (!body.Spawned || DeathRowSystem.HoldsBody(s.table)) { LdFail($"{check}: the executioner did not move the body off the table in {moved} ticks (the body spawned {body.Spawned}, the guard does {named.CurJobDef?.defName}, a morgue cell free {morgueFree})"); return; }
                if (morgueFree ? !Morgue.InMorgue(body, pm) : !Beside(body)) LdFail($"{check}: the body was put at {body.Position} (the table at {s.table.Position}, a morgue cell free {morgueFree}: {(morgueFree ? "the morgue" : "a cell beside the table")} expected)");
                if (body.IsForbidden(Faction.OfPlayer)) LdFail($"{check}: the body that was moved is forbidden");
                // The next one, on the same table, by real ticks: nothing was moved, allowed or taken away by the check. (With the live prison's own table too the giver may choose that one: the job is made for this table.)
                Pawn second = s.Night();
                if (!LastDay.TryName(second, named)) { LdFail($"{check}: premise: the guard could not be named for the second"); return; }
                DeathRowSystem.Restrain(second);
                InmateCandidates.Shared.Invalidate();
                map.reachability.ClearCache();
                if (map.listerBuildings.AllBuildingsColonistOfDef(RPDeathRowDefOf.RP_ExecutionTable).Count() == 1 && !giver.HasJobOnThing(named, second, true))
                    LdFail($"{check}: the guard is not offered the second carry once the body is off the only table");
                named.jobs.StartJob(LdCarryJob(second, s.table), JobCondition.InterruptForced);
                ticked = LdTickUntil(() => second.Dead || named.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution, 5000);
                if (!second.Dead || g.stats.executions != executions0 + 2 || second.Corpse is not { } secondBody)
                {
                    LdFail($"{check}: the second execution on the same table: dead {second.Dead} after {ticked} ticks, {g.stats.executions - executions0} counted (2 expected)");
                    return;
                }
                LdTickUntil(() => secondBody.Spawned && !DeathRowSystem.HoldsBody(s.table), 900);
                if (!secondBody.Spawned || DeathRowSystem.HoldsBody(s.table) || secondBody.Position == body.Position)
                    LdFail($"{check}: the second body was not moved off the table to a cell of its own (spawned {secondBody.Spawned}, at {secondBody.Position}, the first at {body.Position})");
            });
        }

        /// <summary>The start of the chronicle line that says a procedure was stopped, up to the reason (a line that begins so, for this one, is a stop).</summary>
        private static string LdStoppedHead(string name)
        {
            string probe = "RP_Chr_ExecutionStopped".Translate(name, "\u0001").Resolve();
            return probe.Substring(0, probe.IndexOf('\u0001'));
        }

        // ---- the detail over the whole staff ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The detail is named from every guard employed, on site or at home: a guard at home can be named, a named guard sent home stays named through the hourly pass (the day shift's guard is at home at the night's hours), and
        /// only one on leave or leaving is dropped. The file's text says who is off site. (Without this the detail was wiped by the first shift change.)
        /// </summary>
        private static void CheckLastDayDetail(Map map)
        {
            Guarded("last day: the detail is over all the staff", map, r =>
            {
                const string check = "detail";
                LdScene s = LdScene.Build(map, r, check, withTable: false);
                if (s == null) return;
                Pawn onSite = s.Guard(), sentHome = s.Guard(), homeAlready = s.Guard(atHome: true), onLeave = s.Guard(), leaving = s.Guard();
                Pawn p = s.Night();
                CompInmate c = p.Inmate();
                List<Pawn> eligible = LastDay.Eligible(map, c);
                if (!eligible.Contains(homeAlready) || !eligible.Contains(onSite)) { LdFail($"{check}: the guards who may be named are {string.Join(", ", eligible.Select(x => x.LabelShort))}: the one at home is {(eligible.Contains(homeAlready) ? "" : "not ")}among them"); return; }
                // Three named (one of them at home already); a fourth is refused.
                foreach (Pawn g in new[] { homeAlready, sentHome, onLeave }) LastDay.TryName(p, g);
                if (c.detailIds.Count != 3 || !c.detailIds.Contains(homeAlready.thingIDNumber)) { LdFail($"{check}: the detail is {string.Join("+", c.detailIds)} after naming three (the one at home among them expected)"); return; }
                if (LastDay.TryName(p, onSite)) LdFail($"{check}: a fourth guard was named");
                // One named goes home (off shift); one goes on leave. An hour or two pass.
                StaffCommute.Leave(sentHome);
                onLeave.GetComp<CompStaff>().leaveUntilTick = LdScene.Now + GenDate.TicksPerDay;
                DeathRowSystem.Hourly(map);
                DeathRowSystem.Hourly(map);
                if (!c.detailIds.Contains(sentHome.thingIDNumber) || !c.detailIds.Contains(homeAlready.thingIDNumber))
                    LdFail($"{check}: the hourly pass dropped a guard who is only off site (detail {string.Join("+", c.detailIds)}; at home {sentHome.thingIDNumber} and {homeAlready.thingIDNumber})");
                if (c.detailIds.Contains(onLeave.thingIDNumber)) LdFail($"{check}: a guard on leave is still on the detail");
                if (LastDay.TryName(p, onLeave)) LdFail($"{check}: a guard on leave could be named");
                // The file says who is off site.
                string text = LastDay.DetailText(map, c);
                if (!text.Contains(sentHome.LabelShort + " " + "RP_Last_OffSite".Translate().Resolve()) || !text.Contains(homeAlready.LabelShort + " " + "RP_Last_OffSite".Translate().Resolve()))
                    LdFail($"{check}: the file's detail text \"{text}\" does not say the guards at home are off site");
                // A guard named who is let go (leaving) is dropped; the others stay.
                if (!LastDay.TryName(p, leaving)) { LdFail($"{check}: premise: the free slot was not taken by the guard named last"); return; }
                leaving.GetComp<CompStaff>().leaveAtTick = LdScene.Now + 1000;
                DeathRowSystem.Hourly(map);
                if (c.detailIds.Contains(leaving.thingIDNumber) || !c.detailIds.Contains(sentHome.thingIDNumber)) LdFail($"{check}: after one was let go the detail is {string.Join("+", c.detailIds)} (the one let go dropped, the one at home kept expected)");
            });
        }

        // ---- a guard with kin on the row ------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard with kin on the row never takes the carry for that case: a named one is not offered it (the offer writes nothing, and the real work scan - the path that complains with a red error when an offer yields no job - logs nothing for them), the
        /// refusal is written by the night's hourly pass (one chronicle line, kept, never again), one nobody named with kin on the row is not offered it either, at the third hour too; a guard nobody named without kin waits for the third hour and
        /// then is offered it; the refusal is the case's (kept when the guard is unnamed or the detail emptied, no second line when named again). (Without it the named guard carries out the sentence of their own family, and the refusal in the job logs an error.)
        /// </summary>
        private static void CheckLastDayRefusal(Map map)
        {
            Guarded("last day: a guard with kin on the row never carries them out", map, r =>
            {
                const string check = "refusal";
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                Pawn kinGuard = s.Guard(), kinFree = s.Guard(), free = s.Guard(), other = s.Guard();
                Pawn p = s.Night();
                Pawn kinOnRow = s.Condemned();
                CompInmate c = p.Inmate();
                KinAge(kinGuard, 44f);
                KinAge(kinFree, 46f);
                KinAge(kinOnRow, 20f);
                KinClean(r, kinGuard, kinFree, kinOnRow);
                if (!Kin.Link(kinOnRow, kinGuard, KinKind.Parent) || !Kin.Link(kinOnRow, kinFree, KinKind.Parent)) { LdFail($"{check}: premise: the guards and the condemned could not be made kin"); return; }
                if (!LastDay.Refuses(kinGuard) || !LastDay.Refuses(kinFree)) { LdFail($"{check}: premise: a guard with a child on death row does not refuse"); return; }
                if (LastDay.Refuses(free)) LdFail($"{check}: a guard with no kin refuses");
                if (!LastDay.TryName(p, kinGuard) || !LastDay.TryName(p, other)) { LdFail($"{check}: premise: the guards could not be named"); return; }
                DeathRowSystem.Restrain(p);
                InmateCandidates.Shared.Invalidate();
                map.reachability.ClearCache();
                var giver = new WorkGiver_CarryOutExecution();
                string line = "RP_Chr_DetailRefused".Translate(Kin.Mention(kinGuard), p.LabelShort).Resolve();
                // The offer is a question: asked as often as the scanner likes, it writes nothing and decides nothing - and a named guard with kin on the row is not offered it.
                if (giver.HasJobOnThing(kinGuard, p, true) || giver.HasJobOnThing(kinGuard, p, true) || LdLines(r, line).Count() != 0 || c.detailRefused.Count != 0)
                    LdFail($"{check}: the offer to a named guard with kin on the row was made, or wrote {LdLines(r, line).Count()} line(s) and {c.detailRefused.Count} refusal(s) (not offered, none, none expected: the line is the night's hourly pass)");
                // The real work scan (what the guards do): the named guard with no kin is given the carry (so the path is the real one, told if not) and the one with kin is given something else, with no error logged for it (an offer that yields no job is logged by vanilla).
                int errors0 = ErrorCount;
                Job real = new JobGiver_Work().TryIssueJobPackage(other, default).Job;
                // (Told, not failed: another job of the live prison's may come first for a guard; the scan below then proves less.)
                if (real?.def != RPDeathRowDefOf.RP_CarryOutExecution) RPLog.Message($"[RimPrison:test] last day refusal: the real work scan gave the named guard with no kin {real?.def.defName ?? "nothing"}, not the carry: the scan for the guard with kin is not proved to reach the carry's work giver");
                Job refused = new JobGiver_Work().TryIssueJobPackage(kinGuard, default).Job;
                if (refused?.def == RPDeathRowDefOf.RP_CarryOutExecution || ErrorCount != errors0) LdFail($"{check}: the real work scan gave the guard with kin on the row {refused?.def.defName ?? "nothing"} and logged {ErrorCount - errors0} error(s) (not the carry, none expected: {string.Join(" / ", FirstErrors)})");
                // The night's hourly pass writes the refusal, once, and keeps it.
                DeathRowSystem.Hourly(map);
                DeathRowSystem.Hourly(map);
                if (LdLines(r, line).Count() != 1 || !c.detailRefused.Contains(kinGuard.thingIDNumber)) LdFail($"{check}: the refusal was written {LdLines(r, line).Count()} times, kept {c.detailRefused.Contains(kinGuard.thingIDNumber)} (once, kept expected)");
                if (c.detailRefused.Contains(other.thingIDNumber) || c.detailRefused.Contains(kinFree.thingIDNumber)) LdFail($"{check}: a guard who is not named, or has no kin, was written down as refusing");
                // Another named guard is there: the free one waits for the third hour; the other named guard is offered it.
                if (giver.HasJobOnThing(free, p, true)) LdFail($"{check}: a free guard took the carry before the third hour while a guard was named");
                if (!giver.HasJobOnThing(other, p, true) || giver.JobOnThing(other, p) == null) LdFail($"{check}: the other named guard, with no kin on the row, is not offered the carry");
                // The refusal is the case's: the guard unnamed, the whole detail emptied, an hour gone by - still never. A guard with kin on the row who was never named is not offered it either, with nobody named.
                LastDay.Unname(c, kinGuard);
                LastDay.Unname(c, other);
                DeathRowSystem.Hourly(map);
                if (!c.detailRefused.Contains(kinGuard.thingIDNumber)) LdFail($"{check}: the refusal was forgotten when the guard was taken off the names");
                if (giver.HasJobOnThing(kinGuard, p, true)) LdFail($"{check}: with the detail empty the guard who refused is offered the carry");
                if (giver.HasJobOnThing(kinFree, p, true)) LdFail($"{check}: with nobody named a free guard with kin on the row is offered the carry of their own family");
                if (!giver.HasJobOnThing(free, p, true)) LdFail($"{check}: with nobody named a free guard is not offered the carry");
                // Named again: still refused, and not written twice; at the third hour a free guard may and neither of the two with kin does.
                if (!LastDay.TryName(p, kinGuard)) { LdFail($"{check}: premise: the guard could not be named again"); return; }
                DeathRowSystem.Hourly(map);
                if (giver.HasJobOnThing(kinGuard, p, true) || LdLines(r, line).Count() != 1) LdFail($"{check}: named again, the guard who refused was offered the carry or the refusal was written again ({LdLines(r, line).Count()} lines)");
                c.executionTick = LdScene.Now - LastDay.FallbackHours * GenDate.TicksPerHour - 1;
                if (!giver.HasJobOnThing(free, p, true)) LdFail($"{check}: no free guard was offered the carry after the third hour");
                if (giver.HasJobOnThing(kinGuard, p, true) || giver.HasJobOnThing(kinFree, p, true)) LdFail($"{check}: a guard with kin on the row was offered the carry at the third hour");
            });
        }

        // ---- stopped at the last moment -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The procedure stopped (real ticks): a commutation while the guard has them in their arms (the regime's cache of them is false there, and the job goes on until it is), then on the table; the stay at the final action alone; a
        /// pawn who is not on the table; the job ended from outside during the wait (drafted, downed, another job) and the condemned moved off the table; and the table destroyed under the carry. Each: the guard's job ends, the chronicle says why once, the restraint comes off, nobody dies, the carried one is put down. (Without the check the carry ends in an
        /// execution; with the table's destruction ending the job before the stop is asked, the restraint stays on and nothing is said.)
        /// </summary>
        private static void CheckLastDayStopped(Map map)
        {
            Guarded("last day: the final action asks again", map, r =>
            {
                const string check = "stopped";
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                Pawn guard = s.Guard();
                var giver = new WorkGiver_CarryOutExecution();
                // The case of a night, a guard named for it, the carry started: carried or on the table. Null: the premise failed (told, with what was seen: the toils the guard went through, where everyone stood). Every call has a condemned
                // and a guard of its own, put at least six cells from the table (the carry is many ticks, the state asked for is seen), nothing left from the call before: the state of the condemned is asked first.
                Pawn Start(Pawn by, bool onTable, out CompInmate cc)
                {
                    Pawn x = s.Night();
                    cc = x.Inmate();
                    if (x.Dead || x.Corpse != null || cc.execStage != ExecStage.Authorised || !x.Spawned) { LdFail($"{check}: premise: the condemned of the call is not a living case at the night (dead {x.Dead}, {LastDay.Describe(cc)}, spawned {x.Spawned})"); return null; }
                    if (x.Position.DistanceTo(s.table.Position) < 6f && !LdPlaceFar(map, x, s.table.Position, 6f)) { LdFail($"{check}: premise: no open cell six cells from the table for the condemned"); return null; }
                    if (!LastDay.TryName(x, by)) { LdFail($"{check}: premise: the guard could not be named"); return null; }
                    DeathRowSystem.Restrain(x);
                    InmateCandidates.Shared.Invalidate();
                    map.reachability.ClearCache();
                    if (!giver.HasJobOnThing(by, x, true) || giver.JobOnThing(by, x) is not { } job) { LdFail($"{check}: premise: the named guard is not offered the carry"); return null; }
                    IntVec3 from = x.Position, guardFrom = by.Position;
                    int executions0 = s.g.stats.executions;
                    var seen = new List<string>();
                    by.jobs.StartJob(job, JobCondition.InterruptForced);
                    bool Carried() => by.carryTracker?.CarriedThing == x;
                    int ticked = LdTickUntil(() =>
                    {
                        string state = $"toil {(by.jobs.curDriver is JobDriver_CarryOutExecution ? by.jobs.curDriver.CurToilIndex : -1)}{(Carried() ? " carrying" : "")}{(x.Spawned ? "" : " unspawned")}{(x.Dead ? " dead" : "")}";
                        if (seen.Count == 0 || seen[seen.Count - 1] != state) seen.Add(state);
                        return x.Dead || by.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution || (onTable ? x.Spawned && x.Position == s.table.Position : Carried());
                    }, 4000);
                    if (x.Dead || (onTable ? x.Position != s.table.Position : !Carried()))
                    {
                        LdFail($"{check}: premise: the condemned was not {(onTable ? "laid on the table" : "in the guard's arms")} in {ticked} ticks (dead {x.Dead}, executions {s.g.stats.executions - executions0}, the guard does {by.CurJobDef?.defName}; "
                               + $"the condemned from {from} to {x.Position}, the guard from {guardFrom} to {by.Position}, the table at {s.table.Position}; seen: {string.Join(" > ", seen)})");
                        return null;
                    }
                    return x;
                }
                void Stopped(Pawn x, Pawn by, string reason, string what)
                {
                    LdTickUntil(() => by.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution, 30);
                    if (x.Dead) LdFail($"{check}: {what}: the condemned died");
                    if (by.CurJobDef == RPDeathRowDefOf.RP_CarryOutExecution) LdFail($"{check}: {what}: the guard is still at the procedure");
                    if (x.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) LdFail($"{check}: {what}: the restraint stayed on after the procedure was stopped");
                    string stopped = "RP_Chr_ExecutionStopped".Translate(x.LabelShort, reason.Translate().Resolve()).Resolve();
                    if (LdLines(r, stopped).Count() != 1) LdFail($"{check}: {what}: the stop was written {LdLines(r, stopped).Count()} times (once expected: \"{stopped}\")");
                    LdTickUntil(() => x.Spawned, 120);
                    if (!x.Spawned) LdFail($"{check}: {what}: the condemned is still in the guard's arms");
                }

                // A commutation with the condemned in the guard's arms (the commute itself moves beds and needs the map: the state it leaves is set here). The cache of the regime for a pawn nobody sees spawned is false (as the game's
                // sixty-tick pass makes it): the carry goes on all the same, and stops when the sentence is gone.
                Pawn carried = Start(guard, onTable: false, out CompInmate cc1);
                if (carried == null) return;
                cc1.activeFlag = false;
                LdTickUntil(() => false, 5);
                if (guard.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution || LdLines(r, "RP_Chr_ExecutionStopped".Translate(carried.LabelShort, "RP_ExecStop_NotInCustody".Translate().Resolve()).Resolve()).Any())
                    LdFail($"{check}: the carry stopped for a condemned in the guard's arms, whose regime cache is false (the guard does {guard.CurJobDef?.defName}, in custody {LastDay.InCustody(carried)})");
                DeathRowExtension ext = DeathRowSystem.Ext(cc1);
                DeathRowSystem.ClearCase(cc1);
                (cc1.executionTick, cc1.security) = (-1, ext.commuteTo);
                Stopped(carried, guard, "RP_ExecStop_NotSentenced", "a commutation mid-carry");

                // A commutation with the condemned on the table, the whole commute (the regular path): the detail is freed too.
                Pawn guard2 = s.Guard();
                Pawn onTable = Start(guard2, onTable: true, out CompInmate cc2);
                if (onTable == null) return;
                int executions0 = s.g.stats.executions;
                DeathRowSystem.Commute(onTable, cc2, ext);
                Stopped(onTable, guard2, "RP_ExecStop_NotSentenced", "a commutation on the table");
                if (s.g.stats.executions != executions0) LdFail($"{check}: the executions counted {s.g.stats.executions - executions0} with the sentence commuted");
                if (cc2.detailIds.Count != 0 || cc2.execStage != ExecStage.None) LdFail($"{check}: the case after the commutation is {LastDay.Describe(cc2)} (no stage, no detail expected)");

                // The final action alone: a stay (the stage is no longer authorised), then a pawn who is not on the table.
                Pawn stayed = s.Night();
                (stayed.Inmate().execStage, stayed.Inmate().executionTick) = (ExecStage.None, -1);
                if (LastDay.TryExecute(stayed, guard, s.table) || stayed.Dead) LdFail($"{check}: the final action went ahead for a case that is not authorised any more");
                if (LdLines(r, "RP_Chr_ExecutionStopped".Translate(stayed.LabelShort, "RP_ExecStop_NotAuthorised".Translate().Resolve()).Resolve()).Count() != 1) LdFail($"{check}: the refusal for the stay was not written");
                // (The throwaway is spawned at the intake, three cells or more from the table.)
                Pawn away = s.Night();
                if (LastDay.TryExecute(away, guard, s.table) || away.Dead) LdFail($"{check}: the final action went ahead for a pawn who is not on the table");
                if (!LdLines(r, "RP_Chr_ExecutionStopped".Translate(away.LabelShort, "RP_ExecStop_NotOnTable".Translate().Resolve()).Resolve()).Any()) LdFail($"{check}: the refusal for a pawn off the table was not written");

                // Every other way out of the procedure is a stop too, with nothing standing in the way (RP_ExecStop_Interrupted): the executioner drafted during the wait, one downed there, one given another order by hand; and the condemned
                // moved off the table during the wait (RP_ExecStop_NotOnTable). Each: the job ends, one line, the restraint comes off at once, nobody dies. (Without it a job ended from outside left the restraint on and said nothing.)
                Pawn drafter = s.Guard();
                Pawn drafted = Start(drafter, onTable: true, out _);
                if (drafted == null) return;
                if (drafter.drafter != null) drafter.drafter.Drafted = true;
                else drafter.jobs.EndCurrentJob(JobCondition.InterruptForced);
                Stopped(drafted, drafter, "RP_ExecStop_Interrupted", "the executioner drafted during the wait");
                if (drafter.drafter != null) drafter.drafter.Drafted = false;
                LdPlaceFar(map, drafted, s.table.Position, 3f);
                Pawn hurt = s.Guard();
                Pawn hurtFor = Start(hurt, onTable: true, out _);
                if (hurtFor == null) return;
                HealthUtility.DamageUntilDowned(hurt, allowBleedingWounds: false);
                Stopped(hurtFor, hurt, "RP_ExecStop_Interrupted", "the executioner downed during the wait");
                LdPlaceFar(map, hurtFor, s.table.Position, 3f);
                Pawn ordered = s.Guard();
                Pawn orderedFor = Start(ordered, onTable: true, out _);
                if (orderedFor == null) return;
                ordered.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 600), JobCondition.InterruptForced);
                Stopped(orderedFor, ordered, "RP_ExecStop_Interrupted", "the executioner given another job during the wait");
                LdPlaceFar(map, orderedFor, s.table.Position, 3f);
                Pawn moving = s.Guard();
                Pawn moved = Start(moving, onTable: true, out _);
                if (moved == null) return;
                IntVec3 off = CellFinder.StandableCellNear(s.table.Position, map, 4f, c2 => c2 != s.table.Position && c2.GetFirstThing<Pawn>(map) == null);
                if (!off.IsValid) { LdFail($"{check}: premise: no open cell near the table to move the condemned to"); return; }
                moved.DeSpawn();
                GenSpawn.Spawn(moved, off, map);
                Stopped(moved, moving, "RP_ExecStop_NotOnTable", "the condemned moved off the table during the wait");
                if (s.g.stats.executions != executions0) LdFail($"{check}: the executions counted {s.g.stats.executions - executions0} for procedures that were all stopped");

                // The table destroyed under the carry: the stop is asked first, so the reason is written and the restraint comes off.
                Pawn guard3 = s.Guard();
                Pawn lost = Start(guard3, onTable: false, out CompInmate cc3);
                if (lost == null) return;
                s.table.Destroy(DestroyMode.Vanish);
                map.reachability.ClearCache();
                Stopped(lost, guard3, "RP_ExecStop_NoTable", "the table destroyed mid-carry");
                if (cc3.execStage != ExecStage.Authorised) LdFail($"{check}: the destroyed table changed the case to {LastDay.Describe(cc3)}");
            });
        }

        // ---- the body, the letter, the funeral, the resurrected -------------------------------------------------------------------------

        /// <summary>
        /// The body after an execution (the effects called directly): it is in the prison's hands and is sent home like any death's with the family's letter of its own kind (bitter only when the wrongful execution came to light,
        /// never from whether they were guilty), no funeral is offered, and one who comes back to life is a case awaiting the warden with the stay used. (Without the change the body was destroyed on the spot.)
        /// </summary>
        private static void CheckLastDayBodyAndLetter(Map map)
        {
            Guarded("last day: the body, the letter, the funeral and the resurrected", map, r =>
            {
                const string check = "body";
                LdScene s = LdScene.Build(map, r, check, withTable: false);
                if (s == null) return;
                PrisonGame g = s.g;
                List<Condolence> letters0 = g.story.condolences.ToList();
                r.Add(() =>
                {
                    g.story.condolences.Clear();
                    g.story.condolences.AddRange(letters0);
                });
                float exposed0 = s.ext.exposedChance;
                r.Add(() => s.ext.exposedChance = exposed0);
                MailKindDef Letter(bool innocent, float exposure, out Pawn dead)
                {
                    s.ext.exposedChance = exposure;
                    dead = s.Condemned();
                    CompInmate c = dead.Inmate();
                    (c.innocent, c.execStage) = (innocent, ExecStage.Authorised);
                    c.relatives.Clear();
                    c.relatives.Add(Relatives.Make("RP_Kin_Mother"));
                    c.familyCount = 1;
                    DeathRowSystem.Execute(dead, null);
                    if (dead.Corpse is not { Destroyed: false, Spawned: true } body) return null;
                    int n = g.story.condolences.Count;
                    CondolenceSystem.Send(g, body);
                    return g.story.condolences.Count == n + 1 ? g.story.condolences[n].kind : null;
                }
                MailKindDef plain = Letter(false, 0f, out Pawn plainDead);
                MailKindDef hiddenInnocent = Letter(true, 0f, out Pawn hiddenDead);
                MailKindDef bitter = Letter(true, 1f, out Pawn bitterDead);
                if (plain != RPCondolenceDefOf.RP_Mail_CondolenceExecuted) LdFail($"{check}: the family's letter after an execution is {plain?.defName ?? "none"} (the executed kind expected)");
                if (hiddenInnocent != RPCondolenceDefOf.RP_Mail_CondolenceExecuted) LdFail($"{check}: an innocence that did not come out changed the family's letter to {hiddenInnocent?.defName ?? "none"}");
                if (bitter != RPCondolenceDefOf.RP_Mail_CondolenceExecutedBitter) LdFail($"{check}: the wrongful execution that came to light has the letter {bitter?.defName ?? "none"} (the bitter executed kind expected)");
                if (!plainDead.Dead || plainDead.Inmate().execStage != ExecStage.Executed) LdFail($"{check}: the executed one is {plainDead.Inmate().execStage}");
                if (Funerals.Mournable(plainDead) || Funerals.Mournable(hiddenDead) || Funerals.Mournable(bitterDead)) LdFail($"{check}: a funeral would be offered for an execution");
                Pawn natural = s.Condemned();
                if (!Funerals.Mournable(natural)) LdFail($"{check}: (control) no funeral would be offered for an inmate who is alive and not executed");
                // Back from the dead, by the game's own path (a resurrector serum: the Harmony postfix on the health tracker): the executed one, the wrongful execution that came to light, with the body still there (it has not been sent home) is
                // awaiting the warden with the stay used and nothing exposed, and is not "executing" any more. (Without the postfix they would come back as an executed case that nothing ever acts on.)
                s.ext.exposedChance = 1f;
                Pawn back = s.Condemned();
                CompInmate rc = back.Inmate();
                (rc.innocent, rc.execStage) = (true, ExecStage.Authorised);
                DeathRowSystem.Execute(back, null);
                if (!back.Dead || back.Corpse is not { Spawned: true } || !rc.exposedWrongful || rc.execStage != ExecStage.Executed) { LdFail($"{check}: premise: the executed one is {(back.Dead ? "dead" : "alive")}, {LastDay.Describe(rc)}"); return; }
                int resurrectTick = LdScene.Now;
                if (!ResurrectionUtility.TryResurrect(back)) { LdFail($"{check}: premise: the executed one could not be brought back"); return; }
                if (rc.execStage != ExecStage.Awaiting || !rc.stayUsed || rc.exposedWrongful || rc.executing || Math.Abs(rc.executionTick - resurrectTick) > 5 || rc.lastOverdueTick < resurrectTick)
                    LdFail($"{check}: a resurrected executed one is {LastDay.Describe(rc)}, executing {rc.executing} (awaiting from now, the stay used, nothing exposed expected)");
            });
        }

        // ---- a body on the only table ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// A body that lies on a table (put there otherwise than by the procedure) keeps the next execution from it, and says so as its own thing, not "no table": the alert, the to-do line and the night's missed reason name the body and what it needs
        /// (the body allowed first, else a morgue, room in it, or a hauler); a free table makes it none. (Without it the warden read "no table" with a table standing there, and the missed night said they could not be restrained.)
        /// </summary>
        private static void CheckLastDayBodyOnTable(Map map)
        {
            Guarded("last day: a body on the execution table is its own status", map, r =>
            {
                const string check = "table";
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                // Every table of the prison holds a body (the live one's own too), so none is free.
                var bodies = new List<Corpse>();
                foreach (Building table in map.listerBuildings.AllBuildingsColonistOfDef(RPDeathRowDefOf.RP_ExecutionTable).ToList())
                    bodies.Add(FixCorpse(map, r, Throwaway(map, r), table.Position));
                Pawn p = s.Night();
                CompInmate c = p.Inmate();
                if (DeathRowSystem.FindTable(map, null) != null) { LdFail($"{check}: premise: a table is free with a body on each"); return; }
                Corpse body = DeathRowSystem.BodyOnTable(map);
                if (body == null || !bodies.Contains(body)) { LdFail($"{check}: no table is free and no body is found on one ({body?.LabelShort ?? "none"})"); return; }
                string text = DeathRowSystem.BodyOnTableText(body);
                if (!text.Contains(body.InnerPawn.LabelShort)) LdFail($"{check}: the text for the body does not name whose it is: \"{text}\"");
                // The hint: forbidden first; else the morgue (none painted, full, or a hauler's to do).
                body.SetForbidden(true, warnOnFail: false);
                if (!DeathRowSystem.BodyOnTableText(body).Contains("RP_Table_NeedAllow".Translate().Resolve())) LdFail($"{check}: a forbidden body's text does not say to allow it: \"{DeathRowSystem.BodyOnTableText(body)}\"");
                body.SetForbidden(false, warnOnFail: false);
                text = DeathRowSystem.BodyOnTableText(body);
                if (text.Contains("RP_Table_NeedAllow".Translate().Resolve())) LdFail($"{check}: an allowed body's text says to allow it: \"{text}\"");
                // The alert, the to-do list: the body's own, never "no table".
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (PrisonAlerts.Map == map && (PrisonAlerts.ExecutionBody != body || PrisonAlerts.ExecutionNoTable)) LdFail($"{check}: the alerts say body {PrisonAlerts.ExecutionBody?.LabelShort ?? "none"}, no table {PrisonAlerts.ExecutionNoTable} (the body, not no table, expected)");
                List<string> problems = PrisonDiagnostics.Problems(map).ToList();
                if (!problems.Contains(text) || problems.Contains("RP_Problem_NoExecutionTable".Translate().Resolve())) LdFail($"{check}: the to-do list does not carry the body's line, or carries \"no table\" (\"{text}\")");
                // The missed night says why: the body, not "could not be restrained".
                c.executionTick = LdScene.Now - LastDay.MissedAfterHours * GenDate.TicksPerHour - 1;
                DeathRowSystem.Hourly(map);
                string missed = "RP_Chr_NightMissed".Translate(p.LabelShort, "RP_NightMissed_BodyOnTable".Translate()).Resolve();
                if (LdLines(r, missed).Count() != 1) LdFail($"{check}: the missed night's line said \"{string.Join(" / ", r.NewEntries.Where(e => e.text.Contains(p.LabelShort)).Select(e => e.text))}\" (\"{missed}\" once expected)");
                // A table with no body is free again: no body is blamed.
                bodies[0].Destroy();
                if (DeathRowSystem.BodyOnTable(map) != null || DeathRowSystem.FindTable(map, null) == null) LdFail($"{check}: with a body gone from a table the status is still a body on the table ({DeathRowSystem.BodyOnTable(map)?.LabelShort ?? "none"})");
            });
        }

        // ---- an old save ----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// An old save's cases: a past-due date at stage none enters waiting (a future date stays none), and one a guard is already carrying out (the old procedure under way) is authorised with the night now and no evening, so the
        /// procedure goes on. (Without it an old save's condemned would wait for an authority nobody gave while the carry the old pass set going stopped at the last toil.)
        /// </summary>
        private static void CheckLastDayLegacy(Map map)
        {
            Guarded("last day: an old save's cases", map, r =>
            {
                const string check = "legacy";
                LdScene s = LdScene.Build(map, r, check, withTable: true);
                if (s == null) return;
                Pawn guard = s.Guard();
                Pawn pastDue = s.Condemned();
                Pawn carried = s.Condemned();
                pastDue.Inmate().executionTick = LdScene.Now - 5 * GenDate.TicksPerHour;
                DeathRowSystem.Restrain(carried);
                Job job = JobMaker.MakeJob(RPDeathRowDefOf.RP_CarryOutExecution, carried, s.table);
                job.count = 1;
                guard.jobs.StartJob(job, JobCondition.InterruptForced);
                if (guard.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution) { LdFail($"{check}: premise: the guard did not start the carry (does {guard.CurJobDef?.defName})"); return; }
                DeathRowSystem.Hourly(map);
                if (pastDue.Inmate().execStage != ExecStage.Awaiting) LdFail($"{check}: a past-due date at stage none loaded as {pastDue.Inmate().execStage} (awaiting expected)");
                CompInmate cc = carried.Inmate();
                if (cc.execStage != ExecStage.Authorised || !cc.lastEveningDone || Math.Abs(cc.executionTick - LdScene.Now) > 5)
                    LdFail($"{check}: a case a guard was carrying out loaded as {LastDay.Describe(cc)} (authorised, the night now, no evening expected)");
                guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
                DeathRowSystem.RemoveRestraint(carried);
                // The job's own questions meet an old save's case first (the hourly pass is up to an hour away): the carry is not stopped for a stage nobody has set.
                Pawn early = s.Condemned();
                DeathRowSystem.Restrain(early);
                if (LastDay.Blocker(early, s.table, onTable: false) != null || early.Inmate().execStage != ExecStage.Authorised) LdFail($"{check}: an old save's carry that met the job's check first was stopped, or not taken as the night ({LastDay.Describe(early.Inmate())})");
                // The same for the condemned in the guard's arms, unspawned, with the regime's cache of them false (what a load in the middle of a carry leaves): custody is asked of the pawn held, so the old carry goes on as the night.
                Pawn inArms = s.Condemned();
                inArms.Inmate().activeFlag = false;
                DeathRowSystem.Restrain(inArms);
                Pawn carrier = s.Guard();
                r.Add(() =>
                {
                    if (inArms.holdingOwner != null) carrier.carryTracker.innerContainer.Remove(inArms);
                });
                if (carrier.carryTracker.TryStartCarry(inArms, 1) != 1 || inArms.Spawned) { LdFail($"{check}: premise: the guard could not take the condemned in their arms"); return; }
                string stopped = LastDay.Blocker(inArms, s.table, onTable: false);
                if (stopped != null || inArms.Inmate().execStage != ExecStage.Authorised || !inArms.Inmate().lastEveningDone)
                    LdFail($"{check}: an old save's carry, the condemned in the guard's arms, was stopped (\"{stopped}\") or not taken as the night ({LastDay.Describe(inArms.Inmate())})");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------

        private static int lastDaySeedId = -1;
        private static string lastDaySeedState;

        /// <summary>An inmate gets every field of the case off its default (a stage, the stay, the evening, a wrongful exposure, a detail with a refusal): they are in the snapshot of the round trip (<see cref="LastDay.Describe"/>).</summary>
        private static void SeedLastDayForReload(Map map)
        {
            lastDaySeedId = -1;
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true } ic && DeathRowSystem.Ext(ic) == null && ic.execStage == ExecStage.None && !ic.stayUsed);
            if (p == null)
            {
                reloadProblems.Add("saveload: no inmate to seed the last day's fields on");
                return;
            }
            CompInmate c = p.Inmate();
            (c.execStage, c.stayUsed, c.lastEveningDone, c.exposedWrongful) = (ExecStage.Authorised, true, true, true);
            c.detailIds.AddRange(new[] { 70011, 70022, 70033 });
            c.detailRefused.Add(70022);
            (lastDaySeedId, lastDaySeedState) = (p.thingIDNumber, LastDay.Describe(c));
            RPLog.Message($"[RimPrison:test] saveload last day: {p.LabelShort} has {lastDaySeedState}");
        }

        private static void CheckLastDayAfterReload(Map map)
        {
            // (With no tick, like the others here: the procedures are carried on and judged in FinishLastDayCaseAfterReload.)
            CheckLastDayCaseAfterReload(map);
            if (lastDaySeedId < 0) return;
            int id = lastDaySeedId;
            lastDaySeedId = -1;
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);
            if (p?.Inmate() is not { } c)
            {
                reloadProblems.Add("saveload: the inmate with the last day's fields did not survive the load");
                return;
            }
            if (LastDay.Describe(c) != lastDaySeedState) reloadProblems.Add($"saveload: the last day's fields changed across the load ({lastDaySeedState} -> {LastDay.Describe(c)})");
            DeathRowSystem.ClearCase(c);
        }

        // ---- the real case through a save --------------------------------------------------------------------------------------------

        /// <summary>The death-row cases seeded for the save: the condemned and their guards, the tables, and what was true before the save.</summary>
        private sealed class LdSavedCase
        {
            public int a, b, stayed, guardA, guardB, guardS, tableA, tableB, tableS, executions0;
            public float reputation0;
            public Pawn aPawn, bPawn, stayedPawn;
        }

        private static LdSavedCase ldCase;

        private const int ProcedureToil = JobDriver_CarryOutExecution.WaitToil;

        private static Job LdCarryJob(Pawn condemned, Building table)
        {
            Job job = JobMaker.MakeJob(RPDeathRowDefOf.RP_CarryOutExecution, condemned, table);
            job.count = 1;
            return job;
        }

        /// <summary>The pawn moved (not made again) to an open cell next to the thing (a table, a pawn).</summary>
        private static bool LdPutBeside(Map map, Pawn p, Thing anchor)
        {
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(anchor))
            {
                if (!cell.InBounds(map) || !cell.Standable(map) || cell.GetFirstBuilding(map) != null || cell.GetFirstPawn(map) != null) continue;
                if (p.Spawned) p.DeSpawn();
                GenSpawn.Spawn(p, cell, map);
                return true;
            }
            return false;
        }

        private static readonly System.Reflection.FieldInfo DriverToils = AccessTools.Field(typeof(JobDriver), "toils");

        /// <summary>The guard's carry-out job moved on to the toil, by hand (the toil's own start runs: no game tick is wanted). Nothing if the job is not theirs or is at that toil or past it.</summary>
        private static void LdToToil(Pawn guard, int index)
        {
            JobDriver driver = guard.jobs.curDriver;
            if (guard.CurJobDef != RPDeathRowDefOf.RP_CarryOutExecution || driver == null || driver.CurToilIndex >= index) return;
            driver.JumpToToil(((List<Toil>)DriverToils.GetValue(driver))[index]);
        }

        /// <summary>The guard's job at the procedure's wait with the condemned laid on the table: what the job's own toils lead to, finished by hand where they have not yet (the one in the arms put down, the walk skipped).</summary>
        private static void LdToWait(Pawn guard, Pawn condemned, Building table)
        {
            if (guard.carryTracker?.CarriedThing == condemned) guard.carryTracker.TryDropCarriedThing(table.Position, ThingPlaceMode.Direct, out Thing _);
            if (!condemned.Spawned || condemned.Position != table.Position) return;
            LdToToil(guard, ProcedureToil);
        }

        private static void LdPutOn(Map map, Pawn p, Building table)
        {
            if (p.Spawned) p.DeSpawn();
            GenSpawn.Spawn(p, table.Position, map);
        }

        /// <summary>
        /// Before the save: three real cases of a night, each with a guard of the detail on the carry-out job, started by hand and put at their moment (the job's toils met at once or moved on by hand: the game is not ticked, so that the seeds before this one keep the clocks, jobs and
        /// walks they set for the save) - one condemned in the guard's arms on the way to a table (the carry), one laid on a table with the guard in the procedure's wait, and one in the same state to whom a stay is then given. After the load they are judged:
        /// <see cref="CheckLastDayCaseAfterReload"/> (no tick: the stage, the jobs, the custody of the one in the arms) and <see cref="FinishLastDayCaseAfterReload"/> (the sentence carried out once each and none for the stay).
        /// </summary>
        private static void SeedLastDayCaseForReload(Map map)
        {
            ldCase = null;
            ldGuards.Clear();
            var r = new Rollback(map);
            try
            {
                LdScene s = LdScene.Build(map, r, "saveload", withTable: false);
                if (s == null)
                {
                    reloadProblems.Add("saveload: no death row level to seed the last day's procedures on");
                    return;
                }
                Building tableA = LdScene.SpawnTable(map, r), tableB = LdScene.SpawnTable(map, r), tableS = LdScene.SpawnTable(map, r);
                if (tableA == null || tableB == null || tableS == null)
                {
                    reloadProblems.Add("saveload: no room for three execution tables near the intake to seed the last day's procedures (premise)");
                    r.Dispose();
                    return;
                }
                PrisonGame g = s.g;
                Pawn guardA = s.Guard(), guardB = s.Guard(), guardS = s.Guard();
                // (Fresh throwaway staff, never the guards of the seeds before: the supermax walks' escorts, the monitor's operator and the care delivery's guard keep the jobs they were given.)
                var held = new HashSet<int> { supermaxSeedGuardA, supermaxSeedGuardB, supermaxSeedSpare, cctvSeedGuardId, careSeedReserve, careSeedOther };
                if (new[] { guardA, guardB, guardS }.Any(x => held.Contains(x.thingIDNumber)))
                {
                    reloadProblems.Add("saveload: premise: a guard of the last day's seed is one another seed holds");
                    r.Dispose();
                    return;
                }
                Pawn a = s.Night(), b = s.Night(), stayed = s.Night();
                foreach ((Pawn condemned, Pawn guard) in new[] { (a, guardA), (b, guardB), (stayed, guardS) })
                {
                    if (!LastDay.TryName(condemned, guard))
                    {
                        reloadProblems.Add("saveload: premise: a guard could not be named for the last day's procedure");
                        r.Dispose();
                        return;
                    }
                    // (The one to be carried is restrained when its guard starts: until then no guard of the detail is offered the carry, and the job is the one started by hand.)
                    if (condemned != a) DeathRowSystem.Restrain(condemned);
                }
                // (Placed so that the job's toils meet at once what they walk to: the guard beside the one to lift, the other two guards on their tables with the condemned.)
                if (!LdPutBeside(map, guardA, a))
                {
                    reloadProblems.Add("saveload: premise: no open cell beside the condemned to be carried");
                    r.Dispose();
                    return;
                }
                LdPutOn(map, b, tableB);
                LdPutOn(map, guardB, tableB);
                LdPutOn(map, stayed, tableS);
                LdPutOn(map, guardS, tableS);
                map.reachability.ClearCache();
                InmateCandidates.Shared.Invalidate();
                bool Carrying() => !a.Spawned && guardA.CurJobDef == RPDeathRowDefOf.RP_CarryOutExecution && guardA.carryTracker?.CarriedThing == a;
                bool Waiting(Pawn x, Pawn guard, Building table) => guard.CurJobDef == RPDeathRowDefOf.RP_CarryOutExecution && x.Spawned && x.Position == table.Position && guard.jobs.curDriver?.CurToilIndex == ProcedureToil;
                string Reached() => $"carrying {Carrying()} (guard on {guardA.CurJobDef?.defName}, toil {guardA.jobs.curDriver?.CurToilIndex}); waiting {Waiting(b, guardB, tableB)} (guard on {guardB.CurJobDef?.defName}, toil {guardB.jobs.curDriver?.CurToilIndex}, the condemned at {b.Position} for the table at {tableB.Position}) / " +
                                    $"{Waiting(stayed, guardS, tableS)} (guard on {guardS.CurJobDef?.defName}, toil {guardS.jobs.curDriver?.CurToilIndex}, the condemned at {stayed.Position} for the table at {tableS.Position})";
                // The three jobs are started by hand and put at their moment with no game tick (a tick moves every clock, job and walk the other seeds have set just before the save: the first version of this seed ticked up to four hundred, and the trusty's token,
                // the supermax walks, the care delivery and the transfer escort were all lost in the round trip): the two on the tables at the procedure's wait, the third's guard with the condemned in their arms on the way to the table.
                foreach ((Pawn condemned, Pawn guard, Building table) in new[] { (b, guardB, tableB), (stayed, guardS, tableS) })
                {
                    guard.jobs.StartJob(LdCarryJob(condemned, table), JobCondition.InterruptForced);
                    LdToWait(guard, condemned, table);
                }
                DeathRowSystem.Restrain(a);
                InmateCandidates.Shared.Invalidate();
                map.reachability.ClearCache();
                guardA.jobs.StartJob(LdCarryJob(a, tableA), JobCondition.InterruptForced);
                if (a.Spawned && guardA.carryTracker?.CarriedThing != a) LdToToil(guardA, 1);
                if (!Carrying() || !Waiting(b, guardB, tableB) || !Waiting(stayed, guardS, tableS))
                {
                    reloadProblems.Add($"saveload: premise: the last day's procedures were not put mid-carry / mid-wait without a tick: {Reached()}");
                    r.Dispose();
                    return;
                }
                float reputation0 = g.reputation;
                // The stay for the third, as the night's clock stood before it (the stay is only offered until then): the saved stage is what the guard's job meets after the load.
                stayed.Inmate().executionTick = LdScene.Now + GenDate.TicksPerHour;
                if (!LastDay.TryStay(stayed) || stayed.Inmate().execStage != ExecStage.None)
                {
                    reloadProblems.Add($"saveload: premise: the stay was not given to the third condemned ({LastDay.Describe(stayed.Inmate())})");
                    r.Dispose();
                    return;
                }
                ldCase = new LdSavedCase
                {
                    a = a.thingIDNumber, b = b.thingIDNumber, stayed = stayed.thingIDNumber, guardA = guardA.thingIDNumber, guardB = guardB.thingIDNumber, guardS = guardS.thingIDNumber,
                    tableA = tableA.thingIDNumber, tableB = tableB.thingIDNumber, tableS = tableS.thingIDNumber, executions0 = g.stats.executions, reputation0 = reputation0,
                };
                RPLog.Message($"[RimPrison:test] saveload last day: {guardA.LabelShort} carries {a.LabelShort} to the table (no tick), {guardB.LabelShort} is in the procedure for {b.LabelShort}, {guardS.LabelShort} for {stayed.LabelShort} who was given a stay");
            }
            catch (Exception e)
            {
                reloadProblems.Add($"saveload: the last day's procedures could not be seeded: {e}");
            }
        }

        private static Pawn LdSpawnedById(Map map, int id) => map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);

        private static Building LdTableById(Map map, int id) => map.listerBuildings.AllBuildingsColonistOfDef(RPDeathRowDefOf.RP_ExecutionTable).FirstOrDefault(x => x.thingIDNumber == id);

        /// <summary>
        /// After the load, with no tick: the guards are on the carry-out job still, the one carried is in the guard's arms (not spawned: the regime's cache of them is false) and the one on the table is on it, both authorised still, the
        /// third's stay is saved, and the final action's question is answered for the two (nothing stands in the way). The procedures themselves are judged by <see cref="FinishLastDayCaseAfterReload"/>.
        /// </summary>
        private static void CheckLastDayCaseAfterReload(Map map)
        {
            if (ldCase == null) return;
            LdSavedCase k = ldCase;
            Pawn guardA = LdSpawnedById(map, k.guardA), guardB = LdSpawnedById(map, k.guardB), guardS = LdSpawnedById(map, k.guardS), b = LdSpawnedById(map, k.b), stayed = LdSpawnedById(map, k.stayed);
            Pawn a = guardA?.carryTracker?.CarriedThing as Pawn;
            Building tableA = LdTableById(map, k.tableA), tableB = LdTableById(map, k.tableB);
            if (guardA == null || guardB == null || guardS == null || a == null || b == null || stayed == null || tableA == null || tableB == null || a.thingIDNumber != k.a)
            {
                reloadProblems.Add($"saveload last day: what the procedures were made of did not survive the load (guards {guardA != null}/{guardB != null}/{guardS != null}, the one in the arms {a != null}, the others {b != null}/{stayed != null}, tables {tableA != null}/{tableB != null})");
                ldCase = null;
                return;
            }
            // (The condemned are kept as pawns, not looked up again: a body held, hauled or shipped as a record is the same pawn.)
            (k.aPawn, k.bPawn, k.stayedPawn) = (a, b, stayed);
            ldGuards.Clear();
            ldGuards.AddRange(new[] { guardA, guardB, guardS });
            bool OnJob(Pawn guard) => guard.CurJobDef == RPDeathRowDefOf.RP_CarryOutExecution;
            if (!OnJob(guardA) || !OnJob(guardB) || !OnJob(guardS)) reloadProblems.Add($"saveload last day: the guards' carry-out jobs after the load: {OnJob(guardA)}/{OnJob(guardB)}/{OnJob(guardS)} (all three expected: the jobs are saved)");
            if (a.Spawned) reloadProblems.Add("saveload last day: the premise of the carry after a load: the one in the guard's arms is spawned");
            RPLog.Message($"[RimPrison:test] saveload last day: after the load the one in the arms has the regime's cache {a.Inmate().activeFlag} (false is what the custody test must not depend on)");
            if (a.Inmate().execStage != ExecStage.Authorised || b.Inmate().execStage != ExecStage.Authorised || stayed.Inmate().execStage != ExecStage.None || !stayed.Inmate().stayUsed)
                reloadProblems.Add($"saveload last day: the stages after the load: {LastDay.Describe(a.Inmate())} / {LastDay.Describe(b.Inmate())} / {LastDay.Describe(stayed.Inmate())} (authorised, authorised, none with the stay used expected)");
            string why = LastDay.Blocker(a, tableA, onTable: false), whyOnTable = LastDay.Blocker(b, tableB, onTable: true);
            if (why != null || whyOnTable != null) reloadProblems.Add($"saveload last day: after the load the carry is stopped: the one in the arms \"{why}\", the one on the table \"{whyOnTable}\" (neither expected)");
        }

        /// <summary>
        /// After the load, with the game ticked (before the checks that tick it for long): the guard's carry goes on, and the condemned who was in the guard's arms and the one on the table are each executed exactly once (one execution counted for each,
        /// one line in the chronicle for each); the one given a stay is not executed, the guard's job at the table stopped and said so. Then the seeded pawns and tables are taken away.
        /// </summary>
        private static void FinishLastDayCaseAfterReload(Map map)
        {
            if (ldCase == null) return;
            LdSavedCase k = ldCase;
            ldCase = null;
            PrisonGame g = PrisonGame.Instance;
            Pawn a = k.aPawn, b = k.bPawn, stayed = k.stayedPawn;
            Pawn[] guards = { LdSpawnedById(map, k.guardA), LdSpawnedById(map, k.guardB), LdSpawnedById(map, k.guardS) };
            try
            {
                if (a == null || b == null || stayed == null)
                {
                    reloadProblems.Add($"saveload last day: premise: the condemned of the procedures were not kept from the check after the load (the one carried {a != null}, on the table {b != null}, given the stay {stayed != null}): nothing can be judged");
                    return;
                }
                int ticked = LdTickUntil(() => a.Dead && b.Dead, 4000);
                // (A while longer: a second procedure for either would show.)
                LdTickUntil(() => false, 300);
                int executed = g.stats.executions - k.executions0;
                int LinesFor(Pawn x) => g.story.entries.Count(e => e.text == "RP_Chr_Executed".Translate(x.LabelShort).Resolve());
                RPLog.Message($"[RimPrison:test] saveload last day: after the load {a.LabelShort} dead {a.Dead} and {b.LabelShort} dead {b.Dead} ({ticked} ticks), executions counted {executed}, the stayed one {stayed.LabelShort} dead {stayed.Dead}");
                if (!a.Dead || !b.Dead) reloadProblems.Add($"saveload last day: after {ticked} ticks the one carried before the save is dead {a.Dead} and the one on the table {b.Dead} (both expected; guards on {guards[0]?.CurJobDef?.defName}/{guards[1]?.CurJobDef?.defName})");
                if (executed != 2 || LinesFor(a) != 1 || LinesFor(b) != 1) reloadProblems.Add($"saveload last day: executions counted {executed}, chronicle lines {LinesFor(a)}/{LinesFor(b)} (two, one and one expected: each once)");
                if (stayed.Dead || stayed.Inmate().execStage != ExecStage.None || LinesFor(stayed) != 0 || stayed.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained))
                    reloadProblems.Add($"saveload last day: the one given a stay before the save is dead {stayed.Dead}, {LastDay.Describe(stayed.Inmate())}, lines {LinesFor(stayed)} (alive, no stage, no execution expected)");
                string stopped = "RP_Chr_ExecutionStopped".Translate(stayed.LabelShort, "RP_ExecStop_NotAuthorised".Translate().Resolve()).Resolve();
                if (g.story.entries.Count(e => e.text == stopped) != 1) reloadProblems.Add($"saveload last day: the guard's stop for the stay was written {g.story.entries.Count(e => e.text == stopped)} times (once expected: \"{stopped}\")");
            }
            finally
            {
                foreach (Pawn x in new[] { a, b, stayed })
                {
                    if (x == null) continue;
                    if (x.Corpse is { Destroyed: false } body) body.Destroy();
                    if (!x.Destroyed) x.Destroy(DestroyMode.Vanish);
                }
                foreach (Pawn guard in guards)
                {
                    if (guard == null) continue;
                    g.home.Remove(guard);
                    if (!guard.Destroyed) guard.Destroy(DestroyMode.Vanish);
                }
                foreach (int id in new[] { k.tableA, k.tableB, k.tableS })
                    if (LdTableById(map, id) is { Destroyed: false } table) table.Destroy(DestroyMode.Vanish);
                StaffCareers.InvalidateLeads();
                g.reputation = k.reputation0;
                ldGuards.Clear();
            }
        }
    }
}
