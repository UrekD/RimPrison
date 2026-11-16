using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The jobs of one pawn a check is watching: every job that ends is written down (its number, when it began and ended, how, and the mod and vanilla methods that were on the stack when it was ended). For a check that
    /// asks why a cut never came to its end: the line of the job that was cut short names who cut it. Nothing is traced unless a check names a pawn (<see cref="Begin"/>).
    /// </summary>
    internal static class JobTrace
    {
        /// <summary>The pawn whose jobs are written down (null: nobody).</summary>
        internal static Pawn Who;

        private static readonly List<string> lines = new List<string>();

        /// <summary>The longest list kept: the last of them, which is what a check that fails at the end wants.</summary>
        private const int Kept = 14;

        internal static void Begin(Pawn p)
        {
            Who = p;
            lines.Clear();
        }

        internal static void End() => Who = null;

        /// <summary>What was written down, oldest first, for a failure's text.</summary>
        internal static string Text() => lines.Count == 0 ? "no job of theirs ended" : string.Join(" | ", lines);

        /// <summary>The mod and vanilla methods on the stack, nearest first (every frame but the system's and the trace's own, a lambda named by its outer type): who asked.</summary>
        private static string Stack() => string.Join(" < ", new StackTrace().GetFrames().Select(f => f.GetMethod()).Where(m => m != null)
            .Select(m => (m.DeclaringType is { } t && t.Name.StartsWith("<") ? t.DeclaringType : m.DeclaringType, m.Name))
            .Where(x => x.Item1 == null || !(x.Item1.Namespace ?? "").StartsWith("System") && !x.Item1.Name.StartsWith("JobTrace") && !x.Item1.Name.StartsWith("Patch_Pawn_JobTracker_CleanupCurrentJob_JobTrace")
                        && !x.Item1.Name.StartsWith("Patch_PrisonMap_QueueRegimeInterrupt_JobTrace"))
            .Select(x => (x.Item1?.Name ?? "?") + "." + x.Name).Take(9));

        private static void Add(string line)
        {
            if (lines.Count >= Kept) lines.RemoveAt(0);
            lines.Add(line);
        }

        /// <summary>A job of the traced pawn is ending (called from the job tracker, before the job is cleaned up).</summary>
        internal static void Ended(Pawn_JobTracker tracker, JobCondition condition)
        {
            Job job = tracker.curJob;
            int now = Find.TickManager.TicksGame;
            Add($"#{job.loadID} {job.def.defName} began {job.startTick}, {condition} at {now} ({now - job.startTick} ticks) by [{Stack()}]");
        }

        /// <summary>
        /// The traced pawn was queued for a regime interrupt (called as it is asked for, before it is judged): the line says when and by whom - the end of the job it was queued for comes a tick later, from the queue, and names
        /// only the queue. The trace is of what asked.
        /// </summary>
        internal static void Queued(Pawn p)
        {
            Add($"queued for a regime interrupt at {Find.TickManager.TicksGame} (doing {p.CurJobDef?.defName ?? "nothing"}) by [{Stack()}]");
        }
    }

    /// <summary>Writes down the regime interrupts asked for the pawn a check traces (see <see cref="JobTrace"/>).</summary>
    [HarmonyPatch(typeof(PrisonMap), nameof(PrisonMap.QueueRegimeInterrupt))]
    [TestOnly]
    public static class Patch_PrisonMap_QueueRegimeInterrupt_JobTrace
    {
        public static void Prefix(Pawn p)
        {
            if (JobTrace.Who != null && p == JobTrace.Who) JobTrace.Queued(p);
        }
    }

    /// <summary>Writes down the end of every job of the pawn a check traces (see <see cref="JobTrace"/>); costs a field read for every other pawn.</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    [TestOnly]
    public static class Patch_Pawn_JobTracker_CleanupCurrentJob_JobTrace
    {
        public static void Prefix(Pawn_JobTracker __instance, Pawn ___pawn, JobCondition condition)
        {
            if (JobTrace.Who != null && ___pawn == JobTrace.Who && __instance.curJob != null) JobTrace.Ended(__instance, condition);
        }
    }

    /// <summary>
    /// Trusties, round 5 (see <see cref="Trusties"/>): the gate in a stranded pawn's own way (two gates, one of them locked), and the walk of one with no bed (the guard's way chooses where to, never the inmate's own; the evacuation's
    /// walk back is not such a walk). Each says what it would see without the change it guards.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckTrustiesRound5(Map map)
        {
            CheckTrustyJobTrace(map);
            CheckTrustyTwoGates(map);
            CheckTrustyBedlessWalk(map);
        }

        // ---- the job trace -----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The trace that the cut's check fails with: a job ended for the traced pawn is written down with its number, how it ended and the method that ended it, and the job of another pawn is not. (A trace that wrote nothing
        /// would send the next run away with no cause, and so would one that wrote every pawn's.)
        /// </summary>
        private static void CheckTrustyJobTrace(Map map)
        {
            Guarded("trusties: the job trace says what ended a job", map, r =>
            {
                const string check = "job trace";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                Pawn p = s.Inmate("Ztjt1", RPTrustyDefOf.RP_Minimum, s.InZone);
                Pawn other = s.Inmate("Ztjt2", RPTrustyDefOf.RP_Minimum, s.At(14, 3));
                // (The waits first: what they were doing when they were put to wait is not what the trace is asked about.)
                p.jobs.StartJob(new Job(JobDefOf.Wait) { expiryInterval = 600 }, JobCondition.InterruptForced);
                other.jobs.StartJob(new Job(JobDefOf.Wait) { expiryInterval = 600 }, JobCondition.InterruptForced);
                if (p.CurJobDef != JobDefOf.Wait || other.CurJobDef != JobDefOf.Wait) { s.Fail($"the check's own fixture: the waits did not start (doing {p.CurJobDef?.defName ?? "nothing"} and {other.CurJobDef?.defName ?? "nothing"})"); TrustyStill(p, other); return; }
                JobTrace.Begin(p);
                try
                {
                    other.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    if (JobTrace.Text() != "no job of theirs ended") s.Fail($"the job of a pawn the trace was not asked about was written down: {JobTrace.Text()}");
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    string text = JobTrace.Text();
                    if (!text.Contains("Wait") || !text.Contains(nameof(JobCondition.InterruptForced)) || !text.Contains("began") || !text.Contains("EndCurrentJob") || !text.Contains(nameof(CheckTrustyJobTrace)))
                        s.Fail($"the trace of a job ended for the traced pawn does not say which job, how and by what: {text}");
                }
                finally
                {
                    JobTrace.End();
                }
                TrustyStill(p, other);
            });
        }

        // ---- two gates ---------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Two gates onto the same ground, one Locked: a holder outside who cannot get back is locked out only when no gate of theirs can be opened for them. With the second Staff only, a guard's escort takes it: they are stranded (walked
        /// in, by real ticks), not locked out; with both Locked they are locked out, told to the warden only when the work is not what keeps them outside. (Without the change the first locked gate anywhere on the map put them on the
        /// list nobody serves, with the escort through the other gate never offered.)
        /// </summary>
        private static void CheckTrustyTwoGates(Map map)
        {
            Guarded("trusties: two gates, one of them locked", map, r =>
            {
                const string check = "two gates";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                int told0 = Trusties.lockedOutTold;
                r.Add(() => Trusties.lockedOutTold = told0);
                Building_Door second = s.SecondGate(13);
                CompPrisonDoor firstComp = s.gate.GetComp<CompPrisonDoor>(), secondComp = second.GetComp<CompPrisonDoor>();
                Pawn guard = s.Guard("Zttg1", s.At(11, 2));
                Pawn t = s.Trusty("Zttg2", s.At(11, 3), claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                if (!s.pm.party.isOut || !c.outsidePass || !firstComp.trustyGate || !secondComp.trustyGate) { s.Fail($"the check's own premise: the party is out, the holder has the pass and both doors are gates of the zone (gates {firstComp.trustyGate}/{secondComp.trustyGate}): {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                // The first locked, the second Staff only: shut to the holder, open to a guard's escort.
                firstComp.mode = PrisonDoorMode.Locked;
                secondComp.mode = PrisonDoorMode.StaffOnly;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (t.CanReach(s.InCell, PathEndMode.Touch, Danger.Deadly)) { s.Fail("the check's own premise: with one gate locked and the other staff only the holder can still reach their bed"); TrustyStill(t, guard); return; }
                if (s.pm.party.lockedOut.Contains(t.thingIDNumber)) s.Fail("a holder outside a locked gate and a staff-only one is locked out: a guard's escort takes the staff-only gate");
                else if (!Trusties.Stranded(t)) s.Fail($"a holder outside a locked gate and a staff-only one is not on the escort's list: {TrustyLine(s, t)}");
                else
                {
                    if (Trusties.lockedOutTold != told0) s.Fail("the warden was told about a holder at work outside a shut gate with the party out");
                    TrustyEscortHome(s, guard, t, () => t.GetRoom() == InmateUtility.CellOf(t), "a holder outside a locked gate and a staff-only one");
                }
                TrustyStill(t, guard);
                // Both locked: nobody can take either, and a holder who is not at the outside work is told to the warden (the party out, the job another: they are outside for no work).
                firstComp.mode = PrisonDoorMode.Locked;
                secondComp.mode = PrisonDoorMode.Locked;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Pawn u = s.Trusty("Zttg3", s.At(12, 3), claim: s.bed2);
                CompInmate uc = u.Inmate();
                uc.outsidePass = true;
                uc.workPass = RPTrustyDefOf.RP_OutsideWork;
                int told1 = Trusties.lockedOutTold;
                s.Place(guard, s.At(11, 2));
                s.pm.party.holdUntilTick = -1;
                s.Beat();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out with the guard beside the strip again: {TrustyLine(s, u)}"); TrustyStill(t, u, guard); return; }
                if (!s.pm.party.lockedOut.Contains(u.thingIDNumber) || Trusties.Stranded(u)) s.Fail($"a holder outside two locked gates is not locked out: {TrustyLine(s, u)}");
                if (Trusties.lockedOutTold != told1) s.Fail("the warden was told about a holder at work outside two locked gates with the party out");
                uc.job = DefDatabase<InmateJobDef>.GetNamed("RP_JobCleaning");
                s.Beat();
                if (Trusties.lockedOutTold - told1 != 1) s.Fail($"the warden was told {Trusties.lockedOutTold - told1} times about a holder outside two locked gates with another job (1 expected)");
                firstComp.mode = PrisonDoorMode.Auto;
                secondComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                TrustyStill(t, u, guard);
            });
        }

        // ---- the walk of one with no bed -----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A holder with no bed, outside with the strip erased: they cannot reach any cell of their wing alone, and a guard can - the walk's target is a cell of the wing outside the cells (no other inmate's door) chosen by the guard's reach;
        /// the evacuation's walk back (no wing for one with no bed) has none. (Without the change the target was chosen by the inmate's own reach, which a stranded holder has not: no guard was ever offered the walk.)
        /// </summary>
        private static void CheckTrustyBedlessWalk(Map map)
        {
            Guarded("trusties: the walk of a holder with no bed", map, r =>
            {
                const string check = "bedless walk";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztbw1", s.At(10, 2));
                Pawn u = s.Trusty("Ztbw2", s.At(12, 3));
                CompInmate uc = u.Inmate();
                s.Beat();
                uc.UpdateWorkPass();
                if (!uc.outsidePass || u.ownership?.OwnedBed != null) { s.Fail($"the check's own premise: the holder has the pass and no bed: {TrustyLine(s, u)}"); TrustyStill(u, guard); return; }
                foreach (IntVec3 cell in new CellRect(s.origin.x, s.origin.z + 1, TrustyScene.Width, 4)) s.PaintCell(cell, null);
                s.pm.Notify_DoorsChanged();
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (Trusties.WingCell(u).IsValid) { s.Fail("the check's own premise: with the strip erased the holder can still reach a cell of their wing alone"); TrustyStill(u, guard); return; }
                if (!Trusties.Stranded(u)) { s.Fail($"the beat did not find a stranded holder who has no bed: {TrustyLine(s, u)}"); TrustyStill(u, guard); return; }
                // The walk's target: not by the holder's own reach.
                IntVec3 target = EscortWalk.HomeCell(u, guard, toWing: true);
                if (!target.IsValid) s.Fail("a guard has no cell of the wing to walk a stranded holder with no bed to (the target was chosen by the holder's own reach)");
                else
                {
                    if (s.pm.IsOutsideSpace(target)) s.Fail($"the wing cell a stranded holder with no bed is walked to is outside the prison ({target - s.origin})");
                    if (target.GetRoom(map) is { IsPrisonCell: true }) s.Fail($"the wing cell a stranded holder with no bed is walked to is inside a cell ({target - s.origin}): the walk opens no other inmate's cell door");
                    if (!guard.CanReach(target, PathEndMode.OnCell, Danger.Deadly)) s.Fail($"the wing cell chosen for the walk ({target - s.origin}) is one the guard cannot reach");
                }
                // The evacuation's walk back asks the cell without it: no bed, no cell (the all-clear sets such a one down as they were).
                if (EscortWalk.HomeCell(u).IsValid) s.Fail("the home cell for the evacuation's walk back is a wing cell for a holder with no bed");
                TrustyEscortHome(s, guard, u, () => !s.pm.IsOutsideSpace(u.Position), "a stranded holder with no bed");
                TrustyStill(u, guard);
            });
        }
    }
}
