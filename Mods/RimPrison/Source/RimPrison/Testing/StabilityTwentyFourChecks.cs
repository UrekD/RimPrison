using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks and test helpers from stability round 24 (see WORKLOG): the product bugs found in census c84's lines, each with the check that fails without its fix, and the lines made to tell the
    /// rest. Each runs in its own <see cref="Guarded"/> scope from <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        private static void S24Fail(string check, string what) => problems0.Add($"stability 24 ({check}): {what}");

        /// <summary>
        /// A walker at about a third of the pace and not down: one leg gone, the other burnt until Moving is under 0.38 (a guard at a walk is not the quick one a lame inmate falls far behind, and a missing
        /// leg alone was only half). Returns their Moving.
        /// </summary>
        private static float SlowTheWalk(Pawn p)
        {
            p.health.Reset();
            BodyPartRecord leg = p.RaceProps.body.AllParts.First(part => part.def == BodyPartDefOf.Leg);
            p.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, p, leg));
            BodyPartRecord otherLeg = p.RaceProps.body.AllParts.Where(part => part.def == BodyPartDefOf.Leg && part != leg).FirstOrDefault();
            if (otherLeg != null)
            {
                var burn = (Hediff_Injury)HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Burn"), p, otherLeg);
                p.health.AddHediff(burn);
                for (float severity = 2f; severity <= 20f && !p.Downed; severity += 1f)
                {
                    burn.Severity = severity;
                    p.health.capacities.Notify_CapacityLevelsDirty();
                    if (p.health.capacities.GetLevel(PawnCapacityDefOf.Moving) < 0.38f) break;
                }
            }
            return p.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
        }

        /// <summary>The corner of a free, unroofed, unzoned rectangle of open ground outside the compound (with a cell of margin), or invalid.</summary>
        private static IntVec3 OpenGroundFor(Map map, int width, int height)
        {
            CellRect compound = PrisonBuilder.Footprint(map).ExpandedBy(3);
            PrisonMap pm = PrisonMap.Of(map);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 79f, true))
            {
                CellRect around = new CellRect(c.x, c.z, width, height).ExpandedBy(1);
                if (!around.InBounds(map) || around.Overlaps(compound)) continue;
                bool open = true;
                foreach (IntVec3 x in around)
                    if (!x.Walkable(map) || x.Roofed(map) || x.GetEdifice(map) != null || x.GetTerrain(map).IsWater || pm.zones.ZoneAt(x) != null
                        || x.GetThingList(map).Any(t => t is Pawn || t.def.category is ThingCategory.Item or ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame))
                    {
                        open = false;
                        break;
                    }
                if (open) return c;
            }
            return IntVec3.Invalid;
        }

        private static T S24Spawn<T>(Map map, Rollback r, ThingDef def, IntVec3 cell) where T : Thing
        {
            var t = (T)ThingMaker.MakeThing(def, def.MadeFromStuff ? (def == ThingDefOf.Wall ? ThingDefOf.BlocksGranite : GenStuff.DefaultStuffFor(def)) : null);
            if (def.CanHaveFaction) t.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(t, cell, map);
            r.Add(() =>
            {
                if (t.Spawned) t.Destroy(DestroyMode.Vanish);
            });
            return t;
        }

        private static void S24Place(Pawn p, IntVec3 cell)
        {
            p.jobs.StopAll();
            p.pather.StopDead();
            p.Position = cell;
            p.Notify_Teleported(true, true);
        }

        // ---- 1. an inmate taken to a bed does not walk on to a meal they hold no claim on --------------------------------------------------------------------------

        /// <summary>
        /// Vanilla's escort of an inmate to a bed (a guard's <c>EscortPrisonerToBed</c> for one with no bed of their own) clears every claim the inmate holds as it starts, and used to leave them on the job the
        /// claims were for (<see cref="Patch_JobDriver_TakeToBed_TakeeLosesTheJobWithItsClaims"/>): an inmate on the way to a meal walked on to it holding nothing, a doctor feeding a patient took it as free, and the
        /// pick-up could not reserve it (census-c84-sc: "Could not reserve Thing_MealSimple32097 ... for Smalls for job Ingest", the doctor's FeedPatient its only reserver). Without the fix the inmate is still on
        /// the Ingest with no claim on the meal.
        /// </summary>
        private static void CheckEscortToBedLeavesNoMealUnclaimed(Map map)
        {
            Guarded("stability 24: an inmate escorted to a bed is not left walking to a meal they hold no claim on", map, r =>
            {
                const string check = "escort to bed";
                IntVec3 o = OpenGroundFor(map, 8, 3);
                if (!o.IsValid)
                {
                    RPLog.Message("[RimPrison:test] stability 24: no open ground for the escort-to-bed check, not checked");
                    return;
                }
                IntVec3 At(int x, int z) => new IntVec3(o.x + x, 0, o.z + z);
                Pawn inmate = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.health.Reset();
                Thing meal = S24Spawn<Thing>(map, r, ThingDefOf.MealSimple, At(6, 1));
                var bed = S24Spawn<Building_Bed>(map, r, ThingDefOf.SleepingSpot, At(2, 2));
                bed.ForPrisoners = true;
                r.Add(() =>
                {
                    foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                });
                S24Place(inmate, At(0, 1));
                S24Place(guard, At(1, 0));
                Job eat = JobMaker.MakeJob(JobDefOf.Ingest, meal);
                eat.count = 1;
                inmate.jobs.StartJob(eat, JobCondition.InterruptForced);
                if (inmate.CurJob != eat || !map.reservationManager.ReservedBy(meal, inmate, eat))
                {
                    S24Fail(check, $"premise: the inmate is not on the way to the meal with a claim on it (job {inmate.CurJobDef?.defName}, claim {map.reservationManager.ReservedBy(meal, inmate)}, faction {inmate.Faction?.Name})");
                    return;
                }
                Job escort = JobMaker.MakeJob(JobDefOf.EscortPrisonerToBed, inmate, bed);
                escort.count = 1;
                guard.jobs.StartJob(escort, JobCondition.InterruptForced);
                if (guard.CurJob != escort)
                {
                    S24Fail(check, $"premise: the guard's escort to the bed did not start (job {guard.CurJobDef?.defName})");
                    return;
                }
                Job now = inmate.CurJob;
                bool unclaimed = now is { } j && j.def == JobDefOf.Ingest && j.targetA.Thing == meal && !map.reservationManager.ReservedBy(meal, inmate, j);
                RPLog.Message($"[RimPrison:test] stability 24: escort to bed - the inmate was on Ingest of the meal with a claim; the escort began, and they are on {now?.def.defName ?? "nothing"} (the same job {now == eat}), "
                              + $"claim on the meal {map.reservationManager.ReservedBy(meal, inmate)}, staff could take the meal {guard.CanReserve(meal, 10, 1)}");
                if (unclaimed)
                    S24Fail(check, $"the escort to a bed cleared the inmate's claims and left them walking to the meal holding none (job {now.def.defName}, the same {now == eat}): anyone may take it - a doctor's FeedPatient "
                                   + $"did, and the pick-up failed (\"Could not reserve ... for job Ingest\"); staff can take it now {guard.CanReserve(meal, 10, 1)}");
                guard.jobs.StopAll();
                inmate.jobs.StopAll();
            });
        }

        // ---- 4. the transfer escort follows when its own way to the goal goes round ---------------------------------------------------------------------------------------

        /// <summary>
        /// A quick guard and a slow transfer at a fork: the inmate's way runs straight on through three sandbags and two cells of mud, the guard's cheapest way to the cell six steps on along it goes round by a
        /// loop of clean floor (the pathfinder weighs the cells against the walker's pace). The rule's cell is right; handed it, the guard walks away from the inmate (census-c84-ev2's slow transfer: the goal on
        /// the inmate's way at every look, the guard round the block, 22 steps apart by the inmate's way). <see cref="JobDriver_EscortTransfer.WalkTarget"/> sends them after the inmate instead. Without the fix
        /// (the cell handed to the pather, as <c>LeadAtTheirPace</c> did) the guard's own way to its target is the loop.
        /// </summary>
        private static void CheckEscortFollowsWhenItsWayGoesRound(Map map)
        {
            Guarded("stability 24: a transfer escort whose own way to the goal goes round follows the inmate", map, r =>
            {
                const string check = "escort way round";
                const int W = 17, H = 7;
                IntVec3 o = OpenGroundFor(map, W, H);
                if (!o.IsValid)
                {
                    RPLog.Message("[RimPrison:test] stability 24: no open ground for the escort's loop, not checked");
                    return;
                }
                IntVec3 At(int x, int z) => new IntVec3(o.x + x, 0, o.z + z);
                // The way on (z 1, x 1 to 15), the loop up at x 3, along z 5 and down at x 9; walls everywhere else in the rectangle.
                bool Open(int x, int z) => z == 1 && x is >= 1 and <= 15 || (x == 3 || x == 9) && z is >= 2 and <= 5 || z == 5 && x is >= 3 and <= 9;
                var mud = new HashSet<(int, int)> { (5, 1), (7, 1) };
                var bags = new HashSet<(int, int)> { (4, 1), (6, 1), (8, 1) };
                foreach (IntVec3 c in new CellRect(o.x, o.z, W, H).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                TerrainDef mudDef = DefDatabase<TerrainDef>.GetNamedSilentFail("Mud");
                ThingDef bagDef = DefDatabase<ThingDef>.GetNamedSilentFail("Sandbags");
                if (mudDef == null || bagDef == null)
                {
                    S24Fail(check, $"premise: no mud ({mudDef != null}) or sandbags ({bagDef != null}) to make the slow way");
                    return;
                }
                for (int x = 0; x < W; x++)
                    for (int z = 0; z < H; z++)
                    {
                        IntVec3 c = At(x, z);
                        TerrainDef was = map.terrainGrid.TerrainAt(c);
                        r.Add(() => map.terrainGrid.SetTerrain(c, was));
                        map.terrainGrid.SetTerrain(c, mud.Contains((x, z)) ? mudDef : TerrainDefOf.Concrete);
                        // (Snow costs what the deepest of the ground, a thing on it and the snow costs: a winter's snow would make the clean floor as slow as the mud.)
                        float snow = map.snowGrid.GetDepth(c);
                        if (snow > 0f)
                        {
                            r.Add(() => map.snowGrid.SetDepth(c, snow));
                            map.snowGrid.SetDepth(c, 0f);
                        }
                        if (!Open(x, z)) S24Spawn<Thing>(map, r, ThingDefOf.Wall, c);
                        else if (bags.Contains((x, z))) S24Spawn<Thing>(map, r, bagDef, c);
                    }
                map.reachability.ClearCache();
                r.Add(() => map.reachability.ClearCache());
                Pawn inmate = Throwaway(map, r);
                float moving = SlowTheWalk(inmate);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.health.Reset();
                IntVec3 ramp = At(15, 1);
                S24Place(guard, At(2, 1));
                S24Place(inmate, At(3, 1));
                // (At a walk, as the transfer's own walk is: the pathfinder weighs the cells against the pace.)
                Job walk = JobMaker.MakeJob(JobDefOf.Goto, ramp);
                walk.locomotionUrgency = LocomotionUrgency.Walk;
                inmate.jobs.StartJob(walk, JobCondition.InterruptForced);
                ShuttleTicks(() =>
                {
                    if (guard.CurJob != null) guard.jobs.StopAll();
                    return inmate.pather.Moving && inmate.pather.curPath is { Found: true };
                }, 30);
                // (Back behind the fork, wherever the think tree's jobs of those ticks took them.)
                S24Place(guard, At(2, 1));
                if (!inmate.pather.Moving || inmate.pather.curPath is not { Found: true } way)
                {
                    S24Fail(check, $"premise: the slow inmate would not walk the way (job {inmate.CurJobDef?.defName}, at {inmate.Position - o}, Moving {moving:F2})");
                    return;
                }
                List<IntVec3> theirs = way.NodesReversed.ToList();
                string Cells(IEnumerable<IntVec3> cells) => string.Join(" ", cells.Select(c => $"{c.x - o.x},{c.z - o.z}"));
                if (!theirs.Contains(At(6, 1)))
                {
                    S24Fail(check, $"premise: the slow inmate's way does not go through the sandbags but [{Cells(theirs)}] (speed {inmate.GetStatValue(StatDefOf.MoveSpeed):F2})");
                    return;
                }
                IntVec3 goal = JobDriver_EscortTransfer.GoalFor(inmate, ramp, 0, out bool hold);
                bool round = JobDriver_EscortTransfer.WayGoesRound(guard, inmate, goal, out int own, out int toThem);
                using (PawnPath guardWay = map.pathFinder.FindPathNow(guard.Position, goal, TraverseParms.For(guard, Danger.Deadly)))
                {
                    string ownCells = guardWay.Found ? Cells(guardWay.NodesReversed.AsEnumerable().Reverse()) : "none";
                    RPLog.Message($"[RimPrison:test] stability 24: escort way round - the inmate (speed {inmate.GetStatValue(StatDefOf.MoveSpeed):F2}) at {inmate.Position - o}, the guard (speed {guard.GetStatValue(StatDefOf.MoveSpeed):F2}) at "
                                  + $"{guard.Position - o}; the goal {goal - o} (hold {hold}); the guard's own way to it {own} steps [{ownCells}], {toThem} to the inmate, round {round}");
                    if (hold || goal == ramp || !round)
                    {
                        S24Fail(check, $"premise: the scene does not part the ways - the goal {goal - o} (hold {hold}, the ramp {goal == ramp}), the guard's own way to it {own} steps [{ownCells}], to the inmate {toThem} "
                                       + $"(more than {toThem + (int)JobDriver_EscortTransfer.FallBehind + 1 + JobDriver_EscortTransfer.GoesRoundBy} is round)");
                        return;
                    }
                }
                LocalTargetInfo target = JobDriver_EscortTransfer.WalkTarget(guard, inmate, ramp, 0, out bool holdNow);
                if (holdNow || !target.HasThing || target.Thing != inmate)
                {
                    int there = target.HasThing ? -1 : JobDriver_EscortTransfer.StepsOfWalking(map, guard.Position, target.Cell, guard);
                    S24Fail(check, $"the guard is sent to {(target.HasThing ? target.Thing.LabelShort : (target.Cell - o).ToString())} (hold {holdNow}): its own way there is {there} steps, round the loop, while the inmate is {toThem} "
                                   + $"from them and the goal {(int)JobDriver_EscortTransfer.FallBehind + 1} on along their way - it walks away from the slow inmate (the slow transfer's 22 steps apart)");
                }
                inmate.jobs.StopAll();
            });
        }

        // ---- 3. the vocation course's attend job, watched ------------------------------------------------------------------------------------------------------------------

        /// <summary>The measured window of the tailoring course's bench part (real ticks).</summary>
        private const int VocCourseWindow = 900;

        /// <summary>The attend job the tailoring course watches, its session, and how the job ended when the job tracker ended it (see <see cref="Patch_JobTracker_EndCurrentJob_VocationWatch"/>).</summary>
        internal static Job vocWatchedJob;
        internal static ProgramSession vocWatchedSession;
        internal static string vocWatchedEnd;

        /// <summary>The sessions as the attend job's listening sees them: the one it would find for the pawn, the check's own, and every session listed.</summary>
        internal static string VocSessionsSeenBy(Pawn p, PrisonMap pm, ProgramSession own)
        {
            int now = Find.TickManager.TicksGame;
            ProgramSession found = pm == null ? null : ProgramSystem.SessionOf(p, pm);
            string list = pm == null ? "no prison map" : string.Join("; ", pm.sessions.Select(s => $"{s.program?.defName} ({s.attendees.Count} attendees, theirs {s.attendees.Contains(p)}, {s.endTick - now} ticks left, the check's {s == own})"));
            return $"the session the listening finds {found?.program?.defName ?? "none"} (the check's own {found != null && found == own}), the check's active {own?.Active} ({(own == null ? 0 : own.endTick - now)} ticks left), "
                   + $"listed {own != null && pm != null && pm.sessions.Contains(own)}, sessions [{list}]";
        }
    }

    /// <summary>
    /// Test diagnostics: the tailoring course's attend job ended in the first ticks with nothing heard ("listened 0 ticks ... jobs on the way [RP_AttendClass > Wait_MaintainPosture > Wait_Wander]", a job that
    /// ended Succeeded). When the job the course watches (<see cref="AutoTest.vocWatchedJob"/>) is ended through the job tracker, this says with what condition, in which toil, what the listening would find
    /// then, and the calls that ended it.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    [TestOnly]
    public static class Patch_JobTracker_EndCurrentJob_VocationWatch
    {
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition, Pawn ___pawn)
        {
            Job job = __instance.curJob;
            if (!AutoTest.Active || job == null || job != AutoTest.vocWatchedJob || AutoTest.vocWatchedEnd != null) return;
            AutoTest.vocWatchedEnd = $"ended {condition} {Find.TickManager.TicksGame - job.startTick} ticks after it began, in toil {__instance.curDriver?.CurToilIndex} {__instance.curDriver?.CurToilString}; "
                                     + $"{AutoTest.VocSessionsSeenBy(___pawn, PrisonMap.Of(___pawn.Map), AutoTest.vocWatchedSession)}; by "
                                     + string.Join(" < ", new System.Diagnostics.StackTrace(1, false).GetFrames().Take(14).Select(f => f.GetMethod()?.Name));
        }
    }
}
