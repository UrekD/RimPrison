using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPEvacDefOf
    {
        public static JobDef RP_EvacuateInmate;
        public static JobDef RP_CarryEvacuee;
        public static WorkGiverDef RP_Evacuate_WorkGiver;
        public static ThoughtDef RP_EvacuationDrill;

        static RPEvacDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPEvacDefOf));
    }

    public enum EvacPhase : byte
    {
        /// <summary>No evacuation.</summary>
        None,
        /// <summary>The wing is being emptied.</summary>
        Out,
        /// <summary>The all-clear is given: everyone goes back to their own bed.</summary>
        Back,
    }

    /// <summary>Where one inmate stands in the roll call.</summary>
    public enum EvacStatus : byte
    {
        /// <summary>Listed, not yet given a route (planning is staggered, a few inmates a tick).</summary>
        Planning,
        /// <summary>Minimum and medium: walking to the point (or back to the bed) by themselves.</summary>
        Walking,
        /// <summary>Needs a guard (maximum, supermax, the hole, death row) or to be carried (downed): waiting for one.</summary>
        WaitingEscort,
        /// <summary>A guard has them: walking with them, or carrying them.</summary>
        Escorted,
        /// <summary>No route to a safe place for them now (tried again every so often).</summary>
        Blocked,
        /// <summary>At the assembly point (or the fallback).</summary>
        Arrived,
        /// <summary>
        /// Not on the map (<see cref="EvacEntry.missingOffMap"/>: listed again when they are back), or unreachable for two hours: still the evacuation's (kept from the regime, the supermax walks and vanilla's carry to a bed, waiting
        /// where they are), tried again quietly every so often and out after all once a way is found, and holding nothing up (the all-clear sends them home with the rest).
        /// </summary>
        Missing,
        /// <summary>Was outside the wing when it began (work, class, yard): stays where they are, accounted for.</summary>
        Outside,
        /// <summary>Back in their own bed's room (after the all-clear).</summary>
        Home,
        /// <summary>After the all-clear, no bed to go back to: stays where they are, listed.</summary>
        Stayed,
        /// <summary>Left the roll call: released, or taken by the riot response.</summary>
        Dropped,
        /// <summary>
        /// Down (or resting for their wounds) and lying in a bed outside the wing that may hold them - a medical bed, the rescue's - with no fire in its room (<see cref="EvacuationSystem.InRescueBed"/>): accounted for, the doctors' until
        /// well, after the all-clear too. Only while they lie there: up again, or in a bed of the wing, they are given a place (or sent home) again.
        /// </summary>
        Rescued,
    }

    /// <summary>A yard, kept room or holding patch the warden marked as a place to gather, and the levels it takes. Named by its zone and the patch's first cell, as kept patches are.</summary>
    public class AssemblyPoint : IExposable
    {
        public PrisonZoneDef zone;
        public int anchor;
        public List<SecurityLevelDef> levels = new List<SecurityLevelDef>();

        public void ExposeData()
        {
            Scribe_Defs.Look(ref zone, "zone");
            Scribe_Values.Look(ref anchor, "anchor");
            Scribe_Collections.Look(ref levels, "levels", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                levels ??= new List<SecurityLevelDef>();
                levels.RemoveAll(l => l == null);
            }
        }

        public bool Takes(SecurityLevelDef level) => level != null && levels.Contains(level);

        /// <summary>A level whose inmates leave the cell only with a guard and are never with anyone else (supermax) is taken only by a yard of its own (the cage), whatever the warden ticked.</summary>
        public bool TakesAlone(SecurityLevelDef level) => Takes(level) && (level is not { escortOnly: true } || zone?.yardFor == level);
    }

    /// <summary>One inmate's line in the roll call. Saved: the intent (where, how, with whom). Not saved: the pawn, the route and the door pass (rebuilt after a load).</summary>
    public sealed class EvacEntry : IExposable
    {
        public int id;
        public string name;
        public EvacStatus status;
        /// <summary>Index into the map's assembly points; -1 for none (not planned yet, or the fallback).</summary>
        public int point = -1;
        /// <summary>The place goes to the second point when there is one (keep-apart pairs, protective custody, the threatened).</summary>
        public bool second;
        /// <summary>Went to the nearest safe room because no assembly point could be reached (listed as "fallback").</summary>
        public bool fallback;
        /// <summary>Needs a guard (or a carrier) rather than walking by themselves.</summary>
        public bool escort;
        public IntVec3 dest = IntVec3.Invalid;
        /// <summary>The thing id of the guard who has them (0: none).</summary>
        public int escortId;
        public int arrivedTick = -1, sinceTick;
        /// <summary>The drill's mood memory was given (once), and a route was ever blocked for them (the drill's letter names it).</summary>
        public bool memoryGiven, blockedSeen;
        /// <summary>Missing because they were not on the map (they are listed again once they are back); false: unreachable for two hours, and missing they stay until the all-clear.</summary>
        public bool missingOffMap;

        // Not saved.
        public Pawn pawn;
        public EvacRoutes.Route route;
        /// <summary>How far along the route they have been seen (its step): only the cells after it can still make the route bad.</summary>
        public int routeStep;
        public int retryTick, planTick;
        public bool queued;
        /// <summary>For the checks' log: the bed (its thing id, or minus one less the cell's index for a lie-down off a bed) they were last logged lying in, 0 when they were seen up since.</summary>
        public int lyingLogged;
        /// <summary>Read from a save whose point was dropped on load (its zone gone, or no longer a place to mark): planned again on the first tick, whatever their status (one at the point or on the way to it stays there otherwise).</summary>
        public bool pointDropped;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref status, "status");
            Scribe_Values.Look(ref point, "point", -1);
            Scribe_Values.Look(ref second, "second");
            Scribe_Values.Look(ref fallback, "fallback");
            Scribe_Values.Look(ref escort, "escort");
            Scribe_Values.Look(ref dest, "dest", IntVec3.Invalid);
            Scribe_Values.Look(ref escortId, "escortId");
            Scribe_Values.Look(ref arrivedTick, "arrivedTick", -1);
            Scribe_Values.Look(ref sinceTick, "sinceTick");
            Scribe_Values.Look(ref memoryGiven, "memoryGiven");
            Scribe_Values.Look(ref blockedSeen, "blockedSeen");
            Scribe_Values.Look(ref missingOffMap, "missingOffMap");
        }

        /// <summary>Still being moved (or kept at the point, or kept where they are for want of a way), as opposed to counted and left alone.</summary>
        public bool Moving => status is EvacStatus.Planning or EvacStatus.Walking or EvacStatus.WaitingEscort or EvacStatus.Escorted or EvacStatus.Blocked or EvacStatus.Arrived or EvacStatus.Stayed
            || status == EvacStatus.Missing && !missingOffMap;

        /// <summary>Not at a place yet (the drill ends when none is). Not the blocked or the missing: they hold nothing up.</summary>
        public bool InProgress => status is EvacStatus.Planning or EvacStatus.Walking or EvacStatus.WaitingEscort or EvacStatus.Escorted;

        /// <summary>The line as it stood, for <see cref="EvacuationState.ended"/>: no pawn and no route kept with it.</summary>
        public EvacEntry EndedCopy()
        {
            var copy = (EvacEntry)MemberwiseClone();
            copy.pawn = null;
            copy.route = null;
            return copy;
        }
    }

    /// <summary>
    /// A map's evacuation (see <see cref="EvacuationSystem"/>): the assembly points the warden marked (at most <see cref="MaxPoints"/>), and the evacuation running now, if any (the wing, the phase, drill or not, and a line per
    /// inmate). Default: no points, nothing running; a save from before has neither.
    /// </summary>
    public sealed class EvacuationState : IExposable
    {
        public const int MaxPoints = 2;

        public List<AssemblyPoint> points = new List<AssemblyPoint>();
        public PrisonZoneDef wing;
        public EvacPhase phase;
        public bool drill;
        public int startTick = -1, lastFireTick = -1, backTick = -1;
        public List<EvacEntry> entries = new List<EvacEntry>();
        /// <summary>When the last drill began (a drill a day at most: within a day of it, none), and when the last fire letter was sent.</summary>
        public int lastDrillTick = -999999, fireLetterTick = -999999;
        /// <summary>When each wing's last fire letter was sent (a letter a wing every few hours, not one for the whole map).</summary>
        public Dictionary<PrisonZoneDef, int> wingLetterTicks = new Dictionary<PrisonZoneDef, int>();

        // Not saved.
        private Dictionary<int, EvacEntry> byId;
        private readonly Queue<EvacEntry> planQueue = new Queue<EvacEntry>();
        /// <summary>Read from a save: the routes and the door passes are made again (first tick).</summary>
        public bool rebuildPending;
        /// <summary>
        /// The lines of the evacuation that ended last (<see cref="EvacuationSystem.Finish"/>), as they stood when it ended; null once another starts or one is dropped (<see cref="EvacuationSystem.Reset"/>). The beat that counts the last
        /// one home also ends it, so nothing that looks after the tick sees that line on the running roll call (see <see cref="EvacuationSystem.EndedEntryOf"/>).
        /// </summary>
        public List<EvacEntry> ended;

        public bool Active => wing != null && phase != EvacPhase.None;

        /// <summary>A drill began less than a day ago. (A rolling day, not the calendar's: <see cref="GenDate.DaysPassed"/> turns over at the hour the game began, and a drill at 3 am whose walk back ran past it was "yesterday's".)</summary>
        public bool DrilledWithinADay(int now) => System.Math.Abs(now - lastDrillTick) < GenDate.TicksPerDay;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref points, "points", LookMode.Deep);
            Scribe_Defs.Look(ref wing, "wing");
            Scribe_Values.Look(ref phase, "phase");
            Scribe_Values.Look(ref drill, "drill");
            Scribe_Values.Look(ref startTick, "startTick", -1);
            Scribe_Values.Look(ref lastFireTick, "lastFireTick", -1);
            Scribe_Values.Look(ref backTick, "backTick", -1);
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            Scribe_Values.Look(ref lastDrillTick, "lastDrillTick", -999999);
            Scribe_Values.Look(ref fireLetterTick, "fireLetterTick", -999999);
            Scribe_Collections.Look(ref wingLetterTicks, "wingLetterTicks", LookMode.Def, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                points ??= new List<AssemblyPoint>();
                entries ??= new List<EvacEntry>();
                entries.RemoveAll(e => e == null);
                wingLetterTicks ??= new Dictionary<PrisonZoneDef, int>();
                wingLetterTicks.RemoveAll(kv => kv.Key == null);
                // (A point whose zone is gone, or is no longer a place to mark (visitation, once offered), is dropped, and the lines that named a later point by its index follow it up the list; a line whose point went is planned again
                // on the first tick, see TickPlanning.)
                if (points.Exists(p => !EvacuationSystem.MarkableZone(p?.zone)) || points.Count > MaxPoints)
                {
                    var moved = new int[points.Count];
                    int kept = 0;
                    for (int i = 0; i < points.Count; i++) moved[i] = EvacuationSystem.MarkableZone(points[i]?.zone) && kept < MaxPoints ? kept++ : -1;
                    foreach (EvacEntry e in entries)
                    {
                        if (e.point < 0) continue;
                        e.point = e.point < moved.Length ? moved[e.point] : -1;
                        if (e.point < 0) e.pointDropped = true;
                    }
                    points.RemoveAll(p => !EvacuationSystem.MarkableZone(p?.zone));
                    if (points.Count > MaxPoints) points.RemoveRange(MaxPoints, points.Count - MaxPoints);
                }
                if (wing == null || phase == EvacPhase.None) Clear();
                else rebuildPending = true;
                byId = null;
            }
        }

        /// <summary>Ends the evacuation (the points stay).</summary>
        public void Clear()
        {
            wing = null;
            phase = EvacPhase.None;
            drill = false;
            startTick = lastFireTick = backTick = -1;
            entries.Clear();
            planQueue.Clear();
            byId = null;
            rebuildPending = false;
        }

        public void EntriesChanged() => byId = null;

        public EvacEntry Get(int id)
        {
            if (entries.Count == 0) return null;
            if (byId == null)
            {
                byId = new Dictionary<int, EvacEntry>(entries.Count);
                foreach (EvacEntry e in entries) byId[e.id] = e;
            }
            return byId.TryGetValue(id, out EvacEntry found) ? found : null;
        }

        public void Enqueue(EvacEntry e)
        {
            if (e.queued) return;
            e.queued = true;
            planQueue.Enqueue(e);
        }

        public int Queued => planQueue.Count;

        public EvacEntry Dequeue()
        {
            if (planQueue.Count == 0) return null;
            EvacEntry e = planQueue.Dequeue();
            e.queued = false;
            return e;
        }
    }

    /// <summary>
    /// The routes of an evacuation: a breadth-first walk over the cells that finds the way from where an inmate is to where they are going, through doors that are not locked, not held by rioters, not perimeter doors (nor into a
    /// staff room open to the outside) and not another inmate's cell (and, for a walker, no staff-side door and no cell of another wing either), and never through or beside fire. The doors on the way are the whole of the pass
    /// the inmate gets (see <see cref="CompInmate.evacDoors"/>, <see cref="EscortLeg.doors"/>): no door off the route opens for them. Main thread only; the arrays are reused.
    /// </summary>
    public static class EvacRoutes
    {
        public sealed class Route
        {
            /// <summary>Each cell of the way and its step (1 for the first cell out of the one they start in, the most for the destination): a route is judged by the cells still ahead of them.</summary>
            public readonly Dictionary<int, int> steps = new Dictionary<int, int>();
            /// <summary>The doors on the way: the walker's pass (handed over once made, never changed after).</summary>
            public readonly HashSet<Building_Door> doors = new HashSet<Building_Door>();
            /// <summary>The doors on the way with their step (a door still ahead that has been locked since makes the route bad).</summary>
            public readonly List<(Building_Door door, int step)> doorSteps = new List<(Building_Door, int)>();
        }

        private static int[] stamp, parent, queue;
        private static int current;
        private static readonly List<int> backTrack = new List<int>();

        private static readonly KeptRooms.PerMapTickCache<HashSet<int>> fireMasks = new KeptRooms.PerMapTickCache<HashSet<int>>();

        /// <summary>The cells with a fire in them or next to one, for this tick (shared: do not change).</summary>
        public static HashSet<int> FireMask(Map map) => fireMasks.Get(map.uniqueID, Find.TickManager.TicksGame, () =>
        {
            var mask = new HashSet<int>();
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count; i++)
            {
                IntVec3 f = fires[i].Position;
                mask.Add(map.cellIndices.CellToIndex(f));
                for (int k = 0; k < 8; k++)
                {
                    IntVec3 n = f + GenAdj.AdjacentCells[k];
                    if (n.InBounds(map)) mask.Add(map.cellIndices.CellToIndex(n));
                }
            }
            return mask;
        });

        /// <summary>A staff room on the door's other side that is open to the outside (an outdoor or map-edge staff area): an inmate left there by a lost guard would be outside the walls.</summary>
        private static bool StaffOutdoors(DoorSide side, Room room) => side == DoorSide.Staff && room != null && (room.PsychologicallyOutdoors || room.TouchesMapEdge);

        /// <summary>Whether a door may be on a route at all, and for whom (an escort may use a door into a staff room, a walker may not).</summary>
        public static bool DoorAllowed(Building_Door door, bool escorted, Room start, Room dest)
        {
            CompPrisonDoor comp = door.GetComp<CompPrisonDoor>();
            if (comp == null) return true;
            // (Held by rioters: the door verdict refuses the pass there, so the planner does too, or the route would be made and refused again every few seconds.)
            if (comp.mode == PrisonDoorMode.Locked || comp.heldCells != null) return false;
            if (comp.sideA is DoorSide.Outside or DoorSide.None || comp.sideB is DoorSide.Outside or DoorSide.None) return false;
            if (StaffOutdoors(comp.sideA, comp.roomA) || StaffOutdoors(comp.sideB, comp.roomB)) return false;
            if (!escorted && (comp.sideA == DoorSide.Staff || comp.sideB == DoorSide.Staff)) return false;
            if (comp.cellRoom is { } behind && behind != start && behind != dest) return false;
            return true;
        }

        /// <summary>A door on a route still lets the route be walked: standing, not locked since, not held by rioters, and still not a way outside.</summary>
        public static bool DoorStillUsable(Building_Door door)
        {
            if (door == null || door.Destroyed || !door.Spawned) return false;
            CompPrisonDoor comp = door.GetComp<CompPrisonDoor>();
            if (comp == null) return true;
            return comp.mode != PrisonDoorMode.Locked && comp.heldCells == null && comp.sideA is not (DoorSide.Outside or DoorSide.None) && comp.sideB is not (DoorSide.Outside or DoorSide.None)
                && !StaffOutdoors(comp.sideA, comp.roomA) && !StaffOutdoors(comp.sideB, comp.roomB);
        }

        /// <summary>The shortest way from <paramref name="from"/> to <paramref name="to"/> by the rules above, or null when there is none.</summary>
        public static Route Plan(Map map, IntVec3 from, IntVec3 to, bool escorted)
        {
            if (!from.IsValid || !to.IsValid || !from.InBounds(map) || !to.InBounds(map)) return null;
            CellIndices ci = map.cellIndices;
            int n = ci.NumGridCells;
            if (stamp == null || stamp.Length != n)
            {
                stamp = new int[n];
                parent = new int[n];
                queue = new int[n];
                current = 0;
            }
            if (++current == int.MaxValue)
            {
                System.Array.Clear(stamp, 0, n);
                current = 1;
            }
            HashSet<int> fires = FireMask(map);
            int s = ci.CellToIndex(from), t = ci.CellToIndex(to);
            if (fires.Contains(t)) return null;
            var route = new Route();
            if (s == t) return route;
            Room startRoom = from.GetRoom(map), destRoom = to.GetRoom(map);
            // (A walker keeps to the wing they leave and the one they go to: another level's wing is not theirs to cross, even with the doors open. A guard's walk may.)
            ZoneGrid zones = escorted ? null : PrisonMap.Of(map)?.zones;
            PrisonZoneDef startWing = zones?.ZoneAt(from) is { security: not null } sw ? sw : null, destWing = zones?.ZoneAt(to) is { security: not null } dw ? dw : null;
            int head = 0, tail = 0;
            queue[tail++] = s;
            stamp[s] = current;
            parent[s] = -1;
            while (head < tail)
            {
                int cur = queue[head++];
                IntVec3 c = ci.IndexToCell(cur);
                for (int k = 0; k < 4; k++)
                {
                    IntVec3 nb = c + GenAdj.CardinalDirections[k];
                    if (!nb.InBounds(map)) continue;
                    int ni = ci.CellToIndex(nb);
                    if (stamp[ni] == current || !nb.Walkable(map) || fires.Contains(ni)) continue;
                    Building_Door door = nb.GetDoor(map);
                    if (door != null)
                    {
                        if (!DoorAllowed(door, escorted, startRoom, destRoom)) continue;
                    }
                    else if (nb.GetRoom(map) is { IsPrisonCell: true } room && room != startRoom && room != destRoom) continue;
                    if (zones != null && zones.ZoneAt(ni) is { security: not null } z && z != startWing && z != destWing) continue;
                    stamp[ni] = current;
                    parent[ni] = cur;
                    if (ni == t)
                    {
                        // (Not the cell they start in: they may stand beside a fire already, and that is no reason to plan again.)
                        backTrack.Clear();
                        for (int at = t; at >= 0 && at != s; at = parent[at]) backTrack.Add(at);
                        int steps = backTrack.Count;
                        for (int j = 0; j < steps; j++)
                        {
                            int cell = backTrack[j], step = steps - j;
                            route.steps[cell] = step;
                            if (ci.IndexToCell(cell).GetDoor(map) is { } onWay)
                            {
                                route.doors.Add(onWay);
                                route.doorSteps.Add((onWay, step));
                            }
                        }
                        return route;
                    }
                    queue[tail++] = ni;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Evacuating a wing and accounting for its people (gap analysis 6, Astra 8). The warden marks up to two assembly points (a yard, a kept room, a holding patch) with the levels each takes. A fire in a wing with
    /// occupied cells (or in a solitary cell beside it: the hole is part of the wing it is attached to) puts a letter up (and the Deployment tab a button), and nothing starts by itself. Minimum, medium and protective inmates
    /// walk to a point on a route of their own (a pass that opens the doors on that route and no other: <see cref="CompInmate.evacDoors"/>); maximum, supermax, the hole, death row, the downed and a walker with no way of their
    /// own wait where they are for a guard (the supermax escort's walk and pass, <see cref="EscortKind.Evacuation"/>, or a carry); the roll call is the Deployment tab's list. Nobody is sent into fire (a route whose cells
    /// still ahead burn is planned again; no safe point is the survival fallback, the nearest safe room they may enter; supermax only ever to a cage of its own, alone). After one hour with no fire in the wing, or on the
    /// warden's word (never while it burns), everyone goes back to their own bed; a fire in the wing on the way back turns them all out again. A drill runs the same without a fire (and a real fire takes over from it). Nothing that belongs to the prison is changed: door modes, wing restrictions, lockdowns,
    /// punishment clocks, bed owners.
    /// </summary>
    public static class EvacuationSystem
    {
        /// <summary>How many inmates a tick are given a route (a prison of three hundred planned in one tick would stall it).</summary>
        public const int PlanPerTick = 3;
        /// <summary>The beat the roll call is checked on.</summary>
        public const int BeatTicks = 250;
        public const int RetryTicks = 600;
        public const int ClearAfterTicks = GenDate.TicksPerHour;
        public const int MissingAfterTicks = 2 * GenDate.TicksPerHour;
        public const int BackCapTicks = 3 * GenDate.TicksPerHour;
        /// <summary>How long after the all-clear one with no way back to their bed holds the end (two tries at the way): after that the evacuation is over without them, and they are listed.</summary>
        public const int BackBlockedHoldTicks = 2 * RetryTicks;
        public const int DrillCapTicks = 2 * GenDate.TicksPerHour;
        public const int LetterGapTicks = 4 * GenDate.TicksPerHour;
        public const int MaxFallbackTries = 4;

        /// <summary>For the checks: only these inmates are taken into an evacuation (the real inmates of the live prison are not moved by a check's evacuation); null for the game.</summary>
        internal static HashSet<Pawn> OnlyThese;

        public static EvacuationState StateOf(Map map) => map == null ? null : PrisonMap.Of(map)?.evac;

        public static bool Running(Map map) => StateOf(map) is { Active: true };

        private static EvacuationState Of(Pawn p) => p?.MapHeld == null ? null : PrisonMap.Of(p.MapHeld)?.evac;

        /// <summary>The inmate is on the roll call of a running evacuation and is being moved (or kept at the point): the regime's own errands (the compliance reading, the supermax guard's fetch) leave them be. Cheap: it is asked per inmate.</summary>
        public static bool Involves(Pawn p) => Of(p) is { Active: true } s && s.Get(p.thingIDNumber) is { Moving: true };

        /// <summary>The evacuation has something to say about the inmate's next job: they are being moved, or they were outside the wing when it began and the wing is still being emptied (they stay out of it).</summary>
        public static bool HasJobFor(Pawn p) => Of(p) is { Active: true } s && s.Get(p.thingIDNumber) is { } e && (e.Moving || e.status == EvacStatus.Outside && s.phase == EvacPhase.Out && !s.drill);

        /// <summary>The line of this inmate in the running evacuation, or null.</summary>
        public static EvacEntry EntryOf(Pawn p) => Of(p) is { Active: true } s ? s.Get(p.thingIDNumber) : null;

        /// <summary>The line this inmate had when the last evacuation of their map ended (null while one runs, or when they were not on it).</summary>
        public static EvacEntry EndedEntryOf(Pawn p) => Of(p) is { Active: false, ended: { } lines } ? lines.Find(e => e.id == p.thingIDNumber) : null;

        /// <summary>
        /// A downed inmate being carried out of a wing that burns, or waiting for it (the existing rescue would carry them to a bed in it): the rescue leaves them to the evacuation. Once at the point they may be rescued (to a bed
        /// outside the wing, see <see cref="BedAllowed"/>), and so may one with no way out (route blocked, or missing for want of a way): no guard of the evacuation carries them, and the rescue's carry needs no route of its own.
        /// </summary>
        public static bool Keeps(Pawn p) => Of(p) is { Active: true, phase: EvacPhase.Out } s && s.Get(p.thingIDNumber) is { Moving: true, status: not (EvacStatus.Arrived or EvacStatus.Blocked or EvacStatus.Missing) };

        /// <summary>
        /// The wing is being emptied and the inmate is on its roll call (being moved or at the point): vanilla's warden carry to a bed, which knows only their own bed (in the wing), leaves them alone, and so does its escort of one who
        /// cannot reach their bed by themselves (a supermax inmate in the cage) back to it (see <see cref="Patch_WorkGiver_Warden_TakeToBed_Evacuees"/>).
        /// </summary>
        public static bool BeingEmptied(Pawn p) => Of(p) is { Active: true, phase: EvacPhase.Out } s && s.Get(p.thingIDNumber) is { Moving: true };

        /// <summary>The inmate is on the roll call of a running evacuation (anything but dropped from it): the bed they own is theirs and stays theirs, and nothing moves them to another cell until it is over (the hourly move out of a wrong wing, the daily review).</summary>
        public static bool HoldsBed(Pawn p) => Of(p) is { Active: true } s && s.Get(p.thingIDNumber) is { status: not EvacStatus.Dropped };

        /// <summary>
        /// A bed an inmate on the roll call may be carried to: a medical bed (it takes no owner), or their own; never another bed, which vanilla's carry would make theirs (bed owners never change in an evacuation). While the wing is
        /// emptied, none in the wing (or a solitary cell of it: their own bed is there) and none in a room with a fire. Any bed for anyone else.
        /// </summary>
        public static bool BedAllowed(Pawn inmate, Building_Bed bed)
        {
            if (bed == null || Of(inmate) is not { Active: true } s || s.Get(inmate.thingIDNumber) is not { status: not EvacStatus.Dropped }) return true;
            if (!bed.Medical && !bed.OwnersForReading.Contains(inmate)) return false;
            if (FireInRoom(bed.Map, bed.GetRoom())) return false;
            if (s.phase != EvacPhase.Out) return true;
            PrisonMap pm = PrisonMap.Of(bed.Map);
            return pm == null || WingAt(pm, bed.Position) != s.wing;
        }

        /// <summary>A downed (or medically resting) inmate lying in a bed outside the wing that may hold them (a medical bed, or their own when it is not in the wing), with no fire in its room: the rescue's, accounted for there.</summary>
        public static bool InRescueBed(PrisonMap pm, Pawn p, PrisonZoneDef wing)
        {
            if (!p.Spawned || p.CarriedBy != null || !(p.Downed || HealthAIUtility.ShouldSeekMedicalRest(p)) || p.CurrentBed() is not { } bed) return false;
            if (!bed.Medical && !bed.OwnersForReading.Contains(p)) return false;
            return WingAt(pm, bed.Position) != wing && !FireInRoom(p.Map, bed.GetRoom());
        }

        // ---- the wing ---------------------------------------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<int, PrisonZoneDef> holeWings = new Dictionary<int, PrisonZoneDef>();
        private static int holeWingsTick = -1, holeWingsMap = -1;

        /// <summary>The wing a cell belongs to for an evacuation: the wing zone painted on it, or for a solitary cell (the hole, a zone of no level) the wing it stands beside or in; null for anywhere else.</summary>
        public static PrisonZoneDef WingAt(PrisonMap pm, IntVec3 c)
        {
            PrisonZoneDef z = pm.zones.ZoneAt(c);
            if (z == null) return null;
            if (z.security != null) return z;
            return z == RPSecurityDefOf.RP_SolitaryZone ? HoleWing(pm, c) : null;
        }

        /// <summary>The wing zone within two cells of the solitary cell's room (beside it through a wall, or round it), null for a hole on its own; remembered for the tick, per room.</summary>
        private static PrisonZoneDef HoleWing(PrisonMap pm, IntVec3 c)
        {
            Map map = pm.map;
            Room room = c.GetRoom(map);
            if (room == null) return null;
            int now = Find.TickManager.TicksGame;
            if (now != holeWingsTick || map.uniqueID != holeWingsMap)
            {
                holeWings.Clear();
                (holeWingsTick, holeWingsMap) = (now, map.uniqueID);
            }
            if (holeWings.TryGetValue(room.ID, out PrisonZoneDef known)) return known;
            PrisonZoneDef found = null;
            int looked = 0;
            foreach (IntVec3 cell in room.Cells)
            {
                // (A solitary zone painted into some big room is no cell: a few dozen cells are looked at, no more.)
                if (++looked > 64) break;
                for (int dx = -2; dx <= 2 && found == null; dx++)
                    for (int dz = -2; dz <= 2 && found == null; dz++)
                    {
                        var x = new IntVec3(cell.x + dx, 0, cell.z + dz);
                        if (x.InBounds(map) && pm.zones.ZoneAt(x) is { security: not null } w) found = w;
                    }
                if (found != null) break;
            }
            holeWings[room.ID] = found;
            return found;
        }

        // ---- the assembly points ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A zone whose patches can be marked: a yard, a kept room (a patch of a shared zone inside the walls) or the holding patch, the supermax yard among them; never a staff zone or a wing, and never visitation: the room the public
        /// comes into, with its door to the outside and the visitors in it, is no place to gather a wing.
        /// </summary>
        public static bool MarkableZone(PrisonZoneDef z) => z != null && z.inmateAccess && !z.staffOnly && z.security == null && z != RPVisitDefOf.RP_Visitation && (z.keepable || z == RPDefOf.RP_Holding || z.yardFor != null);

        /// <summary>The levels a new point takes: every level but supermax, which has the exercise cage of its own (a yard that names the level) or nothing.</summary>
        public static List<SecurityLevelDef> DefaultLevels(PrisonZoneDef zone)
        {
            if (zone.yardFor != null) return new List<SecurityLevelDef> { zone.yardFor };
            return DefDatabase<SecurityLevelDef>.AllDefsListForReading.Where(l => l != SupermaxSystem.Level).OrderBy(l => l.uiOrder).ToList();
        }

        /// <summary>The index in the zone grid's patches of the point's patch, -1 when it is gone (repainted away). A repaint that moved the patch's first cell is followed.</summary>
        public static int PatchOf(PrisonMap pm, AssemblyPoint point)
        {
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].zone == point.zone && patches[i].anchor == point.anchor) return i;
            int at = pm.zones.PatchAt(pm.map.cellIndices.IndexToCell(point.anchor));
            if (at >= 0 && patches[at].zone == point.zone)
            {
                point.anchor = patches[at].anchor;
                return at;
            }
            return -1;
        }

        /// <summary>Marks the patch as an assembly point (false: two already, or it is not a place to mark, or it is one already).</summary>
        public static bool Mark(PrisonMap pm, int patch)
        {
            EvacuationState s = pm.evac;
            List<ZonePatch> patches = pm.zones.Patches;
            if (patch < 0 || patch >= patches.Count || !MarkableZone(patches[patch].zone) || s.points.Count >= EvacuationState.MaxPoints) return false;
            foreach (AssemblyPoint p in s.points)
                if (PatchOf(pm, p) == patch) return false;
            s.points.Add(new AssemblyPoint { zone = patches[patch].zone, anchor = patches[patch].anchor, levels = DefaultLevels(patches[patch].zone) });
            return true;
        }

        public static void Unmark(PrisonMap pm, AssemblyPoint point)
        {
            EvacuationState s = pm.evac;
            int index = s.points.IndexOf(point);
            if (index < 0) return;
            s.points.RemoveAt(index);
            // (A running evacuation names its points by index: the later ones move up, and whoever was going to (or is at) the point that is gone is planned again.)
            if (s.Active)
                foreach (EvacEntry e in s.entries)
                {
                    if (e.fallback || e.point < 0) continue;
                    if (e.point > index) e.point--;
                    else if (e.point == index)
                    {
                        e.point = -1;
                        if (e.Moving) Replan(pm, e);
                    }
                }
        }

        public static void ToggleLevel(AssemblyPoint point, SecurityLevelDef level)
        {
            if (!point.levels.Remove(level)) point.levels.Add(level);
            point.levels.Sort((a, b) => a.uiOrder.CompareTo(b.uiOrder));
        }

        /// <summary>Some point that is still there takes inmates of this level (supermax: only a cage of its own).</summary>
        public static bool HasPointFor(PrisonMap pm, SecurityLevelDef level)
        {
            foreach (AssemblyPoint p in pm.evac.points)
                if (p.TakesAlone(level) && PatchOf(pm, p) >= 0) return true;
            return false;
        }

        /// <summary>The wings (zones with a security level, with the solitary cells beside them) that have a fire in them now.</summary>
        public static List<PrisonZoneDef> BurningWings(Map map, PrisonMap pm)
        {
            var wings = new List<PrisonZoneDef>();
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count; i++)
                if (WingAt(pm, fires[i].Position) is { } z && !wings.Contains(z)) wings.Add(z);
            return wings;
        }

        public static bool FireInWing(Map map, PrisonMap pm, PrisonZoneDef wing)
        {
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count; i++)
                if (WingAt(pm, fires[i].Position) == wing) return true;
            return false;
        }

        /// <summary>A patch with a fire in it or beside it (the screen's warning colour).</summary>
        public static bool PatchBurning(PrisonMap pm, int patch)
        {
            List<Thing> fires = pm.map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            if (fires.Count == 0) return false;
            CellRect rect = pm.zones.Patches[patch].bounds.ExpandedBy(1);
            for (int i = 0; i < fires.Count; i++)
                if (rect.Contains(fires[i].Position)) return true;
            return false;
        }

        /// <summary>A fire in one of the patch's own cells: no place to send anyone (a fire only beside it leaves the cells away from it, which the planner picks by the cell).</summary>
        public static bool FireInPatch(PrisonMap pm, int patch)
        {
            List<Thing> fires = pm.map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count; i++)
                if (pm.zones.PatchAt(fires[i].Position) == patch) return true;
            return false;
        }

        public static bool FireInRoom(Map map, Room room)
        {
            if (room == null) return false;
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count; i++)
                if (fires[i].Spawned && fires[i].Position.GetRoom(map) == room) return true;
            return false;
        }

        // ---- starting ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The active inmates the evacuation of this wing concerns: those standing in it (moved), and those whose bed is in it who are elsewhere (accounted for where they are). The wing's solitary cells count as the wing.</summary>
        public static List<(Pawn pawn, bool inside)> Occupants(Map map, PrisonMap pm, PrisonZoneDef wing)
        {
            var found = new List<(Pawn, bool)>();
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                if (!InmateUtility.IsActiveInmate(p) || p.Inmate() is not { } c || ShuttleSystem.OnTheWayOut(p) || ShuttleSystem.IsArrival(p) || OnlyThese != null && !OnlyThese.Contains(p)) continue;
                bool inside = WingAt(pm, p.Position) == wing;
                bool owns = p.ownership?.OwnedBed is { Spawned: true } bed && WingAt(pm, bed.Position) == wing;
                if (inside || owns) found.Add((p, inside));
            }
            found.Sort((a, b) => a.Item1.thingIDNumber.CompareTo(b.Item1.thingIDNumber));
            return found;
        }

        /// <summary>Why an evacuation of this wing cannot start now (the words for the screen), or null when it can. A real one may start while a drill runs (it takes over); nothing else while one runs.</summary>
        public static string CannotStart(Map map, PrisonZoneDef wing, bool drill)
        {
            PrisonMap pm = map == null ? null : PrisonMap.Of(map);
            if (pm == null || wing?.security == null) return "RP_EvacNoWing".Translate();
            if (pm.evac.Active && (drill || !pm.evac.drill)) return "RP_EvacAlready".Translate();
            if (drill)
            {
                if (pm.evac.DrilledWithinADay(Find.TickManager.TicksGame)) return "RP_EvacDrilledToday".Translate();
                if (FireInWing(map, pm, wing)) return "RP_EvacDrillFire".Translate(wing.label);
                // (A drill moves people about: not while they are locked down, and not in a riot.)
                if (PrisonGame.Instance is { } game && game.LevelLockedDown(wing.security)) return "RP_EvacDrillLockdown".Translate();
                if (RiotPolice.RiotOngoing(map)) return "RP_EvacDrillRiot".Translate();
            }
            // (The levels of the people standing in it, whatever level the wing is painted for: one of them with a point is enough, the others go to the fallback.)
            var levels = new List<SecurityLevelDef>();
            foreach ((Pawn p, bool inside) in Occupants(map, pm, wing))
                if (inside && p.Inmate()?.security is { } level && !levels.Contains(level)) levels.Add(level);
            if (levels.Count == 0) return "RP_EvacNoOne".Translate(wing.label);
            if (!levels.Exists(l => HasPointFor(pm, l))) return "RP_EvacNoPoint".Translate(string.Join(", ", levels.OrderBy(l => l.uiOrder).Select(l => l.label)));
            return null;
        }

        /// <summary>Whether an inmate waits for a guard rather than walking: maximum, supermax, the hole, death row, the downed.</summary>
        public static bool NeedsGuard(Pawn p, CompInmate c) =>
            p.Downed || c.security is { escortOnly: true } || c.security == RPSecurityDefOf.RP_Maximum || DeathRowSystem.Ext(c) != null || c.forcedActivity == RPSecurityDefOf.RP_Solitary;

        /// <summary>Starts the evacuation of the wing (or its drill). Never by itself: the warden's letter, button or drill. A real evacuation called during a drill ends the drill first.</summary>
        public static bool Start(Map map, PrisonZoneDef wing, bool drill)
        {
            string why = CannotStart(map, wing, drill);
            if (why != null)
            {
                Messages.Message(why, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            PrisonMap pm = PrisonMap.Of(map);
            EvacuationState s = pm.evac;
            if (s.Active)
            {
                PrisonZoneDef drilled = s.wing;
                Reset(map);
                Messages.Message("RP_EvacDrillCalledOff".Translate(drilled.label), MessageTypeDefOf.CautionInput, historical: false);
            }
            int now = Find.TickManager.TicksGame;
            s.Clear();
            s.ended = null;
            (s.wing, s.phase, s.drill, s.startTick, s.lastFireTick) = (wing, EvacPhase.Out, drill, now, now);
            foreach ((Pawn p, bool inside) in Occupants(map, pm, wing))
            {
                var e = new EvacEntry { id = p.thingIDNumber, name = p.LabelShort, pawn = p, sinceTick = now, status = inside ? EvacStatus.Planning : EvacStatus.Outside };
                if (inside) e.escort = NeedsGuard(p, p.Inmate());
                s.entries.Add(e);
            }
            s.EntriesChanged();
            AssignGroups(s);
            // (Those who are never with anyone else are planned first: a cage the warden ticked other levels on is theirs before anyone else's, see ChooseOut.)
            foreach (EvacEntry e in s.entries.Where(e => e.status == EvacStatus.Planning).OrderBy(e => e.pawn.Inmate()?.security is { escortOnly: true } ? 0 : 1))
                s.Enqueue(e);
            if (drill) s.lastDrillTick = now;
            int moving = s.entries.Count(e => e.status == EvacStatus.Planning);
            Messages.Message((drill ? "RP_EvacDrillStarted" : "RP_EvacStarted").Translate(wing.label, moving, s.entries.Count - moving), MessageTypeDefOf.CautionInput, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, drill ? 1 : 3, (drill ? "RP_Chr_EvacDrill" : "RP_Chr_Evacuation").Translate(wing.label, moving));
            return true;
        }

        /// <summary>
        /// Who goes to the second point (when there is one): protective custody, those the old gang's green light is on, and one of each pair that must not stand together - a keep-apart pair or a feud: the inmates are taken in order
        /// of id, and one whose partner (or enemy) was already put at the first point goes to the second. (A feud is two-sided: were both sent, they would stand together at the second.)
        /// </summary>
        private static void AssignGroups(EvacuationState s)
        {
            var moving = s.entries.Where(e => e.status == EvacStatus.Planning && e.pawn != null).OrderBy(e => e.id).ToList();
            var first = new List<EvacEntry>();
            foreach (EvacEntry e in moving)
            {
                e.second = Threatened(e, first);
                if (!e.second) first.Add(e);
            }
        }

        private static bool Threatened(EvacEntry e, List<EvacEntry> first)
        {
            CompInmate c = e.pawn.Inmate();
            if (c == null) return false;
            if (c.security == RPSecurityDefOf.RP_Protective || c.exMarked) return true;
            foreach (EvacEntry other in first)
                if (other != e && other.pawn != null && PrisonRelations.Enemies(e.pawn, other.pawn)) return true;
            if (KeepApart.OrdersOf(e.pawn) is { } orders)
                foreach (KeepApartOrder o in orders)
                    if (o.Other(e.pawn) is { } partner && first.Exists(f => f.id == partner.thingIDNumber)) return true;
            return false;
        }

        // ---- the all-clear and the end -----------------------------------------------------------------------------------------------------------------------

        /// <summary>Drops the evacuation at once, passes and walks with it, with no word said (for the checks' rollback, a map that is going away, and a drill a real fire takes over from).</summary>
        public static void Reset(Map map)
        {
            PrisonMap pm = map == null ? null : PrisonMap.Of(map);
            if (pm?.evac is not { } s) return;
            foreach (EvacEntry e in s.entries)
            {
                Pawn p = e.pawn ?? Resolve(map, e);
                if (p?.Inmate() is not { } c) continue;
                ClearPass(p, c);
                EndGuardWalk(e, p);
                if (c.leg is { kind: EscortKind.Evacuation } && p.Spawned) EscortWalk.End(p, c);
            }
            s.Clear();
            s.ended = null;
        }

        /// <summary>
        /// The all-clear: everyone goes back to their own bed (the passes are made again for the way home), those listed missing for want of a way among them (they are on the map; the way may be open now). The drill's letter is sent
        /// now, with the slowest arrival and every route that was blocked. Never while the wing burns (a fire in it after the all-clear turns everyone out again, see <see cref="BackOut"/>): the warden's button is refused, and says why.
        /// </summary>
        public static void AllClear(Map map, bool byWarden)
        {
            PrisonMap pm = PrisonMap.Of(map);
            EvacuationState s = pm?.evac;
            if (s == null || !s.Active || s.phase != EvacPhase.Out) return;
            if (FireInWing(map, pm, s.wing))
            {
                if (byWarden) Messages.Message("RP_EvacAllClearBurning".Translate(s.wing.label), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (s.drill) SendDrillLetter(map, s, now);
            s.phase = EvacPhase.Back;
            s.backTick = now;
            foreach (EvacEntry e in s.entries)
            {
                Pawn p = Resolve(map, e);
                bool missingHere = e.status == EvacStatus.Missing && p is { Spawned: true };
                // (Rescued and still in the bed: the doctors' patient, who stays there until well and then goes to their own bed by the regime, the bed being theirs still. Up and about again: home with the rest.)
                bool rescuedUp = e.status == EvacStatus.Rescued && p is { Spawned: true } && !InRescueBed(pm, p, s.wing);
                if (!e.Moving && !missingHere && !rescuedUp) continue;
                if (p?.Inmate() is { } c) ClearPass(p, c);
                EndGuardWalk(e, p);
                e.point = -1;
                e.dest = IntVec3.Invalid;
                e.route = null;
                e.fallback = false;
                SetStatus(e, EvacStatus.Planning, now);
                e.sinceTick = now;
                e.retryTick = 0;
                s.Enqueue(e);
            }
            Messages.Message((byWarden ? "RP_EvacAllClearWarden" : "RP_EvacAllClear").Translate(s.wing.label), MessageTypeDefOf.PositiveEvent, historical: false);
        }

        private static void Finish(Map map, PrisonMap pm, EvacuationState s, bool capped)
        {
            int now = Find.TickManager.TicksGame;
            int home = s.entries.Count(e => e.status == EvacStatus.Home), outside = s.entries.Count(e => e.status is EvacStatus.Outside or EvacStatus.Rescued), stayed = s.entries.Count(e => e.status == EvacStatus.Stayed);
            int missing = s.entries.Count(e => e.status == EvacStatus.Missing), dropped = s.entries.Count(e => e.status == EvacStatus.Dropped);
            int notBack = s.entries.Count(e => e.Moving && e.status is not (EvacStatus.Stayed or EvacStatus.Missing)), blocked = s.entries.Count(e => e.status == EvacStatus.Blocked);
            foreach (EvacEntry e in s.entries)
            {
                Pawn p = Resolve(map, e);
                if (p?.Inmate() is not { } c) continue;
                c.evacDoors = null;
                if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
                EndGuardWalk(e, p);
                if (p.Spawned) map.reachability.ClearCacheFor(p);
            }
            string wing = s.wing.label;
            bool drill = s.drill;
            var names = s.entries.Where(e => e.status is EvacStatus.Stayed or EvacStatus.Missing or EvacStatus.Blocked).Select(e => e.name).Take(8).ToList();
            List<EvacEntry> ended = s.entries.Select(e => e.EndedCopy()).ToList();
            s.Clear();
            s.ended = ended;
            Messages.Message("RP_EvacDone".Translate(wing, home, outside, stayed + notBack + missing, dropped), MessageTypeDefOf.NeutralEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, drill ? 1 : 2, "RP_Chr_EvacuationDone".Translate(wing, home, stayed + notBack + missing, names.Count > 0 ? string.Join(", ", names) : "-"));
            if (capped) RPLog.Message($"[RimPrison:evac] the evacuation of {wing} ended at the cap with {notBack} not back at {now}");
            else if (blocked > 0) RPLog.Message($"[RimPrison:evac] the evacuation of {wing} ended with {blocked} who had no way back to their bed: left where they are, the regime's to see in");
        }

        private static void SendDrillLetter(Map map, EvacuationState s, int now)
        {
            EvacEntry slowest = null;
            int moved = 0;
            foreach (EvacEntry e in s.entries)
            {
                if (e.arrivedTick < 0 || e.status == EvacStatus.Outside) continue;
                moved++;
                if (slowest == null || e.arrivedTick - s.startTick > slowest.arrivedTick - s.startTick) slowest = e;
            }
            var blocked = s.entries.Where(e => e.blockedSeen || e.status == EvacStatus.Blocked).Select(e => e.name).ToList();
            var still = s.entries.Where(e => e.InProgress || e.status == EvacStatus.Blocked || e.status == EvacStatus.Missing).Select(e => e.name).ToList();
            var sb = new StringBuilder();
            sb.Append("RP_EvacDrillText".Translate(s.wing.label, moved, s.entries.Count(e => e.status == EvacStatus.Outside)));
            sb.Append("\n\n").Append(slowest == null ? "RP_EvacDrillNoArrival".Translate() : "RP_EvacDrillSlowest".Translate(slowest.name, ((slowest.arrivedTick - s.startTick) / (float)GenDate.TicksPerHour * 60f).ToString("0")));
            if (blocked.Count > 0) sb.Append("\n").Append("RP_EvacDrillBlocked".Translate(string.Join(", ", blocked)));
            if (still.Count > 0) sb.Append("\n").Append("RP_EvacDrillStill".Translate(string.Join(", ", still)));
            int together = SeparationFailures(s);
            if (together > 0) sb.Append("\n").Append("RP_EvacDrillTogether".Translate(together));
            if (blocked.Count == 0 && still.Count == 0 && together == 0) sb.Append("\n").Append("RP_EvacDrillClean".Translate());
            Find.LetterStack.ReceiveLetter("RP_EvacDrillLabel".Translate(s.wing.label), sb.ToString(), blocked.Count > 0 || still.Count > 0 ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent);
        }

        /// <summary>Keep-apart pairs who stand at the same assembly point (the warden's order is not kept: one point only, or no room for a second).</summary>
        public static int SeparationFailures(EvacuationState s)
        {
            if (!KeepApart.Any) return 0;
            int n = 0;
            foreach (KeepApartOrder o in KeepApart.Book.orders)
            {
                EvacEntry a = s.Get(o.idA), b = s.Get(o.idB);
                if (a != null && b != null && a.status == EvacStatus.Arrived && b.status == EvacStatus.Arrived && a.point >= 0 && a.point == b.point && a.dest.IsValid && b.dest.IsValid) n++;
            }
            return n;
        }

        // ---- the passes ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The inmate's pass is taken away (their cached answers of what they can reach go with it, or a path would still be found through a door that no longer opens for them).</summary>
        private static void ClearPass(Pawn p, CompInmate c)
        {
            if (c.evacDoors == null) return;
            c.evacDoors = null;
            if (p.Spawned) p.Map.reachability.ClearCacheFor(p);
        }

        /// <summary>Every tick while an evacuation runs (or was read from a save): a few inmates are given their route.</summary>
        public static void TickPlanning(Map map, PrisonMap pm)
        {
            EvacuationState s = pm.evac;
            if (s.rebuildPending)
            {
                s.rebuildPending = false;
                foreach (EvacEntry e in s.entries)
                {
                    e.pawn = null;
                    e.route = null;
                    e.routeStep = 0;
                    // (Their point was dropped on load: whoever was at it or on the way to it is given a place again, as when the warden unmarks one.)
                    if (e.pointDropped)
                    {
                        e.pointDropped = false;
                        if (e.Moving && e.status != EvacStatus.Missing)
                        {
                            e.dest = IntVec3.Invalid;
                            Replan(pm, e);
                            continue;
                        }
                    }
                    // (Routes and passes are never saved: every inmate who was on the way is planned again. A guard's job is saved: it makes its walk again, see JobDriver_EvacuateInmate; the route the walk follows is made
                    // again here, from where they are, so that a fire on the way ahead is seen before the guard's job ends.)
                    if (e.status is EvacStatus.Walking or EvacStatus.Blocked or EvacStatus.WaitingEscort or EvacStatus.Planning) s.Enqueue(e);
                    else if (e.status == EvacStatus.Escorted && e.dest.IsValid && Resolve(map, e) is { } p) e.route = EvacRoutes.Plan(map, p.PositionHeld, e.dest, escorted: true);
                }
            }
            for (int n = 0; n < PlanPerTick; n++)
            {
                EvacEntry e = s.Dequeue();
                if (e == null) return;
                if (e.Moving) Plan(map, pm, s, e);
            }
        }

        /// <summary>The map's beat (every <see cref="BeatTicks"/>): the roll call of the evacuation that runs, and a look for a fire that has reached a wing (another than the one being emptied).</summary>
        public static void Beat(Map map, PrisonMap pm)
        {
            EvacuationState s = pm.evac;
            if (s == null) return;
            if (s.Active) Refresh(map, pm, s);
            WatchFires(map, pm, s);
        }

        private static void WatchFires(Map map, PrisonMap pm, EvacuationState s)
        {
            int now = Find.TickManager.TicksGame;
            if (map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0) return;
            foreach (PrisonZoneDef wing in BurningWings(map, pm))
            {
                // (The wing being emptied has its evacuation - after its all-clear too: a fire there turns it back out on the beat, before this look, see BackOut; a drill's wing that catches fire while it is out gets the letter, and the real
                // evacuation takes over from the drill.)
                if (s.Active && wing == s.wing && !s.drill) continue;
                if (s.wingLetterTicks.TryGetValue(wing, out int last) && now - last < LetterGapTicks && now >= last) continue;
                int people = Occupants(map, pm, wing).Count(o => o.inside);
                if (people == 0) continue;
                s.wingLetterTicks[wing] = now;
                s.fireLetterTick = now;
                Thing fire = map.listerThings.ThingsOfDef(ThingDefOf.Fire).FirstOrDefault(f => WingAt(pm, f.Position) == wing);
                ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_WingFire", "RP_WingFireLabel".Translate(wing.label),
                    "RP_WingFireText".Translate(wing.label, people, wing.security.label), map, fire != null ? new TargetInfo(fire.Position, map) : TargetInfo.Invalid);
                letter.note = wing.defName;
                letter.StartTimeout(GenDate.TicksPerHour);
                Find.LetterStack.ReceiveLetter(letter);
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_WingFire".Translate(wing.label, people));
                return;
            }
        }

        /// <summary>The roll call, once a beat: who has arrived, who is carried, who waits, whose route burns, who is missing; and whether the all-clear is due.</summary>
        private static void Refresh(Map map, PrisonMap pm, EvacuationState s)
        {
            int now = Find.TickManager.TicksGame;
            bool burning = FireInWing(map, pm, s.wing);
            if (burning && s.phase == EvacPhase.Back) BackOut(map, pm, s, now);
            bool back = s.phase == EvacPhase.Back;
            if (burning && !s.drill) s.lastFireTick = now;
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            HashSet<int> mask = fires.Count > 0 ? EvacRoutes.FireMask(map) : null;
            foreach (EvacEntry e in s.entries)
            {
                if (e.status is EvacStatus.Home or EvacStatus.Dropped) continue;
                if (e.status == EvacStatus.Rescued)
                {
                    // (Up from the rescue's bed, or taken out of it: while the wing is emptied they need a place again (the regime would send them to their cell in it); after the all-clear they go home with the rest.)
                    if (Resolve(map, e) is { Spawned: true } up && InmateUtility.IsActiveInmate(up) && !InRescueBed(pm, up, s.wing))
                    {
                        RPLog.Message($"[RimPrison:evac] {up.LabelShort} is no longer in the bed the rescue took them to (at {up.Position}, {up.CurJobDef?.defName ?? "no job"}): {(back ? "home with the rest" : "given a place again")}");
                        e.point = -1;
                        e.dest = IntVec3.Invalid;
                        e.route = null;
                        e.fallback = false;
                        e.retryTick = 0;
                        SetStatus(e, EvacStatus.Planning, now);
                        s.Enqueue(e);
                    }
                    continue;
                }
                if (e.status == EvacStatus.Outside)
                {
                    // (Outside the wing when it began, and walked into it since: they are moved out with the rest.)
                    if (!back && Resolve(map, e) is { Spawned: true } walkedIn && walkedIn.Inmate() is { } wc && InmateUtility.IsActiveInmate(walkedIn) && WingAt(pm, walkedIn.Position) == s.wing)
                    {
                        e.escort = NeedsGuard(walkedIn, wc);
                        e.second = Threatened(e, s.entries.Where(o => o != e && o.Moving && !o.second && o.pawn != null).ToList());
                        SetStatus(e, EvacStatus.Planning, now);
                        s.Enqueue(e);
                        RPLog.Message($"[RimPrison:evac] {walkedIn.LabelShort} was outside the {s.wing.label} when it was emptied and is in it again: moved out with the rest");
                    }
                    continue;
                }
                Pawn p = Resolve(map, e);
                if (p == null || p.Destroyed || p.Dead || p.Inmate() is not { } c)
                {
                    SetMissing(e, p, offMap: true, now);
                    continue;
                }
                if (!p.IsPrisonerOfColony || p.guest.Released || ShuttleSystem.OnTheWayOut(p))
                {
                    Drop(map, e, p, c, now);
                    continue;
                }
                if (p.CarriedBy == null && !p.Spawned)
                {
                    SetMissing(e, p, offMap: true, now);
                    continue;
                }
                if (AutoTest.Active) LogLying(pm, s, e, p, back);
                // (Back on the map after they had left it: planned again. One listed missing for want of a way stays missing until a way is found (tried again quietly, below): no way yet is no reason to say they were found.)
                if (e.status == EvacStatus.Missing && e.missingOffMap)
                {
                    SetStatus(e, EvacStatus.Planning, now);
                    s.Enqueue(e);
                }
                if (p.InAggroMentalState || p.MentalState is MentalState_Escaping)
                {
                    // (A riot during an evacuation ends the walk of the rioters: the riot response takes them.)
                    Drop(map, e, p, c, now);
                    continue;
                }
                if (p.Downed && !e.escort)
                {
                    e.escort = true;
                    ClearPass(p, c);
                    e.route = null;
                    // (Down at the place they were sent to: they stay arrived, and nobody carries them from their cell to the same cell.)
                    if (!(e.status == EvacStatus.Arrived && p.CarriedBy == null && AtGoal(pm, p, e, back)))
                    {
                        if (e.status == EvacStatus.Walking) SetStatus(e, EvacStatus.Planning, now);
                        s.Enqueue(e);
                    }
                }
                // (For the checks: somebody's job other than the evacuation's has them in their arms while the wing is emptied - the carry that took round 2's evacuees back to their beds in the wing was one.)
                if (AutoTest.Active && !back && p.CarriedBy is { } holder && holder.CurJobDef != RPEvacDefOf.RP_CarryEvacuee)
                    RPLog.Message($"[RimPrison:evac] {p.LabelShort} ({e.status}) is carried by {holder.LabelShort} on {holder.CurJobDef?.defName ?? "no job"} to {holder.CurJob?.targetB.Cell}");
                // (Vanilla's carry to the bed, after the all-clear: the guard has them, and the roll call says so.)
                if (e.status == EvacStatus.WaitingEscort && p.CarriedBy is { } carrier && carrier.CurJob is { } carry && carry.def == JobDefOf.TakeWoundedPrisonerToBed && carry.targetA.Thing == p)
                {
                    e.escortId = carrier.thingIDNumber;
                    SetStatus(e, EvacStatus.Escorted, now);
                }
                if (p.CarriedBy == null && AtGoal(pm, p, e, back))
                {
                    // (Where they were sent, or where they stand in it, has caught fire since: they are sent on, never left in it.)
                    if (e.status == EvacStatus.Arrived && !back && ArrivedUnsafe(pm, e, p, mask)) Replan(pm, e);
                    else if (e.status != EvacStatus.Arrived || back) Arrive(map, e, p, c, back, now);
                    continue;
                }
                if (InRescueBed(pm, p, s.wing))
                {
                    // (Down (or resting for their wounds), and the rescue has put them in a medical bed outside the wing (or their own bed, when it is not in it): accounted for there, and the doctors' now. They stay until well, after
                    // the all-clear too, and the regime takes them to their own bed then: it is still theirs.)
                    EndGuardWalk(e, p);
                    ClearPass(p, c);
                    if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
                    e.route = null;
                    SetStatus(e, EvacStatus.Rescued, now);
                    RPLog.Message($"[RimPrison:evac] {p.LabelShort} is in a bed outside the {s.wing.label} at {p.Position} (the rescue's): accounted for there");
                    continue;
                }
                if (e.status == EvacStatus.Arrived && !back && p.CarriedBy == null && e.escort)
                {
                    // (One who may not walk alone and is no longer at their place (pushed out of the cage, put down elsewhere, a bed in the wing that is no rescue): a guard takes them back to it. A walker walks back by themselves,
                    // see JobFor.)
                    RPLog.Message($"[RimPrison:evac] {p.LabelShort} was arrived and is away from their place at {p.Position} ({(p.InBed() ? "in a bed that is no rescue's" : p.CurJobDef?.defName ?? "no job")}): a guard takes them back to it");
                    Replan(pm, e);
                    continue;
                }
                if (e.status == EvacStatus.Escorted && !GuardHas(map, e, p))
                {
                    // (The guard was lost, or called to something worse: nobody has them by the arm, and where they stand is where the next walk starts.)
                    e.escortId = 0;
                    e.route = null;
                    SetStatus(e, EvacStatus.WaitingEscort, now);
                    s.Enqueue(e);
                    if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
                }
                if (e.status == EvacStatus.Blocked)
                {
                    if (now - e.sinceTick >= MissingAfterTicks)
                    {
                        SetMissing(e, p, offMap: false, now);
                        e.retryTick = now + RetryTicks;
                        RPLog.Message($"[RimPrison:evac] {p.LabelShort} has had no way to a safe place for two hours: listed missing, kept where they are and tried again quietly");
                    }
                    // (Someone has them in their arms - the rescue taking a downed one to a bed outside the wing: nothing is tried until they are put down.)
                    else if (now >= e.retryTick && p.CarriedBy == null)
                    {
                        e.retryTick = now + RetryTicks;
                        s.Enqueue(e);
                    }
                    continue;
                }
                if (e.status == EvacStatus.Missing)
                {
                    // (Missing for want of a way: still the evacuation's - kept from the regime, the supermax walks and vanilla's carry to a bed, waiting where they are, a step away from a fire that comes beside them - and tried again
                    // quietly every so often: a way found takes them out after all. They hold nothing up.)
                    if (now >= e.retryTick && p.CarriedBy == null)
                    {
                        e.retryTick = now + RetryTicks;
                        s.Enqueue(e);
                    }
                    continue;
                }
                // The way ahead burns, or a door on it was locked, or the place has caught: planned again, nobody walks into fire.
                if (e.status is EvacStatus.Walking or EvacStatus.Escorted or EvacStatus.WaitingEscort && RouteInvalid(pm, e, p, mask)) Replan(pm, e);
            }
            if (!back)
            {
                // (Not while the wing burns, a drill's either: a drill whose wing has caught waits at the points for the warden's word (the letter) or the fire's end, nobody is sent home into it.)
                bool clear = !burning && (!s.drill ? now - s.lastFireTick >= ClearAfterTicks : DrillDone(s, now));
                if (clear) AllClear(map, byWarden: false);
                return;
            }
            // (Over when nobody is on the way: one with no way back holds it only until they have been tried again (a door the all-clear opened a moment late), not to the cap - unless their own cell still burns (a cell outside the
            // wing: a fire in the wing turns everyone out again), when they wait where they are until it is out, up to the cap. Missing, rescued and stayed never hold it.)
            bool allHome = !s.entries.Exists(e => e.InProgress || e.status == EvacStatus.Blocked && (now - e.sinceTick < BackBlockedHoldTicks || HomeBurns(map, e)));
            if (allHome || now - s.backTick >= BackCapTicks) Finish(map, pm, s, capped: !allHome);
        }

        /// <summary>The room of the inmate's own bed has a fire in it.</summary>
        private static bool HomeBurns(Map map, EvacEntry e) => Resolve(map, e)?.ownership?.OwnedBed is { Spawned: true } bed && FireInRoom(map, bed.GetRoom());

        /// <summary>
        /// A fire in the wing after the all-clear (one that caught again, or a corner that was never out): the way home is called off and the wing is emptied again. Everyone on the way back, waiting for a guard, with no way, staying
        /// for want of a bed or already home is given a place out again; a carry to a bed in the wing is put down. No new letter: the evacuation runs already (and a drill's was sent at its all-clear - a drill whose wing caught on
        /// the way back is a real evacuation from then on, its all-clear an hour after the last fire).
        /// </summary>
        private static void BackOut(Map map, PrisonMap pm, EvacuationState s, int now)
        {
            bool wasDrill = s.drill;
            s.phase = EvacPhase.Out;
            s.drill = false;
            s.backTick = -1;
            s.lastFireTick = now;
            var again = new List<EvacEntry>();
            foreach (EvacEntry e in s.entries)
            {
                if (!e.Moving && e.status != EvacStatus.Home) continue;
                Pawn p = Resolve(map, e);
                // (Vanilla's carry to their bed (given by the evacuation's own giver after the all-clear, or the rescue's to their own bed) goes into the wing: put down, and the evacuation's carry takes them out.)
                if (p != null && (p.CarriedBy ?? GuardOf(map, e)) is { } holder && holder.CurJob is { } held && held.def == JobDefOf.TakeWoundedPrisonerToBed && held.targetA.Thing == p
                    && held.targetB.Thing is Building_Bed toBed && WingAt(pm, toBed.Position) == s.wing)
                    holder.jobs.EndCurrentJob(JobCondition.InterruptForced);
                EndGuardWalk(e, p);
                if (p?.Inmate() is { } c)
                {
                    ClearPass(p, c);
                    if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
                    e.escort = NeedsGuard(p, c);
                }
                e.point = -1;
                e.dest = IntVec3.Invalid;
                e.route = null;
                e.fallback = false;
                e.retryTick = 0;
                e.escortId = 0;
                SetStatus(e, EvacStatus.Planning, now);
                again.Add(e);
            }
            // (Those who are never with anyone else first, as at the start: the cage is theirs.)
            foreach (EvacEntry e in again.OrderBy(e => e.pawn?.Inmate()?.security is { escortOnly: true } ? 0 : 1)) s.Enqueue(e);
            RPLog.Message($"[RimPrison:evac] a fire in the {s.wing.label} after the all-clear{(wasDrill ? " (the drill's)" : "")}: {again.Count} taken out again at {now}");
            Messages.Message((wasDrill ? "RP_EvacBackOutDrill" : "RP_EvacBackOut").Translate(s.wing.label, again.Count), MessageTypeDefOf.NegativeEvent, historical: false);
        }

        private static bool DrillDone(EvacuationState s, int now) => !s.entries.Exists(e => e.InProgress) && now - s.startTick >= GenDate.TicksPerHour / 4 || now - s.startTick >= DrillCapTicks;

        private static void SetStatus(EvacEntry e, EvacStatus status, int now)
        {
            if (e.status == status) return;
            // (For the checks: every change of a line, where and on what they were - round 2's runs ended with a status nobody could account for.)
            if (AutoTest.Active)
            {
                Pawn p = e.pawn;
                RPLog.Message($"[RimPrison:evac] {e.name}: {e.status} -> {status} at {(p == null ? "?" : p.Spawned ? p.Position.ToString() : p.CarriedBy != null ? "carried by " + p.CarriedBy.LabelShort : "off the map")} on {p?.CurJobDef?.defName ?? "nothing"} (tick {now})");
            }
            e.status = status;
            e.sinceTick = now;
            if (status == EvacStatus.Blocked) e.blockedSeen = true;
        }

        /// <summary>
        /// For the checks: a listed evacuee lying down anywhere but their place (in a bed, or on a lie-down job), once per bed - on what job, whose driver and giver, and whose bed it is, where. Round 4's run ended with the downed one on
        /// LayDown at a cell the log never named; the carry that tucks someone in is logged by the test patch on vanilla's tuck-in (<see cref="Patch_RestUtility_TuckIntoBed_LogEvacuee"/>).
        /// </summary>
        private static void LogLying(PrisonMap pm, EvacuationState s, EvacEntry e, Pawn p, bool back)
        {
            Building_Bed bed = p.Spawned ? p.CurrentBed() : null;
            JobDef job = p.CurJobDef;
            bool lying = bed != null || p.Spawned && (job == JobDefOf.LayDown || job == JobDefOf.LayDownResting || job == JobDefOf.LayDownAwake);
            if (!lying || !back && e.dest.IsValid && AtGoal(pm, p, e, back: false))
            {
                if (!lying) e.lyingLogged = 0;
                return;
            }
            int key = bed?.thingIDNumber ?? -1 - pm.map.cellIndices.CellToIndex(p.Position);
            if (key == e.lyingLogged) return;
            e.lyingLogged = key;
            RPLog.Message($"[RimPrison:evac] {p.LabelShort} ({e.status}, {s.phase}) lies down at {p.Position}, not at their place {(e.dest.IsValid ? e.dest.ToString() : "(none)")}: {job?.defName ?? "no job"}, driver {p.jobs?.curDriver?.GetType().Name ?? "none"}, from {p.CurJob?.workGiverDef?.defName ?? p.CurJob?.jobGiver?.GetType().Name ?? "no giver"}; {BedWords(pm, s, p, bed)}");
        }

        /// <summary>For the checks' log: whose bed it is (their own, a medical one, another's, a free one), in the wing being emptied or not, in their own cell or not, a fire in its room or not.</summary>
        internal static string BedWords(PrisonMap pm, EvacuationState s, Pawn p, Building_Bed bed)
        {
            if (bed == null) return "in no bed";
            Pawn owner = bed.OwnersForReading.FirstOrDefault();
            string whose = bed.OwnersForReading.Contains(p) ? "their own bed" : bed.Medical ? "a medical bed" : owner != null ? owner.LabelShort + "'s bed" : "a free bed";
            bool inWing = s.wing != null && WingAt(pm, bed.Position) == s.wing;
            bool ownCell = bed.GetRoom() is { } room && room == InmateUtility.CellOf(p);
            return $"{whose} at {bed.Position}, {(inWing ? "in" : "outside")} the wing, {(ownCell ? "in" : "not in")} their own cell{(FireInRoom(pm.map, bed.GetRoom()) ? ", a fire in its room" : "")}";
        }

        /// <summary>Listed missing; their pass and walk go (one who left the map mid-walk would come back with doors that open for them), and no place is held for them.</summary>
        private static void SetMissing(EvacEntry e, Pawn p, bool offMap, int now)
        {
            if (p?.Inmate() is { } c)
            {
                ClearPass(p, c);
                if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
            }
            e.route = null;
            e.dest = IntVec3.Invalid;
            e.point = -1;
            e.fallback = false;
            e.missingOffMap = offMap;
            SetStatus(e, EvacStatus.Missing, now);
        }

        private static void Drop(Map map, EvacEntry e, Pawn p, CompInmate c, int now)
        {
            if (c != null) c.evacDoors = null;
            if (c?.leg is { kind: EscortKind.Evacuation } && p != null) EscortWalk.End(p, c);
            EndGuardWalk(e, p);
            SetStatus(e, EvacStatus.Dropped, now);
            if (p is { Spawned: true }) map.reachability.ClearCacheFor(p);
        }

        private static bool GuardHas(Map map, EvacEntry e, Pawn inmate)
        {
            Pawn guard = GuardOf(map, e);
            if (guard == null || guard.Dead || guard.Downed) return false;
            return guard.CurJob is { } job && job.targetA.Thing == inmate && (job.def == RPEvacDefOf.RP_EvacuateInmate || job.def == RPEvacDefOf.RP_CarryEvacuee || job.def == JobDefOf.TakeWoundedPrisonerToBed);
        }

        private static Pawn GuardOf(Map map, EvacEntry e)
        {
            if (e.escortId == 0) return null;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (colonists[i].thingIDNumber == e.escortId) return colonists[i];
            return null;
        }

        private static void EndGuardWalk(EvacEntry e, Pawn inmate)
        {
            if (e.escortId == 0 || inmate?.MapHeld == null) return;
            Pawn guard = GuardOf(inmate.MapHeld, e);
            e.escortId = 0;
            if (guard?.CurJob is { } job && job.targetA.Thing == inmate && (job.def == RPEvacDefOf.RP_EvacuateInmate || job.def == RPEvacDefOf.RP_CarryEvacuee)) guard.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>
        /// The route they are on is no longer safe to go on with: the place is on fire (or beside it), the point was repainted away or caught fire, a cell still ahead of them burns or is beside a fire, or a door still ahead was
        /// locked or taken by rioters since. The cells they have walked do not count: the route begins in the wing that burns, and a fire spreading behind them is no reason to stop them.
        /// </summary>
        internal static bool RouteInvalid(PrisonMap pm, EvacEntry e, Pawn p, HashSet<int> fireMask)
        {
            if (e.dest.IsValid)
            {
                if (fireMask != null && fireMask.Contains(pm.map.cellIndices.CellToIndex(e.dest))) return true;
                if (PlaceGone(pm, e)) return true;
            }
            if (e.route == null) return false;
            if (p != null)
            {
                int here = pm.map.cellIndices.CellToIndex(p.PositionHeld);
                if (e.route.steps.TryGetValue(here, out int step) && step > e.routeStep) e.routeStep = step;
            }
            if (fireMask != null && fireMask.Count > 0)
                foreach (KeyValuePair<int, int> cell in e.route.steps)
                    if (cell.Value > e.routeStep && fireMask.Contains(cell.Key)) return true;
            foreach ((Building_Door door, int step) in e.route.doorSteps)
                if (step > e.routeStep && !EvacRoutes.DoorStillUsable(door)) return true;
            return false;
        }

        /// <summary>The place they were given is gone (the point repainted away) or has a fire in its own cells.</summary>
        private static bool PlaceGone(PrisonMap pm, EvacEntry e)
        {
            int patch;
            if (e.point >= 0 && !e.fallback && e.point < pm.evac.points.Count) patch = PatchOf(pm, pm.evac.points[e.point]);
            else if (e.fallback) patch = pm.zones.PatchAt(e.dest);
            else return false;
            return patch < 0 || FireInPatch(pm, patch);
        }

        /// <summary>Arrived, and the place is no longer safe: the place gone or on fire, their cell or the one they were given beside a fire.</summary>
        private static bool ArrivedUnsafe(PrisonMap pm, EvacEntry e, Pawn p, HashSet<int> fireMask)
        {
            if (fireMask != null && (e.dest.IsValid && fireMask.Contains(pm.map.cellIndices.CellToIndex(e.dest)) || p.Spawned && fireMask.Contains(pm.map.cellIndices.CellToIndex(p.Position)))) return true;
            return e.dest.IsValid && PlaceGone(pm, e);
        }

        /// <summary>
        /// Plans the inmate's way again (their job is cut once, and they start on the new one). One a guard has in their arms on the evacuation's carry stays there: the carry goes on to the new place once it is found (see
        /// <see cref="Plan"/>), and is put down only when there is none - the fire ahead is why they are planned again, and where the guard stands is the worst place to leave them.
        /// </summary>
        public static void Replan(PrisonMap pm, EvacEntry e)
        {
            EvacuationState s = pm.evac;
            Pawn p = Resolve(pm.map, e);
            bool carried = e.status == EvacStatus.Escorted && CarryOf(e, p) != null;
            if (e.status == EvacStatus.Escorted && !carried) EndGuardWalk(e, p);
            if (p?.Inmate() is { } c)
            {
                ClearPass(p, c);
                if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
            }
            e.route = null;
            int now = Find.TickManager.TicksGame;
            if (e.status is EvacStatus.Escorted && !carried) SetStatus(e, EvacStatus.WaitingEscort, now);
            else if (e.status == EvacStatus.Arrived) SetStatus(e, EvacStatus.Planning, now);
            e.retryTick = 0;
            s.Enqueue(e);
        }

        /// <summary>The evacuation's carry that has this inmate in the guard's arms (the guard on the line), or null.</summary>
        private static Job CarryOf(EvacEntry e, Pawn p) =>
            p?.CarriedBy is { } carrier && carrier.thingIDNumber == e.escortId && carrier.CurJob is { } job && job.def == RPEvacDefOf.RP_CarryEvacuee && job.targetA.Thing == p ? job : null;

        /// <summary>
        /// Where a carry that ends short of its place puts the inmate down: where the carrier stands, or when that is in or beside a fire, the nearest cell of the same room a step or two away that is not (main thread; the carry's
        /// end, see <see cref="JobDriver_CarryEvacuee"/>).
        /// </summary>
        public static IntVec3 SafeDropCell(Pawn carrier)
        {
            Map map = carrier.Map;
            IntVec3 at = carrier.Position;
            if (map == null || map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0) return at;
            HashSet<int> mask = EvacRoutes.FireMask(map);
            if (!mask.Contains(map.cellIndices.CellToIndex(at))) return at;
            Room room = at.GetRoom(map);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(at, 2.9f, false))
                if (c.InBounds(map) && !mask.Contains(map.cellIndices.CellToIndex(c)) && c.Standable(map) && c.GetRoom(map) == room) return c;
            return at;
        }

        public static Pawn Resolve(Map map, EvacEntry e)
        {
            if (e.pawn != null && !e.pawn.Destroyed && (e.pawn.Map == map || e.pawn.MapHeld == map)) return e.pawn;
            e.pawn = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (p.thingIDNumber == e.id)
                {
                    e.pawn = p;
                    break;
                }
            return e.pawn;
        }

        // ---- planning an inmate's way -------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Gives the inmate a place and a way: out, the assembly point that takes their level (the second for those kept apart, when there is one) with no fire in it, a cell of it nobody else has and no fire beside; none that
        /// can be reached, the nearest safe room they may enter (the fallback, listed; supermax only a cage of its own, alone); none at all, blocked. Back, the cell of their own bed (a destroyed bed: the wing's free bed, or they
        /// stay and are listed; a room still on fire: not yet). A walker who has no way of their own (another wing, a staff corridor) waits for a guard instead. A walker gets the pass for the doors on the way.
        /// </summary>
        private static void Plan(Map map, PrisonMap pm, EvacuationState s, EvacEntry e)
        {
            int now = Find.TickManager.TicksGame;
            Pawn p = Resolve(map, e);
            if (p == null || p.Inmate() is not { } c || p.CarriedBy == null && !p.Spawned)
            {
                SetMissing(e, p, offMap: true, now);
                return;
            }
            bool back = s.phase == EvacPhase.Back;
            IntVec3 dest = IntVec3.Invalid;
            EvacRoutes.Route route = null;
            int point = -1;
            bool fallback = false, cellBurns = false;
            IntVec3 from = p.PositionHeld;
            // (One who waited for a guard only because they were down, and is up again, walks by themselves again; a walker with no way of their own is turned to a guard's walk again below. Not one a guard has now.)
            bool wasEscort = e.escort;
            if (e.escort && e.status != EvacStatus.Escorted && p.Spawned && !NeedsGuard(p, c)) e.escort = false;
            if (back)
            {
                if (EscortWalk.InOwnCell(p))
                {
                    Arrive(map, e, p, c, true, now);
                    return;
                }
                // (Lying in a medical bed outside the wing (carried there since the all-clear): the doctors' patient, accounted for there, home when well.)
                if (InRescueBed(pm, p, s.wing))
                {
                    SetStatus(e, EvacStatus.Rescued, now);
                    ClearPass(p, c);
                    return;
                }
                dest = EscortWalk.HomeCell(p);
                if (!dest.IsValid)
                {
                    // (Their bed is gone: the bed they are given, or none, and they stay at the point.)
                    InmateUtility.TryReassignBed(p, c);
                    dest = EscortWalk.HomeCell(p);
                }
                if (!dest.IsValid)
                {
                    SetStatus(e, EvacStatus.Stayed, now);
                    ClearPass(p, c);
                    return;
                }
                // (Not into a cell that still burns, though the way to it is clear: a cell outside the wing (a fire in the wing turns everyone out again, see BackOut). They wait where they are until it is out.)
                cellBurns = FireInRoom(map, dest.GetRoom(map));
                if (!cellBurns)
                {
                    route = EvacRoutes.Plan(map, from, dest, e.escort);
                    if (route == null && !e.escort && (route = EvacRoutes.Plan(map, from, dest, escorted: true)) != null) TurnToEscort(e, p, c, back, wasEscort);
                }
            }
            else
            {
                route = ChooseOut(map, pm, s, e, p, c, from, e.escort, out dest, out point, out fallback);
                if (route == null && !e.escort && (route = ChooseOut(map, pm, s, e, p, c, from, true, out dest, out point, out fallback)) != null) TurnToEscort(e, p, c, back, wasEscort);
            }
            if (route == null)
            {
                // (A guard who has them in their arms puts them down: there is nowhere to take them. Not where a fire is, see SafeDropCell.)
                if (e.status == EvacStatus.Escorted) EndGuardWalk(e, p);
                e.escort = wasEscort;
                e.route = null;
                e.dest = IntVec3.Invalid;
                e.point = -1;
                e.fallback = false;
                ClearPass(p, c);
                e.retryTick = now + RetryTicks;
                // (Missing for want of a way stays missing, and quietly: tried again every so often, nothing said until a way is found.)
                if (e.status == EvacStatus.Missing) return;
                if (e.status != EvacStatus.Blocked) RPLog.Message($"[RimPrison:evac] no route for {p.LabelShort} (level {c.security?.defName}, at {from}, {(back ? cellBurns ? "back to the bed - their cell still burns" : "back to the bed" : "to a safe place")}): route blocked");
                SetStatus(e, EvacStatus.Blocked, now);
                return;
            }
            if (e.status == EvacStatus.Missing) RPLog.Message($"[RimPrison:evac] {p.LabelShort}, missing for want of a way, has a way {(back ? "back" : "out")} now (to {dest}): {(e.escort ? "waits for a guard" : "walks")}");
            if (fallback && !e.fallback) RPLog.Message($"[RimPrison:evac] {p.LabelShort} goes to the nearest safe room at {dest}: no assembly point could be reached for level {c.security?.defName} (fallback)");
            bool changed = e.dest != dest || e.status is EvacStatus.Blocked or EvacStatus.Planning or EvacStatus.Missing;
            e.dest = dest;
            e.point = point;
            e.fallback = fallback;
            if (!back && p.CarriedBy == null && p.Spawned && p.Position == dest)
            {
                // (Already where they are to be (one who went down at the point, one moved on to a cell of the patch they stand in): arrived, and nobody carries them from their cell to the same cell.)
                e.route = null;
                ClearPass(p, c);
                Arrive(map, e, p, c, false, now);
                return;
            }
            e.route = route;
            e.routeStep = 0;
            e.planTick = now;
            if (e.escort)
            {
                ClearPass(p, c);
                if (e.status != EvacStatus.Escorted) SetStatus(e, EvacStatus.WaitingEscort, now);
                // (In a guard's arms on the evacuation's carry, planned again because the way ahead burns: the carry goes on to the new place, see JobDriver_CarryEvacuee.)
                else if (CarryOf(e, p) is { } carry && carry.targetB.Cell != dest)
                {
                    carry.targetB = dest;
                    RPLog.Message($"[RimPrison:evac] {p.LabelShort} is carried on to {dest} instead (the way ahead burns)");
                }
            }
            else
            {
                c.evacDoors = route.doors;
                SetStatus(e, EvacStatus.Walking, now);
            }
            if (p.Spawned) map.reachability.ClearCacheFor(p);
            // (Whatever they were doing is over, once: the regime's errand would take them elsewhere.)
            if (changed && p.Spawned) InmateUtility.InterruptForRegimeChange(p, emergency: true);
        }

        /// <summary>A walker with no way they may walk alone (another level's wing or a staff corridor between them and every safe place): a guard walks them instead (said once, not on every plan).</summary>
        private static void TurnToEscort(EvacEntry e, Pawn p, CompInmate c, bool back, bool wasEscort)
        {
            e.escort = true;
            ClearPass(p, c);
            if (!wasEscort) RPLog.Message($"[RimPrison:evac] {p.LabelShort} has no way to walk {(back ? "back" : "out")} alone: waits for a guard");
        }

        private static EvacRoutes.Route ChooseOut(Map map, PrisonMap pm, EvacuationState s, EvacEntry e, Pawn p, CompInmate c, IntVec3 from, bool escorted, out IntVec3 dest, out int point, out bool fallback)
        {
            (dest, point, fallback) = (IntVec3.Invalid, -1, false);
            HashSet<int> used = UsedCells(map, s, e);
            // (Supermax is never with anyone else: a cage of its own, and one of them in it at a time, and nobody else in it with them - a cage the warden ticked other levels on included.)
            bool alone = c.security is { escortOnly: true };
            HashSet<int> barred = BarredPatches(map, pm, s, e, alone);
            var order = new List<int>(EvacuationState.MaxPoints);
            for (int i = 0; i < s.points.Count; i++)
            {
                int found = s.points[i].TakesAlone(c.security) ? PatchOf(pm, s.points[i]) : -1;
                if (found >= 0 && !FireInPatch(pm, found) && !barred.Contains(found)) order.Add(i);
            }
            // (The second point first for those kept apart; the first for everyone else. The other is the next choice: a burning or unreachable point is not the end of it.)
            if (e.second) order.Reverse();
            foreach (int i in order)
            {
                int patch = PatchOf(pm, s.points[i]);
                IntVec3 cell = PickCell(map, pm, patch, p, used);
                if (!cell.IsValid) continue;
                EvacRoutes.Route route = EvacRoutes.Plan(map, from, cell, escorted);
                if (route == null) continue;
                (dest, point) = (cell, i);
                return route;
            }
            // The survival fallback: the nearest safe room of the prison they may enter (their own wing's common room, the yard; supermax only a cage of its own), a place nobody is sent into fire for.
            var rooms = new List<(int patch, long dist)>();
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
            {
                if (!FallbackZone(patches[i].zone, c.security) || !pm.zones.Allows(i, c.security) || barred.Contains(i) || FireInPatch(pm, i)) continue;
                IntVec3 center = patches[i].center.ToIntVec3();
                rooms.Add((i, (center - from).LengthHorizontalSquared));
            }
            rooms.Sort((a, b) => a.dist.CompareTo(b.dist));
            for (int k = 0; k < rooms.Count && k < MaxFallbackTries; k++)
            {
                IntVec3 cell = PickCell(map, pm, rooms[k].patch, p, used);
                if (!cell.IsValid) continue;
                EvacRoutes.Route route = EvacRoutes.Plan(map, from, cell, escorted);
                if (route == null) continue;
                (dest, point, fallback) = (cell, -1, true);
                return route;
            }
            return null;
        }

        /// <summary>A room to fall back to: the yard or a common room for most, only a yard of their own for a level that is never with anyone else (supermax, which is never left in the general population without a guard).</summary>
        private static bool FallbackZone(PrisonZoneDef z, SecurityLevelDef level)
        {
            if (z == null || !z.inmateAccess || z.staffOnly || z.security != null) return false;
            if (level is { escortOnly: true }) return z.yardFor == level;
            return z == RPDefOf.RP_Yard || z == RPDefOf.RP_CommonRoom || z.yardFor != null && z.yardFor == level;
        }

        /// <summary>
        /// The patches this inmate may not be sent to because of who else is going there (or is there): for one who is never with anyone else (<paramref name="alone"/>), every patch another on the roll call has been given; for anyone
        /// else, the patches one who is never with anyone else has been given. One pass over the roll for the whole choice.
        /// </summary>
        private static HashSet<int> BarredPatches(Map map, PrisonMap pm, EvacuationState s, EvacEntry except, bool alone)
        {
            var barred = new HashSet<int>();
            foreach (EvacEntry o in s.entries)
            {
                if (o == except || !o.Moving || !o.dest.IsValid) continue;
                int patch = pm.zones.PatchAt(o.dest);
                if (patch < 0 || barred.Contains(patch)) continue;
                if (alone || (o.pawn ?? Resolve(map, o))?.Inmate()?.security is { escortOnly: true }) barred.Add(patch);
            }
            return barred;
        }

        private static HashSet<int> UsedCells(Map map, EvacuationState s, EvacEntry except)
        {
            var used = new HashSet<int>();
            foreach (EvacEntry o in s.entries)
                if (o != except && o.dest.IsValid && o.Moving) used.Add(map.cellIndices.CellToIndex(o.dest));
            return used;
        }

        /// <summary>A cell of the patch to go to: where they stand when they are in it already; else the first standable cell nobody else is going to, from a start that depends on who they are (so the places are spread). Never a cell in or beside fire.</summary>
        private static IntVec3 PickCell(Map map, PrisonMap pm, int patch, Pawn p, HashSet<int> used)
        {
            List<IntVec3> cells = pm.zones.CellsOfPatch(patch);
            int n = cells.Count;
            if (n == 0) return IntVec3.Invalid;
            HashSet<int> fires = EvacRoutes.FireMask(map);
            if (p.Spawned && pm.zones.PatchAt(p.Position) == patch && !fires.Contains(map.cellIndices.CellToIndex(p.Position)) && !used.Contains(map.cellIndices.CellToIndex(p.Position))) return p.Position;
            int start = (p.thingIDNumber & 0x7fffffff) % n;
            IntVec3 anyStandable = IntVec3.Invalid;
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = cells[(start + i) % n];
                int index = map.cellIndices.CellToIndex(c);
                if (fires.Contains(index) || !c.Standable(map) || c.GetFirstBuilding(map) != null) continue;
                if (!used.Contains(index)) return c;
                if (!anyStandable.IsValid) anyStandable = c;
            }
            return anyStandable;
        }

        // ---- the inmate's own side -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>At the place planned for them: out, the cell given or the patch it is in; back, their own cell (a bed elsewhere is not home: a medical bed outside the wing is the rescue's, see <see cref="InRescueBed"/>).</summary>
        private static bool AtGoal(PrisonMap pm, Pawn p, EvacEntry e, bool back)
        {
            if (back) return EscortWalk.InOwnCell(p);
            if (!e.dest.IsValid || e.status is EvacStatus.Planning or EvacStatus.Blocked or EvacStatus.Missing) return false;
            return InPlace(pm, e.dest, p.Position);
        }

        /// <summary>A cell of the place out whose given cell is <paramref name="dest"/>: that cell, or another of the patch it is in (where the evacuation's carry may lay one down, see <see cref="JobDriver_CarryEvacuee"/>).</summary>
        public static bool InPlace(PrisonMap pm, IntVec3 dest, IntVec3 c)
        {
            if (c == dest) return true;
            int patch = pm.zones.PatchAt(dest);
            return patch >= 0 && pm.zones.PatchAt(c) == patch;
        }

        private static void Arrive(Map map, EvacEntry e, Pawn p, CompInmate c, bool back, int now)
        {
            c.evacDoors = null;
            e.route = null;
            if (back)
            {
                EndGuardWalk(e, p);
                if (c.leg is { kind: EscortKind.Evacuation }) EscortWalk.End(p, c);
                SetStatus(e, EvacStatus.Home, now);
            }
            else
            {
                if (e.status != EvacStatus.Arrived) e.arrivedTick = now;
                SetStatus(e, EvacStatus.Arrived, now);
                if (PrisonMap.Of(map)?.evac is { drill: true } && !e.memoryGiven)
                {
                    e.memoryGiven = true;
                    p.needs?.mood?.thoughts.memories.TryGainMemory(RPEvacDefOf.RP_EvacuationDrill);
                }
            }
            if (p.Spawned) map.reachability.ClearCacheFor(p);
        }

        private static readonly JobGiver_InmateUrgentNeeds urgentNeeds = new JobGiver_InmateUrgentNeeds();

        /// <summary>
        /// The inmate's own job during an evacuation (null: nothing of the evacuation's, the regime goes on): a walker goes to the place on their route (and wanders in it once there, eating, using a toilet or lying down where that
        /// can be had outside the wing, or home to their bed after the all-clear); someone waiting for a guard stays where they are (a step away from a fire that reaches them); one a guard is leading follows the walk
        /// (<see cref="EscortWalk.JobFor"/>); one who was outside the wing when it began stays out of it. Always a job that lasts: never a walk they are already at the end of.
        /// </summary>
        public static Job JobFor(Pawn pawn, CompInmate c, PrisonMap pm)
        {
            EvacuationState s = pm?.evac;
            if (s == null || !s.Active || s.Get(pawn.thingIDNumber) is not { } e) return null;
            if (pawn.InAggroMentalState || pawn.MentalState is MentalState_Escaping) return null;
            if (e.status == EvacStatus.Outside) return OutsideJob(pawn, c, pm, s);
            if (!e.Moving) return null;
            bool back = s.phase == EvacPhase.Back;
            int now = Find.TickManager.TicksGame;
            if (c.leg is { kind: EscortKind.Evacuation }) return EscortWalk.JobFor(pawn, c, pm) ?? StayJob(pawn, 120);
            if (e.status == EvacStatus.Arrived && !back && AtGoal(pm, pawn, e, false))
                return SafeErrand(pawn, pm, s) ?? WanderSafe(pawn, pm, e.dest) ?? StayJob(pawn, Rand.Range(250, 600));
            if (e.status is EvacStatus.Planning or EvacStatus.Blocked or EvacStatus.Missing || e.status == EvacStatus.Walking && e.route == null)
            {
                if (e.status == EvacStatus.Walking) s.Enqueue(e);
                return AwayFromFire(pawn) ?? StayJob(pawn, 120);
            }
            if (e.escort || e.status is EvacStatus.WaitingEscort or EvacStatus.Escorted or EvacStatus.Stayed) return AwayFromFire(pawn) ?? StayJob(pawn, 250);
            if (AtGoal(pm, pawn, e, back))
            {
                if (e.status != EvacStatus.Arrived || back) Arrive(pawn.Map, e, pawn, c, back, now);
                if (back) return null;
                return WanderSafe(pawn, pm, e.dest) ?? StayJob(pawn, Rand.Range(250, 600));
            }
            if (!pawn.CanReach(e.dest, PathEndMode.OnCell, Danger.Deadly))
            {
                // (Just planned and already no way with the pass on: the planner and the doors disagree (a door it could not know of), and planning the same route again would loop; listed blocked and tried later.)
                if (now - e.planTick <= BeatTicks) Notify_NoWay(pawn);
                else Replan(pm, e);
                return StayJob(pawn, 120);
            }
            Job go = JobMaker.MakeJob(JobDefOf.Goto, e.dest);
            go.locomotionUrgency = LocomotionUrgency.Jog;
            go.expiryInterval = 1200;
            return go;
        }

        /// <summary>Outside the wing when it began: their work, class or yard goes on as the regime has it, but an hour that would send them to their cell in the wing (lockup, sleep) is spent where they are.</summary>
        private static Job OutsideJob(Pawn pawn, CompInmate c, PrisonMap pm, EvacuationState s)
        {
            if (s.phase != EvacPhase.Out || s.drill || !pawn.Spawned || WingAt(pm, pawn.Position) == s.wing || PrisonGame.Instance is not { } game) return null;
            RegimeActivityDef act = game.ActivityAt(c, GenLocalDate.HourOfDay(pawn.Map));
            if (act == null || !act.lockup && act != RPDefOf.RP_Sleep) return null;
            return AwayFromFire(pawn) ?? StayJob(pawn, 600);
        }

        /// <summary>An urgent need met at the point (a meal, a toilet, a bed of the regime's choosing) when nothing of it is in the wing being emptied or beside a fire; null otherwise (they wait for the all-clear).</summary>
        private static Job SafeErrand(Pawn pawn, PrisonMap pm, EvacuationState s)
        {
            Job job = urgentNeeds.TryIssueJobPackage(pawn, default).Job;
            if (job == null) return null;
            HashSet<int> mask = pawn.Map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count > 0 ? EvacRoutes.FireMask(pawn.Map) : null;
            bool Safe(LocalTargetInfo t)
            {
                if (!t.IsValid) return true;
                IntVec3 cell = t.HasThing ? t.Thing.PositionHeld : t.Cell;
                if (!cell.IsValid || !cell.InBounds(pawn.Map)) return true;
                return WingAt(pm, cell) != s.wing && (mask == null || !mask.Contains(pawn.Map.cellIndices.CellToIndex(cell)));
            }
            return Safe(job.targetA) && Safe(job.targetB) ? job : null;
        }

        /// <summary>A wander about the place they were sent to, never onto a cell in or beside a fire; null when they are not in it.</summary>
        private static Job WanderSafe(Pawn pawn, PrisonMap pm, IntVec3 anchor)
        {
            int patch = pm.zones.PatchAt(anchor);
            if (patch < 0 || pm.zones.PatchAt(pawn.Position) != patch) return null;
            if (pawn.mindState.nextMoveOrderIsWait)
            {
                pawn.mindState.nextMoveOrderIsWait = false;
                return StayJob(pawn, Rand.Range(250, 900));
            }
            pawn.mindState.nextMoveOrderIsWait = true;
            HashSet<int> mask = pawn.Map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count > 0 ? EvacRoutes.FireMask(pawn.Map) : null;
            IntVec3 dest = RCellFinder.RandomWanderDestFor(pawn, pawn.Position, 8f, (p, c, root) => pm.zones.PatchAt(c) == patch && (mask == null || !mask.Contains(p.Map.cellIndices.CellToIndex(c))), Danger.Deadly);
            return dest.IsValid && dest != pawn.Position ? JobMaker.MakeJob(JobDefOf.GotoWander, dest) : StayJob(pawn, Rand.Range(250, 900));
        }

        /// <summary>One who waits (for a guard, for a way) where a fire has come beside them: a few steps away from it within the room they are in; null when they are not beside one, or no such cell can be had.</summary>
        private static Job AwayFromFire(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Downed || pawn.Map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0) return null;
            Map map = pawn.Map;
            HashSet<int> mask = EvacRoutes.FireMask(map);
            if (!mask.Contains(map.cellIndices.CellToIndex(pawn.Position))) return null;
            Room room = pawn.GetRoom();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(pawn.Position, 4.9f, false))
            {
                if (!c.InBounds(map) || mask.Contains(map.cellIndices.CellToIndex(c)) || !c.Standable(map) || c.GetRoom(map) != room) continue;
                if (!pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
                Job step = JobMaker.MakeJob(JobDefOf.Goto, c);
                step.locomotionUrgency = LocomotionUrgency.Jog;
                return step;
            }
            return null;
        }

        private static Job StayJob(Pawn pawn, int ticks)
        {
            Job job = JobMaker.MakeJob(JobDefOf.Wait);
            job.expiryInterval = ticks;
            return job;
        }

        // ---- the guards' side ------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Whether this inmate is waiting for a guard (or a carrier) with a place and a way planned for them (see <see cref="WorkGiver_EvacuateInmate"/>).</summary>
        public static bool NeedsGuard(Pawn p) => EntryOf(p) is { status: EvacStatus.WaitingEscort, escort: true } e && e.dest.IsValid && p.Spawned && Find.TickManager.TicksGame >= e.retryTick;

        /// <summary>A guard (or doctor) has taken the inmate: they are the one, and the roll call says so.</summary>
        public static void Claim(Pawn inmate, Pawn guard)
        {
            if (EntryOf(inmate) is not { } e) return;
            e.escortId = guard.thingIDNumber;
            SetStatus(e, EvacStatus.Escorted, Find.TickManager.TicksGame);
        }

        /// <summary>
        /// The guard's walk or carry ended. One that did not get there is not offered again for a few seconds, whoever ended it and at whichever toil (a way that fails at once would be taken and dropped by the guards every
        /// tick); if the inmate is not at their place, they are waiting for the next guard.
        /// </summary>
        public static void Notify_GuardDone(Pawn inmate, Pawn guard, JobCondition how)
        {
            if (EntryOf(inmate) is not { } e) return;
            int now = Find.TickManager.TicksGame;
            if (how != JobCondition.Succeeded && e.status is EvacStatus.WaitingEscort or EvacStatus.Escorted) e.retryTick = now + RetryTicks / 2;
            if (e.escortId != guard.thingIDNumber) return;
            e.escortId = 0;
            if (e.status != EvacStatus.Escorted) return;
            PrisonMap pm = PrisonMap.Of(inmate.MapHeld);
            // (Delivered: where they stand is where they were taken.)
            if (pm != null && inmate.CarriedBy == null && inmate.Inmate() is { } c && AtGoal(pm, inmate, e, pm.evac.phase == EvacPhase.Back))
            {
                Arrive(inmate.MapHeld, e, inmate, c, pm.evac.phase == EvacPhase.Back, now);
                return;
            }
            e.route = null;
            SetStatus(e, EvacStatus.WaitingEscort, now);
            // (Planned again from where they stand now; a way that could not be made is told by its own driver.)
            pm?.evac.Enqueue(e);
        }

        /// <summary>The walk could not be made (no way for them with the pass on): the route is blocked, and they are listed.</summary>
        public static void Notify_NoWay(Pawn inmate)
        {
            if (EntryOf(inmate) is not { } e) return;
            e.route = null;
            if (inmate.Inmate() is { } c) ClearPass(inmate, c);
            e.retryTick = Find.TickManager.TicksGame + RetryTicks;
            SetStatus(e, EvacStatus.Blocked, Find.TickManager.TicksGame);
        }

        /// <summary>An evacuee broke (a riot, an assault, an escape): off the roll call at once, and their pass and walk with it, not on the next beat (the riot response takes them).</summary>
        public static void Notify_Broke(Pawn inmate)
        {
            if (!(inmate.InAggroMentalState || inmate.MentalState is MentalState_Escaping) || EntryOf(inmate) is not { Moving: true } e || inmate.MapHeld is not { } map) return;
            RPLog.Message($"[RimPrison:evac] {inmate.LabelShort} broke ({inmate.MentalStateDef?.defName}) during the evacuation: off the roll call, the riot response takes them");
            Drop(map, e, inmate, inmate.Inmate(), Find.TickManager.TicksGame);
        }

        /// <summary>The doors a guard's walk with this inmate opens: the ones on the route to <paramref name="to"/>, by the evacuation's rules (see <see cref="EvacRoutes"/>), none when there is no route.</summary>
        public static HashSet<Building_Door> EscortDoors(Pawn inmate, IntVec3 to)
        {
            EvacRoutes.Route route = inmate.Spawned ? EvacRoutes.Plan(inmate.Map, inmate.Position, to, escorted: true) : null;
            return route?.doors ?? new HashSet<Building_Door>();
        }

        // ---- the screens and the checks --------------------------------------------------------------------------------------------------------------------------

        /// <summary>Counts for the accountability list: arrived (and outside the wing: accounted for), carried, waiting for an escort, on the way, route blocked, missing, and those kept at a place for want of a bed.</summary>
        public sealed class Counts
        {
            public int arrived, outside, carried, waiting, onTheWay, blocked, missing, fallback, home, stayed, dropped, rescued;
            public int Total => arrived + outside + carried + waiting + onTheWay + blocked + missing + home + stayed + dropped + rescued;
        }

        public static Counts Tally(EvacuationState s)
        {
            var n = new Counts();
            foreach (EvacEntry e in s.entries)
            {
                switch (e.status)
                {
                    case EvacStatus.Arrived: n.arrived++; if (e.fallback) n.fallback++; break;
                    case EvacStatus.Outside: n.outside++; break;
                    case EvacStatus.Escorted: if (e.pawn?.CarriedBy != null) n.carried++; else n.onTheWay++; break;
                    case EvacStatus.WaitingEscort: n.waiting++; break;
                    case EvacStatus.Planning:
                    case EvacStatus.Walking: n.onTheWay++; break;
                    case EvacStatus.Blocked: n.blocked++; break;
                    case EvacStatus.Missing: n.missing++; break;
                    case EvacStatus.Home: n.home++; break;
                    case EvacStatus.Stayed: n.stayed++; break;
                    case EvacStatus.Dropped: n.dropped++; break;
                    case EvacStatus.Rescued: n.rescued++; break;
                }
            }
            return n;
        }

        /// <summary>The state on one line (the saveload plan compares it across a round trip): the points, and the evacuation with a line per inmate.</summary>
        public static string Describe(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            EvacuationState s = pm?.evac;
            if (s == null) return "";
            var sb = new StringBuilder();
            sb.Append(string.Join("+", s.points.Select(p => $"{p.zone?.defName}@{p.anchor}:{string.Join("/", p.levels.Select(l => l.defName))}")));
            sb.Append($"|{s.wing?.defName}:{s.phase}:{s.drill}:{s.startTick}:{s.lastFireTick}:{s.backTick}:{s.lastDrillTick}:{s.fireLetterTick}:{string.Join("/", s.wingLetterTicks.OrderBy(kv => kv.Key.defName).Select(kv => $"{kv.Key.defName}={kv.Value}"))}|");
            sb.Append(string.Join(",", s.entries.OrderBy(e => e.id).Select(e => $"{e.id}={e.name}/{e.status}/{e.point}/{e.second}/{e.fallback}/{e.escort}/{e.dest}/{e.escortId}/{e.arrivedTick}/{e.sinceTick}/{e.memoryGiven}/{e.blockedSeen}/{e.missingOffMap}")));
            return sb.ToString();
        }
    }

    /// <summary>The think-tree gate of an inmate the evacuation has a job for (being moved, or kept out of the wing it is emptying).</summary>
    public class ThinkNode_ConditionalEvacuee : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn) => PrisonGame.Instance != null && InmateUtility.IsActiveInmate(pawn) && EvacuationSystem.HasJobFor(pawn);
    }

    public class JobGiver_Evacuate : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn) => EvacuationSystem.JobFor(pawn, pawn.Inmate(), PrisonMap.Of(pawn.Map));
    }

    /// <summary>The letter of a fire that has reached a wing with people in it: evacuate the wing, or leave the fire to the staff.</summary>
    public class WingFireDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_WingFire_Evacuate", "RP_WingFire_Fight" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        private static PrisonZoneDef WingOf(ChoiceLetter_PrisonDecision l) => l.note.NullOrEmpty() ? null : DefDatabase<PrisonZoneDef>.GetNamedSilentFail(l.note);

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 0) return null;
            Map map = l.map ?? Find.AnyPlayerHomeMap;
            return EvacuationSystem.CannotStart(map, WingOf(l), drill: false);
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option == 0) EvacuationSystem.Start(map, WingOf(l), drill: false);
        }
    }
}
