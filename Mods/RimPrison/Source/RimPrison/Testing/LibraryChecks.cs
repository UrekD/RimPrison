using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Exercise, the library and the book cart: the events plan's own check (<see cref="LibraryTest"/>, first thing in FireAllEvents, putting back what it
    /// touches), what the longer plans watch every hour (<see cref="TrackLibrary"/>, <see cref="EvaluateLibrary"/>) and the save/load round trip's seed and check.
    /// </summary>
    public static partial class AutoTest
    {
        // ---- the long plans: watching the need, counting what the cart did ------------------------------------------------------------

        /// <summary>Consecutive hours below <see cref="ExerciseUtility.RestlessBelow"/> per inmate who can reach the yard (not the downed, nor the restrained in detox: they cannot lift), the longest run, and the sampled mean.</summary>
        private static readonly Dictionary<int, int> restlessStreak = new Dictionary<int, int>();
        private static int longestRestless, restlessHours, exerciseSamples;
        private static float exerciseSum;

        /// <summary>The fewest books a 5-day normal run must hand out (the clerk works the day's work hours).</summary>
        private const int MinBooksDelivered = 3;
        /// <summary>The longest an inmate with a bench to use may stay restless (hours).</summary>
        private const int MaxRestlessStreak = 24;

        /// <summary>Hourly: the exercise need of everyone who walks to the yard (not the confined, nor death row, who eat in their cells).</summary>
        private static void TrackLibrary(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() == null || ExerciseUtility.NeedOf(p) is not { } need || !InmateUtility.IsActiveInmate(p) || !PrisonServices.UsesCanteen(p) || p.Downed)
                {
                    restlessStreak.Remove(p.thingIDNumber);
                    continue;
                }
                exerciseSum += need.CurLevel;
                exerciseSamples++;
                restlessStreak.TryGetValue(p.thingIDNumber, out int run);
                run = need.CurLevel < ExerciseUtility.RestlessBelow ? run + 1 : 0;
                restlessStreak[p.thingIDNumber] = run;
                if (run > 0) restlessHours++;
                if (run == MaxRestlessStreak + 1)
                    RPLog.Message($"[RimPrison:restless] {p.LabelShort} ({p.Inmate().security?.defName}) has been restless for {run} hours: act={PrisonGame.Instance.CurrentActivity(p).defName} job={p.CurJobDef?.defName} forced={p.Inmate().forcedActivity?.defName} downed={p.Downed} inBed={p.InBed()} medical={p.CurrentBed()?.Medical} exercise={need.CurLevel:F2}");
                longestRestless = Mathf.Max(longestRestless, run);
            }
        }

        /// <summary>End of a plan with benches and shelves: inmates lifted, never stayed restless for more than a day, and (in a 5-day normal run) the cart delivered.</summary>
        private static void EvaluateLibrary(Map map, PrisonGame game, int days, List<string> problems)
        {
            PrisonMap pm = PrisonMap.Of(map);
            int clerks = map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate()?.job?.workerClass == typeof(InmateJobWorker_Library));
            float meanLiteracy = map.mapPawns.PrisonersOfColonySpawned.Where(x => LibrarySystem.Literacy(x) != null).Select(LibrarySystem.LiteracyLevel).DefaultIfEmpty(0f).Average();
            RPLog.Message($"[RimPrison:test] library run: {game.stats.workouts} workouts, mean exercise {(exerciseSamples == 0 ? 0f : exerciseSum / exerciseSamples):F2}, longest restless spell {longestRestless} h ({restlessHours} restless inmate-hours), "
                          + $"{LibrarySystem.Shelves(map)} shelves with {pm.Shelves.Sum(s => s.books)} books, {clerks} clerks, {game.stats.booksDelivered} books delivered, {game.stats.booksRead} read, mean literacy {meanLiteracy:F2}, {game.stats.bookContraband} with something hidden");
            if (days < 2) return;
            if (game.stats.workouts == 0) problems.Add($"nobody lifted weights in {days} days with {pm.Fixtures.Count(f => f.Props.need == RPExerciseDefOf.RP_Exercise)} benches built");
            if (longestRestless > MaxRestlessStreak) problems.Add($"an inmate stayed restless for {longestRestless} hours (> {MaxRestlessStreak}) with benches built");
            if (Plan == "normal" && days >= 5 && game.stats.booksDelivered < MinBooksDelivered)
                problems.Add($"the library clerk delivered {game.stats.booksDelivered} books in {days} days (< {MinBooksDelivered}; {clerks} clerks, {pm.Shelves.Sum(s => s.books)} books on the shelves)");
        }

        // ---- the events plan's check ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Every need falls and is met by what is built for it, the cart delivers to a confined inmate, the alert names a missing gym or library, a book with contraband
        /// is found at the cell, and the exam bonus follows how well they read. A thrown-away pawn does the falling and reading; two real inmates are the cart's customers (put back after).
        /// </summary>
        private static void LibraryTest(Map map)
        {
            // Its own dice, so the check is the same every run and leaves the game's random stream where it found it (the checks after it have luck of their own).
            Rand.PushState(20261019);
            (int pawns0, int things0) = (map.mapPawns.AllPawnsSpawned.Count, map.listerThings.AllThings.Count);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            var before = new HashSet<Thing>(map.listerThings.AllThings);
            try
            {
                LibraryChecksBody(map);
                LibraryRoundChecks(map);
            }
            finally
            {
                Rand.PopState();
            }
            // Nothing left behind: the thrown-away inmates are gone (the things are only logged: any thrown-away inmate drops what they carry when they go).
            (int pawns1, int things1) = (map.mapPawns.AllPawnsSpawned.Count, map.listerThings.AllThings.Count);
            RPLog.Message($"[RimPrison:test] library leftovers: pawns {pawns0} -> {pawns1}, things {things0} -> {things1}" + (things1 == things0 ? "" : " (new: " + string.Join(", ", map.listerThings.AllThings.Where(t => !before.Contains(t)).Select(t => $"{t.def.defName}@{t.Position}")) + ")"));
            // (The real ticks in the cart check let staff go home and inmates be released: what matters is that none of the check's
            // own pawns stays and nobody who was here was destroyed by it.)
            // (Its own pawns are numbered from 91000; a bus or a shift may bring others in meanwhile.)
            List<Pawn> leftBehind = map.mapPawns.AllPawnsSpawned.Where(x => !pawnsBefore.Contains(x) && x.Inmate() is { inmateNumber: >= 91000 }).ToList();
            // (Nor those who go by the game's own doing in those ticks: an inmate let out of the gate, a member of staff let go or recalled, as the state's chief of an earlier check is (the faction they leave in is not the colony's), who walks off the map.)
            List<Pawn> destroyed = pawnsBefore.Where(x => x.Destroyed && !x.Dead && x.guest?.Released != true && (x.IsPrisonerOfColony || x.Faction == Faction.OfPlayer)).ToList();
            if (leftBehind.Count > 0 || destroyed.Count > 0)
                problems0.Add($"library: the check left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map or destroyed {string.Join(", ", destroyed.Select(x => x.LabelShort))} (pawns {pawns0} -> {pawns1})");
        }

        private static void LibraryChecksBody(Map map)
        {
            Guarded("exercise, library and book cart", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                // (The prison's own clerks on a round hold the inmates they are walking to, and those are not offered to another clerk: see LibrarySystem.Recipients.)
                EndRoundsOnTheWay(map);
                var summary = new StringBuilder();
                InmateJobDef libraryJob = DefDatabase<InmateJobDef>.GetNamed("RP_JobLibrary");
                Pawn clerk = LiterateThrowaway(map, r, DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum"));
                CompInmate cc = clerk.Inmate();
                Need exercise = ExerciseUtility.NeedOf(clerk), literacy = LibrarySystem.Literacy(clerk);
                if (exercise == null || literacy == null)
                {
                    problems0.Add($"library: a new inmate has the exercise need {exercise != null} and the literacy need {literacy != null}");
                    return;
                }

                // ---- the needs fall over a day, slower asleep ----
                exercise.CurLevel = literacy.CurLevel = 1f;
                for (int i = 0; i < GenDate.TicksPerDay / 150; i++)
                {
                    exercise.NeedInterval();
                    literacy.NeedInterval();
                }
                float exerciseFall = 1f - exercise.CurLevel, literacyFall = 1f - literacy.CurLevel;
                float exerciseDay = exercise.def.fallPerDay, literacyDay = literacy.def.fallPerDay;
                if (exerciseFall < exerciseDay * 0.35f || exerciseFall > exerciseDay * 1.02f || literacyFall < literacyDay * 0.15f || literacyFall > literacyDay * 1.02f)
                    problems0.Add($"library: over a day exercise fell {exerciseFall:F2} (rate {exerciseDay}) and literacy {literacyFall:F2} (rate {literacyDay})");
                summary.Append($"falls {exerciseFall:F2}/{literacyFall:F2} a day");

                // ---- exercise is met on a bench, offered in free time; no bench, no offer ----
                CompFixture bench = ExerciseUtility.FindBench(clerk);
                // (The benches can all be taken by the prison's own inmates at their workout when the check runs (a census run had none free): their workouts end, which no tick undoes before the
                // check is done, and the bench is looked for again; what each bench was, if there is still none.)
                var allBenches = PrisonMap.Of(map).Fixtures.Where(f => f.Props.need == RPExerciseDefOf.RP_Exercise).ToList();
                if (bench == null)
                    foreach (CompFixture f in allBenches)
                        if (map.reservationManager.FirstRespectedReserver(f.parent, clerk) is { } user && user != clerk && user.jobs != null)
                            user.jobs.EndCurrentJob(JobCondition.InterruptForced);
                bench ??= ExerciseUtility.FindBench(clerk);
                if (bench == null)
                {
                    PrisonMap lpm = PrisonMap.Of(map);
                    problems0.Add($"library: no weights bench in the yard or the common room can be reached from the intake ({allBenches.Count} benches: " + string.Join(", ", allBenches.Select(f =>
                        $"{f.parent.Position} in {lpm.zones.ZoneAt(f.parent.Position)?.defName ?? "no zone"}, usable {f.Usable}, reserved by {map.reservationManager.FirstRespectedReserver(f.parent, clerk)?.LabelShort ?? "nobody"}, " +
                        $"reach {clerk.CanReach(f.UseCell, PathEndMode.OnCell, Danger.Some)}")) + $"; the clerk at {clerk.Position})");
                    return;
                }
                float lift = bench.Props.gainPerTick * bench.Props.maxUseTicks;
                exercise.CurLevel = 0.1f;
                int offers = 0;
                for (int i = 0; i < 60; i++)
                    if (ExerciseUtility.TryWorkout(clerk) is { } offered && offered.def == RPDefOf.RP_UseFixture && offered.targetA.Thing == bench.parent) offers++;
                exercise.CurLevel = 0.9f;
                int satisfiedOffers = Enumerable.Range(0, 60).Count(_ => ExerciseUtility.TryWorkout(clerk) != null);
                if (lift < 0.5f || bench.Props.need != RPExerciseDefOf.RP_Exercise || offers == 0 || satisfiedOffers != 0)
                    problems0.Add($"library: a session on the bench lifts the need {lift:F2} (0.5 expected), free time offered it {offers} times of 60 when low and {satisfiedOffers} when satisfied");
                List<CompFixture> benches = pm.Fixtures.Where(f => f.Props.need == RPExerciseDefOf.RP_Exercise).ToList();
                r.Add(() => { foreach (CompFixture f in allBenches) pm.RegisterFixture(f); });
                foreach (CompFixture f in allBenches) pm.DeregisterFixture(f);
                exercise.CurLevel = 0.1f;
                bool noBenchFound = ExerciseUtility.FindBench(clerk) != null;
                int noBenchOffers = Enumerable.Range(0, 60).Count(_ => ExerciseUtility.TryWorkout(clerk) != null);
                PrisonServices.Reset();
                int benchesCounted = PrisonServices.Of(map).benches;
                // ...and the cause: restless inmates are the more tense for it (the same inmate, the same hour, with and without the workout).
                (cc.tension, exercise.CurLevel) = (50f, 1f);
                TensionUtility.HourlyUpdate(clerk, cc, pm);
                float calm = cc.tension;
                (cc.tension, exercise.CurLevel) = (50f, 0.05f);
                TensionUtility.HourlyUpdate(clerk, cc, pm);
                float restless = cc.tension - calm;
                if (noBenchFound || noBenchOffers != 0 || benchesCounted != 0 || restless < ExerciseUtility.RestlessTension * 0.3f || !"RP_Cause_Restless".CanTranslate() || !ExerciseUtility.Restless(clerk))
                    problems0.Add($"library: with no bench built one was found {noBenchFound} and offered {noBenchOffers} times, the services count {benchesCounted}; restless adds {restless:F2} tension (>= {ExerciseUtility.RestlessTension * 0.3f:F1} expected: the 1.2 an hour, scaled by their temper)");
                foreach (CompFixture f in allBenches) pm.RegisterFixture(f);
                PrisonServices.Reset();
                summary.Append($", a session lifts {lift:F2}, restless adds {restless:F1} tension");

                // ---- the workout: counted, the violent get tougher, the tense may take a plate (and a plate is a weapon) ----
                CrimeDef crime0 = cc.crime;
                int workouts0 = cc.workouts;
                r.Add(() => (cc.crime, cc.workouts) = (crime0, workouts0));
                cc.crime = DefDatabase<CrimeDef>.AllDefsListForReading.First(d => d.violent);
                cc.reputations.Remove(ReputationDefOf.RP_Tough);
                cc.workouts = ExerciseUtility.ToughAfterWorkouts - 2;
                int statWorkouts = g.stats.workouts;
                cc.tension = 5f;
                ExerciseUtility.Notify_Workout(clerk, cc, plate: true, tough: true);
                bool notYet = !cc.HasReputation(ReputationDefOf.RP_Tough) && !cc.CarriesPlate();
                cc.tension = ExerciseUtility.PlateTension + 10f;
                bool unwatched = TensionUtility.GuardsNear(clerk, pm) == 0;
                // (A prison that already has its quarter of Tough inmates toughens no more: that is CheckLiftingToughensOnlyUpToACap's.)
                bool roomForTough = !ExerciseUtility.ToughCapReached(map);
                ExerciseUtility.Notify_Workout(clerk, cc, plate: true, tough: true);
                bool tougher = cc.HasReputation(ReputationDefOf.RP_Tough) == roomForTough, tookPlate = cc.CarriesPlate();
                if (!notYet || !tougher || tookPlate != unwatched || g.stats.workouts != statWorkouts + 2 || cc.workouts != ExerciseUtility.ToughAfterWorkouts)
                    problems0.Add($"library: workouts counted {g.stats.workouts - statWorkouts} (2 expected); tougher before the sixth {!notYet}, after {tougher}; took a plate {tookPlate} (watched {!unwatched})");
                // A plate in the pocket is drawn when violence starts, as any contraband weapon is, and a frisk finds it.
                ContrabandUtility.Give(clerk, RPExerciseDefOf.RP_WeightPlate);
                bool carried = ContrabandUtility.CarriesContraband(clerk, metalOnly: true);
                ContrabandUtility.Notify_ViolenceStarting(clerk);
                bool drawn = clerk.equipment?.Primary?.def == RPExerciseDefOf.RP_WeightPlate;
                ContrabandUtility.HideWeapon(clerk);
                if (!carried || !drawn || ContrabandUtility.Ext(RPExerciseDefOf.RP_WeightPlate)?.smuggleWeight != 0f)
                    problems0.Add($"library: a weight plate is contraband {carried}, drawn when violence starts {drawn}, never smuggled in by visitors {ContrabandUtility.Ext(RPExerciseDefOf.RP_WeightPlate)?.smuggleWeight == 0f}");

                // ---- the library: shelves, stock, the clerk's job ----
                List<CompBookShelf> shelves = pm.Shelves.Where(s => s.parent.Spawned && pm.zones.ZoneAt(s.parent.Position) == RPLibraryDefOf.RP_Library).ToList();
                if (shelves.Count == 0)
                {
                    problems0.Add("library: no book shelf stands in a library zone");
                    return;
                }
                List<int> stock0 = shelves.Select(s => s.books).ToList();
                r.Add(() =>
                {
                    for (int i = 0; i < shelves.Count; i++) shelves[i].books = stock0[i];
                });
                foreach (CompBookShelf s in shelves) s.books = 3;
                g.ledger.funds += 10000;
                int fundsBefore = g.ledger.funds;
                int bought = LibrarySystem.Daily(map);
                int expectedBought = shelves.Sum(s => s.Props.capacity - 3);
                if (bought != expectedBought || shelves.Any(s => s.books != s.Props.capacity) || g.ledger.funds != fundsBefore - bought * LibrarySystem.BookPrice)
                    problems0.Add($"library: midnight bought {bought} books (expected {expectedBought}) and left {string.Join("/", shelves.Select(s => s.books))} on the shelves; funds {fundsBefore} -> {g.ledger.funds} ({LibrarySystem.BookPrice} a book)");
                cc.tension = 0f;
                int broke = g.ledger.funds;
                g.ledger.funds = 0;
                foreach (CompBookShelf s in shelves) s.books = 0;
                int boughtBroke = LibrarySystem.Daily(map);
                g.ledger.funds = broke;
                foreach (CompBookShelf s in shelves) s.books = 8;
                Pawn mediumInmate = Throwaway(map, r, DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium"));
                if (boughtBroke != 0 || libraryJob.Worker.Capacity(map) != LibrarySystem.ClerkSlots(map) || LibrarySystem.ClerkSlots(map) < 1 || !LaborUtility.Eligible(cc, libraryJob) || LaborUtility.Eligible(mediumInmate.Inmate(), libraryJob))
                    problems0.Add($"library: with no money {boughtBroke} books were bought (0 expected); the job has {libraryJob.Worker.Capacity(map)} places ({LibrarySystem.ClerkSlots(map)} clerk slots), a minimum security inmate may hold it {LaborUtility.Eligible(cc, libraryJob)}, a medium one {LaborUtility.Eligible(mediumInmate.Inmate(), libraryJob)}");

                // ---- the cart: two real inmates with a bed (their state is put back after) want a book; the clerk's round goes to the door of their cells ----
                List<Pawn> readers = map.mapPawns.PrisonersOfColonySpawned.Where(x => x != clerk && x != mediumInmate && InmateUtility.IsActiveInmate(x) && !x.Downed && x.Inmate().activeFlag
                                                                              && x.ownership?.OwnedBed != null && LibrarySystem.Literacy(x) != null).Take(2).ToList();
                if (readers.Count < 2)
                {
                    problems0.Add($"library: the cart needs two active inmates with a bed who can read, found {readers.Count}");
                    return;
                }
                (Pawn resident, Pawn other) = (readers[0], readers[1]);
                (CompInmate residentC, CompInmate otherC) = (resident.Inmate(), other.Inmate());
                var undo = new List<(CompInmate c, int books, int bookTicks, float literacy, InmateWant want, Dictionary<ProgramDef, int> progress)>();
                foreach (CompInmate c in new[] { residentC, otherC })
                    undo.Add((c, c.books, c.bookTicks, LibrarySystem.Literacy(c.Pawn).CurLevel, c.want, new Dictionary<ProgramDef, int>(c.programProgress)));
                r.Add(() =>
                {
                    foreach (var u in undo)
                    {
                        (u.c.books, u.c.bookTicks, LibrarySystem.Literacy(u.c.Pawn).CurLevel, u.c.want) = (u.books, u.bookTicks, u.literacy, u.want);
                        u.c.programProgress.Clear();
                        foreach (KeyValuePair<ProgramDef, int> kv in u.progress) u.c.programProgress[kv.Key] = kv.Value;
                    }
                });
                // (No goal and no class for them: a diploma or an education class would put one first, which is its own rule.)
                foreach (CompInmate c in new[] { residentC, otherC })
                {
                    c.want = null;
                    c.programProgress.Clear();
                    c.books = 0;
                }
                LibrarySystem.Literacy(resident).CurLevel = 0.4f;
                LibrarySystem.Literacy(other).CurLevel = 0.2f;
                List<Pawn> wanting = LibrarySystem.Recipients(map, null, 1000);
                bool both = wanting.Contains(resident) && wanting.Contains(other);
                // The stop is the door of the cell (a book goes through the hatch whether the cell is locked or not), and the clerk can walk to it.
                Thing stop = LibrarySystem.DeliveryPoint(resident);
                Room cell = resident.ownership.OwnedBed.GetRoom();
                bool atCellDoor = stop is Building_Door door && door.GetComp<CompPrisonDoor>() is { } dc && dc.cellRoom == cell;
                bool reachable = LibrarySystem.Recipients(map, clerk, 1000).Contains(resident);
                // Who goes first: the confined (their cell is locked and there is nothing else to do), then those working toward a diploma (the two, as inmates thrown away).
                Pawn shut = LiterateThrowaway(map, r, null), roamer = LiterateThrowaway(map, r, null), student = LiterateThrowaway(map, r, null);
                foreach (Pawn t in new[] { shut, roamer, student })
                {
                    LibrarySystem.Literacy(t).CurLevel = 0.3f;
                    t.Inmate().want = null;
                    t.Inmate().programProgress.Clear();
                }
                shut.Inmate().forcedActivity = RPDefOf.RP_Lockup;
                student.Inmate().programProgress[DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation")] = 2;
                float shutScore = LibrarySystem.Priority(shut, shut.Inmate()), roamerScore = LibrarySystem.Priority(roamer, roamer.Inmate()), studentScore = LibrarySystem.Priority(student, student.Inmate());
                bool confinedFirst = shutScore > roamerScore && studentScore > roamerScore;
                Job rounds = LibrarySystem.MakeRounds(clerk, pm);
                int stops = rounds?.GetTargetQueue(TargetIndex.B)?.Count ?? 0;
                bool roundOk = rounds != null && rounds.def == RPLibraryDefOf.RP_DeliverBooks && stops >= 1 && stops <= LibrarySystem.CartSize && rounds.targetA.Thing?.TryGetComp<CompBookShelf>() != null
                               && rounds.GetTargetQueue(TargetIndex.B).All(t => t.Pawn is { } rp && rp.Inmate() is { } rc && LibrarySystem.WantsBook(rp, rc));
                if (!both || !confinedFirst || !atCellDoor || !reachable || !roundOk || !libraryJob.Worker.HasWork(clerk))
                    problems0.Add($"library: the cart wants both inmates {both}, the confined and the diploma seeker go first {confinedFirst} ({shutScore:F2}/{studentScore:F2} against {roamerScore:F2}), stops at the cell door {atCellDoor} ({stop?.def.defName}), the clerk can walk there {reachable}, "
                                  + $"the round is {(rounds == null ? "none" : rounds.def.defName + " with " + stops + " stops")}, clerk has work {libraryJob.Worker.HasWork(clerk)}");
                // The round's driver builds its toils (not started: a pawn thrown away mid-walk leaves the pathfinder's worker threads with a pawn that is gone).
                int shelfBooks = shelves.Sum(s => s.books);
                int toils = -1;
                if (rounds != null)
                {
                    var driver = (JobDriver_DeliverBooks)rounds.MakeDriver(clerk);
                    driver.job = rounds;
                    toils = ((IEnumerable<Toil>)HarmonyLib.AccessTools.Method(typeof(JobDriver_DeliverBooks), "MakeNewToils").Invoke(driver, null)).Count();
                }
                if (rounds != null && (toils < 8 || shelves.Sum(s => s.books) != shelfBooks))
                    problems0.Add($"library: the round's driver built {toils} toils (8 or more expected), and the shelves held {shelfBooks} books before and {shelves.Sum(s => s.books)} after");
                // The book is handed over, once; the inmate then has one and no longer wants another.
                // (A real inmate's stash holds whatever the days before put there: the book hides nothing, so it is the same after.)
                int delivered0 = g.stats.booksDelivered, stash0 = residentC.stash.TotalStackCount;
                bool handed = LibrarySystem.Deliver(clerk, resident, hides: false), again = LibrarySystem.Deliver(clerk, resident, hides: false);
                if (!handed || again || residentC.books != 1 || g.stats.booksDelivered != delivered0 + 1 || LibrarySystem.WantsBook(resident, residentC) || residentC.stash.TotalStackCount != stash0)
                    problems0.Add($"library: a book handed over {handed}, a second one {again}; the inmate has {residentC.books} (1 expected), delivered {g.stats.booksDelivered - delivered0}, still wants one {LibrarySystem.WantsBook(resident, residentC)}, "
                                  + $"the stash went from {stash0} to {residentC.stash.TotalStackCount} items (the same expected: a book handed over with the dice at \"no\" hides nothing)");
                summary.Append($", cart route {stops} stops (confined first {confinedFirst}, at the cell door {atCellDoor})");

                // ---- reading: offered in the cell only; the need rises and the book is used up (on a thrown-away inmate: reading teaches) ----
                bool inCell = resident.GetRoom() == InmateUtility.CellOf(resident);
                bool offersRead = LibrarySystem.TryRead(resident, residentC) != null;
                (CompInmate shutC, Need readNeed) = (shut.Inmate(), LibrarySystem.Literacy(shut));
                shutC.books = 1;
                readNeed.CurLevel = 0.2f;
                float before = readNeed.CurLevel;
                int read0 = g.stats.booksRead;
                for (int i = 0; i < 2; i++) LibrarySystem.ReadTick(shut, shutC, LibrarySystem.BookTicks / 2);
                float gained = readNeed.CurLevel - before;
                if (offersRead != inCell || shutC.books != 0 || g.stats.booksRead != read0 + 1 || gained < 0.5f)
                    problems0.Add($"library: reading is offered in the cell only ({offersRead} while in the cell {inCell}); after a book's worth of reading they have {shutC.books} books, {g.stats.booksRead - read0} finished, literacy +{gained:F2}");
                summary.Append($", a book gives +{gained:F2} literacy");

                // ---- the exams: the education programs favour the well read, the rest do not ----
                SkillRecord intellect = clerk.skills.GetSkill(SkillDefOf.Intellectual);
                int level0 = intellect.Level;
                r.Add(() => intellect.Level = level0);
                intellect.Level = 0;
                ProgramDef foundation = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation"), rehab = DefDatabase<ProgramDef>.GetNamed("RP_SubstanceAbuse");
                literacy.CurLevel = 0f;
                float bare = ProgramSystem.PassChance(clerk, foundation), bareRehab = ProgramSystem.PassChance(clerk, rehab);
                literacy.CurLevel = 1f;
                float read = ProgramSystem.PassChance(clerk, foundation), readRehab = ProgramSystem.PassChance(clerk, rehab);
                literacy.CurLevel = 0.5f;
                float half = ProgramSystem.PassChance(clerk, foundation);
                if (Mathf.Abs(read - bare - foundation.literacyBonus) > 0.001f || Mathf.Abs(half - bare - foundation.literacyBonus / 2f) > 0.001f || bareRehab != readRehab || foundation.literacyBonus <= 0f || foundation.literacyBonus > 0.15f)
                    problems0.Add($"library: exam odds for foundation education {bare:P0} unread, {half:P0} half, {read:P0} well read (bonus {foundation.literacyBonus}); rehab {bareRehab:P0} / {readRehab:P0} (no change expected)");
                summary.Append($", the exam odds {bare:P0} -> {read:P0}");

                // ---- a book with contraband in it ends up in the stash, and a cell search finds it ----
                Pawn reader = LiterateThrowaway(map, r, null);
                CompInmate readerC = reader.Inmate();
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => GuardUtility.IsGuard(x));
                if (guard == null)
                {
                    problems0.Add("library: no guard to search the cell of a book with contraband in it");
                    return;
                }
                int hidden0 = g.stats.bookContraband, smuggled0 = g.stats.contrabandSmuggled, stashes0 = g.stats.stashesFound;
                LibrarySystem.Literacy(reader).CurLevel = 0.1f;
                bool hiding = LibrarySystem.Deliver(clerk, reader, hides: true);
                Thing hiddenItem = readerC.stash.FirstOrDefault();
                bool inStash = readerC.stash.Count == 1 && hiddenItem != null && ContrabandUtility.Ext(hiddenItem.def) is { kind: ContrabandKind.Drug };
                int searches = 0, foundItems = 0;
                while (foundItems == 0 && searches < 60)
                {
                    searches++;
                    foundItems = StashUtility.Search(guard, reader, readerC);
                }
                if (!hiding || !inStash || foundItems == 0 || readerC.stash.Count != 0 || g.stats.bookContraband != hidden0 + 1 || g.stats.contrabandSmuggled != smuggled0 + 1 || g.stats.stashesFound != stashes0 + 1
                    || LibrarySystem.ContrabandChance(readerC) > MailSystem.ContrabandChance(readerC))
                    problems0.Add($"library: a book with contraband: delivered {hiding}, in the stash {inStash} ({hiddenItem?.def.defName}), found after {searches} searches ({foundItems} items), stash now {readerC.stash.Count}, counted {g.stats.bookContraband - hidden0}/{g.stats.contrabandSmuggled - smuggled0}/{g.stats.stashesFound - stashes0}");
                summary.Append($", contraband in a book found at the cell after {searches} search(es)");

                // ---- the alert: a prison big enough wants a gym and a library, and says so ----
                var big = new PrisonServices.Report { inmates = PrisonServices.LibraryFrom + PrisonServices.GymFrom, benches = 0, shelves = 0 };
                List<string> bigLines = PrisonServices.GymAndLibraryLines(big).ToList();
                var equipped = big;
                (equipped.benches, equipped.shelves) = (PrisonServices.Needed(big.inmates, PrisonServices.InmatesPerBench), PrisonServices.Needed(big.inmates, PrisonServices.InmatesPerShelf));
                var small = new PrisonServices.Report { inmates = PrisonServices.GymFrom - 1 };
                var shortOne = big;
                (shortOne.benches, shortOne.shelves) = (1, 1);
                shortOne.inmates = PrisonServices.InmatesPerBench * 3;
                if (bigLines.Count != 2 || bigLines[0] != "RP_Problem_NoGym".Translate(big.inmates).ToString() || bigLines[1] != "RP_Problem_NoLibrary".Translate(big.inmates).ToString()
                    || PrisonServices.GymAndLibraryLines(equipped).Any() || PrisonServices.GymAndLibraryLines(small).Any()
                    || PrisonServices.GymAndLibraryLines(shortOne).FirstOrDefault() != "RP_Problem_FewBenches".Translate(1, shortOne.inmates, PrisonServices.InmatesPerBench).ToString())
                    problems0.Add($"library: the service lines for {big.inmates} inmates with no bench and no shelf are [{string.Join(" | ", bigLines)}] (a gym and a library expected), none when equipped or small, \"few benches\" when one bench serves {shortOne.inmates}");
                // ...and on this map, with the benches and shelves gone from the index, when the prison is big enough to need them.
                List<CompBookShelf> shelfIndex = pm.Shelves.ToList();
                foreach (CompFixture f in allBenches) pm.DeregisterFixture(f);
                foreach (CompBookShelf s in shelfIndex) pm.DeregisterShelf(s);
                r.Add(() =>
                {
                    foreach (CompFixture f in allBenches) pm.RegisterFixture(f);
                    foreach (CompBookShelf s in shelfIndex) pm.RegisterShelf(s);
                    PrisonServices.Reset();
                });
                PrisonServices.Reset();
                int realInmates = PrisonServices.Of(map).inmates;
                string[] realLines = PrisonServices.Lines(map).ToArray();
                bool mapGym = realLines.Contains("RP_Problem_NoGym".Translate(realInmates).ToString()), mapLibrary = realLines.Contains("RP_Problem_NoLibrary".Translate(realInmates).ToString());
                if (realInmates >= PrisonServices.GymFrom && !mapGym || realInmates >= PrisonServices.LibraryFrom && !mapLibrary)
                    problems0.Add($"library: with the benches and shelves gone, a prison of {realInmates} inmates is told about its gym {mapGym} and its library {mapLibrary}");
                summary.Append($", alert on a prison of {big.inmates}: gym and library named ({realInmates} inmates here: gym {mapGym}, library {mapLibrary})");
                RPLog.Message("[RimPrison:test] library: " + summary);
            });
        }

        // ---- save/load: the round trip of the new state ----------------------------------------------------------------------------------

        private static int libSeedInmateId = -1, libSeedShelf = -1;
        private static (int books, int bookTicks, int workouts, float exercise, float literacy) libSeedWas;
        private static int libSeedShelfWas;
        private static (int workouts, int delivered, int read, int hidden) libStatsWas;

        /// <summary>An inmate with a book half read and some workouts, a shelf with an odd count, and the counters.</summary>
        private static void SeedLibraryForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            Pawn reader = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() != null && ExerciseUtility.NeedOf(x) != null && LibrarySystem.Literacy(x) != null);
            CompBookShelf shelf = PrisonMap.Of(map).Shelves.FirstOrDefault();
            if (reader == null || shelf == null)
            {
                problems0.Add($"saveload: no inmate with the new needs ({reader != null}) or no book shelf ({shelf != null}) to seed the library state with");
                return;
            }
            CompInmate c = reader.Inmate();
            libSeedInmateId = reader.thingIDNumber;
            libSeedWas = (c.books, c.bookTicks, c.workouts, ExerciseUtility.NeedOf(reader).CurLevel, LibrarySystem.Literacy(reader).CurLevel);
            (c.books, c.bookTicks, c.workouts) = (2, 1234, 7);
            ExerciseUtility.NeedOf(reader).CurLevel = 0.37f;
            LibrarySystem.Literacy(reader).CurLevel = 0.58f;
            libSeedShelf = shelf.parent.thingIDNumber;
            libSeedShelfWas = shelf.books;
            shelf.books = 7;
            libStatsWas = (g.stats.workouts, g.stats.booksDelivered, g.stats.booksRead, g.stats.bookContraband);
            (g.stats.workouts, g.stats.booksDelivered, g.stats.booksRead, g.stats.bookContraband) = (41, 17, 9, 2);
            RPLog.Message($"[RimPrison:test] saveload library: {reader.LabelShort} has 2 books (1234 ticks into one), 7 workouts, exercise 0.37, literacy 0.58; a shelf holds 7 books");
        }

        /// <summary>The seeded state came back whole; then it goes (the rest of the plan must not run on it).</summary>
        private static void CheckLibraryAfterReload(Map map)
        {
            if (libSeedInmateId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            Pawn reader = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == libSeedInmateId);
            CompBookShelf shelf = PrisonMap.Of(map).Shelves.FirstOrDefault(s => s.parent.thingIDNumber == libSeedShelf);
            CompInmate c = reader?.Inmate();
            bool whole = c != null && shelf != null && c.books == 2 && c.bookTicks == 1234 && c.workouts == 7
                         && Mathf.Abs(ExerciseUtility.NeedOf(reader).CurLevel - 0.37f) < 0.01f && Mathf.Abs(LibrarySystem.Literacy(reader).CurLevel - 0.58f) < 0.01f && shelf.books == 7
                         && (g.stats.workouts, g.stats.booksDelivered, g.stats.booksRead, g.stats.bookContraband) == (41, 17, 9, 2);
            RPLog.Message($"[RimPrison:test] saveload library: {(whole ? "intact" : "changed")} ({c?.books} books, {c?.bookTicks} ticks, {c?.workouts} workouts, shelf {shelf?.books}, stats {g.stats.workouts}/{g.stats.booksDelivered}/{g.stats.booksRead}/{g.stats.bookContraband})");
            if (!whole) reloadProblems.Add($"library state changed in the round trip: books {c?.books}, ticks {c?.bookTicks}, workouts {c?.workouts}, shelf {shelf?.books}, stats {g.stats.workouts}/{g.stats.booksDelivered}/{g.stats.booksRead}/{g.stats.bookContraband}");
            if (c != null)
            {
                (c.books, c.bookTicks, c.workouts) = (libSeedWas.books, libSeedWas.bookTicks, libSeedWas.workouts);
                ExerciseUtility.NeedOf(reader).CurLevel = libSeedWas.exercise;
                LibrarySystem.Literacy(reader).CurLevel = libSeedWas.literacy;
            }
            if (shelf != null) shelf.books = libSeedShelfWas;
            (g.stats.workouts, g.stats.booksDelivered, g.stats.booksRead, g.stats.bookContraband) = libStatsWas;
            libSeedInmateId = -1;
        }

        /// <summary>A made-up inmate who can read (a few are barred from intellectual work by a backstory or a trait and have no literacy need): the next one is tried, the others go with the rollback.</summary>
        private static Pawn LiterateThrowaway(Map map, Rollback r, SecurityLevelDef security)
        {
            Pawn p = null;
            for (int i = 0; i < 12 && (p == null || LibrarySystem.Literacy(p) == null); i++) p = Throwaway(map, r, security);
            return p;
        }

        private static bool CarriesPlate(this CompInmate c) => c.Pawn.inventory.innerContainer.Any(t => t.def == RPExerciseDefOf.RP_WeightPlate);
    }
}
