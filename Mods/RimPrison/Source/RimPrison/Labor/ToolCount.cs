using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Where a tool that was missing turned up (the message says it, and the room's Intel line keeps it).</summary>
    public enum ToolSource : byte
    {
        /// <summary>On an inmate, in a frisk or a pat-down.</summary>
        Pockets,
        /// <summary>Hidden in a cell.</summary>
        Stash,
        /// <summary>Beside a tunnel that was found.</summary>
        Tunnel,
        /// <summary>Handed in at an amnesty.</summary>
        Amnesty,
        /// <summary>Taken in the bust of a smuggling ring.</summary>
        Raid,
    }

    /// <summary>A stolen thing and the room and work period it came out of. Kept by thing id: a traced thing is never stacked (see <see cref="Patch_Thing_CanStackWith_Traced"/>), so its id outlives every move.</summary>
    public class ToolTrace : IExposable
    {
        public int thingId;
        public int poolId;
        public int period;
        /// <summary>When it was taken, in the order of the book's thefts (<see cref="ToolLedgers.nextOrder"/>): a count has counted it when this is below the pool's <see cref="ToolPool.countedOrder"/>. A tick would not do: a theft and a count can fall in one tick.</summary>
        public int order;
        /// <summary>The game tick it was taken at (a replaced trace is let go of after <see cref="ToolCount.ReplacedKeepTicks"/>).</summary>
        public int tick = -1;
        /// <summary>The warden paid for a new one: if the thing turns up, it is only taken (the pool does not get it back, or it would have one too many).</summary>
        public bool replaced;

        public void ExposeData()
        {
            Scribe_Values.Look(ref thingId, "thingId");
            Scribe_Values.Look(ref poolId, "poolId");
            Scribe_Values.Look(ref period, "period");
            Scribe_Values.Look(ref order, "order");
            Scribe_Values.Look(ref tick, "tick", -1);
            Scribe_Values.Look(ref replaced, "replaced");
        }
    }

    /// <summary>
    /// One room's tool supply (see <see cref="ToolCount"/>). What is true (<see cref="ToolCount.Missing"/>, worked out from the traces) and what the staff know (<see cref="knownMissing"/>, as of the last count) are kept apart: the screens and the
    /// letters read the second only.
    /// </summary>
    public class ToolPool : IExposable
    {
        public int id;
        public PrisonZoneDef zone;
        /// <summary>The benches the supply was sized from, and the tools the room is issued (<see cref="ToolCount.PerBench"/> a bench).</summary>
        public int benches, owned;

        /// <summary>The room's work periods: the id of the current (or last) one, whether it is running, when it began, who was rostered to it (thing ids), and what was issued at its start (the tools that were not missing).</summary>
        public int period;
        public bool inPeriod;
        public int periodStartTick = -1;
        public List<int> workers = new List<int>();
        public int issued;

        /// <summary>A count is waiting for a member of staff (it is for the period <see cref="countDuePeriod"/>).</summary>
        public bool countDue;
        public int countDuePeriod;
        /// <summary>The last count that was done: the period, when, how many tools were issued and how many were counted back in.</summary>
        public int countedPeriod;
        public int countedTick = -1;
        public int countedIssued, countedReturned;
        /// <summary>What the last count has counted: the traces taken before this order (see <see cref="ToolTrace.order"/>) are the staff's knowledge, the ones taken since are not (yet).</summary>
        public int countedOrder;
        /// <summary>The crews of the periods the open letter is about (thing ids): the warden's search is of these, not of whoever works the room by the time the letter is answered.</summary>
        public List<int> countedWorkers = new List<int>();
        /// <summary>What the last count found short and nobody has replaced or got back since (the staff's number: the true one is in the traces).</summary>
        public int knownMissing;
        /// <summary>A letter asks the warden what to do about the shortage (since this tick).</summary>
        public bool asked;
        public int askedTick = -1;

        /// <summary>The warden's answer, for the room's next work period: it starts this many hours late and (a re-count) is supervised, so that nothing is taken in it.</summary>
        public int pendingLateHours;
        public bool pendingSupervised;
        /// <summary>The room's work is held until this tick; the running period is supervised.</summary>
        public int holdUntilTick = -1;
        public bool supervised;

        /// <summary>The crew a search is owed for (thing ids; each is taken off as they are searched) and the tick the search lapses, and how many tools it turned up.</summary>
        public List<int> searchIds = new List<int>();
        public int searchUntilTick = -1;
        public int searchFound;

        /// <summary>The last tool of the room that was found: when, where, and the period it was taken in.</summary>
        public int foundTick = -1;
        public ToolSource foundSource;
        public int foundPeriod;

        /// <summary>Since when the room has had no bench (-1: it has one). A supply is kept for <see cref="ToolCount.BenchlessGraceTicks"/> before it is retired: a press that is uninstalled and put up again is the same room.</summary>
        public int benchlessSinceTick = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Defs.Look(ref zone, "zone");
            Scribe_Values.Look(ref benches, "benches");
            Scribe_Values.Look(ref owned, "owned");
            Scribe_Values.Look(ref period, "period");
            Scribe_Values.Look(ref inPeriod, "inPeriod");
            Scribe_Values.Look(ref periodStartTick, "periodStartTick", -1);
            Scribe_Collections.Look(ref workers, "workers", LookMode.Value);
            Scribe_Values.Look(ref issued, "issued");
            Scribe_Values.Look(ref countDue, "countDue");
            Scribe_Values.Look(ref countDuePeriod, "countDuePeriod");
            Scribe_Values.Look(ref countedPeriod, "countedPeriod");
            Scribe_Values.Look(ref countedTick, "countedTick", -1);
            Scribe_Values.Look(ref countedIssued, "countedIssued");
            Scribe_Values.Look(ref countedReturned, "countedReturned");
            Scribe_Values.Look(ref countedOrder, "countedOrder");
            Scribe_Collections.Look(ref countedWorkers, "countedWorkers", LookMode.Value);
            Scribe_Values.Look(ref knownMissing, "knownMissing");
            Scribe_Values.Look(ref asked, "asked");
            Scribe_Values.Look(ref askedTick, "askedTick", -1);
            Scribe_Values.Look(ref pendingLateHours, "pendingLateHours");
            Scribe_Values.Look(ref pendingSupervised, "pendingSupervised");
            Scribe_Values.Look(ref holdUntilTick, "holdUntilTick", -1);
            Scribe_Values.Look(ref supervised, "supervised");
            Scribe_Collections.Look(ref searchIds, "searchIds", LookMode.Value);
            Scribe_Values.Look(ref searchUntilTick, "searchUntilTick", -1);
            Scribe_Values.Look(ref searchFound, "searchFound");
            Scribe_Values.Look(ref foundTick, "foundTick", -1);
            Scribe_Values.Look(ref foundSource, "foundSource");
            Scribe_Values.Look(ref foundPeriod, "foundPeriod");
            Scribe_Values.Look(ref benchlessSinceTick, "benchlessSinceTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                workers ??= new List<int>();
                countedWorkers ??= new List<int>();
                searchIds ??= new List<int>();
            }
        }
    }

    /// <summary>The prison's tool supplies and the things traced to them (the saved state of <see cref="ToolCount"/>, on <see cref="PrisonGame.tools"/>).</summary>
    public class ToolLedgers : IExposable
    {
        public List<ToolPool> pools = new List<ToolPool>();
        public List<ToolTrace> traces = new List<ToolTrace>();
        public int nextId = 1;
        /// <summary>The order the next trace is given (see <see cref="ToolTrace.order"/>).</summary>
        public int nextOrder = 1;

        /// <summary>The traces by thing id (asked on every stacking question: kept as an index, rebuilt on load).</summary>
        [Unsaved] public readonly Dictionary<int, ToolTrace> byThing = new Dictionary<int, ToolTrace>();

        public ToolPool PoolOf(PrisonZoneDef zone)
        {
            for (int i = 0; i < pools.Count; i++)
                if (pools[i].zone == zone) return pools[i];
            return null;
        }

        public ToolPool PoolById(int id)
        {
            for (int i = 0; i < pools.Count; i++)
                if (pools[i].id == id) return pools[i];
            return null;
        }

        public void Reindex()
        {
            byThing.Clear();
            foreach (ToolTrace t in traces) byThing[t.thingId] = t;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Values.Look(ref nextOrder, "nextOrder", 1);
            Scribe_Collections.Look(ref pools, "pools", LookMode.Deep);
            Scribe_Collections.Look(ref traces, "traces", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pools ??= new List<ToolPool>();
                traces ??= new List<ToolTrace>();
                // (A room whose def is gone has no supply; what was traced to a supply that is not there is only contraband again.)
                pools.RemoveAll(p => p == null || p.zone == null);
                traces.RemoveAll(t => t == null || PoolById(t.poolId) == null);
                Reindex();
            }
        }
    }

    /// <summary>
    /// Count the tools back in. Each workshop and kitchen (the job zones the labour code knows: one supply for the zone, not one per painted room) is issued tools by its benches - <see cref="WorkshopPerBench"/> a press, <see cref="KitchenPerBench"/> a
    /// stove. A theft at work (<see cref="ContrabandUtility.Notify_WorkedWithTools"/>) takes one of them out and the thing the thief carries is traced to the room and the work period (<see cref="ToolTrace"/>, by thing id; a traced thing
    /// is not stacked); the old unlimited creation does not run for it, and a room with nothing left to take loses nothing. At the end of each work period a member of staff counts the tools back in (<see cref="WorkGiver_CountTools"/>, a short
    /// job); only then is a shortage known: a message and a letter with the room and the period, never a name. The letter asks the warden (<see cref="ToolCountDecision"/>) for a supervised re-count, a search of the crew or to carry on short
    /// (unanswered: that). A known shortage costs the room <see cref="CutPerTool"/> of its output a tool, up to <see cref="MaxCut"/>, until it is replaced (Intel, <see cref="ReplaceCost"/> a tool) or got back: a tool found anywhere
    /// (a search, a stash, a tunnel, an amnesty, a bust) that is traced puts its supply right once, and says where it came from.
    /// A supply is made once, at full, for a room that has benches (an old save's included) and retired - with what was traced to it - when its benches are gone. Not on in an autotest run unless a check turns it on (<see cref="TestOn"/>).
    /// </summary>
    public static class ToolCount
    {
        public const int WorkshopPerBench = 4, KitchenPerBench = 3;

        /// <summary>What a tool costs to replace, from Intel.</summary>
        public const int ReplaceCost = 40;

        /// <summary>The output a known missing tool takes off the room's work, and the most that can come off.</summary>
        public const float CutPerTool = 0.15f, MaxCut = 0.5f;

        /// <summary>How late the room's next work period starts after a supervised re-count and after a search of the crew.</summary>
        public const int RecountLateHours = 1, SearchLateHours = 2;

        /// <summary>The time the warden has to answer the letter (and unanswered: carry on short).</summary>
        public const int AnswerTicks = GenDate.TicksPerDay;

        /// <summary>How long a staff member counts the tools, and how long a search of the crew may take.</summary>
        public const int CountTicks = 300, SearchWindowTicks = GenDate.TicksPerDay;

        /// <summary>How long a room with no bench keeps its supply (and what is traced to it) before it is retired, and how long a replaced trace is kept (for the "it had been replaced" message).</summary>
        public const int BenchlessGraceTicks = GenDate.TicksPerDay, ReplacedKeepTicks = 5 * GenDate.TicksPerDay;

        /// <summary>For a check: switches the count on in an autotest run.</summary>
        public static bool TestOn;

        /// <summary>For a check: only these inmates are anybody's crew (null: every inmate; a check's scene stands outside the real rooms and its inmates are the whole cast).</summary>
        internal static System.Func<Pawn, bool> CastForTests;

        /// <summary>For a check: everything the count says to the warden (messages and letters), as it is said; null when no check is listening.</summary>
        internal static List<string> Heard;

        public static bool Active => PrisonGame.Instance != null && (!AutoTest.Active || TestOn);

        public static ToolLedgers Book => PrisonGame.Instance?.tools;

        public static bool IsLedgered(PrisonZoneDef zone) => zone != null && (zone == RPLaborDefOf.RP_Workshop || zone == RPLaborDefOf.RP_Kitchen);

        /// <summary>Whether the room's tools are counted: its theft takes from the supply (see <see cref="ContrabandUtility.Notify_WorkedWithTools"/>).</summary>
        public static bool Ledgered(PrisonZoneDef zone) => Active && IsLedgered(zone);

        public static int PerBench(PrisonZoneDef zone) => zone == RPLaborDefOf.RP_Kitchen ? KitchenPerBench : WorkshopPerBench;

        public static IEnumerable<PrisonZoneDef> Zones
        {
            get
            {
                yield return RPLaborDefOf.RP_Workshop;
                yield return RPLaborDefOf.RP_Kitchen;
            }
        }

        private static void Say(TaggedString message, LookTargets targets = null)
        {
            Heard?.Add(message.Resolve());
            if (targets != null) Messages.Message(message, targets, MessageTypeDefOf.NeutralEvent, historical: false);
            else Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ---- the supply ----------------------------------------------------------------------------------------------------------------------

        /// <summary>The room's benches that carry tools: the presses in the workshop, the stoves in the kitchen.</summary>
        public static List<Thing> BenchesOf(Map map, PrisonMap pm, PrisonZoneDef zone)
        {
            var list = new List<Thing>();
            if (pm == null) return list;
            if (zone == RPLaborDefOf.RP_Workshop)
            {
                foreach (CompWorkstation w in pm.Workstations)
                    if (w.parent.Spawned && w.Props.input != null && pm.zones.ZoneAt(w.parent.Position) == zone) list.Add(w.parent);
            }
            else if (zone == RPLaborDefOf.RP_Kitchen)
            {
                foreach (Building b in InmateJobWorker_Kitchen.KitchenStoves(map, pm)) list.Add(b);
            }
            return list;
        }

        /// <summary>The room's supply, made at full when the room has benches and none has been made (an old save's first hour, or the first theft).</summary>
        public static ToolPool PoolHere(Map map, PrisonZoneDef zone)
        {
            ToolLedgers book = Book;
            if (book == null || !IsLedgered(zone)) return null;
            ToolPool pool = book.PoolOf(zone);
            if (pool != null) return pool;
            int benches = BenchesOf(map, PrisonMap.Of(map), zone).Count;
            if (benches == 0) return null;
            pool = new ToolPool { id = book.nextId++, zone = zone, benches = benches, owned = benches * PerBench(zone) };
            book.pools.Add(pool);
            return pool;
        }

        /// <summary>What is not back: the tools traced to the room that are out, not counting the ones that were paid for.</summary>
        public static int Missing(ToolPool pool)
        {
            ToolLedgers book = Book;
            if (book == null) return 0;
            int n = 0;
            for (int i = 0; i < book.traces.Count; i++)
                if (book.traces[i].poolId == pool.id && !book.traces[i].replaced) n++;
            return n;
        }

        /// <summary>The tools in the room's stores: what a theft can take.</summary>
        public static int OnHand(ToolPool pool) => Mathf.Max(0, pool.owned - Missing(pool));

        /// <summary>The room's work is held (a re-count or a search before the period).</summary>
        public static bool Held(PrisonZoneDef zone) => Book?.PoolOf(zone) is { } pool && pool.holdUntilTick > Find.TickManager.TicksGame;

        /// <summary>What the room's work is worth at the moment, by what the staff know to be short (1 for a room counted whole, or with no supply).</summary>
        public static float OutputFactor(PrisonZoneDef zone) => Book?.PoolOf(zone) is { knownMissing: > 0 } pool ? Mathf.Max(1f - MaxCut, 1f - CutPerTool * pool.knownMissing) : 1f;

        /// <summary>How long a stretch of the room's work takes now (the work of a room short of tools is slower by the output it has lost).</summary>
        public static int WorkTicks(PrisonZoneDef zone, int ticks) => Active ? Mathf.RoundToInt(ticks / OutputFactor(zone)) : ticks;

        // ---- theft and what is traced --------------------------------------------------------------------------------------------------------

        /// <summary>A theft at work in a counted room: one tool comes out of the supply into the thief's pocket, traced to the room and the period. Nothing when there is nothing to take or the work is supervised.</summary>
        public static Thing Steal(Pawn inmate, ContrabandKind kind, PrisonZoneDef zone)
        {
            ToolPool pool = PoolHere(inmate.Map, zone);
            if (pool == null || pool.supervised || pool.benchlessSinceTick >= 0 || OnHand(pool) <= 0) return null;
            return ContrabandUtility.Give(inmate, ContrabandUtility.PickSmuggled(inmate, kind), tracedTo: pool);
        }

        /// <summary>Written down before the thing is handed over, so that nothing can stack it in between.</summary>
        public static void Trace(Thing t, ToolPool pool)
        {
            ToolLedgers book = Book;
            var trace = new ToolTrace { thingId = t.thingIDNumber, poolId = pool.id, period = pool.period, order = book.nextOrder++, tick = Find.TickManager.TicksGame };
            book.traces.Add(trace);
            book.byThing[trace.thingId] = trace;
        }

        public static void Untrace(Thing t)
        {
            ToolLedgers book = Book;
            if (book == null || !book.byThing.TryGetValue(t.thingIDNumber, out ToolTrace trace)) return;
            book.byThing.Remove(trace.thingId);
            book.traces.Remove(trace);
        }

        public static bool Traced(Thing t) => Book is { } book && book.byThing.Count > 0 && book.byThing.ContainsKey(t.thingIDNumber);

        /// <summary>
        /// A thing that is traced is taken by the staff (a search, a stash, a tunnel, an amnesty, a bust) and is about to be destroyed: its supply is put right once - the tool is back - and the warden is told where it came from: the room and the
        /// period, never who. A tool that was already paid for is only taken. Nothing for a thing that is not traced (or whose room is gone: its traces went with it).
        /// What the staff know changes only for a tool they had counted (<see cref="ToolPool.countedOrder"/>): one that was taken after the last count is not in their number, so finding it changes nothing they know and says nothing
        /// (the next count finds the room whole of it) - the message would give away a theft nobody has counted.
        /// </summary>
        public static void Notify_Taken(Thing t, ToolSource source)
        {
            ToolLedgers book = Book;
            if (book == null || t == null || !book.byThing.TryGetValue(t.thingIDNumber, out ToolTrace trace)) return;
            book.byThing.Remove(trace.thingId);
            book.traces.Remove(trace);
            ToolPool pool = book.PoolById(trace.poolId);
            if (pool == null) return;
            if (trace.order >= pool.countedOrder) return;
            if (pool.searchIds.Count > 0 || pool.searchUntilTick >= 0) pool.searchFound++;
            if (trace.replaced)
            {
                Say("RP_ToolFoundReplaced".Translate(pool.zone.label, trace.period, ("RP_ToolSource_" + source).Translate()));
                return;
            }
            pool.knownMissing = Mathf.Max(0, pool.knownMissing - 1);
            pool.foundTick = Find.TickManager.TicksGame;
            pool.foundSource = source;
            pool.foundPeriod = trace.period;
            int still = pool.knownMissing;
            Say("RP_ToolFound".Translate(pool.zone.label, trace.period, ("RP_ToolSource_" + source).Translate(), still > 0 ? "RP_ToolFoundStill".Translate(still).Resolve() : "RP_ToolFoundWhole".Translate().Resolve()));
        }

        /// <summary>A found tunnel: the tools that dug it - the traced ones the owner of the cell has hidden or carries - are taken, and the tunnel is linked to the room they came from.</summary>
        public static void Notify_TunnelFound(Pawn owner, CompInmate c)
        {
            if (!Active || Book is not { traces.Count: > 0 } || owner == null) return;
            PrisonMap pm = PrisonMap.Of(owner.MapHeld);
            var traced = new List<Thing>();
            foreach (Thing t in c.stash) if (Traced(t)) traced.Add(t);
            if (owner.inventory != null) foreach (Thing t in owner.inventory.innerContainer) if (Traced(t)) traced.Add(t);
            if (owner.equipment?.Primary is { } eq && Traced(eq)) traced.Add(eq);
            foreach (Thing t in traced)
            {
                Notify_Taken(t, ToolSource.Tunnel);
                ContrabandKind kind = ContrabandUtility.Ext(t.def).kind;
                IntVec3 at = owner.PositionHeld;
                bool hidden = c.stash.Contains(t);
                if (t.ParentHolder is Pawn_EquipmentTracker tracker) tracker.Remove((ThingWithComps)t);
                else t.holdingOwner?.Remove(t);
                t.Destroy();
                // (A find like the others: on the map, in the day's and the game's count.)
                pm?.AddFind(at, kind, hidden ? FindSource.Stash : FindSource.Pockets);
            }
            if (traced.Count == 0) return;
            PrisonGame.Instance.stats.contrabandFound += traced.Count;
            PrisonGame.Instance.today.contrabandFound += traced.Count;
            MisconductUtility.Record(owner, c, pm, RPContrabandDefOf.RP_ContrabandPossession, evidence: true);
        }

        /// <summary>An inmate was searched (a frisk, a pat-down): they are not owed the crew's search any more.</summary>
        public static void Notify_Searched(Pawn inmate)
        {
            ToolLedgers book = Book;
            if (book == null || book.pools.Count == 0) return;
            for (int i = 0; i < book.pools.Count; i++)
            {
                ToolPool pool = book.pools[i];
                if (pool.searchIds.Count == 0 || !pool.searchIds.Remove(inmate.thingIDNumber) || pool.searchIds.Count > 0) continue;
                EndSearch(pool);
            }
        }

        private static void EndSearch(ToolPool pool)
        {
            pool.searchUntilTick = -1;
            Say("RP_ToolSearchDone".Translate(pool.zone.label, pool.searchFound));
            pool.searchFound = 0;
        }

        /// <summary>A search of the crew is owed this inmate (the frisk's reason, with a suspicion or a shakedown).</summary>
        public static bool SearchOwed(CompInmate c)
        {
            ToolLedgers book = Book;
            if (book == null || book.pools.Count == 0) return false;
            int id = c.parent.thingIDNumber;
            for (int i = 0; i < book.pools.Count; i++)
                if (book.pools[i].searchIds.Count > 0 && book.pools[i].searchIds.Contains(id)) return true;
            return false;
        }

        // ---- the hour ------------------------------------------------------------------------------------------------------------------------

        private static readonly List<int> crew = new List<int>();

        /// <summary>Hourly: the supplies follow the benches, the work periods begin and end (a count falls due at the end of one), a search that ran out of time is let go.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            // (One prison, one set of supplies: a second home map has no benches of the prison's, and would retire them.)
            if (!Active || map != Find.AnyPlayerHomeMap || PrisonMap.Of(map) is not { } pm) return;
            ToolLedgers book = game.tools;
            int now = Find.TickManager.TicksGame;
            SyncPools(book, map, pm, now);
            if (book.pools.Count == 0) return;
            // (A trace of a tool that was paid for is kept a while, to say it had been replaced when it turns up, and then let go: the saved list does not grow with every theft of the game.)
            if (book.traces.RemoveAll(t => t.replaced && t.tick >= 0 && now - t.tick > ReplacedKeepTicks) > 0) book.Reindex();
            foreach (ToolPool pool in book.pools.ToList())
            {
                crew.Clear();
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                {
                    if (p.Inmate() is not { activeFlag: true, job: { } job } c || job.zone != pool.zone || CastForTests != null && !CastForTests(p)) continue;
                    if (job.requiredProgram != null && !c.passedPrograms.Contains(job.requiredProgram)) continue;
                    if (game.CurrentActivity(p) == RPLaborDefOf.RP_Work) crew.Add(p.thingIDNumber);
                }
                if (crew.Count > 0 && !pool.inPeriod) StartPeriod(pool, now);
                else if (crew.Count == 0 && pool.inPeriod) EndPeriod(pool, now);
                if (pool.inPeriod)
                    foreach (int id in crew)
                        if (!pool.workers.Contains(id)) pool.workers.Add(id);
                if (pool.searchUntilTick >= 0 && now >= pool.searchUntilTick)
                {
                    pool.searchIds.Clear();
                    EndSearch(pool);
                }
                // (A letter that is nowhere - lost to a save or a held back letter that was dropped - does not stop the room being asked again.)
                if (pool.asked && now - pool.askedTick > AnswerTicks + (StoryLetters.MaxHeldDays + 1) * GenDate.TicksPerDay) pool.asked = false;
            }
        }

        /// <summary>
        /// Each counted room has a supply while it has benches, sized by them. A room with none keeps it for <see cref="BenchlessGraceTicks"/> (a press that is uninstalled and put up again, or rebuilt, is the same room: nothing is
        /// forgotten, and nobody can steal from it meanwhile) and then loses it, with what was traced to it - at once when the room has no cells left: the traced things are contraband again.
        /// </summary>
        internal static void SyncPools(ToolLedgers book, Map map, PrisonMap pm, int now)
        {
            foreach (PrisonZoneDef zone in Zones)
            {
                int benches = BenchesOf(map, pm, zone).Count;
                ToolPool pool = book.PoolOf(zone);
                if (benches == 0)
                {
                    if (pool == null) continue;
                    if (pool.benchlessSinceTick < 0) pool.benchlessSinceTick = now;
                    if (now - pool.benchlessSinceTick >= BenchlessGraceTicks || !pm.zones.CellsOf(zone).Any()) Retire(book, pool);
                    continue;
                }
                if (pool != null) pool.benchlessSinceTick = -1;
                if (pool == null) PoolHere(map, zone);
                else if (pool.benches != benches)
                {
                    pool.owned = Mathf.Max(0, pool.owned + (benches - pool.benches) * PerBench(zone));
                    pool.benches = benches;
                }
            }
        }

        /// <summary>The room is gone: its supply goes with what was traced to it. No tool is made and nobody is told.</summary>
        internal static void Retire(ToolLedgers book, ToolPool pool)
        {
            book.traces.RemoveAll(t => t.poolId == pool.id);
            book.pools.Remove(pool);
            book.Reindex();
        }

        private static void StartPeriod(ToolPool pool, int now)
        {
            pool.period++;
            pool.inPeriod = true;
            pool.periodStartTick = now;
            pool.workers.Clear();
            pool.issued = OnHand(pool);
            // (A count nobody came for is not made after the next work has begun: the tools are in use. The warden is told that it was not made - nothing of what it would have found.)
            if (pool.countDue) Say("RP_ToolNotCounted".Translate(pool.zone.label, pool.countDuePeriod));
            pool.countDue = false;
            pool.supervised = pool.pendingSupervised;
            pool.holdUntilTick = pool.pendingLateHours > 0 ? now + pool.pendingLateHours * GenDate.TicksPerHour : -1;
            pool.pendingLateHours = 0;
            pool.pendingSupervised = false;
        }

        private static void EndPeriod(ToolPool pool, int now)
        {
            pool.inPeriod = false;
            pool.supervised = false;
            pool.holdUntilTick = -1;
            // (Counted whether the warden has been asked or not: a room waiting for its answer still has its periods counted.)
            pool.countDue = true;
            pool.countDuePeriod = pool.period;
        }

        // ---- the count -----------------------------------------------------------------------------------------------------------------------

        public static bool AnyDue => Book is { pools.Count: > 0 } book && Active && book.pools.Exists(p => p.countDue);

        /// <summary>Whether this member of staff counts tools: a guard (on the warden's work) or a workman (on the hauling).</summary>
        public static bool Counts(Pawn pawn, WorkTypeDef workType) =>
            workType == WorkTypeDefOf.Warden ? GuardUtility.IsGuard(pawn) : pawn.GetComp<CompStaff>()?.role == RPStaffRoleDefOf.RP_Workman;

        public static Job JobFor(Pawn pawn)
        {
            ToolLedgers book = Book;
            if (book == null || !Active || pawn.Map is not { } map) return null;
            PrisonMap pm = PrisonMap.Of(map);
            foreach (ToolPool pool in book.pools)
            {
                if (!pool.countDue) continue;
                foreach (Thing bench in BenchesOf(map, pm, pool.zone))
                {
                    if (!pawn.CanReserveAndReach(bench, PathEndMode.Touch, Danger.Some)) continue;
                    Job job = JobMaker.MakeJob(RPToolDefOf.RP_CountTools, bench);
                    job.count = pool.id;
                    return job;
                }
            }
            return null;
        }

        public static bool StillDue(int poolId) => Active && Book?.PoolById(poolId) is { countDue: true };

        /// <summary>
        /// The tools are counted back in. The staff now know what is short (the traces up to now: what is taken after it is not in their number, see <see cref="ToolPool.countedOrder"/>): a rise is a shortage - a message and, unless the
        /// warden is already asked (the letter waits for the answer, and reads its numbers from here), a letter, with the room and the period and never a name. A count that finds nothing new says nothing.
        /// </summary>
        public static void Counted(ToolPool pool, Pawn counter)
        {
            ToolLedgers book = Book;
            if (book == null || !book.pools.Contains(pool)) return;
            int now = Find.TickManager.TicksGame;
            int actual = Missing(pool);
            int fresh = actual - pool.knownMissing;
            pool.countDue = false;
            pool.countedPeriod = pool.countDuePeriod;
            pool.countedTick = now;
            pool.countedIssued = pool.issued;
            // (What was issued when the work began, less what is new short: a shortage carried over from an earlier count is not this period's.)
            pool.countedReturned = Mathf.Max(0, pool.issued - Mathf.Max(0, fresh));
            pool.knownMissing = actual;
            pool.countedOrder = book.nextOrder;
            if (fresh <= 0) return;
            // (The crews of the periods the letter is about: the first one's, and while it waits each later one's.)
            if (pool.asked)
            {
                foreach (int id in pool.workers)
                    if (!pool.countedWorkers.Contains(id)) pool.countedWorkers.Add(id);
            }
            else pool.countedWorkers = new List<int>(pool.workers);
            Map map = counter?.Map ?? Find.AnyPlayerHomeMap;
            string inAll = actual > fresh ? " " + "RP_ToolShortInAll".Translate(actual).Resolve() : "";
            Say("RP_ToolShort".Translate(pool.zone.label, pool.countedPeriod, pool.countedIssued, pool.countedReturned, fresh, inAll), BenchesOf(map, PrisonMap.Of(map), pool.zone).FirstOrDefault() is { } bench ? new LookTargets(bench) : null);
            if (!pool.asked) Ask(pool, map, fresh, inAll);
        }

        private static void Ask(ToolPool pool, Map map, int fresh, string inAll)
        {
            float cut = 1f - OutputFactor(pool.zone);
            TaggedString text = "RP_ToolCountText".Translate(pool.zone.label, pool.countedPeriod, pool.countedIssued, pool.countedReturned, fresh, cut.ToStringPercent(),
                Money.Format(ReplaceCost), RecountLateHours, SearchLateHours, inAll);
            Heard?.Add(text.Resolve());
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_ToolCount", "RP_ToolCountLabel".Translate(pool.zone.label), text, map,
                BenchesOf(map, PrisonMap.Of(map), pool.zone).FirstOrDefault() is { } bench ? new LookTargets(bench) : null);
            letter.amount = pool.id;
            letter.note = pool.zone.label;
            letter.StartTimeout(AnswerTicks);
            pool.asked = true;
            pool.askedTick = Find.TickManager.TicksGame;
            Find.LetterStack.ReceiveLetter(letter);
        }

        // ---- the warden's answer, and the replacing ------------------------------------------------------------------------------------------

        /// <summary>A supervised re-count: the room's next work period starts late and nothing is taken in it.</summary>
        internal static void Recount(ToolPool pool)
        {
            Defer(pool, RecountLateHours, supervised: true);
            Say("RP_ToolRecount".Translate(pool.zone.label, RecountLateHours));
        }

        /// <summary>A search of the crew of the period (they are owed a frisk, as a suspect is), and the next work period starts late.</summary>
        internal static void SearchCrew(ToolPool pool)
        {
            pool.searchIds = new List<int>(pool.countedWorkers);
            pool.searchUntilTick = Find.TickManager.TicksGame + SearchWindowTicks;
            pool.searchFound = 0;
            Defer(pool, SearchLateHours, supervised: false);
            InmateCandidates.Shared.Invalidate();
            if (pool.searchIds.Count == 0) EndSearch(pool);
            else Say("RP_ToolSearch".Translate(pool.zone.label, pool.searchIds.Count, SearchLateHours));
        }

        /// <summary>The room's work is held for a while - from now, in a period that is running, and from the start of the next one when it is not.</summary>
        private static void Defer(ToolPool pool, int hours, bool supervised)
        {
            if (pool.inPeriod)
            {
                pool.holdUntilTick = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
                pool.supervised |= supervised;
            }
            else
            {
                pool.pendingLateHours = hours;
                pool.pendingSupervised = supervised;
            }
        }

        /// <summary>The price of putting the known shortage right.</summary>
        public static int ReplaceTotal(ToolPool pool) => pool.knownMissing * ReplaceCost;

        /// <summary>Intel's button: new tools for the ones the count found short. The tools that are traced stay traced, but are no longer the supply's (see <see cref="ToolTrace.replaced"/>).</summary>
        public static bool Replace(ToolPool pool)
        {
            PrisonGame game = PrisonGame.Instance;
            ToolLedgers book = Book;
            if (game == null || book == null || pool.knownMissing <= 0) return false;
            int n = pool.knownMissing;
            if (!game.ledger.TrySpend(RPDefOf.RP_Supplies, n * ReplaceCost, "RP_ToolReplaceNote".Translate(n, pool.zone.label))) return false;
            // (The ones the staff counted: what is paid for is what they know to be short, not a tool nobody has counted.)
            foreach (ToolTrace t in book.traces)
            {
                if (n == 0) break;
                if (t.poolId != pool.id || t.replaced || t.order >= pool.countedOrder) continue;
                t.replaced = true;
                n--;
            }
            // (A shortage the count knows that no trace stands behind - the thing was destroyed with its owner's file, say - is paid for the same: the supply is whole.)
            pool.knownMissing = 0;
            pool.asked = false;
            return true;
        }

        /// <summary>The room's line for Intel: only what the staff have counted.</summary>
        public static string IntelLine(ToolPool pool)
        {
            string room = pool.zone.LabelCap;
            if (pool.countedTick < 0)
                return "RP_IntelToolsUncounted".Translate(room, pool.owned, pool.countDue ? "  " + "RP_IntelToolsDue".Translate().Resolve() : "").Resolve();
            int hours = Mathf.Max(0, (Find.TickManager.TicksGame - pool.countedTick) / GenDate.TicksPerHour);
            string line = pool.knownMissing > 0
                ? "RP_IntelToolsShort".Translate(room, pool.countedPeriod, hours, pool.knownMissing, pool.owned, ((1f - OutputFactor(pool.zone))).ToStringPercent()).Resolve()
                : "RP_IntelToolsWhole".Translate(room, pool.countedPeriod, hours, pool.owned).Resolve();
            if (pool.countDue) line += "  " + "RP_IntelToolsDue".Translate();
            else if (pool.asked) line += "  " + "RP_IntelToolsAsked".Translate();
            return line;
        }

        /// <summary>Where the last find of the room was made, for the row's tooltip (empty when nothing was found).</summary>
        public static string FoundLine(ToolPool pool) =>
            pool.foundTick < 0 ? "" : "RP_IntelToolsFound".Translate(pool.zone.label, pool.foundPeriod, ("RP_ToolSource_" + pool.foundSource).Translate()).Resolve();

        /// <summary>The whole of it as one comparable string (the saveload plan and the checks).</summary>
        public static string Describe(PrisonGame g)
        {
            ToolLedgers b = g.tools;
            return $"[{string.Join(";", b.pools.OrderBy(p => p.id).Select(p => $"{p.id}:{p.zone?.defName}:{p.benches}:{p.owned}:{p.period}:{p.inPeriod}:{p.periodStartTick}:{string.Join("+", p.workers)}:{p.issued}:{p.countDue}:{p.countDuePeriod}:{p.countedPeriod}:{p.countedTick}:{p.countedIssued}:{p.countedReturned}:{p.countedOrder}:{string.Join("+", p.countedWorkers)}:{p.knownMissing}:{p.asked}:{p.askedTick}:{p.pendingLateHours}:{p.pendingSupervised}:{p.holdUntilTick}:{p.supervised}:{string.Join("+", p.searchIds)}:{p.searchUntilTick}:{p.searchFound}:{p.foundTick}:{p.foundSource}:{p.foundPeriod}:{p.benchlessSinceTick}"))}]"
                + $"/[{string.Join(";", b.traces.OrderBy(t => t.thingId).Select(t => $"{t.thingId}:{t.poolId}:{t.period}:{t.order}:{t.tick}:{t.replaced}"))}]/{b.nextId}/{b.nextOrder}";
        }
    }

    /// <summary>A traced thing never stacks with anything (the stack it joined would lose the tool's id, and a stack that was split would give a second thing the same history): asked by <see cref="ThingOwner"/>, <see cref="GenPlace"/> and <see cref="Thing.TryAbsorbStack"/> alike.</summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.CanStackWith))]
    public static class Patch_Thing_CanStackWith_Traced
    {
        public static void Postfix(Thing __instance, Thing other, ref bool __result)
        {
            if (!__result || PrisonGame.Instance?.tools is not { byThing.Count: > 0 } book) return;
            if (book.byThing.ContainsKey(__instance.thingIDNumber) || other != null && book.byThing.ContainsKey(other.thingIDNumber)) __result = false;
        }
    }

    /// <summary>The warden's answer to a shortage. Unanswered: carry on short.</summary>
    public class ToolCountDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_ToolCount_Recount", "RP_ToolCount_Search", "RP_ToolCount_Short" };

        public const int Recount = 0, Search = 1, CarryOn = 2;

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => CarryOn;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            // (Answered, replaced, or the room gone meanwhile: nothing left to decide.)
            if (ToolCount.Book?.PoolById(l.amount) is not { asked: true, knownMissing: > 0 }) return "RP_ToolCountStale".Translate().ToString();
            return null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (ToolCount.Book?.PoolById(l.amount) is not { asked: true } pool) return;
            pool.asked = false;
            if (pool.knownMissing <= 0) return;
            switch (option)
            {
                case Recount:
                    ToolCount.Recount(pool);
                    break;
                case Search:
                    ToolCount.SearchCrew(pool);
                    break;
            }
        }
    }

    /// <summary>A guard, or a workman, with nothing more pressing counts the tools of a room back in at the end of its work period.</summary>
    public class WorkGiver_CountTools : WorkGiver
    {
        // (The care reserve takes no count: see CareDetail.)
        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            pawn.Map == null || !ToolCount.AnyDue || !ToolCount.Counts(pawn, def.workType) || !forced && CareDetail.IsReserve(pawn);

        public override Job NonScanJob(Pawn pawn) => ToolCount.JobFor(pawn);
    }

    /// <summary>Go to a bench of the room and count the tools back in: a short job.</summary>
    public class JobDriver_CountTools : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            // (The count made by another, or the room gone: nothing to count.)
            this.FailOn(() => !ToolCount.StillDue(job.count));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil count = Toils_General.Wait(ToolCount.CountTicks);
            count.handlingFacing = true;
            count.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(job.targetA);
            count.WithProgressBarToilDelay(TargetIndex.A);
            yield return count;
            Toil done = ToilMaker.MakeToil("ToolsCounted");
            done.initAction = () =>
            {
                if (ToolCount.Book?.PoolById(job.count) is { countDue: true } pool) ToolCount.Counted(pool, pawn);
            };
            yield return done;
        }
    }

    [DefOf]
    public static class RPToolDefOf
    {
        public static JobDef RP_CountTools;

        static RPToolDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPToolDefOf));
    }
}
