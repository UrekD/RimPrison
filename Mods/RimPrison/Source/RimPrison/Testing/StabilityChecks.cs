using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks from the events plan's stability work (see WORKLOG): a bug found while looking for the cause of a flake, with the check that fails without its fix.
    /// Each runs in its own <see cref="Guarded"/> scope (the rollback puts back what it touches) from <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>
        /// A treat bought at the commissary by an inmate who already carries one of them joins their stack (the new item is gone as a thing of its own): the job to eat it on the spot
        /// is then on what is in their pockets. It was on the item that no longer existed, so the inmate paid and the job was dropped (and nothing in the job's own start could say why).
        /// </summary>
        private static void CheckCommissaryPurchaseEatsFromAHeldStack(Map map)
        {
            Guarded("commissary: a purchase joins a held stack", map, r =>
            {
                ThingDef treat = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d => d.IsIngestible && d.HasModExtension<CommissaryItemExtension>());
                if (treat == null)
                {
                    problems0.Add("commissary: no ingestible item with a price to buy");
                    return;
                }
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.cash = 1000;
                IntVec3 cell = CellFinder.StandableCellNear(p.Position, map, 6f);
                string[] outcomes = new string[2];
                for (int held = 0; held < 2; held++)
                {
                    foreach (Thing old in p.inventory.innerContainer.Where(t => t.def == treat).ToList()) old.Destroy();
                    if (held == 1) p.inventory.innerContainer.TryAdd(MakeStack(treat, 1));
                    Thing stack = MakeStack(treat, 3);
                    GenSpawn.Spawn(stack, cell, map);
                    r.Add(() =>
                    {
                        if (!stack.Destroyed) stack.Destroy();
                    });
                    bool bought = CommissarySystem.Purchase(p, stack, out Job eat);
                    Thing target = eat?.targetA.Thing;
                    bool inPockets = target is { Destroyed: false } && p.inventory.innerContainer.Contains(target);
                    int carried = p.inventory.innerContainer.Where(t => t.def == treat).Sum(t => t.stackCount);
                    outcomes[held] = $"{(held == 1 ? "joining a stack of one" : "alone")}: bought {bought}, job {eat?.def.defName ?? "none"} on {(target == null ? "nothing" : target.Destroyed ? "a thing that is gone" : inPockets ? "what is in their pockets" : "something outside them")}, carrying {carried} (1 or 2 expected)";
                    if (!bought || eat?.def != JobDefOf.Ingest || !inPockets || carried != held + 1) problems0.Add($"commissary: a purchase {outcomes[held]}");
                }
                RPLog.Message($"[RimPrison:test] commissary purchase: {outcomes[0]}; {outcomes[1]}");
            });
        }

        /// <summary>
        /// A class in work hours does not take the laundry worker while clean uniforms are short (two days' worth or less): there is washing to do. It did (laundry said it never had
        /// work waiting), and once class starts pulled students off their jobs every time the one laundry worker sat in class all day: nobody washed, the outside service did it all.
        /// With the stock full they may go to class.
        /// </summary>
        private static void CheckLaundryWorkerKeptFromClassWhileUniformsAreShort(Map map)
        {
            Guarded("laundry: the worker is not taken by a class while uniforms are short", map, r =>
            {
                InmateJobDef laundryJob = DefDatabase<InmateJobDef>.AllDefsListForReading.FirstOrDefault(j => j.Worker is InmateJobWorker_Laundry);
                ProgramDef workClass = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(d => d.Slot == RPLaborDefOf.RP_Work);
                if (laundryJob == null || workClass == null)
                {
                    problems0.Add($"laundry worker: no laundry job ({laundryJob != null}) or no class in work hours ({workClass != null})");
                    return;
                }
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.job = laundryJob;
                LaundrySystem laundry = PrisonGame.Instance.laundry;
                int stock0 = laundry.stock;
                r.Add(() => laundry.stock = stock0);
                int inmates = map.mapPawns.PrisonersOfColonySpawnedCount;
                laundry.stock = 0;
                bool keptWhenShort = ProgramSystem.KeepsToTheirJob(workClass, p, c);
                laundry.stock = 2 * inmates;
                bool keptWhenFull = ProgramSystem.KeepsToTheirJob(workClass, p, c);
                RPLog.Message($"[RimPrison:test] laundry worker: kept from a {workClass.defName} class with no clean uniforms {keptWhenShort}, with two days' worth {keptWhenFull}");
                if (!keptWhenShort || keptWhenFull) problems0.Add($"laundry worker: kept from class with the uniforms short {keptWhenShort} (true expected), with the stock full {keptWhenFull} (false expected)");
            });
        }

        /// <summary>
        /// An inmate in their cell, about to drink a beer from their pockets: the stash pass does not tuck that beer away. It did (the pass moves every contraband item out of the
        /// pockets into the cell's stash when nobody is watching), and the Ingest job then found the beer gone: vanilla's "Can't transfer item Beer ... not here", an error in the log.
        /// </summary>
        private static void CheckStashLeavesWhatTheyAreUsing(Map map)
        {
            Guarded("stash: what an inmate is about to use stays in the pockets", map, r =>
            {
                ThingDef beer = DefDatabase<ThingDef>.GetNamedSilentFail("Beer");
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                if (beer == null || ContrabandUtility.Ext(beer) == null)
                {
                    problems0.Add("stash in use: beer is not contraband here, nothing to check");
                    return;
                }
                Thing drink = MakeStack(beer, 1);
                p.inventory.innerContainer.TryAdd(drink, canMergeWithExistingStacks: false);
                // The drink waits in the job queue (started, its first toil takes it from the pockets into the hands at once: the race is the moment before that).
                Job ingest = JobMaker.MakeJob(JobDefOf.Ingest, drink);
                ingest.count = 1;
                p.jobs.jobQueue.EnqueueLast(ingest);
                r.Add(() => p.jobs.ClearQueuedJobs());
                // (The pass's hiding step itself, as when nobody is watching: it would move everything.)
                StashUtility.TuckAway(p, c, p.inventory.innerContainer.ToList(), 0);
                bool kept = p.inventory.innerContainer.Contains(drink), stashed = c.stash.Contains(drink);
                RPLog.Message($"[RimPrison:test] stash in use: a queued drink still in the pockets {kept}, in the stash {stashed}");
                if (!kept || stashed) problems0.Add("stash: the beer an inmate was about to drink was tucked into the stash (the Ingest finds it gone: 'Can't transfer item ... not here')");
            });
        }

        /// <summary>
        /// Praying in the cell is on the cell they stand in, which the job reserves: with that cell reserved by somebody else's prayer the inmate is not given the job (it was, and a
        /// job whose reservation fails is an error in the log, once for each time it is tried: three in a second for one inmate in the events plan). With it free, they pray.
        /// </summary>
        private static void CheckPrayerInTheCellNeedsTheCellFree(Map map)
        {
            Guarded("faith: a prayer in the cell needs the cell free", map, r =>
            {
                Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true } && InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState && x.ownership?.OwnedBed != null);
                if (p == null)
                {
                    problems0.Add("faith: no active inmate with a bed to pray in the cell");
                    return;
                }
                CompInmate c = p.Inmate();
                Room cell = p.ownership.OwnedBed.GetRoom();
                if (cell == null || !CellFinder.TryFindRandomCellNear(p.ownership.OwnedBed.Position, map, 2, x => x.GetRoom(map) == cell && x.Standable(map) && x.GetFirstPawn(map) == null, out IntVec3 inside))
                {
                    problems0.Add("faith: no free cell in the inmate's cell to pray in");
                    return;
                }
                (bool religious0, IntVec3 at0) = (c.religious, p.Position);
                c.religious = true;
                p.needs.AddOrRemoveNeedsAsAppropriate();
                Need faith = FaithUtility.Need(p);
                if (faith == null)
                {
                    c.religious = religious0;
                    p.needs.AddOrRemoveNeedsAsAppropriate();
                    problems0.Add("faith: the inmate has no faith need to pray for");
                    return;
                }
                float faith0 = faith.CurLevel;
                r.Add(() =>
                {
                    faith.CurLevel = faith0;
                    c.religious = religious0;
                    p.needs.AddOrRemoveNeedsAsAppropriate();
                    p.pather.StopDead();
                    p.Position = at0;
                    p.Notify_Teleported();
                });
                faith.CurLevel = 0.1f;
                p.pather.StopDead();
                p.Position = inside;
                p.Notify_Teleported();
                // (Nobody holds that cell yet: the inmate under test, or one of their cellmates, may be praying on it already, and then the throwaway below cannot reserve it ("Could not reserve ... for job
                // RP_Pray", "reserved False": the flake of the events runs). The claims of the check's own are put right, the prayer is not the check's to judge.)
                p.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                foreach (ReservationManager.Reservation claim in map.reservationManager.ReservationsReadOnly.Where(rv => rv.Target.Cell == inside && !rv.Target.HasThing).ToList())
                    map.reservationManager.Release(claim.Target, claim.Claimant, claim.Job);
                // Somebody else is praying on that very cell.
                Pawn other = Throwaway(map, r);
                other.pather.StopDead();
                other.Position = CellFinder.StandableCellNear(inside, map, 6f);
                other.Notify_Teleported();
                other.jobs.StartJob(JobMaker.MakeJob(RPFaithDefOf.RP_Pray, inside), JobCondition.InterruptForced);
                r.Add(() => other.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false));
                bool reserved = !p.CanReserve(inside);
                Job whileReserved = FaithUtility.TryPrayInCell(p, c);
                other.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                Job whenFree = FaithUtility.TryPrayInCell(p, c);
                if (!reserved || whileReserved != null || whenFree?.def != RPFaithDefOf.RP_Pray)
                    problems0.Add($"faith: praying in the cell with the cell reserved by another (reserved {reserved}): the job offered is {whileReserved?.def.defName ?? "none"} (none expected), and {whenFree?.def.defName ?? "none"} once it is free (RP_Pray expected)");
            });
        }

        private static Thing MakeStack(ThingDef def, int count)
        {
            Thing t = ThingMaker.MakeThing(def);
            t.stackCount = count;
            return t;
        }
    }
}
