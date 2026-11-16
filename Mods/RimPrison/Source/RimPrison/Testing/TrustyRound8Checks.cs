using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Trusties, round 8 (see <see cref="Trusties"/>): the one destination of a walk home for a holder with no bed, the warden's word about a holder locked out after the work, and a gate onto another district. (The class pass, the funeral's
    /// call and the compound invariant are in <see cref="CheckTrustiesRound6"/> and <see cref="CheckTrustiesRound7"/>.) Each check says what it would see without the change it guards.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckTrustiesRound8(Map map)
        {
            CheckTrustyOneDestination(map);
            CheckTrustyNoCellDestination(map);
            CheckTrustyLockedOutAfterWork(map);
            CheckTrustyOtherDistrict(map);
        }

        // ---- one destination for the walk ------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A holder with no bed is outside, the gate is staff only (a guard's escort opens it, the holder's own pass does not), a bit of the wing nearer to them is sealed behind a locked door and the rest of the wing is behind the gate. The
        /// guard plans the walk to a cell it can reach (past the gate); the holder's own walk must go to that same cell, and the doors of the pass are those on the way to it. (The holder chose their own cell, the nearest, without the guard: the
        /// sealed one, which no path reaches, and they stood outside the whole walk.)
        /// </summary>
        private static void CheckTrustyOneDestination(Map map)
        {
            Guarded("trusties: a walk home goes to one cell, the guard's and the holder's", map, r =>
            {
                const string check = "one destination";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Zto1", s.At(5, 2));
                Pawn u = s.Trusty("Zto2", s.At(3, 3));
                CompInmate uc = u.Inmate();
                s.Beat();
                uc.UpdateWorkPass();
                if (!s.pm.party.isOut || !uc.outsidePass || u.ownership?.OwnedBed != null) { s.Fail($"the check's own premise: the party is out and the holder has the pass and no bed: {TrustyLine(s, u)}"); TrustyStill(u, guard); return; }
                // (The staff room is a sealed bit of the minimum wing, nearer to the holder than the corridor: its door is locked for everyone.)
                s.Paint(new CellRect(s.origin.x + 1, s.origin.z + 6, 3, 3), s.wingZone);
                CompPrisonDoor sealedDoor = s.staffDoor.GetComp<CompPrisonDoor>(), gateComp = s.gate.GetComp<CompPrisonDoor>();
                sealedDoor.mode = PrisonDoorMode.Locked;
                gateComp.mode = PrisonDoorMode.StaffOnly;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Trusties.ForgetWingTargets();
                s.Beat();
                IntVec3 nearest = Trusties.WingTarget(u);
                IntVec3 planned = EscortWalk.HomeCell(u, guard, toWing: true);
                if (!nearest.IsValid || nearest.x - s.origin.x > 4 || !planned.IsValid || planned.x - s.origin.x < 6)
                {
                    s.Fail($"the check's own premise: the nearest wing cell is in the sealed room ({nearest - s.origin}) and the guard's is in the corridor behind the gate ({planned - s.origin})");
                    TrustyStill(u, guard);
                    return;
                }
                if (guard.CanReach(nearest, PathEndMode.OnCell, Danger.Deadly) || !guard.CanReach(planned, PathEndMode.OnCell, Danger.Deadly)) { s.Fail("the check's own premise: the sealed cell is unreachable for the guard and the planned one is not"); TrustyStill(u, guard); return; }
                if (!Trusties.Stranded(u)) { s.Fail($"the check's own premise: the beat found the holder stranded: {TrustyLine(s, u)}"); TrustyStill(u, guard); return; }

                // The walk, by real ticks: the guard's job and the leg name the planned cell, and the holder is walked in to it.
                InmateCandidates.Shared.Invalidate();
                var giver = new WorkGiver_EscortInmate();
                if (!giver.HasJobOnThing(guard, u)) { s.Fail($"a free guard is not offered the walk for the holder with no bed: {TrustyLine(s, u)}"); TrustyStill(u, guard); return; }
                Job job = giver.JobOnThing(guard, u);
                if (job == null || job.targetC.Cell != planned) { s.Fail($"the walk offered goes to {job?.targetC.Cell - s.origin} and not to the planned cell ({planned - s.origin})"); TrustyStill(u, guard); return; }
                guard.jobs.StartJob(job, JobCondition.InterruptForced);
                bool escaped = false, targetsAgree = true, sawLeg = false;
                string disagree = null;
                int ticked = TrustyTicks(() =>
                {
                    if (u.MentalStateDef == RPMentalStateDefOf.RP_Escaping) escaped = true;
                    if (uc.leg is { kind: EscortKind.Home } leg)
                    {
                        sawLeg = true;
                        if (leg.target != planned && targetsAgree)
                        {
                            targetsAgree = false;
                            disagree = $"the leg's target is {leg.target - s.origin}, the guard's {planned - s.origin}";
                        }
                    }
                    return escaped || !s.pm.IsOutsideSpace(u.Position);
                }, 5000);
                if (escaped) s.Fail("the holder began an escape while the guard walked them home");
                else if (s.pm.IsOutsideSpace(u.Position)) s.Fail($"the guard did not walk the holder in to the planned cell in {ticked} ticks (the holder walked to the nearest cell, behind a locked door, and stood outside): {TrustyLine(s, u)}, guard on {guard.CurJobDef?.defName}, leg {uc.leg?.phase.ToString() ?? "none"}");
                else if (u.Position.x - s.origin.x < 6) s.Fail($"the holder was walked in to {u.Position - s.origin}, not to the corridor the guard planned ({planned - s.origin})");
                if (!sawLeg) s.Fail("the walk never had a leg (the pass's doors were never held)");
                else if (!targetsAgree) s.Fail($"the walk's cells disagree: {disagree}");
                sealedDoor.mode = PrisonDoorMode.Auto;
                gateComp.mode = PrisonDoorMode.Auto;
                TrustyStill(u, guard);
            });
        }

        /// <summary>
        /// A wing that has only cells (every plain cell of it painted over): a guard has no cell to walk a holder with no bed to - the walk opens no other inmate's cell door, so a cell is no place to bring them (the target was the nearest
        /// cell, the pass left its door shut and the holder stood at it).
        /// </summary>
        private static void CheckTrustyNoCellDestination(Map map)
        {
            Guarded("trusties: no cell of a prison cell is where a holder with no bed is walked to", map, r =>
            {
                const string check = "no cell target";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztn1", s.At(11, 2));
                Pawn u = s.Trusty("Ztn2", s.At(12, 3));
                // (The corridor in front of the cells is no part of the wing now: the wing's cells are the cells of the two bedrooms.)
                IntVec3 before = Trusties.WingTarget(u, guard);
                if (!before.IsValid) { s.Fail("the check's own premise: with the corridor in the wing the guard has a cell to walk to"); TrustyStill(u, guard); return; }
                s.Paint(new CellRect(s.origin.x + 7, s.origin.z + 6, 7, 2), null);
                Trusties.ForgetWingTargets();
                List<IntVec3> cells = s.pm.zones.CellsOf(s.wingZone);
                bool anyCell = cells.Any(c => c.GetRoom(map) is { IsPrisonCell: true } && c.Standable(map));
                if (!anyCell) { s.Fail("the check's own premise: the wing has cells of bedrooms left"); TrustyStill(u, guard); return; }
                IntVec3 target = Trusties.WingTarget(u, guard);
                if (target.IsValid) s.Fail($"a holder with no bed is to be walked to {target - s.origin}, in a room with a bed ({target.GetRoom(map)?.IsPrisonCell}), behind a cell door the walk does not open");
                TrustyStill(u, guard);
            });
        }

        // ---- the warden is told after the work --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty outside a gate that is locked at the end of the work slot: the party stays out through the evening, the job is the outside one, but the work is over and nothing keeps them there. The warden is told (once), as for the party
        /// called in. During the work slot nobody is told (the control: the same gate, the same holder, the slot of work). (The word was only for the party in or the job another: in the evening the party is out and the job is the outside one, so a
        /// holder locked out at night rolled the walk-off every hour and nobody knew.)
        /// </summary>
        private static void CheckTrustyLockedOutAfterWork(Map map)
        {
            Guarded("trusties: a holder locked out after the work is told", map, r =>
            {
                const string check = "locked out after work";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                int told0 = Trusties.lockedOutTold;
                r.Add(() => Trusties.lockedOutTold = told0);
                Pawn guard = s.Guard("Ztl1", s.At(9, 2));
                Pawn t = s.Trusty("Ztl2", s.At(9, 3), claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                if (!s.pm.party.isOut || !c.outsidePass) { s.Fail($"the check's own premise: the party is out and the holder has the pass: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                CompPrisonDoor gateComp = s.gate.GetComp<CompPrisonDoor>();
                gateComp.mode = PrisonDoorMode.Locked;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (t.CanReach(s.InCell, PathEndMode.Touch, Danger.Deadly)) { s.Fail("the check's own premise: with the gate locked the holder can still reach their bed"); TrustyStill(t, guard); return; }
                if (!s.pm.party.lockedOut.Contains(t.thingIDNumber)) { s.Fail($"the check's own premise: the holder is locked out: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                if (Trusties.lockedOutTold != told0) s.Fail("the warden was told about a holder at work outside a locked gate (the work slot, the party out)");
                // The work slot is over: the regime puts them to free time.
                s.RegimeAllDay(RPDefOf.RP_FreeTime);
                s.Beat();
                if (!s.pm.party.isOut || c.job != RPTrustyDefOf.RP_JobOutside || PrisonGame.Instance.CurrentActivity(t) == RPLaborDefOf.RP_Work) { s.Fail($"the check's own premise: the party is out, the job is the outside one and the slot is not work: {TrustyLine(s, t)}"); gateComp.mode = PrisonDoorMode.Auto; TrustyStill(t, guard); return; }
                if (Trusties.lockedOutTold - told0 != 1) s.Fail($"the warden was told {Trusties.lockedOutTold - told0} times about a holder locked out of the gate after the work (once expected): {TrustyLine(s, t)}");
                s.Beat();
                if (Trusties.lockedOutTold - told0 != 1) s.Fail($"the warden was told again the same day ({Trusties.lockedOutTold - told0} in all)");
                gateComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                TrustyStill(t, guard);
            });
        }

        // ---- a gate onto another district --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The wing has a second door onto ground walled in on its own (a courtyard of its own district). The holder outside, on the open ground, has their own gate locked while the courtyard's door is Auto: the holder is locked out (the
        /// courtyard's door is nothing to them), and the warden is told once the work is over (not while they are at it: the party is out and the slot is work, so nobody is told then, the first control) - not "stranded", which would
        /// promise an escort through a gate that stays shut. The control: their own gate Auto (a locked door in the courtyard, a staff-only one in
        /// the way home), and the holder is stranded and escorted. (The check would pass with the gates of every district counted: the Auto one of the courtyard suppressed the word about the locked one.)
        /// </summary>
        private static void CheckTrustyOtherDistrict(Map map)
        {
            Guarded("trusties: a gate onto another district does not decide a holder's way", map, r =>
            {
                const string check = "other district";
                TrustyScene s = TrustyScene.Build(map, r, check, southPad: TrustyScene.CourtyardPad);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                int told0 = Trusties.lockedOutTold;
                r.Add(() => Trusties.lockedOutTold = told0);
                Building_Door courtyardDoor = s.SecondGate(13);
                s.Courtyard();
                CompPrisonDoor ownComp = s.gate.GetComp<CompPrisonDoor>(), farComp = courtyardDoor.GetComp<CompPrisonDoor>();
                Pawn guard = s.Guard("Ztd1", s.At(8, 2));
                Pawn t = s.Trusty("Ztd2", s.At(8, 3), claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                s.pm.ForceDoorUpdate();
                if (!s.pm.party.isOut || !c.outsidePass || !ownComp.trustyGate || !farComp.trustyGate) { s.Fail($"the check's own premise: the party is out, the holder has the pass and both doors are gates of the zone (gates {ownComp.trustyGate}/{farComp.trustyGate}; the courtyard's {s.CourtyardLine(courtyardDoor)}): {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                if (t.GetDistrict() == (courtyardDoor.Position + IntVec3.South).GetDistrict(map)) { s.Fail("the check's own premise: the courtyard's door opens onto the holder's district"); TrustyStill(t, guard); return; }

                // Their own gate locked, the courtyard's Auto: locked out, not stranded.
                ownComp.mode = PrisonDoorMode.Locked;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (t.CanReach(s.InCell, PathEndMode.Touch, Danger.Deadly)) { s.Fail("the check's own premise: with their gate locked the holder can still reach their bed"); TrustyStill(t, guard); return; }
                if (!s.pm.party.lockedOut.Contains(t.thingIDNumber) || Trusties.Stranded(t)) s.Fail($"a holder outside their own locked gate, with an Auto door of a courtyard of another district, is not locked out ({(s.pm.party.lockedOut.Contains(t.thingIDNumber) ? "locked out" : "not locked out")}, stranded {Trusties.Stranded(t)}): {TrustyLine(s, t)}");

                // Not told while they are at the work (the party out, the work slot): the word is for after it.
                if (Trusties.lockedOutTold != told0) s.Fail("the warden was told about a holder at work outside their locked gate (the work slot, the party out)");
                // The work is over: told once, whatever the Auto door of the courtyard (a gate of another district is nothing to them).
                s.RegimeAllDay(RPDefOf.RP_FreeTime);
                s.Beat();
                if (Trusties.lockedOutTold - told0 != 1) s.Fail($"the warden was told {Trusties.lockedOutTold - told0} times about a holder outside their locked gate after the work, with an Auto door of another district's courtyard (once expected): {TrustyLine(s, t)}");

                // The control: their own gate Auto (the way home is blocked by a bedroom door, and the escort takes the gate), the courtyard's locked: stranded, not locked out.
                ownComp.mode = PrisonDoorMode.Auto;
                farComp.mode = PrisonDoorMode.Locked;
                s.bedroomDoor.GetComp<CompPrisonDoor>().mode = PrisonDoorMode.Locked;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                if (t.CanReach(s.InCell, PathEndMode.Touch, Danger.Deadly)) s.Fail("the check's own premise: with their bedroom's door locked the holder can still reach their bed");
                else if (s.pm.party.lockedOut.Contains(t.thingIDNumber) || !Trusties.Stranded(t)) s.Fail($"a holder outside their own Auto gate, with a locked door of a courtyard of another district, is not stranded ({(s.pm.party.lockedOut.Contains(t.thingIDNumber) ? "locked out" : "not locked out")}, stranded {Trusties.Stranded(t)}): {TrustyLine(s, t)}");
                s.bedroomDoor.GetComp<CompPrisonDoor>().mode = PrisonDoorMode.Auto;
                farComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                TrustyStill(t, guard);
            });
        }
    }
}
