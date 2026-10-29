using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the fix round after the balance and stability-round-3 reviews (WORKLOG "Review fixes"). Each is self-contained (throwaway pawns, the supermax scene's walled unit on open
    /// ground, everything put back by the <see cref="Rollback"/>) and fails without the fix it is named for.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckReviewFixes(Map map)
        {
            CheckStrandedMealReadsTheWalkNotTheCounters(map);
            CheckStrandedMealLooksNearTheInmateNotTheWholeRoom(map);
            CheckCellMealIsEatenOnceItFits(map);
            CheckTunnelIsNoticedOnlyBySomeoneThere(map);
            CheckDoctorsCarryTheDownedAfterTheStaffAndNotIntoAFight(map);
            CheckMedicsKeepTheirShiftWhileAway(map);
            CheckQuietedInmatesComeBackAsTheyWere(map);
            CheckHuddleSurvivesAThrownAwayPair(map);
            CheckBoardIsNotSentOnABreak(map);
            CheckSlowTransferIsNotLeftBehind(map);
            CheckSupermaxInmateWhoseGuardWasLostIsFetched(map);
        }

        private static string ReviewFail(string check, string what)
        {
            problems0.Add($"review fixes ({check}): {what}");
            return what;
        }

        /// <summary>A meal in the cook's own pockets, which a work giver's food search falls back on: the check does not depend on what lies in the prison's stores. (The cook stands in another room: a meal in a pocket is in the room of whoever carries it, and the giver does not bring food from the room the inmate is in.)</summary>
        private static Pawn CookWithAMeal(SupermaxScene s, IntVec3 at)
        {
            Pawn cook = s.Cook(at);
            Thing meal = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
            cook.inventory.innerContainer.TryAdd(meal);
            s.r.Add(() => meal.Destroy());
            return cook;
        }

        private static Job CellMealOffered(Map map, Pawn cook, Pawn inmate) => WithoutTheOtherCooks(map, cook, () => new WorkGiver_DeliverCellMeal().JobOnThing(cook, inmate));

        // ---- balance H1: stranded is no way to a counter, not no meal on one ------------------------------------------------------------------------------

        /// <summary>
        /// A free inmate below 25% with a canteen they can walk to is not carried a meal, when the counters happen to be empty (between cooking batches, or every stack reserved): the cooks fill the counters,
        /// and one meal a trip to wherever each hungry inmate stands would keep them empty. (It was "no meal on a counter", so every inmate below 25% anywhere counted as stranded.)
        /// </summary>
        private static void CheckStrandedMealReadsTheWalkNotTheCounters(Map map)
        {
            Guarded("review fix: the stranded meal reads the walk to a counter", map, r =>
            {
                const string check = "stranded meal and the walk";
                SupermaxScene s = SupermaxScene.Build(map, r, check, bait: true);
                if (s == null) return;
                // (The counters are empty: the canteen patch is in the corridor, and so is the inmate, with no cell to be in.)
                foreach (Thing meal in s.At(14, 1).GetThingList(map).Where(t => t.def.defName == "MealSimple").ToList()) meal.Destroy();
                Pawn cook = CookWithAMeal(s, s.CellB);
                Pawn p = s.Inmate("Zstranded1", null, s.CorridorWest);
                p.ownership?.UnclaimBed();
                CompInmate c = p.Inmate();
                c.security = InmateUtility.DefaultSecurity;
                p.needs.food.CurLevel = 0.2f;
                WorkGiver_DeliverCellMeal.ForgetCanteenReach();
                RegimeWorker_Eat.ForgetNoMeal();
                map.reachability.ClearCache();
                s.pm.ForceDoorUpdate();
                bool reaches = p.CanReach(s.At(14, 1), PathEndMode.OnCell, Danger.Deadly);
                Job job = CellMealOffered(map, cook, p);
                if (!reaches) RPLog.Message("[RimPrison:test] review fix: the inmate could not reach the scene's canteen patch, the check proves nothing");
                else if (job != null) s.Fail($"an inmate below 25% with a canteen patch to walk to (and no meal on it) was offered a meal where they stand ({job.def.defName}); the cooks fill the counters");
                // Shut away from every counter (the canteen patch gone), the same inmate is fed where they stand.
                var zoneWas = new List<IntVec3>(s.pm.zones.CellsOf(RPDefOf.RP_Canteen).Where(x => x.x >= s.origin.x && x.x < s.origin.x + SupermaxScene.Width && x.z >= s.origin.z && x.z < s.origin.z + SupermaxScene.Height));
                foreach (IntVec3 cell in zoneWas) s.pm.zones.Set(cell, null);
                r.Add(() => { foreach (IntVec3 cell in zoneWas) s.pm.zones.Set(cell, RPDefOf.RP_Canteen); });
                WorkGiver_DeliverCellMeal.ForgetCanteenReach();
                map.reachability.ClearCache();
                // (The rest of the prison's canteen is behind the unit's walls.)
                if (CellMealOffered(map, cook, p)?.def != JobDefOf.DeliverFood) s.Fail("an inmate below 25% with no cell and no way to any counter is not offered a meal where they stand");
            });
        }

        // ---- balance M4: the food test is near the inmate, not the whole room -----------------------------------------------------------------------------

        /// <summary>
        /// A meal lying far off in the same room (the outdoors, a long corridor) does not block the meal carried to a stranded inmate: only food within a few cells of them does (the check for the whole room
        /// walked every region of it for every hungry inmate on every cook's scan).
        /// </summary>
        private static void CheckStrandedMealLooksNearTheInmateNotTheWholeRoom(Map map)
        {
            Guarded("review fix: the stranded meal looks near the inmate", map, r =>
            {
                const string check = "food near the stranded";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                Pawn cook = CookWithAMeal(s, s.CellB);
                Pawn p = s.Inmate("Zstranded2", null, s.CorridorWest);
                p.ownership?.UnclaimBed();
                CompInmate c = p.Inmate();
                c.security = InmateUtility.DefaultSecurity;
                p.needs.food.CurLevel = 0.2f;
                WorkGiver_DeliverCellMeal.ForgetCanteenReach();
                map.reachability.ClearCache();
                s.pm.ForceDoorUpdate();
                Thing far = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
                GenSpawn.Spawn(far, s.CorridorEast, map);
                r.Add(() => { if (!far.Destroyed) far.Destroy(); });
                // (Forbidden: the cook's food search leaves it alone and brings the pocket meal, where the best meal on the floor of the same room would be turned down as "already in the room"; it is food in the room all the same.)
                far.SetForbidden(true, warnOnFail: false);
                Job withFarFood = CellMealOffered(map, cook, p);
                if (withFarFood?.def != JobDefOf.DeliverFood) s.Fail($"a stranded inmate was not offered a meal while another meal lay {s.CorridorEast.x - p.Position.x} cells away in the same room (job {withFarFood?.def.defName ?? "none"}, secure {p.guest.PrisonerIsSecure}, brought food {p.guest.CanBeBroughtFood}, stranded in the corridor room: cell {p.GetRoom()?.IsPrisonCell}; stranded rule {WorkGiver_DeliverCellMeal.StrandedWithoutMeal(p, p.GetRoom())}, walk to a canteen {WorkGiver_DeliverCellMeal.CanWalkToCanteen(p, s.pm)}, food near {WorkGiver_DeliverCellMeal.FoodNear(p)}, food in the room {WorkGiver_DeliverCellMeal.FoodInRoom(p.GetRoom())}, cell of their own {InmateUtility.CellOf(p) != null}, food {p.needs.food.CurLevelPercentage:P0}, the cook may deliver {WorkGiver_DeliverCellMeal.CookMayDeliver(cook)}, in the cook's room {cook.GetRoom() == p.GetRoom()}, can reach {cook.CanReach(p, PathEndMode.Touch, Danger.Deadly)}, fasting {HungerStrike.Fasting(c)}, active {c.activeFlag}, shut in {PrisonGame.Instance.LockedDown(c)})");
                // A meal at their feet (dropped for them) is enough: no second one.
                far.Position = p.Position + new IntVec3(2, 0, 0);
                if (CellMealOffered(map, cook, p) != null) s.Fail("a stranded inmate with a meal two cells away was offered another");
            });
        }

        // ---- balance M1: a cell meal is eaten once it fits --------------------------------------------------------------------------------------------------

        /// <summary>
        /// A meal is 0.9 of an adult's stomach. Lying in the inmate's own cell it is eaten once it fits (about 20%), not at 30%: eaten at 30% a fifth of every cell meal was lost over the cap, and the condemned,
        /// the punished and a lockdown needed a fifth more meals from a kitchen that only just keeps up.
        /// </summary>
        private static void CheckCellMealIsEatenOnceItFits(Map map)
        {
            Guarded("review fix: a cell meal is eaten once it fits", map, r =>
            {
                const string check = "cell meal fits";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                Pawn p = s.Inmate("Zfits1", s.bedA, s.CellA);
                Thing meal = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
                GenSpawn.Spawn(meal, s.At(3, 4), map);
                r.Add(() => { if (!meal.Destroyed) meal.Destroy(); });
                Need_Food food = p.needs.food;
                food.CurLevel = 0.28f;
                bool fitsAt28 = WorkGiver_DeliverCellMeal.MealFits(food, 0.9f);
                Job at28 = new JobGiver_InmateUrgentNeeds().TryIssueJobPackage(p, default).Job;
                food.CurLevel = 0.18f;
                bool fitsAt18 = WorkGiver_DeliverCellMeal.MealFits(food, 0.9f);
                Job at18 = new JobGiver_InmateUrgentNeeds().TryIssueJobPackage(p, default).Job;
                if (fitsAt28 || !fitsAt18) s.Fail($"the stomach test: a 0.9 meal fits at 28% ({fitsAt28}, no expected) and at 18% ({fitsAt18}, yes expected)");
                if (at28?.def == JobDefOf.Ingest) s.Fail("a meal in the cell was eaten at 28% (a fifth of it lost over a full stomach)");
                if (at18?.def != JobDefOf.Ingest || at18.targetA.Thing != meal) s.Fail($"a meal in the cell was not eaten at 18% (job {at18?.def.defName ?? "none"})");
            });
        }

        // ---- balance M2: a tunnel is noticed by someone who is there ----------------------------------------------------------------------------------------

        /// <summary>
        /// The guards notice a deep tunnel only with somebody near the cell (a guard within earshot, a camera or a tower over it): nobody on site, nothing to notice, so no "cell search" flag out of information
        /// no member of staff has. With a guard beside the cell the same roll does come up.
        /// </summary>
        private static void CheckTunnelIsNoticedOnlyBySomeoneThere(Map map)
        {
            Guarded("review fix: a tunnel is noticed by someone there", map, r =>
            {
                const string check = "tunnel noticed";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                Pawn p = s.Inmate("Ztunnel1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                c.security = InmateUtility.DefaultSecurity;
                ThingDef tools = DefDatabase<ThingDef>.GetNamed("RP_Tools");
                c.stash.TryAdd(ThingMaker.MakeThing(tools));
                r.Add(() => c.stash.ClearAndDestroyContents());
                c.tension = 60f;
                PrisonMap pm = s.pm;
                int noticed = 0;
                int Trials()
                {
                    int found = 0;
                    for (int i = 0; i < 1500; i++)
                    {
                        (c.tunnelProgress, c.tunnelKnown, c.cellSearchPending) = (0.9f, false, false);
                        TunnelSystem.Hourly(p, c, RPDefOf.RP_Lockup);
                        if (c.cellSearchPending) found++;
                    }
                    return found;
                }
                r.Add(() => (c.tunnelProgress, c.tunnelKnown, c.cellSearchPending) = (0f, false, false));
                if (!TunnelSystem.CanDig(p, c) || p.GetRoom() != InmateUtility.CellOf(p)) { RPLog.Message($"[RimPrison:test] review fix: the digger cannot dig here (can dig {TunnelSystem.CanDig(p, c)}), nothing proven"); return; }
                bool covered = TensionUtility.Watchers(p, pm) > 0f;
                if (covered) { RPLog.Message("[RimPrison:test] review fix: a camera or tower covers the scene, skipped"); return; }
                noticed = Trials();
                if (noticed > 0) s.Fail($"a tunnel was noticed {noticed} times in 1500 dig hours with no guard, camera or tower near the cell");
                Pawn guard = s.Guard("Ztunnelguard", s.CorridorWest);
                if (!pm.Guards.Contains(guard))
                {
                    pm.Guards.Add(guard);
                    r.Add(() => pm.Guards.Remove(guard));
                }
                noticed = Trials();
                if (noticed == 0) s.Fail("a tunnel at 90% was never noticed in 1500 dig hours with a guard beside the cell");
            });
        }

        // ---- balance M5: doctors carry the downed after the staff, and not into a fight ---------------------------------------------------------------------

        /// <summary>
        /// The doctors' rescue of a downed inmate is not an emergency and is below vanilla's rescue of a downed colonist (staff come first), and a doctor who is not a guard does not fetch one from beside a brawl
        /// (a downed member of staff is one more to carry), unless it is forced.
        /// </summary>
        private static void CheckDoctorsCarryTheDownedAfterTheStaffAndNotIntoAFight(Map map)
        {
            Guarded("review fix: the doctors' rescue", map, r =>
            {
                WorkGiverDef giver = DefDatabase<WorkGiverDef>.GetNamed("RP_RescueInmateDoctor");
                WorkGiverDef staffRescue = DefDatabase<WorkGiverDef>.GetNamed("DoctorRescue");
                if (giver.emergency || giver.priorityInType >= staffRescue.priorityInType)
                    ReviewFail("doctors' rescue", $"the doctors' rescue of an inmate is {(giver.emergency ? "an emergency, " : "")}priority {giver.priorityInType} against {staffRescue.priorityInType} for a downed member of staff (not an emergency, and lower, expected)");
                Pawn p = Throwaway(map, r);
                Pawn brawler = Throwaway(map, r);
                IntVec3 at = p.Position;
                if (!CellFinder.TryFindRandomCellNear(at, map, 3, x => x.Standable(map) && x.GetFirstPawn(map) == null, out IntVec3 beside)) return;
                brawler.Position = beside;
                brawler.Notify_Teleported();
                if (WorkGiver_RescueInmate.FightNear(p)) ReviewFail("doctors' rescue", "a fight was found beside an inmate with nobody fighting");
                if (!brawler.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true)) { RPLog.Message("[RimPrison:test] review fix: no berserk for the brawler, the fight test is skipped"); return; }
                if (!WorkGiver_RescueInmate.FightNear(p)) ReviewFail("doctors' rescue", "an inmate beside a berserk inmate has no fight near them");
                brawler.MentalState?.RecoverFromState();
            });
        }

        // ---- balance L1: a medic who is away keeps their shift ----------------------------------------------------------------------------------------------

        /// <summary>
        /// A doctor who is away (on leave) keeps the night shift for when they are back, as a guard does (they were reset to days, and nobody covered the night until the next day's balance); and one doctor
        /// of two does not go to nights (half would, and the busier day would have one).
        /// </summary>
        private static void CheckMedicsKeepTheirShiftWhileAway(Map map)
        {
            Guarded("review fix: medics keep their shift while away", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StaffRoleDef doctorRole = DefDatabase<StaffRoleDef>.GetNamed("RP_Doctor");
                Pawn away = ThrowawayStaff(map, r, doctorRole);
                CompStaff sc = away.GetComp<CompStaff>();
                sc.nightShift = true;
                int now = Find.TickManager.TicksGame;
                (sc.leaveUntilTick, sc.restPending) = (now + 3 * GenDate.TicksPerDay, true);
                int day0 = g.lastShiftBalanceDay;
                r.Add(() => g.lastShiftBalanceDay = day0);
                // (Not a balance day: the real staff's shifts are not reshuffled by the check.)
                g.lastShiftBalanceDay = Ledger.CurrentDay;
                if (!ShiftSystem.IsMedic(sc) || StaffCareers.Available(sc)) { RPLog.Message("[RimPrison:test] review fix: the doctor is not away or not a medic, skipped"); return; }
                ShiftSystem.Balance(map, g);
                if (!sc.nightShift) ReviewFail("medic away", "a doctor on leave was put back on days (the night is uncovered when they return until the next day's balance)");
                Pawn two1 = ThrowawayStaff(map, r, doctorRole), two2 = ThrowawayStaff(map, r, doctorRole), two3 = ThrowawayStaff(map, r, doctorRole);
                foreach (Pawn d in new[] { two1, two2, two3 }) d.GetComp<CompStaff>().nightShift = false;
                var two = new List<Pawn> { two1, two2 };
                ShiftSystem.Rebalance(two, true, ShiftSystem.MedicsForANightDoctor);
                if (two.Any(ShiftSystem.NightShift)) ReviewFail("two doctors", "with two doctors one went to nights (half of them)");
                var three = new List<Pawn> { two1, two2, two3 };
                ShiftSystem.Rebalance(three, true, ShiftSystem.MedicsForANightDoctor);
                if (three.Count(ShiftSystem.NightShift) != 1) ReviewFail("three doctors", $"with three doctors {three.Count(ShiftSystem.NightShift)} work nights (one expected)");
            });
        }

        // ---- stability H1: the quieted inmates come back as they were ---------------------------------------------------------------------------------------

        /// <summary>
        /// Shutting the real inmates in for a long check and letting them out again touches nothing it should not: a free inmate is let out; one already confined keeps their own confinement and its timer; one whose
        /// punishment began meanwhile keeps the product's sentence (it was written over with "free", the sentence lost, or an old confinement written back with no end, for good); a job taken off meanwhile is given back.
        /// </summary>
        private static void CheckQuietedInmatesComeBackAsTheyWere(Map map)
        {
            Guarded("review fix: the quieted inmates come back as they were", map, r =>
            {
                int now = Find.TickManager.TicksGame;
                Pawn free = Throwaway(map, r), confined = Throwaway(map, r), sentenced = Throwaway(map, r), worker = Throwaway(map, r);
                CompInmate cf = free.Inmate(), cc = confined.Inmate(), cs = sentenced.Inmate(), cw = worker.Inmate();
                foreach (CompInmate x in new[] { cf, cc, cs, cw }) (x.forcedActivity, x.forcedUntilTick, x.pendingPunishmentHours) = (null, -1, 0);
                (cc.forcedActivity, cc.forcedUntilTick) = (RPDefOf.RP_Lockup, now + 5000);
                InmateJobDef job = DefDatabase<InmateJobDef>.AllDefsListForReading.FirstOrDefault();
                cw.job = job;
                Action back = QuietTheRealInmates(map, new Pawn[0]);
                bool shut = cf.forcedActivity != null && cs.forcedActivity != null && cw.forcedActivity != null;
                if (!shut) ReviewFail("quiet", "the free inmates were not shut in for the check");
                if (cc.forcedActivity != RPDefOf.RP_Lockup || cc.forcedUntilTick != now + 5000) ReviewFail("quiet", $"an inmate who was already confined was touched ({cc.forcedActivity?.defName}, until {cc.forcedUntilTick - now} ticks on)");
                // (A punishment begins meanwhile: the product sets the confinement and its end.)
                (cs.forcedActivity, cs.forcedUntilTick) = (RPSecurityDefOf.RP_Solitary, now + 3000);
                // (The hourly assignment takes the job off the confined.)
                cw.job = null;
                back();
                if (cf.forcedActivity != null || cf.forcedUntilTick != -1) ReviewFail("quiet", $"a free inmate was left confined ({cf.forcedActivity?.defName}, until {cf.forcedUntilTick})");
                if (cc.forcedActivity != RPDefOf.RP_Lockup || cc.forcedUntilTick != now + 5000) ReviewFail("quiet", $"an inmate who was already confined was changed ({cc.forcedActivity?.defName}, until {cc.forcedUntilTick - now} ticks on)");
                if (cs.forcedActivity != RPSecurityDefOf.RP_Solitary || cs.forcedUntilTick != now + 3000) ReviewFail("quiet", $"a sentence that began during the check was undone ({cs.forcedActivity?.defName ?? "free"}, until {cs.forcedUntilTick - now} ticks on)");
                if (cw.forcedActivity != null || cw.job != job) ReviewFail("quiet", $"a worker did not get their job back ({cw.job?.defName ?? "none"}, confined {cw.forcedActivity?.defName})");
            });
        }

        // ---- stability M3: the greedy huddle ------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Four inmates on free time in a row: the first near only the second, the second, third and fourth together. The first's pair is thrown away (too small) and must not take the second off the list:
        /// the three of them are a table. (It was greedy, in list order, so card games started less often than intended and the check had to lock up everyone else.)
        /// </summary>
        private static void CheckHuddleSurvivesAThrownAwayPair(Map map)
        {
            Guarded("review fix: a thrown-away pair does not break a huddle", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                PrisonGame g = PrisonGame.Instance;
                int hour = GenLocalDate.HourOfDay(map);
                RegimeSchedule schedule = g.RegimeFor(InmateUtility.DefaultSecurity);
                RegimeActivityDef scheduled = schedule.At(hour);
                r.Add(() => schedule.Set(hour, scheduled));
                schedule.Set(hour, RPDefOf.RP_FreeTime);
                QuietTheRealInmates(map, r, new Pawn[0]);
                // A row of cells (in the yard, one room) five apart at most: a at 0, b at 5, c at 10 (not near a), d at 7.
                int[] along = { 0, 5, 10, 7 };
                IntVec3 start = IntVec3.Invalid;
                foreach (IntVec3 c in pm.zones.CellsOf(RPDefOf.RP_Yard))
                {
                    if (!c.Standable(map)) continue;
                    Room room = c.GetRoom(map);
                    if (room == null || Enumerable.Range(0, 11).All(k => { IntVec3 x = c + new IntVec3(k, 0, 0); return x.InBounds(map) && x.Standable(map) && x.GetFirstPawn(map) == null && x.GetRoom(map) == room && pm.zones.ZoneAt(x) == RPDefOf.RP_Yard; }))
                    {
                        start = c;
                        break;
                    }
                }
                if (!start.IsValid) { RPLog.Message("[RimPrison:test] review fix: no row of yard cells for the huddle check, skipped"); return; }
                var four = new Pawn[4];
                for (int i = 0; i < 4; i++)
                {
                    four[i] = Throwaway(map, r);
                    four[i].Inmate().forcedActivity = null;
                    four[i].Inmate().processed = true;
                    four[i].jobs.StopAll();
                    four[i].Position = start + new IntVec3(along[i], 0, 0);
                    four[i].Notify_Teleported();
                    if (four[i].needs.rest != null) four[i].needs.rest.CurLevel = 1f;
                }
                List<List<Pawn>> huddles = DebtSystem.Huddles(map, pm);
                bool together = huddles.Any(group => group.Contains(four[1]) && group.Contains(four[2]) && group.Contains(four[3]));
                if (!together) ReviewFail("huddle", $"b, c and d stand together and are not a huddle after a's pair was thrown away ({string.Join(" | ", huddles.Select(gp => string.Join(",", gp.Select(x => x.LabelShort))))}; awake {string.Join("/", four.Select(x => x.Awake()))}, free time {schedule.At(hour) == RPDefOf.RP_FreeTime})");
            });
        }

        // ---- stability M5: a break does not take the board out of a sitting --------------------------------------------------------------------------------

        /// <summary>The administrator on the board's job, or at the hearing room's desk while a case is seated, is busy: a staff break does not send them to the staff room mid-hearing.</summary>
        private static void CheckBoardIsNotSentOnABreak(Map map)
        {
            Guarded("review fix: the board is not sent on a break", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn admin = ThrowawayStaff(map, r, RPParoleDefOf.RP_Administrator);
                // (The hearing room's desk if there is one; else any building they can walk to: the sitting job is what is under test, not the room.)
                Thing desk = map.listerBuildings.AllBuildingsColonistOfDef(RPAdminDefOf.RP_OfficeDesk).FirstOrDefault(d => pm.zones.ZoneAt(d.Position) == RPParoleDefOf.RP_HearingRoom && !d.IsBurning() && admin.CanReserve(d))
                             // (Not one somebody is using: the nearest building was a toilet an inmate had reserved, and the sitting job's reservation failed with an error in the log.)
                             ?? map.listerBuildings.allBuildingsColonist.Where(b => b.def.hasInteractionCell && b.InteractionCell.Standable(map) && !b.IsBurning() && admin.CanReserve(b) && admin.CanReach(b.InteractionCell, PathEndMode.OnCell, Danger.Deadly))
                                 .OrderBy(b => b.Position.DistanceToSquared(admin.Position)).FirstOrDefault();
                if (desk == null) { RPLog.Message("[RimPrison:test] review fix: no building to sit at, the break check is skipped"); return; }
                admin.pather.StopDead();
                admin.Position = desk.InteractionCell;
                admin.Notify_Teleported();
                admin.jobs.StopAll();
                if (StaffBreaks.Busy(admin, map)) { RPLog.Message("[RimPrison:test] review fix: the administrator is busy with something else, skipped"); return; }
                admin.jobs.StartJob(JobMaker.MakeJob(RPParoleDefOf.RP_SitBoard, desk), JobCondition.InterruptForced);
                if (admin.CurJobDef != RPParoleDefOf.RP_SitBoard) { RPLog.Message($"[RimPrison:test] review fix: the sitting job did not start ({admin.CurJobDef?.defName}), skipped"); return; }
                if (!StaffBreaks.Busy(admin, map)) ReviewFail("board break", "an administrator on the board's sitting job is not busy for a staff break");
            });
        }

        // ---- stability M4: the transfer escort keeps pace with the inmate ----------------------------------------------------------------------------------

        /// <summary>
        /// A transferred inmate with a leg gone is walked to the ramp at their pace: the guard stops while they are more than a few cells behind, so the two are never far apart (the guard walked on ahead,
        /// twenty cells at the ramp), and they board.
        /// </summary>
        // (The guard keeps 6 steps ahead of the inmate on their way and looks every 10 ticks: 8 to 9 steps of walking apart in the 18 runs with the walk of the product as it is; the old rule, which went its own way, 12 to 16.
        // 14 was the limit before the guard followed the inmate's way, and a start where the old rule got only 12 steps ahead passed it. Those counts were by the guard's own way back; since stability round 21 the
        // steps are the inmate's way to the guard, which for a guard on the inmate's way six steps ahead is about seven, and no longer jumps when the guard's way goes round what the inmate walks through.)
        private const int SlowTransferMostSteps = 11;

        private static void CheckSlowTransferIsNotLeftBehind(Map map)
        {
            Guarded("review fix: a slow transfer is not left behind", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "slow transfer")) return;
                if (!ShuttleSystem.HasPad(map)) { RPLog.Message("[RimPrison:test] review fix: no pad, the slow transfer check is skipped"); return; }
                Pawn sent = Throwaway(map, r);
                float moving = SlowTheWalk(sent);
                Pawn guard = EscortGuard(map, r);
                QuietTheRealInmates(map, r, new[] { sent });
                sent.Inmate().processed = true;
                r.Add(() =>
                {
                    g.shuttle.Forget(sent);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(sent);
                });
                g.shuttle.Register(sent, OutboundKind.Transferred);
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed || v.shuttle is not { Spawned: true } shuttle) { ReviewFail("slow transfer", $"the shuttle did not land ({v?.Describe()})"); return; }
                if (g.shuttle.OutboundOf(sent) == null || !sent.CanReach(shuttle.InteractionCell, PathEndMode.OnCell, Danger.Deadly) || !guard.CanReach(shuttle.InteractionCell, PathEndMode.Touch, Danger.Deadly))
                {
                    RPLog.Message("[RimPrison:test] review fix: slow transfer skipped (the ramp cannot be reached from where they stand)");
                    return;
                }
                // (A long walk: the inmate waits somewhere thirty to forty steps from the ramp, so that a guard who walks on ahead of them is far in front by the ramp, and the walk is done before the shuttle goes. The intake is a short way from it.)
                // (Where the guard's own way to the ramp and the inmate's part furthest: two ways of the same length round a block. A guard that walks its own way to the ramp went round the one side and the slow
                // inmate round the other, sixteen steps from each other, in a third of the runs, for the start that happened to be picked. The start is the one where they part furthest, so that a run shows
                // the fault whenever it is there, and not by chance.)
                IntVec3 far = FarCellWhereTheWaysPartFurthest(map, sent, guard, shuttle.InteractionCell, out int ways);
                if (far.IsValid && ways >= 8) RPLog.Message($"[RimPrison:test] review fix: the slow transfer starts at {far}, where the guard's way to the ramp and the inmate's part for {ways} cells");
                else
                {
                    RPLog.Message($"[RimPrison:test] review fix: no start where the two ways to the ramp part for more than {ways} cells (8 needed to show it): the walk proves less this run");
                    far = IntVec3.Invalid;
                    foreach (IntVec3 c in PrisonMap.Of(map).zones.CellsOf(RPDefOf.RP_Yard).Concat(PrisonMap.Of(map).zones.CellsOf(RPDefOf.RP_Canteen)).InRandomOrder().Take(80))
                    {
                        if (!c.Standable(map) || c.GetFirstPawn(map) != null || !sent.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
                        using PawnPath path = map.pathFinder.FindPathNow(c, shuttle.InteractionCell, TraverseParms.For(sent, Danger.Deadly));
                        if (path.Found && path.NodesReversed.Count is >= 30 and <= 40) { far = c; break; }
                    }
                }
                if (far.IsValid)
                {
                    sent.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    sent.pather.StopDead();
                    sent.Position = far;
                    sent.Notify_Teleported(true, true);
                }
                else RPLog.Message("[RimPrison:test] review fix: no cell thirty to forty steps from the ramp for the slow transfer, the walk is short or long");
                var giver = new WorkGiver_EscortTransfer { def = DefDatabase<WorkGiverDef>.GetNamed("RP_EscortTransfer_WorkGiver") };
                if (!giver.HasJobOnThing(guard, sent) || giver.JobOnThing(guard, sent) is not { } job) { ReviewFail("slow transfer", "the guard is not offered the walk"); return; }
                // (Beside them: in their room within three cells, else within five cells and six steps of walking - a start in a doorway or a corridor's one-cell room has no cell of its room near. A guard left
                // where they stood walked thirty steps to the inmate first, the measure counted that walk as "ahead", and the walk to the ramp ran past the shuttle's stay.)
                bool Free(IntVec3 c) => c.Standable(map) && c.GetFirstPawn(map) == null && c != sent.Position && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly);
                int StepsTo(IntVec3 c)
                {
                    using PawnPath path = map.pathFinder.FindPathNow(c, sent.Position, TraverseParms.For(guard, Danger.Deadly));
                    return path.Found ? path.NodesReversed.Count : int.MaxValue;
                }
                bool placed = CellFinder.TryFindRandomCellNear(sent.Position, map, 3, c => Free(c) && c.GetRoom(map) == sent.GetRoom(), out IntVec3 beside)
                    || CellFinder.TryFindRandomCellNear(sent.Position, map, 5, c => Free(c) && StepsTo(c) <= 6, out beside);
                if (!placed) RPLog.Message($"[RimPrison:test] review fix: slow transfer: no cell beside the inmate at {sent.Position} for the guard; the walking-apart measure starts once the guard reaches them");
                if (placed)
                {
                    // (Stopped, not ended: a guard who ends a job beside the transfer thinks again at once and takes the escort in that call, before this check's own start.)
                    guard.jobs.StopAll();
                    guard.pather.StopDead();
                    guard.Position = beside;
                    guard.Notify_Teleported(true, true);
                }
                float guardSpeed = guard.GetStatValue(StatDefOf.MoveSpeed), sentSpeed = sent.GetStatValue(StatDefOf.MoveSpeed);
                float walk = (sent.Position - shuttle.InteractionCell).LengthHorizontal;
                // (The guard is taken off the prison's list of guards, as one who came on shift since the list was last made (every 250 ticks): the inmate's side found the guard by
                // that list, forgot the walk the moment it began, and stood beside the guard until they went on alone 1800 ticks later; with the shuttle's stay running out.)
                PrisonMap.Of(map).Guards.Remove(guard);
                guard.jobs.StartJob(job, JobCondition.InterruptForced);
                // (The guard reaches them first: set down within five cells, a few steps; the walk is on once the escort is the inmate's. Then they set out.)
                // (Kept off it, every tick, until the walk is the inmate's: the list is made again every 250 ticks, and the lookup it tests comes when the guard reaches them.)
                ShuttleTicks(() =>
                {
                    PrisonMap.Of(map).Guards.Remove(guard);
                    return g.shuttle.OutboundOf(sent) is { escortId: >= 0 };
                }, 600);
                ShuttleTicks(() => sent.pather.Moving, 120);
                if (placed && (g.shuttle.OutboundOf(sent) is not { escortId: >= 0 } || !sent.pather.Moving))
                    ReviewFail("slow transfer", $"the transfer did not set out with a guard who came on since the list of guards was made (escort {g.shuttle.OutboundOf(sent)?.escortId}, the inmate on {sent.CurJobDef?.defName}, moving {sent.pather.Moving}, the guard on {guard.CurJobDef?.defName})");
                float widest = 0f;
                int widestWalk = 0, sampled = 0;
                var sampledWalk = new List<string>();
                string guardJobs = "";
                string lastGuardJob = null;
                bool reached = placed;
                int ticks = ShuttleTicks(() =>
                {
                    if (guard.CurJobDef?.defName != lastGuardJob)
                    {
                        lastGuardJob = guard.CurJobDef?.defName;
                        guardJobs += $" [{ShNow}: {lastGuardJob ?? "none"} at {guard.Position}, the inmate at {sent.Position} doing {sent.CurJobDef?.defName}]";
                    }
                    if (!sent.Spawned) return true;
                    if (!reached && guard.Position.DistanceTo(sent.Position) <= 3f) reached = true;
                    if (guard.CurJobDef == RPShuttleDefOf.RP_EscortTransfer && reached)
                    {
                        widest = Mathf.Max(widest, guard.Position.DistanceTo(sent.Position));
                        // (By the way they walk: a straight line is short through a winding prison. The inmate's way to the guard, found with the inmate's own costs - the guard walks the inmate's way, six steps ahead on it
                        // (JobDriver_EscortTransfer.GoalFor). The guard's own cheapest way back weighs the cells for a quick walker and can go round what the slow one walks through: in rumours-ru1a the guard stood
                        // on the rule's goal, six steps ahead on the inmate's way, and the guard's way back to them read 12 steps for three samples, then 7 when the inmate took one more step. Both are in the line.)
                        if (++sampled % 15 == 0 && guard.Spawned)
                        {
                            using PawnPath path = map.pathFinder.FindPathNow(sent.Position, guard.Position, TraverseParms.For(sent, Danger.Deadly));
                            if (path.Found) widestWalk = Mathf.Max(widestWalk, path.NodesReversed.Count);
                            if (sampled % 60 == 0 && sampledWalk.Count < 120)
                            {
                                using PawnPath back = map.pathFinder.FindPathNow(guard.Position, sent.Position, TraverseParms.For(guard, Danger.Deadly));
                                // (Where the guard's rule would send them now, and whether they can get there: a goal on the inmate's way the guard cannot reach sends them their own way.)
                                IntVec3 ruleGoal = JobDriver_EscortTransfer.GoalFor(sent, shuttle.InteractionCell, 0, out bool ruleHold);
                                // (And how the guard walks to it: the cell, the ramp, or the inmate followed when the guard's own way to the cell goes round; with the two ways' steps.)
                                LocalTargetInfo walkTo = JobDriver_EscortTransfer.WalkTarget(guard, sent, shuttle.InteractionCell, 0, out _);
                                bool round = JobDriver_EscortTransfer.WayGoesRound(guard, sent, ruleGoal, out int ownWay, out int toThem) && !ruleHold;
                                sampledWalk.Add($"{sampled}: guard {guard.Position.x},{guard.Position.z} {(guard.pather.Moving ? "walking" : "standing")} to {guard.pather.Destination.Cell.x},{guard.pather.Destination.Cell.z}, inmate {sent.Position.x},{sent.Position.z} on {sent.CurJobDef?.defName}, {(path.Found ? path.NodesReversed.Count : -1)} steps apart by the inmate's way ({(back.Found ? back.NodesReversed.Count : -1)} by the guard's), rule goal {ruleGoal.x},{ruleGoal.z} hold {ruleHold} reach {guard.CanReach(ruleGoal, PathEndMode.OnCell, Danger.Deadly)}, walks to {(walkTo.HasThing ? "the inmate (following)" : walkTo.Cell.x + "," + walkTo.Cell.z)}{(round ? $" (own way {ownWay} steps, {toThem} to them)" : "")}");
                            }
                        }
                    }
                    return false;
                }, 6000);
                RPLog.Message($"[RimPrison:test] review fix: slow transfer: Moving {moving:F2}, speed {sentSpeed:F2} against the guard's {guardSpeed:F2}, {walk:F0} cells to the ramp, boarded {!sent.Spawned} after {ticks} ticks, never more than {widest:F1} cells apart ({widestWalk} steps of walking, by the inmate's way to the guard)");
                if (sent.Spawned) ReviewFail("slow transfer", $"the transfer was not aboard after {ticks} ticks (at {sent.Position}, the guard's job {guard.CurJobDef?.defName} at {guard.Position}); the guard's jobs{guardJobs}");
                if (widestWalk > SlowTransferMostSteps) ReviewFail("slow transfer", $"the guard walked on ahead of a slow inmate: {widestWalk} steps of walking apart (by the inmate's way to the guard), {widest:F1} cells in a straight line (about {JobDriver_EscortTransfer.FallBehind} steps allowed; the inmate's speed {sentSpeed:F2} against {guardSpeed:F2} over {walk:F0} cells); the walk, every 60 ticks: [{string.Join("] [", sampledWalk)}]");
                if (sentSpeed > guardSpeed * 0.8f || walk < 15f) RPLog.Message("[RimPrison:test] review fix: the slow transfer was not slow enough, or the walk too short, to prove the guard waits");
            });
        }

        // ---- stability M6: a supermax inmate whose guard was lost is fetched --------------------------------------------------------------------------------

        /// <summary>
        /// A guard who is taken by a mental break partway through a supermax walk leaves the inmate outside their cell with nobody (<see cref="EscortWalk.Stranded"/>): another guard then fetches them
        /// (<see cref="EscortKind.Home"/>) and sees them in, within a couple of hours. (The open case: an inmate stranded without a guard, seen once in three showcase runs; the made-up pawns have their
        /// mental-break traits taken off, so nothing reproduced it.)
        /// </summary>
        private static void CheckSupermaxInmateWhoseGuardWasLostIsFetched(Map map)
        {
            Guarded("review fix: a supermax inmate whose guard was lost is fetched", map, r =>
            {
                const string check = "lost guard";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn lost = s.Guard("Zlost1", s.CorridorWest);
                Pawn fetcher = s.Guard("Zfetch1", s.CorridorEast);
                Pawn p = s.Inmate("Zstray1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                InmateCandidates.Shared.Invalidate();
                var giver = new WorkGiver_EscortInmate();
                if (!giver.HasJobOnThing(lost, p) || giver.JobOnThing(lost, p) is not { } job) { s.Fail("the first guard was not offered the walk to the shower"); return; }
                s.Start(lost, job);
                int ticked = 0;
                // The walk is on, the inmate is out of the cell: the guard breaks.
                while (ticked < 1500 && !(c.leg != null && !s.InCell(p, s.bedA) && lost.CurJobDef == RPSupermaxDefOf.RP_EscortInmate))
                {
                    Find.TickManager.DoSingleTick();
                    ticked++;
                }
                if (c.leg == null || s.InCell(p, s.bedA)) { s.Fail($"the walk never began ({ticked} ticks, leg {c.leg?.phase.ToString() ?? "none"}, the guard does {lost.CurJobDef?.defName})"); return; }
                if (!lost.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true)) { s.Fail("the guard could not be given a mental break"); return; }
                // The second guard is on Warden work now, and takes the walk home by themselves.
                fetcher.workSettings.SetPriority(WorkTypeDefOf.Warden, 1);
                bool stranded = false, sawHome = false;
                int brokeAt = ticked;
                var trace = new System.Text.StringBuilder();
                while (ticked - brokeAt < 5000 && !(sawHome && s.InCell(p, s.bedA) && c.leg == null))
                {
                    Find.TickManager.DoSingleTick();
                    ticked++;
                    stranded |= EscortWalk.Stranded(p, c);
                    sawHome |= c.leg is { kind: EscortKind.Home };
                    if ((ticked - brokeAt) % 300 == 1) trace.Append($" [{ticked - brokeAt}: inmate {p.Position - s.origin} {p.CurJobDef?.defName} leg {(c.leg == null ? "-" : c.leg.kind + "/" + c.leg.phase)} stranded {EscortWalk.Stranded(p, c)} retry in {c.escortRetryTick - s.Now}, due {SupermaxSystem.DueFor(p, c, g)}, candidate {SupermaxSystem.Candidate(p)}; fetcher {fetcher.CurJobDef?.defName} at {fetcher.Position - s.origin}, offered {giver.HasJobOnThing(fetcher, p)}]");
                }
                bool fetched = sawHome && s.InCell(p, s.bedA) && c.leg == null;
                if (!fetched)
                    s.Fail($"the inmate whose guard was lost was not fetched home: after {ticked - brokeAt} ticks stranded {stranded}, a home walk seen {sawHome}, in the cell {s.InCell(p, s.bedA)}, leg {c.leg?.kind.ToString() ?? "none"}; the lost guard is {lost.MentalStateDef?.defName ?? "calm"}; trace{trace}");
            });
        }
    }
}
