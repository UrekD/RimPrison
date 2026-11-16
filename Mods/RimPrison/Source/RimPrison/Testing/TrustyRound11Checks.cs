using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Trusties, round 11 (see <see cref="Trusties"/>): what the landing reviews found. A trusty is taught only when the zone has no work for them right now (not when it has growing cells that are all sown and unripe); a referral
    /// releases them to the referred course and no other; a walk home's planned cell is no cell of a prison; the wing targets of a game are not a loaded one's. Each check says what it would see without the change it guards.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckTrustiesRound11(Map map)
        {
            CheckTrustyIdleGrowers(map);
            CheckTrustyReferralOneCourse(map);
            CheckTrustyChosenCell(map);
            CheckTrustyResetForgets(map);
        }

        /// <summary>
        /// The students the class pass collects for a program in this scene (the pass's own collecting, the plain pass or the referral's), or null (and a failure) when it cannot be asked.
        /// </summary>
        private static List<Pawn> TrustyStudents(Map map, TrustyScene s, ProgramDef prog, bool referredPass = false)
        {
            MethodInfo collect = AccessTools.Method(typeof(ProgramSystem), "CollectAttendees");
            var taken = AccessTools.Field(typeof(ProgramSystem), "taken")?.GetValue(null) as HashSet<Pawn>;
            if (collect == null || taken == null)
            {
                s.Fail("the check's own fixture: the class pass's collecting of students was not found");
                return null;
            }
            taken.Clear();
            var cand = new ProgramSystem.Candidate { program = prog };
            collect.Invoke(null, new object[] { map, s.pm, s.g, prog, cand, referredPass });
            taken.Clear();
            return cand.pawns;
        }

        /// <summary>A course for the check's inmate to be taught: Foundation education, which is for everyone who reads badly (their skill is put down for it, and put back).</summary>
        private static ProgramDef TrustyFoundation(TrustyScene s, Rollback r, Pawn t)
        {
            ProgramDef prog = DefDatabase<ProgramDef>.GetNamedSilentFail("RP_FoundationEducation");
            if (prog == null || prog.Slot != RPLaborDefOf.RP_Work || !prog.everyone) { s.Fail("the check's own premise: no program for everyone that takes the work slot"); return null; }
            if (t.skills?.GetSkill(SkillDefOf.Intellectual) is { } skill && skill.Level > prog.maxSkillLevel)
            {
                int level0 = skill.Level;
                r.Add(() => skill.Level = level0);
                skill.Level = 0;
            }
            if (!prog.Eligible(t, t.Inmate())) { s.Fail($"the check's own premise: the trusty is not eligible for {prog.defName}"); return null; }
            return prog;
        }

        // ---- an idle grower is taught ----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A strip whose only work is a growing zone: with everything in it sown and growing there is nothing to do (the zone counts its growing cells as places, so the staffing says there is work, but a trusty with no target is no one
        /// at work), and a class of the work slot takes the trusty; a ripe plant is a harvest and an empty cell a sowing, and the class leaves them be. (The test was whether the zone had a place for a worker, which a growing zone always has.)
        /// </summary>
        private static void CheckTrustyIdleGrowers(Map map)
        {
            Guarded("trusties: a trusty on a growing zone with nothing ripe or to sow is taught", map, r =>
            {
                const string check = "idle grower";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztig1", s.At(10, 2));
                Pawn t = s.Trusty("Ztig2", s.At(12, 2), claim: s.bed);
                CompInmate c = t.Inmate();
                ProgramDef prog = TrustyFoundation(s, r, t);
                if (prog == null) { TrustyStill(t, guard); return; }
                var growingCells = new CellRect(s.origin.x + 14, s.origin.z + 2, 3, 1);
                ThingDef crop = TrustyGrowerCrop(map, s.r, growingCells, check);
                if (crop == null) { s.Fail("the check's own fixture: no crop the soil allows on the strip's cells, so a sowing cannot be judged"); TrustyStill(t, guard); return; }
                var growing = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(growing);
                s.r.Add(() => { if (map.zoneManager.AllZones.Contains(growing)) growing.Delete(); });
                var plants = new List<Plant>();
                foreach (IntVec3 cell in growingCells)
                {
                    growing.AddCell(cell);
                    var seedling = (Plant)ThingMaker.MakeThing(crop);
                    seedling.Growth = 0.2f;
                    GenSpawn.Spawn(seedling, cell, map);
                    plants.Add(seedling);
                    s.r.Add(() => { if (seedling.Spawned) seedling.Destroy(DestroyMode.Vanish); });
                }
                growing.SetPlantDefToGrow(crop);
                s.Beat();
                c.UpdateWorkPass();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                var worker = (InmateJobWorker_Outside)RPTrustyDefOf.RP_JobOutside.Worker;
                if (worker.Capacity(map) == 0) { s.Fail("the check's own premise: the growing zone on the strip is no place of work (the staffing's count)"); TrustyStill(t, guard); return; }
                if (plants.Any(p => p.HarvestableNow)) { s.Fail($"the check's own premise: the seedlings are ripe ({crop.defName})"); TrustyStill(t, guard); return; }

                // Everything sown and growing: no work, they are taught.
                if (worker.HasWork(t)) s.Fail("a trusty on a strip whose growing zone is all sown and unripe, with nothing to cut, is said to have work (the zone has a place for a worker, but nothing for them to do)");
                if (ProgramSystem.KeepsToTheirJob(prog, t, c)) s.Fail("a class of the work slot leaves a trusty be on a growing zone with nothing ripe and nothing to sow");
                if (TrustyStudents(map, s, prog) is { } idle && !idle.Contains(t)) s.Fail($"the class pass did not collect a trusty with nothing to do on the strip: {TrustyLine(s, t)}");

                // A ripe plant: a harvest to do.
                plants[1].Growth = 1f;
                if (!plants[1].HarvestableNow) s.Fail("the check's own premise: the plant set to full growth is not ripe");
                else if (!worker.HasWork(t)) s.Fail("a trusty with a ripe plant of the growing zone to harvest is said to have no work");
                else if (TrustyStudents(map, s, prog) is { } ripe && ripe.Contains(t)) s.Fail("a class collected a trusty with a ripe plant to harvest");
                plants[1].Growth = 0.2f;

                // An empty cell: a sowing to do.
                plants[2].Destroy(DestroyMode.Vanish);
                if (!worker.HasWork(t)) s.Fail($"a trusty with an empty cell of the growing zone to sow ({crop.defName}) is said to have no work");
                else if (TrustyStudents(map, s, prog) is { } empty && empty.Contains(t)) s.Fail("a class collected a trusty with a cell to sow");
                TrustyStill(t, guard);
            });
        }

        // ---- a referral is for the referred course ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty at the outside work (a tree to cut, the party out) who the warden referred to a course of the work slot: that course takes them (the referral pass; a control, true before and after), and no other course does - the plain pass
        /// of Foundation education leaves them at their work as it leaves any worker with work waiting. (The referral was taken for the whole of the work-slot classes: a referral to a course that could not start that hour left them to
        /// any other.)
        /// </summary>
        private static void CheckTrustyReferralOneCourse(Map map)
        {
            Guarded("trusties: a referral takes a trusty to the referred course only", map, r =>
            {
                const string check = "referral";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztrf1", s.At(10, 2));
                Pawn t = s.Trusty("Ztrf2", s.At(12, 2), claim: s.bed);
                CompInmate c = t.Inmate();
                s.Tree(s.At(12, 3));
                ProgramDef other = TrustyFoundation(s, r, t);
                if (other == null) { TrustyStill(t, guard); return; }
                ProgramDef referral0 = c.referral;
                r.Add(() => c.referral = referral0);
                // (A course of the work slot, other than the one the plain pass is asked about, that the warden's referral makes them eligible for.)
                ProgramDef referred = null;
                foreach (ProgramDef prog in DefDatabase<ProgramDef>.AllDefsListForReading)
                {
                    if (prog == other || prog.Slot != RPLaborDefOf.RP_Work) continue;
                    c.referral = prog;
                    if (prog.Eligible(t, c)) { referred = prog; break; }
                }
                if (referred == null) { s.Fail("the check's own fixture: no other course of the work slot that a referral makes the trusty eligible for"); TrustyStill(t, guard); return; }
                s.Beat();
                c.UpdateWorkPass();
                var worker = (InmateJobWorker_Outside)RPTrustyDefOf.RP_JobOutside.Worker;
                if (!s.pm.party.isOut || !worker.HasWork(t)) { s.Fail($"the check's own premise: the party is out and the trusty has work to do ({referred.defName} is the referral): {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                if (!other.Eligible(t, c)) { s.Fail($"the check's own premise: the trusty is eligible for {other.defName}"); TrustyStill(t, guard); return; }

                if (TrustyStudents(map, s, other) is { } plain && plain.Contains(t)) s.Fail($"referred to {referred.defName}, the plain pass of {other.defName} took the trusty from their work: {TrustyLine(s, t)}");
                if (!ProgramSystem.KeepsToTheirJob(other, t, c)) s.Fail($"a class that is not the referral does not leave a trusty with work be ({other.defName}; referred to {referred.defName})");
                // The controls: the referral pass of the referred course takes them, and the course that holds them is not kept off them by their job.
                if (ProgramSystem.KeepsToTheirJob(referred, t, c)) s.Fail($"the course a trusty was referred to is kept from them by their job ({referred.defName})");
                if (TrustyStudents(map, s, referred, referredPass: true) is { } referralPass && !referralPass.Contains(t)) s.Fail($"a trusty at work was not taken by the referral pass of the course they were referred to ({referred.defName}): {TrustyLine(s, t)}");
                TrustyStill(t, guard);
            });
        }

        // ---- the walk's chosen cell ---------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A walk home for a holder with no bed names its cell once (the leg's target), and the cell is honoured unless it is in a prison cell: a cell chosen for a walk to a bed that was lost before the end (a cell beside the bed, in a
        /// room whose door the guard's walk does not open) is asked for anew. The control: a cell of the corridor, off the prison cells, is the one used.
        /// </summary>
        private static void CheckTrustyChosenCell(Map map)
        {
            Guarded("trusties: a chosen cell in a prison cell is not where a holder with no bed is walked to", map, r =>
            {
                const string check = "chosen cell";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztcc1", s.At(5, 2));
                Pawn u = s.Trusty("Ztcc2", s.At(3, 3));
                CompInmate uc = u.Inmate();
                s.Beat();
                uc.UpdateWorkPass();
                if (!s.pm.party.isOut || !uc.outsidePass || u.ownership?.OwnedBed != null) { s.Fail($"the check's own premise: the party is out and the holder has the pass and no bed: {TrustyLine(s, u)}"); TrustyStill(u, guard); return; }
                IntVec3 inCell = s.InCell, corridor = s.Corridor;
                if (inCell.GetRoom(map) is not { IsPrisonCell: true } || corridor.GetRoom(map) is { IsPrisonCell: true }) { s.Fail($"the check's own premise: {inCell - s.origin} is in a prison cell and {corridor - s.origin} is not"); TrustyStill(u, guard); return; }
                IntVec3 asked = EscortWalk.HomeCell(u, guard, toWing: true, chosen: inCell);
                if (!asked.IsValid || asked.GetRoom(map) is { IsPrisonCell: true }) s.Fail($"a walk home with a cell of a prison cell chosen ({inCell - s.origin}) goes to {(asked.IsValid ? (asked - s.origin).ToString() : "nowhere")}, in a prison cell: a cell the guard's walk opens no door to");
                IntVec3 kept = EscortWalk.HomeCell(u, guard, toWing: true, chosen: corridor);
                if (kept != corridor) s.Fail($"the control: a chosen cell off the prison cells ({corridor - s.origin}) was not the one the walk went to ({(kept.IsValid ? (kept - s.origin).ToString() : "nowhere")})");
                TrustyStill(u, guard);
            });
        }

        // ---- a game's wing targets ---------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The wing targets are kept for a beat, by pawn and guard (thing ids): what the game's start does about them (<see cref="Trusties.Reset"/>, from the game's init, since a reloaded save gives the same ids to the same pawns before the
        /// first beat can empty them). With a target kept for a holder at one side of the strip and the holder put at the other, the kept cell is answered again (the premise: that is the cache); after the reset the cell nearest to them is.
        /// </summary>
        private static void CheckTrustyResetForgets(Map map)
        {
            Guarded("trusties: a game's start forgets the wing targets", map, r =>
            {
                const string check = "reset";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                Pawn u = s.Trusty("Ztrs1", s.At(3, 3), withJob: false);
                Trusties.ForgetWingTargets();
                IntVec3 west = Trusties.WingTarget(u);
                s.Place(u, s.At(18, 3));
                IntVec3 stale = Trusties.WingTarget(u);
                if (!west.IsValid || stale != west) { s.Fail($"the check's own premise: the wing target is kept for the beat ({west - s.origin} asked from the west, {stale - s.origin} from the east)"); TrustyStill(u); return; }
                Trusties.Reset();
                IntVec3 east = Trusties.WingTarget(u);
                if (!east.IsValid || east == west) s.Fail($"after a game's start the wing target of a holder is still the one kept from before it ({west - s.origin}; asked from {(u.Position - s.origin)}, {(east.IsValid ? (east - s.origin).ToString() : "none")})");
                Trusties.ForgetWingTargets();
                TrustyStill(u);
            });
        }
    }
}
