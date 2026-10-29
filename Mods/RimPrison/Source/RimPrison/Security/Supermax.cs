using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPSupermaxDefOf
    {
        public static SecurityLevelDef RP_Supermax;
        public static PrisonZoneDef RP_WingSupermax;
        public static PrisonZoneDef RP_SupermaxYard;
        public static RegimeActivityDef RP_YardAlone;
        public static JobDef RP_EscortInmate;
        public static WorkGiverDef RP_EscortInmate_WorkGiver;
        public static HediffDef RP_SupermaxStay;

        static RPSupermaxDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPSupermaxDefOf));
    }

    /// <summary>Why an inmate was moved to supermax (the chronicle says it; the keys are <c>RP_Supermax_Why_*</c>).</summary>
    public enum SupermaxReason : byte
    {
        Warden,
        StaffKiller,
        Convicted,
        Ringleader,
        StepDown,
        /// <summary>Only the way back down: a staff killer the court acquitted goes back to where they were.</summary>
        Acquitted,
    }

    /// <summary>
    /// Supermax, where the worst inmates go: staff killers, convicted murderers, hostage ringleaders and whoever the warden sends. A single cell (one bed in the room), meals brought to it, and out of it
    /// only with a guard (<see cref="EscortWalk"/>) for a shower with nobody else in it and an hour in a yard of their own (<see cref="SecurityLevelDef.escortOnly"/>, <see cref="SecurityLevelDef.yardAlone"/>):
    /// a guard fetches one who is out of the cell with nobody (<see cref="EscortKind.Home"/>), and the doors open to them only on the path of the guard's walk. Several hours of the regime are set aside
    /// for the yard and for the shower, so that a unit of a few cells has them all (a message at midnight says who missed the yard). No parole from here. A long stay wears them down (<see cref="Sync"/>),
    /// and after ten clean days the warden is asked whether to step them down (<see cref="Daily"/>). Nobody goes there unless the project is done and a cell of the wing is free; without that, every
    /// caller does what it did before. A fire is the one thing that opens a locked cell (<see cref="PrisonMap"/>'s door verdict).
    /// </summary>
    public static class SupermaxSystem
    {
        public const int StepDownCleanDays = 10, AskAgainDays = 5, StepDownTimeoutDays = 2;
        /// <summary>The letter that puts a clean supermax inmate to the warden for the step-down.</summary>
        public const string StepDownDecisionDef = "RP_Decision_SupermaxStepDown";
        /// <summary>The weeks before the long stay starts to tell (the hediff is added after the first).</summary>
        public const float StayStartsWeeks = 1f;
        /// <summary>A shower is asked for below this hygiene (as the showers slot's own worker does), and left once they are this clean.</summary>
        public const float ShowerBelow = 0.8f, CleanEnough = 0.9f;
        /// <summary>How long they stay in the yard once they are in it.</summary>
        public const int YardStayTicks = 2000;
        /// <summary>A walk whose inmate has not got to where they are going by then (a patient who stays in bed, a door that will not open) is given up, and not tried again for half an hour.</summary>
        public const int NoShowTicks = 1800;

        public static SecurityLevelDef Level => RPSupermaxDefOf.RP_Supermax;

        public static bool IsIn(CompInmate c) => c?.security != null && c.security == Level;

        // ---- who goes there -----------------------------------------------------------------------------------------------------------------------------

        /// <summary>Why this inmate cannot be moved to supermax now (the words for the screen), or null when they can.</summary>
        public static string CannotSend(Pawn p, CompInmate c)
        {
            SecurityLevelDef level = Level;
            if (level == null || c == null) return "RP_SupermaxNotUnlocked".Translate();
            if (!Unlocks.IsUnlocked(level)) return "RP_SupermaxNotUnlocked".Translate();
            if (c.security == level) return "RP_SupermaxAlready".Translate(p.LabelShort);
            if (DeathRowSystem.Ext(c) != null) return "RP_SupermaxDeathRow".Translate(p.LabelShort);
            if (!InmateUtility.HasRoomIn(p, level)) return InmateUtility.NoRoomText(p, level, "RP_SupermaxNoRoom");
            return null;
        }

        /// <summary>Moves the inmate to a free supermax cell, when the project is done and there is one (false: nothing changed, and the caller does what it did before).</summary>
        public static bool TrySend(Pawn p, CompInmate c, SupermaxReason why)
        {
            if (p == null || c == null || CannotSend(p, c) != null) return false;
            SecurityLevelDef before = c.security;
            InmateUtility.ChangeSecurity(p, c, Level, why: ("RP_Supermax_Why_" + why).Translate());
            if (c.security != Level) return false;
            (c.supermaxWhy, c.levelBeforeSupermax) = ((int)why, before);
            // (The wing they were taken from talks of it: the rumour mill.)
            Rumours.Notify_Source(p.MapHeld, before, RumourKind.Supermax, p.LabelShort, p);
            return true;
        }

        /// <summary>
        /// The state's payment for the day for this inmate. Supermax pays its rate for those sent there by the rules (a staff killer, a conviction, a ringleader); the warden's own sends are paid at the maximum
        /// rate (an order is not a reason: moving a shoplifter would pay six times what they are worth).
        /// </summary>
        public static int DailyPayment(CompInmate c)
        {
            if (c.security == Level && c.supermaxWhy == (int)SupermaxReason.Warden) return RPSecurityDefOf.RP_Maximum.dailyPayment;
            return c.security.dailyPayment;
        }

        /// <summary>
        /// The court acquitted a staff killer who was sent here at the charge: they go back to the level they came from (a level up, maximum, when that cell is gone), and the stay did not count against
        /// them. Anyone else stays where they are (the verdict says nothing of them).
        /// </summary>
        public static void StepDownAcquitted(Pawn p, CompInmate c)
        {
            if (!IsIn(c) || c.supermaxWhy != (int)SupermaxReason.StaffKiller) return;
            SecurityLevelDef back = c.levelBeforeSupermax;
            if (back == null || back == Level || !Unlocks.IsUnlocked(back) || !InmateUtility.HasRoomIn(p, back)) back = RPSecurityDefOf.RP_Maximum;
            if (back == null || !Unlocks.IsUnlocked(back) || !InmateUtility.HasRoomIn(p, back))
            {
                InmateUtility.TellIfWingKeepsThemOut(p, back ?? Level);
                return;
            }
            InmateUtility.ChangeSecurity(p, c, back, why: ("RP_Supermax_Why_" + SupermaxReason.Acquitted).Translate());
        }

        // ---- the long stay ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Weeks since they came to this level (the level changes and the clock restarts, see <see cref="CompInmate.lastReclassTick"/>).</summary>
        public static float WeeksIn(CompInmate c) => (Find.TickManager.TicksGame - c.lastReclassTick) / (float)(7 * GenDate.TicksPerDay);

        /// <summary>The long stay's hediff follows the weeks served: added after the first, its severity the weeks, gone the moment they leave the level or the prison (released, paroled, transferred: they do not walk out with it).</summary>
        public static void Sync(Pawn p, CompInmate c)
        {
            if (p?.health?.hediffSet == null) return;
            HediffDef def = RPSupermaxDefOf.RP_SupermaxStay;
            Hediff stay = p.health.hediffSet.GetFirstHediffOfDef(def);
            if (!IsIn(c))
            {
                (c.supermaxWhy, c.levelBeforeSupermax) = (-1, null);
                if (stay != null) p.health.RemoveHediff(stay);
                return;
            }
            if (!c.activeFlag)
            {
                if (stay != null) p.health.RemoveHediff(stay);
                return;
            }
            float weeks = WeeksIn(c);
            if (weeks < StayStartsWeeks)
            {
                if (stay != null) p.health.RemoveHediff(stay);
                return;
            }
            stay ??= p.health.AddHediff(def);
            stay.Severity = Mathf.Min(weeks, def.maxSeverity);
        }

        /// <summary>Hourly, per inmate (<see cref="CompInmate"/>'s update): the long stay.</summary>
        public static void Hourly(Pawn p, CompInmate c)
        {
            if (IsIn(c) || p.health?.hediffSet.HasHediff(RPSupermaxDefOf.RP_SupermaxStay) == true) Sync(p, c);
        }

        // ---- the step-down ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The tick of the last misconduct the staff saw, or a long time ago when there was none.</summary>
        public static int LastSeenMisconduct(CompInmate c)
        {
            int last = int.MinValue / 2;
            for (int i = 0; i < c.records.Count; i++)
                if (c.records[i].seen && !c.records[i].def.mark && c.records[i].tick > last) last = c.records[i].tick;
            return last;
        }

        /// <summary>Days since they came to supermax or since the last misconduct the staff saw, whichever is later.</summary>
        public static int CleanDays(CompInmate c) => (Find.TickManager.TicksGame - Mathf.Max(c.lastReclassTick, LastSeenMisconduct(c))) / GenDate.TicksPerDay;

        /// <summary>Midnight: whoever has been clean for ten days is put to the warden (a letter, once in five days at most).</summary>
        public static void Daily(Map map, PrisonGame game)
        {
            if (Level == null) return;
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c || !IsIn(c) || c.forcedActivity != null || Trials.OnRemand(game, p)) continue;
                if (CleanDays(c) < StepDownCleanDays || now - c.supermaxAskTick < AskAgainDays * GenDate.TicksPerDay) continue;
                AskStepDown(p, c, map);
            }
            TellMissedYard(map, now);
        }

        /// <summary>Midnight: who has not had their hour in the yard for a day (too few yard patches or hours for the cells of the wing), once, by name.</summary>
        private static void TellMissedYard(Map map, int now)
        {
            List<Pawn> missed = MissedYard(map, now);
            if (missed.Count > 0) Messages.Message("RP_SupermaxMissedYard".Translate(missed.Count, string.Join(", ", missed.Select(m => m.LabelShort))), new LookTargets(missed), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <summary>The inmates of a level with a yard of its own who have had none in the last day (a new arrival has had no day yet; a slot is an hour).</summary>
        public static List<Pawn> MissedYard(Map map, int now)
        {
            var missed = new List<Pawn>();
            int slot = SlotOf(map);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true, security: { escortOnly: true, yardAlone: true } } c || c.forcedActivity != null) continue;
                if (now - c.lastReclassTick < GenDate.TicksPerDay || slot - c.yardSlot <= 24) continue;
                missed.Add(p);
            }
            return missed;
        }

        public static void AskStepDown(Pawn p, CompInmate c, Map map)
        {
            c.supermaxAskTick = Find.TickManager.TicksGame;
            int clean = CleanDays(c);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(StepDownDecisionDef, "RP_SupermaxStepDownLabel".Translate(p.LabelShort),
                "RP_SupermaxStepDownText".Translate(p.LabelShort, clean, Mathf.FloorToInt(WeeksIn(c)), RPSecurityDefOf.RP_Maximum.label), map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(StepDownTimeoutDays * GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            WardenAsks.Raise(p, WardenAskReason.StepDown);
        }

        // ---- the yard -----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The yard zones that only this level uses.</summary>
        public static IEnumerable<PrisonZoneDef> YardZones(SecurityLevelDef level)
        {
            List<PrisonZoneDef> zones = DefDatabase<PrisonZoneDef>.AllDefsListForReading;
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].yardFor == level) yield return zones[i];
        }

        /// <summary>Is anyone else in this patch of a yard (an inmate standing in it, or one a guard is walking to it)?</summary>
        public static bool PatchTaken(PrisonMap pm, int patch, Pawn except)
        {
            IReadOnlyList<Pawn> prisoners = pm.map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn o = prisoners[i];
                if (o == except) continue;
                if (pm.zones.PatchAt(o.Position) == patch) return true;
                if (o.Inmate()?.leg is { kind: EscortKind.Yard } leg && pm.zones.PatchAt(leg.target) == patch) return true;
            }
            return false;
        }

        private static readonly HashSet<int> triedPatches = new HashSet<int>();

        /// <summary>A standable cell in a patch of the level's own yard that nobody else is in, which <paramref name="reachBy"/> can walk to (invalid when every patch is taken or there is none).</summary>
        public static IntVec3 FreeYardCell(Pawn inmate, SecurityLevelDef level, PrisonMap pm, Pawn reachBy)
        {
            Map map = pm.map;
            foreach (PrisonZoneDef zone in YardZones(level))
            {
                List<IntVec3> cells = pm.zones.CellsOf(zone);
                if (cells.Count == 0) continue;
                triedPatches.Clear();
                foreach (IntVec3 start in cells)
                {
                    int patch = pm.zones.PatchAt(start);
                    if (patch < 0 || !triedPatches.Add(patch) || PatchTaken(pm, patch, inmate)) continue;
                    IntVec3 pick = IntVec3.Invalid;
                    int seen = 0;
                    foreach (IntVec3 c in cells)
                    {
                        if (pm.zones.PatchAt(c) != patch || !c.Standable(map) || c.GetFirstBuilding(map) != null) continue;
                        // (A random one of the patch's cells: reservoir sampling, no list.)
                        if (Rand.Range(0, ++seen) == 0) pick = c;
                    }
                    if (pick.IsValid && reachBy.CanReach(pick, PathEndMode.OnCell, Danger.Deadly)) return pick;
                }
            }
            return IntVec3.Invalid;
        }

        // ---- the shower ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The shower an escorted inmate is walked to: a usable one the guard can reach, in a room with nobody else in it and no other walk going to it, in the level's own wing (the showers painted with it)
        /// before the showers of the prison (the blocks of them not kept for other levels, see <see cref="ZoneGrid.SetKept"/>), the nearest to the inmate. A room that is not the wing's is shared with whoever else comes (see <see cref="OtherInmateIn"/>: the stay ends when one does).
        /// </summary>
        public static CompFixture ShowerFor(Pawn inmate, SecurityLevelDef level, PrisonMap pm, Pawn reachBy)
        {
            CompFixture best = null;
            float bestScore = float.MaxValue;
            foreach (CompFixture f in pm.Fixtures)
            {
                if (f.Props.need != RPDefOf.RP_Hygiene || !f.Usable) continue;
                PrisonZoneDef zone = pm.zones.ZoneAt(f.parent.Position);
                bool own = zone != null && zone.security == level;
                if (!own && zone != RPDefOf.RP_Shower) continue;
                // (A block of the prison's showers kept for other levels is not theirs; the wing's own are, and the yard rule below is left as it is.)
                if (!own && !pm.zones.Allows(f.UseCell, level)) continue;
                // (The wing's own, whatever the distance, before any other.)
                float score = (f.parent.Position - inmate.Position).LengthHorizontalSquared + (own ? 0f : 1e7f);
                if (score >= bestScore) continue;
                if (!inmate.CanReserve(f.parent) || !reachBy.CanReach(f.UseCell, PathEndMode.OnCell, Danger.Deadly) || SomeoneElseIn(f.parent.GetRoom(), inmate, pm)) continue;
                best = f;
                bestScore = score;
            }
            return best;
        }

        private static bool SomeoneElseIn(Room room, Pawn inmate, PrisonMap pm)
        {
            if (room == null) return true;
            IReadOnlyList<Pawn> prisoners = pm.map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn o = prisoners[i];
                if (o == inmate) continue;
                if (o.GetRoom() == room) return true;
                if (o.Inmate()?.leg is { kind: EscortKind.Shower } leg && leg.target.GetRoom(pm.map) == room) return true;
            }
            return false;
        }

        /// <summary>Is another inmate in the room now (not one who is only on the way)?</summary>
        public static bool OtherInmateIn(Room room, Pawn inmate, PrisonMap pm)
        {
            if (room == null || pm == null) return false;
            IReadOnlyList<Pawn> prisoners = pm.map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
                if (prisoners[i] != inmate && prisoners[i].GetRoom() == room) return true;
            return false;
        }

        // ---- the walks the guards are asked for ---------------------------------------------------------------------------------------------------------

        /// <summary>The hour's slot, for "once in this slot" (the regime's own hour boundaries).</summary>
        public static int SlotOf(Map map) => (GenLocalDate.Year(map) * 60 + GenLocalDate.DayOfYear(map)) * 24 + GenLocalDate.HourOfDay(map);

        /// <summary>
        /// What this inmate is due for now: out of the cell with nobody, a walk back to it (whatever the slot); else the shower or the yard their regime has for this hour, from the cell (none: in the cell).
        /// </summary>
        public static EscortKind? DueFor(Pawn p, CompInmate c, PrisonGame game)
        {
            if (c.security is not { escortOnly: true } || c.leg != null || c.forcedActivity != null || p.Downed || p.InMentalState || !p.Awake() || p.Map == null || Find.TickManager.TicksGame < c.escortRetryTick) return null;
            // (Out of the cell in an evacuation: the evacuation's guards have them, and the walk back is the all-clear's.)
            if (EvacuationSystem.Involves(p)) return null;
            if (EscortWalk.Stranded(p, c)) return EscortKind.Home;
            if (game.LockedDown(c)) return null;
            // (A patient stays in bed, whatever the slot: the walk would only hold a guard at the far end.)
            if (HealthAIUtility.ShouldSeekMedicalRest(p)) return null;
            if (p.GetRoom() != InmateUtility.CellOf(p)) return null;
            RegimeActivityDef act = game.ActivityAt(c, GenLocalDate.HourOfDay(p.Map));
            if (act == RPDefOf.RP_ShowerTime) return p.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { CurLevel: < ShowerBelow } ? EscortKind.Shower : null;
            if (act == RPSupermaxDefOf.RP_YardAlone && c.security.yardAlone && c.yardSlot != SlotOf(p.Map)) return EscortKind.Yard;
            return null;
        }

        /// <summary>The inmates a guard could be sent to walk now (see <see cref="WorkGiver_EscortInmate"/>): in their cells and due for the shower or the yard, or out of them with nobody (<see cref="TryPlan"/> says whether there is anywhere to take them, and for the yard whose turn it is).</summary>
        public static bool Candidate(Pawn p)
        {
            if (p.Inmate() is not { activeFlag: true } c || c.security is not { escortOnly: true } || PrisonGame.Instance is not { } game) return false;
            return DueFor(p, c, game) != null;
        }

        /// <summary>A supermax inmate who is out of the cell with nobody and due a walk back to it (what the care reserve's queue calls a return).</summary>
        public static bool ReturnDue(Pawn p) => p.Inmate() is { activeFlag: true } c && c.security is { escortOnly: true } && PrisonGame.Instance is { } game && DueFor(p, c, game) == EscortKind.Home;

        /// <summary>Is this job the walk that fetches an inmate who is out of the cell with nobody (<see cref="EscortKind.Home"/>)?</summary>
        public static bool IsFetch(Job job) => job.def == RPSupermaxDefOf.RP_EscortInmate && (EscortKind)job.count == EscortKind.Home;

        /// <summary>The walk that fetches a supermax inmate who is out of the cell with nobody, for a guard who takes no other work (on strike): none when there is no such inmate, or the guard's warden work is off.</summary>
        public static Job FetchJobFor(Pawn guard)
        {
            WorkGiverDef giverDef = RPSupermaxDefOf.RP_EscortInmate_WorkGiver;
            if (guard.Map == null || !GuardUtility.IsGuard(guard) || guard.WorkTypeIsDisabled(WorkTypeDefOf.Warden) || guard.workSettings?.WorkIsActive(WorkTypeDefOf.Warden) != true
                || giverDef.Worker is not WorkGiver_EscortInmate fetchGiver || fetchGiver.MissingRequiredCapacity(guard) != null) return null;
            List<Thing> candidates = InmateCandidates.Shared.Escort(guard.Map);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] is not Pawn inmate || !fetchGiver.HasJobOnThing(guard, inmate) || fetchGiver.JobOnThing(guard, inmate) is not { } job || !IsFetch(job)) continue;
                job.workGiverDef = giverDef;
                return job;
            }
            return null;
        }

        /// <summary>Where a walk would go (the inmate's target and the guard's post), or false when there is nowhere to go now.</summary>
        public static bool TryPlan(Pawn guard, Pawn inmate, out EscortKind kind, out IntVec3 target, out IntVec3 post)
        {
            (kind, target, post) = (EscortKind.Yard, IntVec3.Invalid, IntVec3.Invalid);
            if (inmate.Inmate() is not { } c || PrisonGame.Instance is not { } game || PrisonMap.Of(inmate.Map) is not { } pm || DueFor(inmate, c, game) is not { } due) return false;
            kind = due;
            // (A guard on strike takes no new work but the walk back to the cell, see Patch_JobGiver_Work_Strike: not the shower, not the yard.)
            if (due != EscortKind.Home && IncidentWorker_StaffStrike.Striking(guard)) return false;
            if (due == EscortKind.Home)
            {
                target = post = EscortWalk.HomeCell(inmate);
                return target.IsValid && guard.CanReach(target, PathEndMode.OnCell, Danger.Deadly);
            }
            if (due == EscortKind.Shower)
            {
                CompFixture shower = ShowerFor(inmate, c.security, pm, guard);
                if (shower == null) return false;
                target = shower.UseCell;
            }
            else
            {
                target = FreeYardCell(inmate, c.security, pm, guard);
                if (!target.IsValid || YardQueueAhead(pm, inmate, game) >= FreePatches(pm, inmate, c.security)) return false;
            }
            // (The guard waits in the room of the inmate's cell, not on it.)
            Room room = target.GetRoom(inmate.Map);
            IntVec3 at = target;
            post = CellFinder.StandableCellNear(at, inmate.Map, 3f, c => c != at && c.GetRoom(inmate.Map) == room);
            if (!post.IsValid) post = target;
            return true;
        }

        /// <summary>Inmates due for the yard now who have gone longest without it (before this one).</summary>
        private static int YardQueueAhead(PrisonMap pm, Pawn inmate, PrisonGame game)
        {
            int ahead = 0;
            CompInmate mine = inmate.Inmate();
            IReadOnlyList<Pawn> prisoners = pm.map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn o = prisoners[i];
                if (o == inmate || o.Inmate() is not { activeFlag: true } oc || oc.security != mine.security || DueFor(o, oc, game) != EscortKind.Yard) continue;
                if (oc.yardSlot < mine.yardSlot || oc.yardSlot == mine.yardSlot && o.thingIDNumber < inmate.thingIDNumber) ahead++;
            }
            return ahead;
        }

        private static int FreePatches(PrisonMap pm, Pawn inmate, SecurityLevelDef level)
        {
            int free = 0;
            foreach (PrisonZoneDef zone in YardZones(level))
                foreach (ZonePatch patch in pm.zones.Patches)
                    if (patch.zone == zone && !PatchTaken(pm, pm.zones.PatchAt(patch.anchor), inmate)) free++;
            return free;
        }
    }

    /// <summary>The warden's step-down letter: after ten clean days, to maximum, or stay where they are.</summary>
    public class SupermaxStepDownDecision : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p == null || !InmateUtility.IsActiveInmate(p) || p.Inmate() is not { } c || !SupermaxSystem.IsIn(c)) return;
            SecurityLevelDef maximum = RPSecurityDefOf.RP_Maximum;
            if (!Unlocks.IsUnlocked(maximum) || !InmateUtility.HasRoomIn(p, maximum))
            {
                Messages.Message("RP_SupermaxStepDownNoRoom".Translate(p.LabelShort, maximum.label), p, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            InmateUtility.ChangeSecurity(p, c, maximum, why: ("RP_Supermax_Why_" + SupermaxReason.StepDown).Translate());
        }
    }
}
