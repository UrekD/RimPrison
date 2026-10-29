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
    public static class RPHostageDefOf
    {
        public static CrimeDef RP_Crime_CapitalMurder;
        public static CrimeDef RP_Crime_Kidnapping;
        public static StoryArcDef RP_Arc_Hostage;
        public static MentalStateDef RP_BeingHeld;
        public static HediffDef RP_Hostage;
        public static ThoughtDef RP_TakenHostage;
        public static JobDef RP_HeldHostage;
        public static JobDef RP_LeaveHeld;
        public static MisconductDef RP_HostageTaking;

        static RPHostageDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPHostageDefOf));
    }

    /// <summary>
    /// A member of staff held by rioters: still a real colonist on the map (nothing is despawned), kept in place by this state
    /// (a held job, no work, no commute) until the crisis lets them go. Only <see cref="HostageCrisis"/> ends it, never the clock.
    /// </summary>
    public class MentalState_BeingHeld : MentalState
    {
        public override void MentalStateTick(int delta)
        {
        }

        public override bool AllowRestingInBed => false;

        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;

        public override string InspectLine => causedByPawn != null ? "RP_HeldBy".Translate(causedByPawn.LabelShort).ToString() : base.InspectLine;
    }

    public class JobGiver_Hostage : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Job job = JobMaker.MakeJob(RPHostageDefOf.RP_HeldHostage);
            job.expiryInterval = 600;
            return job;
        }
    }

    /// <summary>Stands where they were taken, hands tied.</summary>
    public class JobDriver_HeldHostage : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddEndCondition(() => HostageCrisis.IsHeld(pawn) ? JobCondition.Ongoing : JobCondition.Succeeded);
            Toil hold = Toils_General.Wait(600);
            hold.handlingFacing = true;
            yield return hold;
        }
    }

    /// <summary>
    /// A member of staff caught inside the held area walks to the inner side of a border door and is let through it (the doors refuse every
    /// member of staff, so no path leads out: see <see cref="HostageHold.LetStaffOut"/>).
    /// </summary>
    public class JobDriver_LeaveHeld : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil through = ToilMaker.MakeToil("LeaveHeld");
            through.initAction = () =>
            {
                if (job.targetC.Thing is Building_Door door && door.Spawned) door.StartManualOpenBy(pawn);
                pawn.pather.StopDead();
                pawn.Position = job.targetB.Cell;
                pawn.Notify_Teleported(endCurrentJob: false);
            };
            through.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return through;
        }
    }

    /// <summary>What a hostage crisis keeps in <see cref="StoryArc.note"/>: where it is, how much is held, what started it, what they want.</summary>
    public struct HostageNote
    {
        public IntVec3 anchor;
        public bool wing;
        public string trigger;
        public NeedDef need;

        public static string Make(IntVec3 anchor, bool wing, string trigger, NeedDef need) => $"{anchor.x},{anchor.z}|{(wing ? 1 : 0)}|{trigger}|{need?.defName}";

        public static HostageNote Of(StoryArc arc)
        {
            var note = new HostageNote { anchor = IntVec3.Invalid };
            string[] parts = arc.note?.Split('|');
            if (parts == null || parts.Length < 4) return note;
            string[] xz = parts[0].Split(',');
            if (xz.Length == 2 && int.TryParse(xz[0], out int x) && int.TryParse(xz[1], out int z)) note.anchor = new IntVec3(x, 0, z);
            note.wing = parts[1] == "1";
            note.trigger = parts[2];
            note.need = DefDatabase<NeedDef>.GetNamedSilentFail(parts[3]);
            return note;
        }
    }

    /// <summary>
    /// The room (or the wing) the rioters hold. The door pass marks every door on its border: staff may not come in through
    /// one (they may still leave it), and the map shows the zone as held. Recomputed with every door pass, never per path query.
    /// </summary>
    public static class HostageHold
    {
        public const int MaxRooms = 8, MaxCells = 700;
        public static readonly Color HeldColor = new Color(0.9f, 0.15f, 0.1f);

        /// <summary>The held rooms of the crisis on this map, or null when nothing is held.</summary>
        public static HashSet<Room> Rooms(Map map)
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null || arcs.Count == 0) return null;
            HashSet<Room> held = null;
            for (int i = 0; i < arcs.Count; i++)
            {
                StoryArc arc = arcs[i];
                if (arc.done || arc.def != RPHostageDefOf.RP_Arc_Hostage || arc.A?.MapHeld != map) continue;
                held = Of(arc, map, held);
            }
            return held;
        }

        /// <summary>The rooms one crisis holds (added to <paramref name="held"/> when given), done or not: the ending needs them after the arc is marked done.</summary>
        public static HashSet<Room> Of(StoryArc arc, Map map, HashSet<Room> held = null)
        {
            HostageNote note = HostageNote.Of(arc);
            if (!note.anchor.IsValid || HostageCrisis.HoldRoom(note.anchor, map) is not { } room) return held;
            held ??= new HashSet<Room>();
            held.Add(room);
            if (note.wing) Expand(map, room, note.anchor, held);
            return held;
        }

        /// <summary>The wing: the rooms of the same zone that the held room's doors lead to (and theirs), within a limit.</summary>
        private static void Expand(Map map, Room start, IntVec3 anchor, HashSet<Room> held)
        {
            PrisonZoneDef zone = PrisonMap.Of(map)?.zones.ZoneAt(anchor);
            if (zone == null || zone.staffOnly) return;
            PrisonMap pm = PrisonMap.Of(map);
            int cells = start.CellCount;
            var queue = new Queue<Room>();
            queue.Enqueue(start);
            while (queue.Count > 0 && held.Count < MaxRooms)
            {
                Room room = queue.Dequeue();
                foreach (Thing t in room.ContainedAndAdjacentThings)
                {
                    if (t is not Building_Door door) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        IntVec3 c = door.Position + GenAdj.CardinalDirections[k];
                        if (!c.InBounds(map) || !c.Walkable(map) || HostageCrisis.HoldRoom(c, map) is not { } next || held.Contains(next) || pm.zones.ZoneAt(c) != zone) continue;
                        if (cells + next.CellCount > MaxCells || held.Count >= MaxRooms) continue;
                        cells += next.CellCount;
                        held.Add(next);
                        queue.Enqueue(next);
                    }
                }
            }
        }

        /// <summary>A door with held space on one side and something else on the other.</summary>
        public static bool IsBorder(Building_Door door, HashSet<Room> held, Map map)
        {
            bool inside = false, outside = false;
            for (int k = 0; k < 4; k++)
            {
                IntVec3 c = door.Position + GenAdj.CardinalDirections[k];
                if (!c.InBounds(map) || !c.Walkable(map) || c.GetRoom(map) is not { } room) continue;
                if (held.Contains(room)) inside = true;
                else outside = true;
            }
            return inside && outside;
        }

        /// <summary>
        /// Where a hostage-taker who strayed goes back to: the free held cell nearest the hostage (never the hostage's own cell, which is
        /// occupied: a goto there ends at once and is asked for again in the same tick), or invalid when there is none to reach.
        /// </summary>
        public static IntVec3 BackSpot(Pawn taker, IntVec3 anchor)
        {
            Map map = taker.Map;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 5f, true))
                if (c.InBounds(map) && c.Standable(map) && InHeld(map, c) && c.GetFirstPawn(map) == null && taker.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return c;
            return IntVec3.Invalid;
        }

        /// <summary>
        /// Staff caught inside the held area (a guard on patrol, a cook) are sent out through the nearest border door they can walk to. The doors
        /// refuse every member of staff, inside or out: a verdict that depended on where the pawn stands would go stale in vanilla's reachability
        /// cache, which keeps one answer per pawn and pair of districts and is only cleared when a door's state flips, not when a pawn crosses it.
        /// So the way out is a job that opens the door and puts them on its far side. Called with every door pass.
        /// </summary>
        public static void LetStaffOut(Map map, PrisonMap pm)
        {
            if (pm.heldCells == null) return;
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
            {
                if (s.Downed || s.Drafted || s.InMentalState || s.CurJobDef == RPHostageDefOf.RP_LeaveHeld || !InHeld(map, s.Position)
                    || s.GetComp<CompStaff>() is not { role: not null, passLocked: false } || HostageCrisis.IsHeld(s)) continue;
                CompPrisonDoor best = null;
                IntVec3 bestInside = IntVec3.Invalid, bestOutside = IntVec3.Invalid;
                float bestDist = float.MaxValue;
                foreach (CompPrisonDoor d in pm.Doors)
                {
                    if (d.heldCells == null || !d.Door.Spawned) continue;
                    IntVec3 inside = IntVec3.Invalid, outside = IntVec3.Invalid;
                    for (int k = 0; k < 4; k++)
                    {
                        IntVec3 c = d.Door.Position + GenAdj.CardinalDirections[k];
                        if (!c.InBounds(map) || !c.Walkable(map)) continue;
                        if (InHeld(map, c)) inside = c;
                        else outside = c;
                    }
                    float dist = (inside - s.Position).LengthHorizontalSquared;
                    if (!inside.IsValid || !outside.IsValid || dist >= bestDist || !s.CanReach(inside, PathEndMode.OnCell, Danger.Deadly)) continue;
                    (best, bestInside, bestOutside, bestDist) = (d, inside, outside, dist);
                }
                if (best == null) continue;
                Job job = JobMaker.MakeJob(RPHostageDefOf.RP_LeaveHeld, bestInside, bestOutside, best.Door);
                s.jobs.StartJob(job, JobCondition.InterruptForced);
            }
        }

        public static bool InHeld(Map map, IntVec3 c) => PrisonMap.Of(map)?.heldCells is { } cells && cells.Contains(map.cellIndices.CellToIndex(c));

        /// <summary>Cutting power or water: the held buildings go dark (see <see cref="UtilitySystem"/>).</summary>
        public static bool PowerCut(Map map)
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null) return false;
            for (int i = 0; i < arcs.Count; i++)
                if (!arcs[i].done && arcs[i].def == RPHostageDefOf.RP_Arc_Hostage && arcs[i].A?.MapHeld == map && arcs[i].markTick != HostageCrisis.NoMark) return true;
            return false;
        }
    }

    /// <summary>How a crisis ended. Killed and Died are a hostage lost outside the crisis' own path (a blow from a taker or anyone else; a wound): with and without a known killer.</summary>
    public enum HostageHow { Talked, Conceded, GaveUp, Stormed, Deadline, Gone, Killed, Died }

    public enum HostageFate { Freed, Hurt, Dead }

    /// <summary>
    /// The hostage crisis, in one place: how it starts (a ringleader in a riot, or an assault in the workshop or kitchen, who has
    /// a member of staff in reach), who is held and by whom, what it does to the staff member, and how it ends (see
    /// <see cref="HostageArc"/> for the warden's side).
    /// </summary>
    public static class HostageCrisis
    {
        /// <summary>Chances, per ringleader with a member of staff in reach, that the riot or the assault becomes a hostage-taking.</summary>
        public const float RiotChance = 0.15f, AssaultChance = 0.05f;
        /// <summary>The chance each time the ringleader of a riot, going for staff, has one within reach (they get at the guards that come for them).</summary>
        public const float EngageChance = 0.05f;
        public const float SeizeRadius = 7f;
        public const int Standoff = 1, Deadline = 2, Resolved = 3;
        /// <summary>The "not cut" value of <see cref="StoryArc.markTick"/> (any other value is the tick utilities were cut).</summary>
        public const int NoMark = -1;
        public const int StandoffHours = 6, DeadlineHours = 4, MaxRoomCells = 600;
        /// <summary>
        /// The warden is the prize (see <see cref="WardenSystem"/>), and a crisis over them is the worst there is: the hostage-takers seize them before anyone else in reach unless there is one
        /// they hate more, a seizure is this many times as likely, the stand-off and the last deadline are shorter, the talks have less to go on (-0.1), the end is likelier to be a death
        /// (+0.05 in a storm, +0.1 at the deadline) and the prison's name suffers twice as much.
        /// </summary>
        public const float WardenSeizureFactor = 2f, WardenTalkPenalty = 0.1f, WardenStormKill = 0.05f, WardenDeadlineKill = 0.1f, WardenReputationFactor = 2f;
        public const int WardenStandoffHours = 4, WardenDeadlineHours = 3;
        /// <summary>This many rioters (or more) hold the wing and not only the room.</summary>
        public const int WingTakers = 5;
        public const string Riot = "riot", Assault = "assault";

        /// <summary>For tests: decides the seizure's chance roll (null: the dice).</summary>
        public static bool? ForcedRoll;

        public static bool IsHeld(Pawn p) => p?.health?.hediffSet != null && RPHostageDefOf.RP_Hostage != null && p.health.hediffSet.HasHediff(RPHostageDefOf.RP_Hostage);

        /// <summary>The crisis that is running now (one at a time), on <paramref name="map"/> if given.</summary>
        public static StoryArc Running(Map map = null)
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null) return null;
            for (int i = 0; i < arcs.Count; i++)
                if (!arcs[i].done && arcs[i].def == RPHostageDefOf.RP_Arc_Hostage && (map == null || arcs[i].A?.MapHeld == map)) return arcs[i];
            return null;
        }

        /// <summary>This inmate is one of the hostage-takers of the running crisis (the ringleader or a follower).</summary>
        public static bool Holding(Pawn p) => p != null && Running() is { } arc && arc.pawns.IndexOf(p) >= 1;

        public static bool Holding(Pawn p, out StoryArc arc)
        {
            arc = p != null ? Running() : null;
            return arc != null && arc.pawns.IndexOf(p) >= 1;
        }

        /// <summary>The ringleader: the first of the hostage-takers who is still rioting, else the first named.</summary>
        public static Pawn Leader(StoryArc arc)
        {
            for (int i = 1; i < arc.pawns.Count; i++)
                if (arc.pawns[i] is { Dead: false, Destroyed: false } p && p.MentalStateDef == RPMentalStateDefOf.RP_Rioting) return p;
            return arc.pawns.Count > 1 ? arc.pawns[1] : null;
        }

        /// <summary>The hostage-takers who are still at it.</summary>
        public static List<Pawn> Takers(StoryArc arc)
        {
            var list = new List<Pawn>();
            for (int i = 1; i < arc.pawns.Count; i++)
                if (arc.pawns[i] is { Dead: false, Destroyed: false } p && p.MentalStateDef == RPMentalStateDefOf.RP_Rioting) list.Add(p);
            return list;
        }

        /// <summary>A room the rioters can hold: indoors, closed in by doors, not the whole yard.</summary>
        public static Room HoldRoom(IntVec3 c, Map map)
        {
            if (!c.InBounds(map)) return null;
            Room room = c.GetRoom(map);
            return room == null || room.IsDoorway || room.PsychologicallyOutdoors || room.TouchesMapEdge || room.CellCount > MaxRoomCells ? null : room;
        }

        public static bool CanBeTaken(Pawn s) =>
            s.Spawned && s.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 } sc && !s.Downed && !s.InMentalState && !IsHeld(s)
            && !CorruptionSystem.Suspended(sc) && !StaffLives.OnLeave(sc) && !PrisonGame.Instance.story.InArc(s);

        /// <summary>
        /// The member of staff the ringleader seizes: one in the same closed room within a few steps, the one they hate if there is
        /// such a one (<see cref="StaffMemories"/>), else the one they went for (<paramref name="preferred"/>), else the nearest.
        /// </summary>
        public static Pawn Candidate(Pawn leader, CompInmate c, Pawn preferred)
        {
            Map map = leader.Map;
            Room room = leader.GetRoom();
            if (HoldRoom(leader.Position, map) == null) return null;
            var reach = new List<Pawn>();
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
                if (CanBeTaken(s) && s.GetRoom() == room && (s.Position - leader.Position).LengthHorizontalSquared <= SeizeRadius * SeizeRadius
                    && leader.CanReach(s, PathEndMode.Touch, Danger.Deadly)) reach.Add(s);
            if (reach.Count == 0) return null;
            if (StaffMemories.HatedAmong(c, reach) is { } hated) return hated;
            // (The warden is the prize: whoever they went for, the warden in reach is taken.)
            if (reach.Find(WardenSystem.IsWarden) is { } warden) return warden;
            if (preferred != null && reach.Contains(preferred)) return preferred;
            return reach.OrderBy(s => (s.Position - leader.Position).LengthHorizontalSquared).First();
        }

        /// <summary>
        /// A riot or an assault where a ringleader has a member of staff in reach: at a low chance they take them. Returns the crisis
        /// (the warden is asked what to do), or null (nobody in reach, a crisis already running, or the dice said no).
        /// </summary>
        public static StoryArc TryStart(Pawn leader, CompInmate c, IEnumerable<Pawn> takers, string trigger, Pawn preferred = null, float? chance = null)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || leader?.Map is not { IsPlayerHome: true } map || leader.Dead || Running() != null || game.story.InArc(leader)) return null;
            Pawn hostage = Candidate(leader, c, preferred);
            if (hostage == null) return null;
            float odds = chance ?? (trigger == Riot ? RiotChance : AssaultChance);
            if (!(ForcedRoll ?? Rand.Chance(SeizureChance(hostage, odds)))) return null;
            return Seize(map, leader, c, takers, hostage, trigger);
        }

        /// <summary>The chance that a ringleader with <paramref name="hostage"/> in reach takes them: the trigger's own odds, twice that for the warden.</summary>
        public static float SeizureChance(Pawn hostage, float odds) => WardenSystem.IsWarden(hostage) ? Mathf.Min(0.9f, odds * WardenSeizureFactor) : odds;

        /// <summary>Started this riot (a record of it, as the ringleader, within the last day) and is still at it.</summary>
        public static bool IsRingleader(Pawn p)
        {
            if (p.Inmate() is not { } c || p.MentalStateDef != RPMentalStateDefOf.RP_Rioting) return false;
            int now = Find.TickManager.TicksGame;
            foreach (MisconductRecord rec in c.records)
                if (rec.def == RPRiotDefOf.RP_Riot && now - rec.tick < MisconductWorker_Riot.RiotCooldownTicks) return true;
            return false;
        }

        /// <summary>
        /// The ringleader of a riot who has got at a member of staff (the guards come for the rioters, who go for them): at a low chance each time
        /// they do, the nearest one is taken, the rioters around them holding the room.
        /// </summary>
        public static StoryArc TryStartFromFight(Pawn rioter, Pawn staff)
        {
            if (rioter?.Map == null || Running() != null || !IsRingleader(rioter) || !CanBeTaken(staff)) return null;
            var takers = new List<Pawn>();
            foreach (Pawn p in rioter.Map.mapPawns.PrisonersOfColonySpawned)
                if (p.MentalStateDef == RPMentalStateDefOf.RP_Rioting && p.Position.InHorDistOf(rioter.Position, 12f)) takers.Add(p);
            return TryStart(rioter, rioter.Inmate(), takers, Riot, staff, EngageChance);
        }

        /// <summary>An assault in the workshop or the kitchen, where inmates and staff work side by side.</summary>
        public static StoryArc TryStartFromAssault(Pawn assailant, CompInmate c, Pawn target)
        {
            PrisonZoneDef zone = assailant.Map == null ? null : PrisonMap.Of(assailant.Map)?.zones.ZoneAt(assailant.Position);
            if (zone == null || zone != RPLaborDefOf.RP_Workshop && zone != RPLaborDefOf.RP_Kitchen) return null;
            return TryStart(assailant, c, new[] { assailant }, Assault, target);
        }

        /// <summary>Takes <paramref name="hostage"/>: the cast, the state, the held room, the chronicle, and the warden's first letter.</summary>
        public static StoryArc Seize(Map map, Pawn leader, CompInmate c, IEnumerable<Pawn> takers, Pawn hostage, string trigger)
        {
            PrisonGame game = PrisonGame.Instance;
            StoryArc arc = game.story.TryStartArc(RPHostageDefOf.RP_Arc_Hostage, hostage, leader);
            if (arc == null) return null;
            int now = Find.TickManager.TicksGame;
            NeedDef grievance = RiotNegotiation.Grievance(leader, c);
            IntVec3 anchor = hostage.Position;
            // The ringleader riots (an assailant in the workshop turns from the one they struck to everything they hold).
            bool leaderRioting = leader.MentalStateDef == RPMentalStateDefOf.RP_Rioting;
            if (!leaderRioting)
            {
                leader.MentalState?.RecoverFromState();
                leaderRioting = leader.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, "RP_HostageReason".Translate(), forced: true);
            }
            if (!leaderRioting)
            {
                Abandon(game, arc);
                return null;
            }
            foreach (Pawn t in takers)
                if (t != null && t != leader && t.MentalStateDef == RPMentalStateDefOf.RP_Rioting && !arc.pawns.Contains(t) && !game.story.InArc(t) && t.Inmate() != null) arc.pawns.Add(t);
            bool wing = arc.pawns.Count - 1 >= WingTakers;
            arc.note = HostageNote.Make(anchor, wing, trigger, grievance);
            arc.stage = Standoff;
            arc.nextTick = now + (WardenSystem.IsWarden(hostage) ? WardenStandoffHours : StandoffHours) * GenDate.TicksPerHour;
            arc.markTick = NoMark;
            arc.count = 0;
            // The hostage: stopped where they stand, hands tied.
            hostage.jobs?.StopAll();
            hostage.pather?.StopDead();
            if (hostage.carryTracker?.CarriedThing != null) hostage.carryTracker.TryDropCarriedThing(hostage.Position, ThingPlaceMode.Near, out _);
            hostage.health.AddHediff(RPHostageDefOf.RP_Hostage);
            Roster.Invalidate();
            if (!hostage.mindState.mentalStateHandler.TryStartMentalState(RPHostageDefOf.RP_BeingHeld, "RP_HostageReason".Translate(), forced: true, otherPawn: leader))
            {
                Release(hostage);
                Abandon(game, arc);
                return null;
            }
            game.stats.hostageCrises++;
            game.story.dramaToday += 3;
            string place = PrisonMap.Of(map)?.zones.ZoneAt(anchor)?.label ?? "RP_HostageSomewhere".Translate().ToString();
            CompStaff sc = hostage.GetComp<CompStaff>();
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 3, "RP_Chr_HostageTaken".Translate(hostage.LabelShort, StaffCareers.Title(sc), leader.LabelShort, place, arc.pawns.Count - 1), hostage, leader);
            PrisonMap.Of(map)?.Notify_DoorsChanged();
            PrisonAlerts.Invalidate();
            ((HostageArc)arc.def.Worker).Declare(arc, map);
            return arc;
        }

        private static void Abandon(PrisonGame game, StoryArc arc)
        {
            arc.done = true;
            game.story.arcs.Remove(arc);
            game.story.lastArcStart.Remove(arc.def);
        }

        /// <summary>The hostage goes free: the hediff and the state end (their memories and the rest are the crisis' business).</summary>
        public static void Release(Pawn hostage)
        {
            if (hostage == null || hostage.Dead) return;
            if (hostage.health?.hediffSet?.GetFirstHediffOfDef(RPHostageDefOf.RP_Hostage) is { } tied)
            {
                hostage.health.RemoveHediff(tied);
                Roster.Invalidate();
            }
            if (hostage.MentalStateDef == RPHostageDefOf.RP_BeingHeld) hostage.MentalState.RecoverFromState();
        }

        /// <summary>
        /// Hourly: nobody stays held (or in the state) without a crisis that holds them: a save that lost its arc, a crisis cancelled by
        /// the hostage being let go.
        /// </summary>
        public static void Sweep(Map map)
        {
            StoryArc running = Running();
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned.ToList())
                if ((IsHeld(s) || s.MentalStateDef == RPHostageDefOf.RP_BeingHeld) && running?.A != s) Release(s);
        }

        /// <summary>Two blows to an arm or a leg: bruised and cut, not maimed (a leg may put them on the floor, which the staff stories count).</summary>
        public static void Hurt(Pawn hostage, Pawn by)
        {
            if (hostage == null || hostage.Dead) return;
            List<BodyPartRecord> limbs = hostage.health.hediffSet.GetNotMissingParts().Where(p => p.def == BodyPartDefOf.Arm || p.def == BodyPartDefOf.Leg).ToList();
            for (int i = 0; i < 2 && limbs.Count > 0; i++)
            {
                BodyPartRecord part = limbs.RandomElement();
                // (The blows land whatever the uniform's armour: the letter says they were hurt, and a pair of rolls could absorb both.)
                var blow = new DamageInfo(i == 0 ? DamageDefOf.Blunt : DamageDefOf.Cut, Rand.RangeInclusive(5, 8), 0f, -1f, by, part);
                blow.SetIgnoreArmor(true);
                hostage.TakeDamage(blow);
            }
        }

        /// <summary>Killed through the staff death path (the chronicle, the colleagues' mourning, the murder charge and the family letter all follow).</summary>
        public static void Kill(Pawn hostage, Pawn by)
        {
            if (hostage == null || hostage.Dead) return;
            hostage.Kill(new DamageInfo(DamageDefOf.Blunt, 200f, 999f, -1f, by));
        }

        /// <summary>
        /// The hostage died and the crisis did not do it (its deadline and its storm end the arc before they kill): a taker's blow, a
        /// fire, a wound that bled out. The crisis ends with the "killed" ending and its consequences (the ringleader's capital charge, the
        /// colleagues, the inmates who trusted them, the warden's letter); the staff death path has run already, with the killer's own charge.
        /// </summary>
        public static void Notify_Died(Pawn hostage, DamageInfo? dinfo)
        {
            if (hostage is not { Dead: true } || Running() is not { } arc || arc.A != hostage) return;
            Pawn killer = dinfo?.Instigator as Pawn;
            ((HostageArc)arc.def.Worker).Finish(arc, hostage.MapHeld ?? Find.AnyPlayerHomeMap, killer != null && killer != hostage ? HostageHow.Killed : HostageHow.Died, HostageFate.Dead, killer?.LabelShort ?? "");
        }

        /// <summary>
        /// A melee job against the held hostage: nothing but the crisis' own harm (<see cref="Hurt"/>, <see cref="Kill"/>) touches them. Vanilla's
        /// pather turns a pawn that is hostile to whoever blocks its path into one, so the riot state is not hostile to the hostage (see
        /// <see cref="MentalState_Rioting"/>), and this is the net for the rest (a berserk inmate, a state that forces hostility).
        /// </summary>
        public static bool Shielded(Job job) =>
            job?.def == JobDefOf.AttackMelee && job.targetA.Thing is Pawn target && IsHeld(target);

        /// <summary>What <see cref="Charge"/> did, as the line the chronicle and the warden's letter tell (only what really happened).</summary>
        public struct ChargeOutcome
        {
            public bool charged;
            public string line;
        }

        /// <summary>
        /// A charge for the ringleader: taking a member of staff hostage is the gravest record short of a murder (life, and a wing with more
        /// bars when there is one to go to and room in it). A conviction that is already graver (capital murder, death row, a life sentence
        /// for a hostage who lived) stands.
        /// </summary>
        public static ChargeOutcome Charge(Pawn leader, Map map, bool murder)
        {
            if (leader == null || leader.Dead || leader.Inmate() is not { } c) return new ChargeOutcome { line = NotCharged(leader) };
            MisconductUtility.Record(leader, c, PrisonMap.Of(map), RPHostageDefOf.RP_HostageTaking, evidence: true, chronicle: false);
            CrimeDef crime = murder ? RPHostageDefOf.RP_Crime_CapitalMurder : RPHostageDefOf.RP_Crime_Kidnapping;
            bool kept = c.crime == RPHostageDefOf.RP_Crime_CapitalMurder || DeathRowSystem.Ext(c) != null || !murder && c.sentenceDays < 0;
            string sentence;
            if (kept) sentence = "RP_HostageChargedKept".Translate(c.crime?.label ?? "?");
            else
            {
                c.crime = crime;
                c.sentenceDays = -1;
                sentence = "RP_HostageChargedLife".Translate(crime.label);
                // (A life term handed down mid-stay: what can put them at risk, see WatchList.)
                WatchList.Notify_Trigger(leader, RiskTrigger.LifeTerm);
            }
            SecurityLevelDef was = c.security;
            // Supermax when the wing has a free cell (one already there stays); else a level up.
            if (!SupermaxSystem.IsIn(c) && !(leader.Spawned && SupermaxSystem.TrySend(leader, c, SupermaxReason.Ringleader)) && c.security?.demoteTo is { } up && Unlocks.IsUnlocked(up) && leader.Spawned)
            {
                if (InmateUtility.HasRoomIn(leader, up)) InmateUtility.ChangeSecurity(leader, c, up);
                else InmateUtility.TellIfWingKeepsThemOut(leader, up);
            }
            string wing = c.security?.label ?? "?";
            string where = (c.security != was ? "RP_HostageMovedTo" : "RP_HostageStaysIn").Translate(wing);
            string line = "RP_HostageChargeLine".Translate(leader.LabelShort, sentence, where).Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Violence, 2, line, leader);
            return new ChargeOutcome { charged = true, line = line };
        }

        /// <summary>The ringleader who cannot be charged: dead, gone, or not an inmate any more.</summary>
        public static string NotCharged(Pawn leader) =>
            (leader is { Dead: true } ? "RP_HostageNotChargedDead" : "RP_HostageNotChargedGone").Translate(leader?.LabelShort ?? "RP_HostageTheLeader".Translate().ToString()).Resolve().CapitalizeFirst();

        /// <summary>
        /// The crisis is over and the hostage walks free: the hostage-takers (and any rioter who joined them in the held rooms) stand down. Left
        /// rioting, they would be hostile to the freed hostage again at once (the riot is only not hostile to a held one), and the freed hostage stands
        /// among them: a storm brings the hostage out of the room but the squad is far away, and a deal or a talk leaves the rioters who joined by
        /// spread, who were never in the cast. Ending the riot state is the choice over keeping them non-hostile until restrained: no extra state to carry.
        /// </summary>
        /// <param name="exhausted">A calm ending: they stand down tired (their tension eases); a storm leaves them as they are.</param>
        public static void StandDown(StoryArc arc, Map map, List<Pawn> takers, bool exhausted)
        {
            var calmed = new HashSet<Pawn>(takers);
            if (HostageHold.Of(arc, map) is { } held)
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.MentalStateDef == RPMentalStateDefOf.RP_Rioting && p.GetRoom() is { } room && held.Contains(room)) calmed.Add(p);
            foreach (Pawn t in calmed)
            {
                t.MentalState?.RecoverFromState();
                if (exhausted && t.Inmate() is { } tc) tc.tension = Mathf.Max(0f, tc.tension - 20f);
            }
        }

        /// <summary>The weather of a stand-off: how likely the hostage-takers are to give up this hour.</summary>
        public static float GiveUpChance(StoryArc arc, List<Pawn> takers, int now)
        {
            if (now - arc.startTick < 2 * GenDate.TicksPerHour) return 0f;
            float tension = 0f;
            foreach (Pawn t in takers) tension += t.Inmate()?.tension ?? 0f;
            float chance = takers.Count > 0 && tension / takers.Count < 35f ? 0.08f : 0f;
            if (arc.markTick != NoMark && now - arc.markTick >= GenDate.TicksPerHour) chance += 0.2f;
            return Mathf.Clamp01(chance);
        }

        /// <summary>The storm's two rolls, drawn first thing so a test can seed them: (what happens to the hostage, whether a rioter is hurt).</summary>
        public static (float outcome, float casualty) StormRolls() => (Rand.Value, Rand.Value);

        public const float StormCasualty = 0.3f;

        public static HostageFate StormFate(StoryArc arc, float outcome)
        {
            float late = arc.stage >= Deadline ? 1f : 0f;
            float kill = 0.10f + 0.10f * late + (HatedBy(arc.A, Leader(arc)) ? 0.05f : 0f) + (WardenSystem.IsWarden(arc.A) ? WardenStormKill : 0f);
            float hurt = 0.30f + 0.15f * late;
            return outcome < kill ? HostageFate.Dead : outcome < kill + hurt ? HostageFate.Hurt : HostageFate.Freed;
        }

        public static bool HatedBy(Pawn staff, Pawn inmate) => staff != null && StaffMemories.ScoreFor(inmate?.Inmate(), staff) <= StaffMemories.HatedAt;

        /// <summary>The hostage's last chance: the odds that the hostage-takers kill them when the deadline passes with nothing done.</summary>
        public static float KillChance(StoryArc arc) =>
            Mathf.Clamp01(0.45f + (HatedBy(arc.A, Leader(arc)) ? 0.3f : 0f) - (arc.markTick != NoMark ? 0.15f : 0f) + (WardenSystem.IsWarden(arc.A) ? WardenDeadlineKill : 0f));
    }

    /// <summary>
    /// The hostage crisis arc. A stand-off the warden is asked about: talk (a trusted officer, a counsellor or a psychologist improve
    /// the odds), give the grievance, cut power and water to the held area, call the riot police to storm it, or wait. The deadline
    /// hurts the hostage if nothing is resolved, and at the last deadline they are killed (through the staff death path) or the
    /// hostage-takers lose their nerve. It ends with the hostage released, rescued or killed; the ringleader is charged and moved up a
    /// level, the hostage remembers and is wary, the colleagues are shaken, and the chronicle gets a milestone with the officer's name.
    /// The cast is the hostage first, then the ringleader and the rioters who hold them (a follower takes over when the ringleader
    /// leaves); the note holds the held area and the grievance (<see cref="HostageNote"/>), the arc's count the failed talks and its
    /// mark the tick power and water were cut.
    /// </summary>
    public class HostageArc : StoryArcWorker
    {
        private static readonly string[] Keys = { "RP_Hostage_Talk", "RP_Hostage_Concede", "RP_Hostage_Cut", "RP_Hostage_Storm", "RP_Hostage_Wait" };
        public const int MaxFailedTalks = 2;
        public const float TalkBase = 0.3f, CounsellorBonus = 0.2f, TrustedBonus = 0.3f, VeryTrustedBonus = 0.1f, HostageTrustBonus = 0.1f, FailedTalkPenalty = 0.15f, LatePenalty = 0.1f;
        public const int WaitOption = 4;

        /// <summary>The hostage is still ours and still on the map; their death is the crisis' business (see <see cref="HostageCrisis.Notify_Died"/>, <see cref="Hourly"/>).</summary>
        public override bool Valid(StoryArc arc) =>
            arc.A is { } h && h.GetComp<CompStaff>() is { role: not null } && h.Faction == Faction.OfPlayer && (h.Dead || h.Spawned && !h.Destroyed);

        /// <summary>The hostage was let go, or left, and nobody told the crisis: it ends quietly.</summary>
        public override void Cancelled(StoryArc arc)
        {
            Map map = arc.A?.MapHeld ?? Find.AnyPlayerHomeMap;
            // (A hostage who died and was not caught by Notify_Died or Hourly is no quiet ending either.)
            if (arc.A is { Dead: true })
            {
                Finish(arc, map, HostageHow.Died, HostageFate.Dead);
                return;
            }
            CloseLetters(arc);
            HostageCrisis.Release(arc.A);
            // A hostage who is still here is let go among the takers: they stand down, as for any ending that frees the hostage.
            if (arc.A is { Spawned: true } && map != null) HostageCrisis.StandDown(arc, map, HostageCrisis.Takers(arc), exhausted: false);
            if (arc.A != null) Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_HostageEnd_Gone".Translate(arc.A.LabelShort, "", HostageCrisis.Leader(arc)?.LabelShort ?? "?", ""), arc.A);
            Closed(map);
        }

        /// <summary>The cast changes: the hostage going ends it, a hostage-taker going hands it to the next, and nobody left to hold them ends it.</summary>
        public override void Notify_Leaving(StoryArc arc, Pawn p)
        {
            int seat = arc.pawns.IndexOf(p);
            if (seat < 0 || arc.done) return;
            if (seat == 0)
            {
                arc.done = true;
                Cancelled(arc);
                return;
            }
            Pawn leader = HostageCrisis.Leader(arc);
            arc.pawns.RemoveAt(seat);
            // (A ringleader who has left is named in the line but not charged: they are not here.)
            if (HostageCrisis.Takers(arc).Count == 0) Finish(arc, arc.A?.MapHeld ?? Find.AnyPlayerHomeMap, HostageHow.GaveUp, HostageFate.Freed, leader: leader, charge: p != leader);
        }

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override int TimeoutChoice(StoryArc arc) => WaitOption;

        public override string Disabled(StoryArc arc, int option)
        {
            switch (option)
            {
                case 0:
                    return arc.count >= MaxFailedTalks ? "RP_Hostage_TalkSpent".Translate().ToString() : null;
                case 2:
                    return arc.markTick != HostageCrisis.NoMark ? "RP_Hostage_CutDone".Translate().ToString() : null;
                case 3:
                    return PrisonGame.Instance.ledger.funds < RiotPolice.Cost ? "RP_CannotAfford".Translate(Money.Format(RiotPolice.Cost)).ToString() : null;
                default:
                    return null;
            }
        }

        // ---- The warden's letters ----------------------------------------------------------------------------------------

        private static string Odds(float chance) => chance >= 0.6f ? "RP_Hostage_OddsGood".Translate() : chance >= 0.35f ? "RP_Hostage_OddsFair".Translate() : "RP_Hostage_OddsPoor".Translate();

        private static TaggedString HoursLeft(StoryArc arc) => Mathf.Max(1, Mathf.CeilToInt((arc.nextTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour)).ToString();

        /// <summary>The first letter, from the moment of the seizure.</summary>
        public void Declare(StoryArc arc, Map map)
        {
            Pawn hostage = arc.A, leader = HostageCrisis.Leader(arc);
            HostageNote note = HostageNote.Of(arc);
            string place = PrisonMap.Of(map)?.zones.ZoneAt(note.anchor)?.label ?? "RP_HostageSomewhere".Translate().ToString();
            CompStaff sc = hostage.GetComp<CompStaff>();
            bool wardenHeld = WardenSystem.IsWarden(hostage);
            TaggedString text = "RP_HostageText".Translate(hostage.LabelShort, StaffCareers.Title(sc), leader.LabelShort, arc.pawns.Count - 1, place, note.need?.label ?? "?", HoursLeft(arc),
                Odds(TalkChance(arc, map, out _)), Money.Format(RiotPolice.Cost), (note.wing ? "RP_HostageWing" : "RP_HostageRoom").Translate());
            AskWarden(arc, map, (wardenHeld ? "RP_HostageWardenLabel" : "RP_HostageLabel").Translate(hostage.LabelShort), wardenHeld ? text + "\n\n" + "RP_HostageWardenNote".Translate(hostage.LabelShort) : text);
        }

        /// <summary>Asks again (a talk that failed, power and water cut, the warden asking for the options back): one open letter at a time.</summary>
        public void Reask(StoryArc arc)
        {
            if (arc.done || arc.A == null) return;
            Map map = arc.A.MapHeld ?? Find.AnyPlayerHomeMap;
            CloseLetters(arc);
            Pawn leader = HostageCrisis.Leader(arc);
            string key = arc.stage >= HostageCrisis.Deadline ? "RP_HostageDeadlineText" : "RP_HostageAgainText";
            AskWarden(arc, map, (arc.stage >= HostageCrisis.Deadline ? "RP_HostageDeadlineLabel" : "RP_HostageAgainLabel").Translate(arc.A.LabelShort),
                key.Translate(arc.A.LabelShort, leader?.LabelShort ?? "?", HoursLeft(arc), Odds(TalkChance(arc, map, out _)), Money.Format(RiotPolice.Cost)));
        }

        /// <summary>The crisis' open decision letters are answered and gone: an ending that does not come from a letter (the takers give up, the hostage dies, a storm from the prison pane) leaves none to click.</summary>
        private static void CloseLetters(StoryArc arc)
        {
            foreach (ChoiceLetter_PrisonDecision open in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.arcId == arc.id && !l.answered).ToList())
            {
                open.answered = true;
                Find.LetterStack.RemoveLetter(open);
            }
        }

        // ---- The warden's choices ----------------------------------------------------------------------------------------

        public override void Choose(StoryArc arc, Map map, int option)
        {
            if (arc.done || arc.A == null || arc.A.Dead) return;
            switch (option)
            {
                case 0:
                    Talk(arc, map);
                    break;
                case 1:
                    Concede(arc, map);
                    break;
                case 2:
                    CutUtilities(arc, map);
                    break;
                case 3:
                    Storm(arc, map);
                    break;
            }
        }

        /// <summary>The staff on site who could talk them down (not the hostage, not down, not held).</summary>
        private static IEnumerable<Pawn> OnSite(Map map, Pawn hostage)
        {
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
                if (s != hostage && !s.Downed && !s.InMentalState && !HostageCrisis.IsHeld(s) && s.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 } sc && !CorruptionSystem.Suspended(sc))
                    yield return s;
        }

        /// <summary>
        /// The odds of talking them down: a start, a counsellor or a psychologist on site, an officer the ringleader trusts (and
        /// <paramref name="negotiator"/> is the one who does the talking), the ringleader's feelings about the hostage; less after each
        /// failed talk and once the deadline has passed.
        /// </summary>
        public static float TalkChance(StoryArc arc, Map map, out Pawn negotiator)
        {
            negotiator = null;
            Pawn leader = HostageCrisis.Leader(arc);
            CompInmate lc = leader?.Inmate();
            float chance = TalkBase;
            float best = StaffMemories.TrustedAt;
            Pawn counsellor = null;
            foreach (Pawn s in OnSite(map, arc.A))
            {
                StaffRoleDef role = s.GetComp<CompStaff>().role;
                if (role == RPProgramDefOf.RP_Counsellor || role == RPStaffRoleDefOf.RP_Psychologist) counsellor ??= s;
                float score = StaffMemories.ScoreFor(lc, s);
                if (score >= best)
                {
                    best = score;
                    negotiator = s;
                }
            }
            if (counsellor != null)
            {
                chance += CounsellorBonus;
                negotiator ??= counsellor;
            }
            if (best >= StaffMemories.TrustedAt && negotiator != null && StaffMemories.ScoreFor(lc, negotiator) >= StaffMemories.TrustedAt) chance += TrustedBonus + (best >= 2f * StaffMemories.TrustedAt ? VeryTrustedBonus : 0f);
            float hostageScore = StaffMemories.ScoreFor(lc, arc.A);
            chance += hostageScore >= StaffMemories.TrustedAt ? HostageTrustBonus : hostageScore <= StaffMemories.HatedAt ? -HostageTrustBonus : 0f;
            chance -= FailedTalkPenalty * arc.count + (arc.stage >= HostageCrisis.Deadline ? LatePenalty : 0f);
            if (WardenSystem.IsWarden(arc.A)) chance -= HostageCrisis.WardenTalkPenalty;
            return Mathf.Clamp(chance, 0.05f, 0.9f);
        }

        private void Talk(StoryArc arc, Map map)
        {
            float chance = TalkChance(arc, map, out Pawn negotiator);
            // (The roll is the first draw: a test seeds it.)
            if (Rand.Chance(chance))
            {
                if (negotiator != null && HostageCrisis.Leader(arc)?.Inmate() is { } lc) StaffMemories.Note(lc, negotiator, 10f);
                Finish(arc, map, HostageHow.Talked, HostageFate.Freed, negotiator?.LabelShort ?? "RP_Hostage_TheWarden".Translate().ToString());
                return;
            }
            arc.count++;
            Pawn leader = HostageCrisis.Leader(arc);
            foreach (Pawn t in HostageCrisis.Takers(arc))
                if (t.Inmate() is { } c) c.tension = Mathf.Min(100f, c.tension + 8f);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_HostageTalksFailed".Translate(arc.A.LabelShort, leader?.LabelShort ?? "?", negotiator?.LabelShort ?? "RP_Hostage_TheWarden".Translate().ToString()), arc.A, leader);
            Reask(arc);
        }

        private void Concede(StoryArc arc, Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            Pawn leader = HostageCrisis.Leader(arc);
            HostageNote note = HostageNote.Of(arc);
            NeedDef need = note.need ?? (leader != null ? RiotNegotiation.Grievance(leader, leader.Inmate()) : NeedDefOf.Food);
            // A promise, as in a negotiated riot: the clock starts, the ringleader becomes the one it was made to, and giving in costs face.
            DemandSystem.Accept(game, need, leader);
            game.reputation = Mathf.Max(0f, game.reputation - (game.stats.riotsNegotiated > 0 ? 2f : 1f));
            game.stats.riotsNegotiated++;
            // (A crisis a riot started: its record says the riot ended in a deal. One an assault started is no riot's.)
            if (leader != null && note.trigger == HostageCrisis.Riot) RiotReview.Notify_Negotiated(leader.thingIDNumber);
            Finish(arc, map, HostageHow.Conceded, HostageFate.Freed, need.label, leader);
        }

        private void CutUtilities(StoryArc arc, Map map)
        {
            arc.markTick = Find.TickManager.TicksGame;
            Pawn leader = HostageCrisis.Leader(arc);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_HostageCut".Translate(arc.A.LabelShort, leader?.LabelShort ?? "?"), arc.A, leader);
            PrisonMap.Of(map)?.Notify_DoorsChanged();
            Reask(arc);
        }

        private void Storm(StoryArc arc, Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            (float outcome, float casualty) = HostageCrisis.StormRolls();
            List<Pawn> squad = RiotPolice.CallSquad(map);
            if (squad == null)
            {
                Reask(arc);
                return;
            }
            Pawn hostage = arc.A, leader = HostageCrisis.Leader(arc);
            HostageFate fate = HostageCrisis.StormFate(arc, outcome);
            List<Pawn> takers = HostageCrisis.Takers(arc);
            // The storm is the end of it: nobody inside is still "holding" anyone (and a death inside does not end it twice).
            arc.done = true;
            HostageCrisis.Release(hostage);
            Pawn officer = squad.FirstOrDefault(p => p is { Spawned: true });
            if (fate == HostageFate.Hurt) HostageCrisis.Hurt(hostage, leader);
            else if (fate == HostageFate.Dead) HostageCrisis.Kill(hostage, leader);
            // A rioter may not come out of it (the rest of the guards' business, with an inquiry to follow): a follower before the ringleader.
            if (casualty < HostageCrisis.StormCasualty && officer != null && (takers.FirstOrDefault(t => t != leader) ?? leader) is { Dead: false } victim)
                victim.Kill(new DamageInfo(DamageDefOf.Blunt, 100f, 999f, -1f, officer));
            Finish(arc, map, HostageHow.Stormed, fate, "", leader);
        }

        // ---- Hours ------------------------------------------------------------------------------------------------------

        public override void Hourly(StoryArc arc, Map map)
        {
            Pawn hostage = arc.A;
            if (hostage.Dead)
            {
                Finish(arc, map, HostageHow.Died, HostageFate.Dead);
                return;
            }
            // Still held, whatever happened to the state (a leg that put them on the floor ends it).
            if (!HostageCrisis.IsHeld(hostage)) hostage.health.AddHediff(RPHostageDefOf.RP_Hostage);
            if (!hostage.InMentalState && !hostage.Downed) hostage.mindState.mentalStateHandler.TryStartMentalState(RPHostageDefOf.RP_BeingHeld, null, forced: true, otherPawn: HostageCrisis.Leader(arc));
            int now = Find.TickManager.TicksGame;
            List<Pawn> takers = HostageCrisis.Takers(arc);
            if (takers.Count == 0)
            {
                Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                return;
            }
            if (now >= arc.nextTick)
            {
                DeadlineReached(arc, map);
                return;
            }
            if (arc.markTick != HostageCrisis.NoMark) Squeeze(takers);
            if (Rand.Chance(HostageCrisis.GiveUpChance(arc, takers, now))) Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
        }

        /// <summary>No power, no water: hungrier and filthier by the hour.</summary>
        private static void Squeeze(List<Pawn> takers)
        {
            foreach (Pawn t in takers)
            {
                if (t.needs?.food is { } food) food.CurLevel = Mathf.Max(0f, food.CurLevel - 0.04f * food.MaxLevel);
                if (t.needs?.TryGetNeed(RPDefOf.RP_Hygiene) is { } hygiene) hygiene.CurLevel = Mathf.Max(0f, hygiene.CurLevel - 0.06f);
            }
        }

        /// <summary>The first deadline hurts the hostage and asks again; the last kills them or breaks the hostage-takers' nerve.</summary>
        public void DeadlineReached(StoryArc arc, Map map)
        {
            Pawn hostage = arc.A, leader = HostageCrisis.Leader(arc);
            int now = Find.TickManager.TicksGame;
            if (arc.stage < HostageCrisis.Deadline)
            {
                arc.stage = HostageCrisis.Deadline;
                arc.nextTick = now + (WardenSystem.IsWarden(hostage) ? HostageCrisis.WardenDeadlineHours : HostageCrisis.DeadlineHours) * GenDate.TicksPerHour;
                HostageCrisis.Hurt(hostage, leader);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_HostageHurt".Translate(hostage.LabelShort, leader?.LabelShort ?? "?"), hostage, leader);
                Reask(arc);
                return;
            }
            // (The roll is the first draw: a test seeds it.)
            if (Rand.Chance(HostageCrisis.KillChance(arc)))
            {
                arc.done = true;
                HostageCrisis.Kill(hostage, leader);
                Finish(arc, map, HostageHow.Deadline, HostageFate.Dead, "", leader);
            }
            else Finish(arc, map, HostageHow.GaveUp, HostageFate.Hurt, "", leader);
        }

        // ---- The end ------------------------------------------------------------------------------------------------------

        private static void Closed(Map map)
        {
            PrisonMap.Of(map ?? Find.AnyPlayerHomeMap)?.Notify_DoorsChanged();
            PrisonAlerts.Invalidate();
        }

        /// <summary>
        /// The crisis is over. The hostage goes free (or is lost), the rioters stand down when the hostage is free (see
        /// <see cref="HostageCrisis.StandDown"/>), the hostage and the colleagues remember, the inmates who trusted the hostage feel it, the ringleader
        /// is charged, the prison's name is marked, and the chronicle and the warden get the end of it.
        /// </summary>
        /// <param name="leader">The ringleader as the crisis ended (they may be dead by now); null: whoever leads now.</param>
        /// <param name="charge">False when the ringleader is no longer here to be charged (released, transferred).</param>
        internal void Finish(StoryArc arc, Map map, HostageHow how, HostageFate fate, string detail = "", Pawn leader = null, bool charge = true)
        {
            PrisonGame game = PrisonGame.Instance;
            Pawn hostage = arc.A;
            leader ??= HostageCrisis.Leader(arc);
            List<Pawn> takers = HostageCrisis.Takers(arc);
            // Hurt at the first deadline and let go after: freed, but not unharmed (a storm has its own outcome).
            bool late = arc.stage >= HostageCrisis.Deadline;
            arc.done = true;
            arc.stage = HostageCrisis.Resolved;
            if (hostage.Dead) fate = HostageFate.Dead;
            else if (fate == HostageFate.Freed && how != HostageHow.Stormed && late) fate = HostageFate.Hurt;
            CloseLetters(arc);
            HostageCrisis.Release(hostage);
            // A hostage who walks free is not left among rioters who are hostile to them again: the takers stand down (tired after a calm ending, subdued after a storm).
            if (!hostage.Dead && hostage.Spawned && how is not (HostageHow.Killed or HostageHow.Died or HostageHow.Deadline))
                HostageCrisis.StandDown(arc, map, takers, exhausted: how is HostageHow.Talked or HostageHow.Conceded or HostageHow.GaveUp);
            CompStaff hsc = hostage.GetComp<CompStaff>();
            string title = hsc?.role != null ? StaffCareers.Title(hsc) : "";
            string who = hostage.LabelShort, leaderName = leader?.LabelShort ?? "?";
            bool wary = false;
            if (!hostage.Dead)
            {
                game.stats.hostagesFreed++;
                hostage.needs?.mood?.thoughts.memories.TryGainMemory(RPHostageDefOf.RP_TakenHostage, leader);
                if (hsc != null && leader?.Inmate() != null)
                {
                    StaffWariness.Notify_HurtBy(hostage, hsc, leader);
                    wary = StaffWariness.WaryOf(hsc, leader);
                }
                foreach (Pawn o in StaffCommute.AllStaff(map))
                    if (o != hostage && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_ColleagueHurt, hostage);
            }
            else game.stats.hostagesKilled++;
            // The wing: those who trusted the hostage are relieved or furious; a storm leaves everyone sore.
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { } c || p == leader) continue;
                if (StaffMemories.ScoreFor(c, hostage) >= StaffMemories.TrustedAt)
                {
                    if (fate == HostageFate.Freed) c.tension = Mathf.Max(0f, c.tension - 5f);
                    else
                    {
                        c.tension = Mathf.Min(100f, c.tension + 8f);
                        if (fate == HostageFate.Hurt && leader?.Inmate() != null) PrisonRelations.MakeEnemies(p, leader);
                    }
                }
                else if (how == HostageHow.Stormed) c.tension = Mathf.Min(100f, c.tension + 5f);
            }
            game.reputation = Mathf.Clamp(game.reputation + ReputationChange(hostage, how, fate), 0f, 100f);
            string line = ("RP_Chr_HostageEnd_" + how + (how == HostageHow.Stormed ? "_" + fate : "")).Translate(who, title, leaderName, detail).Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 3, line, hostage, leader);
            HostageCrisis.ChargeOutcome charged = charge ? HostageCrisis.Charge(leader, map, murder: fate == HostageFate.Dead) : new HostageCrisis.ChargeOutcome { line = HostageCrisis.NotCharged(leader) };
            game.story.dramaToday += 2;
            bool lost = fate == HostageFate.Dead;
            // (The letter tells what happened: the charge, the wing and the wariness are each there only when they are so.)
            Find.LetterStack.ReceiveLetter((lost ? "RP_HostageLostLabel" : "RP_HostageFreedLabel").Translate(who),
                line + "\n\n" + (lost ? "RP_HostageLostAfter".Translate(who, leaderName, charged.line) : "RP_HostageFreedAfter".Translate(who, leaderName, charged.line, wary ? " " + "RP_HostageWary".Translate(who, leaderName) : "")),
                lost ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent, hostage.Spawned ? new LookTargets(hostage) : LookTargets.Invalid);
            Closed(map);
        }

        /// <summary>What the end of the crisis does to the prison's name: the prison suffers twice as much for a warden lost or hurt, and a warden freed earns no more than any member of staff.</summary>
        internal static float ReputationChange(Pawn hostage, HostageHow how, HostageFate fate)
        {
            float delta = ReputationDelta(how, fate);
            return delta < 0f && WardenSystem.IsWarden(hostage) ? delta * HostageCrisis.WardenReputationFactor : delta;
        }

        private static float ReputationDelta(HostageHow how, HostageFate fate) => how switch
        {
            HostageHow.Talked => 1f,
            HostageHow.Stormed => fate == HostageFate.Freed ? 1f : fate == HostageFate.Hurt ? -1f : -5f,
            HostageHow.Deadline or HostageHow.Killed or HostageHow.Died => -5f,
            HostageHow.Gone => -2f,
            _ => 0f,
        };
    }

    /// <summary>A hostage who dies outside the crisis' own path ends it as a death, never quietly (see <see cref="HostageCrisis.Notify_Died"/>).</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill_Hostage
    {
        public static void Postfix(Pawn __instance, DamageInfo? dinfo)
        {
            if (__instance.Dead) HostageCrisis.Notify_Died(__instance, dinfo);
        }
    }

    /// <summary>The held hostage cannot be arrested (vanilla lets a drafted colonist arrest a colonist in a mental state that is not aggressive).</summary>
    [HarmonyPatch(typeof(GenAI), nameof(GenAI.CanBeArrestedBy))]
    public static class Patch_GenAI_CanBeArrestedBy_Hostage
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result && HostageCrisis.IsHeld(pawn)) __result = false;
        }
    }

    /// <summary>Nobody starts a melee fight with the held hostage (see <see cref="HostageCrisis.Shielded"/>).</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_Pawn_JobTracker_StartJob_Hostage
    {
        public static bool Prefix(Job newJob) => !HostageCrisis.Shielded(newJob);
    }
}
