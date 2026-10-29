using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>The medication side of the psych wing's checks (see <see cref="CheckPsychWing"/>): the doctor's round, the cover that fades when one is missed, the stock of the drug, and the penitentiary's padded cells.</summary>
    public static partial class AutoTest
    {
        // ---- the doctor's round -------------------------------------------------------------------------------------------------------------------

        /// <summary>The doctor is offered a round only for an inmate of psychiatric care whose cover is running out, and only with a dose to take; the round (real ticks) takes a dose, walks it to them and gives it.</summary>
        private static void CheckPsychMedicationRound(Map map)
        {
            Guarded("psych: the doctor makes a medication round", map, r =>
            {
                const string check = "round";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.Project(true);
                s.RegimeAllDay(level, RPDefOf.RP_Lockup);
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                Pawn patient = s.Inmate("Zpr1", level, s.padded, s.CellP);
                Pawn bystander = s.Inmate("Zpr2", medium, s.plain, s.CellQ);
                CompInmate pc = patient.Inmate();
                Pawn doctor = s.Staff("Zpd1", "RP_Doctor", s.At(3, 1));
                QuietTheRealStaff(map, r, "Doctor");
                Thing dose = s.Drug(s.At(5, 1), 3);
                var giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("RP_MedicationRound").Worker;
                InmateCandidates.Shared.Invalidate();
                if (!InmateCandidates.Shared.Medication(map).Contains(patient)) PsychFail(check, "the doctor's list of inmates for a round does not have the patient");
                if (InmateCandidates.Shared.Medication(map).Contains(bystander)) PsychFail(check, "an inmate who is not in psychiatric care is on the doctor's list for a round");
                if (!giver.HasJobOnThing(doctor, patient)) PsychFail(check, "the doctor is offered no round for an uncovered patient");
                if (giver.HasJobOnThing(doctor, bystander)) PsychFail(check, "the doctor is offered a round for an inmate who is not in psychiatric care");
                // A guard has the Doctor work too, and is wanted at the posts: the rounds are the doctors'.
                Pawn guard = s.Staff("Zpd2", "RP_Guard", s.At(3, 1));
                if (giver.HasJobOnThing(guard, patient)) PsychFail(check, "a guard is offered a medication round (the doctors' work)");
                if (!giver.HasJobOnThing(guard, patient, forced: true)) PsychFail(check, "a guard ordered to make a round is refused");
                // A patient in a break cannot be given their medication.
                patient.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Psychotic, forced: true, causedByMood: true);
                InmateCandidates.Shared.Invalidate();
                if (giver.HasJobOnThing(doctor, patient)) PsychFail(check, "the doctor is offered a round for a patient in a break");
                patient.mindState.mentalStateHandler.Reset();
                // The stat the cover lowers (the game's own mood breaks).
                float without = patient.GetStatValue(StatDefOf.MentalBreakThreshold);
                // The round itself.
                InmateCandidates.Shared.Invalidate();
                Job job = giver.JobOnThing(doctor, patient);
                if (job == null || job.targetB.Thing != dose) PsychFail(check, $"the round does not take the dose on the corridor floor ({job?.targetB.Thing})");
                else
                {
                    if (!doctor.CanReach(patient, PathEndMode.Touch, Danger.Deadly)) PsychFail(check, "the doctor cannot reach the patient: " + s.ReachDiagnostics(doctor, patient));
                    doctor.jobs.StartJob(job, JobCondition.InterruptForced);
                    int ticked = ShuttleTicks(() => PsychWing.Covered(patient), 2500);
                    if (!PsychWing.Covered(patient)) PsychFail(check, $"after {ticked} ticks the patient has no cover; the doctor is {doctor.CurJob?.def.defName} at {doctor.Position - s.origin}, carrying {doctor.carryTracker.CarriedThing}");
                    else
                    {
                        if (PsychWing.Cover(patient).Severity < 0.99f) PsychFail(check, $"the dose is {PsychWing.Cover(patient).Severity:F2} of a full one");
                        if (dose.stackCount != 2) PsychFail(check, $"the stack of doses is {dose.stackCount} (3 before the round, 2 expected)");
                        if (doctor.carryTracker.CarriedThing != null) PsychFail(check, "the doctor is still carrying a dose");
                        if (pc.lastDoseTick < s.Now - 4000 || pc.unmedicatedHours != 0) PsychFail(check, $"the file does not know of the dose (last {pc.lastDoseTick}, now {s.Now}, hours without {pc.unmedicatedHours})");
                        float with = patient.GetStatValue(StatDefOf.MentalBreakThreshold);
                        if (with >= without) PsychFail(check, $"the cover does not lower the break threshold ({without:F3} -> {with:F3})");
                    }
                }
                // Covered, no round; the cover running out (half a dose), a round.
                InmateCandidates.Shared.Invalidate();
                if (PsychWing.Cover(patient) is { } cover)
                {
                    cover.Severity = 0.9f;
                    InmateCandidates.Shared.Invalidate();
                    if (giver.HasJobOnThing(doctor, patient)) PsychFail(check, "the doctor is offered a round for a patient who has had one");
                    cover.Severity = PsychWing.RoundBelow - 0.05f;
                    InmateCandidates.Shared.Invalidate();
                    if (!giver.HasJobOnThing(doctor, patient)) PsychFail(check, "the doctor is offered no round when the cover is half gone");
                }
                // No dose on the map: no round.
                dose.Destroy(DestroyMode.Vanish);
                if (giver.HasJobOnThing(doctor, patient)) PsychFail(check, "the doctor is offered a round with no dose to give");
            });
        }

        // ---- a missed round ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The cover is only a day: when a round is missed it runs out (real ticks, the hediff's own fade), the hours without it are counted, and after the grace a break comes (the hourly pass, run for
        /// as many hours as it takes; hours are not waited out). With cover the hours are not counted and the chance is small.
        /// </summary>
        private static void CheckPsychMissedRoundBreaks(Map map)
        {
            Guarded("psych: a missed round lets the cover fade and a break comes", map, r =>
            {
                const string check = "missed round";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level;
                s.Project(true);
                s.RegimeAllDay(level, RPDefOf.RP_Lockup);
                Pawn patient = s.Inmate("Zpm1", level, s.padded, s.CellP);
                CompInmate c = patient.Inmate();
                r.Add(() => patient.mindState.mentalStateHandler.Reset());
                // Covered: the hours are not counted, and the chance of a break is small.
                PsychWing.Give(null, patient);
                for (int h = 0; h < 30; h++) PsychWing.Hourly(patient, c);
                if (c.unmedicatedHours != 0) PsychFail(check, $"hours were counted against a patient with cover ({c.unmedicatedHours})");
                float covered = PsychWing.BreakChancePerHour(patient, c);
                // (Its own fade, in real ticks: a dose with a few minutes left.)
                PsychWing.Cover(patient).Severity = 0.0004f;
                int ticked = ShuttleTicks(() => !PsychWing.Covered(patient), 800);
                if (PsychWing.Covered(patient)) PsychFail(check, $"the cover did not fade after {ticked} ticks");
                patient.mindState.mentalStateHandler.Reset();
                c.breakTicks.Clear();
                c.unmedicatedHours = 0;
                // The hours without it: nothing in the grace, then the chance; a break within a few days.
                if (PsychWing.BreakChancePerHour(patient, c) != 0f) PsychFail(check, "a patient with no cover has a chance of a break in the first hour");
                int broke = -1;
                for (int h = 1; h <= 150 && broke < 0; h++)
                {
                    PsychWing.Hourly(patient, c);
                    if (patient.InMentalState) broke = h;
                    else if (h == PsychWing.GraceHours && PsychWing.BreakChancePerHour(patient, c) != 0f) PsychFail(check, "the chance starts inside the grace");
                }
                if (broke < 0) PsychFail(check, "no break came in 150 hours without cover");
                else if (broke <= PsychWing.GraceHours) PsychFail(check, $"the break came in hour {broke}, inside the {PsychWing.GraceHours} hours of grace");
                if (c.breakTicks.Count != 1) PsychFail(check, $"the break was not counted ({c.breakTicks.Count} on the record)");
                float uncovered = PsychWing.BreakChancePerHour(patient, c);
                if (!(uncovered > covered * 5f)) PsychFail(check, $"the chance of a break without cover ({uncovered:F3} an hour) is not well above the chance with it ({covered:F3})");
                // The next round puts the count back to nothing.
                patient.mindState.mentalStateHandler.Reset();
                PsychWing.Give(null, patient);
                if (c.unmedicatedHours != 0 || PsychWing.BreakChancePerHour(patient, c) != covered) PsychFail(check, "a round did not put the cover back");
            });
        }

        // ---- the stock ----------------------------------------------------------------------------------------------------------------------

        /// <summary>The drug is kept in stock for the rounds: with patients and none on the map, the midnight pass orders it from the supply company; with no patients, or enough, it does not.</summary>
        private static void CheckPsychStock(Map map)
        {
            Guarded("psych: the antipsychotic is kept in stock", map, r =>
            {
                const string check = "stock";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                ThingDef drug = RPPsychWingDefOf.RP_Antipsychotic;
                SecurityLevelDef level = s.level, medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.Project(true);
                List<SupplyOrder> pending0 = g.supply.pending.ToList();
                r.Add(() =>
                {
                    g.supply.pending.Clear();
                    g.supply.pending.AddRange(pending0);
                });
                g.ledger.funds += 5000;
                // (The prison's own stock is set aside for the check and put back after it: the events plan's real patients can have had the drug ordered, on the shuttle or on the shelf by now, as a
                // census run had.)
                g.supply.pending.RemoveAll(o => o.thing == drug);
                if (ShuttleSystem.Current is { } visit)
                {
                    List<SupplyOrder> cargo0 = visit.cargo.ToList();
                    visit.cargo.RemoveAll(o => o.thing == drug);
                    r.Add(() =>
                    {
                        visit.cargo.Clear();
                        visit.cargo.AddRange(cargo0);
                    });
                }
                foreach (Thing t in map.listerThings.ThingsOfDef(drug).Where(t => t.Spawned).ToList())
                {
                    IntVec3 at = t.Position;
                    t.DeSpawn();
                    r.Add(() =>
                    {
                        if (!t.Destroyed && !t.Spawned) GenSpawn.Spawn(t, at, map);
                    });
                }
                if (SupplyOrderCount(g, drug) > 0 || SupplySystem.CountOnMap(map, drug) > 0)
                {
                    PsychFail(check, $"the prison starts the check with some of the drug it could not set aside (nothing to measure: {SupplyOrderCount(g, drug)} on order, {SupplySystem.CountOnMap(map, drug)} on the map)");
                    return;
                }
                // No patients: nothing is ordered.
                Pawn p = s.Inmate("Zpk1", medium, s.plain, s.CellQ);
                PsychWing.Daily(map, g);
                if (SupplyOrderCount(g, drug) > 0) PsychFail(check, "the drug was ordered for a prison with no patients");
                // A patient and none on the map: ordered, once (the next midnight sees the order).
                p.Inmate().security = level;
                PsychWing.Daily(map, g);
                int ordered = SupplyOrderCount(g, drug);
                if (ordered <= 0) PsychFail(check, "the drug was not ordered for a patient with none on the map");
                PsychWing.Daily(map, g);
                if (SupplyOrderCount(g, drug) != ordered) PsychFail(check, $"the next midnight ordered more ({ordered} -> {SupplyOrderCount(g, drug)})");
                // Enough on the map: no order.
                g.supply.pending.Clear();
                Thing stack = s.Drug(s.At(3, 1), 20);
                PsychWing.Daily(map, g);
                if (SupplyOrderCount(g, drug) > 0) PsychFail(check, "the drug was ordered with a stock of 20 for one patient");
                stack.Destroy(DestroyMode.Vanish);
                p.Inmate().security = medium;
            });
        }

        private static int SupplyOrderCount(PrisonGame g, ThingDef def) => g.supply.PendingCount(def);

        // ---- the penitentiary ---------------------------------------------------------------------------------------------------------------

        /// <summary>The state penitentiary has a small psych wing: padded cells, each a room of its own, painted as the wing, and the level open from the start (called from the showcase plan's evaluation).</summary>
        private static void CheckPsychPenitentiary(Map map, List<string> problems)
        {
            void Fail(string what) => problems.Add("psych (penitentiary): " + what);
            PrisonMap pm = PrisonMap.Of(map);
            List<Building_Bed> beds = map.listerThings.ThingsOfDef(RPPsychWingDefOf.RP_PaddedBed).OfType<Building_Bed>().ToList();
            if (beds.Count < 2) Fail($"{beds.Count} padded beds (at least 2 expected)");
            foreach (Building_Bed bed in beds)
            {
                if (!bed.ForPrisoners) Fail($"the padded bed at {bed.Position} is not a prisoners' bed");
                if (pm.zones.ZoneAt(bed.Position) != RPPsychWingDefOf.RP_WingPsych) Fail($"the padded bed at {bed.Position} is not painted as the psych wing");
                if (!IntakeSystem.SingleCell(bed)) Fail($"the padded bed at {bed.Position} is not alone in its room");
                if (!PaddedCells.IsPadded(bed.GetRoom())) Fail($"the room of the padded bed at {bed.Position} is not a padded cell");
            }
            if (!Unlocks.IsUnlocked(RPPsychWingDefOf.RP_Psych) && PrisonGame.Instance.bureaucracy.IsDone(Unlocks.GateOf(RPPsychWingDefOf.RP_Psych))) Fail("the level is locked with its project done");
            int wingCells = pm.zones.CellsOf(RPPsychWingDefOf.RP_WingPsych).Count;
            if (wingCells == 0) Fail("no cell is painted as the psych wing");
            // The ward the cells open onto is heated at both ends (the corner heater had been put under a bed when the beds moved south).
            ThingDef heater = DefDatabase<ThingDef>.GetNamed("Heater");
            Building_Bed wardBed = beds.Count == 0 ? null : map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("HospitalBed")).OfType<Building_Bed>().Where(b => b.Position.DistanceTo(beds[0].Position) < 12f).OrderBy(b => b.Position.DistanceTo(beds[0].Position)).FirstOrDefault();
            if (wardBed?.GetRoom() is { } ward)
            {
                int heaters = map.listerThings.ThingsOfDef(heater).Count(h => h.GetRoom() == ward);
                if (heaters < 2) Fail($"the medical ward the padded cells open onto has {heaters} heater(s) (2 expected: the corner one is under a bed)");
            }
            else Fail("no hospital bed near the padded cells to find the ward by");
            if (!PrisonGame.Instance.bureaucracy.IsDone(Unlocks.GateOf(RPPsychWingDefOf.RP_Psych))) Fail("the penitentiary starts without the psychiatric wing project");
        }
    }
}
