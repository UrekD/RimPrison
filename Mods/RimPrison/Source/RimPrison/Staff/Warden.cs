using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPWardenDefOf
    {
        public static StaffRoleDef RP_Warden;
        public static JobDef RP_WardenAddress;
        public static JobDef RP_WardenWalk;
        public static JobDef RP_WardenWord;
        public static JobDef RP_WardenRetire;
        public static ThoughtDef RP_WardenCalmed;
        public static ThoughtDef RP_WardenFirm;
        public static ThoughtDef RP_WardenHadAWord;

        static RPWardenDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPWardenDefOf));
    }

    /// <summary>What a word with an inmate is about (see <see cref="WardenSystem.WordOptions"/>).</summary>
    public enum WardenWord : byte
    {
        /// <summary>A few words: trust, and a little calm.</summary>
        Chat,
        /// <summary>Backing the goal the warden knows they have.</summary>
        Goal,
        /// <summary>Sounding them out as an informant.</summary>
        Informant,
        /// <summary>Telling them where they stand before the parole hearing.</summary>
        Hearing,
        /// <summary>A known gang's known leader: "keep your people quiet", and the terms (see <see cref="GangDeals"/>).</summary>
        GangDeal,
    }

    /// <summary>One thing the warden can say to an inmate now: what it is, and why not (a keyed phrase, resolved) when it cannot be said.</summary>
    public struct WordOption
    {
        public WardenWord kind;
        public string label, desc, disabled;
    }

    /// <summary>
    /// The warden on the floor (gap analysis 4, section 1.5): the player's own presence in the prison, a member of staff with the role <c>RP_Warden</c> who is protected where the state's
    /// chief is (never let go, sent home, put on a shift, retrained or led astray: see <see cref="CompStaff.Protected"/>) and who does three things the warden orders, each with a cooldown
    /// that is saved on the staff record: address the canteen (calm or firm), walk a wing, have a word with an inmate (see <see cref="WardenAbilities"/>). The price is the risk: the one a
    /// riot would like to get hold of (the hostage-takers take the warden before anyone else they can reach, and a crisis over the warden is the worst there is: see
    /// <see cref="HostageCrisis"/>), and a blow to them is a letter. A warden who dies is replaced by the state after <see cref="SuccessionDays"/> days, and a prison that has none (an old
    /// save) is given one by the same path after the same wait.
    /// </summary>
    public static class WardenSystem
    {
        /// <summary>Days the post stays empty before the state names a successor.</summary>
        public const int SuccessionDays = 2;

        /// <summary>The wait in ticks. For a test to shorten (a game that is hours old has not two days to wait on, and the saveload plan's old-save path needs the first midnight after a reload to find the wait over).</summary>
        public static int WaitTicks => WaitOverrideTicks >= 0 ? WaitOverrideTicks : SuccessionDays * GenDate.TicksPerDay;

        internal static int WaitOverrideTicks = -1;

        /// <summary>The abilities' cooldowns in hours: they start when the thing is done, not when it is ordered.</summary>
        public const int AddressCooldownHours = 12, WalkCooldownHours = 6, WordCooldownHours = 3;

        /// <summary>The canteen: calm takes this much off their tension and off their suppression; firm adds suppression, and then tension creeps up this much an hour for <see cref="FirmHours"/> hours.</summary>
        public const float CalmTension = 10f, CalmSuppression = 15f, FirmSuppression = 20f, FirmTensionPerHour = 1.5f;
        public const int FirmHours = 10;

        /// <summary>A blow to the warden is a letter once in this many ticks (a fight is many blows).</summary>
        public const int StruckLetterTicks = 2500;
        /// <summary>The wing hears of it: those who trust the warden take it hard (tension up this much).</summary>
        public const float StruckTrustedTension = 6f;

        public static bool IsWarden(Pawn p) => p?.GetComp<CompStaff>() is { role: { warden: true } };

        /// <summary>The warden in post on this map, or null (of two, the one named last: a warden who was away while the state named another, and is back, is the old one).</summary>
        public static Pawn InPost(Map map)
        {
            if (map == null) return null;
            Pawn best = null;
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p is { Dead: false, Destroyed: false } && p.Faction == Faction.OfPlayer && IsWarden(p) && (best == null || NamedLater(p, best))) best = p;
            return best;
        }

        private static bool NamedLater(Pawn a, Pawn b)
        {
            (int ha, int hb) = (a.GetComp<CompStaff>().hiredTick, b.GetComp<CompStaff>().hiredTick);
            return ha != hb ? ha > hb : a.thingIDNumber > b.thingIDNumber;
        }

        /// <summary>
        /// A living warden of the player's who is not on this map: with a caravan, in a pod or a shuttle, on another map, or in the world (left, and not yet back). The post is theirs
        /// while they are away, and the state names nobody over them (see <see cref="Daily"/>).
        /// </summary>
        public static Pawn Away(Map map)
        {
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
                if (IsWarden(p) && p.MapHeld != map) return p;
            foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
                if (p.Faction == Faction.OfPlayer && IsWarden(p)) return p;
            return null;
        }

        /// <summary>
        /// A new warden on the floor, on staff and out of the payroll, near <paramref name="near"/> (the prison's start, or the intake). <paramref name="announce"/>: a line in the chronicle
        /// (not at the start: it is the prison's first day, and the warden has always been there).
        /// </summary>
        public static Pawn Appoint(Map map, IntVec3 near, bool announce)
        {
            StaffRoleDef role = RPWardenDefOf.RP_Warden;
            Pawn p = StaffUtility.GenerateCandidate(role);
            if (p == null) return null;
            IntVec3 cell = near.IsValid && near.InBounds(map) && CellFinder.TryFindRandomCellNear(near, map, 4, c => c.Standable(map) && c.GetFirstPawn(map) == null, out IntVec3 found)
                ? found : IntakeSystem.IntakeSpot(map);
            GenSpawn.Spawn(p, cell, map);
            StaffUtility.ApplyRole(p, role, record: false);
            StaffUtility.DressInUniform(p, role);
            if (PrisonGame.Instance != null) PrisonGame.Instance.wardenVacantTick = -1;
            EnsureCot(p);
            if (announce) Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_WardenAppointed".Translate(p.LabelShort), p);
            return p;
        }

        /// <summary>
        /// A bed for the warden, who never goes home and so must sleep here (the staff who go home are the ones who sleep off the floor): an unowned colonist bed if there is one, else a cot
        /// of the prison's own, put in a small roofed room the staff keep to (painted staff only: an office, a guard room; never the staff room, whose breaks are no place for a bed), in a corner
        /// and off the doors. With no staff-only room painted yet, any roofed room outside inmates' zones near the warden. Nothing when there is no such room (a bare plot): they sleep where
        /// they drop until there is. Returns the bed they have.
        /// </summary>
        public static Building_Bed EnsureCot(Pawn warden)
        {
            if (warden?.ownership == null || !warden.Spawned) return null;
            if (warden.ownership.OwnedBed is { } owned) return owned;
            Map map = warden.Map;
            foreach (Building_Bed bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
                if (!bed.ForPrisoners && !bed.Medical && bed.OwnersForReading.Count == 0 && bed.def.building.bed_humanlike && warden.CanReach(bed, PathEndMode.OnCell, Danger.Deadly) && warden.ownership.ClaimBedIfNonMedical(bed)) return bed;
            PrisonMap pm = PrisonMap.Of(map);
            ThingDef def = ThingDefOf.Bed;
            IEnumerable<IntVec3> candidates = pm == null ? new List<IntVec3>() : pm.zones.CellsOf(RPDefOf.RP_StaffOnly).ToList();
            if (!candidates.Any()) candidates = GenRadial.RadialCellsAround(warden.Position, 14f, true).Where(c => c.InBounds(map) && pm?.zones.ZoneAt(c) == null).ToList();
            IntVec3 best = IntVec3.Invalid;
            (int bestSize, int bestWalls) = (int.MaxValue, -1);
            foreach (IntVec3 c in candidates)
            {
                CellRect rect = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                if (!rect.Cells.All(x => x.InBounds(map) && x.Standable(map) && x.Roofed(map) && x.GetEdifice(map) == null && !x.GetThingList(map).Any(t => t.def.category is ThingCategory.Item or ThingCategory.Building) && x.GetRoom(map) is { PsychologicallyOutdoors: false, IsPrisonCell: false })) continue;
                CellRect ring = rect.ExpandedBy(1);
                // (Never in a doorway's way, and the more walls round it the better: a bed in a corner, not in the middle of the floor.)
                if (ring.Cells.Any(x => x.InBounds(map) && x.GetDoor(map) != null)) continue;
                int walls = ring.Cells.Count(x => !rect.Contains(x) && (!x.InBounds(map) || x.Impassable(map))), size = c.GetRoom(map).CellCount;
                if (size < bestSize || size == bestSize && walls > bestWalls)
                {
                    best = c;
                    (bestSize, bestWalls) = (size, walls);
                }
            }
            if (!best.IsValid) return null;
            var cot = (Building_Bed)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            cot.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(cot, best, map, Rot4.North);
            warden.ownership.ClaimBedIfNonMedical(cot);
            return cot;
        }

        /// <summary>
        /// Midnight: a post found empty starts the wait, and once it has run its days the state names a successor (a death, or a prison from before there was a warden to find: the same
        /// path, after the same wait).
        /// </summary>
        public static void Daily(PrisonGame game, Map map)
        {
            DailyPasses++;
            Daily(game, map, Find.TickManager.TicksGame);
        }

        /// <summary>How many times the midnight settlement has run the pass, and the hour's pass has run (a run's own checks say whether either hook went missing).</summary>
        public static int DailyPasses, HourlyPasses;

        /// <summary>The same at a given tick (a check asks about a vacancy that has run for days in a game that is hours old).</summary>
        public static void Daily(PrisonGame game, Map map, int now)
        {
            if (map == null || !map.IsPlayerHome) return;
            if (InPost(map) is { } inPost)
            {
                game.wardenVacantTick = -1;
                StandDownOthers(map, inPost);
                EnsureCot(inPost); // (a cot that burnt down, or a plot that has a roof now)
                if (inPost.Spawned && inPost.needs?.joy is { } joy) joy.CurLevel = Mathf.Max(joy.CurLevel, JoyFloor); // (the rest the staff get at home: the warden never goes)
                return;
            }
            // A warden who is away (a caravan, a pod, the world) has not left the post: no wait, and no one named, or they would come back to a second one.
            if (Away(map) != null)
            {
                game.wardenVacantTick = -1;
                return;
            }
            if (game.wardenVacantTick < 0)
            {
                game.wardenVacantTick = now;
                return;
            }
            if (now - game.wardenVacantTick < WaitTicks) return;
            Pawn successor = Appoint(map, IntakeSystem.IntakeSpot(map), announce: true);
            if (successor == null) return; // (tried again tomorrow)
            Find.LetterStack.ReceiveLetter("RP_WardenNamedLabel".Translate(), "RP_WardenNamedText".Translate(successor.LabelShort), LetterDefOf.PositiveEvent, successor);
        }

        /// <summary>The joy a warden has at least at midnight: staff who go home get it from the evening (see <see cref="StaffCommute"/>), and a warden who never does would sink to a break.</summary>
        public const float JoyFloor = 0.8f;

        /// <summary>
        /// Another warden on the floor (the first was away, or came back after the state named a successor): they are let go, with a line, and walk out. Two wardens would have each three gizmos
        /// with cooldowns of their own, and the prize of a riot would be two.
        /// </summary>
        public static int StandDownOthers(Map map, Pawn inPost)
        {
            int n = 0;
            foreach (Pawn p in StaffCommute.AllStaff(map).ToList())
            {
                if (p == inPost || p is not { Dead: false, Destroyed: false, Spawned: true } || p.Faction != Faction.OfPlayer || !IsWarden(p)) continue;
                CompStaff sc = p.GetComp<CompStaff>();
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_WardenRetired".Translate(p.LabelShort, inPost.LabelShort), p, inPost);
                p.ownership?.UnclaimBed();
                // (Out of the post first, or the dismissal would be refused: it is the warden's.)
                sc.role = null;
                StaffCommute.StaffVersion++;
                p.Notify_DisabledWorkTypesChanged();
                StaffUtility.Fire(p, record: false);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Hourly: the hour before the warden's timetable says sleep, they go to their bed (so that they lie down in it when the hour comes, and do not cross the prison at midnight and lie
        /// down where they stand).
        /// </summary>
        public static void Hourly(Map map, int hour)
        {
            HourlyPasses++;
            Pawn w = InPost(map);
            if (w != null && w.Spawned && RetireHour(w, hour)) Retire(w);
        }

        /// <summary>This is the hour before the warden's timetable says sleep.</summary>
        public static bool RetireHour(Pawn w, int hour) =>
            w.timetable != null && w.timetable.GetAssignment((hour + 1) % 24) == TimeAssignmentDefOf.Sleep && w.timetable.GetAssignment(hour) != TimeAssignmentDefOf.Sleep;

        /// <summary>The warden walks to their bed (a cot is found for them if they have none). False when they cannot just now: held, down, busy with an ability or something that must finish, or already there.</summary>
        public static bool Retire(Pawn w)
        {
            Building_Bed bed = w.ownership?.OwnedBed ?? EnsureCot(w);
            if (bed == null || !bed.Spawned || !w.Spawned || HostageCrisis.IsHeld(w) || w.Downed || w.InMentalState || w.Drafted || DoingAbility(w) || Retiring(w)) return false;
            if (w.CurJob != null && !w.CurJob.def.casualInterruptible) return false;
            if (w.Position.InHorDistOf(bed.Position, 4f) || !w.CanReach(bed, PathEndMode.OnCell, Danger.Deadly)) return false;
            Job job = JobMaker.MakeJob(RPWardenDefOf.RP_WardenRetire, bed.Position);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            // (Nothing they left in the queue comes after it: vanilla puts a stroll back at the head of the queue when a haul is slipped in before it, and the queue outranks the rest giver, so the
            // warden, in bed on the stroke of sleep, got up for the stroll and lay down at the far end of the prison an hour later.)
            w.jobs.ClearQueuedJobs();
            w.jobs.StartJob(job, JobCondition.InterruptForced);
            return true;
        }

        /// <summary>
        /// Every few seconds of game time (the warden's own tick): in the hour before sleep, a warden who could not be sent to bed at its start (a job that must finish, an ability, a draft) is sent as soon as
        /// they can be, not left to wander until the timetable says sleep and then walk to the bed from wherever they are.
        /// </summary>
        public static void Rare(Pawn w)
        {
            if (w.Spawned && RetireHour(w, GenLocalDate.HourOfDay(w))) Retire(w);
        }

        // ---- What the warden can do now ----------------------------------------------------------------------------------

        public static bool Retiring(Pawn p) => p.CurJobDef == RPWardenDefOf.RP_WardenRetire;

        public static bool DoingAbility(Pawn p) => p.CurJobDef == RPWardenDefOf.RP_WardenAddress || p.CurJobDef == RPWardenDefOf.RP_WardenWalk || p.CurJobDef == RPWardenDefOf.RP_WardenWord;

        /// <summary>Hours until the tick (rounded up, at least one).</summary>
        private static string HoursUntil(int tick) => Mathf.Max(1, Mathf.CeilToInt((tick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour)).ToString();

        /// <summary>Why the warden cannot start this ability now (a resolved phrase), or null: held, down, busy with another, or the cooldown is running.</summary>
        public static string Unready(Pawn p, int readyTick)
        {
            if (p == null || !p.Spawned || p.Dead) return "RP_WardenWhy_Away".Translate();
            if (HostageCrisis.IsHeld(p)) return "RP_WardenWhy_Held".Translate();
            if (p.Downed || p.InMentalState) return "RP_WardenWhy_Down".Translate();
            if (DoingAbility(p)) return "RP_WardenWhy_Busy".Translate();
            if (readyTick > Find.TickManager.TicksGame) return "RP_WardenWhy_Cooldown".Translate(HoursUntil(readyTick));
            return null;
        }

        private static void StartCooldown(CompStaff sc, ref int field, int hours) => field = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;

        // ---- Address the canteen -------------------------------------------------------------------------------------------

        /// <summary>The canteens of the map: each a block of painted cells that touch (a second canteen, a yard's, is another).</summary>
        public static List<List<IntVec3>> Canteens(Map map)
        {
            var blocks = new List<List<IntVec3>>();
            List<IntVec3> cells = PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Canteen);
            if (cells == null || cells.Count == 0) return blocks;
            var left = new HashSet<IntVec3>(cells);
            var queue = new Queue<IntVec3>();
            while (left.Count > 0)
            {
                IntVec3 first = left.First();
                left.Remove(first);
                var block = new List<IntVec3> { first };
                queue.Enqueue(first);
                while (queue.Count > 0)
                {
                    IntVec3 c = queue.Dequeue();
                    for (int i = 0; i < 8; i++)
                    {
                        IntVec3 n = c + GenAdj.AdjacentCells[i];
                        if (!left.Remove(n)) continue;
                        block.Add(n);
                        queue.Enqueue(n);
                    }
                }
                blocks.Add(block);
            }
            return blocks;
        }

        /// <summary>The canteen the warden is in (the block of cells they stand on), or the nearest to them.</summary>
        public static List<IntVec3> CanteenOf(Pawn warden)
        {
            List<List<IntVec3>> blocks = Canteens(warden.MapHeld);
            IntVec3 at = warden.PositionHeld;
            return blocks.OrderBy(b => b.Min(c => (c - at).LengthHorizontalSquared)).FirstOrDefault();
        }

        /// <summary>The inmates in <paramref name="canteen"/> (the cells of one) who can hear the warden: awake, on their feet, in no mental state.</summary>
        public static List<Pawn> InCanteen(Map map, List<IntVec3> canteen)
        {
            var heard = new List<Pawn>();
            if (canteen == null || canteen.Count == 0) return heard;
            var cells = new HashSet<IntVec3>(canteen);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate() != null && p.Awake() && !p.Downed && !p.InMentalState && cells.Contains(p.Position)) heard.Add(p);
            return heard;
        }

        /// <summary>
        /// The warden's address, done now: <b>calm</b> takes <see cref="CalmTension"/> off the tension of everyone in the canteen they are in (a second canteen across the prison does not hear it)
        /// and <see cref="CalmSuppression"/> off their suppression; <b>firm</b> puts <see cref="FirmSuppression"/> on it (fewer outbursts boil over) and starts a creep of
        /// <see cref="FirmTensionPerHour"/> tension an hour for <see cref="FirmHours"/> hours. Each remembers the warden for it, a little. Returns how many heard it (none: nothing is done,
        /// and the cooldown is not started).
        /// </summary>
        public static int Address(Pawn warden, bool firm)
        {
            Map map = warden.MapHeld;
            CompStaff sc = warden.GetComp<CompStaff>();
            List<Pawn> heard = InCanteen(map, CanteenOf(warden));
            if (heard.Count == 0 || sc == null) return 0;
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in heard)
            {
                CompInmate c = p.Inmate();
                if (firm)
                {
                    c.suppressed = Mathf.Min(100f, c.suppressed + FirmSuppression);
                    c.wardenFirmUntil = now + FirmHours * GenDate.TicksPerHour;
                    StaffMemories.Note(c, warden, -2f);
                    p.needs?.mood?.thoughts.memories.TryGainMemory(RPWardenDefOf.RP_WardenFirm);
                }
                else
                {
                    c.tension = Mathf.Max(0f, c.tension - CalmTension);
                    c.suppressed = Mathf.Max(0f, c.suppressed - CalmSuppression);
                    StaffMemories.Note(c, warden, 2f);
                    p.needs?.mood?.thoughts.memories.TryGainMemory(RPWardenDefOf.RP_WardenCalmed);
                }
            }
            StartCooldown(sc, ref sc.addressReadyTick, AddressCooldownHours);
            // (The talk on the wings of those who heard it: calm shows a false rumour up and halves a true one, firm makes either stick.)
            Rumours.Notify_Address(map, heard, firm);
            PrisonGame.Instance.stats.wardenAddresses++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, (firm ? "RP_Chr_WardenAddressFirm" : "RP_Chr_WardenAddressCalm").Translate(warden.LabelShort, heard.Count), warden);
            Messages.Message((firm ? "RP_WardenAddressedFirm" : "RP_WardenAddressedCalm").Translate(warden.LabelShort, heard.Count), warden, MessageTypeDefOf.NeutralEvent, historical: false);
            return heard.Count;
        }

        private static (int tick, Pawn warden, IntVec3 from, IntVec3 spot) spotCache = (-1, null, IntVec3.Invalid, IntVec3.Invalid);

        /// <summary>
        /// A cell the warden can stand at to speak: in the canteen nearest to them that has one they can reach, the cell nearest the middle of it. Invalid when there is none. The gizmo asks
        /// every frame while the warden is selected: the answer is kept for the tick (and the cell they are at).
        /// </summary>
        public static IntVec3 CanteenSpot(Pawn warden)
        {
            int now = Find.TickManager.TicksGame;
            IntVec3 at = warden.PositionHeld;
            if (spotCache.tick == now && spotCache.warden == warden && spotCache.from == at) return spotCache.spot;
            Map map = warden.MapHeld;
            IntVec3 best = IntVec3.Invalid;
            foreach (List<IntVec3> block in Canteens(map).OrderBy(b => b.Min(c => (c - at).LengthHorizontalSquared)))
            {
                Vector3 sum = Vector3.zero;
                foreach (IntVec3 c in block) sum += c.ToVector3();
                Vector3 middle = sum / block.Count;
                float bestDist = float.MaxValue;
                foreach (IntVec3 c in block)
                {
                    float d = (c.ToVector3() - middle).sqrMagnitude;
                    if (d >= bestDist || !c.Standable(map) || !warden.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
                    (best, bestDist) = (c, d);
                }
                if (best.IsValid) break;
            }
            spotCache = (now, warden, at, best);
            return best;
        }

        // ---- Walk the wing -------------------------------------------------------------------------------------------------

        public const float WalkRadius = 9f, WalkTrust = 3f, WalkResentment = -4f, WalkCalmBelow = 45f, WalkRestlessAbove = 65f;
        public const int WalkStops = 5, WalkSpacing = 7;
        /// <summary>Of the walk's end: the chance a snitch who was near passes on something.</summary>
        public const float WalkTipChance = 0.5f;

        /// <summary>The wings the warden can walk: painted, with somewhere to stand.</summary>
        public static List<PrisonZoneDef> Wings(Map map)
        {
            var wings = new List<PrisonZoneDef>();
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return wings;
            foreach (PrisonZoneDef z in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                if (z.security != null && pm.zones.CellsOf(z).Count > 0) wings.Add(z);
            return wings;
        }

        /// <summary>
        /// The stops of a walk: up to <see cref="WalkStops"/> cells of the wing the warden can reach, at least <see cref="WalkSpacing"/> apart, in the order a walk takes them (the first the
        /// nearest, then the nearest of what is left). Corridors before the cells themselves, when the wing has any.
        /// </summary>
        public static List<IntVec3> WalkPlan(Pawn warden, PrisonZoneDef wing)
        {
            Map map = warden.MapHeld;
            PrisonMap pm = PrisonMap.Of(map);
            var stops = new List<IntVec3>();
            if (pm == null) return stops;
            List<IntVec3> cells = pm.zones.CellsOf(wing).Where(c => c.Standable(map) && warden.CanReach(c, PathEndMode.OnCell, Danger.Deadly)).ToList();
            List<IntVec3> open = cells.Where(c => c.GetRoom(map) is { IsPrisonCell: false }).ToList();
            if (open.Count > 0) cells = open;
            if (cells.Count == 0) return stops;
            cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            // Far apart first (each next stop the one furthest from those taken), then walked in the order of the nearest next.
            var chosen = new List<IntVec3> { cells.OrderBy(c => (c - warden.Position).LengthHorizontalSquared).First() };
            while (chosen.Count < WalkStops)
            {
                IntVec3 next = IntVec3.Invalid;
                float best = WalkSpacing * WalkSpacing - 0.5f;
                foreach (IntVec3 c in cells)
                {
                    float nearest = chosen.Min(x => (x - c).LengthHorizontalSquared);
                    if (nearest > best)
                    {
                        best = nearest;
                        next = c;
                    }
                }
                if (!next.IsValid) break;
                chosen.Add(next);
            }
            IntVec3 at = warden.Position;
            while (chosen.Count > 0)
            {
                IntVec3 nearest = chosen.OrderBy(c => (c - at).LengthHorizontalSquared).First();
                chosen.Remove(nearest);
                stops.Add(nearest);
                at = nearest;
            }
            return stops;
        }

        /// <summary>The inmate belongs to the wing: they are in it now, or their bed is.</summary>
        public static bool InWing(Pawn inmate, PrisonZoneDef wing, PrisonMap pm)
        {
            if (pm.zones.ZoneAt(inmate.Position) == wing) return true;
            return inmate.ownership?.OwnedBed is { Spawned: true } bed && pm.zones.ZoneAt(bed.Position) == wing;
        }

        /// <summary>The inmates of the wing within earshot of the warden now: awake, on their feet, in no mental state.</summary>
        public static List<Pawn> NearOnWing(Pawn warden, PrisonZoneDef wing)
        {
            var near = new List<Pawn>();
            Map map = warden.MapHeld;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || !warden.Spawned) return near;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate() != null && p.Awake() && !p.Downed && !p.InMentalState && p.Position.InHorDistOf(warden.Position, WalkRadius) && InWing(p, wing, pm)) near.Add(p);
            return near;
        }

        /// <summary>
        /// What the warden's walk past one inmate does: one who is calm (tension under <see cref="WalkCalmBelow"/>) takes it as a sign they are looked after and trusts the warden a little more;
        /// one who is wound up (over <see cref="WalkRestlessAbove"/>) takes it as being watched and resents it. In between nothing changes. One who asked to see the warden is answered instead (see
        /// <see cref="WardenAsks.Answer"/>: twice the trust, never resented). Returns +1, -1 or 0.
        /// </summary>
        public static int Meet(Pawn warden, Pawn inmate)
        {
            CompInmate c = inmate.Inmate();
            if (c == null) return 0;
            if (WardenAsks.Answer(warden, inmate, byWalk: true)) return 1;
            if (c.tension >= WalkRestlessAbove)
            {
                StaffMemories.Note(c, warden, WalkResentment);
                return -1;
            }
            if (c.tension <= WalkCalmBelow)
            {
                StaffMemories.Note(c, warden, WalkTrust);
                return 1;
            }
            return 0;
        }

        /// <summary>The walk's cooldown starts (at each stop, and again at the end): a walk broken off partway has still been done, and the trust it earned is not to be had again at once.</summary>
        public static void StartWalkCooldown(Pawn warden)
        {
            if (warden.GetComp<CompStaff>() is { } sc) StartCooldown(sc, ref sc.walkReadyTick, WalkCooldownHours);
        }

        /// <summary>
        /// The end of a walk: of the inmates the warden met, a snitch among them may pass on something in person (the same tip as the daily pass: <see cref="InformantSystem.GiveTip"/>) and
        /// anyone whose complaint is filed and not yet on the desk says it to their face (<see cref="ComplaintSystem.RaiseInPerson"/>). Starts the cooldown. Returns (tipped, complaints).
        /// </summary>
        public static (bool tipped, int complaints) FinishWalk(Pawn warden, List<Pawn> met, int warmer, int resentful, PrisonZoneDef wing)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = warden.MapHeld;
            CompStaff sc = warden.GetComp<CompStaff>();
            if (sc != null) StartCooldown(sc, ref sc.walkReadyTick, WalkCooldownHours);
            game.stats.wardenWalks++;
            bool tipped = false;
            foreach (Pawn p in met.InRandomOrder())
            {
                if (p.Inmate() is not { } c || !c.activeFlag || !c.HasReputation(ReputationDefOf.RP_Snitch) || c.snitchExposed || c.informantQuit) continue;
                if (Rand.Chance(WalkTipChance) && InformantSystem.GiveTip(p, c))
                {
                    tipped = true;
                    break;
                }
            }
            int complaints = 0;
            foreach (Pawn p in met)
                if (ComplaintSystem.RaiseInPerson(game, map, p)) complaints++;
            // (Asked about the talk on the wing, those met say who it came from.)
            Rumours.Notify_Walked(map, wing?.security, met);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_WardenWalked".Translate(warden.LabelShort, wing.label, met.Count), warden);
            Messages.Message("RP_WardenWalked".Translate(warden.LabelShort, wing.label, met.Count, warmer, resentful), warden, MessageTypeDefOf.NeutralEvent, historical: false);
            return (tipped, complaints);
        }

        // ---- A word with an inmate -----------------------------------------------------------------------------------------

        /// <summary>The dice of a word's roll, for a test to settle (null: the dice): that an inmate agrees to inform, that a grudge is talked down.</summary>
        public static bool? ForcedRoll;

        public static bool NeedsAProtector(CompInmate c) => c.fear >= 40f || FearSystem.Vulnerable(c) || c.cash < 5 && c.tension >= 40f;

        /// <summary>Whether this inmate would inform for the warden: a base chance, more for one who is afraid, broke and wound up, or who trusts the warden; less for a gang's own; never a gang's leader.</summary>
        public static float InformantChance(Pawn warden, CompInmate c)
        {
            if (c.gangLeader) return 0f;
            float chance = 0.15f + (NeedsAProtector(c) ? 0.35f : 0f) + 0.4f * Mathf.Clamp(StaffMemories.ScoreFor(c, warden) / 50f, -1f, 1f);
            if (c.gang != null) chance *= 0.5f;
            return Mathf.Clamp(chance, 0.03f, 0.9f);
        }

        /// <summary>Whether a hearing is near for this inmate (within three days, or due): what the warden can tell them of where they stand.</summary>
        public static bool HearingNear(CompInmate c) =>
            c.activeFlag && !c.IsLifer && c.sentenceDays > 0 && c.admittedTick >= 0 && ParoleSystem.MayBeParoled(c) && DeathRowSystem.Ext(c) == null
            && WantWorker_Parole.HearingTick(c) - Find.TickManager.TicksGame <= 3 * GenDate.TicksPerDay;

        /// <summary>
        /// What the warden can say to this inmate: always a few words; their goal if the warden knows of one and has not backed it; informant if they are not one the warden knows of; and
        /// where they stand if a hearing is near. Each with why not when it cannot be said (they were spoken to today).
        /// </summary>
        public static List<WordOption> WordOptions(Pawn warden, Pawn inmate)
        {
            var options = new List<WordOption>();
            CompInmate c = inmate.Inmate();
            if (c == null) return options;
            string today = Find.TickManager.TicksGame - c.wardenWordTick < GenDate.TicksPerDay ? "RP_WardenWhy_SpokenToday".Translate(inmate.LabelShort).Resolve() : null;
            // (One who asked to see the warden since their last word may be spoken to again, for what answers a request - a few words, their goal, where they stand: see WardenAsks. Not to be sounded out again, nor the
            // leader's terms: a pitch turned down is not tried again the same day because they asked about something else.)
            string heard = today != null && WardenAsks.AskedSince(inmate, c.wardenWordTick) ? null : today;
            options.Add(new WordOption { kind = WardenWord.Chat, label = "RP_WardenWord_Chat".Translate(), desc = "RP_WardenWord_ChatDesc".Translate(), disabled = heard });
            // (Only a goal the warden knows of: one that is secret, or would name a gang nobody has found, is not on offer.)
            if (Wants.Shown(c) is { Resolved: false, backed: false } w)
                options.Add(new WordOption { kind = WardenWord.Goal, label = "RP_WardenWord_Goal".Translate(Wants.Title(w)), desc = "RP_WardenWord_GoalDesc".Translate(), disabled = heard });
            if (!c.KnownReputation(ReputationDefOf.RP_Snitch) && !c.informantPaid)
                options.Add(new WordOption { kind = WardenWord.Informant, label = "RP_WardenWord_Informant".Translate(Money.Format(InformantDecision.DailyPay)), desc = "RP_WardenWord_InformantDesc".Translate(), disabled = today });
            if (HearingNear(c))
                options.Add(new WordOption { kind = WardenWord.Hearing, label = "RP_WardenWord_Hearing".Translate(), desc = "RP_WardenWord_HearingDesc".Translate(), disabled = heard });
            // (Only a known gang's known leader gets the line at all: it would give the leader away.)
            if (GangDeals.LineFor(inmate, c, out string dealWhy))
                options.Add(new WordOption { kind = WardenWord.GangDeal, label = "RP_WardenWord_GangDeal".Translate(), desc = "RP_WardenWord_GangDealDesc".Translate(GangDeals.DealDays, GangDeals.InspectionPoints), disabled = today ?? dealWhy });
            return options;
        }

        /// <summary>
        /// The word, said now: what it does is the kind's (see <see cref="WordOptions"/>), and the inmate remembers it. Returns the line the warden reads. Starts the cooldown, and the
        /// inmate's day.
        /// </summary>
        public static string Word(Pawn warden, Pawn inmate, WardenWord kind)
        {
            CompInmate c = inmate.Inmate();
            CompStaff sc = warden.GetComp<CompStaff>();
            if (c == null || sc == null) return null;
            // The order was given some hundreds of ticks ago: what it was about is looked at again (spoken to today in the meantime, the goal gone or backed, an informant already, no hearing near).
            if (!WordOptions(warden, inmate).Any(o => o.kind == kind && o.disabled == null))
            {
                Messages.Message("RP_WardenWord_Gone".Translate(warden.LabelShort, inmate.LabelShort), warden, MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            int now = Find.TickManager.TicksGame;
            string line;
            switch (kind)
            {
                case WardenWord.Goal when Wants.Shown(c) is { Resolved: false, backed: false } w:
                    string key = w.def.Worker.Encourage(inmate, c, w, warden);
                    // (A grudge they would not be talked out of is the warden's loss, and their own to bear: it is no word backed.)
                    if (key != Wants.TalkFailedKey)
                    {
                        StaffMemories.Note(c, warden, 4f);
                        c.tension = Mathf.Max(0f, c.tension - 3f);
                        inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPWardenDefOf.RP_WardenHadAWord);
                        Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_WardenBacked".Translate(warden.LabelShort, inmate.LabelShort, Wants.Title(w)), inmate, warden);
                    }
                    line = key.Translate(inmate.LabelShort, Wants.Title(w));
                    break;
                case WardenWord.Informant:
                    line = Sound(warden, inmate, c);
                    break;
                case WardenWord.GangDeal:
                    line = GangDeals.Offer(warden, inmate, c);
                    break;
                case WardenWord.Hearing:
                    c.headsUpTick = now;
                    c.tension = Mathf.Max(0f, c.tension - 6f);
                    StaffMemories.Note(c, warden, 4f);
                    inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPWardenDefOf.RP_WardenHadAWord);
                    List<string> needs = WantWorker_Parole.Needs(c);
                    line = "RP_WardenWord_HearingTold".Translate(inmate.LabelShort, ParoleSystem.ParoleChance(c).ToStringPercent(), needs.Count == 0 ? "RP_Want_Need_Nothing".Translate().ToString() : GenText.ToCommaList(needs, useAnd: true));
                    break;
                default:
                    c.tension = Mathf.Max(0f, c.tension - 5f);
                    StaffMemories.Note(c, warden, 6f);
                    inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPWardenDefOf.RP_WardenHadAWord);
                    line = "RP_WardenWord_ChatTold".Translate(inmate.LabelShort);
                    break;
            }
            c.wardenWordTick = now;
            StartCooldown(sc, ref sc.wordReadyTick, WordCooldownHours);
            PrisonGame.Instance.stats.wardenWords++;
            Messages.Message(line, inmate, MessageTypeDefOf.NeutralEvent, historical: false);
            // (They had asked to see the warden: the word answers it, with more than the word alone.)
            WardenAsks.Answer(warden, inmate, byWalk: false);
            return line;
        }

        /// <summary>The approach: they agree (a wage from now on, the file says so) or they will not be drawn, and think the less of the warden for asking.</summary>
        private static string Sound(Pawn warden, Pawn inmate, CompInmate c)
        {
            // (One who is an informant already, unknown to the warden, simply says yes: the asking is what finds them.)
            bool agrees = c.HasReputation(ReputationDefOf.RP_Snitch) || (ForcedRoll ?? Rand.Chance(InformantChance(warden, c)));
            if (agrees)
            {
                InformantDecision.Recruit(inmate, c, paid: true);
                StaffMemories.Note(c, warden, 3f);
                return "RP_WardenWord_InformantYes".Translate(inmate.LabelShort, Money.Format(InformantDecision.DailyPay));
            }
            StaffMemories.Note(c, warden, -4f);
            c.tension = Mathf.Min(100f, c.tension + 3f);
            return "RP_WardenWord_InformantNo".Translate(inmate.LabelShort);
        }

        // ---- The risk --------------------------------------------------------------------------------------------------------

        /// <summary>
        /// An inmate's blow landed on the warden (called from the damage patch): a line and a letter, once in <see cref="StruckLetterTicks"/>. The wing hears of it: those who trust the warden
        /// take it hard.
        /// </summary>
        public static void Notify_Struck(Pawn warden, Pawn attacker, bool fresh)
        {
            // (Not while they are held: the crisis's own letter says the blow, and a deadline's blow would be told twice.)
            if (!fresh || attacker == null || HostageCrisis.IsHeld(warden)) return;
            PrisonGame game = PrisonGame.Instance;
            Map map = warden.MapHeld ?? Find.AnyPlayerHomeMap;
            if (game == null || map == null) return;
            game.stats.wardenStruck++;
            PrisonMap pm = PrisonMap.Of(map);
            PrisonZoneDef wing = pm?.zones.ZoneAt(warden.PositionHeld) is { security: not null } z ? z : null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p != attacker && p.Inmate() is { } c && StaffMemories.ScoreFor(c, warden) >= StaffMemories.TrustedAt && Hears(p, warden, wing, pm)) c.tension = Mathf.Min(100f, c.tension + StruckTrustedTension);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_WardenStruck".Translate(warden.LabelShort, attacker.LabelShort), warden, attacker);
            Find.LetterStack.ReceiveLetter("RP_WardenStruckLabel".Translate(warden.LabelShort), "RP_WardenStruckText".Translate(warden.LabelShort, attacker.LabelShort), LetterDefOf.ThreatSmall, new LookTargets(warden, attacker));
        }

        /// <summary>The blow was in a wing: that wing hears of it. Anywhere else, those within <see cref="StruckEarshot"/> cells.</summary>
        private static bool Hears(Pawn inmate, Pawn warden, PrisonZoneDef wing, PrisonMap pm) =>
            wing != null && pm != null ? InWing(inmate, wing, pm) : inmate.Position.InHorDistOf(warden.PositionHeld, StruckEarshot);

        public const float StruckEarshot = 25f;

        /// <summary>The warden died: the post is empty and the state will name another (see <see cref="Daily"/>).</summary>
        public static void Notify_Died(Pawn warden)
        {
            if (PrisonGame.Instance is { } game && game.wardenVacantTick < 0) game.wardenVacantTick = Find.TickManager.TicksGame;
        }

        /// <summary>For the saveload snapshot: the vacancy, the cooldowns of the warden (and of anyone who still carries some: a pawn whose post was taken away), and what the inmates carry of the warden's words.</summary>
        public static string Describe(PrisonGame game, Map map)
        {
            string staff = string.Join(",", map.mapPawns.FreeColonists.OrderBy(p => p.thingIDNumber)
                .Where(p => p.GetComp<CompStaff>() is { } sc && (sc.role is { warden: true } || sc.addressReadyTick != -1 || sc.walkReadyTick != -1 || sc.wordReadyTick != -1))
                .Select(p => $"{p.thingIDNumber}:{p.GetComp<CompStaff>().addressReadyTick}:{p.GetComp<CompStaff>().walkReadyTick}:{p.GetComp<CompStaff>().wordReadyTick}:{IsWarden(p)}"));
            string inmates = string.Join(",", map.mapPawns.PrisonersOfColonySpawned.OrderBy(p => p.thingIDNumber)
                .Where(p => p.Inmate() is { } c && (c.wardenWordTick > -999999 || c.headsUpTick >= 0 || c.wardenFirmUntil >= 0))
                .Select(p => $"{p.thingIDNumber}:{p.Inmate().wardenWordTick}:{p.Inmate().headsUpTick}:{p.Inmate().wardenFirmUntil}"));
            return $"{game.wardenVacantTick}/{staff}/{game.stats.wardenAddresses}:{game.stats.wardenWalks}:{game.stats.wardenWords}:{game.stats.wardenStruck}/{inmates}";
        }
    }
    /// <summary>
    /// Vanilla's "jealous of X's bedroom" would fall on every member of staff the moment the warden has a bed: the staff go home and own no room, so a room of any impressiveness is better than
    /// theirs, and it was the last straw for an insulting spree and a berserk in the first run with a cot. The warden's bedroom is the post's, not a colonist's to envy: it is left out of the comparison
    /// (the vanilla worker's own, without the warden).
    /// </summary>
    [HarmonyPatch(typeof(ThoughtWorker_BedroomJealous), "CurrentStateInternal")]
    public static class Patch_ThoughtWorker_BedroomJealous_Warden
    {
        public static bool Prefix(Pawn p, ref ThoughtState __result)
        {
            if (!p.IsColonist)
            {
                __result = false;
                return false;
            }
            Pawn envied = Envied(p, x => x.ownership.OwnedRoom);
            __result = envied != null ? ThoughtState.ActiveWithReason(envied.LabelShort) : ThoughtState.Inactive;
            return false;
        }

        /// <summary>The colonist whose bedroom is far better than <paramref name="p"/>'s (vanilla's rule, 10% or more, the best of them), the warden left out. <paramref name="roomOf"/>: the room a pawn owns.</summary>
        public static Pawn Envied(Pawn p, System.Func<Pawn, Room> roomOf)
        {
            Room ownedRoom = roomOf(p);
            float own = ownedRoom?.GetStat(RoomStatDefOf.Impressiveness) ?? 0f;
            List<Pawn> pawns = p.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            Pawn envied = null;
            float enviedStat = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn other = pawns[i];
                if (other.HostFaction != null || !p.RaceProps.Humanlike || other.ownership == null || WardenSystem.IsWarden(other)) continue;
                Room room = roomOf(other);
                if (room == null || room == ownedRoom) continue;
                float stat = room.GetStat(RoomStatDefOf.Impressiveness);
                if (stat - own >= Mathf.Abs(own * 0.1f) && (envied == null || stat > enviedStat))
                {
                    envied = other;
                    enviedStat = stat;
                }
            }
            return envied;
        }
    }
}
