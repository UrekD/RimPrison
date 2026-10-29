using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>What a guard is walking an inmate to.</summary>
    public enum EscortKind : byte
    {
        /// <summary>The hearing room (the parole scene's own escort job; this only holds the inmate's pass through the doors).</summary>
        Hearing,
        /// <summary>A shower with nobody else in it.</summary>
        Shower,
        /// <summary>A patch of the level's own yard.</summary>
        Yard,
        /// <summary>Back to the cell: they are out of it with nobody (new to the level, a hearing is over, well again after the infirmary, a walk whose guard was lost).</summary>
        Home,
        /// <summary>Out of a wing that burns to the place planned for them, and back to the bed after the all-clear (see <see cref="EvacuationSystem"/>): the doors of the evacuation's own route.</summary>
        Evacuation,
    }

    public enum EscortPhase : byte
    {
        /// <summary>Walking out, and there (the shower, the yard).</summary>
        Out,
        /// <summary>Walking back to the cell.</summary>
        Back,
    }

    /// <summary>One walk: who has the inmate, where to, and how far it has got. Not saved (a guard's job carries all of it, see <see cref="JobDriver_EscortInmate"/>).</summary>
    public sealed class EscortLeg
    {
        public Pawn guard;
        public EscortKind kind;
        public EscortPhase phase;
        /// <summary>Where the inmate walks to (a hearing: the guard's post in the hearing room).</summary>
        public IntVec3 target = IntVec3.Invalid;
        /// <summary>
        /// The doors the pass opens: the ones on the path the guard would walk from where the inmate is to where they are going (to the target on the way out, to the cell on the way back), the
        /// inmate's own cell door among them, never another inmate's. Planned on the main thread, never changed once set (a new set replaces it), read on path threads.
        /// </summary>
        public volatile HashSet<Building_Door> doors;

        /// <summary>Does the pass open this door (read on path threads)?</summary>
        public bool PassesDoor(Building_Door door) => doors is { } set && set.Contains(door);
        /// <summary>The tick the walk began, the tick the inmate got there (-1 not yet), and the tick the guard may take them back (a yard stay's end, -1 none).</summary>
        public int startTick, arrivedTick = -1, untilTick = -1;
        /// <summary>Done with what they came for (washed), or could not get there.</summary>
        public bool done, failed;
    }

    /// <summary>
    /// The walk of an inmate who may leave the cell only with a guard (supermax, and a hearing for anyone the board calls who may not go alone): the guard collects them at the cell, both walk to the
    /// guard's target at the guard's pace (the inmate's own job is a walk to the same place, never a follow job: a follower jogs and arrives first), the guard waits, and both walk back. The guard
    /// has them by the arm (<see cref="CompInmate.leg"/>): the doors on the path of that walk, and only those, open to the inmate (<see cref="EscortLeg.doors"/>), and only while the guard is on the
    /// escort job, standing, on the map and near (<see cref="Holds"/>). It ends the moment any of that stops, and a supermax inmate left outside the cell is fetched (<see cref="EscortKind.Home"/>).
    /// </summary>
    public static class EscortWalk
    {
        /// <summary>How far apart the guard and the inmate may be, in cells (a walk at one pace keeps them within a few; the guard waits at a post in the same room as the inmate's target).</summary>
        public const int NearCells = 25;

        /// <summary>The guard has the inmate by the arm now: they are on the escort job for them, standing, on the same map and near.</summary>
        public static bool Holds(Pawn guard, Pawn inmate) =>
            guard is { Spawned: true, Dead: false, Downed: false } && inmate is { Spawned: true } && guard.Map == inmate.Map
            && guard.Position.DistanceToSquared(inmate.Position) <= NearCells * NearCells
            && guard.CurJob is { } job && job.targetA.Thing == inmate && (job.def == RPSupermaxDefOf.RP_EscortInmate || job.def == RPParoleDefOf.RP_EscortToHearing || job.def == RPEvacDefOf.RP_EvacuateInmate);

        /// <summary>
        /// The guard has the inmate by the arm: the doors on the path to where they are going let them through, and what they were doing is over. Nothing is held (and a walk this guard had is
        /// ended) when the guard does not hold them (<see cref="Holds"/>). <paramref name="target"/> is where the walk goes on the way out; on the way back it is the cell.
        /// </summary>
        public static EscortLeg Hold(Pawn inmate, Pawn guard, EscortKind kind, IntVec3 target, EscortPhase phase = EscortPhase.Out)
        {
            CompInmate c = inmate.Inmate();
            if (c == null) return null;
            if (!Holds(guard, inmate))
            {
                Release(inmate, guard);
                return null;
            }
            EscortLeg leg = c.leg;
            bool fresh = leg == null || leg.guard != guard || leg.kind != kind;
            if (!fresh && leg.phase == phase) return leg;
            if (fresh) leg = new EscortLeg { guard = guard, kind = kind, target = target, startTick = Find.TickManager.TicksGame };
            leg.phase = phase;
            // (An evacuation's walk has its own route and rules: no perimeter door, no other inmate's cell, no door the guard happens to pass on a shorter way, see EvacRoutes.)
            leg.doors = kind == EscortKind.Evacuation ? EvacuationSystem.EscortDoors(inmate, phase == EscortPhase.Out ? target : HomeCell(inmate)) : PlanDoors(inmate, guard, phase == EscortPhase.Out ? target : HomeCell(inmate));
            c.leg = leg;
            Forget(inmate);
            // (Turned for home: whatever they were doing at the far end (a wander of up to fifteen minutes) is over, and they look for the way back at once.)
            if (phase == EscortPhase.Back && inmate.Spawned && !inmate.Downed && inmate.CurJob != null) inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            return leg;
        }

        /// <summary>
        /// The doors on the path the guard would walk from where the inmate stands to <paramref name="to"/>, except the cell doors of other inmates' cells (a path never needs one; the cell they stand in is not another's). Staff-only
        /// doors on the path are in it (the guard is the staff); no other staff-only door is.
        /// </summary>
        public static HashSet<Building_Door> PlanDoors(Pawn inmate, Pawn guard, IntVec3 to)
        {
            var doors = new HashSet<Building_Door>();
            if (!to.IsValid || !inmate.Spawned) return doors;
            Map map = inmate.Map;
            using PawnPath path = map.pathFinder.FindPathNow(inmate.Position, to, TraverseParms.For(guard, Danger.Deadly));
            if (!path.Found) return doors;
            // (Their own cell's door, and the door of the cell they stand in now (a cell they were moved out of): the way out of a room is the walk's.)
            Room own = InmateUtility.CellOf(inmate), here = inmate.GetRoom();
            foreach (IntVec3 cell in path.NodesReversed)
                if (cell.GetDoor(map) is { } door && (door.GetComp<CompPrisonDoor>()?.cellRoom is not { } behind || behind == own || behind == here)) doors.Add(door);
            return doors;
        }

        /// <summary>The walk is over (the guard's job ended, or the level changed): the doors close to them again.</summary>
        public static void End(Pawn inmate, CompInmate c)
        {
            if (c.leg == null) return;
            c.leg = null;
            Forget(inmate);
        }

        public static void Release(Pawn inmate, Pawn guard)
        {
            if (inmate?.Inmate() is { leg: { } leg } c && leg.guard == guard) End(inmate, c);
        }

        /// <summary>A pass changed for this inmate: what they can reach is different (no door's verdict is), so only their cached answers go.</summary>
        private static void Forget(Pawn inmate)
        {
            if (inmate.Spawned) inmate.Map.reachability.ClearCacheFor(inmate);
        }

        /// <summary>A walk whose guard no longer holds the inmate (an alarm took them, they died, they are down, the inmate fell behind) ends: nobody has the inmate by the arm.</summary>
        public static void Validate(Pawn inmate, CompInmate c)
        {
            if (c.leg is { } leg && !Holds(leg.guard, inmate)) End(inmate, c);
        }

        /// <summary>Nobody came: the inmate has not got to where they are going <see cref="SupermaxSystem.NoShowTicks"/> after the walk began (an inmate who did not set out, a way that is shut to them).</summary>
        public static bool NoShow(EscortLeg leg, int now) => leg.arrivedTick < 0 && now - leg.startTick > SupermaxSystem.NoShowTicks;

        /// <summary>The cell a guard sees an inmate back into.</summary>
        public static IntVec3 HomeCell(Pawn inmate)
        {
            Building_Bed bed = inmate.ownership?.OwnedBed;
            if (bed == null || !bed.Spawned) return IntVec3.Invalid;
            Room cell = bed.GetRoom();
            if (cell == null) return IntVec3.Invalid;
            return CellFinder.TryFindRandomCellNear(bed.Position, inmate.Map, 2, c => c.GetRoom(inmate.Map) == cell && c.Standable(inmate.Map), out IntVec3 near) ? near : bed.Position;
        }

        public static bool InOwnCell(Pawn inmate) => inmate.GetRoom() is { } room && room == InmateUtility.CellOf(inmate);

        /// <summary>
        /// A supermax inmate who is out of their cell with nobody: new to the level, or let out of a hearing, a medical bed or solitary, or left by a guard who was lost. They wait where they are (a
        /// walk home alone would take them through the prison) until a guard comes for them (<see cref="EscortKind.Home"/>).
        /// </summary>
        public static bool Stranded(Pawn p, CompInmate c) =>
            c.security is { escortOnly: true } && c.leg == null && c.forcedActivity == null && p.Spawned && p.ownership?.OwnedBed is { Spawned: true } && !InOwnCell(p)
            && !p.Downed && !p.InBed() && !HealthAIUtility.ShouldSeekMedicalRest(p) && !c.awaitingSearch && c.processed && ParoleScene.CaseOf(p) is not { stage: HearingStage.Called or HearingStage.Seated };

        /// <summary>The inmate's own job during a walk (null: none, the schedule goes on): a walk to where the guard is going, then the shower or a wander in the yard; a walk home at the end.</summary>
        public static Job JobFor(Pawn pawn, CompInmate c, PrisonMap pm)
        {
            EscortLeg leg = c.leg;
            if (leg == null || leg.kind == EscortKind.Hearing) return null;
            if (leg.phase == EscortPhase.Back) return RegimeActivityWorker.GoToOwnCell(pawn) ?? RegimeActivityWorker.WaitJob(pawn, 120);
            if (leg.arrivedTick < 0)
            {
                if (pawn.Position != leg.target)
                {
                    // (A walk that cannot be made, a door that is shut to them or a room gone: the guard gives up and takes them back.)
                    if (!pawn.CanReach(leg.target, PathEndMode.OnCell, Danger.Deadly))
                    {
                        leg.failed = true;
                        return RegimeActivityWorker.WaitJob(pawn, 120);
                    }
                    Job walk = JobMaker.MakeJob(JobDefOf.Goto, leg.target);
                    walk.locomotionUrgency = LocomotionUrgency.Walk;
                    walk.expiryInterval = 900;
                    return walk;
                }
                leg.arrivedTick = Find.TickManager.TicksGame;
                if (leg.kind == EscortKind.Yard) leg.untilTick = leg.arrivedTick + SupermaxSystem.YardStayTicks;
            }
            if (leg.kind == EscortKind.Shower)
            {
                Need hygiene = pawn.needs.TryGetNeed(RPDefOf.RP_Hygiene);
                CompFixture shower = pm.Fixtures.Find(f => f.UseCell == leg.target && f.Props.need == RPDefOf.RP_Hygiene);
                if (hygiene == null || hygiene.CurLevel >= SupermaxSystem.CleanEnough || shower is not { Usable: true } || !pawn.CanReserve(shower.parent))
                {
                    leg.done = true;
                    return RegimeActivityWorker.WaitJob(pawn, 200);
                }
                return FixtureFinder.UseJob(shower);
            }
            // The yard: the air, alone (a wander about the patch they were walked to, or a stand). Out of it (a toilet trip would have taken them, a push), they walk back to it.
            if (WanderInPatch(pawn, pm, leg.target) is { } air) return air;
            if (pawn.CanReach(leg.target, PathEndMode.OnCell, Danger.Deadly))
            {
                Job back = JobMaker.MakeJob(JobDefOf.Goto, leg.target);
                back.locomotionUrgency = LocomotionUrgency.Walk;
                return back;
            }
            return RegimeActivityWorker.WaitJob(pawn, 300);
        }

        /// <summary>A wander about the zone patch that holds <paramref name="anchor"/> (never the zone at the pawn's feet: wherever they have got to), a stand between walks; null when they are not in it.</summary>
        public static Job WanderInPatch(Pawn pawn, PrisonMap pm, IntVec3 anchor)
        {
            int patch = pm.zones.PatchAt(anchor);
            if (patch < 0 || pm.zones.PatchAt(pawn.Position) != patch) return null;
            if (pawn.mindState.nextMoveOrderIsWait)
            {
                pawn.mindState.nextMoveOrderIsWait = false;
                return RegimeActivityWorker.WaitJob(pawn, Rand.Range(250, 900));
            }
            pawn.mindState.nextMoveOrderIsWait = true;
            IntVec3 dest = RCellFinder.RandomWanderDestFor(pawn, pawn.Position, 8f, (p, c, root) => pm.zones.PatchAt(c) == patch, Danger.Deadly);
            return dest.IsValid && dest != pawn.Position ? JobMaker.MakeJob(JobDefOf.GotoWander, dest) : RegimeActivityWorker.WaitJob(pawn, Rand.Range(250, 900));
        }
    }

    /// <summary>
    /// The guard collects the inmate at the cell, walks them to the shower or yard, waits there while they are in it, and walks them back and sees them in; or (<see cref="EscortKind.Home"/>) collects one who
    /// is out of the cell with nobody and sees them in. After a load every toil makes the walk again (<see cref="EnsureLeg"/>): the walk is not saved, the guard's job is.
    /// </summary>
    public class JobDriver_EscortInmate : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        /// <summary>The kind of walk: the job's count.</summary>
        private EscortKind Kind => (EscortKind)job.count;

        /// <summary>The longest a shower walk and its wait may take, and the wait for the inmate to be seen in.</summary>
        public const int ShowerMaxTicks = 3000, SeeInMaxTicks = 900;

        /// <summary>Loaded from a save: the walk is not in the inmate's record yet, and the toil that is running makes it (see <see cref="EnsureLeg"/>).</summary>
        private bool loaded;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) loaded = true;
        }

        private EscortLeg Leg => Inmate?.Inmate()?.leg;

        private bool StillOn => Inmate?.Inmate() is { activeFlag: true } c && c.security is { escortOnly: true } && !Inmate.Downed && !Inmate.InMentalState;

        /// <summary>A lockdown: whatever they came for is over, they are walked back.</summary>
        private bool Lockdown => Inmate?.Inmate() is { } c && PrisonGame.Instance is { } game && game.LockedDown(c);

        /// <summary>
        /// The inmate's walk exists, or is made again: the first time it is asked for after a load (every toil asks, in its own end condition, before it does anything), the walk of the phase the toil is in is
        /// made from the job. False when there is no walk and it could not be made (the guard is not near the inmate, say).
        /// </summary>
        private bool EnsureLeg(EscortPhase phase)
        {
            if (Leg != null) return true;
            if (!loaded) return false;
            loaded = false;
            return Hold(phase) != null;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !StillOn);
            AddFinishAction(condition =>
            {
                if (AutoTest.Active)
                {
                    // (Cut short from outside: by whom, the mod methods on the stack.)
                    // (Every frame but the system's, a lambda named by its outer type, the first ten: a cut from vanilla's job tracker or from a lambda of ours showed as nothing but the test's loop.)
                    string by = condition is JobCondition.InterruptForced or JobCondition.InterruptOptional
                        ? " by [" + string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Select(f => f.GetMethod()).Where(m => m != null)
                            .Select(m => (m.DeclaringType is { } t && t.Name.StartsWith("<") ? t.DeclaringType : m.DeclaringType, m.Name))
                            .Where(x => x.Item1 == null || !(x.Item1.Namespace ?? "").StartsWith("System"))
                            .Select(x => (x.Item1?.Name ?? "?") + "." + x.Name).Skip(1).Take(10)) + "]"
                        : "";
                    RPLog.Message($"[RimPrison:escort] {pawn.LabelShort}'s walk with {Inmate?.LabelShort} ended: {condition} at toil {CurToilIndex} ({Kind}, leg {Leg?.phase}, still on {StillOn}){by}, the guard's timetable {pawn.timetable?.CurrentAssignment?.defName}");
                    // (A walk back that ends with the inmate still outside their cell: what they were doing, and whether their own way in was open.)
                    if (Inmate is { Spawned: true } im && Leg is { phase: EscortPhase.Back } bl && !EscortWalk.InOwnCell(im))
                    {
                        Building_Bed bed = im.ownership?.OwnedBed;
                        RPLog.Message($"[RimPrison:escort] ... and {im.LabelShort} is not in: at {im.Position} on {im.CurJobDef?.defName} (driver {im.jobs.curDriver?.GetType().Name}), guard at {pawn.Position}, bed {bed?.Position} in a room of {bed?.GetRoom()?.CellCount} cells, "
                                      + $"reach the bed {bed != null && im.CanReach(bed, PathEndMode.Touch, Danger.Deadly)}, go-to-cell job {RegimeActivityWorker.GoToOwnCell(im)?.def.defName ?? "none"}, pass doors [{string.Join(",", bl.doors?.Select(d => d.Position.ToString()) ?? new string[0])}], "
                                      + $"punishment due {im.Inmate()?.pendingPunishmentHours} h, forced {im.Inmate()?.forcedActivity?.defName ?? "none"}, mental {im.MentalStateDef?.defName ?? "none"}, moving {im.pather.Moving}");
                    }
                    AutoTest.Notify_EscortWalk(Kind, condition);
                }
                EscortWalk.Release(Inmate, pawn);
            });
            bool home = Kind == EscortKind.Home;
            Toil meet = Pace(Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch), LocomotionUrgency.Jog);
            meet.FailOn(() => Inmate.Inmate().leg != null && Leg.guard != pawn);
            yield return meet;
            yield return Toils_General.Do(() =>
            {
                CompInmate c = Inmate.Inmate();
                EscortWalk.Hold(Inmate, pawn, Kind, job.targetC.Cell, home ? EscortPhase.Back : EscortPhase.Out);
                // (With the pass on, can they get there? Not: no walk for a while, and nobody is left outside.)
                IntVec3 goal = home ? EscortWalk.HomeCell(Inmate) : job.targetC.Cell;
                if (Leg == null || !goal.IsValid || !Inmate.CanReach(goal, PathEndMode.OnCell, Danger.Deadly))
                {
                    c.escortRetryTick = Find.TickManager.TicksGame + GenDate.TicksPerHour / 2;
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // The yard slot is theirs now (the next one waits its turn); whatever they were doing is over.
                if (Kind == EscortKind.Yard) c.yardSlot = SupermaxSystem.SlotOf(pawn.Map);
                if (Inmate.CurJob is { } current && current.def != JobDefOf.Ingest) Inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            });
            if (!home)
            {
                Toil lead = Pace(Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell), LocomotionUrgency.Walk);
                lead.tickIntervalAction = delta =>
                {
                    Hold(EscortPhase.Out);
                    if (Lockdown && Leg is { } leg)
                    {
                        leg.done = true;
                        ReadyForNextToil();
                    }
                };
                lead.FailOn(() => !EnsureLeg(EscortPhase.Out) || Leg.failed);
                yield return lead;
                // (An end condition ends the whole job: the stay is left by a ready-for-next-toil, once the inmate is done, the yard hour is up, or the walk could not be made.)
                Toil stay = Toils_General.Wait(GenDate.TicksPerHour * 2);
                stay.tickIntervalAction = delta =>
                {
                    Hold(EscortPhase.Out);
                    EscortLeg leg = Leg;
                    if (leg != null && Lockdown) leg.done = true;
                    // (Another inmate in the shower room: the room is not theirs alone, and they are taken back.)
                    if (leg is { kind: EscortKind.Shower, arrivedTick: >= 0 } && SupermaxSystem.OtherInmateIn(leg.target.GetRoom(pawn.Map), Inmate, PrisonMap.Of(pawn.Map))) leg.done = true;
                    // (Nobody came: an inmate who did not set out, a way that is shut to them. Given up, and they are not offered another walk for half an hour.)
                    if (leg != null && EscortWalk.NoShow(leg, Find.TickManager.TicksGame))
                    {
                        leg.failed = true;
                        Inmate.Inmate().escortRetryTick = Find.TickManager.TicksGame + GenDate.TicksPerHour / 2;
                    }
                    if (leg == null || leg.done || leg.failed || leg.untilTick >= 0 && Find.TickManager.TicksGame >= leg.untilTick
                        || Kind == EscortKind.Shower && Find.TickManager.TicksGame - leg.startTick > ShowerMaxTicks) ReadyForNextToil();
                };
                stay.FailOn(() => !EnsureLeg(EscortPhase.Out));
                yield return stay;
                // Back: pick them up where they are, and see them in.
                Toil fetch = Pace(Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch), LocomotionUrgency.Jog);
                fetch.FailOn(() => !EnsureLeg(EscortPhase.Out));
                yield return fetch;
                yield return Toils_General.Do(() => Hold(EscortPhase.Back));
            }
            Toil walkHome = Pace(ToilMaker.MakeToil("EscortHome"), LocomotionUrgency.Walk);
            walkHome.initAction = () =>
            {
                IntVec3 cell = EscortWalk.HomeCell(Inmate);
                if (!cell.IsValid || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) EndJobWith(JobCondition.Incompletable);
                else pawn.pather.StartPath(cell, PathEndMode.OnCell);
            };
            walkHome.tickIntervalAction = delta => Hold(EscortPhase.Back);
            walkHome.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            walkHome.FailOn(() => !EnsureLeg(EscortPhase.Back));
            yield return walkHome;
            Toil seeIn = Toils_General.Wait(SeeInMaxTicks);
            seeIn.tickIntervalAction = delta => Hold(EscortPhase.Back);
            seeIn.FailOn(() => !EnsureLeg(EscortPhase.Back));
            seeIn.AddEndCondition(() => EscortWalk.InOwnCell(Inmate) ? JobCondition.Succeeded : JobCondition.Ongoing);
            yield return seeIn;
        }

        private EscortLeg Hold(EscortPhase phase) => EscortWalk.Hold(Inmate, pawn, Kind, job.targetC.Cell, phase);

        /// <summary>
        /// The pace of the toil's walking, set as it begins (the pather reads the job's urgency at every step): the guard goes to the inmate at a jog, as a guard answers a call, and walks with them at the
        /// pace the inmate keeps. (All of it at a walk, a guard from the far side of the prison took two or three hours to get to one left outside the cell, and the inmate stood there, or went hungry.)
        /// </summary>
        private Toil Pace(Toil toil, LocomotionUrgency urgency)
        {
            toil.AddPreInitAction(() => job.locomotionUrgency = urgency);
            return toil;
        }
    }

    /// <summary>
    /// Guards with nothing more urgent walk the supermax inmates who are due for the shower or the yard (as many walks as there are guards free, and one inmate in a yard patch at a time), and fetch
    /// the ones who are out of their cells with nobody (<see cref="EscortKind.Home"/>).
    /// </summary>
    public class WorkGiver_EscortInmate : WorkGiver_Scanner
    {
        /// <summary>The plan <see cref="HasJobOnThing"/> made, for <see cref="JobOnThing"/> to use in the same tick (a yard cell is picked at random: two plans would pick two cells, and every scan would run twice).</summary>
        private Pawn plannedGuard, plannedInmate;
        private int plannedTick = -1;
        private EscortKind plannedKind;
        private IntVec3 plannedTarget, plannedPost;

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || PrisonGame.Instance == null || InmateCandidates.Shared.Escort(pawn.Map).Count == 0;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Escort(pawn.Map);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            plannedTick = -1;
            if (t is not Pawn inmate || !SupermaxSystem.Candidate(inmate) || !pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)
                || !SupermaxSystem.TryPlan(pawn, inmate, out plannedKind, out plannedTarget, out plannedPost)) return false;
            (plannedGuard, plannedInmate, plannedTick) = (pawn, inmate, Find.TickManager.TicksGame);
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Pawn inmate) return null;
            if (plannedTick != Find.TickManager.TicksGame || plannedGuard != pawn || plannedInmate != inmate)
            {
                if (!SupermaxSystem.TryPlan(pawn, inmate, out plannedKind, out plannedTarget, out plannedPost)) return null;
            }
            plannedTick = -1;
            Job job = JobMaker.MakeJob(RPSupermaxDefOf.RP_EscortInmate, inmate, plannedPost, plannedTarget);
            job.count = (int)plannedKind;
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }
    }

    /// <summary>The yard slot of a level that takes its yard alone, for one that walks there by itself (a level that is not also <see cref="SecurityLevelDef.escortOnly"/>): a patch of its own yard that no one else is in, or nothing (the slot is skipped).</summary>
    public class RegimeWorker_YardAlone : RegimeActivityWorker
    {
        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            if (comp.security is not { yardAlone: true } level || pm == null) return null;
            int here = pm.zones.PatchAt(pawn.Position);
            if (here >= 0 && SupermaxSystem.YardZones(level).Contains(pm.zones.ZoneAt(pawn.Position))) return WanderInZones(pawn, pm, new List<PrisonZoneDef> { pm.zones.ZoneAt(pawn.Position) });
            IntVec3 cell = SupermaxSystem.FreeYardCell(pawn, level, pm, pawn);
            if (!cell.IsValid) return null;
            Job walk = JobMaker.MakeJob(JobDefOf.Goto, cell);
            walk.locomotionUrgency = LocomotionUrgency.Walk;
            return walk;
        }
    }
}
