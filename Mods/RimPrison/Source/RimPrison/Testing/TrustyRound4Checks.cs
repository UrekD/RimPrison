using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Trusties, round 4 (see <see cref="Trusties"/>): a locked gate (the holder outside is told to the warden, no escort is promised, arrivals do not use the gate), the return token across a changed job and a mental break, the party's
    /// supervision (a guard beside one still inside, an unmanned post), the daily pass moving a trusty to the outside job, a running job ended when its target leaves the zone (after a repaint, and on the first beat after a load) and a
    /// new stay ending the pass at once. Each says what it would see without the change it guards.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckTrustiesRound4(Map map)
        {
            CheckTrustyLockedGate(map);
            CheckTrustyToken(map);
            CheckTrustySupervision(map);
            CheckTrustyDailyPass(map);
            CheckTrustyRunningJobs(map);
            CheckTrustyNewStayPass(map);
        }

        // ---- a locked gate -----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The gate set to Locked while a holder is out on the strip: it stays locked for everyone (the escort cannot take it), so the holder is not in the escort's list, the warden is told once a day (not once a beat), the count does not
        /// write them up, and the gate is still a gate of the zone (listed with its mode); a Staff-only gate tells too (and the escort is asked for it). No gate of the work zone is a way in for arrivals, in any mode. (Without the change a
        /// locked gate put the holder on the escort's list that nobody could serve, in silence.)
        /// </summary>
        private static void CheckTrustyLockedGate(Map map)
        {
            Guarded("trusties: a locked gate", map, r =>
            {
                const string check = "locked gate";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                int told0 = Trusties.lockedOutTold;
                r.Add(() => Trusties.lockedOutTold = told0);
                Pawn guard = s.Guard("Ztlg1", s.At(10, 2));
                Pawn t = s.Trusty("Ztlg2", s.InZone, claim: s.bed);
                Pawn t2 = s.Trusty("Ztlg3", s.At(12, 3), claim: s.bed2);
                CompInmate c = t.Inmate();
                CompPrisonDoor gateComp = s.gate.GetComp<CompPrisonDoor>();
                s.Beat();
                c.UpdateWorkPass();
                t2.Inmate().UpdateWorkPass();
                if (!s.pm.party.isOut || !c.outsidePass || !t2.Inmate().outsidePass) { s.Fail($"the check's own premise: the party is not out with two holders on the strip: {TrustyLine(s, t)}"); TrustyStill(t, t2, guard); return; }
                // No gate of the work zone is a way in for arrivals, whatever its mode (the rule itself, and the door's own verdict).
                if (!CompPrisonDoor.ArrivalPass(PrisonDoorMode.Auto, DoorSide.Outside, DoorSide.InmateSpace, false, false)) s.Fail("the control: an Auto door between the open and inmate space is no way in for arrivals");
                if (CompPrisonDoor.ArrivalPass(PrisonDoorMode.Auto, DoorSide.Outside, DoorSide.InmateSpace, false, false, trustyGate: true)) s.Fail("the work zone's gate is a way in for arrivals");
                foreach (PrisonDoorMode mode in new[] { PrisonDoorMode.Auto, PrisonDoorMode.Inmates })
                {
                    gateComp.mode = mode;
                    s.pm.ForceDoorUpdate();
                    if (!gateComp.trustyGate) s.Fail($"the gate is no gate of the zone with the door set to {mode} (the geometry flag)");
                    if (gateComp.arrivalPass) s.Fail($"a new arrival may pass the work zone's gate with the door set to {mode}");
                }
                gateComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                // Locked: the holder cannot get in, and nobody can let them.
                gateComp.mode = PrisonDoorMode.Locked;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (!gateComp.trustyGate) s.Fail("a locked gate is no gate of the zone (the Deployment tab would stop listing it: 'no gate yet')");
                // At work with the party out, a shut gate is no news for the warden (the day's work is why they are outside): not told yet.
                if (Trusties.lockedOutTold != told0) s.Fail($"the warden was told {Trusties.lockedOutTold - told0} times about holders outside a locked gate while the party was out and they were at work (none expected)");
                if (t.CanReach(s.InCell, PathEndMode.Touch, Danger.Deadly)) { s.Fail("the check's own premise: with the gate locked the holder can still reach their bed"); TrustyStill(t, t2, guard); return; }
                if (Trusties.Stranded(t)) s.Fail("a holder behind a locked gate is on the escort's list: no guard can take a locked gate");
                if (!s.pm.party.lockedOut.Contains(t.thingIDNumber) || !Trusties.AwaitingWayBack(t)) s.Fail($"a holder outside a locked gate is not found: {TrustyLine(s, t)}");
                // The party called in (a raid on the map): now they are outside for no work, and the warden is told, once each.
                Trusties.ThreatForTests = true;
                s.Beat();
                if (s.pm.party.isOut) s.Fail($"the check's own premise: a raid did not call the party in ({s.pm.party.state})");
                if (Trusties.lockedOutTold - told0 != 2) s.Fail($"the warden was told {Trusties.lockedOutTold - told0} times about the two outside a locked gate once the party was called in (2 expected)");
                if (!TrustyLiveMessages().Any(m => m.Contains(t.LabelShort) && m.ToLowerInvariant().Contains("locked"))) s.Fail("the message that one is outside the wall and the gate is locked is not on screen");
                s.Beat();
                s.Beat();
                if (Trusties.lockedOutTold - told0 != 2) s.Fail($"the warden was told again within the day ({Trusties.lockedOutTold - told0} in all, 2 expected): once a day, not once a beat");
                InmateCandidates.Shared.Invalidate();
                if (new WorkGiver_EscortInmate().HasJobOnThing(guard, t)) s.Fail("a guard is offered the walk for a holder behind a locked gate");
                // The count does not write them up (the work slot expects the cell with the party in).
                bool partyOut = s.pm.party.isOut;
                s.pm.party.isOut = false;
                if (!RegimePlacement.InExpectedPlace(t, RPLaborDefOf.RP_Work, s.pm)) s.Fail("a holder outside a locked gate is out of place for the count");
                s.pm.party.isOut = partyOut;
                // Unlocked: no longer outside a locked gate, and the day's telling is not forgotten (they are told again tomorrow, not today).
                gateComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (s.pm.party.lockedOut.Count != 0 || Trusties.Stranded(t)) s.Fail("an unlocked gate left the holder locked out or stranded");
                // Staff only: shut to the inmate, a guard's escort passes it - the holder is on the escort's list, and the warden is told (a second holder here is told once, the first was told today).
                Pawn t3 = s.Trusty("Ztlg4", s.At(14, 2));
                t3.Inmate().outsidePass = true;
                t3.Inmate().workPass = RPTrustyDefOf.RP_OutsideWork;
                int told1 = Trusties.lockedOutTold;
                gateComp.mode = PrisonDoorMode.StaffOnly;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (Trusties.lockedOutTold - told1 != 1) s.Fail($"the warden was told {Trusties.lockedOutTold - told1} times about one more holder outside a staff-only gate (1 expected: the other two were told today)");
                if (s.pm.party.lockedOut.Count != 0 || !Trusties.Stranded(t3)) s.Fail("a holder outside a staff-only gate is not on the escort's list");
                gateComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                Trusties.ThreatForTests = false;
                TrustyStill(t, t2, t3, guard);
            });
        }

        // ---- the return token --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A mental break outside shuts the gate to the pawn on the spot (before any look) and keeps the token: the pass is theirs again when it ends; and a job changed to a staff job while outside leaves the token, so the way back is
        /// still theirs when the zone is erased (the guard walks them in), and the staff job's pass waits until they are back inside. (Without the change the pass was recomputed from the job, and the token was lost both ways.)
        /// </summary>
        private static void CheckTrustyToken(Map map)
        {
            Guarded("trusties: the return token", map, r =>
            {
                const string check = "token";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztkn1", s.At(10, 2));
                Pawn t = s.Trusty("Ztkn2", s.At(12, 3), claim: s.bed);
                Pawn u = s.Trusty("Ztkn3", s.At(14, 3), claim: s.bed2);
                CompInmate c = t.Inmate(), uc = u.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                uc.UpdateWorkPass();
                if (!s.pm.party.isOut || !c.outsidePass || !uc.outsidePass) { s.Fail($"the check's own premise: the party is not out with two holders on the strip: {TrustyLine(s, t)}"); TrustyStill(t, u, guard); return; }
                // A mental break outside (a revoked returner, so only the token is theirs): the gate is shut to them at the first moment, the token kept, the pass back when the break is over.
                Trusties.Revoke(u, null, silent: true);
                if (!uc.outsidePass) s.Fail("the check's own premise: a revoked trusty on the strip lost the token");
                else if (!u.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Vandalizing, null, forced: true, otherPawn: null)) s.Fail("the check's own premise: a mental break could not be started");
                else
                {
                    if (uc.workPass != null || s.gate.PawnCanOpen(u)) s.Fail($"the pass was not taken at the start of a mental break outside: {TrustyLine(s, u)}, gate open {s.gate.PawnCanOpen(u)}");
                    uc.UpdateWorkPass();
                    if (uc.workPass != null || s.gate.PawnCanOpen(u)) s.Fail("the gate opens to a pawn in a mental break");
                    if (!uc.outsidePass) s.Fail("a mental break outside took the return token");
                    u.MentalState?.RecoverFromState();
                    uc.UpdateWorkPass();
                    if (!uc.outsidePass || uc.workPass != RPTrustyDefOf.RP_OutsideWork || !s.gate.PawnCanOpen(u)) s.Fail($"the pass did not come back when the mental break was over: {TrustyLine(s, u)}");
                }
                TrustyStill(u);
                // The job changed to the kitchen's while outside: the token stays (the kitchen's pass waits), and the zone erased: found stranded, walked in, and the kitchen's pass is theirs inside.
                InmateJobDef kitchen = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                if (kitchen.zone is not { staffOnly: true }) { s.Fail("the check's own premise: the kitchen job has no staff zone"); TrustyStill(t, u, guard); return; }
                // (The kitchen asks for its safety course: without it the hourly pass takes the job from an untrained inmate whatever the lock says, and an hour of the real ticks of the walk below does.)
                if (kitchen.requiredProgram is { } course && !c.passedPrograms.Contains(course))
                {
                    c.passedPrograms.Add(course);
                    r.Add(() => c.passedPrograms.Remove(course));
                }
                c.job = kitchen;
                c.jobLocked = true;
                c.UpdateWorkPass();
                if (!c.outsidePass || c.workPass != RPTrustyDefOf.RP_OutsideWork) s.Fail($"a job changed to the kitchen's outside took the return token or the pass: {TrustyLine(s, t)}");
                foreach (IntVec3 cell in new CellRect(s.origin.x, s.origin.z + 1, TrustyScene.Width, 4)) s.PaintCell(cell, null);
                s.pm.Notify_DoorsChanged();
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (!Trusties.Stranded(t)) { s.Fail($"a holder whose job was changed outside is not found stranded when the zone is erased: {TrustyLine(s, t)}"); TrustyStill(t, u, guard); return; }
                TrustyEscortHome(s, guard, t, () => t.GetRoom() == InmateUtility.CellOf(t), "a holder with a kitchen job");
                if (t.GetRoom() == InmateUtility.CellOf(t))
                {
                    if (c.job != kitchen) s.Fail($"the check's own fixture: the kitchen job was taken from the holder on the way ({c.job?.defName ?? "none"}): the kitchen's pass cannot be judged");
                    else
                    {
                        c.UpdateWorkPass();
                        if (c.outsidePass || c.workPass != kitchen.zone) s.Fail($"back inside, the token is not given up for the kitchen's pass: {TrustyLine(s, t)}");
                    }
                }
                TrustyStill(t, u, guard);
            });
        }

        // ---- supervision -------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard beside a trusty who is still inside the wing is no supervision of the one out on the strip (a split party: one out, one in); one beside the one outside is. An open guard post that nobody is on or on the way to is
        /// none either, and one a guard is on the way to is. (Without the change the guard beside the one inside counted, and an open post counted as a guard to come.)
        /// </summary>
        private static void CheckTrustySupervision(Map map)
        {
            Guarded("trusties: what supervises the party", map, r =>
            {
                const string check = "supervision";
                // (Where none of the prison's own guards or posts are near: what is near the zone is the check's own, and a post's baseline is none.)
                TrustyScene s = TrustyScene.Build(map, r, check, TrustyScene.Needs.NoGuardsNear);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                TrustyParty party = s.pm.party;
                Pawn outside = s.Trusty("Ztsv1", s.At(1, 1), claim: s.bed);
                Pawn inside = s.Trusty("Ztsv2", s.At(13, 6), claim: s.bed2);
                party.isOut = true;
                party.state = PartyState.Out;
                outside.Inmate().UpdateWorkPass();
                inside.Inmate().UpdateWorkPass();
                s.Beat();
                if (party.near != 0) { s.Fail($"the check's own premise: {party.near} of the prison's own guards or posts are near the scene's strip"); TrustyStill(outside, inside); return; }
                if (!outside.Inmate().outsidePass || !inside.Inmate().outsidePass || !s.pm.IsOutsideSpace(outside.Position) || s.pm.IsOutsideSpace(inside.Position)) { s.Fail($"the check's own premise: one holder out and one inside: {TrustyLine(s, outside)} / {TrustyLine(s, inside)}"); TrustyStill(outside, inside); return; }
                // The guard beside the one inside (thirteen cells from the one outside).
                Pawn guard = s.Guard("Ztsv3", s.At(13, 7));
                s.Beat();
                if (party.near != 0) s.Fail($"a guard standing beside a trusty still inside the wing counts as watching the party out on the strip (near {party.near}): {TrustyLine(s, inside)}");
                s.Place(guard, s.At(3, 2));
                s.Beat();
                if (party.near != 1) s.Fail($"the control: a guard standing beside the trusty out on the strip does not count (near {party.near})");
                // The posts: nobody out, no guard near (the guard goes as far as the compound reaches), an open post near the zone. The baseline first: nothing near, the party in.
                IntVec3 away = s.FarCell();
                if (!away.IsValid) { s.Fail("the check's own premise: no cell of the compound is out of the supervision's reach of the strip, so the guard cannot be put far from the zone"); TrustyStill(outside, inside, guard); return; }
                s.Place(guard, away);
                s.Place(outside, s.Corridor);
                s.Place(inside, s.At(11, 6));
                party.isOut = false;
                party.state = PartyState.Hold;
                party.holdUntilTick = -1;
                s.Beat();
                if (party.near != 0 || party.isOut) { s.Fail($"the check's own premise: with the guard far off, nothing is near the zone and the party is in ({party.state}, near {party.near}, out {party.isOut})"); TrustyStill(outside, inside, guard); return; }
                Thing post = GuardPosts.Place(map, s.At(10, 2));
                if (post == null) { s.Fail("the check's own fixture: no guard post could be placed beside the strip, so the posts are not judged"); TrustyStill(outside, inside, guard); return; }
                s.r.Add(() => { if (post.Spawned) post.Destroy(DestroyMode.Vanish); });
                s.Beat();
                if (party.near != 0 || party.isOut) s.Fail($"an open guard post with nobody on it or on the way counts as a guard to come: near {party.near}, party out {party.isOut} ({party.state})");
                if (party.postsNear < 1) s.Fail($"the unmanned post near the zone is not counted for the tab (posts near {party.postsNear})");
                else if (Trusties.StateText(s.pm) != "RP_TrustyParty_Unmanned".Translate(party.postsNear, party.holders).Resolve()) s.Fail($"the line for a party kept in by a post nobody mans says '{Trusties.StateText(s.pm)}'");
                guard.jobs.StartJob(JobMaker.MakeJob(RPPostDefOf.RP_ManPost, post), JobCondition.InterruptForced);
                if (guard.CurJobDef != RPPostDefOf.RP_ManPost || GuardPosts.StanderOf(post) != guard) s.Fail($"the check's own fixture: the guard was not put on the way to the post (doing {guard.CurJobDef?.defName ?? "nothing"})");
                else
                {
                    s.Beat();
                    if (!party.isOut || party.near < 1) s.Fail($"a guard on the way to the open post near the zone does not count (state {party.state}, near {party.near})");
                }
                TrustyStill(outside, inside, guard);
            });
        }

        // ---- the daily pass ----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The daily job pass moves a trusty whose job the warden did not lock to the outside job when a place is free (those with no job first); a locked job stays; the grant's message stays as it is. (Without the change only a trusty
        /// with no job was ever given it, and a trusty nearly always has one.)
        /// </summary>
        private static void CheckTrustyDailyPass(Map map)
        {
            Guarded("trusties: the daily pass gives the outside job to a trusty who has another", map, r =>
            {
                const string check = "daily pass";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.Tree(s.At(12, 3));
                InmateJobDef cleaning = DefDatabase<InmateJobDef>.GetNamed("RP_JobCleaning");
                Pawn busy = s.Trusty("Ztdj1", s.At(11, 6), withJob: false);
                Pawn idle = s.Trusty("Ztdj2", s.At(12, 6), withJob: false);
                CompInmate bc = busy.Inmate(), ic = idle.Inmate();
                bc.job = cleaning;
                bc.jobLocked = false;
                if (!LaborUtility.Eligible(bc, cleaning) || !LaborUtility.Eligible(bc, RPTrustyDefOf.RP_JobOutside)) { s.Fail("the check's own premise: the trusty is not eligible for the cleaning and the outside job"); TrustyStill(busy, idle); return; }
                if (RPTrustyDefOf.RP_JobOutside.Worker.Capacity(map) != 1) { s.Fail($"the check's own premise: the outside job has {RPTrustyDefOf.RP_JobOutside.Worker.Capacity(map)} places for the one tree on the strip, 1 expected"); TrustyStill(busy, idle); return; }
                // One place, two trusties: the one with no job has it, the one with a job keeps it.
                LaborUtility.AssignJobs(map);
                if (ic.job != RPTrustyDefOf.RP_JobOutside) s.Fail($"the daily pass did not give the outside job to the trusty with no job ({ic.job?.defName ?? "none"})");
                if (bc.job != cleaning) s.Fail($"the daily pass took the cleaning from a trusty while a trusty with no job had the one place ({bc.job?.defName ?? "none"})");
                // The one with no job gone: the one with an unlocked job is moved to the place.
                ic.job = null;
                ic.trustySinceTick = -1;
                LaborUtility.AssignJobs(map);
                if (bc.job != RPTrustyDefOf.RP_JobOutside) s.Fail($"the daily pass left a trusty with an unlocked job on it while the outside place was free ({bc.job?.defName ?? "none"}): the status would do nothing");
                // The warden's lock keeps the job.
                bc.job = cleaning;
                bc.jobLocked = true;
                LaborUtility.AssignJobs(map);
                if (bc.job != cleaning || !bc.jobLocked) s.Fail($"the daily pass moved a trusty whose job the warden locked ({bc.job?.defName ?? "none"})");
                TrustyStill(busy, idle);
            });
        }

        // ---- running jobs ------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A cut under way on a tree the strip's repaint left off the zone is ended at the hour's look (the interrupt queued, the reservation released), and the same on the first beat after a load (a new party: no hour seen yet). (Without
        /// the look the job ran on, outside the zone.)
        /// </summary>
        private static void CheckTrustyRunningJobs(Map map)
        {
            Guarded("trusties: a running job off the zone is ended", map, r =>
            {
                const string check = "running jobs";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztrj1", s.At(10, 2));
                Plant tree = s.Tree(s.At(14, 3));
                Pawn t = s.Trusty("Ztrj2", s.At(13, 2), claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                for (int round = 0; round < 2; round++)
                {
                    string when = round == 0 ? "after a repaint" : "on the first beat after a load";
                    if (round == 1)
                    {
                        // (A load: the party is read back without the hour it last looked in; the tree is cut by hand again, and the zone is as it was.)
                        s.pm.party = new TrustyParty { isOut = true, state = PartyState.Out };
                    }
                    t.jobs.StartJob(new Job(JobDefOf.CutPlant, tree), JobCondition.InterruptForced);
                    if (t.CurJobDef != JobDefOf.CutPlant || !map.reservationManager.ReservedBy(tree, t)) { s.Fail($"the check's own fixture ({when}): the cut did not start or did not hold the tree (doing {t.CurJobDef?.defName ?? "nothing"})"); break; }
                    if (round == 0)
                    {
                        s.PaintCell(tree.Position, null);
                        s.pm.party.lastHour = (GenLocalDate.HourOfDay(map) + 12) % 24;
                    }
                    int pending0 = s.pm.PendingInterrupts;
                    s.Beat();
                    if (s.pm.PendingInterrupts <= pending0) s.Fail($"the look {when} queued no interrupt for a cut of a tree off the zone");
                    int ticked = TrustyTicks(() => t.CurJobDef != JobDefOf.CutPlant, 600);
                    if (t.CurJobDef == JobDefOf.CutPlant) s.Fail($"the cut of a tree off the zone ran on {when} ({ticked} ticks): {TrustyLine(s, t)}");
                    else if (map.reservationManager.ReservedBy(tree, t)) s.Fail($"the tree stayed reserved by the trusty after their cut was ended {when}");
                    TrustyStill(t);
                }
                TrustyStill(t, guard);
            });
        }

        // ---- a new stay --------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A new stay ends the pass at once, answers cached for the pawn included (the gate is shut to them the same tick). (Without the notice the warmed reachability still said the strip could be reached.)</summary>
        private static void CheckTrustyNewStayPass(Map map)
        {
            Guarded("trusties: a new stay takes the pass at once", map, r =>
            {
                const string check = "new stay";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztns1", s.At(10, 2));
                Pawn t = s.Trusty("Ztns2", s.Corridor, claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                if (c.workPass != RPTrustyDefOf.RP_OutsideWork) { s.Fail($"the check's own premise: no pass to take: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                map.reachability.ClearCache();
                if (!t.CanReach(s.InZone, PathEndMode.OnCell, Danger.Deadly)) { s.Fail("the check's own premise: the strip cannot be reached with the pass"); TrustyStill(t, guard); return; }
                c.ResetForNewStay();
                if (c.workPass != null || c.outsidePass) s.Fail($"a new stay left the pass: {TrustyLine(s, t)}");
                if (t.CanReach(s.InZone, PathEndMode.OnCell, Danger.Deadly)) s.Fail("a new stay took the pass but the warmed reachability still says the strip can be reached");
                TrustyStill(t, guard);
            });
        }
    }
}
