using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Trusties, rounds 7 and 8 (see <see cref="Trusties"/>): what the events run's own invariants and its live prison make of the trusty checks. A real arrival is not sent to a bed of the checks' scene (and left on the open ground when the scene is
    /// taken down), and a trusty at the outside work is not an inmate "outside the compound" for the run's invariant. Each check says what it would see without the change it guards.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckTrustiesRound7(Map map)
        {
            CheckTrustySceneBedsAreHeld(map);
            CheckTrustyOutsideTheCompound(map);
        }

        /// <summary>
        /// The invariant at the end of a run: an inmate who is not released and stands off the compound's ground is outside it - but not by the rule of the trusties' work (<see cref="Trusties.Exempt"/>, <see cref="Trusties.WantsOutsideToken"/>):
        /// not in a mental state, out in the open on land the prison owns, and either holding the return token (a trusty on the way, or a revoked one on the way back) or a trusty standing on the strip or within the radius of it. One who walked off has lost
        /// the status and the token and counts; one in a mental break, or standing off the prison's land, counts. Used by <see cref="AutoTest"/>'s end-of-run check.
        /// </summary>
        private static bool OutsideTheCompound(Pawn p, CellRect footprint)
        {
            if (p.guest.Released || footprint.Contains(p.Position)) return false;
            if (p.Inmate() is not { } c || p.InMentalState || !p.Spawned || PrisonMap.Of(p.Map) is not { } pm || !pm.IsOutsideSpace(p.Position) || !PrisonGrounds.Allows(p.Map, p.Position)) return true;
            return !(c.outsidePass || c.IsTrusty && NearTheStrip(p, pm));
        }

        /// <summary>The strip, or within <see cref="Trusties.ExemptRadius"/> cells of it (the rule's own radius: a trusty at the edge of the work, a cell off it beside a tree).</summary>
        private static bool NearTheStrip(Pawn p, PrisonMap pm)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(p.Position, Trusties.ExemptRadius, true))
                if (cell.InBounds(p.Map) && pm.zones.ZoneAt(cell) == RPTrustyDefOf.RP_OutsideWork) return true;
            return false;
        }

        // ---- the scene's beds are not the live prison's -------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The scene's beds are in the minimum wing and are prisoner beds, so a real arrival of the minimum level (a shuttle lands in the middle of the checks' thousands of real ticks) was sent to one of them - and left on the open ground
        /// when the check was taken down (the run's "1 inmates outside the compound": Marina, who came off the shuttle during the checks and lost her scene bed four times). With them held, the list of free beds has none of them. (The
        /// control: with the hold taken off the same list has them all - the scene's beds are free beds, so the check judges something.)
        /// </summary>
        private static void CheckTrustySceneBedsAreHeld(Map map)
        {
            Guarded("trusties: the scene's beds are not free beds of the live prison", map, r =>
            {
                const string check = "scene beds";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                Building_Bed[] beds = { s.bed, s.bed2, s.bedOut };
                s.pm.ForceDoorUpdate();
                HashSet<Building_Bed> held = IntakeSystem.HeldForTests;
                string Describe(List<Building_Bed> free) => string.Join(", ", beds.Where(free.Contains).Select(b => (b.Position - s.origin).ToString()));
                bool premise;
                try
                {
                    // (Taken off: what a real arrival would be offered.)
                    IntakeSystem.HeldForTests = null;
                    List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(map, RPTrustyDefOf.RP_Minimum);
                    premise = beds.All(free.Contains);
                    if (!premise) s.Fail($"the check's own premise: the scene's beds are not all free beds for the minimum level with no hold (free: {Describe(free)}; the bed in the wing is a prisoner bed {s.bed.ForPrisoners})");
                }
                finally
                {
                    IntakeSystem.HeldForTests = held;
                }
                if (!premise) return;
                List<Building_Bed> offered = IntakeSystem.FreePrisonerBeds(map, RPTrustyDefOf.RP_Minimum);
                if (beds.Any(offered.Contains)) s.Fail($"a real arrival of the minimum level is offered the scene's beds: {Describe(offered)}");
                if (beds.Any(IntakeSystem.FreeBedsInOrder(map, RPTrustyDefOf.RP_Minimum).Contains)) s.Fail("the beds in order (the reading that asks whether a bed exists) list the scene's beds");
            });
        }

        // ---- the trusty at work is not outside the compound ------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The invariant mirrors the trusties' rule. Not outside: a trusty on the strip, one holding the token off it, a revoked one holding it on the way back, a released one. Not outside either: a trusty with no token a cell or two off the strip (the rule's radius). Outside (each a control, and each fails if the invariant
        /// is wider than the rule): an inmate who is no trusty on the same strip or a cell or two off it, a trusty beyond the radius with no token, a trusty far out with no token, a trusty in a mental break on the strip, a holder of the token in a mental break, and a holder of the token standing off the
        /// prison's land. (The invariant exempted every trusty on the strip and every holder wherever they stood: a berserk one on the strip, or one off the land, was not seen.)
        /// </summary>
        private static void CheckTrustyOutsideTheCompound(Map map)
        {
            Guarded("trusties: the compound invariant mirrors the trusties' rule", map, r =>
            {
                const string check = "invariant";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                CellRect footprint = PrisonBuilder.Footprint(map);
                Pawn onStrip = s.Trusty("Ztiv1", s.InZone, claim: s.bed);
                Pawn withPass = s.Trusty("Ztiv2", s.OffZoneNear, claim: s.bed2);
                Pawn farOut = s.Trusty("Ztiv3", s.FarOut, withJob: false);
                Pawn plain = s.Inmate("Ztiv4", RPTrustyDefOf.RP_Minimum, s.At(12, 2));
                Pawn released = s.Inmate("Ztiv5", RPTrustyDefOf.RP_Minimum, s.At(14, 2));
                Pawn revoked = s.Inmate("Ztiv6", RPTrustyDefOf.RP_Minimum, s.OffZoneNear);
                Pawn berserkOnStrip = s.Trusty("Ztiv7", s.At(16, 2), withJob: false);
                Pawn berserkWithPass = s.Trusty("Ztiv8", s.At(4, 0), withJob: false);
                Pawn offLand = s.Trusty("Ztiv9", s.At(6, 0), withJob: false);
                Pawn nearStrip = s.Trusty("Ztiv10", s.At(14, -1), withJob: false);
                Pawn plainNear = s.Inmate("Ztiv11", RPTrustyDefOf.RP_Minimum, s.At(16, -1));
                withPass.Inmate().outsidePass = true;
                revoked.Inmate().outsidePass = true;
                berserkWithPass.Inmate().outsidePass = true;
                offLand.Inmate().outsidePass = true;
                var releasedGuest = released.guest;
                bool released0 = releasedGuest.Released;
                r.Add(() => releasedGuest.Released = released0);
                releasedGuest.Released = true;
                Pawn[] all = { onStrip, withPass, farOut, plain, released, revoked, berserkOnStrip, berserkWithPass, offLand, nearStrip, plainNear };
                if (all.Any(p => footprint.Contains(p.Position))) { s.Fail("the check's own premise: the scene is inside the compound's footprint"); return; }
                if (s.pm.zones.ZoneAt(onStrip.Position) != RPTrustyDefOf.RP_OutsideWork || s.pm.zones.ZoneAt(berserkOnStrip.Position) != RPTrustyDefOf.RP_OutsideWork
                    || new[] { withPass, revoked, berserkWithPass, offLand }.Any(p => s.pm.zones.ZoneAt(p.Position) == RPTrustyDefOf.RP_OutsideWork || !s.pm.IsOutsideSpace(p.Position)))
                    s.Fail("the check's own premise: two trusties are on the strip and the pass holders are off it, in the open");
                if (revoked.Inmate().IsTrusty) s.Fail("the check's own premise: the revoked one is a trusty");
                if (new[] { nearStrip, plainNear }.Any(p => s.pm.zones.ZoneAt(p.Position) == RPTrustyDefOf.RP_OutsideWork || !s.pm.IsOutsideSpace(p.Position)) || !Trusties.Exempt(nearStrip, nearStrip.Inmate()) || Trusties.Exempt(plainNear, plainNear.Inmate()))
                    s.Fail($"the check's own premise: the two are off the strip, in the open, and the rule's exemption covers the trusty and not the other ({TrustyLine(s, nearStrip)})");
                foreach (Pawn p in new[] { berserkOnStrip, berserkWithPass })
                    if (!p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, transitionSilently: true)) s.Fail($"the check's own premise: a mental break could not be started on {p.LabelShort}");
                // (The token survives a break that is no escape: the start takes the pass and keeps it.)
                if (!berserkWithPass.InMentalState || !berserkWithPass.Inmate().outsidePass) s.Fail($"the check's own premise: the holder of the token in a break keeps the token ({TrustyLine(s, berserkWithPass)})");
                if (OutsideTheCompound(onStrip, footprint)) s.Fail($"a trusty on the strip counts as outside the compound ({TrustyLine(s, onStrip)})");
                if (OutsideTheCompound(withPass, footprint)) s.Fail($"a trusty off the strip with the pass counts as outside the compound ({TrustyLine(s, withPass)})");
                if (OutsideTheCompound(revoked, footprint)) s.Fail($"one whose status was taken, on the way back with the token, counts as outside the compound ({TrustyLine(s, revoked)})");
                if (!OutsideTheCompound(plain, footprint)) s.Fail($"an inmate who is no trusty, on the strip, does not count as outside the compound ({TrustyLine(s, plain)})");
                if (!OutsideTheCompound(farOut, footprint)) s.Fail($"a trusty far from the strip with no pass does not count as outside the compound ({TrustyLine(s, farOut)})");
                if (!OutsideTheCompound(berserkOnStrip, footprint)) s.Fail($"a trusty in a mental break on the strip does not count as outside the compound ({TrustyLine(s, berserkOnStrip)})");
                if (!OutsideTheCompound(berserkWithPass, footprint)) s.Fail($"a holder of the token in a mental break does not count as outside the compound ({TrustyLine(s, berserkWithPass)})");
                if (OutsideTheCompound(nearStrip, footprint)) s.Fail($"a trusty with no token a cell or two off the strip, where the rule exempts them (within {Trusties.ExemptRadius} cells of it), counts as outside the compound ({TrustyLine(s, nearStrip)})");
                if (!OutsideTheCompound(plainNear, footprint)) s.Fail($"an inmate who is no trusty, a cell or two off the strip, does not count as outside the compound ({TrustyLine(s, plainNear)})");
                if (OutsideTheCompound(released, footprint)) s.Fail("a released inmate counts as outside the compound");
                // The prison's land ends short of them: the token is not kept there, and the invariant does not see it kept.
                PrisonGrounds grounds = s.pm.grounds;
                (bool defined, int minX, int maxX, int minZ, int maxZ) = (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ);
                r.Add(() => (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ));
                (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (true, 0, 1, 0, 1);
                if (!OutsideTheCompound(offLand, footprint)) s.Fail($"a holder of the token standing off the prison's land does not count as outside the compound ({TrustyLine(s, offLand)})");
                (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ);
                if (OutsideTheCompound(offLand, footprint)) s.Fail($"the control: the same holder on the prison's land counts as outside the compound ({TrustyLine(s, offLand)})");
                TrustyStill(all);
            });
        }
    }
}
