using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Shuttle checks for the state's own passengers and for the way out of a transfer: the marshals' prisoners (returning ex-inmates, recaptured escapees) come on the next daily shuttle, first on its
    /// roster, with an arrival line of their own and no grant; a transferred inmate is walked to the ramp by a guard (a walk at the guard's pace, never a follow job) and walks on alone only when no guard
    /// comes. They run on the clear stage of <see cref="ShuttleStage"/>, like the other shuttle checks, and put back what they touch.
    /// </summary>
    public static partial class AutoTest
    {
        private static void RunInsideShuttleChecks(Map map)
        {
            CheckInsideTransferIsWalkedToTheShuttle(map);
            CheckInsideTransferWalksOnAloneWhenNoGuardComes(map);
            CheckInsideTransferStopsWhenTheEscortEnds(map);
            CheckInsideMarshalsPrisonersWaitForTheShuttle(map);
            CheckInsideMarshalsPrisonersStepOffTheShuttle(map);
        }

        private static List<Message> LiveMessages() =>
            (List<Message>)typeof(Messages).GetField("liveMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) ?? new List<Message>();

        // ---- escorted transfers --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard for the walk: one on site whose own shift covers the next three hours (the hourly shift pass rewrites timetables, and a guard whose shift ended walks home in the middle of the walk), else a new one.
        /// </summary>
        private static Pawn EscortGuard(Map map, Rollback r)
        {
            int hour0 = GenLocalDate.HourOfDay(map);
            bool OnDutyAhead(Pawn x) => x.timetable == null || Enumerable.Range(0, 4).All(k => x.timetable.GetAssignment((hour0 + k) % 24) != TimeAssignmentDefOf.Sleep);
            // (One who walks at the pace of a walk: a real guard with a bad leg lags the inmate they walk by a third of the way, and the check's gap of twelve cells.)
            Pawn guard = map.mapPawns.FreeColonistsSpawned.Where(x => GuardUtility.IsGuard(x) && !x.Downed && !x.InMentalState && OnDutyAhead(x) && x.CurJobDef != RPCommuteDefOf.RP_GoHome
                                                                      && x.health.capacities.GetLevel(PawnCapacityDefOf.Moving) >= 0.99f).FirstOrDefault();
            if (guard == null)
            {
                guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.health.Reset();
            }
            if (guard.needs?.rest != null) guard.needs.rest.CurLevel = 1f;
            return guard;
        }

        /// <summary>
        /// A transferred inmate waits for a guard when the shuttle is down: the outbound think node does not send them aboard by themselves, the escort work giver offers the walk to a guard (and not for
        /// a released inmate, who walks on their own), and the guard walks them to the ramp: the guard's job is a walk and the inmate's is a walk to the spot the guard is making for, never a follow job, and the
        /// two stay together to the ramp. The inmate boards (counted once, as a transfer) and the guard's job is over; a walk cut short leaves the inmate waiting for the next guard.
        /// </summary>
        private static void CheckInsideTransferIsWalkedToTheShuttle(Map map)
        {
            Guarded("inside: a transfer is walked to the shuttle by a guard", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "escorted transfer")) return;
                if (!ShuttleSystem.HasPad(map))
                {
                    problems0.Add("escorted transfer: no pad");
                    return;
                }
                Pawn sent = Throwaway(map, r), released = Throwaway(map, r);
                // (A random inmate can arrive lame or old, at a third of a guard's pace, and a guard who walks on ahead of them is more than the check's twelve cells away, and the walk takes more than the check waits for: the walk is of an inmate who is fit to walk.)
                sent.health.Reset();
                Pawn guard = EscortGuard(map, r);
                // (The rest of the prison is shut in, as the programs and court checks do: a brawl among the real inmates in the half hour or more the walk takes sends the guard off to subdue them, and the transfer walks on alone.)
                QuietTheRealInmates(map, r, new[] { sent, released });
                foreach (Pawn p in new[] { sent, released })
                {
                    p.Inmate().processed = true;
                    r.Add(() =>
                    {
                        g.shuttle.Forget(p);
                        Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                    });
                }
                int released0 = g.stats.released;
                g.shuttle.Register(sent, OutboundKind.Transferred);
                // (Vanilla's own release order: outbound, boards by itself, and is not counted.)
                released.guest.Released = true;
                g.shuttle.Register(released, OutboundKind.Released, counted: false);
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed || v.shuttle is not { Spawned: true } shuttle)
                {
                    problems0.Add($"escorted transfer: the shuttle did not land ({v?.Describe()})");
                    return;
                }
                OutboundEntry entry = g.shuttle.OutboundOf(sent);
                if (entry == null || !sent.CanReach(shuttle.InteractionCell, PathEndMode.OnCell, Danger.Deadly) || !guard.CanReach(shuttle.InteractionCell, PathEndMode.Touch, Danger.Deadly))
                {
                    RPLog.Message("[RimPrison:test] inside: escorted transfer check skipped (the ramp cannot be reached from where they stand)");
                    return;
                }
                // The think node waits for a guard: it does not send a transfer aboard by themselves (a released inmate it does).
                Job waiting = new JobGiver_Outbound().TryIssueJobPackage(sent, default).Job, boarding = new JobGiver_Outbound().TryIssueJobPackage(released, default).Job;
                if (waiting == null || waiting.def == RPShuttleDefOf.RP_BoardShuttle) InsideFail($"a transfer was sent aboard by themselves while a guard could still walk them ({waiting?.def?.defName})");
                if (boarding?.def != RPShuttleDefOf.RP_BoardShuttle) InsideFail($"a released inmate was not sent aboard by the outbound think node ({boarding?.def?.defName})");
                // The work giver: a guard is offered the transfer and not the released inmate, and the walk is to the ramp at a walk.
                var giver = new WorkGiver_EscortTransfer { def = DefDatabase<WorkGiverDef>.GetNamed("RP_EscortTransfer_WorkGiver") };
                if (!giver.HasJobOnThing(guard, sent) || giver.HasJobOnThing(guard, released)) InsideFail($"the escort work giver offers the transfer {giver.HasJobOnThing(guard, sent)} (true expected) and a released inmate {giver.HasJobOnThing(guard, released)} (false expected)");
                Job job = giver.JobOnThing(guard, sent);
                if (job?.def != RPShuttleDefOf.RP_EscortTransfer || job.targetA.Thing != sent || job.targetB.Cell != shuttle.InteractionCell || job.locomotionUrgency != LocomotionUrgency.Walk)
                    InsideFail($"the escort job is {job?.def?.defName} for {job?.targetA.Thing?.LabelShort} to {job?.targetB.Cell} at {job?.locomotionUrgency}");
                if (job == null) return;
                // The walk: the guard (standing beside them: a guard who starts at the far end of the prison takes longer than the half hour after which they walk on alone) takes the job and the two go to the ramp together.
                // (In the same room: a free cell three steps away may be on the other side of a wall or a locked door, and the guard set down there walks the long way round, or not at all, while the half hour runs out and the transfer walks on alone.)
                if (CellFinder.TryFindRandomCellNear(sent.Position, map, 3, c => c.Standable(map) && c.GetFirstPawn(map) == null && c != sent.Position && c.GetRoom(map) == sent.GetRoom() && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly), out IntVec3 beside))
                {
                    // (Stopped, not ended: ending a job makes the guard think again at once, and beside the transfer the work giver offers them the escort in the same call - the walk started and counted before the
                    // check's own start, which then ended it and walked to a transfer already on its way to the ramp alone: "escorts started 0" in count-k3b.)
                    guard.jobs.StopAll();
                    guard.pather.StopDead();
                    guard.Position = beside;
                    guard.Notify_Teleported(true, true);
                }
                int escorts0 = g.shuttle.escortsStarted, boarded0 = v.boarded;
                string startNote = $"start: the transfer at {sent.Position}, the guard at {guard.Position} (set down beside them {beside != default}, same room {guard.GetRoom() == sent.GetRoom()}, reach {guard.CanReach(sent, PathEndMode.Touch, Danger.Deadly)})";
                guard.jobs.StartJob(job, JobCondition.InterruptForced);
                bool guardHadJob = false, followed = false, walked = false, together = true;
                float widest = 0f;
                string widestAt = "";
                int tickStart = ShNow;
                int ticks = ShuttleTicks(() =>
                {
                    if (!sent.Spawned) return true;
                    if (guard.CurJobDef == RPShuttleDefOf.RP_EscortTransfer) guardHadJob = true;
                    if (sent.CurJobDef == JobDefOf.Follow || sent.CurJobDef == JobDefOf.FollowClose) followed = true;
                    if (entry.escortId == guard.thingIDNumber && sent.CurJobDef == JobDefOf.Goto && sent.CurJob.locomotionUrgency == LocomotionUrgency.Walk)
                    {
                        walked = true;
                        float apart = guard.Position.DistanceTo(sent.Position);
                        if (apart > widest)
                        {
                            widest = apart;
                            widestAt = $"{ShNow - tickStart} ticks in, the guard at {guard.Position} {(guard.pather.Moving ? "moving" : "standing")} (speed {guard.GetStatValue(StatDefOf.MoveSpeed):F2}), the inmate at {sent.Position} {(sent.pather.Moving ? "moving" : "standing")} (speed {sent.GetStatValue(StatDefOf.MoveSpeed):F2}, {(sent.Downed ? "downed" : "up")}), ramp {shuttle.InteractionCell}; moving capacity {sent.health.capacities.GetLevel(PawnCapacityDefOf.Moving):F2}, age {sent.ageTracker.AgeBiologicalYears}, hediffs [{string.Join(", ", sent.health.hediffSet.hediffs.Select(h => h.def.defName))}]";
                        }
                    }
                    return false;
                }, 3000);
                together = widest <= 12f;
                RPLog.Message($"[RimPrison:test] inside: the transfer boarded {!sent.Spawned} after {ticks} ticks, the guard had the escort job {guardHadJob}, the inmate walked beside the guard {walked} (never more than {widest:F1} cells apart), a follow job {followed}, escorts started {g.shuttle.escortsStarted - escorts0}, boarded on the visit {v.boarded - boarded0}");
                if (sent.Spawned) InsideFail($"the transfer was not at the ramp and aboard after {ticks} ticks (at {sent.Position}, ramp {shuttle.InteractionCell}, job {sent.CurJob}, the guard's {guard.CurJob} at {guard.Position}; {startNote})");
                if (!guardHadJob || g.shuttle.escortsStarted != escorts0 + 1) InsideFail($"a guard did not walk the transfer (the guard had the job {guardHadJob}, escorts started {g.shuttle.escortsStarted - escorts0}, escorted by {entry.escortId} and the guard is {guard.thingIDNumber}; {startNote})");
                if (!walked) InsideFail($"the transfer was not seen walking to the guard's spot at a walk while the guard had them (escorted by {entry.escortId} at the end, the guard is {guard.thingIDNumber}, the transfer's last job {sent.CurJobDef?.defName ?? "none"})");
                if (followed) InsideFail("the transfer was given a follow job (it is done the moment they stand beside the guard, and was handed out again in the same tick)");
                if (!together) InsideFail($"the transfer and the guard were {widest:F1} cells apart on the walk (the inmate keeps near the guard): {widestAt}");
                // (The inmate vanilla's order released boards by themselves on the same shuttle: that is a second boarding, and not a release the prison counts.)
                int expectedBoarded = released.Spawned ? 1 : 2;
                if (v.boarded != boarded0 + expectedBoarded || g.shuttle.OutboundOf(sent) != null || g.stats.released != released0) InsideFail($"the transfer's boarding: boarded {v.boarded - boarded0} ({expectedBoarded}), still outbound {g.shuttle.OutboundOf(sent) != null}, released counted {g.stats.released - released0} (0: a transfer is not a release)");
                // The guard is done a moment after (the job ends when the inmate is aboard).
                ShuttleTicks(() => guard.CurJobDef != RPShuttleDefOf.RP_EscortTransfer, 900);
                if (guard.CurJobDef == RPShuttleDefOf.RP_EscortTransfer) InsideFail("the guard is still on the escort job after the transfer boarded");
                // A walk cut short: the next transfer, a guard taken off the job, waits for the next guard.
                Pawn cut = Throwaway(map, r);
                cut.Inmate().processed = true;
                r.Add(() => g.shuttle.Forget(cut));
                g.shuttle.Register(cut, OutboundKind.Transferred);
                OutboundEntry cutEntry = g.shuttle.OutboundOf(cut);
                if (g.shuttle.visit == v && v.phase == ShuttlePhase.Landed && giver.HasJobOnThing(guard, cut) && giver.JobOnThing(guard, cut) is { } second)
                {
                    if (CellFinder.TryFindRandomCellNear(cut.Position, map, 3, c => c.Standable(map) && c.GetFirstPawn(map) == null && c != cut.Position && c.GetRoom(map) == cut.GetRoom() && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly), out IntVec3 next))
                    {
                        guard.jobs.StopAll();
                        guard.pather.StopDead();
                        guard.Position = next;
                        guard.Notify_Teleported(true, true);
                    }
                    guard.jobs.StartJob(second, JobCondition.InterruptForced);
                    ShuttleTicks(() => cutEntry.escortId >= 0 || !cut.Spawned, 3500);
                    // (Whichever guard has them: a real guard may have got there first.)
                    Pawn escorter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == cutEntry.escortId);
                    bool had = escorter != null && escorter.CurJobDef == RPShuttleDefOf.RP_EscortTransfer;
                    // (Stopped, not ended: ending a job makes the guard think again at once, and the work giver offers them the same transfer.)
                    escorter?.jobs.StopAll();
                    if (!had || cutEntry.escortId >= 0 || !cut.Spawned) InsideFail($"a walk cut short: a guard had them {had}, still escorted {cutEntry.escortId}, boarded alone {!cut.Spawned}");
                }
                else RPLog.Message("[RimPrison:test] inside: the cut-short escort was not checked (the shuttle is gone or no guard could take them)");
                EndShuttleVisit(g, map);
            });
        }

        /// <summary>
        /// With no guard to come, a transfer goes to the ramp on their own after half an hour (as the arrivals walk to holding on their own): not before, not while a guard has them, and a guard who is not
        /// there any more (a stale id) is forgotten, so the inmate does not wait for them for ever.
        /// </summary>
        private static void CheckInsideTransferWalksOnAloneWhenNoGuardComes(Map map)
        {
            Guarded("inside: a transfer walks on alone when no guard comes", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "transfer with no guard")) return;
                Pawn sent = Throwaway(map, r);
                sent.Inmate().processed = true;
                r.Add(() => g.shuttle.Forget(sent));
                g.shuttle.Register(sent, OutboundKind.Transferred);
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed || v.shuttle is not { Spawned: true } shuttle)
                {
                    problems0.Add($"transfer with no guard: the shuttle did not land ({v?.Describe()})");
                    return;
                }
                OutboundEntry entry = g.shuttle.OutboundOf(sent);
                // Just landed: they wait.
                (entry.sinceTick, v.landedTick) = (ShNow - 5000, ShNow);
                if (ShuttleSystem.WalksOnAlone(entry, v) || ShuttleSystem.TransferWalk(sent, entry, shuttle) == null) InsideFail("a transfer was sent to the ramp alone the moment the shuttle landed");
                // Half an hour with no guard: they go, even if they have waited for hours before the shuttle came (the wait is for the shuttle's stay, not their own).
                v.landedTick = ShNow - ShuttleSystem.EscortWaitTicks - 10;
                if (!ShuttleSystem.WalksOnAlone(entry, v) || ShuttleSystem.TransferWalk(sent, entry, shuttle) != null) InsideFail("a transfer no guard came for in half an hour still waits for one");
                // With a guard who has them they wait for that guard, however long (the guard's own job decides when they are let go): a guard's id with no job behind it is forgotten at once.
                entry.escortId = 987654;
                if (ShuttleSystem.WalksOnAlone(entry, v)) InsideFail("a transfer with a guard walking them was sent alone");
                Job stale = ShuttleSystem.TransferWalk(sent, entry, shuttle);
                if (entry.escortId >= 0 || stale == null) InsideFail($"a guard who is not walking them was not forgotten (escort {entry.escortId}, job {stale?.def?.defName})");
                if (ShuttleSystem.TransferWalk(sent, entry, shuttle) != null) InsideFail("once the missing guard was forgotten the transfer still waited");
                // A released inmate never waits (the walk is a transfer's).
                Pawn released = Throwaway(map, r);
                released.Inmate().processed = true;
                r.Add(() => g.shuttle.Forget(released));
                g.shuttle.Register(released, OutboundKind.Released, counted: false);
                (g.shuttle.OutboundOf(released).sinceTick, v.landedTick) = (ShNow, ShNow);
                if (ShuttleSystem.TransferWalk(released, g.shuttle.OutboundOf(released), shuttle) != null) InsideFail("a released inmate was made to wait for a guard");
                EndShuttleVisit(g, map);
            });
        }

        /// <summary>
        /// A guard taken off the walk (an alarm, a shift's end, a draft) leaves the transfer where they stand, waiting for the next guard: not walking on to the ramp alone (the walk is a job of the inmate's own,
        /// which nothing asked again until it was done), and a new escort takes them from there.
        /// </summary>
        private static void CheckInsideTransferStopsWhenTheEscortEnds(Map map)
        {
            Guarded("inside: a transfer whose escort ended mid-walk waits for the next", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "escort ended")) return;
                if (!ShuttleSystem.HasPad(map)) { problems0.Add("escort ended: no pad"); return; }
                Pawn sent = Throwaway(map, r);
                sent.health.Reset();
                Pawn guard = EscortGuard(map, r);
                QuietTheRealInmates(map, r, new[] { sent });
                sent.Inmate().processed = true;
                r.Add(() => g.shuttle.Forget(sent));
                g.shuttle.Register(sent, OutboundKind.Transferred);
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed || v.shuttle is not { Spawned: true } shuttle)
                {
                    problems0.Add($"escort ended: the shuttle did not land ({v?.Describe()})");
                    return;
                }
                OutboundEntry entry = g.shuttle.OutboundOf(sent);
                var giver = new WorkGiver_EscortTransfer { def = DefDatabase<WorkGiverDef>.GetNamed("RP_EscortTransfer_WorkGiver") };
                if (entry == null || !sent.CanReach(shuttle.InteractionCell, PathEndMode.OnCell, Danger.Deadly) || !guard.CanReach(shuttle.InteractionCell, PathEndMode.Touch, Danger.Deadly)
                    || !giver.HasJobOnThing(guard, sent) || giver.JobOnThing(guard, sent) is not { } job)
                {
                    RPLog.Message("[RimPrison:test] inside: the escort that ended mid-walk was not checked (the ramp cannot be reached, or no guard could take them)");
                    return;
                }

                // A guard takes them (set down beside them, as the walk check does) and the two set out.
                if (CellFinder.TryFindRandomCellNear(sent.Position, map, 3, c => c.Standable(map) && c.GetFirstPawn(map) == null && c != sent.Position && c.GetRoom(map) == sent.GetRoom() && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly), out IntVec3 beside))
                {
                    guard.jobs.StopAll();
                    guard.pather.StopDead();
                    guard.Position = beside;
                    guard.Notify_Teleported(true, true);
                }
                guard.jobs.StartJob(job, JobCondition.InterruptForced);
                ShuttleTicks(() => entry.escortId == guard.thingIDNumber && sent.CurJobDef == JobDefOf.Goto && sent.pather.Moving || !sent.Spawned, 600);
                if (!sent.Spawned || entry.escortId != guard.thingIDNumber || sent.CurJobDef != JobDefOf.Goto || !sent.pather.Moving)
                {
                    RPLog.Message($"[RimPrison:test] inside: the escort that ended mid-walk was not checked (the walk did not set out: escort {entry.escortId}, the guard is {guard.thingIDNumber}, the inmate on {sent.CurJobDef?.defName})");
                    guard.jobs.StopAll();
                    return;
                }
                ShuttleTicks(() => !sent.Spawned, 40);
                if (!sent.Spawned) { RPLog.Message("[RimPrison:test] inside: the escort that ended mid-walk was not checked (the ramp was too near: they boarded)"); return; }

                // The guard is taken off the walk (stopped, as an alarm or a draft would: nothing thinks again for them). The inmate stops, and is not seen walking while nobody has them.
                IntVec3 cutAt = sent.Position;
                guard.jobs.StopAll();
                guard.pather.StopDead();
                bool walkedAlone = false;
                int ticks = ShuttleTicks(() =>
                {
                    if (!sent.Spawned) return true;
                    // (A real guard may have taken them since: then somebody has them, and the walk is theirs.)
                    if (entry.escortId < 0 && (sent.CurJobDef == JobDefOf.Goto || sent.CurJobDef == RPShuttleDefOf.RP_BoardShuttle)) walkedAlone = true;
                    return false;
                }, 300);
                string state = $"{ticks} ticks after the guard was taken off: escort {entry.escortId}, the inmate on {sent.CurJobDef?.defName ?? "no job"} at {(sent.Spawned ? sent.Position.ToString() : "-")} (cut at {cutAt}, ramp {shuttle.InteractionCell}), spawned {sent.Spawned}";
                if (!sent.Spawned || walkedAlone) InsideFail($"a transfer whose guard was taken off the walk went on to the ramp alone ({state})");
                else if (g.shuttle.OutboundOf(sent) == null) InsideFail($"a transfer whose guard was taken off the walk is no longer outbound ({state})");
                if (!sent.Spawned) return;

                // A new escort takes them from where they stand, and they set out again.
                if (entry.escortId < 0)
                {
                    if (!giver.HasJobOnThing(guard, sent) || giver.JobOnThing(guard, sent) is not { } again)
                    {
                        // (The transfer left waiting is on offer to every guard, and the work giver's scan is the game's own: a real guard who was offered it in the 300 ticks is on their way to the inmate (the escort is noted only when they meet) and
                        // holds the reservation, so this guard is rightly not offered it as well - stability11-s11r3a: escort -1, the inmate on Wait. That is the offer working, told and not failed.)
                        Pawn walking = map.reservationManager.FirstRespectedReserver(sent, guard) ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x != guard && x.CurJobDef == RPShuttleDefOf.RP_EscortTransfer && x.CurJob.targetA.Thing == sent);
                        if (walking != null)
                            RPLog.Message($"[RimPrison:test] inside: a transfer whose escort ended mid-walk was left waiting and taken by {walking.LabelShort} (on {walking.CurJobDef?.defName ?? "no job"}), not by the check's guard: the second escort is not judged ({state})");
                        else InsideFail($"no guard could take the transfer a walk cut short left waiting, and nobody else has them ({state}; the guard on {guard.CurJobDef?.defName ?? "no job"}, on duty ahead: {guard.timetable == null || guard.timetable.CurrentAssignment == TimeAssignmentDefOf.Work}, can reach them: {guard.CanReach(sent, PathEndMode.Touch, Danger.Deadly)})");
                        EndShuttleVisit(g, map);
                        return;
                    }
                    if (CellFinder.TryFindRandomCellNear(sent.Position, map, 3, c => c.Standable(map) && c.GetFirstPawn(map) == null && c != sent.Position && c.GetRoom(map) == sent.GetRoom() && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly), out IntVec3 next))
                    {
                        guard.pather.StopDead();
                        guard.Position = next;
                        guard.Notify_Teleported(true, true);
                    }
                    guard.jobs.StartJob(again, JobCondition.InterruptForced);
                    ShuttleTicks(() => entry.escortId >= 0 && sent.CurJobDef == JobDefOf.Goto || !sent.Spawned, 600);
                }
                if (sent.Spawned && (entry.escortId < 0 || sent.CurJobDef != JobDefOf.Goto)) InsideFail($"a new escort did not pick up the transfer left waiting (escort {entry.escortId}, the inmate on {sent.CurJobDef?.defName ?? "no job"}, the guard on {guard.CurJobDef?.defName ?? "no job"})");
                // (Stopped, not ended: ending a job makes the guard think again at once, and the work giver offers them the same transfer.)
                guard.jobs.StopAll();
                if (map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == entry.escortId) is { } other) other.jobs.StopAll();
                RPLog.Message($"[RimPrison:test] inside: a transfer whose escort ended mid-walk stood and waited ({state}), and a new escort took them");
                EndShuttleVisit(g, map);
            });
        }

        // ---- the marshals' prisoners ---------------------------------------------------------------------------------------------------------------

        /// <summary>Free beds for this level for the check: a spare bed in the cells of inmates (taken down again by the rollback) until there are <paramref name="want"/> (or no room for more). Returns how many there are.</summary>
        private static int InsideFreeBeds(Map map, Rollback r, SecurityLevelDef sec, int want)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (IntakeSystem.FreeBedCount(map, sec) >= want) break;
                if (p.Inmate() is not { } || p.ownership?.OwnedBed?.GetRoom() is not { } room) continue;
                InsideSpareBed(map, room, r);
            }
            return IntakeSystem.FreeBedCount(map, sec);
        }

        /// <summary>Two prisoners of the state's marshals: an escapee the police have caught and an ex-inmate rearrested, both off the map and kept, as they are in the game.</summary>
        private static (Pawn runner, Pawn leaver) MarshalsPair(Map map, Rollback r, PrisonGame g)
        {
            // (The real ones waiting, if any, are set aside: the check counts who is on the roster and who steps off.)
            List<MarshalEntry> marshals0 = g.shuttle.marshals.ToList();
            List<Fugitive> fugitives0 = g.fugitives.ToList();
            List<Rearrest> returning0 = g.returning.ToList();
            g.shuttle.marshals.Clear();
            g.fugitives.Clear();
            g.returning.Clear();
            r.Add(() =>
            {
                g.shuttle.marshals.Clear();
                g.shuttle.marshals.AddRange(marshals0);
                g.fugitives.Clear();
                g.fugitives.AddRange(fugitives0);
                g.returning.Clear();
                g.returning.AddRange(returning0);
            });
            Pawn runner = Throwaway(map, r, spawn: false), leaver = Throwaway(map, r, spawn: false);
            foreach (Pawn p in new[] { runner, leaver })
            {
                if (!Find.WorldPawns.Contains(p) && !p.Spawned) Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                // (Nothing about their health is the check's business: an injury or a sickness the generator gave one of them has killed them before the shuttle landed.)
                foreach (Hediff h in p.health.hediffSet.hediffs.Where(x => x is Hediff_Injury || x.Bleeding || x.def.lethalSeverity > 0f).ToList()) p.health.RemoveHediff(h);
                p.Inmate().sentenceDays = 10;
                p.Inmate().admittedTick = Math.Max(1, ShNow - 20 * GenDate.TicksPerDay);
                r.Add(() =>
                {
                    g.shuttle.marshals.RemoveAll(m => m.pawn == p);
                    g.shuttle.carriedOver.Remove(p);
                    g.fugitives.RemoveAll(f => f.pawn == p);
                    g.returning.RemoveAll(x => x.pawn == p);
                    g.shuttle.Forget(p);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                    if (p.Spawned) ShuttleRemovePawn(p);
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                });
            }
            Find.WorldPawns.ForcefullyKeptPawns.Add(runner);
            Find.WorldPawns.ForcefullyKeptPawns.Add(leaver);
            // (Ticks of the past that are ticks: at the start of a game three days ago is before the first, and a negative tick means none.)
            g.fugitives.Add(new Fugitive { pawn = runner, escapedTick = Math.Max(1, ShNow - 3 * GenDate.TicksPerDay), returnTick = ShNow });
            g.returning.Add(new Rearrest { name = leaver.LabelShort, pawn = leaver, dueTick = ShNow, reason = ParoleLife.NewOffence });
            return (runner, leaver);
        }

        /// <summary>
        /// A recaptured escapee and a returning ex-inmate who are due are not put on the map at the hour: they wait for the daily shuttle in the marshals' list (the escapee's case still waits for the recapture), come
        /// first on its roster (before the new inmates of any level), and a shuttle that leaves before they have stepped off keeps them there
        /// (they are not carried over as new inmates, who would come back through the intake and not through the return).
        /// </summary>
        private static void CheckInsideMarshalsPrisonersWaitForTheShuttle(Map map)
        {
            Guarded("inside: the marshals' prisoners wait for the shuttle and come first", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "marshals' prisoners waiting")) return;
                SecurityLevelDef sec = InmateUtility.DefaultSecurity;
                int beds = InsideFreeBeds(map, r, sec, 4);
                (Pawn runner, Pawn leaver) = MarshalsPair(map, r, g);
                g.intake.allowOvercrowding = true;
                int number0 = g.intake.nextInmateNumber;
                r.Add(() => g.intake.nextInmateNumber = number0);
                FugitiveSystem.Hourly(map, g);
                Reoffenders.Hourly(g, map);
                if (runner.Spawned || leaver.Spawned) InsideFail($"the hour they came due put them on the map (the escapee {runner.Spawned}, the ex-inmate {leaver.Spawned}), as it used to: off the shuttle");
                MarshalEntry m0 = g.shuttle.MarshalOf(runner), m1 = g.shuttle.MarshalOf(leaver);
                if (m0 is not { kind: MarshalKind.Recaptured } || m1 is not { kind: MarshalKind.Returning } || m1.reason != ParoleLife.NewOffence || m0.escapedTick < 0)
                    InsideFail($"the escapee and the ex-inmate are not waiting for the shuttle as such ({m0?.kind}, {m1?.kind}, reason {m1?.reason})");
                if (g.fugitives.Exists(f => f.pawn == runner) || g.returning.Exists(x => x.pawn == leaver)) InsideFail("they are still on the police's and the rearrests' lists as well as the shuttle's");
                if (!FugitiveSystem.Tracks(g, runner.thingIDNumber)) InsideFail("the escapee waiting for the shuttle is no longer tracked: their open court case would be dropped");
                if (!Find.WorldPawns.ForcefullyKeptPawns.Contains(runner) || !Find.WorldPawns.ForcefullyKeptPawns.Contains(leaver)) InsideFail("they are not kept in the world while they wait");
                // The roster: first, in the order they were brought in, with nobody generated for them; and before the new inmates of the level.
                var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
                r.Add(() =>
                {
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                });
                List<Pawn> generated = null;
                r.Add(() =>
                {
                    foreach (Pawn p in generated ?? new List<Pawn>())
                    {
                        if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                        if (!p.Destroyed) p.Destroy();
                    }
                });
                List<Pawn> alone = IntakeSystem.GenerateRoster(g, map, out _, out _, ShuttleSystem.MaxRoster);
                if (alone.Count != 2 || alone[0] != runner || alone[1] != leaver) InsideFail($"with no policy intake the roster is {string.Join(", ", alone.Select(p => p.LabelShort))}, not the escapee and the ex-inmate");
                g.intake.perDay[sec] = 2;
                generated = IntakeSystem.GenerateRoster(g, map, out bool held, out int limit, ShuttleSystem.MaxRoster);
                if (generated.Count < 2 || generated[0] != runner || generated[1] != leaver) InsideFail($"with a policy intake of two the marshals' prisoners are not first on the roster ({string.Join(", ", generated.Select(p => p.LabelShort))})");
                else if (generated.Count == 2) RPLog.Message($"[RimPrison:test] inside: no new inmate came on the roster beside the two ({beds} free beds), so that they are first of several was not checked");
                generated.RemoveAll(p => p == runner || p == leaver);
                foreach (Pawn p in generated)
                {
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                    if (!p.Destroyed) p.Destroy();
                }
                generated = null;
                g.shuttle.carriedOver.Clear();
                // A shuttle that lifts off before they step off keeps them on the marshals' list (not in the carried-over: those come as new inmates).
                g.intake.perDay[sec] = 0;
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed || v.rosterSize != 2 || runner.Spawned || leaver.Spawned)
                {
                    InsideFail($"the shuttle did not land with the two aboard and nobody off yet ({v?.Describe()}, spawned {runner.Spawned}/{leaver.Spawned})");
                    EndShuttleVisit(g, map);
                    return;
                }
                ShuttleDepartIn(v, 0);
                v.nextStepTick = ShNow + 5000;
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                if (g.shuttle.marshals.Count != 2 || g.shuttle.carriedOver.Contains(runner) || g.shuttle.carriedOver.Contains(leaver) || runner.Spawned || leaver.Spawned)
                    InsideFail($"a shuttle that left before they stepped off: waiting for the next {g.shuttle.marshals.Count}, carried over as new inmates {g.shuttle.carriedOver.Count(p => p == runner || p == leaver)}");
                // A prisoner who has waited too long for a shuttle (no bed, no shuttle) is sent to another prison, and is forgotten.
                m0 = g.shuttle.MarshalOf(runner);
                if (m0 != null) m0.sinceTick = ShNow - (ShuttleSystem.MarshalWaitDays + 1) * GenDate.TicksPerDay;
                g.shuttle.LivingMarshals();
                if (g.shuttle.IsMarshal(runner) || Find.WorldPawns.ForcefullyKeptPawns.Contains(runner) || !g.shuttle.IsMarshal(leaver)) InsideFail("a prisoner who waited past the limit was kept, or the one who had not was dropped with them");
                EndShuttleVisit(g, map);
            });
        }

        /// <summary>
        /// On the real landing the marshals' prisoners step down the ramp first, as arrivals who wait for a guard (not as a new inmate: no grant, no intake line for them, the stay as a return, counted as admitted
        /// once for the ex-inmate and never for an escapee), each with an arrival line of their own and the letter and the chronicle of their return; the escapee comes back with a longer sentence and time on the
        /// run not counted as served, the ex-inmate on a second stay.
        /// </summary>
        private static void CheckInsideMarshalsPrisonersStepOffTheShuttle(Map map)
        {
            Guarded("inside: the marshals' prisoners step off the shuttle", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "marshals' prisoners arriving")) return;
                SecurityLevelDef sec = InmateUtility.DefaultSecurity;
                InsideFreeBeds(map, r, sec, 4);
                (Pawn runner, Pawn leaver) = MarshalsPair(map, r, g);
                g.intake.allowOvercrowding = true;
                int number0 = g.intake.nextInmateNumber;
                r.Add(() => g.intake.nextInmateNumber = number0);
                CompInmate rc = runner.Inmate(), lc = leaver.Inmate();
                (int sentence0, int admittedAt0, int stays0) = (rc.sentenceDays, rc.admittedTick, lc.timesAdmitted);
                // Something in their pockets (a quarter of the generated are addicts and carry their drug): it must not be left where they step off - on the ramp it held the next shuttle up.
                ThingDef pocketDef = ThingDefOf.Chocolate;
                var pockets0 = new HashSet<Thing>(map.listerThings.ThingsOfDef(pocketDef));
                foreach (Pawn x in new[] { runner, leaver }) x.inventory?.innerContainer.TryAdd(ThingMaker.MakeThing(pocketDef));
                FugitiveSystem.Hourly(map, g);
                Reoffenders.Hourly(g, map);
                if (!g.shuttle.IsMarshal(runner) || !g.shuttle.IsMarshal(leaver))
                {
                    InsideFail("the set-up failed: they are not waiting for the shuttle");
                    return;
                }
                int admitted0 = g.stats.admitted, recaptured0 = g.stats.recaptured, returned0 = g.stats.reoffendersReturned, grants0 = r.NewLedgerEntries.Count();
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 1500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    InsideFail($"the shuttle did not land ({v?.Describe()})");
                    return;
                }
                if (runner.Spawned || leaver.Spawned) InsideFail("they were on the map when the shuttle landed (not aboard)");
                int stepping = ShuttleTicks(() => runner.Spawned && leaver.Spawned, 1200);
                if (!runner.Spawned || !leaver.Spawned)
                {
                    InsideFail($"they did not step off the shuttle ({stepping} ticks, spawned {runner.Spawned}/{leaver.Spawned}, {v.Describe()})");
                    EndShuttleVisit(g, map);
                    return;
                }
                bool arrivals = g.shuttle.ArrivalOf(runner) != null && g.shuttle.ArrivalOf(leaver) != null && rc.inTransit && lc.inTransit;
                bool inmates = runner.IsPrisonerOfColony && leaver.IsPrisonerOfColony && !runner.guest.Released && !leaver.guest.Released;
                if (!arrivals || !inmates) InsideFail($"they did not come off as inmates who are arrivals (arrivals {arrivals}, inmates {inmates})");
                if (g.shuttle.marshals.Exists(m => m.pawn == runner || m.pawn == leaver)) InsideFail("they are still waiting for a shuttle after they stepped off");
                List<Thing> leftBehind = map.listerThings.ThingsOfDef(pocketDef).Where(t => t.Spawned && !pockets0.Contains(t)).ToList();
                if (leftBehind.Count > 0)
                {
                    InsideFail($"what they had in their pockets was left on the ground where they stepped off ({string.Join(", ", leftBehind.Select(t => $"{t.LabelShort} at {t.Position}{(t.IsForbidden(Faction.OfPlayer) ? ", forbidden" : "")}"))}; the ramp {v.shuttle.InteractionCell}): on the ramp it holds the next shuttle up");
                    foreach (Thing t in leftBehind) t.Destroy();
                }
                if (v.steppedOff != 2 || v.grant != 0) InsideFail($"two of the state's own came off: stepped off {v.steppedOff}, grant {v.grant} (the state pays nothing for the return)");
                // What the return does.
                if (rc.sentenceDays <= sentence0 || rc.admittedTick <= admittedAt0 || g.stats.recaptured != recaptured0 + 1) InsideFail($"the escapee came back with sentence {sentence0} -> {rc.sentenceDays}, time on the run counted as served {rc.admittedTick <= admittedAt0}, recaptured {g.stats.recaptured - recaptured0} (1)");
                if (lc.timesAdmitted != stays0 + 1 || g.stats.reoffendersReturned != returned0 + 1) InsideFail($"the ex-inmate came back on stay {lc.timesAdmitted} ({stays0 + 1} expected), returned {g.stats.reoffendersReturned - returned0} (1)");
                if (g.stats.admitted != admitted0 + 1) InsideFail($"the intake counted {g.stats.admitted - admitted0} admitted for an ex-inmate and an escapee brought back (1: the ex-inmate, as a return always did)");
                // Their own arrival line, each.
                List<Message> live = LiveMessages();
                string lineRunner = "RP_MarshalsArrival_Recaptured".Translate(runner.LabelShort).ToString(), lineLeaver = "RP_MarshalsArrival_Returning".Translate(leaver.LabelShort).ToString();
                if (!live.Any(m => m.text == lineRunner) || !live.Any(m => m.text == lineLeaver)) InsideFail("a marshals' prisoner stepped off with no arrival line of their own (the escapee's or the ex-inmate's)");
                live.RemoveAll(m => m.text == lineRunner || m.text == lineLeaver);
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_Recaptured".Translate(runner.LabelShort).ToString()) || !r.NewLetters.Any(l => l.Label.ToString() == "RP_RecapturedLabel".Translate().ToString()))
                    InsideFail("the escapee's return has no chronicle line or no letter");
                if (!r.NewLetters.Any(l => l.Label.ToString() == "RP_ReturnedLabel".Translate(leaver.LabelShort).ToString())) InsideFail("the ex-inmate's return has no letter");
                // The books: no grant for them, the intake's line counts them as arrived.
                int grantsBooked = r.NewLedgerEntries.Skip(grants0).Where(e => e.category == RPDefOf.RP_IntakeGrants).Sum(e => e.amount);
                ShuttleTicks(() => v.arrivalsDone || g.shuttle.visit != v, 600);
                grantsBooked = r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_IntakeGrants).Sum(e => e.amount);
                if (grantsBooked != 0) InsideFail($"an intake grant of {grantsBooked} was booked for the state's own prisoners");
                // The arrivals go on to holding like any other (they are in the arrivals list until they are in).
                ShuttleDepartIn(v, 0);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                foreach (Pawn p in new[] { runner, leaver }) g.shuttle.EndArrival(g.shuttle.ArrivalOf(p) ?? new ArrivalEntry { pawn = p });
                EndShuttleVisit(g, map);
            });
        }
    }
}
