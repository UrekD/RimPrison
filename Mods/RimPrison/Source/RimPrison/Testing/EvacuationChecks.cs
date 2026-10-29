using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Evacuating a wing (<see cref="EvacuationSystem"/>), in the events plan (<see cref="CheckEvacuation"/>) and the saveload plan (<see cref="SeedEvacuationForReload"/>). The proof runs in the small walled unit of the supermax checks
    /// (<see cref="SupermaxScene"/>: three cells painted as one wing, a shower, a yard of the supermax level for a cage), with an ordinary yard patch painted in the corridor for the first assembly point and the cage for the second, real
    /// ticks, and the evacuation limited to the check's own inmates (<see cref="EvacuationSystem.OnlyThese"/>: a real wing's people are not moved by a check). The cells are locked in every hour (the regimes are the lockup all day) and the
    /// shower and cage doors are staff-only, so that a door that opens for an evacuee has opened by the pass planned for them and for nothing else. Each check says what it saw. The first five ran once (round 1: the fire run and the
    /// letter failed, fixed in round 2); the no-tick checks after the drill (the route ahead, the feud, supermax alone, the hole, no bed in the wing) were written in round 2 without a game run, and ran in round 2's runs (the fire run,
    /// the blocked route and the drill failed on vanilla's warden carry and the hourly cell move, fixed in round 3 without a run: the fire run's rescued inmate, vanilla's giver asked itself, the drill's rolling day). Round 6 (no run):
    /// the fire run's all-clear waits for the fire to be put out (refused while the wing burns), the blocked route's missing inmate gets out once the door is unlocked, and four no-tick checks (a fire after the all-clear, a cage not
    /// shared with supermax, a downed inmate with no way, a carry planned again). Stability round 22 (no run): a carry to the end of its place lays the inmate in it; its round 2 (no run): the carry by hand
    /// is had at once (<see cref="EvacCarryByHand"/>), and the two carry checks fail on a set-up they cannot make.
    /// </summary>
    public static partial class AutoTest
    {
        private static void EvacFail(string what) => problems0.Add("evacuation: " + what);

        private static void CheckEvacuation(Map map)
        {
            CheckEvacMarkablePlaces();
            CheckEvacRoutesFollowTheRules(map);
            CheckEvacFireWithADownedAndASupermaxInmate(map);
            CheckEvacBlockedRouteAndDestroyedPoint(map);
            CheckEvacSecondPointAndTheLetter(map);
            CheckEvacDrill(map);
            CheckEvacRouteAhead(map);
            CheckEvacFeud(map);
            CheckEvacSupermaxAlone(map);
            CheckEvacTheHole(map);
            CheckEvacNoBedInTheWing(map);
            CheckEvacFireAfterTheAllClear(map);
            CheckEvacCageNotShared(map);
            CheckEvacDownedWithNoWay(map);
            CheckEvacCarryGoesOn(map);
            CheckEvacCarryLaidAtTheEdge(map);
        }

        /// <summary>The places the marking menu offers: a yard, the supermax cage, a kept room and holding; never visitation (the public's room), a wing or a staff zone.</summary>
        private static void CheckEvacMarkablePlaces()
        {
            foreach (PrisonZoneDef z in new[] { RPDefOf.RP_Yard, RPSupermaxDefOf.RP_SupermaxYard, RPDefOf.RP_Canteen, RPDefOf.RP_Holding })
                if (!EvacuationSystem.MarkableZone(z)) EvacFail($"the {z.label} is not offered as an assembly point");
            foreach (PrisonZoneDef z in new[] { RPVisitDefOf.RP_Visitation, RPSupermaxDefOf.RP_WingSupermax, RPDefOf.RP_StaffOnly })
                if (EvacuationSystem.MarkableZone(z)) EvacFail($"the {z.label} is offered as an assembly point");
        }

        // ---- the fixture ------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class EvacFixture
        {
            public SupermaxScene s;
            public EvacuationState st;
            public Pawn g1, g2, a, b, c;
            public AssemblyPoint p1, p2;
            public Building_Door[] doors;
            public IntVec3 fireCell = IntVec3.Invalid;
            /// <summary>The evacuation is kept in its Out phase through the check's ticks (as a fire burning on would), for a check that lights none and runs past the hour of the all-clear.</summary>
            public bool holdOut;
            public PrisonZoneDef Wing => RPSupermaxDefOf.RP_WingSupermax;
            public Pawn[] All => new[] { a, b, c };
            public Room ShowerRoom => s.shower.parent.GetRoom();
        }

        private static SecurityLevelDef EvacLevel(string defName) => DefDatabase<SecurityLevelDef>.GetNamed(defName);

        private static void EvacPaint(SupermaxScene s, CellRect rect, PrisonZoneDef zone)
        {
            foreach (IntVec3 c in rect)
            {
                PrisonZoneDef was = s.pm.zones.ZoneAt(c);
                s.r.Add(() => s.pm.zones.Set(c, was));
                s.pm.zones.Set(c, zone);
            }
        }

        private static void EvacDoorMode(SupermaxScene s, Building_Door door, PrisonDoorMode mode)
        {
            CompPrisonDoor comp = door.GetComp<CompPrisonDoor>();
            PrisonDoorMode was = comp.mode;
            s.r.Add(() => comp.mode = was);
            comp.mode = mode;
        }

        /// <summary>
        /// The unit for an evacuation: the three cells are one wing (zone), each locked in every hour; <c>a</c> (cell A) is a supermax inmate (or a medium one when <paramref name="walkers"/>), <c>b</c> (cell B) a medium inmate, <c>c</c> (cell C) a
        /// minimum security inmate, lying downed unless <paramref name="walkers"/>; two guards stand at the west end; the first assembly point is an ordinary yard patch in the corridor, the second the cage (the supermax yard). The shower door and the cage
        /// door are staff-only. Null when there is no open ground for the unit (a problem is noted by the scene). The levels do not match the wing's on purpose (the offer and the plan read the levels of the people standing in a wing, and
        /// the checks need walkers), so the log notes <c>b</c> and <c>c</c> (and <c>a</c> among walkers) taking "a bed of the RP_Supermax wing"; the hourly move out of a wrong wing and the daily review leave anyone on the roll call alone
        /// (<see cref="EvacuationSystem.HoldsBed"/>), and no check ticks between the fixture and the start or after the end, so that claim moves nobody here (round 4's failing line was the check's, not a move).
        /// </summary>
        private static EvacFixture BuildEvacFixture(Map map, Rollback r, string check, bool walkers = false, bool points = true, bool fallbackPatch = false, bool downed = true, bool confine = true)
        {
            SupermaxScene s = SupermaxScene.Build(map, r, "evacuation " + check);
            if (s == null) return null;
            var f = new EvacFixture { s = s, st = s.pm.evac };
            EvacuationState st = f.st;
            List<AssemblyPoint> points0 = st.points.ToList();
            int drill0 = st.lastDrillTick, letter0 = st.fireLetterTick;
            var letters0 = new Dictionary<PrisonZoneDef, int>(st.wingLetterTicks);
            r.Add(() =>
            {
                EvacuationSystem.OnlyThese = null;
                EvacuationSystem.Reset(map);
                st.points.Clear();
                st.points.AddRange(points0);
                st.lastDrillTick = drill0;
                st.fireLetterTick = letter0;
                st.wingLetterTicks.Clear();
                foreach (KeyValuePair<PrisonZoneDef, int> kv in letters0) st.wingLetterTicks[kv.Key] = kv.Value;
                // (A fire kept burning through a long check can bring a second letter for the unit's wing once its evacuation is over.)
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_WingFire") && l.note == RPSupermaxDefOf.RP_WingSupermax.defName).ToList())
                    Find.LetterStack.RemoveLetter(l);
            });
            EvacuationSystem.Reset(map);
            st.points.Clear();
            st.lastDrillTick = -999999;
            st.fireLetterTick = -999999;
            st.wingLetterTicks.Clear();
            // (The cells locked in every hour, for the length of the check, by the levels' regimes (put back by the rollback). Not for the saveload seed: the save holds the regimes, and the other seeds' are in force.)
            if (confine)
                foreach (SecurityLevelDef level in new[] { s.level, EvacLevel("RP_Medium"), EvacLevel("RP_Minimum"), RPSecurityDefOf.RP_Protective })
                    SupermaxRegimeAllDay(s.g, r, level, RPDefOf.RP_Lockup);
            s.PaintCellCAsWing();
            EvacPaint(s, new CellRect(s.origin.x + 10, s.origin.z + 1, 5, 1), RPDefOf.RP_Yard);
            if (fallbackPatch) EvacPaint(s, new CellRect(s.origin.x + 1, s.origin.z + 1, 4, 1), RPDefOf.RP_Yard);
            EvacDoorMode(s, s.showerDoor, PrisonDoorMode.StaffOnly);
            EvacDoorMode(s, s.yardDoor, PrisonDoorMode.StaffOnly);
            f.doors = new[] { s.doorA, s.doorB, s.doorC, s.showerDoor, s.yardDoor };
            s.pm.ForceDoorUpdate();
            map.reachability.ClearCache();
            if (points)
            {
                EvacuationSystem.Mark(s.pm, s.pm.zones.PatchAt(s.At(12, 1)));
                EvacuationSystem.Mark(s.pm, s.pm.zones.PatchAt(s.YardCell));
                if (st.points.Count == 2) (f.p1, f.p2) = (st.points[0], st.points[1]);
            }
            f.g1 = CareGuard(s, "Zevg1", s.At(1, 1));
            f.g2 = CareGuard(s, "Zevg2", s.At(2, 1));
            f.a = s.Inmate("Zeva", s.bedA, s.CellA, walkers ? EvacLevel("RP_Medium") : null);
            f.b = s.Inmate("Zevb", s.bedB, s.CellB, EvacLevel("RP_Medium"));
            f.c = s.Inmate("Zevc", s.bedC, s.CellC, EvacLevel("RP_Minimum"));
            if (downed && !walkers) HealthUtility.DamageUntilDowned(f.c, allowBleedingWounds: false);
            EvacuationSystem.OnlyThese = new HashSet<Pawn>(f.All);
            s.pm.ForceDoorUpdate();
            InmateCandidates.Shared.Invalidate();
            return f;
        }

        /// <summary>A fire in the shower room (a cell of the wing, beside the cells), kept burning for the check: put out by the rollback.</summary>
        private static void EvacLightFire(EvacFixture f)
        {
            f.fireCell = f.s.At(14, 5);
            SupermaxScene s = f.s;
            // (Every fire in the unit, the ones it spread to included.)
            s.r.Add(() => EvacPutOutFires(s));
            EvacKeepFire(f);
        }

        /// <summary>Every fire in the unit (and the ring round it) put out at once: a fire a check spawned may have spread in a tick.</summary>
        private static void EvacPutOutFires(SupermaxScene s)
        {
            Map map = s.map;
            foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
                if (!t.Destroyed && new CellRect(s.origin.x, s.origin.z, SupermaxScene.Width, SupermaxScene.Height).ExpandedBy(1).Contains(t.Position)) t.Destroy(DestroyMode.Vanish);
        }

        private static void EvacKeepFire(EvacFixture f)
        {
            if (!f.fireCell.IsValid || f.fireCell.GetThingList(f.s.map).Any(t => t is Fire)) return;
            var fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
            GenSpawn.Spawn(fire, f.fireCell, f.s.map);
        }

        /// <summary>What the ticks of a check saw of the evacuees.</summary>
        private sealed class EvacWatch
        {
            public readonly HashSet<IntVec3> doorsInPasses = new HashSet<IntVec3>();
            public readonly Dictionary<int, int> jobChanges = new Dictionary<int, int>(), lastJob = new Dictionary<int, int>();
            public readonly HashSet<int> sawHome = new HashSet<int>(), sawCarried = new HashSet<int>();
            /// <summary>Seen listed as rescued while lying in a bed outside the wing that may hold them (a medical bed): accounted for there, the doctors' until well.</summary>
            public readonly HashSet<int> sawRescued = new HashSet<int>();
            /// <summary>The guards' evacuation jobs (walks and carries) by their load id: the same walk given again and again is churn too.</summary>
            public readonly HashSet<int> guardJobs = new HashSet<int>();
            /// <summary>When each evacuee arrived at a place (the latest arrival seen), for the drill letter's slowest.</summary>
            public readonly Dictionary<int, int> arrivedAt = new Dictionary<int, int>();
            public readonly List<string> violations = new List<string>();

            public void Observe(EvacFixture f)
            {
                Map map = f.s.map;
                foreach (Pawn g in new[] { f.g1, f.g2 })
                    if (g is { Destroyed: false } && g.CurJob is { } gj && (gj.def == RPEvacDefOf.RP_EvacuateInmate || gj.def == RPEvacDefOf.RP_CarryEvacuee)) guardJobs.Add(gj.loadID);
                foreach (Pawn p in f.All)
                {
                    if (p == null || p.Destroyed || p.Inmate() is not { } c) continue;
                    if (c.evacDoors is { } set)
                        foreach (Building_Door d in set) doorsInPasses.Add(d.Position);
                    if (c.leg is { doors: { } legDoors })
                        foreach (Building_Door d in legDoors) doorsInPasses.Add(d.Position);
                    if (p.CarriedBy != null) sawCarried.Add(p.thingIDNumber);
                    EvacEntry entry = EvacuationSystem.EntryOf(p);
                    // (The beat that counts the last one home ends the evacuation in the same call, before this look: their line is read as it stood at the end. Round 4's run: the downed one carried to her own bed in cell C on the
                    // last beat, failed as "not seen back".)
                    if (entry is { status: EvacStatus.Home } || entry == null && EvacuationSystem.EndedEntryOf(p) is { status: EvacStatus.Home }) sawHome.Add(p.thingIDNumber);
                    if (entry is { status: EvacStatus.Rescued } && EvacuationSystem.InRescueBed(f.s.pm, p, f.Wing)) sawRescued.Add(p.thingIDNumber);
                    if (entry is { arrivedTick: >= 0 } arrived) arrivedAt[p.thingIDNumber] = arrived.arrivedTick;
                    // (Round 2's runs: vanilla's warden carry took the downed inmate from the point, and its escort the supermax inmate from the cage, back to their own beds in the wing being emptied.)
                    if (entry is { arrivedTick: >= 0 } && f.st.phase == EvacPhase.Out && p.InBed() && p.CurrentBed() is { } inBed && EvacuationSystem.WingAt(f.s.pm, inBed.Position) == f.Wing && violations.Count < 6)
                        violations.Add($"{p.LabelShort} arrived and then lay in a bed of the wing being emptied at {inBed.Position - f.s.origin} ({entry.status})");
                    int job = p.CurJob?.loadID ?? -1;
                    if (!lastJob.TryGetValue(p.thingIDNumber, out int was) || was != job)
                    {
                        lastJob[p.thingIDNumber] = job;
                        jobChanges[p.thingIDNumber] = (jobChanges.TryGetValue(p.thingIDNumber, out int n) ? n : 0) + 1;
                    }
                    if (!p.Spawned) continue;
                    if (!p.Downed && p.GetRoom() is { } room && room == f.ShowerRoom && violations.Count < 6) violations.Add($"{p.LabelShort} was in the shower room at {p.Position - f.s.origin}, a place on nobody's route");
                    if (p.Position.GetThingList(map).Any(t => t is Fire) && violations.Count < 6) violations.Add($"{p.LabelShort} stood in a fire at {p.Position - f.s.origin}");
                }
            }

            public int MostJobs() => jobChanges.Count == 0 ? 0 : jobChanges.Values.Max();
        }

        private static int EvacTicks(EvacFixture f, EvacWatch w, Func<bool> done, int most) => ShuttleTicks(() =>
        {
            EvacKeepFire(f);
            if (f.holdOut && f.st.Active && f.st.phase == EvacPhase.Out) f.st.lastFireTick = Find.TickManager.TicksGame;
            w.Observe(f);
            return done();
        }, most);

        private static EvacEntry EvacEntryOf(Pawn p) => EvacuationSystem.EntryOf(p);

        /// <summary>
        /// The evacuation's carry of a downed inmate started by hand, whole within this call and with no tick: the guard's job is ended with none found in its place (a guard put somewhere has their job ended, and the think tree finds
        /// the next - the evacuation's own giver, with a carry of its own), they are put on her cell, and started on the giver's carry to the place on her line (set it first), which has her in their arms at once. On her cell, because
        /// the carry's walk to her is ClosestTouch, which is OnCell for a pawn lying on open ground: from the cell beside her it is one step, a tick away, and the carry checks and the saveload seed, started from beside her since round 6,
        /// never had her picked up (stability round 22's round 2). Null when she is in their arms, else what stood in the way.
        /// </summary>
        private static string EvacCarryByHand(SupermaxScene s, Pawn carrier, Pawn inmate)
        {
            IntVec3 at = inmate.Position;
            carrier.jobs.StopAll();
            s.Place(carrier, at);
            Pawn reserver = s.map.reservationManager.FirstRespectedReserver(inmate, carrier);
            var giver = (WorkGiver_EvacuateInmate)RPEvacDefOf.RP_Evacuate_WorkGiver.Worker;
            Job carry = giver.JobOnThing(carrier, inmate);
            // (Not started when another holds her: the start would fail its reservation with an error.)
            if (carry != null && reserver == null) s.Start(carrier, carry);
            if (carry != null && carry.def == RPEvacDefOf.RP_CarryEvacuee && carrier.CurJob == carry && inmate.CarriedBy == carrier && EvacEntryOf(inmate) is { status: EvacStatus.Escorted }) return null;
            string Rel(IntVec3 c) => c.IsValid ? (c - s.origin).ToString() : "-";
            string where = inmate.Spawned ? "lying at " + Rel(inmate.Position) : inmate.CarriedBy is { } holder ? "in " + holder.LabelShort + "'s arms" : "gone";
            return $"the giver's job {carry?.def.defName ?? "none"}, she is reserved by {reserver?.LabelShort ?? "nobody else"}; the guard on {carrier.CurJobDef?.defName ?? "nothing"} at the toil {carrier.jobs.curDriver?.CurToilString ?? "-"} "
                + $"to {Rel(carrier.CurJob?.targetB.Cell ?? IntVec3.Invalid)}, at {Rel(carrier.Position)} (her cell {Rel(at)}), carrying {carrier.carryTracker.CarriedThing?.LabelShort ?? "nothing"}, moving {carrier.pather.Moving}; she is {where}, "
                + $"her line {EvacEntryOf(inmate)?.status.ToString() ?? "none"} to {Rel(EvacEntryOf(inmate)?.dest ?? IntVec3.Invalid)}";
        }

        /// <summary>Each evacuee's line (or, once it is over, the line they ended with), where they are in the unit's coordinates and on what job, and the bed they lie in when they do.</summary>
        private static string EvacLine(EvacFixture f) =>
            string.Join("; ", f.All.Select(p =>
            {
                EvacEntry e = EvacEntryOf(p);
                EvacEntry end = e == null ? EvacuationSystem.EndedEntryOf(p) : null;
                string status = e?.status.ToString() ?? (end != null ? end.status + " at the end" : "-");
                string bed = p.Spawned && p.CurrentBed() is { } inBed ? $" in {(inBed.OwnersForReading.Contains(p) ? "their own bed" : inBed.Medical ? "a medical bed" : "another bed")} at {inBed.Position - f.s.origin}" : "";
                return $"{p.LabelShort} {status} point {(e ?? end)?.point} at {(p.Spawned ? (p.Position - f.s.origin).ToString() : "carried")} on {p.CurJobDef?.defName ?? "nothing"}{bed}";
            }));

        // ---- the routes: what a route may pass --------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The rules of a route (<see cref="EvacRoutes"/>): the doors on the way are the pass and no other; never a locked door, a perimeter door (a side that is outside), or a staff-side door for a walker (a guard's walk may); never through or
        /// beside fire. No ticks but one (the fire mask is kept for a tick).
        /// </summary>
        private static void CheckEvacRoutesFollowTheRules(Map map)
        {
            Guarded("evacuation: what a route may pass", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "routes", walkers: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                IntVec3 toYard = s.At(12, 1), toCage = s.At(16, 4), fromA = s.CellA, fromB = s.CellB;
                EvacRoutes.Route plain = EvacRoutes.Plan(map, fromB, toYard, escorted: false);
                if (plain == null || plain.doors.Count != 1 || !plain.doors.Contains(s.doorB))
                    EvacFail($"the route from cell B to the corridor yard has {plain?.doors.Count.ToString() ?? "no route"} doors (cell B's own door and nothing else expected)");
                // The cage door is staff-only by mode, not by side: a guard's walk to the cage passes it and the cell's own door, and so does a walker's (the pass is for the doors of the route).
                EvacRoutes.Route guardCage = EvacRoutes.Plan(map, fromA, toCage, escorted: true);
                CompPrisonDoor cageComp = s.yardDoor.GetComp<CompPrisonDoor>();
                if (guardCage == null || !guardCage.doors.Contains(s.yardDoor) || !guardCage.doors.Contains(s.doorA) || guardCage.doors.Count != 2)
                    EvacFail($"the guard's route from cell A to the cage has {guardCage?.doors.Count.ToString() ?? "no route"} doors (the cell's own and the cage's expected; the cage door is {cageComp.mode}, sides {cageComp.sideA}/{cageComp.sideB})");
                // A perimeter door (a side that is outside) is on no route at all, for anybody.
                DoorSide a0 = cageComp.sideA;
                cageComp.sideA = DoorSide.Outside;
                EvacRoutes.Route outside = EvacRoutes.Plan(map, fromA, toCage, escorted: true);
                cageComp.sideA = a0;
                if (outside != null) EvacFail("a route went through a door with the outside on one side (a perimeter door is on no route)");
                // A staff side stops a walker, not a guard.
                cageComp.sideA = DoorSide.Staff;
                EvacRoutes.Route staffWalker = EvacRoutes.Plan(map, fromA, toCage, escorted: false), staffGuard = EvacRoutes.Plan(map, fromA, toCage, escorted: true);
                cageComp.sideA = a0;
                if (staffWalker != null || staffGuard == null) EvacFail($"a door into a staff room: a walker's route {(staffWalker == null ? "refused" : "found")} (refused expected), a guard's {(staffGuard == null ? "refused" : "found")} (found expected)");
                // A locked door is on no route.
                CompPrisonDoor aComp = s.doorA.GetComp<CompPrisonDoor>();
                PrisonDoorMode was = aComp.mode;
                aComp.mode = PrisonDoorMode.Locked;
                EvacRoutes.Route locked = EvacRoutes.Plan(map, fromA, toYard, escorted: true);
                aComp.mode = was;
                if (locked != null) EvacFail("a route went out through a locked door");
                // A door held by rioters is on no route either (the door verdict refuses the pass there: a route through it would be made and refused again every few seconds).
                aComp.heldCells = new HashSet<int>();
                EvacRoutes.Route held = EvacRoutes.Plan(map, fromA, toYard, escorted: true);
                aComp.heldCells = null;
                if (held != null) EvacFail("a route went out through a door held by rioters");
                // Another inmate's cell is no way through: a route to the yard from cell A passes the doors of cell A and none of B's or C's.
                EvacRoutes.Route own = EvacRoutes.Plan(map, fromA, toYard, escorted: false);
                if (own == null || own.doors.Contains(s.doorB) || own.doors.Contains(s.doorC) || own.doors.Contains(s.showerDoor)) EvacFail($"the route from cell A to the yard passed {string.Join(", ", (own?.doors ?? new HashSet<Building_Door>()).Select(d => (d.Position - s.origin).ToString()))} (cell A's door only expected)");
                // Another level's wing across the corridor: a walker does not cross it, a guard's walk may.
                if (DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.security != null && z != f.Wing) is { } otherWing)
                {
                    var across = new CellRect(s.origin.x + 7, s.origin.z + 1, 3, 1);
                    EvacPaint(s, across, otherWing);
                    EvacRoutes.Route walkerAcross = EvacRoutes.Plan(map, fromB, toYard, escorted: false), guardAcross = EvacRoutes.Plan(map, fromB, toYard, escorted: true);
                    EvacPaint(s, across, null);
                    if (walkerAcross != null || guardAcross == null) EvacFail($"another level's wing across the corridor: a walker's route {(walkerAcross == null ? "refused" : "found")} (refused expected), a guard's {(guardAcross == null ? "refused" : "found")} (found expected)");
                }
                // Fire: no way past a cell beside it (the fire is on cell C's door, the corridor in front of it is beside it and is the only way from cell B), none to a place on fire, a way again once it is out.
                var fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                GenSpawn.Spawn(fire, s.doorC.Position, map);
                ShuttleTicks(() => false, 1);
                EvacRoutes.Route beside = EvacRoutes.Plan(map, fromB, toYard, escorted: true);
                if (beside != null) EvacFail("a route went past cells beside a fire (the corridor in front of cell C's burning door)");
                if (!fire.Destroyed) fire.Destroy(DestroyMode.Vanish);
                EvacPutOutFires(s);
                ShuttleTicks(() => false, 1);
                EvacRoutes.Route after = EvacRoutes.Plan(map, fromB, toYard, escorted: true);
                if (after == null) EvacFail("no route from cell B once the fire beside its door was out");
                s.pm.ForceDoorUpdate();
            });
        }

        // ---- a fire beside occupied cells -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The headless proof of the feature: a fire in the wing, the letter offers the evacuation and nothing starts by itself, then (answered) real ticks until a supermax inmate (a guard walks them to the cage, on the pass of the walk),
        /// a downed inmate (carried) and a medium inmate (walking, on a pass for the doors of their route) are at safe places; no door off any route is in a pass, nobody is in the shower room or in a fire, no job churn; the all-clear sends all
        /// three back, and the door modes, bed owners and sanctions are as before.
        /// </summary>
        private static void CheckEvacFireWithADownedAndASupermaxInmate(Map map)
        {
            Guarded("evacuation: a fire beside occupied cells, a downed inmate and a supermax inmate", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "fire run");
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                EvacuationState st = f.st;
                if (f.p1 == null || f.p2 == null || !f.c.Downed) { RPLog.Message($"[RimPrison:test] evacuation: no run - the premises (two assembly points {f.p1 != null && f.p2 != null}, a downed inmate {f.c.Downed}) are not there (told, not failed)"); return; }
                CompInmate ac = f.a.Inmate(), bc = f.b.Inmate(), cc = f.c.Inmate();
                // The sanctions that must not change: a privilege lost, a confinement with its clock.
                int now = s.Now;
                (ac.yardBanUntilTick, cc.shopBanUntilTick) = (now + 5 * GenDate.TicksPerDay, now + 4 * GenDate.TicksPerDay);
                (bc.forcedActivity, bc.forcedUntilTick) = (RPDefOf.RP_Lockup, now + 2 * GenDate.TicksPerDay);
                string Sanctions() => $"{ac.yardBanUntilTick}/{cc.shopBanUntilTick}/{bc.forcedActivity?.defName}:{bc.forcedUntilTick}/{ac.pendingPunishmentHours}{bc.pendingPunishmentHours}{cc.pendingPunishmentHours}";
                string Beds() => string.Join(",", new[] { s.bedA, s.bedB, s.bedC }.Select(b => b.OwnersForReading.FirstOrDefault()?.thingIDNumber.ToString() ?? "-"));
                string Modes() => string.Join(",", f.doors.Select(d => d.GetComp<CompPrisonDoor>().mode.ToString()));
                (string sanctions0, string beds0, string modes0) = (Sanctions(), Beds(), Modes());
                CompPrisonDoor doorBComp = s.doorB.GetComp<CompPrisonDoor>();
                bool bClosedForAll = !doorBComp.inmatePass;
                EvacLightFire(f);
                // The letter: a fire in a wing with people in it offers the evacuation, and nothing starts by itself.
                EvacuationSystem.Beat(map, pm);
                ChoiceLetter_PrisonDecision letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_WingFire") && l.note == f.Wing.defName);
                if (letter != null) r.Add(() => Find.LetterStack.RemoveLetter(letter));
                if (letter == null) RPLog.Message("[RimPrison:test] evacuation: no fire letter for the unit's wing (another wing's fire took it, or none) - started by hand (told, not failed)");
                else
                {
                    if (st.Active) EvacFail("an evacuation started by itself when the fire reached the wing (never automatic)");
                    if (letter.decision.Worker.Disabled(letter, 0) is { } why) EvacFail($"the evacuation is not offered with two points marked: {why}");
                    letter.answered = true;
                    PrisonDecisions.Choose(letter, 0);
                    Find.LetterStack.RemoveLetter(letter);
                }
                if (!st.Active && !EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation of the wing did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                if (st.entries.Count(e => e.status != EvacStatus.Outside) != 3) EvacFail($"the roll call has {st.entries.Count} lines for three people in the wing ({string.Join(", ", st.entries.Select(e => e.name + " " + e.status))})");
                var w = new EvacWatch();
                // Planned a few a tick: within a few ticks the walker has a route and a pass, the others wait for a guard.
                EvacTicks(f, w, () => EvacEntryOf(f.b) is { status: EvacStatus.Walking } && EvacEntryOf(f.a) is { status: not EvacStatus.Planning } && EvacEntryOf(f.c) is { status: not EvacStatus.Planning }, 60);
                if (EvacEntryOf(f.b) is not { status: EvacStatus.Walking }) EvacFail($"after 60 ticks the medium inmate has no route ({EvacLine(f)})");
                bool onRoute = bc.evacDoors != null && bc.evacDoors.Contains(s.doorB);
                if (!onRoute || !s.doorB.PawnCanOpen(f.b) || s.doorA.PawnCanOpen(f.b) || s.doorC.PawnCanOpen(f.b) || s.showerDoor.PawnCanOpen(f.b) || s.yardDoor.PawnCanOpen(f.b) || bc.evacDoors?.Count != 1)
                    EvacFail($"the walker's pass: their own cell's door on it {onRoute}, can open it {s.doorB.PawnCanOpen(f.b)}, cell A's {s.doorA.PawnCanOpen(f.b)}, cell C's {s.doorC.PawnCanOpen(f.b)}, the shower door {s.showerDoor.PawnCanOpen(f.b)}, the cage door {s.yardDoor.PawnCanOpen(f.b)} (only the first two expected), {bc.evacDoors?.Count} doors in it (1 expected)");
                if (!bClosedForAll) RPLog.Message("[RimPrison:test] evacuation: cell B's door was open for everyone before the evacuation (the lockup did not hold it), so its pass is not proved by this run (told, not failed)");
                // (The downed inmate may be taken on from the point by the prison's rescue to a medical bed outside the wing: listed rescued, accounted for there. Nobody else is ever "rescued", and never in a bed of the wing.)
                bool CRescued() => EvacEntryOf(f.c) is { status: EvacStatus.Rescued } && EvacuationSystem.InRescueBed(pm, f.c, f.Wing);
                bool AllArrived() => EvacEntryOf(f.a) is { status: EvacStatus.Arrived } && EvacEntryOf(f.b) is { status: EvacStatus.Arrived } && (EvacEntryOf(f.c) is { status: EvacStatus.Arrived } || CRescued());
                int ticked = EvacTicks(f, w, AllArrived, 5000);
                if (!AllArrived()) EvacFail($"after {ticked} ticks not everyone was at a safe place ({EvacLine(f)}; the downed one in a rescue bed {EvacuationSystem.InRescueBed(pm, f.c, f.Wing)})");
                else
                {
                    bool aInCage = pm.zones.ZoneAt(f.a.Position) == RPSupermaxDefOf.RP_SupermaxYard, bInYard = pm.zones.ZoneAt(f.b.Position) == RPDefOf.RP_Yard, cInYard = CRescued() || pm.zones.ZoneAt(f.c.Position) == RPDefOf.RP_Yard;
                    if (!aInCage || !bInYard || !cInYard || EvacEntryOf(f.a).point != 1 || EvacEntryOf(f.b).point != 0 || EvacEntryOf(f.c).point != 0)
                        EvacFail($"where they went: the supermax inmate in the cage {aInCage} (point {EvacEntryOf(f.a).point}, 1 expected), the walker in the yard {bInYard} (point {EvacEntryOf(f.b).point}, 0 expected), the downed one in the yard or a rescue bed {cInYard} (point {EvacEntryOf(f.c).point}, 0 expected)");
                    if (!w.sawCarried.Contains(f.c.thingIDNumber)) EvacFail("the downed inmate was never carried (they arrived by themselves?)");
                    EvacuationSystem.Counts n = EvacuationSystem.Tally(st);
                    if (n.arrived + n.rescued != 3 || n.rescued > (CRescued() ? 1 : 0) || n.missing + n.blocked + n.waiting + n.carried != 0)
                        EvacFail($"the roll call: arrived {n.arrived}, rescued {n.rescued} (3 together expected, rescued only the downed one), blocked {n.blocked}, missing {n.missing}, waiting {n.waiting}, carried {n.carried}");
                }
                if (w.violations.Count > 0) EvacFail(string.Join("; ", w.violations));
                if (w.doorsInPasses.Contains(s.showerDoor.Position) || w.doorsInPasses.Contains(s.doorC.Position))
                    EvacFail($"a door that is on nobody's route was in a pass: {string.Join(", ", w.doorsInPasses.Select(p => (p - s.origin).ToString()))} (cell A's, cell B's and the cage door expected; not the shower door or cell C's)");
                if (!w.doorsInPasses.Contains(s.doorA.Position) || !w.doorsInPasses.Contains(s.doorB.Position) || !w.doorsInPasses.Contains(s.yardDoor.Position))
                    EvacFail($"the passes did not open the doors of the routes: {string.Join(", ", w.doorsInPasses.Select(p => (p - s.origin).ToString()))} (cell A's, cell B's and the cage door expected)");
                if (w.MostJobs() > 80) EvacFail($"job churn: one inmate was given {w.MostJobs()} jobs in the run");
                // (Two escorts out, one walk and one carry: a few more for a route the fire spread over, not the same carry handed out again and again.)
                if (w.guardJobs.Count > 8) EvacFail($"job churn: the two guards were given {w.guardJobs.Count} evacuation walks and carries for one walk and one carry");
                // The all-clear: refused while the wing burns; with the fire out, everyone goes back, the passes end, the evacuation is over.
                EvacuationSystem.AllClear(map, byWarden: true);
                if (st.phase != EvacPhase.Out) EvacFail("the warden's all-clear turned the evacuation round while the wing still burned");
                f.fireCell = IntVec3.Invalid;
                EvacPutOutFires(s);
                EvacuationSystem.AllClear(map, byWarden: true);
                if (st.phase != EvacPhase.Back) EvacFail($"the warden's all-clear did not turn the evacuation round once the fire was out (fire in the wing {EvacuationSystem.FireInWing(map, pm, f.Wing)})");
                ticked = EvacTicks(f, w, () => !st.Active, 7000);
                if (st.Active) EvacFail($"{ticked} ticks after the all-clear the evacuation was not over ({EvacLine(f)})");
                // (Back in their own cell; or, for the downed one only, rescued to a medical bed outside the wing, where they stay until well - the all-clear does not move a patient, and their bed is still theirs.)
                foreach (Pawn p in f.All)
                    if (!w.sawHome.Contains(p.thingIDNumber) && !(p == f.c && w.sawRescued.Contains(p.thingIDNumber)))
                        EvacFail($"{p.LabelShort} was not seen back in their own bed's room{(p == f.c ? " nor rescued to a medical bed outside the wing" : "")} ({EvacLine(f)})");
                if (f.All.Any(p => p.Inmate().evacDoors != null || p.Inmate().leg != null)) EvacFail("a pass or a walk was still on after the evacuation was over");
                if (Sanctions() != sanctions0 || Beds() != beds0 || Modes() != modes0)
                    EvacFail($"after the evacuation sanctions {sanctions0} -> {Sanctions()}, bed owners {beds0} -> {Beds()}, door modes {modes0} -> {Modes()} (none changed expected)");
            });
        }

        // ---- a route blocked, a point destroyed -------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// One cell's door is locked (no route out for its inmate: listed as blocked, tried again), the first point is repainted away (whoever was going to it is planned again and goes to the second), the second goes too (the nearest safe room
        /// they may enter, listed as the fallback), two hours pass (the blocked inmate is listed missing for want of a way, and stays so while the door is locked, still the evacuation's), and the door is unlocked (round 6: the missing
        /// inmate is tried again and gets out after all, to the fallback room; rounds 3-5 expected them to stay missing until the all-clear). No job churn throughout.
        /// </summary>
        private static void CheckEvacBlockedRouteAndDestroyedPoint(Map map)
        {
            Guarded("evacuation: a route blocked, a point destroyed", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "blocked", walkers: true, fallbackPatch: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                EvacuationState st = f.st;
                if (f.p1 == null || f.p2 == null) { RPLog.Message("[RimPrison:test] evacuation: no blocked-route run - two assembly points could not be marked (told, not failed)"); return; }
                f.p2.levels = new List<SecurityLevelDef> { EvacLevel("RP_Minimum"), EvacLevel("RP_Medium"), s.level };
                // (No fire is lit, and the check runs past the hour after which the all-clear would come by itself: the missing inmate is judged in the Out phase, the all-clear is the warden's at the end.)
                f.holdOut = true;
                CompPrisonDoor aComp = s.doorA.GetComp<CompPrisonDoor>();
                PrisonDoorMode modeA = aComp.mode;
                aComp.mode = PrisonDoorMode.Locked;
                r.Add(() => aComp.mode = modeA);
                pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                var w = new EvacWatch();
                EvacTicks(f, w, () => EvacEntryOf(f.a) is { status: EvacStatus.Blocked } && EvacEntryOf(f.b) is { status: EvacStatus.Walking }, 120);
                if (EvacEntryOf(f.a) is not { status: EvacStatus.Blocked, blockedSeen: true }) EvacFail($"the inmate in the cell with the locked door is not listed as route blocked ({EvacLine(f)})");
                if (EvacEntryOf(f.b) is not { status: EvacStatus.Walking, point: 0 }) EvacFail($"the walker is not on the way to the first point ({EvacLine(f)})");
                // The first point is repainted away: the walker is planned again and goes to the second.
                foreach (IntVec3 c in new CellRect(s.origin.x + 10, s.origin.z + 1, 5, 1)) EvacPaint(s, new CellRect(c.x, c.z, 1, 1), null);
                int ticked = EvacTicks(f, w, () => EvacEntryOf(f.b) is { status: EvacStatus.Arrived, point: 1 }, 3000);
                if (EvacEntryOf(f.b) is not { status: EvacStatus.Arrived, point: 1, fallback: false } || pm.zones.ZoneAt(f.b.Position) != RPSupermaxDefOf.RP_SupermaxYard)
                    EvacFail($"after the first point was gone ({ticked} ticks) the walker is not at the second ({EvacLine(f)})");
                // The second goes too: the nearest safe room they may enter, listed as the fallback.
                foreach (IntVec3 c in new CellRect(s.origin.x + 16, s.origin.z + 3, 2, 3)) EvacPaint(s, new CellRect(c.x, c.z, 1, 1), null);
                ticked = EvacTicks(f, w, () => EvacEntryOf(f.b) is { status: EvacStatus.Arrived, fallback: true }, 3500);
                bool inPatch3 = f.b.Position.z == s.origin.z + 1 && f.b.Position.x >= s.origin.x + 1 && f.b.Position.x <= s.origin.x + 4;
                if (EvacEntryOf(f.b) is not { status: EvacStatus.Arrived, fallback: true } || !inPatch3)
                    EvacFail($"with both points gone ({ticked} ticks) the walker is not in the nearest safe room, listed as the fallback ({EvacLine(f)}, in the corridor patch {inPatch3})");
                // Two hours with no way out: listed missing (unreachable). Missing they stay while there is no way - not "found again" on the next beat because they are still on the map - but still the evacuation's (kept from the regime,
                // the supermax walks and vanilla's carry), tried again quietly, and out once their door is unlocked.
                EvacEntry ea = EvacEntryOf(f.a);
                if (ea is { status: EvacStatus.Blocked })
                {
                    int left = Math.Max(0, EvacuationSystem.MissingAfterTicks - (s.Now - ea.sinceTick)) + 2 * EvacuationSystem.BeatTicks;
                    ticked = EvacTicks(f, w, () => EvacEntryOf(f.a) is { status: EvacStatus.Missing }, left);
                }
                if (EvacEntryOf(f.a) is not { status: EvacStatus.Missing, missingOffMap: false }) { EvacFail($"two hours with no way out did not list the blocked inmate as missing (unreachable) ({EvacLine(f)}, off the map {EvacEntryOf(f.a)?.missingOffMap})"); return; }
                EvacTicks(f, w, () => EvacEntryOf(f.a) is not { status: EvacStatus.Missing }, EvacuationSystem.RetryTicks + 2 * EvacuationSystem.BeatTicks);
                if (EvacEntryOf(f.a) is not { status: EvacStatus.Missing }) EvacFail($"the inmate listed missing for want of a way was taken off it on a later beat while still shut in their cell ({EvacLine(f)})");
                bool missingInProgress = EvacEntryOf(f.a) is { InProgress: true };
                if (!EvacuationSystem.HasJobFor(f.a) || !EvacuationSystem.Involves(f.a) || !EvacuationSystem.BeingEmptied(f.a) || missingInProgress)
                    EvacFail($"the inmate missing for want of a way is out of the evacuation's hands, or holds it up: its job {EvacuationSystem.HasJobFor(f.a)}, kept from the regime {EvacuationSystem.Involves(f.a)}, kept from vanilla's carry {EvacuationSystem.BeingEmptied(f.a)} (all three expected), on the way {missingInProgress} (not expected)");
                aComp.mode = modeA;
                pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                // (Tried again within a retry and a beat or two, then the walk to the fallback room the walker went to.)
                ticked = EvacTicks(f, w, () => EvacEntryOf(f.a) is { status: EvacStatus.Arrived }, EvacuationSystem.RetryTicks + 2 * EvacuationSystem.BeatTicks + 2500);
                if (EvacEntryOf(f.a) is not { status: EvacStatus.Arrived }) EvacFail($"{ticked} ticks after their door was unlocked the inmate missing for want of a way was not out at a safe place ({EvacLine(f)})");
                // The all-clear sends them home with the rest, and the evacuation ends without waiting for its cap.
                EvacuationSystem.AllClear(map, byWarden: true);
                ticked = EvacTicks(f, w, () => !st.Active, EvacuationSystem.BackCapTicks / 2);
                if (st.Active) EvacFail($"{ticked} ticks after the all-clear the evacuation was not over ({EvacLine(f)})");
                if (!w.sawHome.Contains(f.a.thingIDNumber)) EvacFail($"the inmate who was missing for want of a way, then out, was not counted home after the all-clear ({EvacLine(f)})");
                if (w.violations.Count > 0) EvacFail(string.Join("; ", w.violations));
                if (w.MostJobs() > 80) EvacFail($"job churn: one inmate was given {w.MostJobs()} jobs in the run (a blocked route, a point gone, a fallback)");
            });
        }

        // ---- the second point, and the letter ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The letter without an assembly point offers nothing (and answering it starts nothing), with one it does; a keep-apart pair is split between two points, protective custody goes to the second, and with one point only the
        /// pair ends together and the roll call says so.
        /// </summary>
        private static void CheckEvacSecondPointAndTheLetter(Map map)
        {
            Guarded("evacuation: the letter, and a second point for those kept apart", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "second point", walkers: true, points: false);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                EvacuationState st = f.st;
                // (Cell C holds a protective custody inmate, A and B are the pair.)
                f.c.Inmate().security = RPSecurityDefOf.RP_Protective;
                EvacLightFire(f);
                EvacuationSystem.Beat(map, pm);
                ChoiceLetter_PrisonDecision letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_WingFire") && l.note == f.Wing.defName);
                if (letter == null) { RPLog.Message("[RimPrison:test] evacuation: no fire letter for the unit's wing (told, not failed)"); return; }
                r.Add(() => Find.LetterStack.RemoveLetter(letter));
                if (letter.decision.Worker.Disabled(letter, 0) == null) EvacFail("the evacuation was offered with no assembly point marked");
                letter.answered = true;
                PrisonDecisions.Choose(letter, 0);
                if (st.Active) EvacFail("answering the letter with no assembly point started an evacuation");
                EvacuationSystem.Mark(pm, pm.zones.PatchAt(s.At(12, 1)));
                EvacuationSystem.Mark(pm, pm.zones.PatchAt(s.YardCell));
                if (st.points.Count != 2) { RPLog.Message("[RimPrison:test] evacuation: two points could not be marked (told, not failed)"); return; }
                (f.p1, f.p2) = (st.points[0], st.points[1]);
                st.points[1].levels = new List<SecurityLevelDef> { EvacLevel("RP_Minimum"), EvacLevel("RP_Medium"), RPSecurityDefOf.RP_Protective };
                if (letter.decision.Worker.Disabled(letter, 0) is { } why) EvacFail($"the evacuation is not offered with two points marked: {why}");
                KeepApartOrder order = null;
                if (KeepApart.Add(f.a, f.b, "warden", silent: true) == KeepApartResult.Added)
                {
                    order = KeepApart.OrderBetween(f.a, f.b);
                    r.Add(() => KeepApart.Lift(order, silent: true));
                }
                else { RPLog.Message("[RimPrison:test] evacuation: the keep-apart order could not be written (told, not failed)"); return; }
                PrisonDecisions.Choose(letter, 0);
                Find.LetterStack.RemoveLetter(letter);
                if (!st.Active) { EvacFail("answering the letter with two points did not start the evacuation"); return; }
                var w = new EvacWatch();
                EvacTicks(f, w, () => f.All.All(p => EvacEntryOf(p) is { status: not EvacStatus.Planning }), 90);
                EvacEntry ea = EvacEntryOf(f.a), eb = EvacEntryOf(f.b), ec = EvacEntryOf(f.c);
                int first = Math.Min(f.a.thingIDNumber, f.b.thingIDNumber);
                EvacEntry early = f.a.thingIDNumber == first ? ea : eb, late = f.a.thingIDNumber == first ? eb : ea;
                if (early.point != 0 || late.point != 1 || ec.point != 1 || !late.second || !ec.second)
                    EvacFail($"the points: the kept-apart pair at {early.point} and {late.point} (0 and 1 expected), protective custody at {ec.point} (1 expected), second flags {late.second}/{ec.second}");
                // With one point only the pair ends together, and says so.
                EvacuationSystem.Unmark(pm, st.points[1]);
                EvacTicks(f, w, () => f.All.All(p => EvacEntryOf(p) is { status: EvacStatus.Arrived }) && EvacuationSystem.SeparationFailures(st) > 0, 4000);
                if (EvacuationSystem.SeparationFailures(st) != 1) EvacFail($"with one point left the kept-apart pair was not reported together ({EvacuationSystem.SeparationFailures(st)} pairs together, {EvacLine(f)})");
                if (w.violations.Count > 0) EvacFail(string.Join("; ", w.violations));
            });
        }

        // ---- the drill ------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A drill without a fire: the same walk, a small mood memory for each inmate it moved (not the one who never left their cell), the letter that names the slowest arrival and the route that was blocked, nobody sent back to a bed
        /// that is not theirs, and not a second one the same day.
        /// </summary>
        private static void CheckEvacDrill(Map map)
        {
            Guarded("evacuation: the drill", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "drill", walkers: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                EvacuationState st = f.st;
                if (f.p1 == null) { RPLog.Message("[RimPrison:test] evacuation: no drill run - the assembly points could not be marked (told, not failed)"); return; }
                EvacDoorMode(s, s.doorC, PrisonDoorMode.Locked);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (EvacuationSystem.CannotStart(map, f.Wing, drill: true) is { } why) { EvacFail($"a drill could not start: {why}"); return; }
                string beds0 = string.Join(",", new[] { s.bedA, s.bedB, s.bedC }.Select(b => b.OwnersForReading.FirstOrDefault()?.thingIDNumber.ToString() ?? "-"));
                int lettersBefore = Find.LetterStack.LettersListForReading.Count;
                if (!EvacuationSystem.Start(map, f.Wing, drill: true) || !st.drill) { EvacFail("the drill did not start"); return; }
                if (EvacuationSystem.CannotStart(map, f.Wing, drill: true) == null) EvacFail("a second drill could be started while one runs (and the same day)");
                var w = new EvacWatch();
                int ticked = EvacTicks(f, w, () => !st.Active, 9000);
                if (st.Active) { EvacFail($"the drill was not over after {ticked} ticks ({EvacLine(f)})"); return; }
                if (EvacuationSystem.CannotStart(map, f.Wing, drill: true) == null) EvacFail($"a second drill could be started on the same day ({(s.Now - st.lastDrillTick) / (float)GenDate.TicksPerHour:0.0} h after the first began)");
                // (A day after it began, the next may: the rule is a rolling day, which no turn of the calendar's day in the middle of a drill can open early.)
                int drillTick0 = st.lastDrillTick;
                st.lastDrillTick = s.Now - GenDate.TicksPerDay - 1;
                if (EvacuationSystem.CannotStart(map, f.Wing, drill: true) is { } dayAfter && dayAfter == "RP_EvacDrilledToday".Translate().ToString()) EvacFail("a day after the drill began, another was still refused as the same day's");
                st.lastDrillTick = drillTick0;
                string label = "RP_EvacDrillLabel".Translate(f.Wing.label).ToString();
                Letter letter = Find.LetterStack.LettersListForReading.Skip(lettersBefore).LastOrDefault(l => l.Label.ToString() == label) ?? Find.LetterStack.LettersListForReading.LastOrDefault(l => l.Label.ToString() == label);
                if (letter != null) r.Add(() => Find.LetterStack.RemoveLetter(letter));
                string text = letter is StandardLetter std ? std.Text.ToString() : "";
                // (The slowest is the one who arrived last, as the ticks saw it: the blocked inmate's name in the letter does not stand in for it.)
                Pawn slowest = w.arrivedAt.Count == 0 ? null : f.All.Where(p => w.arrivedAt.ContainsKey(p.thingIDNumber)).OrderByDescending(p => w.arrivedAt[p.thingIDNumber]).FirstOrDefault();
                bool namesSlowest = slowest != null && text.Contains(slowest.LabelShort + ", after");
                bool names = namesSlowest && text.Contains(f.c.LabelShort);
                if (letter == null || !names || !text.Contains("Route blocked")) EvacFail($"the drill's letter: found {letter != null}, names the slowest ({slowest?.LabelShort ?? "nobody arrived"}) {namesSlowest} and the blocked inmate {text.Contains(f.c.LabelShort)}, says a route was blocked {text.Contains("Route blocked")} ({text.Replace('\n', '/')})");
                foreach (Pawn p in new[] { f.a, f.b })
                    if (p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPEvacDefOf.RP_EvacuationDrill) == null) EvacFail($"{p.LabelShort} was moved in the drill and has no memory of it");
                if (f.c.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPEvacDefOf.RP_EvacuationDrill) != null) EvacFail($"{f.c.LabelShort} never left their cell and has a memory of the drill");
                string beds1 = string.Join(",", new[] { s.bedA, s.bedB, s.bedC }.Select(b => b.OwnersForReading.FirstOrDefault()?.thingIDNumber.ToString() ?? "-"));
                if (beds1 != beds0) EvacFail($"bed owners changed in the drill: {beds0} -> {beds1}");
                if (w.violations.Count > 0) EvacFail(string.Join("; ", w.violations));
            });
        }

        // ---- no ticks: the plan alone -------------------------------------------------------------------------------------------------------------------------

        /// <summary>Plans everyone the evacuation has queued (a few a call, as the ticks would), with no game tick.</summary>
        private static void EvacPlanAll(EvacFixture f)
        {
            for (int i = 0; i < 10 && f.st.Queued > 0; i++) EvacuationSystem.TickPlanning(f.s.map, f.s.pm);
        }

        /// <summary>
        /// A route is judged by the cells still ahead (<see cref="EvacuationSystem.RouteInvalid"/>): a fire behind the walker (in the cell they left) leaves it be, one ahead makes it bad, and so does a door ahead that was locked since. The
        /// masks are made by hand, no tick and no fire: the walker is placed along the way.
        /// </summary>
        private static void CheckEvacRouteAhead(Map map)
        {
            Guarded("evacuation: a route is judged by the cells still ahead", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "route ahead", walkers: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                if (f.p1 == null) { RPLog.Message("[RimPrison:test] evacuation: no route-ahead check - no assembly point could be marked (told, not failed)"); return; }
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                EvacEntry ea = EvacEntryOf(f.a), eb = EvacEntryOf(f.b);
                if (eb is not { status: EvacStatus.Walking, route: not null } || ea is not { status: EvacStatus.Walking, route: not null }) { EvacFail($"the two walkers have no route after planning ({EvacLine(f)})"); return; }
                CellIndices ci = map.cellIndices;
                // (On their way: out of cell B and in the corridor in front of it.)
                s.Place(f.b, s.At(6, 1));
                if (EvacuationSystem.RouteInvalid(pm, eb, f.b, new HashSet<int> { ci.CellToIndex(s.At(6, 3)) }))
                    EvacFail("a fire behind the walker (in the cell they had walked out of) made their route bad: only the cells still ahead count");
                if (!EvacuationSystem.RouteInvalid(pm, eb, f.b, new HashSet<int> { ci.CellToIndex(s.At(8, 1)) }))
                    EvacFail("a fire ahead of the walker (the corridor on the way to the yard) did not make their route bad");
                // A door still ahead (cell A's own, the walker still in the cell) locked since the route was made.
                if (EvacuationSystem.RouteInvalid(pm, ea, f.a, null)) EvacFail("a route with nothing wrong on it was judged bad");
                CompPrisonDoor aComp = s.doorA.GetComp<CompPrisonDoor>();
                PrisonDoorMode was = aComp.mode;
                aComp.mode = PrisonDoorMode.Locked;
                bool lockedAhead = EvacuationSystem.RouteInvalid(pm, ea, f.a, null);
                aComp.mode = was;
                if (!lockedAhead) EvacFail("a door still ahead that was locked since did not make the route bad");
            });
        }

        /// <summary>A feud in the evacuation: one of the two goes to the second point, not both (a feud is two-sided; both at the second would stand together there). No ticks: the groups are set when it starts.</summary>
        private static void CheckEvacFeud(Map map)
        {
            Guarded("evacuation: one of a feud goes to the second point", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "feud", walkers: true);
                if (f == null) return;
                if (f.p1 == null || f.p2 == null) { RPLog.Message("[RimPrison:test] evacuation: no feud check - two assembly points could not be marked (told, not failed)"); return; }
                f.p2.levels = new List<SecurityLevelDef> { EvacLevel("RP_Minimum"), EvacLevel("RP_Medium") };
                PrisonRelations.MakeEnemies(f.a, f.b);
                if (!PrisonRelations.Enemies(f.a, f.b) || !PrisonRelations.Enemies(f.b, f.a)) { RPLog.Message("[RimPrison:test] evacuation: no feud check - the two could not be made enemies both ways (told, not failed)"); return; }
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                bool aFirst = f.a.thingIDNumber < f.b.thingIDNumber;
                EvacEntry early = EvacEntryOf(aFirst ? f.a : f.b), late = EvacEntryOf(aFirst ? f.b : f.a);
                if (early == null || late == null || early.second || !late.second)
                    EvacFail($"a feud: the first of the two (by id) to the second point {early?.second} (false expected), the other {late?.second} (true expected)");
                EvacPlanAll(f);
                if (EvacEntryOf(f.a)?.point is int pa && EvacEntryOf(f.b)?.point is int pb && pa >= 0 && pa == pb) EvacFail($"a feud: both sent to point {pa} ({EvacLine(f)})");
            });
        }

        /// <summary>
        /// Supermax is never with anyone else and never left in the general population without a guard: a general yard the warden ticked supermax on is not theirs, the cage of their own takes one of them (the other has no place and waits
        /// in the cell, listed blocked), and with the cage gone neither goes to the general yard as a fallback. No ticks: the plan alone.
        /// </summary>
        private static void CheckEvacSupermaxAlone(Map map)
        {
            Guarded("evacuation: supermax alone in a cage of its own, never the general yard", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "supermax alone", walkers: true, points: false);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                EvacuationState st = f.st;
                f.a.Inmate().security = s.level;
                f.b.Inmate().security = s.level;
                EvacuationSystem.Mark(pm, pm.zones.PatchAt(s.At(12, 1)));
                if (st.points.Count != 1) { RPLog.Message("[RimPrison:test] evacuation: no supermax check - the corridor yard could not be marked (told, not failed)"); return; }
                if (!st.points[0].Takes(s.level)) EvacuationSystem.ToggleLevel(st.points[0], s.level);
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                PrisonZoneDef ZoneOfDest(Pawn p) => EvacEntryOf(p) is { dest: { IsValid: true } d } ? pm.zones.ZoneAt(d) : null;
                Pawn[] supermax = { f.a, f.b };
                int caged = supermax.Count(p => EvacEntryOf(p) is { fallback: true } && ZoneOfDest(p) == RPSupermaxDefOf.RP_SupermaxYard);
                int blocked = supermax.Count(p => EvacEntryOf(p) is { status: EvacStatus.Blocked });
                if (supermax.Any(p => ZoneOfDest(p) == RPDefOf.RP_Yard) || caged != 1 || blocked != 1)
                    EvacFail($"supermax with the general yard ticked for them and one cage: in the general yard {supermax.Count(p => ZoneOfDest(p) == RPDefOf.RP_Yard)} (0 expected), in the cage {caged} (1 expected), waiting in the cell {blocked} (1 expected) ({EvacLine(f)})");
                if (EvacEntryOf(f.c) is not { status: EvacStatus.Walking, point: 0 }) EvacFail($"the minimum security walker is not on the way to the general yard ({EvacLine(f)})");
                // The cage gone: no fallback into the general population for them.
                EvacuationSystem.Reset(map);
                EvacPaint(s, new CellRect(s.origin.x + 16, s.origin.z + 3, 2, 3), null);
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start again: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                if (supermax.Any(p => EvacEntryOf(p) is not { status: EvacStatus.Blocked } || ZoneOfDest(p) == RPDefOf.RP_Yard))
                    EvacFail($"with no cage of their own a supermax inmate was given a place ({EvacLine(f)}; waiting in the cell, listed blocked, expected)");
            });
        }

        /// <summary>
        /// The hole beside a wing is part of it: an inmate serving solitary in a solitary cell beside the wing's cells is among the people in it (and waits for a guard), and a fire in the solitary cell is a fire in the wing. No ticks:
        /// the fire is put out before anything burns.
        /// </summary>
        private static void CheckEvacTheHole(Map map)
        {
            Guarded("evacuation: the hole beside a wing is evacuated with it", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "the hole", walkers: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                if (f.p1 == null) { RPLog.Message("[RimPrison:test] evacuation: no hole check - no assembly point could be marked (told, not failed)"); return; }
                EvacPaint(s, new CellRect(s.origin.x + 5, s.origin.z + 3, 3, 3), RPSecurityDefOf.RP_SolitaryZone);
                CompInmate bc = f.b.Inmate();
                (bc.forcedActivity, bc.forcedUntilTick) = (RPSecurityDefOf.RP_Solitary, s.Now + 2 * GenDate.TicksPerDay);
                pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (pm.zones.ZoneAt(f.b.Position) != RPSecurityDefOf.RP_SolitaryZone) { RPLog.Message("[RimPrison:test] evacuation: no hole check - cell B could not be painted as a solitary cell (told, not failed)"); return; }
                if (EvacuationSystem.WingAt(pm, f.b.Position) != f.Wing) EvacFail($"the solitary cell beside the wing's cells is not part of the wing (it is {EvacuationSystem.WingAt(pm, f.b.Position)?.defName ?? "none"})");
                if (!EvacuationSystem.Occupants(map, pm, f.Wing).Exists(o => o.pawn == f.b && o.inside)) EvacFail("the inmate in the hole beside the wing is not among the people in it");
                var fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                GenSpawn.Spawn(fire, s.At(7, 5), map);
                bool burning = EvacuationSystem.BurningWings(map, pm).Contains(f.Wing);
                if (!fire.Destroyed) fire.Destroy(DestroyMode.Vanish);
                if (!burning) EvacFail("a fire in the solitary cell beside the wing is not a fire in the wing (no letter, no button)");
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                if (EvacEntryOf(f.b) is not { escort: true } eb || !eb.Moving) EvacFail($"the inmate in the hole is not moved by a guard ({EvacLine(f)}, escort {EvacEntryOf(f.b)?.escort})");
                if (bc.forcedActivity != RPSecurityDefOf.RP_Solitary) EvacFail("starting the evacuation changed the hole's sentence");
            });
        }

        /// <summary>
        /// A downed evacuee is the evacuation's to carry: neither vanilla's carry to a bed (which knows only their bed, in the wing) nor the prison's rescue takes them while they wait, nor the carry to solitary with a punishment owed
        /// (round 6: the hole beside the wing is part of it); at the point the rescue may (to a bed that is not in the wing), vanilla still not; after the all-clear vanilla's carry and the carry to solitary are back. No ticks: the givers'
        /// own tests.
        /// </summary>
        private static void CheckEvacNoBedInTheWing(Map map)
        {
            Guarded("evacuation: a downed evacuee is not carried to a bed in the wing", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "no bed in the wing");
                if (f == null) return;
                SupermaxScene s = f.s;
                if (f.p1 == null || !f.c.Downed) { RPLog.Message($"[RimPrison:test] evacuation: no bed check - the premises (a point {f.p1 != null}, a downed inmate {f.c.Downed}) are not there (told, not failed)"); return; }
                if (!VanillaGiverLists.TakeToBedCandidate(f.c)) { RPLog.Message("[RimPrison:test] evacuation: no bed check - vanilla would not carry the downed inmate to a bed anyway (told, not failed)"); return; }
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                EvacEntry ec = EvacEntryOf(f.c);
                if (ec is not { status: EvacStatus.WaitingEscort }) { EvacFail($"the downed inmate is not waiting to be carried out ({EvacLine(f)})"); return; }
                if (VanillaGiverLists.TakeToBedCandidate(f.c) || WorkGiver_RescueInmate.Candidate(f.c))
                    EvacFail($"while waiting to be carried out: vanilla's carry to a bed {VanillaGiverLists.TakeToBedCandidate(f.c)}, the rescue {WorkGiver_RescueInmate.Candidate(f.c)} (neither expected)");
                // (Nor the carry to solitary with a punishment owed: the hole beside the wing is part of it, and their own cell is in it. It comes after the all-clear.)
                CompInmate pc = f.c.Inmate();
                int owed0 = pc.pendingPunishmentHours;
                r.Add(() => pc.pendingPunishmentHours = owed0);
                pc.pendingPunishmentHours = 6;
                if (WorkGiver_TakeToPunishment.Candidate(f.c)) EvacFail("while the wing is emptied the carry to solitary would take the downed evacuee who owes a punishment");
                pc.pendingPunishmentHours = owed0;
                // At the point (put there by hand, as the carry would).
                s.Place(f.c, ec.dest);
                EvacuationSystem.Beat(map, s.pm);
                if (ec.status != EvacStatus.Arrived) { EvacFail($"the downed inmate laid at their place is not listed as arrived ({EvacLine(f)})"); return; }
                if (VanillaGiverLists.TakeToBedCandidate(f.c)) EvacFail("at the point vanilla's carry to a bed (their own, in the wing) would take the downed inmate");
                // (The giver itself, not only its list: the work scan finds a warden giver's inmates region by region round the guard and reads the list only when that finds none.)
                Job vanillaCarry = WorkGiver_Warden_TakeToBed.TryMakeJob(f.g1, f.c, forced: true);
                if (vanillaCarry != null) EvacFail($"at the point vanilla's warden giver made {vanillaCarry.def.defName} to {vanillaCarry.targetB.Thing?.Position - s.origin} for the downed inmate (none expected while the wing is emptied)");
                if (!WorkGiver_RescueInmate.Candidate(f.c)) EvacFail("at the point the prison's rescue does not see the downed inmate (a bed outside the wing is theirs)");
                if (new[] { s.bedA, s.bedB, s.bedC }.Any(b => EvacuationSystem.BedAllowed(f.c, b))) EvacFail("a bed in the wing being emptied is allowed for the rescue of an evacuee");
                // (Nor a free bed outside the wing: vanilla's carry would make it theirs, and bed owners never change in an evacuation. A medical bed takes no owner.)
                if (IntakeSystem.FreePrisonerBeds(map, f.c.Inmate().security, f.c.gender).FirstOrDefault(b => !b.Medical && EvacuationSystem.WingAt(s.pm, b.Position) != f.Wing) is { } freeBed)
                {
                    if (EvacuationSystem.BedAllowed(f.c, freeBed)) EvacFail($"a free bed outside the wing at {freeBed.Position} is allowed for the rescue of an evacuee (the carry would take it as theirs)");
                }
                else RPLog.Message("[RimPrison:test] evacuation: no free bed outside the wing for the downed inmate's level - the rescue's no-claim rule not shown (told, not failed)");
                // The supermax inmate in the cage cannot reach their own bed (the cell is shut to them): vanilla's escort back to it makes no job either.
                EvacEntry ea = EvacEntryOf(f.a);
                bool aAtCage = false;
                if (ea is { dest: { IsValid: true } cageCell })
                {
                    s.Place(f.a, cageCell);
                    EvacuationSystem.Beat(map, s.pm);
                    aAtCage = ea.status == EvacStatus.Arrived;
                    Job vanillaEscort = aAtCage ? WorkGiver_Warden_TakeToBed.TryMakeJob(f.g1, f.a, forced: true) : null;
                    if (vanillaEscort != null) EvacFail($"vanilla's warden giver made {vanillaEscort.def.defName} for the supermax inmate arrived at the cage (none expected while the wing is emptied)");
                }
                if (!aAtCage) RPLog.Message($"[RimPrison:test] evacuation: the supermax inmate could not be put arrived at the cage - vanilla's escort not asked ({EvacLine(f)}) (told, not failed)");
                EvacuationSystem.AllClear(map, byWarden: true);
                if (!VanillaGiverLists.TakeToBedCandidate(f.c)) EvacFail("after the all-clear vanilla's carry to a bed is still off for the downed inmate");
                pc.pendingPunishmentHours = 6;
                if (!WorkGiver_TakeToPunishment.Candidate(f.c)) EvacFail("after the all-clear the carry to solitary is still off for the downed evacuee who owes a punishment");
                pc.pendingPunishmentHours = owed0;
                // (The premise: the same giver does make the carry once the wing is no longer being emptied, so the refusal above was the evacuation's.)
                if (WorkGiver_Warden_TakeToBed.TryMakeJob(f.g1, f.c, forced: true) == null) RPLog.Message("[RimPrison:test] evacuation: vanilla's warden giver made no carry for the downed inmate after the all-clear either - the refusal at the point is not proved the evacuation's (told, not failed)");
            });
        }

        // ---- round 6: review 2's must-fixes and should-fixes (no ticks: the beats and the plan are called by hand) --------------------------------------------

        /// <summary>
        /// A fire in the wing after the all-clear (review 2's first must-fix): the way back is called off and the wing is emptied again - those already home are on the roll call's way out again and the evacuation's own, no letter
        /// is sent for the wing (it runs already), and the warden's all-clear is refused while it burns. The same after a drill's all-clear: it is a real evacuation from then on, and its letter is not sent again. Written in round 6
        /// without a game run.
        /// </summary>
        private static void CheckEvacFireAfterTheAllClear(Map map)
        {
            Guarded("evacuation: a fire in the wing after the all-clear", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "fire after the all-clear", walkers: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                EvacuationState st = f.st;
                if (f.p1 == null) { RPLog.Message("[RimPrison:test] evacuation: no fire-after-the-all-clear check - no assembly point could be marked (told, not failed)"); return; }
                string drillLabel = "RP_EvacDrillLabel".Translate(f.Wing.label).ToString();
                var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                r.Add(() =>
                {
                    foreach (Letter l in Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l) && l.Label.ToString() == drillLabel).ToList()) Find.LetterStack.RemoveLetter(l);
                });
                int WingLetters() => Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is("RP_Decision_WingFire") && l.note == f.Wing.defName);
                int DrillLetters() => Find.LetterStack.LettersListForReading.Count(l => l.Label.ToString() == drillLabel);
                void Run(bool drill)
                {
                    string what = drill ? "after a drill's all-clear" : "after the all-clear";
                    if (!EvacuationSystem.Start(map, f.Wing, drill)) { EvacFail($"{what}: the evacuation did not start ({EvacuationSystem.CannotStart(map, f.Wing, drill)})"); return; }
                    EvacPlanAll(f);
                    // (One still on the way back, at the corridor yard, so that the beat that counts the other two home does not end it.)
                    s.Place(f.b, s.At(12, 1));
                    EvacuationSystem.AllClear(map, byWarden: true);
                    if (st.phase != EvacPhase.Back) { EvacFail($"{what}: the warden's all-clear with no fire did not turn the evacuation round"); return; }
                    EvacuationSystem.Beat(map, pm);
                    if (!st.Active || EvacEntryOf(f.a) is not { status: EvacStatus.Home } || EvacEntryOf(f.c) is not { status: EvacStatus.Home })
                    {
                        RPLog.Message($"[RimPrison:test] evacuation: {what} - the two in their cells were not counted home on the beat ({EvacLine(f)}, running {st.Active}) - not judged (told, not failed)");
                        return;
                    }
                    int wing0 = WingLetters(), drill0 = DrillLetters();
                    EvacLightFire(f);
                    EvacuationSystem.Beat(map, pm);
                    int now = s.Now;
                    if (!st.Active || st.phase != EvacPhase.Out || st.drill || st.lastFireTick != now || st.backTick != -1)
                        EvacFail($"{what}: a fire in the wing did not turn the evacuation back out (running {st.Active}, phase {st.phase}, drill {st.drill}, last fire {st.lastFireTick} at {now}, all-clear tick {st.backTick})");
                    if (f.All.Any(p => EvacEntryOf(p) is not { status: EvacStatus.Planning }))
                        EvacFail($"{what}: with the wing on fire again not everyone was listed to be taken out again, those home among them ({EvacLine(f)}; all on the way expected)");
                    if (!EvacuationSystem.BeingEmptied(f.a) || !EvacuationSystem.HasJobFor(f.a)) EvacFail($"{what}: one who was home is not the evacuation's again (being emptied {EvacuationSystem.BeingEmptied(f.a)}, its job {EvacuationSystem.HasJobFor(f.a)})");
                    if (WingLetters() != wing0) EvacFail($"{what}: a fire letter was sent for the wing whose evacuation runs ({WingLetters() - wing0} more)");
                    if (DrillLetters() != drill0) EvacFail($"{what}: the drill's letter was sent again ({DrillLetters() - drill0} more)");
                    EvacuationSystem.AllClear(map, byWarden: true);
                    if (st.phase != EvacPhase.Out) EvacFail($"{what}: the warden's all-clear was taken while the wing burned");
                }
                // (Whatever a half found, the fire is put out and the evacuation dropped before the other half.)
                void RunAndClear(bool drill)
                {
                    try { Run(drill); }
                    finally
                    {
                        f.fireCell = IntVec3.Invalid;
                        EvacPutOutFires(s);
                        EvacuationSystem.Reset(map);
                        s.Place(f.b, s.CellB);
                    }
                }
                RunAndClear(drill: false);
                if (EvacuationSystem.CannotStart(map, f.Wing, drill: true) is { } why) RPLog.Message($"[RimPrison:test] evacuation: no drill half of the fire-after-the-all-clear check - {why} (told, not failed)");
                else RunAndClear(drill: true);
            });
        }

        /// <summary>
        /// Supermax and a cage the warden ticked other levels on (review 2's A): the supermax inmate is planned first and has the cage to themselves - the medium walker is not sent in with them - and a supermax inmate is not sent to a cage a
        /// medium inmate is going to. Written in round 6 without a game run.
        /// </summary>
        private static void CheckEvacCageNotShared(Map map)
        {
            Guarded("evacuation: a cage ticked for other levels is not shared with supermax", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "cage not shared", walkers: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                if (f.p1 == null || f.p2 == null) { RPLog.Message("[RimPrison:test] evacuation: no shared-cage check - two assembly points could not be marked (told, not failed)"); return; }
                EvacuationSystem.Unmark(pm, f.p1);
                f.p2.levels = new List<SecurityLevelDef> { EvacLevel("RP_Medium"), s.level };
                f.a.Inmate().security = s.level;
                int cage = pm.zones.PatchAt(s.YardCell);
                bool InCage(Pawn p) => EvacEntryOf(p) is { dest: { IsValid: true } d } && pm.zones.PatchAt(d) == cage;
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                if (!InCage(f.a) || InCage(f.b) || InCage(f.c))
                    EvacFail($"a cage ticked for medium too: the supermax inmate sent there {InCage(f.a)} (expected), the medium walker {InCage(f.b)}, the minimum one {InCage(f.c)} (neither expected) ({EvacLine(f)})");
                // The other way round: both medium (the cage takes them, together), then one is supermax and planned again - not into the cage the other is going to.
                EvacuationSystem.Reset(map);
                f.a.Inmate().security = EvacLevel("RP_Medium");
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start again: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                if (!InCage(f.a) || !InCage(f.b)) { RPLog.Message($"[RimPrison:test] evacuation: the two medium inmates were not both sent to the cage ({EvacLine(f)}) - the other half not judged (told, not failed)"); return; }
                f.a.Inmate().security = s.level;
                EvacuationSystem.Replan(pm, EvacEntryOf(f.a));
                EvacPlanAll(f);
                if (InCage(f.a)) EvacFail($"a supermax inmate was sent to the cage a medium inmate is going to ({EvacLine(f)})");
            });
        }

        /// <summary>
        /// A downed inmate with no way out (their cell door locked; review 2's 2b): listed blocked, and the prison's rescue may take them to a bed outside the wing (no guard of the evacuation carries one with no way), never to their own in
        /// the wing; vanilla's carry to their bed still not. Written in round 6 without a game run.
        /// </summary>
        private static void CheckEvacDownedWithNoWay(Map map)
        {
            Guarded("evacuation: a downed inmate with no way out may be rescued", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "downed with no way");
                if (f == null) return;
                SupermaxScene s = f.s;
                if (f.p1 == null || !f.c.Downed) { RPLog.Message($"[RimPrison:test] evacuation: no downed-with-no-way check - the premises (a point {f.p1 != null}, a downed inmate {f.c.Downed}) are not there (told, not failed)"); return; }
                EvacDoorMode(s, s.doorC, PrisonDoorMode.Locked);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                if (EvacEntryOf(f.c) is not { status: EvacStatus.Blocked }) { EvacFail($"the downed inmate behind a locked door is not listed route blocked ({EvacLine(f)})"); return; }
                if (!WorkGiver_RescueInmate.Candidate(f.c)) EvacFail("the prison's rescue does not see the downed inmate with no way out (nobody else would carry them)");
                if (EvacuationSystem.BedAllowed(f.c, s.bedC)) EvacFail("the downed inmate's own bed, in the wing being emptied, is allowed for their rescue");
                if (!EvacuationSystem.BeingEmptied(f.c) || VanillaGiverLists.TakeToBedCandidate(f.c)) EvacFail($"vanilla's carry to a bed would take the downed inmate with no way out (being emptied {EvacuationSystem.BeingEmptied(f.c)}, vanilla's list {VanillaGiverLists.TakeToBedCandidate(f.c)})");
            });
        }

        /// <summary>
        /// A carry planned again on the way (review 2's B): the point the guard carries the downed inmate to is repainted away, the beat plans them again, and the guard keeps them in their arms and turns for the new place (the fallback
        /// room) - not put down where they stand. No ticks: the carry is started by hand (<see cref="EvacCarryByHand"/>). Written in round 6 without a game run; until stability round 22's round 2 the carry was started from beside her,
        /// never had her in the guard's arms, and the check was told away in every run.
        /// </summary>
        private static void CheckEvacCarryGoesOn(Map map)
        {
            Guarded("evacuation: a carry planned again goes on to the new place", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "carry goes on", fallbackPatch: true);
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                // (The set-up's premises are the check's own: one that is not there is a failure of the set-up, said exactly - round 2 of stability round 22; until then they were told, and the carry was never had.)
                if (f.p1 == null || !f.c.Downed) { EvacFail($"the carry check's set-up - the premises (a point {f.p1 != null}, a downed inmate {f.c.Downed}) are not there"); return; }
                Pawn carrier = f.g2;
                r.Add(() =>
                {
                    if (carrier is { Destroyed: false, Spawned: true }) carrier.jobs.StopAll();
                });
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                EvacEntry ec = EvacEntryOf(f.c);
                if (ec is not { status: EvacStatus.WaitingEscort, point: 0 }) { EvacFail($"the carry check's set-up - the downed inmate is not waiting to be carried to the first point ({EvacLine(f)})"); return; }
                // (The other guard holds no job on her either: ended, with nothing found in its place.)
                f.g1.jobs.StopAll();
                if (EvacCarryByHand(s, carrier, f.c) is { } notCarried) { EvacFail($"the carry check's set-up - the carry could not be started by hand: {notCarried} ({EvacLine(f)})"); return; }
                IntVec3 old = ec.dest;
                foreach (IntVec3 c in new CellRect(s.origin.x + 10, s.origin.z + 1, 5, 1)) EvacPaint(s, new CellRect(c.x, c.z, 1, 1), null);
                EvacuationSystem.Beat(map, pm);
                if (f.c.CarriedBy != carrier || carrier.CurJobDef != RPEvacDefOf.RP_CarryEvacuee) EvacFail($"planning the carry again put the inmate down (carried {f.c.CarriedBy != null}, the guard on {carrier.CurJobDef?.defName})");
                EvacPlanAll(f);
                IntVec3 goal = carrier.CurJob?.targetB.Cell ?? IntVec3.Invalid;
                bool inFallback = ec.dest.IsValid && ec.dest.z == s.origin.z + 1 && ec.dest.x >= s.origin.x + 1 && ec.dest.x <= s.origin.x + 4;
                if (f.c.CarriedBy != carrier || ec.status != EvacStatus.Escorted || goal != ec.dest || !inFallback || ec.dest == old)
                    EvacFail($"after the point went: still carried {f.c.CarriedBy == carrier}, {ec.status} (Escorted expected), the carry's place {goal - s.origin}, the line's {ec.dest - s.origin} (the same, in the fallback room, not the old {old - s.origin}, expected)");
            });
        }

        // ---- stability round 22 -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A carry to the end of its place (stability round 22: the fire run's "nine walks and carries for one walk and one carry" were one carry handed out eight times): the downed inmate is carried to the east end of the corridor
        /// yard and laid on that cell, where the guard stands - not on the free corridor cell beyond it, outside the yard, where vanilla's Near put her (a downed pawn goes on no cell a pawn stands on) - so she is arrived, and carried
        /// once. The other guard waits on the yard cell west of the end, so that Near has one free cell beside the end and it is the one outside the yard (without him the cell inside or the one outside would be first by the radial
        /// order). The carry is started by hand (<see cref="EvacCarryByHand"/>); a few hundred real ticks. Round 2: round 1's runs never had the carry (started from beside her, the guard never got to her without a tick), and the check
        /// told it away; its set-up is the check's own now, and a premise that is not there fails it, saying why.
        /// </summary>
        private static void CheckEvacCarryLaidAtTheEdge(Map map)
        {
            Guarded("evacuation: a carry to the end of a place lays the inmate in it", map, r =>
            {
                EvacFixture f = BuildEvacFixture(map, r, "carry to the edge");
                if (f == null) return;
                SupermaxScene s = f.s;
                PrisonMap pm = s.pm;
                if (f.p1 == null || !f.c.Downed) { EvacFail($"the edge carry check's set-up - the premises (a point {f.p1 != null}, a downed inmate {f.c.Downed}) are not there"); return; }
                // (The yard patch is the corridor's x 10..14: its east end, the cell west of it for the other guard, and the corridor cell beyond it, of the same room and free.)
                IntVec3 edge = s.At(14, 1), inside = s.At(13, 1), beyond = s.At(15, 1);
                int patch = pm.zones.PatchAt(s.At(12, 1));
                if (patch < 0 || pm.zones.PatchAt(edge) != patch || pm.zones.PatchAt(inside) != patch || !inside.Standable(map) || pm.zones.PatchAt(beyond) == patch || !beyond.Standable(map) || beyond.GetRoom(map) != edge.GetRoom(map)
                    || beyond.GetFirstPawn(map) != null)
                {
                    EvacFail($"the edge carry check's set-up - the yard's east end {edge - s.origin} (patch {pm.zones.PatchAt(edge)}, the yard's {patch}), the cell west of it {inside - s.origin} (patch {pm.zones.PatchAt(inside)}, standable {inside.Standable(map)}) "
                        + $"and the corridor beyond it {beyond - s.origin} (patch {pm.zones.PatchAt(beyond)}, standable {beyond.Standable(map)}, the same room {beyond.GetRoom(map) == edge.GetRoom(map)}, a pawn on it {beyond.GetFirstPawn(map)?.LabelShort ?? "none"}) are not as built");
                    return;
                }
                Pawn carrier = f.g2, other = f.g1;
                r.Add(() =>
                {
                    foreach (Pawn g in new[] { carrier, other })
                        if (g is { Destroyed: false, Spawned: true }) g.jobs.StopAll();
                });
                // (Only the downed one on the roll call, so nobody walks into the yard.)
                EvacuationSystem.OnlyThese = new HashSet<Pawn> { f.c };
                f.holdOut = true;
                if (!EvacuationSystem.Start(map, f.Wing, drill: false)) { EvacFail($"the evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}"); return; }
                EvacPlanAll(f);
                EvacEntry ec = EvacEntryOf(f.c);
                if (ec is not { status: EvacStatus.WaitingEscort, point: 0 }) { EvacFail($"the edge carry check's set-up - the downed inmate is not waiting to be carried to the first point ({EvacLine(f)})"); return; }
                // (Her place first, so that any carry made from here on is to it.)
                ec.dest = edge;
                ec.route = null;
                // (The other guard: his job ended with none found in its place (a placement would end it and the evacuation's giver would give him her carry), put on the cell west of the end, and waiting there.)
                other.jobs.StopAll();
                s.Place(other, inside);
                Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                wait.expiryInterval = 3000;
                s.Start(other, wait);
                if (other.CurJob != wait || other.Position != inside) { EvacFail($"the edge carry check's set-up - the other guard is not waiting on the cell west of the end (on {other.CurJobDef?.defName ?? "nothing"} at {other.Position - s.origin})"); return; }
                if (EvacCarryByHand(s, carrier, f.c) is { } notCarried) { EvacFail($"the edge carry check's set-up - the carry could not be started by hand: {notCarried} ({EvacLine(f)})"); return; }
                if (carrier.CurJob.targetB.Cell != edge) { EvacFail($"the edge carry check's set-up - the carry by hand is to {carrier.CurJob.targetB.Cell - s.origin}, not to the yard's east end {edge - s.origin} ({EvacLine(f)})"); return; }
                var w = new EvacWatch();
                int ticked = EvacTicks(f, w, () => f.c.CarriedBy == null && carrier.CurJobDef != RPEvacDefOf.RP_CarryEvacuee, 1500);
                IntVec3 laid = f.c.PositionHeld;
                bool laidIn = f.c.Spawned && EvacuationSystem.InPlace(pm, edge, f.c.Position);
                EvacStatus after = ec.status;
                string otherAt = other.Position == inside ? "" : $", the other guard had left the cell west of the end for {other.Position - s.origin} on {other.CurJobDef?.defName ?? "nothing"}";
                // (Two beats on: a carry handed out again is among the guards' jobs by then.)
                EvacTicks(f, w, () => false, 2 * EvacuationSystem.BeatTicks);
                if (!laidIn || after != EvacStatus.Arrived || w.guardJobs.Count != 1)
                    EvacFail($"a carry to the yard's east end {edge - s.origin}: laid at {laid - s.origin} after {ticked} ticks, in the yard {laidIn}, {after} (Arrived expected), {w.guardJobs.Count} carries given in all (1 expected){otherAt} ({EvacLine(f)})");
            });
        }
    }

    /// <summary>
    /// Autotest: a listed evacuee tucked into a bed by somebody's carry - who carried them, on what job, driver and giver, and whose bed it is, where (round 4's run ended with the downed one on LayDown in a bed the log never named; the
    /// beat's own line, <c>EvacuationSystem.LogLying</c>, says what they lie on once per bed but cannot name the carrier, gone by the next beat).
    /// </summary>
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.TuckIntoBed))]
    [TestOnly]
    public static class Patch_RestUtility_TuckIntoBed_LogEvacuee
    {
        public static void Prefix(Building_Bed bed, Pawn taker, Pawn takee)
        {
            if (!AutoTest.Active || bed == null || takee == null || taker == takee || EvacuationSystem.EntryOf(takee) is not { } e || takee.MapHeld is not { } map || PrisonMap.Of(map) is not { } pm) return;
            Job job = taker?.CurJob;
            RPLog.Message($"[RimPrison:evac] {takee.LabelShort} ({e.status}, {pm.evac.phase}) is tucked into a bed by {taker?.LabelShort ?? "nobody"} on {job?.def?.defName ?? "no job"}, driver {taker?.jobs?.curDriver?.GetType().Name ?? "none"}, from {job?.workGiverDef?.defName ?? job?.jobGiver?.GetType().Name ?? "no giver"}; {EvacuationSystem.BedWords(pm, pm.evac, takee, bed)}; their place {(e.dest.IsValid ? e.dest.ToString() : "(none)")}");
        }
    }
}
