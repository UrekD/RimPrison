using System.Linq;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Test diagnostics: vanilla's "Can't transfer item ... because it's not here" (an item a job points at has left the pawn's pockets) says nothing of whose pockets or what
    /// the pawn was doing. In an autotest run this names the pawn, the job and what was queued behind it, where the item is now.
    /// </summary>
    [HarmonyPatch(typeof(ThingOwner), nameof(ThingOwner.TryTransferToContainer), new[] { typeof(Thing), typeof(ThingOwner), typeof(int), typeof(Thing), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    [TestOnly]
    public static class Patch_ThingOwner_TransferNotHere
    {
        public static void Prefix(ThingOwner __instance, Thing item)
        {
            if (!AutoTest.Active || item == null || item.holdingOwner == __instance || __instance.Owner is not Pawn_InventoryTracker tracker) return;
            Pawn pawn = tracker.pawn;
            Job job = pawn.CurJob;
            RPLog.Message($"[RimPrison:test] NOT HERE: {pawn.LabelShort} (inmate {pawn.Inmate() != null}) does {job?.def.defName} on {job?.targetA.Thing?.ToStringSafe()} count {job?.count}, queued [{string.Join(", ", pawn.jobs.jobQueue.Select(q => q.job.def.defName + " " + q.job.targetA.Thing?.ToStringSafe()))}]; "
                          + $"the item {item.ToStringSafe()} is {(item.Destroyed ? "destroyed" : item.Spawned ? "on the ground" : item.holdingOwner != null ? "held by " + item.holdingOwner.Owner.ToStringSafe() : "nowhere")}; "
                          + $"the driver's toil {pawn.jobs.curDriver?.CurToilIndex} {pawn.jobs.curDriver?.CurToilString}, started {Find.TickManager.TicksGame - (job?.startTick ?? 0)} ticks ago");
        }
    }
}

namespace RimPrison
{
    /// <summary>
    /// Test diagnostics: a cook "started 10 jobs in one tick" on DoBill at a stove (the work scan offered the bill, and the job refused to run at once, ten times over). Vanilla's error comes
    /// after the tenth job has ended, so it cannot say why; this runs when a DoBill ends as a failure in the very tick it began and says which check refused it: the end condition
    /// and the toil it was in, the ingredient it was after (where it lies, its stack against what the job wants, forbidden, who holds a physical-interaction reservation on it, every reserver of
    /// it, prisoners included) and a short stack trace. One occurrence names the failing check.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    [TestOnly]
    public static class Patch_JobTracker_EndCurrentJob_DoBillSameTick
    {
        private static int logged;
        private const int MaxLogged = 60;

        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition, Pawn ___pawn)
        {
            Job job = __instance.curJob;
            if (!AutoTest.Active || job == null || job.def != RimWorld.JobDefOf.DoBill || job.startTick != Find.TickManager.TicksGame || logged >= MaxLogged) return;
            if (condition is not (JobCondition.Incompletable or JobCondition.Errored or JobCondition.ErroredPather or JobCondition.QueuedNoLongerValid)) return;
            logged++;
            RPLog.Message(Describe(___pawn, job, condition, __instance.curDriver));
        }

        /// <summary>The line the diagnostic logs (also read by the check that provokes it).</summary>
        public static string Describe(Pawn pawn, Job job, JobCondition condition, JobDriver driver)
        {
            Map map = pawn.Map;
            Thing stove = job.targetA.Thing;
            Thing food = job.targetB.Thing;
            if (food == null && job.targetQueueB is { Count: > 0 }) food = job.targetQueueB[0].Thing;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[RimPrison:test] DOBILL SAME TICK: {pawn.LabelShort} (inmate {pawn.Inmate() != null}) {condition} on {stove.ToStringSafe()} at {stove?.Position}, toil {driver?.CurToilIndex} {driver?.CurToilString}, ");
            RimWorld.Bill bill = job.bill;
            sb.Append($"bill {bill?.LabelCap} deleted {bill?.DeletedOrDereferenced}, usable now {(stove as RimWorld.IBillGiver)?.CurrentlyUsableForBills()}, burning {RimWorld.FireUtility.IsBurning(stove)}, ");
            sb.Append($"ingredient {food.ToStringSafe()} ({food?.Position}, the pawn at {pawn.Position}, queued {job.targetQueueB?.Count ?? 0}): stack {food?.stackCount} against the job's {job.count}, spawned {food?.Spawned}, forbidden {food != null && RimWorld.ForbidUtility.IsForbidden(food, pawn)}, ");
            if (food != null && map != null)
            {
                Pawn physical = map.physicalInteractionReservationManager.FirstReserverOf(food);
                sb.Append($"physical interaction by {physical?.LabelShort ?? "nobody"} (prisoner {physical?.IsPrisonerOfColony}, job {physical?.CurJobDef?.defName}), reservers [");
                sb.Append(string.Join("; ", map.reservationManager.ReservationsReadOnly.Where(r => r.Target == food).Select(r => $"{r.Claimant.LabelShort} prisoner {r.Claimant.IsPrisonerOfColony} job {r.Job?.def.defName} stack {r.StackCount} layer {r.Layer?.defName ?? "default"}")));
                sb.Append($"], the scan would offer it again {food.Spawned && pawn.CanReserve(food)}, ");
            }
            sb.Append("trace: ").Append(string.Join(" < ", new System.Diagnostics.StackTrace(2, false).GetFrames().Take(24).Select(f => f.GetMethod()?.Name)));
            return sb.ToString();
        }
    }
}
