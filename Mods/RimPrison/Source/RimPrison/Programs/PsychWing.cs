using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPPsychWingDefOf
    {
        public static SecurityLevelDef RP_Psych;
        public static PrisonZoneDef RP_WingPsych;
        public static ThingDef RP_PaddedBed, RP_Antipsychotic;
        public static HediffDef RP_Medicated;
        public static JobDef RP_MedicationRound;

        static RPPsychWingDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPPsychWingDefOf));
    }

    /// <summary>Makes a security level psychiatric care: the traits the psychologist's first assessment moves an inmate for (beside two breaks in ten days).</summary>
    public class PsychLevelExtension : DefModExtension
    {
        public List<TraitDef> traits = new List<TraitDef>();
    }

    /// <summary>
    /// A padded cell is a room indoors with a padded bed (<see cref="RPPsychWingDefOf.RP_PaddedBed"/>) that fits the psych level: painted as the wing and the only bed in its room (<see cref="InmateUtility.BedFitsLevel"/>).
    /// A padded bed anywhere else (a dormitory, a workshop, outdoors) pads nothing. A mental break there harms neither the inmate nor the things in it, when the one breaking is an inmate in the cell of their own bed.
    /// </summary>
    public static class PaddedCells
    {
        public static bool IsPadded(Building_Bed bed) => bed != null && bed.def == RPPsychWingDefOf.RP_PaddedBed;

        /// <summary>No padded bed on the map (the common case): nothing to look for.</summary>
        private static bool NoneOn(Map map) => map == null || RPPsychWingDefOf.RP_PaddedBed == null || map.listerThings.ThingsOfDef(RPPsychWingDefOf.RP_PaddedBed).Count == 0;

        /// <summary>The padded bed that makes this room a padded cell, or null: a room indoors, a padded bed in it that fits the psych level. (Walks the map's few padded beds, not the room's things.)</summary>
        public static Building_Bed CellBed(Room room)
        {
            if (room == null || NoneOn(room.Map) || room.PsychologicallyOutdoors) return null;
            List<Thing> beds = room.Map.listerThings.ThingsOfDef(RPPsychWingDefOf.RP_PaddedBed);
            for (int i = 0; i < beds.Count; i++)
                if (beds[i] is Building_Bed { Spawned: true } bed && bed.GetRoom() == room && InmateUtility.BedFitsLevel(bed, PsychWing.Level)) return bed;
            return null;
        }

        public static bool IsPadded(Room room) => CellBed(room) != null;

        /// <summary>The inmate is in the padded cell of their own bed (the room of the padded bed they sleep in).</summary>
        public static bool InOwnCell(Pawn p, out Room room)
        {
            room = null;
            if (p?.Spawned != true || p.Inmate() is not { activeFlag: true } || NoneOn(p.Map)) return false;
            room = p.GetRoom();
            return CellBed(room) is { } bed && bed.OwnersForReading.Contains(p);
        }

        /// <summary>Whether a cell is in the room or touches it (the cell's walls and its door).</summary>
        private static bool InOrBeside(Room room, IntVec3 cell, Map map)
        {
            if (cell.GetRoom(map) == room) return true;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = cell + GenAdj.CardinalDirections[i];
                if (n.InBounds(map) && n.GetRoom(map) == room) return true;
            }
            return false;
        }

        /// <summary>Whether a thing is in the room or touches it (not a person, but its walls and its door: a tantrum smashes up the room and what is adjacent to it, and the padding is on the walls).</summary>
        private static bool InOrBeside(Room room, Thing t)
        {
            foreach (IntVec3 c in t.OccupiedRect())
                if (InOrBeside(room, c, t.Map)) return true;
            return false;
        }

        /// <summary>
        /// Whether a blow of someone in a mental break does nothing: an inmate in the padded cell of their own bed, and what takes it is themself, or a thing (not a person) in the cell or on its edge. A guard or a doctor
        /// in the cell is still in danger from them: the padding is for the cell.
        /// </summary>
        public static bool Shields(Pawn breaker, Thing target)
        {
            if (breaker == null || target == null || !breaker.InMentalState || !target.Spawned || target.Map != breaker.Map) return false;
            if (target is Pawn && target != breaker) return false;
            return InOwnCell(breaker, out Room room) && (target == breaker || InOrBeside(room, target));
        }

        /// <summary>Whether a fire the breaker starts at this cell takes nothing: they are in their padded cell, and the cell is in it or on its edge.</summary>
        public static bool ShieldsCell(Pawn breaker, IntVec3 cell, Map map) =>
            breaker is { InMentalState: true } && map != null && breaker.Map == map && cell.InBounds(map) && InOwnCell(breaker, out Room room) && InOrBeside(room, cell, map);
    }

    /// <summary>
    /// Psychiatric care (the criminally insane, gap analysis 4 section 1.6). An inmate who has had two mental breaks in ten days, or who has a trait the level names (a pyromaniac, a bloodlust) when
    /// the psychologist first assesses them, is moved by the psychologist into a padded cell of the psych wing (<see cref="Review"/>, from the session in <see cref="PsychologyUtility.Complete"/>) once the
    /// psychiatric wing project is done and one is free. There the doctor makes medication rounds with an antipsychotic (<see cref="WorkGiver_MedicationRound"/>): a dose is a hediff that lasts a day and
    /// lowers the risk of a break. When a round is missed it runs out, and once the inmate has been without it for a few hours a break comes (<see cref="Hourly"/>), only while they are in their padded cell,
    /// where it harms nobody and nothing (<see cref="PaddedCells"/>). The regime has no workshop and no kitchen. Nobody goes there unless the project is done: without it nothing in the prison changes.
    /// </summary>
    public static class PsychWing
    {
        /// <summary>The breaks that make a case: this many inside this many days.</summary>
        public const int BreaksNeeded = 2, WindowDays = 10;
        /// <summary>A case that could not be moved (no padded cell free) is looked at again after this many days.</summary>
        public const int CheckDays = 2;
        /// <summary>A round is due once the cover is down to this much of a dose (a dose lasts a day).</summary>
        public const float RoundBelow = 0.5f;
        /// <summary>Hours without cover before the breaks start, and the chance of one in each hour after that (covered: a small one).</summary>
        public const int GraceHours = 6;
        public const float UncoveredChance = 0.08f, CoveredChance = 0.004f;
        /// <summary>Days of rounds the stock is kept at, and a few spare.</summary>
        public const int StockDays = 2, StockSpare = 4;

        public static SecurityLevelDef Level => RPPsychWingDefOf.RP_Psych;

        public static PsychLevelExtension Ext => Level?.GetModExtension<PsychLevelExtension>();

        public static bool IsIn(CompInmate c) => c?.security != null && c.security == Level;

        // ---- the breaks ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A mental break, for the count, as the mood gives one (vanilla's mood breaks, and the ones <see cref="StartBreak"/> starts): not the ones the story sets up (a hostage held). The mod's own tempers are counted where
        /// the warden's prison sees them, see <see cref="CountsAsTemper"/>.
        /// </summary>
        public static bool CountsAsBreak(MentalStateDef def, bool causedByMood) => def != null && def != RPHostageDefOf.RP_BeingHeld && causedByMood;

        /// <summary>
        /// A spontaneous temper of the mod (a vandalism, an assault on staff), counted when it is recorded as seen: an unwatched one is no fact the prison has, and a riot is a protest of many about their
        /// conditions, not a sign of a sick mind (nor is an assault forced by a restraint or one with a score behind it: those are started elsewhere and never recorded as these).
        /// </summary>
        public static bool CountsAsTemper(MisconductDef def) => def?.Worker is MisconductWorker_Vandalism or MisconductWorker_AssaultStaff;

        /// <summary>Hours within which a second break is the same incident (an assault that becomes a hostage-taking, a break that ends in the next).</summary>
        public const int SameIncidentHours = 3;

        public static void Notify_Break(CompInmate c)
        {
            int now = Find.TickManager.TicksGame;
            if (c.breakTicks.Count > 0 && now - c.breakTicks[c.breakTicks.Count - 1] < SameIncidentHours * GenDate.TicksPerHour) return;
            c.breakTicks.Add(now);
            c.lastBreakTick = now;
            c.breakTicks.RemoveAll(t => now - t > WindowDays * GenDate.TicksPerDay);
        }

        /// <summary>Breaks inside the window and since they last changed level (the ones before were acted on).</summary>
        public static int BreaksInWindow(CompInmate c)
        {
            int now = Find.TickManager.TicksGame, n = 0;
            for (int i = 0; i < c.breakTicks.Count; i++)
                if (now - c.breakTicks[i] <= WindowDays * GenDate.TicksPerDay && c.breakTicks[i] > c.lastReclassTick) n++;
            return n;
        }

        public static Trait FittingTrait(Pawn p)
        {
            if (p.story?.traits == null || Ext is not { } ext) return null;
            foreach (TraitDef t in ext.traits)
                if (p.story.traits.GetTrait(t) is { } trait) return trait;
            return null;
        }

        // ---- the assessment -----------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Why this inmate should be in psychiatric care, in the words for the chronicle, or null when there is no case: two breaks in the window, or a fitting trait that the first assessment found
        /// (<see cref="CompInmate.psychTraitCase"/>, kept until they change level, so a case the first assessment could not act on is not lost, and one who has been moved is not sent back for what they are).
        /// </summary>
        public static string CaseFor(Pawn p, CompInmate c)
        {
            int breaks = BreaksInWindow(c);
            if (breaks >= BreaksNeeded) return "RP_Psych_CaseBreaks".Translate(breaks, WindowDays);
            if (c.psychTraitCase && FittingTrait(p) is { } trait) return "RP_Psych_CaseTrait".Translate(trait.Label);
            return null;
        }

        /// <summary>Why they cannot be moved to psychiatric care at all, whoever they are or whatever the prison has built (words for the screen), or null.</summary>
        public static string CannotMoveWho(Pawn p, CompInmate c)
        {
            SecurityLevelDef level = Level;
            if (level == null || c == null) return "RP_PsychNotUnlocked".Translate();
            if (c.security == level) return "RP_PsychAlready".Translate(p.LabelShort);
            // (Not one in protective custody, a protected witness among them: the wing has the general population in its canteen and yard, and the way out leads back to it.)
            if (DeathRowSystem.Ext(c) != null || c.security is { escortOnly: true } || Trials.IsProtected(c) || ShuttleSystem.OnTheWayOut(p)) return "RP_PsychNotThem".Translate(p.LabelShort);
            return null;
        }

        /// <summary>Why they cannot be moved to psychiatric care now (words for the screen), or null when they can.</summary>
        public static string CannotMove(Pawn p, CompInmate c)
        {
            SecurityLevelDef level = Level;
            if (level == null || c == null || !Unlocks.IsUnlocked(level)) return "RP_PsychNotUnlocked".Translate();
            if (CannotMoveWho(p, c) is { } who) return who;
            if (!InmateUtility.HasRoomIn(p, level)) return InmateUtility.NoRoomText(p, level, "RP_PsychNoRoom");
            return null;
        }

        /// <summary>Whether the psychologist should see them for their case now (it makes <see cref="PsychologyUtility.WantsSession"/> true): a case (two breaks in the window, or the trait), a cell to move them to, not looked at in the last two days.</summary>
        public static bool ReviewDue(CompInmate c)
        {
            if (Level == null || c == null || !c.activeFlag || IsIn(c) || c.Pawn is not { } p) return false;
            if (Find.TickManager.TicksGame - c.psychCheckTick < CheckDays * GenDate.TicksPerDay || CaseFor(p, c) == null) return false;
            return CannotMove(p, c) == null;
        }

        /// <summary>
        /// The end of a session with the psychologist: if the inmate has a case (two breaks in ten days, or a fitting trait the first assessment found) they are moved to a padded cell, with the reason in
        /// the chronicle line, and the cell they were in goes to someone else. Without a cell they stay and the warden is told, once in two days (the next look is two days on); without the project, or for one
        /// who cannot be moved at all, nothing is said (the case is kept: it is acted on when the project is done).
        /// </summary>
        public static bool Review(Pawn p, CompInmate c, bool firstAssessment)
        {
            if (Level == null || c == null || IsIn(c)) return false;
            // (The trait is read once, at the first assessment, and remembered: a conduct step-down is not sent back for what they are.)
            if (firstAssessment) c.psychTraitCase = FittingTrait(p) != null;
            string why = CaseFor(p, c);
            if (why == null || !Unlocks.IsUnlocked(Level) || CannotMoveWho(p, c) != null) return false;
            int now = Find.TickManager.TicksGame;
            bool due = now - c.psychCheckTick >= CheckDays * GenDate.TicksPerDay;
            if (CannotMove(p, c) is { } cannot)
            {
                if (due)
                {
                    c.psychCheckTick = now;
                    Messages.Message("RP_PsychCaseNotMoved".Translate(p.LabelShort, cannot), p, MessageTypeDefOf.CautionInput, historical: false);
                }
                return false;
            }
            c.psychCheckTick = now;
            SecurityLevelDef from = c.security;
            if (!InmateUtility.ChangeSecurity(p, c, Level, why: "RP_Psych_Why".Translate(why))) return false;
            c.psychFrom = from;
            return c.security == Level;
        }

        // ---- the level --------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Called when an inmate changes level: the cover and the count of hours start again, and a trait case is closed (they have been moved: not sent back for what they are).</summary>
        public static void Sync(CompInmate c)
        {
            c.unmedicatedHours = 0;
            c.psychTraitCase = false;
            c.psychFrom = null;
        }

        /// <summary>
        /// Where a patient goes when conduct moves them out (<see cref="Reclassification"/>): the level they came from, if they can go back to it, else the level's own step (medium). None while a break is
        /// too recent: the clean days are the days without one.
        /// </summary>
        public static SecurityLevelDef StepOutLevel(CompInmate c)
        {
            if (!IsIn(c)) return null;
            SecurityLevelDef from = c.psychFrom;
            return from != null && from != Level && !from.referralOnly && !from.escortOnly && Unlocks.IsUnlocked(from) ? from : Level.promoteTo;
        }

        /// <summary>A patient who broke inside the clean days that move them out: they stay.</summary>
        public static bool BrokeRecently(CompInmate c) => Find.TickManager.TicksGame - c.lastBreakTick < Level.promoteAfterCleanDays * GenDate.TicksPerDay;

        // ---- medication ---------------------------------------------------------------------------------------------------------------------------------

        public static Hediff Cover(Pawn p) => p.health?.hediffSet?.GetFirstHediffOfDef(RPPsychWingDefOf.RP_Medicated);

        public static bool Covered(Pawn p) => Cover(p) is { Severity: > 0f };

        /// <summary>Someone in psychiatric care whose cover is running out (or is gone): the doctor's round. Not one in a break or on the way out (one downed or in a bed gets their dose there).</summary>
        public static bool Candidate(Pawn p) =>
            p.Spawned && p.Inmate() is { activeFlag: true } c && IsIn(c) && !p.InMentalState && !ShuttleSystem.OnTheWayOut(p) && (Cover(p) is not { } h || h.Severity <= RoundBelow);

        public static void Give(Pawn doctor, Pawn inmate)
        {
            CompInmate c = inmate.Inmate();
            Hediff cover = Cover(inmate) ?? inmate.health.AddHediff(RPPsychWingDefOf.RP_Medicated);
            cover.Severity = 1f;
            if (c != null) (c.lastDoseTick, c.unmedicatedHours) = (Find.TickManager.TicksGame, 0);
            doctor?.skills?.Learn(SkillDefOf.Medicine, 120f);
        }

        /// <summary>The chance of a break in an hour: none in the first hours without cover, then the uncovered chance; covered, a small one.</summary>
        public static float BreakChancePerHour(Pawn p, CompInmate c)
        {
            if (Covered(p)) return CoveredChance;
            return c.unmedicatedHours > GraceHours ? UncoveredChance : 0f;
        }

        /// <summary>
        /// Hourly, per inmate (<see cref="CompInmate"/>'s update): a patient's hours without cover, and a break when they run out of patience. The break rolls only while they are in their padded cell (the hours
        /// keep counting outside it, and the roll is made when they are back: the regime has them in it sixteen hours of the day), so a break never comes among the others in the canteen, the showers or the yard.
        /// </summary>
        public static void Hourly(Pawn p, CompInmate c)
        {
            if (!IsIn(c) || !c.activeFlag) return;
            if (Covered(p)) c.unmedicatedHours = 0;
            else c.unmedicatedHours++;
            if (p.InMentalState || p.Downed || !p.Awake() || HostageCrisis.IsHeld(p) || !PaddedCells.InOwnCell(p, out _)) return;
            float chance = BreakChancePerHour(p, c);
            if (chance > 0f && Rand.Chance(chance)) StartBreak(p);
        }

        /// <summary>
        /// A break as the mood would give one, started now, of the kinds that harm no one else: not an aggressive one (a berserk or a murderous rage is vanilla's own mood breaker's, which does not wait for a missed
        /// round). The reason names whether the cover was on. False when none can.
        /// </summary>
        public static bool StartBreak(Pawn p)
        {
            if (!DefDatabase<MentalBreakDef>.AllDefsListForReading.Where(d => d.mentalState != null && !d.mentalState.IsAggro && d.Worker.BreakCanOccur(p))
                    .TryRandomElementByWeight(d => d.Worker.CommonalityFor(p, moodCaused: true), out MentalBreakDef pick)) return false;
            return pick.Worker.TryStart(p, BreakReason(p), causedByMood: true);
        }

        /// <summary>What the letter says of why: the medication ran out, or it was on and was not enough.</summary>
        public static string BreakReason(Pawn p) => (Covered(p) ? "RP_PsychBreakReasonCovered" : "RP_PsychBreakReason").Translate();

        /// <summary>Patients on the map (inmates in psychiatric care).</summary>
        public static int Patients(Map map)
        {
            int n = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && IsIn(c)) n++;
            return n;
        }

        /// <summary>Midnight: the antipsychotic is kept in stock for the rounds (ordered from the supply company when it runs low), and a prison with patients and none, or with no doctor to make the rounds, is told so.</summary>
        public static void Daily(Map map, PrisonGame game)
        {
            if (Level == null || RPPsychWingDefOf.RP_Antipsychotic == null) return;
            int patients = Patients(map);
            if (patients == 0) return;
            ThingDef drug = RPPsychWingDefOf.RP_Antipsychotic;
            int have = SupplySystem.CountOnMap(map, drug);
            int want = StockSpare + StockDays * 2 * patients;
            if (have + game.supply.PendingCount(drug) < want)
                game.supply.TryOrder(drug, Mathf.CeilToInt((want - have - game.supply.PendingCount(drug)) / 10f) * 10, RPDefOf.RP_Supplies, silent: true);
            if (have == 0) Messages.Message("RP_PsychNoDrug".Translate(patients), MessageTypeDefOf.CautionInput, historical: false);
            if (!AnyMedic(map)) Messages.Message("RP_PsychNoMedic".Translate(patients), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <summary>A doctor who can make the rounds: on the staff, on site, not down, not on leave (burnout rest or a course).</summary>
        public static bool AnyMedic(Map map)
        {
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
                if (!s.Downed && !s.Dead && WorkGiver_MedicationRound.IsRoundDoctor(s) && StaffCareers.Available(s.GetComp<CompStaff>())) return true;
            return false;
        }
    }

    // ---- the doctor's round ----------------------------------------------------------------------------------------------------------------------------

    public class WorkGiver_MedicationRound : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Medication(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !forced && (!IsRoundDoctor(pawn) || InmateCandidates.Shared.Medication(pawn.Map).Count == 0);

        /// <summary>The rounds are the doctors': a guard has the Doctor work too (to tend and rescue the hurt) but is wanted at the posts, and a long walk for a dose would take them off one (<see cref="ShiftSystem.IsMedic"/>).</summary>
        public static bool IsRoundDoctor(Pawn p) => p.GetComp<CompStaff>() is { } sc && ShiftSystem.IsMedic(sc);

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the doctor.</summary>
        public static bool Candidate(Pawn inmate) => PsychWing.Candidate(inmate);

        /// <summary>How many doctors may have part of one stack of doses reserved: a reservation names how many pawns may share the thing, and by default it is one, whatever part of the stack the first has.</summary>
        public const int DoseReservers = 10;

        private static Pawn doseDoctor;
        private static int doseTick = -1;
        private static Thing doseFound;

        /// <summary>
        /// The nearest dose the doctor can take (one of it: another doctor may have part of the stack reserved). Asked for each patient the doctor is offered and again for the job, so the answer is kept for the tick.
        /// </summary>
        public static Thing DoseFor(Pawn doctor)
        {
            int now = Find.TickManager.TicksGame;
            if (doseDoctor == doctor && doseTick == now && doseFound is { Spawned: true } && doctor.CanReserve(doseFound, DoseReservers, 1)) return doseFound;
            doseFound = GenClosest.ClosestThingReachable(doctor.Position, doctor.Map, ThingRequest.ForDef(RPPsychWingDefOf.RP_Antipsychotic), PathEndMode.ClosestTouch, TraverseParms.For(doctor), 9999f,
                t => !t.IsForbidden(doctor) && doctor.CanReserve(t, DoseReservers, 1));
            (doseDoctor, doseTick) = (doctor, now);
            return doseFound;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            (forced || IsRoundDoctor(pawn)) && t is Pawn inmate && Candidate(inmate) && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Some, 1, -1, null, forced) && DoseFor(pawn) != null;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Job job = JobMaker.MakeJob(RPPsychWingDefOf.RP_MedicationRound, t, DoseFor(pawn));
            job.count = 1;
            return job;
        }
    }

    public class JobDriver_MedicationRound : JobDriver
    {
        public const int GiveTicks = 240;

        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetB, job, WorkGiver_MedicationRound.DoseReservers, 1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !PsychWing.Candidate(Inmate));
            // (A dose picked up and not given goes back on the ground.)
            AddFinishAction(condition =>
            {
                if (pawn.carryTracker?.CarriedThing != null) pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            });
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, putRemainderInQueue: false, subtractNumTakenFromJobCount: true);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil give = Toils_General.Wait(GiveTicks);
            give.handlingFacing = true;
            give.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            give.WithProgressBarToilDelay(TargetIndex.A);
            give.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, 3f));
            yield return give;
            Toil done = ToilMaker.MakeToil("MedicationGiven");
            done.initAction = () =>
            {
                pawn.carryTracker?.innerContainer.ClearAndDestroyContents();
                PsychWing.Give(pawn, Inmate);
            };
            yield return done;
        }
    }

    // ---- what vanilla does, changed -----------------------------------------------------------------------------------------------------------------

    /// <summary>Every mental break of an inmate is counted (see <see cref="PsychWing.CountsAsBreak"/>).</summary>
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class Patch_MentalStateHandler_TryStartMentalState_Psych
    {
        private static readonly AccessTools.FieldRef<MentalStateHandler, Pawn> PawnOf = AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");

        public static void Postfix(MentalStateHandler __instance, bool __result, MentalStateDef stateDef, bool causedByMood)
        {
            if (__result && PsychWing.CountsAsBreak(stateDef, causedByMood) && PawnOf(__instance).Inmate() is { activeFlag: true } c) PsychWing.Notify_Break(c);
        }
    }

    /// <summary>A break in a padded cell harms neither the one in it nor the things in it: their blow does nothing.</summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    public static class Patch_Thing_TakeDamage_Padded
    {
        public static bool Prefix(Thing __instance, DamageInfo dinfo, ref DamageWorker.DamageResult __result)
        {
            if (dinfo.Instigator is not Pawn { InMentalState: true } breaker || !PaddedCells.Shields(breaker, __instance)) return true;
            __result = new DamageWorker.DamageResult();
            return false;
        }
    }

    /// <summary>...nor does a fire they start (a fire-starting spree in a padded cell lights nothing).</summary>
    [HarmonyPatch(typeof(FireUtility), nameof(FireUtility.TryStartFireIn))]
    public static class Patch_FireUtility_TryStartFireIn_Padded
    {
        public static bool Prefix(IntVec3 c, Map map, Thing instigator, ref bool __result)
        {
            if (instigator is not Pawn { InMentalState: true } breaker || !PaddedCells.ShieldsCell(breaker, c, map)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(FireUtility), nameof(FireUtility.TryAttachFire))]
    public static class Patch_FireUtility_TryAttachFire_Padded
    {
        public static bool Prefix(Thing t, Thing instigator) => !(instigator is Pawn { InMentalState: true } breaker && PaddedCells.Shields(breaker, t));
    }
}
