using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The library and exercise review's regression checks, one method per finding (each in its own <see cref="Guarded"/> scope, which puts the state back in a finally):
    /// the cart (clerks keep apart, the route follows the stops, a round run through to its end), reading in the cell, books and a stay, the alert's benches, the plate,
    /// the cap on Tough, and what a book may hide. Run from <see cref="LibraryTest"/>, after the main check, inside its fixed dice.
    /// </summary>
    public static partial class AutoTest
    {
        private static void LibraryRoundChecks(Map map)
        {
            CheckClerksDoNotShareInmates(map);
            CheckClerkWithNoRoundHasNoWork(map);
            CheckCartRouteFollowsTheStops(map);
            CheckCartRoundRunsToTheEnd(map);
            CheckReadingHappensInTheCell(map);
            CheckBooksDoNotOutliveAStay(map);
            CheckAlertCountsOnlyBenchesInmatesUse(map);
            CheckPlateOutHitsAFist(map);
            CheckLiftingToughensOnlyUpToACap(map);
            CheckBookContrabandIsOnlyDrugs(map);
            CheckBookContrabandPaysADebt(map);
        }

        /// <summary>
        /// Ticks the game on, if need be, until the next <paramref name="needed"/> ticks hold no change of hour: the prison's hour pass (a regime change that ends the
        /// casual jobs of every inmate, a thrown-away reader's included) is seen at the first multiple of 250 ticks after the hour turns. A check that runs real ticks
        /// on a job the regime may end would otherwise fail about once in 2500 / needed runs, by when in the day the plan happened to reach it.
        /// </summary>
        private static void ClearOfTheHourChange(Map map, int needed)
        {
            const int passDelay = 260;
            int Left() => GenDate.TicksPerHour - GenLocalDate.DayTick(map) % GenDate.TicksPerHour;
            for (int i = 0; i < GenDate.TicksPerHour && !(Left() >= needed + passDelay - 10 && Left() <= GenDate.TicksPerHour - passDelay); i++) Find.TickManager.DoSingleTick();
        }

        private static void MoveTo(Pawn p, IntVec3 cell)
        {
            p.pather?.StopDead();
            p.Position = cell;
            p.Notify_Teleported();
        }

        /// <summary>The shelves in the library zone, each holding <paramref name="books"/> (put back by the rollback), and no midnight purchase while the check runs.</summary>
        private static List<CompBookShelf> StockedLibrary(Rollback r, PrisonMap pm, int books)
        {
            List<CompBookShelf> shelves = pm.Shelves.Where(s => s.parent.Spawned && pm.zones.ZoneAt(s.parent.Position) == RPLibraryDefOf.RP_Library).ToList();
            List<int> stock0 = shelves.Select(s => s.books).ToList();
            int capacity0 = shelves.Count == 0 ? 0 : shelves[0].Props.capacity;
            r.Add(() =>
            {
                for (int i = 0; i < shelves.Count; i++) shelves[i].books = stock0[i];
                foreach (CompBookShelf s in shelves) s.Props.capacity = capacity0;
            });
            foreach (CompBookShelf s in shelves)
            {
                s.books = books;
                s.Props.capacity = 0;
            }
            return shelves;
        }

        /// <summary>Rounds the prison's own clerks are on are ended (they would otherwise hold the inmates they are on their way to: a book is not handed to those twice; the clerks start new rounds later).</summary>
        private static void EndRoundsOnTheWay(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
                if (p.CurJobDef == RPLibraryDefOf.RP_DeliverBooks) p.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }

        /// <summary>
        /// Two real inmates with a bed who can read, the only ones in the prison who want a book (everybody else holds one; a real clerk's round is ended), so that what a
        /// clerk is sent to is theirs alone. Their state and the others' books are put back by the rollback. False (and a problem) when the prison has no two such inmates.
        /// </summary>
        private static bool TwoWhoWantABook(Map map, Rollback r, out Pawn x, out Pawn y)
        {
            x = y = null;
            EndRoundsOnTheWay(map);
            List<Pawn> readers = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { activeFlag: true } && InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState
                                                                                  && !p.health.HasHediffsNeedingTend() && p.ownership?.OwnedBed != null && LibrarySystem.Literacy(p) != null).Take(2).ToList();
            if (readers.Count < 2)
            {
                problems0.Add($"library: the cart checks need two active inmates with a bed who can read, found {readers.Count}");
                return false;
            }
            (x, y) = (readers[0], readers[1]);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (p.Inmate() is not { } c) continue;
                (int books, int bookTicks, InmateWant want, Dictionary<ProgramDef, int> progress) was = (c.books, c.bookTicks, c.want, new Dictionary<ProgramDef, int>(c.programProgress));
                float literacy = LibrarySystem.LiteracyLevel(p);
                r.Add(() =>
                {
                    (c.books, c.bookTicks, c.want) = (was.books, was.bookTicks, was.want);
                    c.programProgress.Clear();
                    foreach (KeyValuePair<ProgramDef, int> kv in was.progress) c.programProgress[kv.Key] = kv.Value;
                    if (LibrarySystem.Literacy(p) is { } need) need.CurLevel = literacy;
                });
                if (p == x || p == y)
                {
                    // (A religious inmate whose faith is low is sent to pray by the lockup activity, every time it is asked (`FaithUtility.TryPrayInCell`), and never reads: which of the real inmates
                    // came first decided whether the reading checks passed. The two readers are not religious for the check.)
                    bool religious = c.religious;
                    r.Add(() => c.religious = religious);
                    c.religious = false;
                    c.books = 0;
                    c.want = null;
                    c.programProgress.Clear();
                    LibrarySystem.Literacy(p).CurLevel = p == x ? 0.4f : 0.2f;
                }
                else if (c.books <= 0) c.books = 1;
            }
            if (!LibrarySystem.WantsBook(x, x.Inmate()) || !LibrarySystem.WantsBook(y, y.Inmate()))
            {
                problems0.Add($"library: the two readers set up for the cart do not both want a book ({LibrarySystem.WantsBook(x, x.Inmate())}/{LibrarySystem.WantsBook(y, y.Inmate())})");
                return false;
            }
            return true;
        }

        /// <summary>
        /// One clerk (<paramref name="first"/>) is set off on a round for the two readers, and a second is made ready. The first starts walking at once, no ticks run, and
        /// its job is ended by the rollback. False (and a problem) when the setup does not hold.
        /// </summary>
        private static bool OneClerkOnARound(Map map, Rollback r, out PrisonMap pm, out Pawn first, out Pawn second, out Pawn x, out Pawn y)
        {
            pm = PrisonMap.Of(map);
            first = second = null;
            if (!TwoWhoWantABook(map, r, out x, out y)) return false;
            SecurityLevelDef minimum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
            first = Throwaway(map, r, minimum);
            second = Throwaway(map, r, minimum);
            if (StockedLibrary(r, pm, 8).Count == 0)
            {
                problems0.Add("library: no book shelf stands in a library zone for the cart checks");
                return false;
            }
            List<Pawn> open = LibrarySystem.Recipients(map, second, 1000);
            Job round = LibrarySystem.MakeRounds(first, pm);
            if (!open.Contains(x) || !open.Contains(y) || round == null)
            {
                problems0.Add($"library: before any round the second clerk can reach {(open.Contains(x) ? "" : "not ")}the first reader and {(open.Contains(y) ? "" : "not ")}the second, and the first clerk's round is {(round == null ? "none" : "made")}");
                return false;
            }
            Pawn started = first;
            started.jobs.StartJob(round, JobCondition.InterruptForced);
            r.Add(() => started.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false));
            if (started.CurJobDef != RPLibraryDefOf.RP_DeliverBooks)
            {
                problems0.Add($"library: the first clerk's round did not start (job now {started.CurJobDef?.defName ?? "none"})");
                return false;
            }
            return true;
        }

        // ---- 1: several clerks never pick the same inmates ------------------------------------------------------------------------------

        /// <summary>A second clerk, while the first is on a round to the only two inmates who want a book, is not sent to the same two (and has no round to make).</summary>
        private static void CheckClerksDoNotShareInmates(Map map)
        {
            Guarded("library: two clerks do not share inmates", map, r =>
            {
                if (!OneClerkOnARound(map, r, out PrisonMap pm, out Pawn first, out Pawn second, out Pawn x, out Pawn y)) return;
                // (The shelf is the first clerk's while they hold it; a second shelf would do, the recipients are what is tested.)
                List<Pawn> secondRecipients = LibrarySystem.Recipients(map, second, 1000);
                if (secondRecipients.Contains(x) || secondRecipients.Contains(y))
                    problems0.Add($"library: the first clerk is on a round to {x.LabelShort} and {y.LabelShort}, and the second clerk's recipients are [{string.Join(", ", secondRecipients.Select(p => p.LabelShort))}] (neither expected)");
            });
        }

        // ---- 3: a clerk with no round to make is not kept from class ---------------------------------------------------------------------

        /// <summary>
        /// "Has work" is what a round would find: with every inmate who wants a book already on another clerk's round it is false (it was true: somebody wants a book),
        /// and it agrees with whether a round can be made, before and after.
        /// </summary>
        private static void CheckClerkWithNoRoundHasNoWork(Map map)
        {
            Guarded("library: no round, no work for the clerk", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                if (!TwoWhoWantABook(map, r, out Pawn x, out Pawn y)) return;
                SecurityLevelDef minimum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                Pawn first = Throwaway(map, r, minimum), second = Throwaway(map, r, minimum);
                if (StockedLibrary(r, pm, 8).Count == 0)
                {
                    problems0.Add("library: no book shelf stands in a library zone for the clerk's work check");
                    return;
                }
                bool freeBefore = LibrarySystem.HasRounds(second), roundBefore = LibrarySystem.MakeRounds(second, pm) != null;
                Job round = LibrarySystem.MakeRounds(first, pm);
                if (round == null)
                {
                    problems0.Add("library: the first clerk has no round to make for the two who want a book");
                    return;
                }
                first.jobs.StartJob(round, JobCondition.InterruptForced);
                r.Add(() => first.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false));
                // The first clerk's hold on the shelf is let go (a second shelf would be free): what stands between the second clerk and a round is who is left to bring a book to.
                map.reservationManager.ReleaseAllClaimedBy(first);
                bool freeAfter = LibrarySystem.HasRounds(second), roundAfter = LibrarySystem.MakeRounds(second, pm) != null;
                if (!freeBefore || !roundBefore || freeAfter || roundAfter)
                    problems0.Add($"library: with two inmates wanting a book the second clerk has work {freeBefore} and a round {roundBefore}; once the first clerk is on the way to both: work {freeAfter}, a round {roundAfter} (true/true, then false/false expected)");
            });
        }

        // ---- 5: the route follows where the cart stops -----------------------------------------------------------------------------------

        /// <summary>The cart goes next to the nearest <i>stop</i> (a cell door), not to whoever stands nearest: two inmates who stand at each other's door are visited by their own.</summary>
        private static void CheckCartRouteFollowsTheStops(Map map)
        {
            Guarded("library: the cart's route follows its stops", map, r =>
            {
                if (!TwoWhoWantABook(map, r, out Pawn x, out Pawn y)) return;
                Thing doorX = LibrarySystem.DeliveryPoint(x), doorY = LibrarySystem.DeliveryPoint(y);
                if (doorX == null || doorY == null || doorX.Position == doorY.Position)
                {
                    problems0.Add($"library: the route check needs two different stops ({doorX?.Position}, {doorY?.Position})");
                    return;
                }
                (IntVec3 at0, IntVec3 at1) = (x.Position, y.Position);
                r.Add(() =>
                {
                    MoveTo(x, at0);
                    MoveTo(y, at1);
                });
                // Each stands at the other's door: by where they are, y (at x's door) is the nearest to a cart that starts at x's door.
                MoveTo(x, doorY.Position);
                MoveTo(y, doorX.Position);
                List<Pawn> order = LibrarySystem.Route(doorX.Position, new List<Pawn> { y, x });
                if (order.Count != 2 || order[0] != x || order[1] != y)
                    problems0.Add($"library: starting at {x.LabelShort}'s door the route is [{string.Join(", ", order.Select(p => p.LabelShort))}] ({x.LabelShort} first expected, whoever stands where)");
            });
        }

        // ---- the test gap: a round run through to its end --------------------------------------------------------------------------------

        /// <summary>
        /// A round of the cart, really run (in game ticks, from the shelf to the cells): it loads the cart off the shelf, walks to the door of each cell, hands a book over (once each),
        /// passes by a stop it has no door for, and puts what is left back on the shelf (once), ending as a success.
        /// </summary>
        private static void CheckCartRoundRunsToTheEnd(Map map)
        {
            Guarded("library: a round of the cart, run to its end", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                if (!TwoWhoWantABook(map, r, out Pawn x, out Pawn y)) return;
                Pawn clerk = Throwaway(map, r, DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum"));
                Pawn nobody = Throwaway(map, r, DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum"));
                StockedLibrary(r, pm, 6);
                CompBookShelf shelf = LibrarySystem.StockedShelf(clerk, pm);
                if (shelf == null)
                {
                    problems0.Add("library: no stocked shelf the clerk can walk to for the round check");
                    return;
                }
                if (!LibrarySystem.Recipients(map, clerk, 1000).Contains(x) || !LibrarySystem.Recipients(map, clerk, 1000).Contains(y))
                {
                    problems0.Add("library: the clerk cannot walk to the doors of the two readers' cells for the round check");
                    return;
                }
                // Three stops, one of them an inmate with no bed (no door to go to): three books come off the shelf, two are handed over, one goes back.
                List<Pawn> route = LibrarySystem.Route(shelf.parent.Position, new List<Pawn> { x, y });
                Job round = JobMaker.MakeJob(RPLibraryDefOf.RP_DeliverBooks, shelf.parent);
                round.AddQueuedTarget(TargetIndex.B, route[0]);
                round.AddQueuedTarget(TargetIndex.B, nobody);
                round.AddQueuedTarget(TargetIndex.B, route[1]);
                int onShelf = shelf.books, delivered0 = g.stats.booksDelivered;
                MoveTo(clerk, shelf.parent.InteractionCell);
                JobCondition? ended = null;
                clerk.jobs.StartJob(round, JobCondition.InterruptForced);
                r.Add(() => clerk.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false));
                // (Who ended it, when it was not the round's own end: the mod frames of the stack, for the failure text.)
                string endedBy = null;
                clerk.jobs.curDriver?.AddFinishAction(c =>
                {
                    ended = c;
                    if (c != JobCondition.Succeeded)
                        endedBy = string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace == "RimPrison" && m.DeclaringType != typeof(AutoTest) && !m.DeclaringType.Name.StartsWith("<"))
                            .Select(m => m.DeclaringType.Name + "." + m.Name).Distinct().Take(6));
                });
                var visited = new HashSet<Pawn>();
                // (For the failure text: how near the clerk came to each stop, and where each stop was at the end of the round.)
                var nearest = new Dictionary<Pawn, int> { [x] = int.MaxValue, [y] = int.MaxValue };
                // (A round is a thousand ticks of the live prison: a lying-down inmate claims the nearest free bed, so the bedless one had one by the second stop and was handed a book, and a reader's bed
                // went to someone else. The beds of the three are held as set up, every tick.)
                (Pawn, Building_Bed)[] beds = { (x, x.ownership.OwnedBed), (y, y.ownership.OwnedBed) };
                int ticks = 0;
                while (ticks < 4000 && clerk.CurJobDef == RPLibraryDefOf.RP_DeliverBooks)
                {
                    if (nobody.ownership.OwnedBed != null) nobody.ownership.UnclaimBed();
                    foreach ((Pawn reader, Building_Bed bed) in beds)
                        if (reader.ownership.OwnedBed != bed && bed.Spawned) reader.ownership.ClaimBedIfNonMedical(bed);
                    Find.TickManager.DoSingleTick();
                    ticks++;
                    foreach (Pawn p in new[] { x, y })
                        if (LibrarySystem.DeliveryPoint(p) is { } door)
                        {
                            int d2 = (door.Position - clerk.Position).LengthHorizontalSquared;
                            nearest[p] = Mathf.Min(nearest[p], d2);
                            if (d2 <= 8) visited.Add(p);
                        }
                }
                string stops = string.Join(", ", new[] { x, y }.Select(p => $"{p.LabelShort}'s stop {LibrarySystem.DeliveryPoint(p)?.def.defName} at {LibrarySystem.DeliveryPoint(p)?.Position}, the clerk came within {(nearest[p] == int.MaxValue ? "never" : Mathf.Sqrt(nearest[p]).ToString("F1"))} cells, {p.LabelShort} is at {p.Position} doing {p.CurJobDef?.defName}"));
                RPLog.Message($"[RimPrison:test] library round: {ticks} ticks, ended {ended?.ToString() ?? "not"}, visited {visited.Count} doors, shelf {onShelf} -> {shelf.books}, books {x.Inmate().books}/{y.Inmate().books}/{nobody.Inmate().books}");
                if (ended != JobCondition.Succeeded || clerk.CurJobDef == RPLibraryDefOf.RP_DeliverBooks || visited.Count != 2 || x.Inmate().books != 1 || y.Inmate().books != 1 || nobody.Inmate().books != 0
                    || shelf.books != onShelf - 2 || g.stats.booksDelivered != delivered0 + 2)
                    problems0.Add($"library: a round of the cart took {ticks} ticks and ended {ended?.ToString() ?? "not at all"} (success expected), reached {visited.Count} of 2 doors; the readers hold {x.Inmate().books}/{y.Inmate().books} books (1/1) and the bedless one {nobody.Inmate().books} (0), "
                                  + $"the shelf went from {onShelf} to {shelf.books} books (two fewer expected: three loaded, two handed over, the third put back once), {g.stats.booksDelivered - delivered0} counted as delivered (2); {stops}; ended by [{endedBy ?? "-"}] at tick {ticks} with the clerk's activity {g.CurrentActivity(clerk)?.defName}");
            });
        }

        // ---- the test gap: reading that really happens ------------------------------------------------------------------------------------

        /// <summary>
        /// With a book in the cell, the lockup and the sleep activities hand out the reading job (they did not read: they lay there), and the job run in game ticks fills
        /// the need and uses up the book.
        /// </summary>
        private static void CheckReadingHappensInTheCell(Map map)
        {
            Guarded("library: reading in the cell", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                if (!TwoWhoWantABook(map, r, out Pawn reader, out Pawn _)) return;
                CompInmate c = reader.Inmate();
                Building_Bed bed = reader.ownership.OwnedBed;
                Room cell = bed.GetRoom();
                if (cell == null || !CellFinder.TryFindRandomCellNear(bed.Position, map, 2, cl => cl.GetRoom(map) == cell && cl.Standable(map) && cl.GetFirstPawn(map) == null, out IntVec3 inside))
                {
                    problems0.Add("library: no free cell in the reader's cell to read in");
                    return;
                }
                IntVec3 at0 = reader.Position;
                Need rest = reader.needs.rest, bladder = reader.needs.TryGetNeed(RPDefOf.RP_Bladder);
                (float rest0, float bladder0) = (rest?.CurLevel ?? 1f, bladder?.CurLevel ?? 1f);
                r.Add(() =>
                {
                    if (rest != null) rest.CurLevel = rest0;
                    if (bladder != null) bladder.CurLevel = bladder0;
                    MoveTo(reader, at0);
                });
                if (rest != null) rest.CurLevel = 1f;
                if (bladder != null) bladder.CurLevel = 1f;
                MoveTo(reader, inside);
                c.books = 1;
                c.bookTicks = 0;
                Need literacy = LibrarySystem.Literacy(reader);
                literacy.CurLevel = 0.2f;
                // The activities, as the inmate's think tree calls them (the lockup may first send them to pray: asked a few times).
                Job lockup = null;
                for (int i = 0; i < 40 && lockup?.def != RPLibraryDefOf.RP_ReadBook; i++) lockup = RPDefOf.RP_Lockup.Worker.TryGiveJob(reader, c, pm);
                Job sleep = RPDefOf.RP_Sleep.Worker.TryGiveJob(reader, c, pm);
                // (A sleep job that is neither lying down nor reading — a wait, no job — says nothing about the wiring: the bed was not free to lie in.)
                bool sleepLiesThere = sleep != null && sleep.def != RPLibraryDefOf.RP_ReadBook && (sleep.def == JobDefOf.LayDown || sleep.def == JobDefOf.LayDownAwake);
                if (lockup?.def != RPLibraryDefOf.RP_ReadBook || sleepLiesThere)
                    problems0.Add($"library: with a book in the cell the lockup activity gives {lockup?.def?.defName ?? "no job"} (reading expected), the sleep activity (rested) {sleep?.def?.defName ?? "no job"} (reading expected)");
                if (lockup?.def != RPLibraryDefOf.RP_ReadBook) return;
                // The reading job, really run on the inmate in their own cell (only logged: a real inmate's own think tree may take them off it for reasons of its own)...
                reader.jobs.StartJob(lockup, JobCondition.InterruptForced);
                r.Add(() => reader.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false));
                JobCondition? stopped = null;
                reader.jobs.curDriver?.AddFinishAction(cond => stopped = cond);
                string started = $"{reader.CurJobDef?.defName ?? "no job"}, awake {reader.Awake()}, in bed {reader.InBed()}, downed {reader.Downed}, in the cell {reader.GetRoom() == cell}";
                int onCell = 0;
                while (onCell < 120 && reader.CurJobDef == RPLibraryDefOf.RP_ReadBook)
                {
                    Find.TickManager.DoSingleTick();
                    onCell++;
                }
                RPLog.Message($"[RimPrison:test] library reading, their own cell: started as {started}; {onCell} ticks on the job, then {reader.CurJobDef?.defName ?? "no job"} (the job ended {stopped?.ToString() ?? "not"}), literacy +{literacy.CurLevel - 0.2f:F3}");
                reader.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                // ...and on a thrown-away inmate, whose job nothing else touches: the need rises as they read and the book is read down.
                // (Not across the turn of an hour: the regime changes then, and ends the reading like any casual job.)
                ClearOfTheHourChange(map, 300);
                Pawn bookworm = LiterateThrowaway(map, r, null);
                CompInmate bc = bookworm.Inmate();
                Need level = LibrarySystem.Literacy(bookworm);
                // (Nothing else they need: a thrown-away inmate is generated with needs at any level, and a full bladder or an empty stomach is a job that comes before reading, which a casual
                // job like this gives way to when the think tree is next asked, a few ticks in.)
                foreach (Need need in bookworm.needs.AllNeeds)
                    if (need != level) need.CurLevel = need.MaxLevel;
                bc.awaitingSearch = false;
                (bc.books, bc.bookTicks, level.CurLevel) = (1, 0, 0.2f);
                float level0 = level.CurLevel;
                bookworm.jobs.StartJob(JobMaker.MakeJob(RPLibraryDefOf.RP_ReadBook), JobCondition.InterruptForced);
                r.Add(() => bookworm.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false));
                // (For the failure text, should the reading end early: how, and what called for it.)
                string endedBy = null;
                bookworm.jobs.curDriver?.AddFinishAction(cond => endedBy ??= $"{cond} from {string.Join(" < ", new System.Diagnostics.StackTrace(1).GetFrames().Select(f => f.GetMethod()?.Name).Where(n => n != null && !n.StartsWith("<") && n != "Invoke").Take(8))}");
                const int ticks = 300;
                int ran = 0;
                bool reading = bookworm.CurJobDef == RPLibraryDefOf.RP_ReadBook;
                while (ran < ticks && reading)
                {
                    Find.TickManager.DoSingleTick();
                    ran++;
                    reading = bookworm.CurJobDef == RPLibraryDefOf.RP_ReadBook;
                }
                float gained = level.CurLevel - level0;
                RPLog.Message($"[RimPrison:test] library reading, thrown-away inmate: {ran} ticks, still reading {reading}, literacy +{gained:F3}, {bc.bookTicks} ticks into the book");
                if (!reading || gained < LibrarySystem.GainPerTick * ticks * 0.5f || bc.bookTicks < ticks / 2)
                    problems0.Add($"library: reading for {ran} ticks (still on the job {reading}) raised literacy by {gained:F3} (about {LibrarySystem.GainPerTick * ticks:F2} expected) and read {bc.bookTicks} ticks of the book ({ticks / 2}+ expected); the reader now does {bookworm.CurJobDef?.defName ?? "nothing"} at {GenLocalDate.HourOfDay(map)}h (the reading ended: {endedBy ?? "not"}), "
                                  + $"food {bookworm.needs.food?.CurLevelPercentage:F2}, rest {bookworm.needs.rest?.CurLevel:F2}, bladder {bookworm.needs.TryGetNeed(RPDefOf.RP_Bladder)?.CurLevel:F2}, books left {bc.books}");
            });
        }

        // ---- 4: a book does not outlive the stay -----------------------------------------------------------------------------------------

        private static void CheckBooksDoNotOutliveAStay(Map map)
        {
            Guarded("library: a new stay starts without the old book", map, r =>
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                (c.books, c.bookTicks) = (1, 900);
                c.ResetForNewStay();
                if (c.books != 0 || c.bookTicks != 0)
                    problems0.Add($"library: after a stay ended the inmate still has {c.books} books, {c.bookTicks} ticks into one (none expected)");
            });
        }

        // ---- 6: the alert counts the benches inmates can use -----------------------------------------------------------------------------

        /// <summary>A bench in a zone the free-time search never uses (inmate space, but not the yard or the common room) does not silence the alert's "no weights bench".</summary>
        private static void CheckAlertCountsOnlyBenchesInmatesUse(Map map)
        {
            Guarded("library: the alert and the benches inmates use", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                r.Add(PrisonServices.Reset);
                CompFixture bench = pm.Fixtures.FirstOrDefault(f => f.Props.need == RPExerciseDefOf.RP_Exercise && f.Usable && ExerciseUtility.InBenchZone(pm.zones.ZoneAt(f.parent.Position)));
                PrisonZoneDef elsewhere = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.inmateAccess && !z.staffOnly && !ExerciseUtility.InBenchZone(z));
                if (bench == null || elsewhere == null)
                {
                    problems0.Add($"library: the alert check needs a bench in the yard or common room ({bench != null}) and an inmate zone besides ({elsewhere != null})");
                    return;
                }
                PrisonServices.Reset();
                int before = PrisonServices.Of(map).benches;
                var cells = bench.parent.OccupiedRect().Cells.ToList();
                foreach (IntVec3 cell in cells)
                {
                    PrisonZoneDef old = pm.zones.ZoneAt(cell);
                    r.Add(() => pm.zones.Set(cell, old));
                    pm.zones.Set(cell, elsewhere);
                }
                PrisonServices.Reset();
                int after = PrisonServices.Of(map).benches;
                if (after != before - 1)
                    problems0.Add($"library: the alert counts {before} benches with the bench in {bench.parent.Position}'s zone free-time, {after} with it moved to the {elsewhere.defName} zone (one fewer expected)");
            });
        }

        // ---- 7: a plate is a better weapon than a fist ------------------------------------------------------------------------------------

        /// <summary>The plate's best attack out-damages the best human fist by a clear margin (a quarter), and stays under a shiv (it is no knife).</summary>
        private static void CheckPlateOutHitsAFist(Map map)
        {
            Guarded("library: a plate against a fist", map, r =>
            {
                float Dps(List<Tool> tools) => tools == null || tools.Count == 0 ? 0f : tools.Max(t => t.power / t.cooldownTime);
                float fist = Dps(ThingDefOf.Human.tools), plate = Dps(RPExerciseDefOf.RP_WeightPlate.tools), shiv = Dps(DefDatabase<ThingDef>.GetNamed("RP_Shiv").tools);
                if (plate < fist * 1.25f || plate >= shiv)
                    problems0.Add($"library: a weight plate hits {plate:F2} a second, a human fist {fist:F2} and a shiv {shiv:F2} (the plate at least {fist * 1.25f:F2} and under the shiv expected)");
            });
        }

        // ---- 8: lifting makes only so many Tough ------------------------------------------------------------------------------------------

        /// <summary>
        /// A violent inmate who has lifted enough becomes Tough when the prison is under the cap; once enough others are Tough the same workout (with the same lucky roll) does not
        /// add another.
        /// </summary>
        private static void CheckLiftingToughensOnlyUpToACap(Map map)
        {
            Guarded("library: lifting and the cap on Tough", map, r =>
            {
                Pawn lifter = Throwaway(map, r);
                CompInmate c = lifter.Inmate();
                c.crime = DefDatabase<CrimeDef>.AllDefsListForReading.First(d => d.violent);
                c.reputations.Remove(ReputationDefOf.RP_Tough);
                c.workouts = ExerciseUtility.ToughAfterWorkouts;
                // Under the cap (the others' Tough names are taken off for the first workout and put back after).
                var holders = map.mapPawns.PrisonersOfColonySpawned.Where(p => p != lifter && p.Inmate() is { } o && o.HasReputation(ReputationDefOf.RP_Tough)).Select(p => p.Inmate()).ToList();
                r.Add(() =>
                {
                    foreach (CompInmate o in holders)
                        if (!o.HasReputation(ReputationDefOf.RP_Tough)) o.reputations.Add(ReputationDefOf.RP_Tough);
                });
                foreach (CompInmate o in holders) o.reputations.Remove(ReputationDefOf.RP_Tough);
                bool openBefore = !ExerciseUtility.ToughCapReached(map);
                ExerciseUtility.Notify_Workout(lifter, c, plate: false, tough: true);
                bool toughWhenOpen = c.HasReputation(ReputationDefOf.RP_Tough);
                c.reputations.Remove(ReputationDefOf.RP_Tough);
                // At the cap: the other inmates, up to the cap, are Tough (put right by the rollback).
                int toGive = ExerciseUtility.ToughCap(map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate() != null));
                var given = new List<CompInmate>();
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
                {
                    if (given.Count >= toGive) break;
                    if (p == lifter || p.Inmate() is not { } o || o.HasReputation(ReputationDefOf.RP_Tough)) continue;
                    o.reputations.Add(ReputationDefOf.RP_Tough);
                    given.Add(o);
                }
                r.Add(() =>
                {
                    foreach (CompInmate o in given) o.reputations.Remove(ReputationDefOf.RP_Tough);
                });
                bool capped = ExerciseUtility.ToughCapReached(map);
                c.workouts = ExerciseUtility.ToughAfterWorkouts;
                ExerciseUtility.Notify_Workout(lifter, c, plate: false, tough: true);
                bool toughWhenCapped = c.HasReputation(ReputationDefOf.RP_Tough);
                if (!openBefore || !toughWhenOpen || !capped || toughWhenCapped)
                    problems0.Add($"library: under the cap ({openBefore}) a lucky workout made them Tough {toughWhenOpen} (true expected); with {given.Count} others Tough the cap is reached {capped} and a lucky workout made them Tough {toughWhenCapped} (false expected)");
            });
        }

        // ---- 2 and 9: what a book may hide ------------------------------------------------------------------------------------------------

        /// <summary>For the most dangerous inmates, whose tools and weapons are the likelier smuggle, a book still hides only drugs: nothing screens a book, so it is no route for a digging tool or a phone.</summary>
        private static void CheckBookContrabandIsOnlyDrugs(Map map)
        {
            Guarded("library: a book hides only drugs", map, r =>
            {
                SecurityLevelDef dangerous = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderByDescending(d => d.dangerFactor).First();
                Pawn p = LiterateThrowaway(map, r, dangerous);
                CompInmate c = p.Inmate();
                var kinds = new Dictionary<string, int>();
                int hidden = 0;
                for (int i = 0; i < 60; i++)
                {
                    if (LibrarySystem.Hide(p, c) is not { } item) continue;
                    hidden++;
                    string kind = ContrabandUtility.Ext(item.def).kind.ToString();
                    kinds[kind] = kinds.TryGetValue(kind, out int n) ? n + 1 : 1;
                }
                if (hidden == 0 || kinds.Keys.Any(k => k != nameof(ContrabandKind.Drug)))
                    problems0.Add($"library: 60 books for a {dangerous.defName} inmate (danger {dangerous.dangerFactor}) hid {hidden} items: {string.Join(", ", kinds.Select(kv => kv.Key + " x" + kv.Value))} (only drugs expected)");
            });
        }

        /// <summary>An inmate who arranged a delivery to pay a debt is paid off by a book that hides something, as by a letter: the creditor gets it and the debt is settled.</summary>
        private static void CheckBookContrabandPaysADebt(Map map)
        {
            Guarded("library: a book that pays a debt", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef minimum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                Pawn debtor = LiterateThrowaway(map, r, minimum), creditor = Throwaway(map, r, minimum), clerk = Throwaway(map, r, minimum);
                CompInmate dc = debtor.Inmate();
                InmateDebt debt = DebtSystem.Open(creditor, debtor, DebtReason.Drugs, 5, 5);
                debt.smuggling = true;
                r.Add(() => g.story.debts.Remove(debt));
                LibrarySystem.Literacy(debtor).CurLevel = 0.1f;
                dc.books = 0;
                int DrugsHeld() => creditor.inventory.innerContainer.Where(t => ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Drug }).Sum(t => t.stackCount);
                int drugs0 = DrugsHeld();
                bool delivered = LibrarySystem.Deliver(clerk, debtor, hides: true);
                bool settled = !g.story.debts.Contains(debt) && !DebtSystem.Smuggling(dc);
                bool creditorHas = DrugsHeld() > drugs0;
                if (!delivered || !settled || !creditorHas || dc.stash.Count != 0)
                    problems0.Add($"library: a book with a drug in it for somebody with a smuggling debt: delivered {delivered}, debt settled {settled}, the creditor got it {creditorHas}, left in the debtor's stash {dc.stash.Count} (true, true, true, 0 expected)");
            });
        }
    }
}
