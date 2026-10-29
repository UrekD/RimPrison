using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    [DefOf]
    public static class RPCommuteDefOf
    {
        public static JobDef RP_GoHome;

        static RPCommuteDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCommuteDefOf));
    }

    /// <summary>
    /// Staff go home: when their shift ends (their timetable says sleep) they walk out of the gate and leave the map,
    /// and they come back through it rested, fed and in better spirits when the next shift starts. Suspended staff stay
    /// home. Handlers take their dog. At least one member of staff always stays on site.
    /// </summary>
    public static class StaffCommute
    {
        /// <summary>Bumped when somebody gets or loses a role (see <see cref="StaffUtility.ApplyRole"/>): the staff list below is read again.</summary>
        public static int StaffVersion;

        private static List<Pawn> staffCache;
        private static int staffTick = -1, staffVersion = -1, staffColonists = -1, staffAtHome = -1;
        private static Map staffMap;
        private static PrisonGame staffGame;

        /// <summary>
        /// Everyone employed here, on site or at home. The list is built once a tick (the Intel tab, the alerts and the HUD ask for it often, with 36 callers); it is read again when the
        /// colonists on the map, the staff at home or anyone's role changed since. A list given out is never changed afterwards, so a caller may go on reading it while the staff change under it.
        /// </summary>
        public static List<Pawn> AllStaff(Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            int tick = Find.TickManager.TicksGame, colonists = map.mapPawns.FreeColonistsCount, atHome = game?.home?.Count ?? 0;
            if (staffCache != null && staffTick == tick && staffMap == map && staffGame == game && staffVersion == StaffVersion && staffColonists == colonists && staffAtHome == atHome) return staffCache;
            var list = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.FreeColonists)
                if (p.GetComp<CompStaff>()?.role != null) list.Add(p);
            if (game != null)
            {
                var onMap = new HashSet<Pawn>(list);
                foreach (Pawn p in game.OffSite)
                    if (p is { Dead: false } && p.GetComp<CompStaff>()?.role != null && !onMap.Contains(p)) list.Add(p);
            }
            (staffCache, staffTick, staffMap, staffGame, staffVersion, staffColonists, staffAtHome) = (list, tick, map, game, StaffVersion, colonists, atHome);
            return list;
        }

        /// <summary>For tests: people who came back from home as anything but our staff (it happened once).</summary>
        public static int ReturnedNotStaff;

        /// <summary>Someone employed here is at home, off shift or suspended: the prison still has staff, who come back.</summary>
        public static bool AnyAtHome()
        {
            if (PrisonGame.Instance == null) return false;
            foreach (Pawn p in PrisonGame.Instance.OffSite)
                if (p is { Dead: false, Destroyed: false } && p.GetComp<CompStaff>()?.role != null) return true;
            return false;
        }

        public static bool AtHome(Pawn p) => PrisonGame.Instance?.home.Contains(p) == true;

        /// <summary>Off shift (or suspended): should be at home.</summary>
        public static bool ShouldBeHome(Pawn p, CompStaff sc, int hour)
        {
            if (sc.role == null || sc.leaveAtTick >= 0) return false;
            // The warden on the floor never goes home (not off shift, not suspended, not on leave).
            if (sc.role.warden) return false;
            // A hostage is held on site whatever their shift says.
            if (HostageCrisis.IsHeld(p)) return false;
            if (CorruptionSystem.Suspended(sc) || StaffLives.OnLeave(sc)) return true;
            // The handover keeps the day guards on past the end of the shift (see ShiftSystem.InHandover; their timetable says work in those hours as well).
            if (ShiftSystem.InHandover(sc, hour)) return false;
            return p.timetable?.GetAssignment(hour) == TimeAssignmentDefOf.Sleep;
        }

        /// <summary>
        /// A member of staff who is off shift now (<see cref="ShouldBeHome"/>) and still on site, not on the way out: the one the commute keeps so that the prison is never left empty, or one it could not
        /// send. Not the warden (never off shift), not temporary callout staff, not a hostage.
        /// </summary>
        public static bool OffShiftOnSite(Pawn p, CompStaff sc, int hour) =>
            p.Spawned && sc.role != null && sc.leaveAtTick < 0 && p.CurJobDef != RPCommuteDefOf.RP_GoHome && ShouldBeHome(p, sc, hour);

        /// <summary>
        /// Guards on site and on shift at this hour: what the commute counts as on site (spawned, not on the way home, not a held hostage), of the guards on the roster, and only those the timetable has at work (the one the
        /// commute keeps on site off shift is not cover, nor a suspended or striking guard, nor one who is down or in a mental break). Staff agreements measure cover by this (<see cref="StaffAgreements"/>).
        /// </summary>
        public static int GuardsOnDuty(Map map, int hour)
        {
            int count = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (p.GetComp<CompStaff>() is { } sc && Roster.OnRoster(sc) && !HostageCrisis.IsHeld(p) && p.CurJobDef != RPCommuteDefOf.RP_GoHome && !p.Downed && !p.InMentalState && !IncidentWorker_StaffStrike.Striking(p) && !ShouldBeHome(p, sc, hour)) count++;
            return count;
        }

        /// <summary>Hourly: send the off-shift home, bring the next shift in.</summary>
        public static void Hourly(Map map, PrisonGame game, int hour)
        {
            // Back for their shift.
            List<Pawn> atHome = game.OffSite.ToList();
            for (int i = atHome.Count - 1; i >= 0; i--)
            {
                Pawn p = atHome[i];
                if (p == null || p.Dead || p.Destroyed || p.GetComp<CompStaff>() is not { role: not null } sc)
                {
                    if (AutoTest.Active) RPLog.Message($"[commute] {p?.LabelShort} dropped from home list: dead={p?.Dead} destroyed={p?.Destroyed} role={p?.GetComp<CompStaff>()?.role?.defName}");
                    // Fired or gone while at home: they simply do not come back.
                    game.home.Remove(p);
                    continue;
                }
                if (!ShouldBeHome(p, sc, hour)) Arrive(p, sc, map, game);
            }
            SendOffShiftHome(map, game, hour);
        }

        /// <summary>The off-shift on site set off for home, the one who stays kept (the hour's second half of <see cref="Hourly"/>).</summary>
        public static void SendOffShiftHome(Map map, PrisonGame game, int hour)
        {
            int onSite = 0;
            var leaving = new List<Pawn>();
            // (Over a copy: a walk home cut short below starts the pawn's next job, which can change who is on the map.)
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (p.GetComp<CompStaff>() is not { role: not null } sc || sc.leaveAtTick >= 0) continue;
                // A held hostage is on site but not the one kept on duty: they cannot stop a riot or talk anyone down.
                if (HostageCrisis.IsHeld(p)) continue;
                // The warden is always on site, and is not the member of staff the prison is kept staffed with (a guard still is).
                if (sc.role.warden) continue;
                // (One already on the way home is not on site: counted, they would leave the last one walking home too, one sent by vanilla's rest when their search ended. One whose shift has
                // begun on the way, kept past the end of the last by a patient, turns back and is on site.)
                if (p.CurJobDef == RPCommuteDefOf.RP_GoHome)
                {
                    if (ShouldBeHome(p, sc, hour)) continue;
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                onSite++;
                if (ShouldBeHome(p, sc, hour)) leaving.Add(p);
            }
            if (AutoTest.Active && (hour == 22 || hour == 10))
                RPLog.Message($"[commute] hour {hour}: onSite={onSite} leaving={leaving.Count} home={game.home.Count} " + string.Join(",", map.mapPawns.FreeColonistsSpawned.Select(x => $"{x.LabelShort}:{x.timetable?.GetAssignment(hour)?.defName}")));
            foreach (Pawn p in SendHomeOrder(leaving))
            {
                // Never leave the prison without anyone in it.
                if (onSite <= 1) break;
                if (TrySendHome(p)) onSite--;
                else if (AutoTest.Active)
                {
                    Room room = p.GetRoom();
                    string doors = room == null ? "-" : string.Join(",", room.ContainedAndAdjacentThings.OfType<Building_Door>().Select(d => $"{d.Position}:{d.GetComp<CompPrisonDoor>()?.mode}:canOpen={d.PawnCanOpen(p)}"));
                    RPLog.Message($"[commute] {p.LabelShort} could not leave: job={p.CurJobDef?.defName} awake={p.Awake()} room cells={room?.CellCount} cell={room?.IsPrisonCell} doors={doors} reachIntake={p.CanReach(IntakeSystem.IntakeSpot(map), PathEndMode.OnCell, Danger.Deadly)}");
                }
            }
        }

        /// <summary>
        /// The order the off-shift go home in: the last one is kept on site, so that one should be an available member of staff
        /// (a suspended or burnt-out one goes first: keeping them leaves nobody effectively on duty; so does one who needs medical rest, who with no bed on site lies down on the floor where they stand, as the
        /// counsellor kept as the last on site did, "staff asleep on the floor 5 times").
        /// </summary>
        public static List<Pawn> SendHomeOrder(List<Pawn> offShift) =>
            offShift.OrderBy(p => p.GetComp<CompStaff>() is { } sc && StaffCareers.Available(sc) ? (HealthAIUtility.ShouldSeekMedicalRest(p) ? 1 : 2) : 0).ToList();

        private static bool TrySendHome(Pawn p)
        {
            if (p.Downed || p.InMentalState || p.Drafted || p.IsBurning() || !p.Awake() && p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)) return false;
            // Finish what matters first: restraining, carrying, executing, searching.
            if (p.carryTracker?.CarriedThing is Pawn) return false;
            JobDef cur = p.CurJobDef;
            if (cur != null && !cur.casualInterruptible && cur != JobDefOf.LayDown && cur != JobDefOf.Wait && cur != JobDefOf.Wait_Wander && cur != JobDefOf.GotoWander) return false;
            if (GoHomeJob(p) is not { } job) return false;
            if (p.carryTracker?.CarriedThing != null) p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out _);
            p.jobs.StartJob(job, JobCondition.InterruptForced);
            return true;
        }

        /// <summary>The walk home: to a reachable edge cell, by the road if there is one (vanilla's best-exit search gives up after 30 random tries and left people sleeping on the floor); null with none.</summary>
        public static Job GoHomeJob(Pawn p)
        {
            if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 exit, p.Map, CellFinder.EdgeRoadChance_Always, allowFogged: false,
                    c => p.CanReach(c, PathEndMode.OnCell, Danger.Deadly))) return null;
            Job job = JobMaker.MakeJob(RPCommuteDefOf.RP_GoHome, exit);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }

        /// <summary>
        /// Instead of a sleep on the floor (staff have no beds: they sleep at home), the walk home for one whose shift is over: kept past the hour's commute by something that could not
        /// be cut short (a visitor's search, a class), they lay down where they stood when it ended, until the next hour's pass sent them off. Not the last one on site.
        /// One who needs medical rest walks home too: this is asked only when vanilla's rest found no bed for them (no medical bed of the staff's - only the penitentiary has them), and a humanlike on the
        /// floor is never tended (<see cref="WorkGiver_Tend.GoodLayingStatusForTend"/> wants them in a bed), so the floor bought nothing; the hourly pass sends the ill first anyway (<see cref="SendHomeOrder"/>)
        /// and walked them off the floor at the next hour (a counsellor off an escort at 22:00 with medical rest, "staff asleep on the floor" in a census run).
        /// </summary>
        public static Job InsteadOfTheFloor(Pawn p)
        {
            // (On the prison's own map and nobody's but their own: not on a caravan, at a site or forming one, where vanilla's rest is asked too and the walk would take them out of it.)
            if (p.Map is not { IsPlayerHome: true } || p.GetLord() != null || PrisonGame.Instance == null || p.GetComp<CompStaff>() is not { role: not null } sc) return null;
            if (p.Downed || p.InMentalState || p.Drafted || p.IsBurning() || p.carryTracker?.CarriedThing != null) return null;
            if (!ShouldBeHome(p, sc, GenLocalDate.HourOfDay(p.Map))) return null;
            int others = 0;
            foreach (Pawn o in p.Map.mapPawns.FreeColonistsSpawned)
                if (o != p && o.GetComp<CompStaff>() is { role: not null } osc && osc.leaveAtTick < 0 && !osc.role.warden && !HostageCrisis.IsHeld(o) && o.CurJobDef != RPCommuteDefOf.RP_GoHome) others++;
            return others >= 1 ? GoHomeJob(p) : null;
        }

        /// <summary>Reached the edge of the map: off home (out of the map, held by the prison until they return).</summary>
        public static void Leave(Pawn p)
        {
            PrisonGame game = PrisonGame.Instance;
            CompStaff sc = p.GetComp<CompStaff>();
            if (game == null || sc == null) return;
            if (sc.dog is { Spawned: true } dog)
            {
                dog.DeSpawn();
                game.home.TryAdd(dog);
            }
            p.DeSpawn();
            game.home.TryAdd(p);
            StaffCareers.InvalidateLeads();
            game.stats.staffWentHome++;
            if (AutoTest.Active) RPLog.Message($"[commute] {p.LabelShort} went home ({sc.role.defName}, {(sc.nightShift ? "night" : "day")} shift)");
        }

        /// <summary>Back through the gate for their shift, with their dog; a night at home put them right.</summary>
        private static void Arrive(Pawn p, CompStaff sc, Map map, PrisonGame game)
        {
            if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 entry, map, CellFinder.EdgeRoadChance_Always)) return;
            game.home.Remove(p);
            GenSpawn.Spawn(p, entry, map);
            StaffCareers.InvalidateLeads();
            if (sc.dog is { Dead: false } dog && game.home.Contains(dog))
            {
                game.home.Remove(dog);
                GenSpawn.Spawn(dog, CellFinder.StandableCellNear(entry, map, 2f), map);
            }
            if (!p.IsFreeColonist) ReturnedNotStaff++;
            if (AutoTest.Active) RPLog.Message($"[commute] {p.LabelShort} came in for the shift (free colonist: {p.IsFreeColonist})");
            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
            if (sc.leaveUntilTick >= 0 && !StaffLives.OnLeave(sc))
            {
                sc.leaveUntilTick = -1;
                Roster.Invalidate();
                // A course and a rest can share one leave (burnout leave granted after a course was booked): both count.
                bool rest = sc.restPending || !sc.trainingPending;
                if (sc.trainingPending) StaffTraining.Complete(p, sc);
                if (rest) p.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_Rested);
                sc.restPending = false;
            }
            if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel * 0.9f;
            if (p.needs?.joy != null) p.needs.joy.CurLevel = UnityEngine.Mathf.Max(p.needs.joy.CurLevel, 0.8f);
        }
    }

    /// <summary>Walk to the edge of the map and go home.</summary>
    public class JobDriver_GoHome : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil leave = ToilMaker.MakeToil("GoHome");
            leave.initAction = () => StaffCommute.Leave(pawn);
            yield return leave;
        }
    }
}
