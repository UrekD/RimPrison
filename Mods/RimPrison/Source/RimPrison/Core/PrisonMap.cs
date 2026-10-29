using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Per-map prison state: zone grid, fixture and door indexes, and the passes that run on schedule boundaries
    /// (door verdicts every 250 ticks, regime changes on the hour).
    /// </summary>
    public class PrisonMap : MapComponent
    {
        public ZoneGrid zones;
        /// <summary>The land the prison owns on this map: building and painting zones are allowed inside it only (see <see cref="PrisonGrounds"/>).</summary>
        public PrisonGrounds grounds;
        /// <summary>Which gang holds which patch of the yard, common room and canteen (see <see cref="GangTurf"/>).</summary>
        public TurfBook turf = new TurfBook();
        /// <summary>What each slot of the regime did yesterday (the Regime tab's strip); not saved: a reload loses a day.</summary>
        public RegimeFeedback regime = new RegimeFeedback();
        /// <summary>The care reserve (Deployment tab): guards kept for the care jobs while the rest answer the trouble. Size 0 (the default) changes nothing.</summary>
        public CareDetail care = new CareDetail();
        /// <summary>The assembly points the warden marked and the evacuation running now, if any (Deployment tab, see <see cref="EvacuationSystem"/>). None by default; a save from before has neither.</summary>
        public EvacuationState evac = new EvacuationState();
        private TensionOverlay tensionOverlay;
        private CoverageOverlay coverageOverlay;
        private TurfOverlay turfOverlay;
        private DangerOverlay dangerOverlay;
        private GroundsOverlay groundsOverlay;
        public CoverageOverlay Coverage => coverageOverlay ??= new CoverageOverlay(map);
        public TurfOverlay TurfShown => turfOverlay ??= new TurfOverlay(map);
        public DangerOverlay DangerShown => dangerOverlay ??= new DangerOverlay(map);
        /// <summary>The reform sessions running now, at most one per program (see <see cref="ProgramSystem"/>).</summary>
        public List<ProgramSession> sessions = new List<ProgramSession>();
        /// <summary>The classes the warden has booked hours for (Programs tab): tried before any other when their hour comes, see <see cref="ClassBookings"/>. A save from before has none.</summary>
        public ClassBookings bookings = new ClassBookings();
        /// <summary>The count: the one running, how the last ended, who it found missing, and the escapes the warden has not been told of (see <see cref="CountSystem"/>).</summary>
        public CountState count = new CountState();
        /// <summary>The single session of an old save; moved into <see cref="sessions"/> once loaded.</summary>
        public ProgramSession session;
        private readonly List<CompPrisonDoor> doors = new List<CompPrisonDoor>();
        private readonly List<CompFixture> fixtures = new List<CompFixture>();
        private readonly List<Pawn> guards = new List<Pawn>();
        private readonly List<CompWorkstation> workstations = new List<CompWorkstation>();

        public List<CompWorkstation> Workstations => workstations;

        private readonly List<CompSecurityCamera> cameras = new List<CompSecurityCamera>();
        private readonly List<Building_Bed> prisonerBeds = new List<Building_Bed>();

        /// <summary>Spawned prisoner beds (not medical), refreshed every 250 ticks.</summary>
        public List<Building_Bed> PrisonerBeds => prisonerBeds;

        /// <summary>The number each cell has been given, by the map cell index of each of its beds (see
        /// <see cref="Cellmates.Cells"/>). Numbers are handed out once and never reused, so a rebuilt bed or a renumbered
        /// block never makes an old "Cell 7" in the chronicle point somewhere else.</summary>
        public Dictionary<int, int> cellNumbers = new Dictionary<int, int>();
        public int nextCellNumber = 1;

        /// <summary>The stoves that have been given their standing meal bill (by thing id: a bill the warden then deletes is not made again), and the target each bill the prison made last had (by bill load id: one the warden edited is theirs, see <see cref="KitchenBills"/>).</summary>
        public List<int> billedStoves = new List<int>();
        public Dictionary<string, int> cookBills = new Dictionary<string, int>();
        /// <summary>Whether the stoves this map had when stoves began to be billed are remembered (a new game: yes; a save from before has the key missing, so false: see <see cref="KitchenBills.Ensure"/>).</summary>
        public bool stovesRemembered = true;

        /// <summary>Where contraband was found lately (the last <see cref="FindsDays"/> days, oldest first): the map drawn next to the coverage overlay.</summary>
        public List<ContrabandFind> finds = new List<ContrabandFind>();
        public const int FindsDays = 30, MaxFinds = 400;

        /// <summary>Where violence was seen lately (the last <see cref="DangerDays"/> days, oldest first): the danger map (see <see cref="DangerMap"/>).</summary>
        public List<IncidentMark> incidents = new List<IncidentMark>();
        public const int DangerDays = 30, MaxIncidents = 400;

        /// <summary>The smokeleaf patches gardener inmates have sown in the garden (hidden until found, see <see cref="GardenPatches"/>).</summary>
        public List<GardenPatch> patches = new List<GardenPatch>();

        /// <summary>The rumours on the wings: at most one live per wing, and the ones ended lately (at most <see cref="Rumours.MaxKept"/>, see <see cref="Rumours"/>).</summary>
        public List<Rumour> rumours = new List<Rumour>();

        /// <summary>The inmates asking to see the warden: at most one request each, each lapsing after a day (see <see cref="WardenAsks"/>).</summary>
        public List<WardenAsk> asks = new List<WardenAsk>();

        /// <summary>Marks a find on the map of finds, and forgets the ones that are too old (or too many).</summary>
        public void AddFind(IntVec3 cell, ContrabandKind kind, FindSource source)
        {
            int now = Find.TickManager.TicksGame;
            finds.Add(new ContrabandFind { cell = cell, kind = kind, source = source, tick = now });
            finds.RemoveAll(f => now - f.tick > FindsDays * GenDate.TicksPerDay);
            if (finds.Count > MaxFinds) finds.RemoveRange(0, finds.Count - MaxFinds);
        }

        /// <summary>Marks a seen incident on the danger map, and forgets the ones that are too old (or too many).</summary>
        public void AddIncident(IntVec3 cell, DangerKind kind)
        {
            int now = Find.TickManager.TicksGame;
            incidents.Add(new IncidentMark { cell = cell, kind = kind, tick = now });
            incidents.RemoveAll(m => now - m.tick > DangerDays * GenDate.TicksPerDay);
            if (incidents.Count > MaxIncidents) incidents.RemoveRange(0, incidents.Count - MaxIncidents);
        }

        /// <summary>The prisoner beds of each room, as of the last <see cref="RefreshBeds"/>: a door pass asks for a cell's beds once per cell door, and walking the room for them
        /// (<see cref="Room.ContainedBeds"/> rebuilds the room's whole thing list) was most of what it cost. A room the map has rebuilt since is not in it (asked live).</summary>
        private readonly Dictionary<Room, List<Building_Bed>> bedsByRoom = new Dictionary<Room, List<Building_Bed>>();
        private readonly List<List<Building_Bed>> bedListPool = new List<List<Building_Bed>>();
        /// <summary>The grade (0-5, see <see cref="CellQuality"/>) of each room that holds prisoner beds, worked out on the same walk as <see cref="bedsByRoom"/>: a fit is scored per bed per candidate, so it reads this, never the room.</summary>
        private readonly Dictionary<Room, int> gradeByRoom = new Dictionary<Room, int>();

        /// <summary>A cell room's grade as of the last <see cref="RefreshBeds"/>, or -1 for a room that walk has not seen (rebuilt since, or no prisoner bed in it).</summary>
        public int GradeOf(Room room) => room != null && gradeByRoom.TryGetValue(room, out int grade) ? grade : -1;

        /// <summary>The grade cache itself (for the checks, which set a grade the way the walk would and read it back).</summary>
        internal Dictionary<Room, int> CellGrades => gradeByRoom;
        private readonly List<Building_Bed> scratchBeds = new List<Building_Bed>();

        private void RefreshBeds()
        {
            bedsDirty = false;
            prisonerBeds.Clear();
            foreach (List<Building_Bed> used in bedsByRoom.Values)
            {
                used.Clear();
                bedListPool.Add(used);
            }
            bedsByRoom.Clear();
            gradeByRoom.Clear();
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is not Building_Bed { ForPrisoners: true, Medical: false } bed || !bed.def.building.bed_humanlike) continue;
                prisonerBeds.Add(bed);
                if (!bed.Spawned || bed.GetRoom() is not { } room) continue;
                if (!bedsByRoom.TryGetValue(room, out List<Building_Bed> inRoom))
                {
                    int last = bedListPool.Count - 1;
                    if (last >= 0)
                    {
                        inRoom = bedListPool[last];
                        bedListPool.RemoveAt(last);
                    }
                    else inRoom = new List<Building_Bed>();
                    bedsByRoom[room] = inRoom;
                }
                inRoom.Add(bed);
            }
            // (Once each room's beds are counted: a shared cell's grade is capped by how many it sleeps.)
            foreach (KeyValuePair<Room, List<Building_Bed>> kv in bedsByRoom)
            {
                int sleepers = 0;
                for (int i = 0; i < kv.Value.Count; i++) sleepers += kv.Value[i].SleepingSlotsCount;
                gradeByRoom[kv.Key] = CellQuality.CapForSleepers(CellQuality.GradeOf(kv.Key), sleepers);
            }
        }

        private bool anyInstigator;
        private int anyInstigatorTick = -99999;
        public const int InstigatorCheckTicks = 250;

        /// <summary>Whether any inmate is an instigator (as of up to <see cref="InstigatorCheckTicks"/> ticks ago): the hourly tension update walks a room's things to count them, which is pointless in a prison with none.</summary>
        public bool AnyInstigator
        {
            get
            {
                int now = Find.TickManager.TicksGame;
                if (now - anyInstigatorTick >= InstigatorCheckTicks || now < anyInstigatorTick)
                {
                    anyInstigatorTick = now;
                    anyInstigator = false;
                    IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
                    for (int i = 0; i < prisoners.Count && !anyInstigator; i++)
                        anyInstigator = prisoners[i].Inmate() is { } c && c.HasReputation(ReputationDefOf.RP_Instigator);
                }
                return anyInstigator;
            }
        }

        public List<CompSecurityCamera> Cameras => cameras;

        public void RegisterCamera(CompSecurityCamera c)
        {
            if (!cameras.Contains(c)) cameras.Add(c);
        }

        public void DeregisterCamera(CompSecurityCamera c) => cameras.Remove(c);

        private readonly List<CompGuardTower> towers = new List<CompGuardTower>();

        public List<CompGuardTower> Towers => towers;

        public void RegisterTower(CompGuardTower t)
        {
            if (!towers.Contains(t)) towers.Add(t);
        }

        public void DeregisterTower(CompGuardTower t) => towers.Remove(t);

        public void RegisterWorkstation(CompWorkstation w)
        {
            if (!workstations.Contains(w)) workstations.Add(w);
        }

        public void DeregisterWorkstation(CompWorkstation w) => workstations.Remove(w);

        private readonly List<CompBookShelf> shelves = new List<CompBookShelf>();

        public List<CompBookShelf> Shelves => shelves;

        public void RegisterShelf(CompBookShelf s)
        {
            if (!shelves.Contains(s)) shelves.Add(s);
        }

        public void DeregisterShelf(CompBookShelf s) => shelves.Remove(s);
        private int lastHour = -1;
        private bool doorsDirty = true;
        /// <summary>The whole pass is due now whatever the tick (the first tick of a new game or a load: nothing has been worked out yet).</summary>
        private bool firstPassDue = true;

        /// <summary>The door verdicts are due a fresh look at the next tick (for tests).</summary>
        internal bool DoorsDirty => doorsDirty;

        public List<CompFixture> Fixtures => fixtures;
        public List<CompPrisonDoor> Doors => doors;

        /// <summary>Spawned staff who count for suppression (guards first; any staff counts a little via role).</summary>
        public List<Pawn> Guards => guards;

        public PrisonMap(Map map) : base(map)
        {
            zones = new ZoneGrid(map);
            grounds = new PrisonGrounds(map);
        }

        // (Map.GetComponent walks the map's component list: loops asked for the same map's component hundreds of times a tick, so the last answer is kept.
        // Weakly: a map left behind by a quit to the menu must not be held on to.)
        private static System.WeakReference<PrisonMap> lastOf;

        public static PrisonMap Of(Map map)
        {
            if (map == null) return null;
            if (lastOf != null && lastOf.TryGetTarget(out PrisonMap cached) && cached.map == map) return cached;
            PrisonMap found = map.GetComponent<PrisonMap>();
            if (found != null)
            {
                if (lastOf == null) lastOf = new System.WeakReference<PrisonMap>(found);
                else lastOf.SetTarget(found);
            }
            return found;
        }

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref zones, "zones", map);
            Scribe_Deep.Look(ref grounds, "grounds", map);
            Scribe_Deep.Look(ref turf, "turf");
            Scribe_Deep.Look(ref count, "count");
            Scribe_Deep.Look(ref care, "careDetail");
            Scribe_Deep.Look(ref evac, "evacuation");
            Scribe_Deep.Look(ref session, "session");
            Scribe_Collections.Look(ref sessions, "sessions", LookMode.Deep);
            Scribe_Deep.Look(ref bookings, "classBookings");
            Scribe_Values.Look(ref lastHour, "lastHour", -1);
            Scribe_Collections.Look(ref cellNumbers, "cellNumbers", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref nextCellNumber, "nextCellNumber", 1);
            Scribe_Collections.Look(ref finds, "finds", LookMode.Deep);
            Scribe_Collections.Look(ref incidents, "incidents", LookMode.Deep);
            Scribe_Collections.Look(ref patches, "gardenPatches", LookMode.Deep);
            Scribe_Collections.Look(ref rumours, "rumours", LookMode.Deep);
            Scribe_Collections.Look(ref asks, "wardenAsks", LookMode.Deep);
            Scribe_Collections.Look(ref billedStoves, "billedStoves", LookMode.Value);
            Scribe_Collections.Look(ref cookBills, "cookBills", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref stovesRemembered, "stovesRemembered", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                billedStoves ??= new List<int>();
                cookBills ??= new Dictionary<string, int>();
                finds ??= new List<ContrabandFind>();
                finds.RemoveAll(f => f == null);
                incidents ??= new List<IncidentMark>();
                incidents.RemoveAll(m => m == null);
                patches ??= new List<GardenPatch>();
                patches.RemoveAll(x => x == null);
                // (A save from before the rumour mill has none; one whose level is gone is dropped.)
                rumours ??= new List<Rumour>();
                rumours.RemoveAll(r => r?.level == null);
                // (A save from before the requests to see the warden has none.)
                asks ??= new List<WardenAsk>();
                asks.RemoveAll(a => a == null || a.inmateId < 0);
                cellNumbers ??= new Dictionary<int, int>();
                if (zones == null) zones = new ZoneGrid(map);
                // (A save from before land has none: the whole map is the warden's.)
                grounds ??= new PrisonGrounds(map);
                turf ??= new TurfBook();
                count ??= new CountState();
                care ??= new CareDetail();
                evac ??= new EvacuationState();
                bookings ??= new ClassBookings();
                sessions ??= new List<ProgramSession>();
                sessions.RemoveAll(s => s?.program == null);
                MigrateOldSession();
            }
        }

        /// <summary>An old save's single session joins the list (it holds no room: it ends within the hour).</summary>
        public void MigrateOldSession()
        {
            if (session?.program != null) sessions.Add(session);
            session = null;
        }

        public void RegisterDoor(CompPrisonDoor door)
        {
            if (!doors.Contains(door)) doors.Add(door);
            doorsDirty = true;
        }

        public void DeregisterDoor(CompPrisonDoor door)
        {
            doors.Remove(door);
            doorsDirty = true;
        }

        public void RegisterFixture(CompFixture f)
        {
            if (!fixtures.Contains(f)) fixtures.Add(f);
        }

        public void DeregisterFixture(CompFixture f) => fixtures.Remove(f);

        public void Notify_DoorsChanged() => doorsDirty = true;

        private bool bedsDirty;

        /// <summary>A bed was built or removed: the beds each room holds (what a door's verdict reads) are listed again at the next tick, not the next 250-tick beat.</summary>
        public void Notify_BedsChanged()
        {
            bedsDirty = true;
            doorsDirty = true;
        }

        /// <summary>The cells the rioters hold in a hostage crisis (null when nothing is held), as an immutable snapshot that pathfinding threads may read, and the same cells for drawing.</summary>
        public volatile HashSet<int> heldCells;
        private readonly List<IntVec3> heldList = new List<IntVec3>();
        /// <summary>Power and water to the held area have been cut (see <see cref="UtilitySystem"/>).</summary>
        public bool heldPowerCut;
        private HashSet<Room> heldRooms;
        /// <summary>Where the "HELD" label goes: the hostage's own spot when it is held, else the middle of the cells.</summary>
        private IntVec3 heldLabelCell;
        /// <summary>The warden has been told the hold broke (a crisis is running but its room is gone); how many times, for tests.</summary>
        private bool holdBrokenTold;
        public int holdBrokenMessages;
        public IntVec3 HeldLabelCell => heldLabelCell;

        /// <summary>Works out what is held now (with every door pass).</summary>
        private void RefreshHold()
        {
            heldRooms = HostageHold.Rooms(map);
            if (heldRooms == null)
            {
                // A wall or a door of the room is gone (bashed, burnt): the crisis goes on but nothing is held. Said once.
                if (HostageCrisis.Running(map) is { } crisis)
                {
                    if (!holdBrokenTold)
                    {
                        holdBrokenTold = true;
                        holdBrokenMessages++;
                        Messages.Message("RP_HostageHoldBroken".Translate(crisis.A.LabelShort), crisis.A, MessageTypeDefOf.NegativeEvent, historical: false);
                    }
                }
                else holdBrokenTold = false;
                if (heldCells == null) return;
                heldCells = null;
                heldList.Clear();
                heldPowerCut = false;
                return;
            }
            var cells = new HashSet<int>();
            heldList.Clear();
            foreach (Room room in heldRooms)
                foreach (IntVec3 c in room.Cells)
                {
                    cells.Add(map.cellIndices.CellToIndex(c));
                    heldList.Add(c);
                }
            if (heldCells == null || !heldCells.SetEquals(cells)) heldCells = cells;
            heldPowerCut = HostageHold.PowerCut(map);
            holdBrokenTold = false;
            IntVec3 anchor = HostageCrisis.Running(map) is { } running ? HostageNote.Of(running).anchor : IntVec3.Invalid;
            heldLabelCell = anchor.IsValid && cells.Contains(map.cellIndices.CellToIndex(anchor)) ? anchor : heldList[heldList.Count / 2];
        }

        private bool doorModeChanged;

        /// <summary>How many times the door pass cleared the map's reachability cache (for tests).</summary>
        public int reachabilityClears;

        /// <summary>A door's mode was set: who may pass changed whether or not an inmate verdict did, so the next pass clears the reachability cache.</summary>
        public void Notify_DoorModeChanged()
        {
            doorModeChanged = true;
            doorsDirty = true;
        }

        public void ForceDoorUpdate()
        {
            RefreshBeds();
            UpdateDoors();
        }

        /// <summary>
        /// Inmates to be sent to look for a new job because the regime moved on (every inmate whose activity changed at the hour, the attendees of a class
        /// that starts), the job each had when the change came. Drained a few a tick: a prison of three hundred got a new job each in one tick and the
        /// whole job search of the prison (a think tree each) fell in that tick.
        /// </summary>
        private readonly Queue<(Pawn pawn, int jobId)> interruptQueue = new Queue<(Pawn, int)>();
        private readonly HashSet<Pawn> interruptQueued = new HashSet<Pawn>();

        /// <summary>How many queued inmates are sent to a new job in a tick.</summary>
        public const int InterruptsPerTick = 12;

        /// <summary>Inmates waiting to be sent to a new job (for tests).</summary>
        public int PendingInterrupts => interruptQueue.Count;

        /// <summary>Queues the inmate to be interrupted (in the next few ticks) if what they are doing now is something a regime change ends; once per inmate until they are.</summary>
        public void QueueRegimeInterrupt(Pawn p)
        {
            if (!InmateUtility.WouldInterruptForRegimeChange(p, emergency: false) || !interruptQueued.Add(p)) return;
            // (By the job's number, not the object: jobs are pooled and handed out again, and a reused object is not the job they had.)
            interruptQueue.Enqueue((p, p.CurJob.loadID));
        }

        /// <summary>
        /// The first pass of a session (a new game, or a load: the queue is not saved, and a save in the first ticks of an hour dropped the inmates still waiting in it): everyone who is
        /// idle is sent to look for what the regime has for them now.
        /// </summary>
        private void RegimeSweep()
        {
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
                if (InmateUtility.IsActiveInmate(prisoners[i])) QueueRegimeInterrupt(prisoners[i]);
        }

        internal void DrainInterrupts()
        {
            for (int n = 0; n < InterruptsPerTick && interruptQueue.Count > 0; n++)
            {
                (Pawn p, int jobId) = interruptQueue.Dequeue();
                interruptQueued.Remove(p);
                // (Already on to something else by themselves: they chose it under the new regime.)
                if (p.Spawned && p.Map == map && p.CurJob is { } job && job.loadID == jobId) InmateUtility.InterruptForRegimeChange(p);
            }
        }

        public override void MapComponentTick()
        {
            // The prison passes are global (one prison, one set of timers): a second map (dev mode, another mod's site) must not run
            // them, or whichever map's hour changes first would judge a promise, an inspection or a news item against an empty map.
            if (!map.IsPlayerHome) return;
            if (interruptQueue.Count > 0) DrainInterrupts();
            if (evac.Active) EvacuationSystem.TickPlanning(map, this);
            int ticks = Find.TickManager.TicksGame;
            // Off the 250-tick beat only the door verdicts are redone (something changed who may pass): the rest of the pass (guards, beds, utilities,
            // the K9 sniff whose rolls assume a 250-tick step) runs on the beat alone.
            if (ticks % 250 != 0 && !firstPassDue)
            {
                if (doorsDirty)
                {
                    if (bedsDirty) RefreshBeds();
                    UpdateDoors();
                }
                return;
            }
            firstPassDue = false;
            RefreshBeds();
            int hour = GenLocalDate.HourOfDay(map);
            if (hour != lastHour)
            {
                bool first = lastHour < 0;
                lastHour = hour;
                if (first) RegimeSweep();
                else OnHourChanged(hour);
            }
            UpdateDoors();
            RefreshGuards();
            care.Tick(map);
            WatchList.CallFreeGuards(map);
            EvacuationSystem.Beat(map, this);
            K9Utility.Tick(map);
            UtilitySystem.Tick(map);
            CountSystem.Tick(map, this);
            ProgramSystem.CompleteFinished(this);
            ProgramSystem.NudgeTeachers(map, this);
        }

        private void RefreshGuards()
        {
            guards.Clear();
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (GuardUtility.IsGuard(colonists[i])) guards.Add(colonists[i]);
        }

        /// <summary>A released inmate who goes down on the way out is picked up by an ambulance (they are no longer
        /// the prison's to carry, and would otherwise lie there).</summary>
        private void CollectDownedReleased()
        {
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = prisoners.Count - 1; i >= 0; i--)
            {
                Pawn p = prisoners[i];
                if (!p.guest.Released || !p.Downed || p.Dead) continue;
                Messages.Message("RP_ReleasedCollected".Translate(p.LabelShort), MessageTypeDefOf.NeutralEvent, historical: false);
                ShuttleSystem.Notify_Left(p);
                p.DeSpawn();
                Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.Discard);
            }
        }

        private void OnHourChanged(int hour)
        {
            if (AutoTest.Active) PerfProbe.HourChangedAt = Find.TickManager.TicksGame;
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return;
            int prevHour = (hour + 23) % 24;
            // (Before the regime change moves anyone: who stood where during the hour that is over.)
            GangTurf.Sample(map, this);
            // (The same count is the rumour mill's room meetings: who of each wing met others of it in a shared place.)
            Rumours.Hourly(map, this);
            // (The requests to see the warden: lapsed ones, and those whose reason is over, go.)
            WardenAsks.Hourly(map, this);
            // (The hour that is over is counted for the regime tab's strip, today's orders and all; then the orders of a day that is over go.)
            regime.Sample(map, this, game, prevHour);
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            // (Where each stood in the hour that is over is read under the orders of that day, before they go: at midnight the 23:00 an early lockup had is not free time because the order is gone.)
            RegimeActivityDef[] was = game.dayOrders.Count > 0 ? new RegimeActivityDef[prisoners.Count] : null;
            if (was != null)
                for (int i = 0; i < prisoners.Count; i++)
                    if (InmateUtility.IsActiveInmate(prisoners[i])) was[i] = game.ActivityAt(prisoners[i].Inmate(), prevHour);
            RegimeOrders.Sweep(game, map);
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                if (!InmateUtility.IsActiveInmate(p)) continue;
                CompInmate comp = p.Inmate();
                if ((was?[i] ?? game.ActivityAt(comp, prevHour)) == game.ActivityAt(comp, hour)) continue;
                QueueRegimeInterrupt(p);
            }
            game.Notify_HourChanged(map, hour);
            CountSystem.Hourly(map, this, game, hour);
            CollectDownedReleased();
            InmateJobWorker_Garden.ReleaseProduce(map, this);
            GardenPatches.Hourly(map, this);
            DeathRowSystem.Hourly(map);
            ProgramSystem.HourlyTick(map, this, game);
        }

        /// <summary>Recomputes every door's inmate verdict; clears reachability once if any verdict flipped.</summary>
        private bool workZonesChanged;

        /// <summary>An inmate's work pass changed (work hours started or ended): only what that inmate can reach is different (no door's verdict is), so only their cached answers go.</summary>
        public void Notify_WorkPassChanged(Pawn p)
        {
            if (p.Spawned) map.reachability.ClearCacheFor(p);
        }

        private void UpdateDoors()
        {
            doorsDirty = false;
            PrisonGame game = PrisonGame.Instance;
            bool changed = false;
            int now = Find.TickManager.TicksGame;
            RefreshHold();
            for (int i = 0; i < doors.Count; i++)
            {
                CompPrisonDoor d = doors[i];
                bool pass = ComputeInmatePass(d, game, now);
                if (pass != d.inmatePass)
                {
                    d.inmatePass = pass;
                    changed = true;
                }
                // A door on the border of a held room or wing is locked to staff coming in (the verdict is computed here, never per path query).
                HashSet<int> border = heldRooms != null && d.Door.Spawned && HostageHold.IsBorder(d.Door, heldRooms, map) ? heldCells : null;
                if (border != d.heldCells)
                {
                    if ((border != null) != (d.heldCells != null)) changed = true;
                    d.heldCells = border;
                }
            }
            if (UpdateArrivalPasses()) changed = true;
            if (workZonesChanged || doorModeChanged)
            {
                workZonesChanged = doorModeChanged = false;
                changed = true;
            }
            if (changed)
            {
                reachabilityClears++;
                map.reachability.ClearCache();
            }
            if (heldRooms != null) HostageHold.LetStaffOut(map, this);
        }

        private readonly HashSet<Room> sallyPorts = new HashSet<Room>();

        /// <summary>
        /// Which doors a new arrival may pass (<see cref="CompPrisonDoor.arrivalPass"/>): a gate to the outside; a door of a sally port (the staff room a
        /// gate opens into: the gatehouse) into anywhere but another staff room; any other door that is not staff-only and touches no staff room. The sides
        /// come from this pass's <see cref="ComputeInmatePass"/>. True when a verdict changed.
        /// </summary>
        private bool UpdateArrivalPasses()
        {
            sallyPorts.Clear();
            for (int i = 0; i < doors.Count; i++)
            {
                CompPrisonDoor d = doors[i];
                Room inner = d.sideA == DoorSide.Outside && d.sideB == DoorSide.Staff ? d.roomB : d.sideB == DoorSide.Outside && d.sideA == DoorSide.Staff ? d.roomA : null;
                // (Only a staff-only or unpainted room, the gatehouse: a kitchen or staff room with a door outside is no way in.)
                if (inner == null || inner.IsDoorway) continue;
                PrisonZoneDef z = null;
                foreach (IntVec3 ic in inner.Cells)
                {
                    z = zones.ZoneAt(ic);
                    break;
                }
                if (z == null || z == RPDefOf.RP_StaffOnly) sallyPorts.Add(inner);
            }
            bool changed = false;
            for (int i = 0; i < doors.Count; i++)
            {
                CompPrisonDoor d = doors[i];
                bool portA = d.roomA != null && sallyPorts.Contains(d.roomA), portB = d.roomB != null && sallyPorts.Contains(d.roomB);
                bool pass = CompPrisonDoor.ArrivalPass(d.mode, d.sideA, d.sideB, portA, portB);
                if (pass != d.arrivalPass)
                {
                    d.arrivalPass = pass;
                    changed = true;
                }
            }
            return changed;
        }

        private bool ComputeInmatePass(CompPrisonDoor d, PrisonGame game, int now)
        {
            d.cellRoom = null;
            d.roomA = d.roomB = null;
            d.sideA = d.sideB = DoorSide.None;
            PrisonZoneDef zoneA = null, zoneB = null;
            Room secondRoom = null;
            Building_Door door = d.Door;
            if (!door.Spawned) return false;
            // Sides: the rooms on the cardinal neighbours that are not walls.
            Room first = null;
            for (int k = 0; k < 4; k++)
            {
                IntVec3 c = door.Position + GenAdj.CardinalDirections[k];
                if (!c.InBounds(map) || !c.Walkable(map)) continue;
                Room room = c.GetRoom(map);
                if (room != null && room.IsDoorway)
                {
                    // Double door / airlock: classify by what lies beyond the next door.
                    IntVec3 beyond = c + GenAdj.CardinalDirections[k];
                    if (!beyond.InBounds(map) || !beyond.Walkable(map)) continue;
                    c = beyond;
                    room = c.GetRoom(map);
                }
                if (room == null || room.IsDoorway || room == first) continue;
                DoorSide side = Classify(c, room);
                if (first == null)
                {
                    first = room;
                    d.roomA = room;
                    d.sideA = side;
                    zoneA = zones.ZoneAt(c);
                }
                else
                {
                    d.sideB = side;
                    zoneB = zones.ZoneAt(c);
                    secondRoom = room;
                    d.roomB = room;
                    if (side == DoorSide.Cell) d.cellRoom = room;
                    break;
                }
                if (side == DoorSide.Cell) d.cellRoom = room;
            }
            // An Auto door between inmate space and a staff work zone: trusted workers may pass during work hours.
            // (Only an indoor staff room: a staff zone painted outdoors must never become a way out.)
            PrisonZoneDef work = null;
            if (d.mode == PrisonDoorMode.Auto)
            {
                if (d.sideA == DoorSide.Staff && d.sideB == DoorSide.InmateSpace && IndoorRoom(first)) work = zoneA;
                else if (d.sideB == DoorSide.Staff && d.sideA == DoorSide.InmateSpace && IndoorRoom(secondRoom)) work = zoneB;
            }
            if (d.workZone != work)
            {
                d.workZone = work;
                workZonesChanged = true;
            }
            switch (d.mode)
            {
                case PrisonDoorMode.Locked:
                case PrisonDoorMode.StaffOnly:
                    d.lockupSince = -1;
                    return false;
                case PrisonDoorMode.Inmates:
                    return true;
            }
            if (d.sideA is DoorSide.Outside or DoorSide.Staff or DoorSide.None || d.sideB is DoorSide.Outside or DoorSide.Staff or DoorSide.None)
            {
                d.lockupSince = -1;
                return false;
            }
            if (d.cellRoom == null || game == null)
            {
                d.lockupSince = -1;
                return true;
            }
            return CellDoorOpen(d, game, now, GenLocalDate.HourOfDay(map));
        }

        /// <summary>
        /// A cell door is open outside lockup. When lockup starts it stays open until every occupant is inside, or
        /// for at most two hours; after that, late inmates are escorted in by staff.
        /// </summary>
        internal bool CellDoorOpen(CompPrisonDoor d, PrisonGame game, int now, int hour)
        {
            bool result = CellDoorOpen(d, game, now, hour, live: false);
            if (AutoTest.Active)
            {
                // (A test run asks the live room as well, and counts the verdicts that differ: the beds kept by the last pass may be up to 250 ticks old.)
                int since = d.lockupSince;
                bool liveResult = CellDoorOpen(d, game, now, hour, live: true);
                d.lockupSince = since;
                cellDoorChecks++;
                if (liveResult != result) cellDoorMismatches++;
            }
            return result;
        }

        /// <summary>For tests: cell door verdicts, and how many differed between the kept beds and the live room.</summary>
        public static int cellDoorChecks, cellDoorMismatches;

        private bool CellDoorOpen(CompPrisonDoor d, PrisonGame game, int now, int hour, bool live)
        {
            // Locked when an owner's schedule says lockup, or when every owner is confined (a frightened inmate hiding
            // in a shared cell does not lock their cellmates in).
            bool anyOwner = false, lockup = false, ownerOutside = false, allConfined = true;
            scratchBeds.Clear();
            if (!live && bedsByRoom.TryGetValue(d.cellRoom, out List<Building_Bed> cellBeds)) scratchBeds.AddRange(cellBeds);
            else foreach (Building_Bed inRoom in d.cellRoom.ContainedBeds) scratchBeds.Add(inRoom);
            for (int b = 0; b < scratchBeds.Count; b++)
            {
                Building_Bed bed = scratchBeds[b];
                if (!bed.Spawned) continue;
                List<Pawn> owners = bed.OwnersForReading;
                for (int i = 0; i < owners.Count; i++)
                {
                    Pawn o = owners[i];
                    if (!InmateUtility.IsActiveInmate(o) || o.Map != map) continue;
                    anyOwner = true;
                    CompInmate oc = o.Inmate();
                    RegimeActivityDef act = game.ActivityAt(oc, hour);
                    bool hiding = game.Hiding(oc, act);
                    if (hiding) act = RPDefOf.RP_Lockup;
                    // Supermax leaves the cell only with a guard (who passes the door for them): the cell is locked in every hour of the regime, the shower and the yard slots included.
                    bool confined = act.lockup || oc.security is { escortOnly: true };
                    if (confined && !hiding && !game.ConfinedAlone(oc, hour)) lockup = true;
                    if (!confined) allConfined = false;
                    // Owners away for a reason (infirmary, carried, restrained) do not hold the door open. Nor does a supermax inmate who is out of the cell: their cell is shut to everyone else (a guard
                    // brings them back, and the walk's pass opens it for them).
                    if (o.GetRoom() != d.cellRoom && !o.Downed && !o.InBed() && o.CarriedBy == null && oc.security is not { escortOnly: true }) ownerOutside = true;
                }
            }
            if (anyOwner && allConfined) lockup = true;
            if (!anyOwner || !lockup)
            {
                d.lockupSince = -1;
                return true;
            }
            if (d.lockupSince < 0) d.lockupSince = now;
            // A fire in the cell, or in the room outside it: nobody is left locked in with it (the one thing that opens a supermax cell outside a guard's walk).
            if (FireAt(d)) return true;
            if (ownerOutside) return true;
            // A visitor from another cell is let out before the door locks. (Whoever stands in the room is in the lister of one of its regions.)
            foreach (Region region in d.cellRoom.Regions)
            {
                List<Thing> inRegion = region.ListerThings.AllThings;
                for (int i = 0; inRegion != null && i < inRegion.Count; i++)
                    if (inRegion[i] is Pawn p && p.GetRoom() == d.cellRoom && p.GetComp<CompInmate>() is { activeFlag: true } && HoldsDoorOpen(p.Downed, InmateUtility.CellOf(p), d.cellRoom)) return true;
            }
            return false;
        }

        /// <summary>Is something burning in the cell behind this door, or in the indoor room on its other side?</summary>
        private bool FireAt(CompPrisonDoor d)
        {
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count; i++)
            {
                Room room = fires[i].Position.GetRoom(map);
                if (room != null && (room == d.cellRoom || (room == d.roomA || room == d.roomB) && !room.PsychologicallyOutdoors)) return true;
            }
            return false;
        }

        /// <summary>An inmate in a cell holds its door open through lockup only as a visitor with a cell of their own to go back to, and able to walk there: a downed one, or one with no bed, is not a visitor.</summary>
        internal static bool HoldsDoorOpen(bool downed, Room ownCell, Room inCell) => !downed && ownCell != null && ownCell != inCell;

        private static bool IndoorRoom(Room room) => room != null && !room.PsychologicallyOutdoors && !room.TouchesMapEdge;

        private DoorSide Classify(IntVec3 c, Room room)
        {
            PrisonZoneDef zone = zones.ZoneAt(c);
            if (zone != null && zone.staffOnly) return DoorSide.Staff;
            // Cells first: wing zones are painted over them.
            if (room.IsPrisonCell) return DoorSide.Cell;
            if (zone != null && zone.inmateAccess) return DoorSide.InmateSpace;
            if (room.PsychologicallyOutdoors || room.TouchesMapEdge) return DoorSide.Outside;
            // Unzoned indoor space (corridors) is inmate space unless it is a staff zone.
            return zone == null ? DoorSide.InmateSpace : DoorSide.Staff;
        }

        public override void MapComponentUpdate()
        {
            if (Find.CurrentMap != map) return;
            // Barely there while playing; clear while a zone tool is in hand.
            bool designating = Find.DesignatorManager.SelectedDesignator is Designator_PrisonZone;
            if (designating || RimPrisonMod.Settings.showZoneOverlay) zones.MarkForDraw(designating);
            zones.DrawerUpdate();
            if (heldList.Count > 0) GenDraw.DrawFieldEdges(heldList, HostageHold.HeldColor);
            (tensionOverlay ??= new TensionOverlay(map)).Update();
            if (CoverageOverlay.Show) Coverage.Update(); // (the per-cell arrays only once it is used)
            if (TurfOverlay.Show) TurfShown.Update();
            if (DangerOverlay.Show) DangerShown.Update();
            (groundsOverlay ??= new GroundsOverlay(map)).Update(grounds);
        }

        public override void MapComponentOnGUI()
        {
            if (Find.CurrentMap != map) return;
            if (Find.DesignatorManager.SelectedDesignator is Designator_PrisonZone
                && Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle && Event.current.type == EventType.Repaint)
            {
                foreach ((PrisonZoneDef zone, Vector3 center) in zones.Labels)
                    if (zone != null) GenMapUI.DrawThingLabel(center.MapToUIPosition(), zone.label, zone.color);
            }
            if (heldList.Count > 0 && Event.current.type == EventType.Repaint && Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle)
                GenMapUI.DrawThingLabel(heldLabelCell.ToVector3Shifted().MapToUIPosition(), "RP_Held".Translate(), HostageHold.HeldColor);
            if (TurfOverlay.Show && Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle && Event.current.type == EventType.Repaint)
                TurfShown.DrawLabels(this);
            if (CoverageOverlay.Show && Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle && Event.current.type == EventType.Repaint)
                FindsOverlay.DrawMarks(this);
            if (DangerOverlay.Show && Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle && Event.current.type == EventType.Repaint)
                DangerOverlay.DrawLabels(this);
            PrisonHUD.DoHUD(map);
        }
    }

    /// <summary>A bed built or taken away changes which beds a cell holds: the door verdicts that read the list are redone at the next tick (<see cref="PrisonMap.Notify_BedsChanged"/>).</summary>
    [HarmonyLib.HarmonyPatch(typeof(Building_Bed), nameof(Building_Bed.SpawnSetup))]
    public static class Patch_Building_Bed_Spawn_Beds
    {
        public static void Postfix(Building_Bed __instance, Map map) => PrisonMap.Of(map)?.Notify_BedsChanged();
    }

    [HarmonyLib.HarmonyPatch(typeof(Building_Bed), nameof(Building_Bed.DeSpawn))]
    public static class Patch_Building_Bed_DeSpawn_Beds
    {
        public static void Prefix(Building_Bed __instance) => PrisonMap.Of(__instance.Map)?.Notify_BedsChanged();
    }
}
