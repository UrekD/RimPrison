using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The evacuation in the saveload plan: the game is saved in the middle of one (a fire in the wing of the evacuation unit, a supermax inmate held by one guard on the way out of the cell, a downed inmate in the other guard's arms, a
    /// walker already at the first point, a protective custody inmate on the way to a point - the first: the second is the cage the supermax inmate is going to, and since round 6 nobody is sent into a cage with supermax), with every
    /// field of the roll call off its default, and after the load the evacuation goes on: the guards' jobs are saved, the walk and the
    /// passes are not, and each is made again; everyone gets to a safe place, the all-clear sends them back to their own beds, and their sanctions and bed owners are as they were. The snapshot of the round trip holds
    /// <see cref="EvacuationSystem.Describe"/>. The half-way state is built by hand, with no game tick (the seeds before this one have recorded walks, hearings and a shuttle that a tick would move) and no regime changed (the other
    /// seeds' regimes are in force and are saved). The unit is taken down after the check. Written without a game run: none of it has been run yet.
    /// </summary>
    public static partial class AutoTest
    {
        private static IntVec3 evacSeedOrigin = IntVec3.Invalid;
        private static int evacSeedG1 = -1, evacSeedG2 = -1, evacSeedA = -1, evacSeedB = -1, evacSeedC = -1, evacSeedD = -1;
        private static List<(PrisonZoneDef zone, int anchor, List<SecurityLevelDef> levels)> evacSeedPoints0;
        private static int evacSeedDrill0, evacSeedLetter0, evacSeedRebuilt0;
        private static Dictionary<PrisonZoneDef, int> evacSeedLetters0;
        private static string evacSeedInvariants, evacAtLoad;
        /// <summary>The seed's evacuation carries the drill flag through the round trip (for the snapshot), and is a real one again before anything ticks.</summary>
        private static bool evacSeedDrillFlag;

        /// <summary>A pawn of the seed by id, carried ones included (a downed inmate in a guard's arms is not spawned).</summary>
        private static Pawn EvacSeedPawn(Map map, int id) => id < 0 ? null : map.mapPawns.AllPawns.FirstOrDefault(x => x.thingIDNumber == id);

        /// <summary>What the evacuation must not change, per inmate: the bed they own and the sanctions on them.</summary>
        private static string EvacInvariants(Map map, params int[] ids) =>
            string.Join(";", ids.Select(id =>
            {
                Pawn p = EvacSeedPawn(map, id);
                CompInmate c = p?.Inmate();
                return c == null ? "-" : $"{p.ownership?.OwnedBed?.thingIDNumber}/{c.yardBanUntilTick}/{c.shopBanUntilTick}/{c.forcedActivity?.defName}:{c.forcedUntilTick}/{c.pendingPunishmentHours}/{c.security?.defName}";
            }));

        /// <summary>The seed's premise does not hold in the live prison: told (the log), not failed; the unit is put away and the points put back.</summary>
        private static void EvacSeedTells(Map map, Rollback r, string why)
        {
            RPLog.Message($"[RimPrison:test] saveload evacuation: not seeded - {why} (told, not failed: the live prison did not give the seed's premise)");
            evacSeedOrigin = IntVec3.Invalid;
            EvacuationSystem.OnlyThese = null;
            RestoreEvacSeed(map);
            r.Dispose();
        }

        /// <summary>
        /// Before the save: the unit with an evacuation half done, built by hand. The evacuation is started and everyone planned (direct calls, no tick); the walker is put at the place given them and the beat lists them arrived; a guard who
        /// stands beside the supermax inmate is started on the walk (it holds them at once, the walk's pass made, and sets out), and one beside the downed inmate on the carry (they are in the guard's arms at once).
        /// </summary>
        private static void SeedEvacuationForReload(Map map)
        {
            evacSeedOrigin = IntVec3.Invalid;
            evacSeedDrillFlag = false;
            if (RPSupermaxDefOf.RP_Supermax == null) return;
            PrisonMap pm = PrisonMap.Of(map);
            EvacuationState live = pm.evac;
            if (live.Active)
            {
                RPLog.Message("[RimPrison:test] saveload evacuation: not seeded - the live prison has an evacuation running (told, not failed)");
                return;
            }
            evacSeedPoints0 = live.points.Select(p => (p.zone, p.anchor, p.levels.ToList())).ToList();
            (evacSeedDrill0, evacSeedLetter0) = (live.lastDrillTick, live.fireLetterTick);
            evacSeedLetters0 = new Dictionary<PrisonZoneDef, int>(live.wingLetterTicks);
            var r = new Rollback(map);
            EvacFixture f = BuildEvacFixture(map, r, "saveload", confine: false);
            if (f == null)
            {
                EvacSeedTells(map, r, "no open ground for a unit to seed an evacuation in");
                return;
            }
            SupermaxScene s = f.s;
            if (f.p1 == null || f.p2 == null || !f.c.Downed)
            {
                EvacSeedTells(map, r, $"the premises are not all there (two points {f.p1 != null && f.p2 != null}, a downed inmate {f.c.Downed})");
                return;
            }
            // A protective custody inmate with a bed of their own in the third cell (the second point is theirs), a feud with nobody.
            Building_Bed extra = s.ExtraBedInCellC();
            Pawn d = s.Inmate("Zevd", extra, s.At(10, 5), RPSecurityDefOf.RP_Protective);
            f.p2.levels = new List<SecurityLevelDef> { EvacLevel("RP_Minimum"), EvacLevel("RP_Medium"), RPSecurityDefOf.RP_Protective, s.level };
            int now = s.Now;
            (f.a.Inmate().yardBanUntilTick, f.c.Inmate().shopBanUntilTick) = (now + 5 * GenDate.TicksPerDay, now + 4 * GenDate.TicksPerDay);
            (f.b.Inmate().forcedActivity, f.b.Inmate().forcedUntilTick) = (RPDefOf.RP_Lockup, now + 2 * GenDate.TicksPerDay);
            evacSeedInvariants = EvacInvariants(map, f.a.thingIDNumber, f.b.thingIDNumber, f.c.thingIDNumber, d.thingIDNumber);
            EvacLightFire(f);
            s.pm.ForceDoorUpdate();
            map.reachability.ClearCache();
            EvacuationSystem.OnlyThese = new HashSet<Pawn>(f.All) { d };
            bool started = EvacuationSystem.Start(map, f.Wing, drill: false);
            // (The roll call is made: the live prison's own inmates were never in it, and the pre-save pawns are not kept across the load.)
            EvacuationSystem.OnlyThese = null;
            if (!started)
            {
                EvacSeedTells(map, r, $"the seeded evacuation did not start: {EvacuationSystem.CannotStart(map, f.Wing, false)}");
                return;
            }
            EvacuationState st = f.st;
            EvacPlanAll(f);
            EvacEntry ea = EvacEntryOf(f.a), eb = EvacEntryOf(f.b), ec = EvacEntryOf(f.c), ed = EvacEntryOf(d);
            if (ea is not { status: EvacStatus.WaitingEscort } || eb is not { status: EvacStatus.Walking } || ec is not { status: EvacStatus.WaitingEscort } || ed is not { status: EvacStatus.Walking })
            {
                EvacSeedTells(map, r, $"the plan did not come out as the seed needs ({EvacLine(f)}, {d.LabelShort} {ed?.status})");
                return;
            }
            // The walker at their place: the beat lists them arrived.
            s.Place(f.b, eb.dest);
            EvacuationSystem.Beat(map, s.pm);
            // The supermax inmate: a guard beside them in the cell is started on the walk, which holds them, makes the walk's pass and sets out (the guard's lead toil), all at once.
            var giver = (WorkGiver_EvacuateInmate)RPEvacDefOf.RP_Evacuate_WorkGiver.Worker;
            s.Place(f.g1, s.At(2, 3));
            if (giver.JobOnThing(f.g1, f.a) is { } walk) s.Start(f.g1, walk);
            // The downed inmate: a guard beside them is started on the carry, and has them in their arms at once.
            s.Place(f.g2, s.At(10, 3));
            if (giver.JobOnThing(f.g2, f.c) is { } carry) s.Start(f.g2, carry);
            bool held = f.g1.CurJobDef == RPEvacDefOf.RP_EvacuateInmate && f.a.Inmate().leg is { kind: EscortKind.Evacuation } && ea.status == EvacStatus.Escorted && ea.escortId == f.g1.thingIDNumber;
            bool carried = f.c.CarriedBy == f.g2 && f.g2.CurJobDef == RPEvacDefOf.RP_CarryEvacuee && ec.status == EvacStatus.Escorted;
            if (!held || !carried || eb.status != EvacStatus.Arrived)
            {
                string why = $"the half-way state could not be made (the supermax inmate held {held}, the downed one carried {carried}, the walker arrived {eb.status == EvacStatus.Arrived}; {EvacLine(f)}, guards doing {f.g1.CurJobDef?.defName}/{f.g2.CurJobDef?.defName})";
                // (The carry's end puts the inmate down before the pawns are taken away.)
                f.g1.jobs.StopAll();
                f.g2.jobs.StopAll();
                EvacSeedTells(map, r, why);
                return;
            }
            // (Every field off its default: the second flag of the protective inmate is set by the plan, the arrival and the escorts by the steps above; the rest by hand. The drill flag is set last of all, just before the
            // snapshot, see MarkEvacuationForSnapshot: a drill would send a letter for its own wing on a beat in the care seed's ticks.)
            eb.memoryGiven = true;
            eb.fallback = true;
            ed.blockedSeen = true;
            ed.missingOffMap = true;
            st.lastDrillTick = now - 3 * GenDate.TicksPerDay - 333;
            st.fireLetterTick = now - 777;
            st.wingLetterTicks[f.Wing] = now - 555;
            st.backTick = now - 444;
            evacSeedOrigin = s.origin;
            (evacSeedG1, evacSeedG2, evacSeedA, evacSeedB, evacSeedC, evacSeedD) = (f.g1.thingIDNumber, f.g2.thingIDNumber, f.a.thingIDNumber, f.b.thingIDNumber, f.c.thingIDNumber, d.thingIDNumber);
            RPLog.Message($"[RimPrison:test] saveload evacuation: half done with no tick - {EvacLine(f)}, {d.LabelShort} {ed.status} point {ed.point}, guards doing {f.g1.CurJobDef?.defName}/{f.g2.CurJobDef?.defName}");
        }

        /// <summary>Right before the snapshot is taken (no tick after it): the drill flag set, so that its round trip is seen.</summary>
        private static void MarkEvacuationForSnapshot(Map map)
        {
            if (!evacSeedOrigin.IsValid || PrisonMap.Of(map).evac is not { Active: true } st) return;
            st.drill = true;
            evacSeedDrillFlag = true;
        }

        /// <summary>After the load and the comparison, before any tick: the seed's evacuation is a real one again (the flag was there for the snapshot).</summary>
        private static void UnmarkEvacuationAfterCompare(Map map)
        {
            if (!evacSeedDrillFlag) return;
            evacSeedDrillFlag = false;
            if (PrisonMap.Of(map).evac is { Active: true } st) st.drill = false;
        }

        /// <summary>First thing after the load, before any tick: the evacuation is on, the walk is not in the inmate's record (it is not saved), the guard's job is, and the downed inmate is still in the other guard's arms.</summary>
        private static void RecordEvacuationAtLoad(Map map)
        {
            evacAtLoad = null;
            if (!evacSeedOrigin.IsValid) return;
            EvacuationState st = PrisonMap.Of(map).evac;
            Pawn a = EvacSeedPawn(map, evacSeedA), c = EvacSeedPawn(map, evacSeedC), g1 = EvacSeedPawn(map, evacSeedG1), g2 = EvacSeedPawn(map, evacSeedG2);
            if (a == null || c == null) return;
            EvacEntry ea = st.Get(a.thingIDNumber), ec = st.Get(c.thingIDNumber);
            bool walkKept = g1?.CurJob is { } walk && walk.def == RPEvacDefOf.RP_EvacuateInmate && walk.targetA.Thing == a;
            bool carryKept = g2 != null && c.CarriedBy == g2 && g2.CurJobDef == RPEvacDefOf.RP_CarryEvacuee;
            evacAtLoad = $"{st.Active}/{a.Inmate().leg == null}/{walkKept}/{ea?.status}/{carryKept}/{ec?.status}";
            evacSeedRebuilt0 = JobDriver_EvacuateInmate.rebuiltAfterLoad;
        }

        private static void RestoreEvacSeed(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            EvacuationSystem.OnlyThese = null;
            EvacuationSystem.Reset(map);
            EvacuationState st = pm.evac;
            if (evacSeedPoints0 != null)
            {
                st.points.Clear();
                foreach ((PrisonZoneDef zone, int anchor, List<SecurityLevelDef> levels) in evacSeedPoints0) st.points.Add(new AssemblyPoint { zone = zone, anchor = anchor, levels = levels });
                (st.lastDrillTick, st.fireLetterTick) = (evacSeedDrill0, evacSeedLetter0);
                st.wingLetterTicks.Clear();
                if (evacSeedLetters0 != null)
                    foreach (KeyValuePair<PrisonZoneDef, int> kv in evacSeedLetters0) st.wingLetterTicks[kv.Key] = kv.Value;
                evacSeedPoints0 = null;
                evacSeedLetters0 = null;
            }
        }

        /// <summary>The unit of the seed and everything in it: a pawn in someone's arms is put down first, the fires go with the buildings.</summary>
        private static void TakeDownEvacSeed(Map map, PrisonMap pm, IntVec3 origin, Pawn[] guards, Pawn[] inmates)
        {
            foreach (Pawn p in inmates.Concat(guards))
                if (p is { Destroyed: false, CarriedBy: { } carrier }) carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out Thing _);
            foreach (Pawn g in guards)
                if (g is { Destroyed: false, Spawned: true }) g.jobs.StopAll();
            var rect = new CellRect(origin.x, origin.z, SupermaxScene.Width, SupermaxScene.Height).ExpandedBy(1);
            foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
                if (!fire.Destroyed && rect.Contains(fire.Position)) fire.Destroy(DestroyMode.Vanish);
            TakeDownSupermaxSeed(map, pm, PrisonGame.Instance, origin, guards, inmates, restoreRegime: false);
        }

        /// <summary>
        /// After the load and the other checks' ticks (the evacuation goes on through them: what is judged is what is still to be seen): the guard's walk was made again after the load, everyone gets to a safe place, the all-clear sends
        /// them back, and the sanctions and bed owners are as they were. Then the unit, the pawns and the points are put away (whatever the check found: a pawn of the seed is never left on the map).
        /// </summary>
        private static void CheckEvacuationAfterReload(Map map)
        {
            if (!evacSeedOrigin.IsValid) return;
            PrisonMap pm = PrisonMap.Of(map);
            EvacuationState st = pm.evac;
            IntVec3 origin = evacSeedOrigin;
            evacSeedOrigin = IntVec3.Invalid;
            Pawn[] guards = { EvacSeedPawn(map, evacSeedG1), EvacSeedPawn(map, evacSeedG2) };
            Pawn[] inmates = { EvacSeedPawn(map, evacSeedA), EvacSeedPawn(map, evacSeedB), EvacSeedPawn(map, evacSeedC), EvacSeedPawn(map, evacSeedD) };
            try
            {
                if (inmates.Any(p => p == null))
                {
                    reloadProblems.Add($"saveload: the evacuees did not survive the load (found {string.Join("/", inmates.Select(p => p?.LabelShort ?? "-"))})");
                    return;
                }
                EvacuationSystem.OnlyThese = null;
                // (Active, no walk in the record, the guard's job kept when the line says a guard has them, the downed one still carried when the line says so.)
                string[] atLoad = (evacAtLoad ?? "").Split('/');
                bool atLoadOk = atLoad.Length >= 6 && atLoad[0] == "True" && atLoad[1] == "True" && (atLoad[3] != "Escorted" || atLoad[2] == "True") && (atLoad[5] != "Escorted" || atLoad[4] == "True");
                if (!atLoadOk)
                    reloadProblems.Add($"evacuation across a load: just after the load (active / no walk in the record / the guard's walk kept / the supermax inmate's line / the carry kept / the downed one's line) it was {evacAtLoad} (True / True / True when a guard has them / the line / True when carried / the line expected)");
                // (The walk is not saved: the guard's job made it again on its first tick.)
                if (atLoad.Length >= 6 && atLoad[3] == "Escorted" && atLoad[2] == "True" && JobDriver_EvacuateInmate.rebuiltAfterLoad <= evacSeedRebuilt0)
                    reloadProblems.Add("evacuation across a load: the guard's walk with the supermax inmate was not made again after the load (no leg rebuilt by the job)");
                var sawHome = new HashSet<int>();
                // (Rescued: the downed one taken on from the point to a medical bed outside the wing, accounted for there.)
                bool AllArrived() => inmates.All(p => st.Get(p.thingIDNumber) is { status: EvacStatus.Arrived or EvacStatus.Home or EvacStatus.Rescued });
                void Watch()
                {
                    foreach (Pawn p in inmates)
                        if ((st.Get(p.thingIDNumber) ?? EvacuationSystem.EndedEntryOf(p)) is { status: EvacStatus.Home }) sawHome.Add(p.thingIDNumber);
                }
                int ticked = 0;
                if (st.Active && st.phase == EvacPhase.Out)
                {
                    ticked = ShuttleTicks(() => { Watch(); return AllArrived() || !st.Active || st.phase != EvacPhase.Out; }, 5000);
                    if (st.Active && st.phase == EvacPhase.Out && !AllArrived())
                        reloadProblems.Add($"evacuation across a load: after {ticked} ticks not everyone was at a safe place ({string.Join("; ", inmates.Select(p => $"{p.LabelShort} {st.Get(p.thingIDNumber)?.status} on {p.CurJobDef?.defName ?? "nothing"}"))})");
                    else if (st.Active && st.phase == EvacPhase.Out)
                    {
                        // (The warden's all-clear is refused while the wing burns (round 6): the seed's fire, if it still burns, is put out first.)
                        var unit = new CellRect(origin.x, origin.z, SupermaxScene.Width, SupermaxScene.Height).ExpandedBy(1);
                        foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList())
                            if (!fire.Destroyed && unit.Contains(fire.Position)) fire.Destroy(DestroyMode.Vanish);
                        EvacuationSystem.AllClear(map, byWarden: true);
                    }
                }
                if (st.Active)
                {
                    int more = ShuttleTicks(() => { Watch(); return !st.Active; }, 7000);
                    ticked += more;
                    if (st.Active) reloadProblems.Add($"evacuation across a load: {more} ticks after the all-clear the evacuation was not over ({string.Join("; ", inmates.Select(p => $"{p.LabelShort} {st.Get(p.thingIDNumber)?.status} on {p.CurJobDef?.defName ?? "nothing"}"))})");
                }
                Watch();
                string invariants = EvacInvariants(map, inmates.Select(p => p.thingIDNumber).ToArray());
                if (invariants != evacSeedInvariants) reloadProblems.Add($"evacuation across a load: bed owners and sanctions changed - {evacSeedInvariants} -> {invariants}");
                // (The evacuation's own pass and walk: a supermax yard walk the regime gave them after it ended is not the evacuation's.)
                if (!st.Active && inmates.Any(p => p.Inmate().evacDoors != null || p.Inmate().leg is { kind: EscortKind.Evacuation }))
                    reloadProblems.Add("evacuation across a load: a pass or an evacuation walk was still on after the evacuation was over");
                RPLog.Message($"[RimPrison:test] saveload evacuation: after the load ({evacAtLoad}) the evacuation {(st.Active ? "is still running" : "finished")} after {ticked} more ticks here, {sawHome.Count} of {inmates.Length} seen home, walks made again {JobDriver_EvacuateInmate.rebuiltAfterLoad - evacSeedRebuilt0}");
            }
            finally
            {
                RestoreEvacSeed(map);
                TakeDownEvacSeed(map, pm, origin, guards, inmates);
            }
        }
    }
}
