using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The count (Security/Count.cs): an escape nobody saw is not known until a count finds the cell empty (or something sees the runaway), with the head start in the police's odds; one under a camera or a
    /// guard's eye is known at once; a count with no guard free is missed; an inmate out of place is written up; the HUD and the alert say what the map says; counts off is the old behaviour. Self-contained,
    /// on throwaway inmates and guards in the dark checks' scene (what watches the prison put aside, the ground lit), rolled back. Under <see cref="AutoTest.Active"/> the count is off unless a check turns it on
    /// (<see cref="CountSystem.TestOn"/>), so every older escape and stowaway check keeps its letter at once. The round trip of the count's state is in the saveload plan (<see cref="SeedCountForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void CountFail(string check, string what) => problems0.Add($"count ({check}): {what}");

        private static void CheckCount(Map map)
        {
            CheckCountTunnelUnseen(map);
            CheckCountTunnelUnderACamera(map);
            CheckCountStowaway(map);
            CheckCountMissed(map);
            CheckCountGuardJob(map);
            CheckCountOutOfPlace(map);
            CheckCountHudAndAlert(map);
            CheckCountHoursAndPolicy(map);
            CheckCountRoundTwo(map);
        }

        /// <summary>A random seed for which the first few rolls of a chance all come up (what a check needs so that a game roll it does not make itself is the one it wants).</summary>
        private static int CountRollingSeed(float chance)
        {
            for (int seed = 1; seed < 20000; seed++)
            {
                Rand.PushState(seed);
                bool all = Rand.Chance(chance) && Rand.Chance(chance) && Rand.Chance(chance);
                Rand.PopState();
                if (all) return seed;
            }
            return 1;
        }

        /// <summary>The dark checks' scene (an open spot in the yard, lit, nothing watching) with counts on, two a day, and the count's state the check's own (put back on dispose).</summary>
        private sealed class CountScene
        {
            public readonly DarkScene s;
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonGame g;
            public readonly Rollback r;

            private CountScene(DarkScene s, Rollback r, int perDay)
            {
                (this.s, this.r, map, pm, g) = (s, r, s.map, s.pm, PrisonGame.Instance);
                CountState state0 = pm.count;
                int perDay0 = g.countsPerDay;
                pm.count = new CountState();
                g.countsPerDay = perDay;
                CountSystem.TestOn = true;
                // (The check's own inmates are the ones a wing's count looks at: the live prison's are not written up by it.)
                CountSystem.OnlyThese = new HashSet<Pawn>();
                r.Add(() =>
                {
                    pm.count = state0;
                    g.countsPerDay = perDay0;
                    CountSystem.TestOn = false;
                    CountSystem.OnlyThese = null;
                    g.shuttle.stowaway = null;
                });
                // Lit around the spot (the sight of a guard or a camera is the lit one), and nothing watching.
                foreach (IntVec3 c in GenRadial.RadialCellsAround(s.cell, 4f, true)) s.Glow(1f, c);
                s.Watch();
            }

            public static CountScene TryMake(Map map, Rollback r, int perDay = CountSystem.DefaultPerDay)
            {
                DarkScene s = DarkScene.TryMake(map, r);
                return s == null ? null : new CountScene(s, r, perDay);
            }

            /// <summary>An inmate of the check's own, on their feet and processed, standing at <paramref name="at"/>.</summary>
            public Pawn Inmate(IntVec3 at)
            {
                Pawn p = Throwaway(map, r);
                (p.Inmate().processed, p.Inmate().awaitingSearch) = (true, false);
                Place(p, at);
                CountSystem.OnlyThese.Add(p);
                // (Anything the check puts in the books about them goes with them.)
                // (A runaway the police or the count kept as a world pawn is not kept once the check is done with them.)
                r.Add(() =>
                {
                    g.fugitives.RemoveAll(f => f.pawn == p);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                });
                return p;
            }

            public static void Place(Pawn p, IntVec3 c)
            {
                p.pather?.StopDead();
                p.Position = c;
                p.Notify_Teleported();
            }

            /// <summary>A runner who breaks for the map edge with nothing watching and gets there: held as an escape the count will find (their record, and what they were doing when they left).</summary>
            public Pawn GoneUnseen(EscapeWay way)
            {
                Pawn p = Inmate(s.cell);
                CountSystem.StartEscaping(p, "check", way);
                if (way == EscapeWay.Stowaway) g.shuttle.stowaway = p;
                p.ExitMap(false, Rot4.Invalid);
                return p;
            }

            /// <summary>A guard of the check's own, on the roster and on duty this hour.</summary>
            public Pawn Guard()
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.timetable?.SetAssignment(GenLocalDate.HourOfDay(map), TimeAssignmentDefOf.Work);
                return guard;
            }

            /// <summary>Calls a count now and has the guard walk every wing that wants it; false (told) when no wing has anyone on it.</summary>
            public bool CountEveryWing(Pawn guard)
            {
                CountSystem.Begin(map, pm, g, Find.TickManager.TicksGame);
                if (!pm.count.Running)
                {
                    RPLog.Message("[RimPrison:test] count: no wing has anyone on it, the count is not judged");
                    return false;
                }
                foreach (int idx in pm.count.wingsLeft.ToList()) CountSystem.WingCounted(guard, PrisonZoneDef.FromGridIndex((byte)idx));
                return true;
            }

            public IEnumerable<string> LettersTold() => r.NewLetters.Select(l => l.Label.Resolve() + " | " + (l as ChoiceLetter)?.Text.Resolve());

            public int Told(string label) => r.NewLetters.Count(l => l.Label.Resolve() == label);

            public int Lines(string text) => r.NewEntries.Count(e => e.text == text);
        }

        /// <summary>A prison cell with nobody in or beside it (a tunneller there takes no cellmates with them), with its bed; null when there is none.</summary>
        private static Building_Bed EmptyCellBed(Map map) =>
            map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().FirstOrDefault(b => b.Spawned && b.ForPrisoners && !b.Medical && b.GetRoom() is { IsPrisonCell: true } room
                && !room.ContainedAndAdjacentThings.OfType<Pawn>().Any());

        // ---- A tunnel nobody watches ------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A tunnel breakout at night with no watcher is unknown: no letter, no fine, nothing on the escape alert, not in the books, even when the runner reaches the edge. The six o'clock count finds the cell empty:
        /// the letter names them, the fine and the chronicle come then, the unaccounted list has them, and the head start the police are given is the six hours.
        /// </summary>
        private static void CheckCountTunnelUnseen(Map map)
        {
            Guarded("count: a tunnel nobody watches is unknown until the count", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                Building_Bed bed = EmptyCellBed(map);
                if (bed == null)
                {
                    RPLog.Message("[RimPrison:test] count: no empty prison cell for a tunnel, the unseen breakout is not judged");
                    return;
                }
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                Pawn p = t.Inmate(bed.Position);
                CompInmate c = p.Inmate();
                c.tunnelProgress = 1f;
                int escaped0 = g.stats.escaped, today0 = g.today.escapes, funds0 = g.ledger.funds, fugitives0 = g.fugitives.Count;
                TunnelSystem.TryBreakOut(p, c, force: true, exitAt: t.s.cell);
                if (!p.Spawned || p.Position != t.s.cell || p.MentalStateDef != RPMentalStateDefOf.RP_Escaping)
                {
                    CountFail("tunnel unseen", $"the check's own premise: the tunneller came up at {p.Position} (wanted {t.s.cell}), spawned {p.Spawned}, mental {p.MentalStateDef?.defName}");
                    return;
                }
                // Nobody has been told: not the tunnel's letter, not the begin letter of the escape, not the alert; the record is the count's.
                if (!CountSystem.IsHidden(p) || pm.count.hidden.Count != 1)
                    CountFail("tunnel unseen", $"a breakout with nothing watching the spot is not held: hidden {CountSystem.IsHidden(p)}, {pm.count.hidden.Count} record(s)");
                if (r.NewLetters.Any()) CountFail("tunnel unseen", "the warden was told of a breakout nobody saw: " + string.Join(" // ", t.LettersTold()));
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (PrisonAlerts.Escaping.Contains(p)) CountFail("tunnel unseen", "the escape alert names a runner nobody saw");
                // They reach the edge of the map, still unseen: gone, and still not in the books.
                p.ExitMap(false, Rot4.Invalid);
                HiddenEscape h = pm.count.HiddenOf(p);
                if (h == null || !h.Exited || p.Spawned) CountFail("tunnel unseen", $"a runner who left the map unseen has no record of it (record {h != null}, exited {h?.Exited}, still spawned {p.Spawned})");
                if (g.stats.escaped != escaped0 || g.today.escapes != today0 || g.ledger.funds != funds0 || g.fugitives.Count != fugitives0 || r.NewLetters.Any() || r.NewEntries.Any(e => e.text == "RP_Chr_Escaped".Translate(p.LabelShort).Resolve()))
                    CountFail("tunnel unseen", $"an escape nobody saw is in the books before any count: escaped {escaped0} -> {g.stats.escaped}, funds {funds0} -> {g.ledger.funds}, fugitives {fugitives0} -> {g.fugitives.Count}, told {string.Join(" // ", t.LettersTold())}");
                if (h == null) return;
                // Six hours on (the record is moved back: a check cannot wait them out), the count is called and a guard walks the wing the runner belonged to.
                const int Six = 6 * GenDate.TicksPerHour;
                (h.startTick, h.exitTick) = (h.startTick - Six, h.exitTick - Six);
                Pawn guard = t.Guard();
                // (The police's roll is the game's: the count's books make the fugitive record only when it comes up. The check sets the random state to one where the first rolls all do, so that the record is the one the count made.)
                int exitedAt = h.exitTick;
                int seed = CountRollingSeed(CountSystem.RecaptureChanceAfter(Find.TickManager.TicksGame - exitedAt));
                bool counted;
                Rand.PushState(seed);
                try
                {
                    counted = t.CountEveryWing(guard);
                }
                finally
                {
                    Rand.PopState();
                }
                if (!counted) return;
                if (pm.count.hidden.Count != 0 || pm.count.unaccounted.Count != 1 || pm.count.unaccounted[0].pawnId != p.thingIDNumber)
                    CountFail("tunnel unseen", $"the count did not find the runner: {pm.count.hidden.Count} still hidden, unaccounted {string.Join(",", pm.count.unaccounted.Select(e => e.name))} (wanted {p.LabelShort})");
                if (g.stats.escaped != escaped0 + 1 || g.today.escapes != today0 + 1 || g.ledger.funds != funds0 - EscapeUtility.EscapeFine)
                    CountFail("tunnel unseen", $"the count did not open the books: escaped {escaped0} -> {g.stats.escaped}, today {today0} -> {g.today.escapes}, funds {funds0} -> {g.ledger.funds} (the fine is {EscapeUtility.EscapeFine})");
                int shorts = t.Told("RP_Count_ShortLabel".Translate().Resolve());
                string text = t.LettersTold().FirstOrDefault(l => l.StartsWith("RP_Count_ShortLabel".Translate().Resolve())) ?? "";
                if (shorts != 1 || !text.Contains(p.LabelShort) || !text.Contains("6 hours"))
                    CountFail("tunnel unseen", $"the count's letter should name {p.LabelShort} and say six hours, {shorts} letter(s) of it: {text}");
                if (t.Lines("RP_Chr_CountShort".Translate(p.LabelShort).Resolve()) != 1 || t.Lines("RP_Chr_Escaped".Translate(p.LabelShort).Resolve()) != 0)
                    CountFail("tunnel unseen", "the chronicle should have the count's one line for the escape and not the generic one");
                // What the police were given: the odds fall with every hour of the head start, and the record the count's own books made (not one made by calling the police's own routine) carries it.
                float now0 = CountSystem.RecaptureChanceAfter(0), six = CountSystem.RecaptureChanceAfter(Six);
                if (!Mathf.Approximately(now0, FugitiveSystem.RecaptureChance) || six >= now0 || six <= 0f)
                    CountFail("tunnel unseen", $"the odds of getting a runaway back are {now0} with no head start and {six} with six hours (they must start at {FugitiveSystem.RecaptureChance} and fall)");
                Fugitive record = g.fugitives.FirstOrDefault(f => f.pawn == p);
                if (record == null) RPLog.Message("[RimPrison:test] count: the police's roll did not come up in the check's random state, the head start on the record is not judged");
                else if (record.escapedTick != exitedAt || record.headStart < Six || record.headStart > Six + GenDate.TicksPerHour / 4)
                    CountFail("tunnel unseen", $"the record the count made has a head start of {record.headStart} ticks (wanted about {Six}) and a getaway at {record.escapedTick} (wanted {exitedAt}, when they left the map)");
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (PrisonAlerts.Unaccounted.Count != 1) CountFail("tunnel unseen", $"the unaccounted alert lists {PrisonAlerts.Unaccounted.Count} (wanted 1)");
                RPLog.Message("[RimPrison:test] count: a tunnel breakout nobody watched was unknown (no letter, no fine, no alert) until the count, which named the runner with a six hours' head start");
            });
        }

        /// <summary>The same breakout where a camera covers the spot is known at once: the tunnel's letter, the alert, and the escape in the books when they leave the map.</summary>
        private static void CheckCountTunnelUnderACamera(Map map)
        {
            Guarded("count: a tunnel under a camera is known at once", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                Building_Bed bed = EmptyCellBed(map);
                if (bed == null)
                {
                    RPLog.Message("[RimPrison:test] count: no empty prison cell for a tunnel, the breakout under a camera is not judged");
                    return;
                }
                t.s.Watch(cam: true);
                if (!TensionUtility.SeenAt(t.s.cell, map, t.pm))
                {
                    RPLog.Message("[RimPrison:test] count: the scene's camera does not cover the spot, the breakout under a camera is not judged");
                    return;
                }
                PrisonGame g = t.g;
                Pawn p = t.Inmate(bed.Position);
                CompInmate c = p.Inmate();
                c.tunnelProgress = 1f;
                int escaped0 = g.stats.escaped, funds0 = g.ledger.funds;
                TunnelSystem.TryBreakOut(p, c, force: true, exitAt: t.s.cell);
                if (CountSystem.IsHidden(p) || t.pm.count.hidden.Count != 0) CountFail("tunnel camera", "a breakout the camera covers is held as unseen");
                if (t.Told("RP_TunnelBreakoutLabel".Translate().Resolve()) != 1) CountFail("tunnel camera", "the camera saw the breakout and the warden was not told at once: " + string.Join(" // ", t.LettersTold()));
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (!PrisonAlerts.Escaping.Contains(p)) CountFail("tunnel camera", "the escape alert does not name a runner the camera saw");
                p.ExitMap(false, Rot4.Invalid);
                if (g.stats.escaped != escaped0 + 1 || g.ledger.funds != funds0 - EscapeUtility.EscapeFine || t.Told("RP_EscapedLabel".Translate().Resolve()) != 1)
                    CountFail("tunnel camera", $"a seen escape is not in the books at once: escaped {escaped0} -> {g.stats.escaped}, funds {funds0} -> {g.ledger.funds}, {t.Told("RP_EscapedLabel".Translate().Resolve())} escape letter(s)");
                RPLog.Message("[RimPrison:test] count: a tunnel breakout under a camera was known at once (the tunnel's letter, the alert, the escape in the books)");
            });
        }

        // ---- The stowaway --------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A stowaway a guard has in sight is known at once (the escape's letter, the fine, the stowaway's own chronicle line); one nobody sees is held until the count, which tells it with one chronicle line
        /// (the count's: not the generic escape line, not the stowaway's as well) and the fine then.
        /// </summary>
        private static void CheckCountStowaway(Map map)
        {
            Guarded("count: a stowaway seen is known at once, one unseen at the count", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                // A guard has the runner in sight: told at once, as a stowaway always was.
                t.s.Watch(near: true);
                Pawn seen = t.Inmate(t.s.cell);
                int escaped0 = g.stats.escaped, away0 = g.stats.stowawaysAway, funds0 = g.ledger.funds;
                CountSystem.StartEscaping(seen, "check", EscapeWay.Stowaway);
                if (CountSystem.IsHidden(seen)) CountFail("stowaway", "a stowaway a guard has in sight is held as unseen");
                g.shuttle.stowaway = seen;
                seen.ExitMap(false, Rot4.Invalid);
                if (g.stats.escaped != escaped0 + 1 || g.stats.stowawaysAway != away0 + 1 || g.ledger.funds != funds0 - EscapeUtility.EscapeFine || t.Told("RP_EscapedLabel".Translate().Resolve()) != 1
                    || t.Lines("RP_Chr_StowawayAway".Translate(seen.LabelShort).Resolve()) != 1 || t.Lines("RP_Chr_Escaped".Translate(seen.LabelShort).Resolve()) != 0)
                    CountFail("stowaway", $"a stowaway seen going is not told at once, once: escaped {escaped0} -> {g.stats.escaped}, away {away0} -> {g.stats.stowawaysAway}, funds {funds0} -> {g.ledger.funds}, letters {t.Told("RP_EscapedLabel".Translate().Resolve())}, own line {t.Lines("RP_Chr_StowawayAway".Translate(seen.LabelShort).Resolve())}");
                // Nobody sees the next: aboard, and not in the books (the shuttle's own statistics are the shuttle's, and counted).
                t.s.Watch();
                int letters0 = r.NewLetters.Count();
                (escaped0, away0, funds0) = (g.stats.escaped, g.stats.stowawaysAway, g.ledger.funds);
                Pawn hidden = t.GoneUnseen(EscapeWay.Stowaway);
                if (g.stats.stowawaysAway != away0 + 1) CountFail("stowaway", $"the stowaway's own statistic is not counted when they get aboard (away {away0} -> {g.stats.stowawaysAway})");
                if (g.stats.escaped != escaped0 || g.ledger.funds != funds0 || r.NewLetters.Count() != letters0 || t.Lines("RP_Chr_StowawayAway".Translate(hidden.LabelShort).Resolve()) != 0 || pm.count.HiddenOf(hidden) is not { Exited: true })
                    CountFail("stowaway", $"a stowaway nobody saw is told before the count: escaped {escaped0} -> {g.stats.escaped}, funds {funds0} -> {g.ledger.funds}, letters {letters0} -> {r.NewLetters.Count()}, record {pm.count.HiddenOf(hidden) is { Exited: true }}");
                Pawn guard = t.Guard();
                if (!t.CountEveryWing(guard)) return;
                if (g.stats.escaped != escaped0 + 1 || t.Told("RP_Count_ShortLabel".Translate().Resolve()) != 1 || pm.count.unaccounted.All(e => e.pawnId != hidden.thingIDNumber)
                    || t.Lines("RP_Chr_CountShort".Translate(hidden.LabelShort).Resolve()) != 1 || t.Lines("RP_Chr_StowawayAway".Translate(hidden.LabelShort).Resolve()) != 0 || t.Lines("RP_Chr_Escaped".Translate(hidden.LabelShort).Resolve()) != 0)
                    CountFail("stowaway", $"the count did not tell the stowaway once: escaped {escaped0} -> {g.stats.escaped}, letters {t.Told("RP_Count_ShortLabel".Translate().Resolve())}, count line {t.Lines("RP_Chr_CountShort".Translate(hidden.LabelShort).Resolve())}, own line {t.Lines("RP_Chr_StowawayAway".Translate(hidden.LabelShort).Resolve())}, generic {t.Lines("RP_Chr_Escaped".Translate(hidden.LabelShort).Resolve())}");
                RPLog.Message("[RimPrison:test] count: a stowaway a guard saw was told at once; one nobody saw was told by the count, in one line");
            });
        }

        // ---- A count with no guard free ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A count that no guard walks (none free in the hour: off duty or at home) is missed: a line in the chronicle and the log, the HUD says so, and nothing is cleared or found (an escape in the meantime stays unknown until the next count).
        /// A guard at home is sent nowhere.
        /// </summary>
        private static void CheckCountMissed(Map map)
        {
            Guarded("count: a count with no guard free is missed", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                Pawn gone = t.GoneUnseen(EscapeWay.Walked);
                int escaped0 = g.stats.escaped;
                CountSystem.Begin(map, pm, g, Find.TickManager.TicksGame);
                if (!pm.count.Running)
                {
                    CountFail("missed", "the check's own premise: a count called with a runner of the wing hidden has no wing to walk");
                    return;
                }
                Pawn away = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                pm.count.dueTick = Find.TickManager.TicksGame;
                if (CountSystem.JobFor(away) != null) CountFail("missed", "a guard at home is sent to count a wing");
                // (A guard at home is off the map and is sent nowhere before any rule is asked: the rule is for one on site who is off shift, against the same guard on shift.)
                Pawn resting = t.Guard();
                CountSystem.lastTry.Clear();
                if (CountSystem.JobFor(resting) == null) RPLog.Message("[RimPrison:test] count: the guard on shift is given no wing, the guard off shift is not judged");
                else
                {
                    resting.timetable.SetAssignment(GenLocalDate.HourOfDay(map), TimeAssignmentDefOf.Sleep);
                    CountSystem.lastTry.Clear();
                    if (CountSystem.JobFor(resting) != null) CountFail("missed", "a guard on site but off shift is sent to count a wing");
                }
                // The hour goes by with nobody on a wing.
                (pm.count.startedTick, pm.count.dueTick) = (pm.count.startedTick - CountSystem.WindowTicks - 1, pm.count.dueTick - CountSystem.WindowTicks - 1);
                CountSystem.Tick(map, pm);
                if (pm.count.Running || pm.count.lastMissedTick < 0 || pm.count.lastClearedTick >= 0 || pm.count.unaccounted.Count != 0)
                    CountFail("missed", $"a count nobody walked is not a miss: running {pm.count.Running}, missed at {pm.count.lastMissedTick}, cleared at {pm.count.lastClearedTick}, unaccounted {pm.count.unaccounted.Count}");
                if (t.Lines("RP_Chr_CountMissed".Translate(CountSystem.ClockAt(map, pm.count.lastMissedTick)).Resolve()) != 1) CountFail("missed", "the missed count has no line in the chronicle");
                // The risk: the runner is still unknown, the books as they were.
                if (pm.count.hidden.Count != 1 || g.stats.escaped != escaped0 || r.NewLetters.Any(l => l.Label.Resolve() == "RP_Count_ShortLabel".Translate().Resolve()))
                    CountFail("missed", $"a count nobody walked found the runner: {pm.count.hidden.Count} hidden, escaped {escaped0} -> {g.stats.escaped}");
                string line = CountSystem.HudLine(map, pm, g, out bool warn);
                if (line != "RP_HudCountMissed".Translate(CountSystem.ClockAt(map, pm.count.lastMissedTick)).Resolve() || !warn) CountFail("missed", $"the HUD says \"{line}\" (warning {warn}) of a missed count");
                if (gone.Spawned) CountFail("missed", "the check's own premise: the runner left the map");
                RPLog.Message("[RimPrison:test] count: a count no guard walked was missed (a line, the HUD), and the runner stayed unknown");
            });
        }

        /// <summary>
        /// The guard's job: nobody is sent before the inmates have settled, a guard on duty is given the wing with the warden's walk plan as its stops (the wing's index on the job), nobody who is at home is.
        /// Told, not failed, when the prison's wings cannot be walked from the intake.
        /// </summary>
        private static void CheckCountGuardJob(Map map)
        {
            Guarded("count: a guard on duty walks a wing", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonMap pm = t.pm;
                // (Some inmate on a wing, so that there is one to walk: the live prison's, or a runner of the check's.)
                t.GoneUnseen(EscapeWay.Walked);
                CountSystem.Begin(t.map, pm, t.g, Find.TickManager.TicksGame);
                if (!pm.count.Running)
                {
                    CountFail("guard job", "the check's own premise: a count called with a runner of the wing hidden has no wing to walk");
                    return;
                }
                Pawn guard = t.Guard();
                if (CountSystem.JobFor(guard) != null) CountFail("guard job", "a guard is sent to walk a wing before the inmates have had their time to settle");
                pm.count.dueTick = Find.TickManager.TicksGame;
                Job job = CountSystem.JobFor(guard);
                if (job == null)
                {
                    RPLog.Message($"[RimPrison:test] count: the guard (on duty {GuardPosts.OnDuty(guard)}) is given no wing from {guard.Position}, the guard's job is not judged");
                    return;
                }
                if (job.def != RPCountDefOf.RP_Count || !pm.count.wingsLeft.Contains(job.count) || job.targetQueueA.NullOrEmpty())
                    CountFail("guard job", $"the guard is given {job.def.defName} for wing {job.count} with {job.targetQueueA?.Count ?? 0} stops (the wings left are {string.Join(",", pm.count.wingsLeft)})");
                List<IntVec3> plan = WardenSystem.WalkPlan(guard, PrisonZoneDef.FromGridIndex((byte)job.count));
                if (plan.Count != job.targetQueueA.Count || plan.Where((c, i) => c != job.targetQueueA[i].Cell).Any()) CountFail("guard job", "the stops of the count are not the warden's walk plan of the wing");
                // The wing is the guard's to walk once, by the guard who is on it: an interrupted walk leaves it for the next, a finished one takes it off the list.
                int left0 = pm.count.wingsLeft.Count;
                CountSystem.WingCounted(guard, PrisonZoneDef.FromGridIndex((byte)job.count));
                if (pm.count.wingsLeft.Count != (pm.count.Running ? left0 - 1 : 0)) CountFail("guard job", $"a wing that was walked is still wanted ({left0} left before, {pm.count.wingsLeft.Count} after)");
                RPLog.Message("[RimPrison:test] count: a guard on duty was given a wing to walk by the warden's walk plan, and none before the inmates had settled");
            });
        }

        // ---- Out of place --------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// At the count an inmate of the wing who is where the prison has not put them is written up (a mild misconduct, seen: evidence, whoever watches), and one in their own cell, or in a place the prison put them, is not. Both are
        /// counted (<see cref="CompInmate.lastCountedTick"/>). Told, not failed, when no wing has a free bed for the check's inmates.
        /// </summary>
        private static void CheckCountOutOfPlace(Map map)
        {
            Guarded("count: an inmate out of place is written up", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(map, level).Where(b => b.GetRoom() is { IsPrisonCell: true } && pm.zones.ZoneAt(b.Position)?.security == level).Take(2).ToList();
                if (free.Count < 2)
                {
                    RPLog.Message("[RimPrison:test] count: fewer than two free cell beds in a wing of the default level, the write-up is not judged");
                    return;
                }
                Pawn inCell = t.Inmate(free[0].Position), astray = t.Inmate(t.s.cell);
                foreach ((Pawn p, Building_Bed bed) in new[] { (inCell, free[0]), (astray, free[1]) })
                {
                    p.ownership.ClaimBedIfNonMedical(bed);
                    Pawn captured = p;
                    r.Add(() => captured.ownership?.UnclaimBed());
                }
                // A lockdown: the cell is where the regime wants everyone, whatever the hour is, in this slot and in the one before it.
                int lockdown0 = g.lockdownUntilTick;
                g.lockdownUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerHour;
                r.Add(() => g.lockdownUntilTick = lockdown0);
                t.s.Watch();
                int misconducts0 = g.stats.misconducts;
                Pawn guard = t.Guard();
                if (!t.CountEveryWing(guard)) return;
                CompInmate ac = astray.Inmate(), cc = inCell.Inmate();
                MisconductRecord last = ac.records.LastOrDefault();
                if (last == null || last.def != RPCountDefOf.RP_OutOfPlace || !last.seen)
                    CountFail("out of place", $"an inmate of the wing standing in the yard at the count was not written up as out of place and seen (last record {last?.def?.defName}, seen {last?.seen})");
                if (cc.records.Any(rec => rec.def == RPCountDefOf.RP_OutOfPlace)) CountFail("out of place", "an inmate in their own cell was written up");
                if (g.stats.misconducts != misconducts0 || pm.count.writeUps != 1)
                    CountFail("out of place", $"the misconducts went {misconducts0} -> {g.stats.misconducts} (a mark on the file is not counted) and the count wrote up {pm.count.writeUps} (wanted none more, and one)");
                int now = Find.TickManager.TicksGame;
                if (ac.lastCountedTick != now || cc.lastCountedTick != now) CountFail("out of place", $"the inmates of the wing were not both counted (last counted {cc.lastCountedTick} and {ac.lastCountedTick}, now {now})");
                if (RPCountDefOf.RP_OutOfPlace.baseWeight > 0f || RPCountDefOf.RP_OutOfPlace.severity > 1 || !RPCountDefOf.RP_OutOfPlace.mark) CountFail("out of place", "the write-up is not a mild mark nobody commits at random");

                // The same inmate, counted again in the places and states the prison put them in: not written up (each against the control that they are, standing in the yard).
                bool WrittenUp()
                {
                    pm.count.writeUps = 0;
                    return t.CountEveryWing(guard) && pm.count.writeUps > 0;
                }
                void Back()
                {
                    astray.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                    astray.pather.StopDead();
                    CountScene.Place(astray, t.s.cell);
                }
                void Case(string what, System.Func<bool> setUp)
                {
                    Back();
                    if (!setUp())
                    {
                        RPLog.Message($"[RimPrison:test] count: could not put the inmate {what}, not judged");
                        Back();
                        return;
                    }
                    if (WrittenUp()) CountFail("out of place", $"an inmate {what} was written up as out of place");
                    Back();
                    if (!WrittenUp()) CountFail("out of place", $"the control after {what}: the inmate standing in the yard at lockdown was not written up (the case would pass for the wrong reason)");
                }
                Case("on the way to where the regime sends them", () =>
                {
                    IntVec3 goal = GenRadial.RadialCellsAround(t.s.cell, 6f, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && astray.CanReach(c, PathEndMode.OnCell, Danger.Some));
                    if (!goal.IsValid || goal == default) return false;
                    astray.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, goal), JobCondition.InterruptForced);
                    return astray.pather.Moving;
                });
                Case("held by staff for a session", () =>
                {
                    astray.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 600), JobCondition.InterruptForced);
                    return astray.CurJobDef == JobDefOf.Wait_MaintainPosture;
                });
                foreach (PrisonZoneDef zone in new[] { RPSecurityDefOf.RP_SolitaryZone, RPDefOf.RP_Holding })
                {
                    IntVec3 inside = pm.zones.CellsOf(zone).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null);
                    Case("in " + zone.defName, () =>
                    {
                        if (!inside.IsValid || inside == default) return false;
                        CountScene.Place(astray, inside);
                        return true;
                    });
                }
                Case("waiting to be searched", () =>
                {
                    ac.awaitingSearch = true;
                    return true;
                });
                ac.awaitingSearch = false;

                // What a write-up does and does not do to the file: it is a mark (see MisconductDef.mark). It does not push an older record out of a full file, restart the clean days, bar a mentor, count in the statistics, or
                // stand for the offence a restraint was for.
                Back();
                int now2 = Find.TickManager.TicksGame;
                ac.records.Clear();
                for (int i = 0; i < CompInmate.MaxRecords - 1; i++) ac.AddRecord(new MisconductRecord { def = RPShuttleDefOf.RP_EscapeAttempt, tick = now2 - 20 * GenDate.TicksPerDay + i, seen = true });
                int severe0 = ac.records.Count(rec => rec.def == RPShuttleDefOf.RP_EscapeAttempt);
                int board0 = g.today.misconducts, stats0 = g.stats.misconducts;
                for (int i = 0; i < 3; i++) WrittenUp();
                if (ac.records.Count(rec => rec.def == RPShuttleDefOf.RP_EscapeAttempt) != severe0 || ac.records.Count > CompInmate.MaxRecords || !ac.records.Any(rec => rec.def == RPCountDefOf.RP_OutOfPlace))
                    CountFail("out of place", $"write-ups pushed the file's older records out: {severe0} serious before, {ac.records.Count(rec => rec.def == RPShuttleDefOf.RP_EscapeAttempt)} after, {ac.records.Count} in the file (at most {CompInmate.MaxRecords}), a mark kept {ac.records.Any(rec => rec.def == RPCountDefOf.RP_OutOfPlace)}");
                if (g.today.misconducts != board0 || g.stats.misconducts != stats0) CountFail("out of place", "write-ups were counted as misconducts on the desk board or in the statistics");
                if (ac.MisbehavedWithin(3 * GenDate.TicksPerDay)) CountFail("out of place", "a write-up counts as misbehaving (a mentor is barred for three days after it)");
                if (SupermaxSystem.LastSeenMisconduct(ac) >= now2 - 10 * GenDate.TicksPerDay) CountFail("out of place", "a write-up restarts the supermax step-down's clean days");
                if (ac.PolicyHoursForLatest(true) != CompInmate.ResistingHours) CountFail("out of place", $"a write-up within the hour is the offence a restraint was for: the hours in the hole are {ac.PolicyHoursForLatest(true)} (wanted {CompInmate.ResistingHours}, for resisting)");
                // The other door: a real record added to a full file gives up a mark, never the oldest record while a mark is in the file; with no mark in it the oldest record goes, as ever.
                ac.records.Clear();
                int oldestTick = now2 - 30 * GenDate.TicksPerDay;
                ac.AddRecord(new MisconductRecord { def = RPShuttleDefOf.RP_EscapeAttempt, tick = oldestTick, seen = true });
                for (int i = 0; i < CompInmate.MaxRecords - 1; i++) ac.AddRecord(new MisconductRecord { def = RPCountDefOf.RP_OutOfPlace, tick = now2 - 20 * GenDate.TicksPerDay + i, seen = true });
                ac.AddRecord(new MisconductRecord { def = RPShuttleDefOf.RP_EscapeAttempt, tick = now2, seen = true });
                int marks = ac.records.Count(rec => rec.def.mark);
                if (ac.records.Count != CompInmate.MaxRecords || !ac.records.Any(rec => rec.tick == oldestTick) || !ac.records.Any(rec => rec.tick == now2) || marks != CompInmate.MaxRecords - 2)
                    CountFail("out of place", $"a record added to a full file of marks pushed out the oldest record, not a mark ({ac.records.Count} in the file, oldest record kept {ac.records.Any(rec => rec.tick == oldestTick)}, new one kept {ac.records.Any(rec => rec.tick == now2)}, {marks} marks of {CompInmate.MaxRecords - 2} wanted)");
                ac.records.Clear();
                for (int i = 0; i < CompInmate.MaxRecords; i++) ac.AddRecord(new MisconductRecord { def = RPShuttleDefOf.RP_EscapeAttempt, tick = oldestTick + i, seen = true });
                ac.AddRecord(new MisconductRecord { def = RPShuttleDefOf.RP_EscapeAttempt, tick = now2, seen = true });
                if (ac.records.Count != CompInmate.MaxRecords || ac.records.Any(rec => rec.tick == oldestTick) || !ac.records.Any(rec => rec.tick == now2))
                    CountFail("out of place", "a record added to a full file of records did not push out the oldest");
                RPLog.Message("[RimPrison:test] count: the inmate in the yard was written up (mild, seen), the one in their cell was not, both were counted");
            });
        }

        // ---- The HUD and the alert -----------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The HUD's line is what the map's count says: "count: N unaccounted" with N what the map lists (the alert lists them too, by name), and "count cleared at" the hour the next count ended with nobody missing.</summary>
        private static void CheckCountHudAndAlert(Map map)
        {
            Guarded("count: the HUD line and the alert are what the map says", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                PrisonMap pm = t.pm;
                string next = CountSystem.HudLine(map, pm, g, out bool warn0);
                if (next != "RP_HudCountNext".Translate(CountSystem.NextHour(g, GenLocalDate.HourOfDay(map)).ToString("00")).Resolve() || warn0) CountFail("HUD", $"before any count the HUD says \"{next}\" (warning {warn0}) and not when the next one is");
                Pawn gone = t.GoneUnseen(EscapeWay.Walked);
                Pawn guard = t.Guard();
                if (!t.CountEveryWing(guard)) return;
                CountState c = pm.count;
                string line = CountSystem.HudLine(map, pm, g, out bool warn);
                if (c.unaccounted.Count != 1 || line != "RP_HudCountShort".Translate(c.unaccounted.Count).Resolve() || !warn)
                    CountFail("HUD", $"the map lists {c.unaccounted.Count} unaccounted and the HUD says \"{line}\" (warning {warn})");
                List<string> lines = CountSystem.UnaccountedLines(map, pm);
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                var alert = new Alert_RP_Unaccounted();
                if (lines.Count != c.unaccounted.Count || PrisonAlerts.Unaccounted.Count != c.unaccounted.Count || !PrisonAlerts.Unaccounted.Any(l => l.Contains(gone.LabelShort)) || !alert.GetReport().active)
                    CountFail("alert", $"the map lists {c.unaccounted.Count} unaccounted, the screens' lines {lines.Count}, the alert's {PrisonAlerts.Unaccounted.Count} (naming {gone.LabelShort}: {PrisonAlerts.Unaccounted.Any(l => l.Contains(gone.LabelShort))}), the alert reports {alert.GetReport().active}");
                // Named apart from the alert for the rooms the prison lacks.
                if ("RP_Alert_Unaccounted".Translate().Resolve() == "RP_Alert_Missing".Translate().Resolve()) CountFail("alert", "the unaccounted alert has the missing rooms' name");
                // The next count finds everyone: cleared, and the HUD says at what time.
                if (!t.CountEveryWing(guard)) return;
                string cleared = CountSystem.HudLine(map, pm, g, out bool warn2);
                if (c.unaccounted.Count != 0 || c.lastClearedTick != Find.TickManager.TicksGame || cleared != "RP_HudCountCleared".Translate(CountSystem.ClockAt(map, c.lastClearedTick)).Resolve() || warn2)
                    CountFail("HUD", $"a count that found everyone: unaccounted {c.unaccounted.Count}, cleared at {c.lastClearedTick} (now {Find.TickManager.TicksGame}), the HUD says \"{cleared}\" (warning {warn2})");
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (PrisonAlerts.Unaccounted.Count != 0) CountFail("alert", "the unaccounted alert is still up after a count that found everyone");
                // The row's order: who is unaccounted first, a missed count next, a count running after either (never in its place), alone when nothing is wrong.
                int hudNow = Find.TickManager.TicksGame;
                var hudRun = new CountState { startedTick = hudNow, dueTick = hudNow, wingsLeft = new List<int> { 0, 1 } };
                pm.count = hudRun;
                string hudCounting = CountSystem.HudLine(map, pm, g, out bool hudWarn1);
                if (hudCounting != "RP_HudCounting".Translate(2).Resolve() || hudWarn1) CountFail("HUD", $"a count running alone: the HUD says \"{hudCounting}\" (warning {hudWarn1})");
                hudRun.lastMissedTick = hudNow - 100;
                string hudMissed = CountSystem.HudLine(map, pm, g, out bool hudWarn2);
                if (hudMissed != "RP_HudCountAlso".Translate("RP_HudCountMissed".Translate(CountSystem.ClockAt(map, hudRun.lastMissedTick)), 2).Resolve() || !hudWarn2)
                    CountFail("HUD", $"a missed count and one running: the HUD says \"{hudMissed}\" (warning {hudWarn2}), the missed count first and the running one after it was wanted");
                hudRun.unaccounted.Add(new CountEntry { pawnId = 900001, name = "Marek", place = "in the yard", escapedTick = hudNow });
                string hudShort = CountSystem.HudLine(map, pm, g, out bool hudWarn3);
                if (hudShort != "RP_HudCountAlso".Translate("RP_HudCountShort".Translate(1), 2).Resolve() || !hudWarn3)
                    CountFail("HUD", $"one unaccounted, a count missed and one running: the HUD says \"{hudShort}\" (warning {hudWarn3}), the unaccounted first and the running count after it was wanted");
                hudRun.wingsLeft.Clear();
                hudRun.startedTick = hudRun.dueTick = -1;
                string hudQuiet = CountSystem.HudLine(map, pm, g, out bool hudWarn4);
                if (hudQuiet != "RP_HudCountShort".Translate(1).Resolve() || !hudWarn4) CountFail("HUD", $"one unaccounted and no count running: the HUD says \"{hudQuiet}\" (warning {hudWarn4})");
                RPLog.Message("[RimPrison:test] count: the HUD says \"count: 1 unaccounted\" with the map's one, the alert names them, and \"count cleared\" after the next count; unaccounted, then missed, then counting");
            });
        }

        // ---- Counts a day, and none ---------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The count hours: one to four a day, the lockup hour first (the regime's, wherever it is put) and six second; none at nought. With counts off, or off under a test, an escape is known at once as ever (no record,
        /// the tunnel's letter and the escape's letter at once).
        /// </summary>
        private static void CheckCountHoursAndPolicy(Map map)
        {
            Guarded("count: counts a day follow the regime, and none is as before", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonGame g = t.g;
                int perDay0 = g.countsPerDay;
                RegimeSchedule sched = g.RegimeFor(InmateUtility.DefaultSecurity);
                List<RegimeActivityDef> slots0 = sched.slots.ToList();
                r.Add(() => sched.slots = slots0);
                // A day as runs of hours: (from, to, what), the rest lockup.
                List<RegimeActivityDef> Day(params (int from, int to, RegimeActivityDef def)[] runs)
                {
                    var day = Enumerable.Repeat(RPDefOf.RP_Lockup, 24).ToList();
                    foreach ((int from, int to, RegimeActivityDef def) in runs)
                        for (int h = from; h <= to; h++) day[h] = def;
                    return day;
                }
                // The default regime (Medium): lockup at midday and again in the evening, sleep from 23 and from midnight, free time and a shower between. The night's count is the evening's lockup, not the midday one.
                RegimeActivityDef eat = DefDatabase<RegimeActivityDef>.GetNamed("RP_Eat");
                List<RegimeActivityDef> medium = Day((0, 5, RPDefOf.RP_Sleep), (6, 6, RPDefOf.RP_ShowerTime), (7, 7, eat), (8, 11, RPLaborDefOf.RP_Work), (12, 12, eat), (13, 14, RPDefOf.RP_Lockup),
                    (15, 16, RPDefOf.RP_FreeTime), (17, 17, RPLaborDefOf.RP_Work), (18, 20, RPDefOf.RP_FreeTime), (21, 21, eat), (22, 22, RPDefOf.RP_Lockup), (23, 23, RPDefOf.RP_Sleep));
                sched.slots = medium.ToList();
                int[][] wanted = { new int[0], new[] { 22 }, new[] { 6, 22 }, new[] { 6, 14, 22 }, new[] { 2, 6, 14, 22 } };
                for (int n = 0; n <= CountSystem.MaxPerDay; n++)
                {
                    g.countsPerDay = n;
                    List<int> hours = CountSystem.Hours(g);
                    if (!hours.SequenceEqual(wanted[n])) CountFail("hours", $"{n} a day with the default regime's lockup at 22:00 (and the midday one at 13:00) gives the hours {string.Join(",", hours)} (wanted {string.Join(",", wanted[n])})");
                }
                g.countsPerDay = CountSystem.DefaultPerDay;
                if (CountSystem.LockupHour(g) != 22) CountFail("hours", $"the default regime's lockup for the night is at {CountSystem.LockupHour(g)} (wanted 22: the midday lockup is not the night's)");
                // Lockup from 20:00 (after free time, sleep after midnight only): the evening count follows it.
                sched.slots = Day((0, 5, RPDefOf.RP_Sleep), (6, 19, RPDefOf.RP_FreeTime)).ToList();
                if (CountSystem.LockupHour(g) != 20 || !CountSystem.Hours(g).SequenceEqual(new[] { 6, 20 })) CountFail("hours", $"the default two a day with lockup at 20:00 are {string.Join(",", CountSystem.Hours(g))} (wanted 6, 20)");
                // Sleep straight after free time, no lockup before it: the sleep hour itself. No sleep at all: 22:00.
                sched.slots = Day((0, 20, RPDefOf.RP_FreeTime), (21, 23, RPDefOf.RP_Sleep)).ToList();
                if (CountSystem.LockupHour(g) != 21) CountFail("hours", $"with sleep straight after free time the night's count is at {CountSystem.LockupHour(g)} (wanted the sleep hour, 21)");
                sched.slots = Day((0, 23, RPDefOf.RP_FreeTime)).ToList();
                if (CountSystem.LockupHour(g) != 22) CountFail("hours", $"with no sleep in the regime the night's count is at {CountSystem.LockupHour(g)} (wanted 22)");
                sched.slots = medium.ToList();
                if (CountSystem.NextHour(g, 23) != 6 || CountSystem.NextHour(g, 6) != 22 || CountSystem.NextHour(g, 7) != 22) CountFail("hours", "the next count's hour is not the next of the day's");
                // Off: nothing is held, whatever the checks' switch says; every part is told at once.
                g.countsPerDay = 0;
                PrisonMap pm = t.pm;
                Pawn walker = t.Inmate(t.s.cell);
                int escaped0 = g.stats.escaped, funds0 = g.ledger.funds;
                if (CountSystem.Enabled(g)) CountFail("policy off", "counts at nought a day are on");
                CountSystem.StartEscaping(walker, "check", EscapeWay.Walked);
                if (CountSystem.IsHidden(walker) || pm.count.hidden.Count != 0) CountFail("policy off", "with counts off a runner nobody saw is held for a count");
                walker.ExitMap(false, Rot4.Invalid);
                if (g.stats.escaped != escaped0 + 1 || g.ledger.funds != funds0 - EscapeUtility.EscapeFine || t.Told("RP_EscapedLabel".Translate().Resolve()) != 1)
                    CountFail("policy off", $"with counts off an escape is not told at once: escaped {escaped0} -> {g.stats.escaped}, funds {funds0} -> {g.ledger.funds}, {t.Told("RP_EscapedLabel".Translate().Resolve())} letter(s)");
                if (CountSystem.HudLine(map, pm, g, out bool warn) != "RP_HudCountOff".Translate().Resolve() || warn) CountFail("policy off", "the HUD does not say that counts are off");
                // And the checks' own rule: counts on in the game, off under a test unless a check turns them on.
                g.countsPerDay = 2;
                CountSystem.TestOn = false;
                if (CountSystem.Enabled(g) != !AutoTest.Active) CountFail("policy off", "under a test the count is on without a check turning it on");
                CountSystem.TestOn = true;
                g.countsPerDay = perDay0;
                RPLog.Message("[RimPrison:test] count: the day's hours follow the regime's lockup hour, none a day holds nothing back, and a test has no count unless a check turns it on");
            });
        }

        // ---- The saveload plan ----------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Everything the count keeps in a save as one comparable string: the policy, the map's state, who was last counted when, and what the police were given.</summary>
        private static string CountDescribe(PrisonGame g, Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            CountState c = pm.count;
            string state = $"{c.lastClearedTick}/{c.lastMissedTick}/{c.startedTick}/{c.dueTick}/{c.walked}/{c.writeUps}/[{string.Join(",", c.wingsLeft)}]";
            string unaccounted = string.Join(",", c.unaccounted.Select(e => $"{e.pawnId}:{e.name}:{e.place}:{e.escapedTick}"));
            string hidden = string.Join(",", c.hidden.Select(h => $"{h.pawn?.thingIDNumber}:{h.way}:{h.startTick}:{h.exitTick}:{h.cell}:{h.place}:{h.security?.defName}:{h.name}:{h.group}"));
            string fugitives = string.Join(",", g.fugitives.Select(f => $"{f.pawn?.thingIDNumber}:{f.returnTick}:{f.attempts}:{f.escapedTick}:{f.headStart}"));
            return $"{g.countsPerDay}/{state}/u=[{unaccounted}]/h=[{hidden}]/f=[{fugitives}]";
        }

        private static (int pawnId, int counted) countSeed = (-1, -1);
        private static Pawn countSeedFugitive;
        private static int countSeedWalker = -1;

        /// <summary>
        /// Saveload plan, before the snapshot: three counts a day, a count half done (two wings left, one walked, a write-up), a count's last result (cleared and missed at ticks that are not the defaults), one inmate missing and
        /// one escape nobody has seen (the runner a live inmate stands in for), an inmate who was counted, and a fugitive with a head start. <see cref="CheckCountAfterReload"/> puts them all back.
        /// </summary>
        private static void SeedCountForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            Pawn counted = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null).OrderBy(p => p.thingIDNumber).FirstOrDefault();
            if (counted == null)
            {
                reloadProblems.Add("saveload: no inmate to have counted");
                return;
            }
            int now = Find.TickManager.TicksGame;
            g.countsPerDay = 3;
            (counted.Inmate().lastCountedTick, countSeed) = (now - 777, (counted.thingIDNumber, now - 777));
            CountState c = pm.count;
            (c.lastClearedTick, c.lastMissedTick, c.startedTick, c.dueTick, c.walked, c.writeUps) = (now - 5000, now - 2500, now - 100, now + 500, 1, 2);
            c.wingsLeft = new List<int> { 3, 5 };
            c.unaccounted.Add(new CountEntry { pawnId = 424242, name = "Seeded Runner", place = "in the yard", escapedTick = now - 9000 });
            // (Another still on the map: an inmate who is on their feet, whom the beat would drop were counts on. Under a test they are not, which is what keeps the record whole until the snapshot.)
            Pawn onMap = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null && p != counted).OrderBy(p => p.thingIDNumber).FirstOrDefault();
            countSeedWalker = onMap?.thingIDNumber ?? -1;
            if (onMap != null) c.hidden.Add(new HiddenEscape { pawn = onMap, way = EscapeWay.Walked, startTick = now - 1800, cell = onMap.Position, place = "on the grounds", security = InmateUtility.DefaultSecurity, name = "Seeded Walker" });
            c.hidden.Add(new HiddenEscape { pawn = counted, way = EscapeWay.Tunnel, startTick = now - 3000, exitTick = now - 2900, cell = new IntVec3(12, 0, 13), place = "in a cell", security = InmateUtility.DefaultSecurity, name = "Seeded Hider", group = 2 });
            // A fugitive with a head start: a made-up inmate in the world's keeping, as the police's own record keeps one.
            countSeedFugitive = InmateGenerator.Generate(InmateUtility.DefaultSecurity, throwawayNumber++);
            Find.WorldPawns.PassToWorld(countSeedFugitive, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            Find.WorldPawns.ForcefullyKeptPawns.Add(countSeedFugitive);
            g.fugitives.Add(new Fugitive { pawn = countSeedFugitive, returnTick = now + 30 * GenDate.TicksPerDay, attempts = 1, escapedTick = now - 20000, headStart = 15000 });
        }

        /// <summary>After the reload: the count's state came back whole (the snapshot compared it), and is put back as the game had it (the seed's own inmate and fugitive taken away).</summary>
        private static void CheckCountAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            if (countSeed.pawnId < 0) return;
            Pawn counted = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == countSeed.pawnId);
            if (counted?.Inmate() == null || counted.Inmate().lastCountedTick != countSeed.counted)
                reloadProblems.Add($"the tick an inmate was last counted changed in the round trip ({counted?.Inmate()?.lastCountedTick} for {countSeed.counted})");
            else RPLog.Message("[RimPrison:test] saveload count: the state of the count, the hidden escape, the fugitive's head start and the inmate's last count are intact");
            // (The one still on the map came back with its record: held, not exited, the pawn spawned.)
            if (countSeedWalker >= 0 && !pm.count.hidden.Any(h => !h.Exited && h.pawn is { Spawned: true } w && w.thingIDNumber == countSeedWalker)) reloadProblems.Add($"the escape nobody had seen, the runner still on the map, did not come back from the save ({pm.count.hidden.Count} held)");
            countSeedWalker = -1;
            if (counted?.Inmate() != null) counted.Inmate().lastCountedTick = -1;
            pm.count = new CountState();
            g.countsPerDay = CountSystem.DefaultPerDay;
            if (countSeedFugitive != null)
            {
                Pawn seeded = g.fugitives.Select(f => f.pawn).FirstOrDefault(p => p != null && p.thingIDNumber == countSeedFugitive.thingIDNumber);
                g.fugitives.RemoveAll(f => f.pawn == null || f.pawn.thingIDNumber == countSeedFugitive.thingIDNumber);
                if (seeded != null)
                {
                    ExDiscard(seeded);
                }
            }
            countSeed = (-1, -1);
            countSeedFugitive = null;
        }
    }
}
