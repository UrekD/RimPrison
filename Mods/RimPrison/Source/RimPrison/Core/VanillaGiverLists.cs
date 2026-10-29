using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Vanilla's warden and doctor givers offer the scan every prisoner (or every hurt, hungry or downed pawn), and the scan asks whether the guard can reach each one before the giver is asked
    /// whether it has a job there: at a hundred inmates that was most of the staff's work scan. These postfixes give each giver the inmates it could act on (<see cref="InmateCandidates"/>).
    /// Every list holds, exactly, the inmates that pass the giver's own tests that do not depend on who is asking (the tests its <c>HasJobOnThing</c> and <c>JobOnThing</c> start with); an
    /// inmate left out is one the giver would have answered "no job" for, for every guard. Forced orders (the right-click menu) do not go through the lists.
    /// </summary>
    public static class VanillaGiverLists
    {
        private static bool Mode(Pawn p, PrisonerInteractionModeDef mode) => mode != null && p.guest != null && p.guest.IsInteractionEnabled(mode);

        /// <summary>A recruit or resistance interview (<c>WorkGiver_Warden_Chat</c>).</summary>
        public static bool ChatCandidate(Pawn p) => Mode(p, PrisonerInteractionModeDefOf.AttemptRecruit) || Mode(p, PrisonerInteractionModeDefOf.ReduceResistance);

        public static bool ConvertCandidate(Pawn p) => Mode(p, PrisonerInteractionModeDefOf.Convert);

        public static bool InterrogateCandidate(Pawn p) => Mode(p, PrisonerInteractionModeDefOf.Interrogate);

        /// <summary>Told to be released, and in a state to be carried to the gate (<c>WorkGiver_Warden_ReleasePrisoner</c>).</summary>
        public static bool ReleaseCandidate(Pawn p) => Mode(p, PrisonerInteractionModeDefOf.Release) && !p.Downed && !p.guest.Released;

        public static bool ExecutionCandidate(Pawn p) => Mode(p, PrisonerInteractionModeDefOf.Execution);

        /// <summary>In bed, hungry, and one the wardens feed by hand (<c>WorkGiver_Warden_Feed</c>).</summary>
        public static bool FeedPrisonerCandidate(Pawn p) =>
            WardenFeedUtility.ShouldBeFed(p) && p.needs?.food is { } food && food.CurLevelPercentage < food.PercentageThreshHungry + 0.02f;

        /// <summary>
        /// Downed and needing a bed, or on their feet with no bed of their own they could lie down in (<c>WorkGiver_Warden_TakeToBed</c>): an inmate in their own cell, in or at their bed, is
        /// not, and finding one a bed was most of what the giver cost. Nor is an evacuee while their wing is being emptied: the giver knows only their own bed, which is in it (the prison's rescue takes one down at the point to
        /// a bed outside the wing, see <see cref="EvacuationSystem.BedAllowed"/>). The list alone does not keep the giver off them (the work scan looks round the guard region by region first): the giver itself makes no job for them,
        /// see <see cref="Patch_WorkGiver_Warden_TakeToBed_Evacuees"/>.
        /// </summary>
        public static bool TakeToBedCandidate(Pawn p)
        {
            if (EvacuationSystem.BeingEmptied(p)) return false;
            if (p.Downed) return HealthAIUtility.ShouldSeekMedicalRest(p) && !p.InBed();
            Building_Bed own = p.ownership?.OwnedBed;
            return own == null || !RestUtility.IsValidBedFor(own, p, p, checkSocialProperness: true, allowMedBedEvenIfSetToNoCare: false, ignoreOtherReservations: false, GuestStatus.Prisoner);
        }

        /// <summary>Someone to tend, as every doctor but themselves would see it (<c>WorkGiver_Tend.HasJobOnThing</c> for a patient who is not the doctor).</summary>
        public static bool TendCandidate(Pawn p) =>
            WorkGiver_Tend.GoodLayingStatusForTend(p, null) && HealthAIUtility.ShouldBeTendedNowByPlayer(p) && !(p.IsMutant && !p.mutant.Def.entitledToMedicalCare)
            && !(p.InAggroMentalState && !p.health.hediffSet.HasHediff(HediffDefOf.Scaria));

        public static bool FeedPatientCandidate(Pawn p) => FeedPatientUtility.IsHungry(p) && FeedPatientUtility.ShouldBeFed(p) && !p.DevelopmentalStage.Baby();

        /// <summary>A downed inmate a guard of the colony would pick up (<c>HealthAIUtility.CanRescueNow</c>: their own side only; the prison's own giver looks after the rest).</summary>
        public static bool RescueDownedCandidate(Pawn p) => p.Faction == Faction.OfPlayer && HealthAIUtility.WantsToBeRescued(p);
    }

    /// <summary>The wardens' vanilla givers scan the inmates that could need them.</summary>
    [HarmonyPatch(typeof(WorkGiver_Warden), nameof(WorkGiver_Warden.PotentialWorkThingsGlobal))]
    public static class Patch_WorkGiver_Warden_Candidates
    {
        public static void Postfix(WorkGiver_Warden __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (PrisonGame.Instance == null || pawn?.Map is not { } map) return;
            InmateCandidates lists = InmateCandidates.Shared;
            System.Type type = __instance.GetType();
            // (Exactly these classes: a giver that is another's subclass has tests of its own.)
            if (type == typeof(WorkGiver_Warden_Chat)) __result = lists.Chat(map);
            else if (type == typeof(WorkGiver_Warden_Convert)) __result = lists.Convert(map);
            else if (type == typeof(WorkGiver_Warden_InterrogateIdentity)) __result = lists.Interrogate(map);
            else if (type == typeof(WorkGiver_Warden_ReleasePrisoner)) __result = lists.Release(map);
            else if (type == typeof(WorkGiver_Warden_DoExecution)) __result = lists.DoExecution(map);
            else if (type == typeof(WorkGiver_Warden_Feed)) __result = lists.FeedPrisoner(map);
            else if (type == typeof(WorkGiver_Warden_TakeToBed)) __result = lists.TakeToBed(map);
        }
    }

    /// <summary>The doctors' tending scans the hurt, without the inmates nobody could tend.</summary>
    [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.PotentialWorkThingsGlobal))]
    public static class Patch_WorkGiver_Tend_Candidates
    {
        public static void Postfix(WorkGiver_Tend __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (PrisonGame.Instance == null || pawn?.Map is not { } map) return;
            System.Type type = __instance.GetType();
            if (type == typeof(WorkGiver_TendOther) || type == typeof(WorkGiver_TendOtherUrgent)) __result = InmateCandidates.Shared.Tend(map, __result);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_TendOther_Humanlike), nameof(WorkGiver_TendOther_Humanlike.PotentialWorkThingsGlobal))]
    public static class Patch_WorkGiver_TendHumanlike_Candidates
    {
        public static void Postfix(Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (PrisonGame.Instance != null && pawn?.Map is { } map) __result = InmateCandidates.Shared.TendHumanlike(map, __result);
        }
    }

    /// <summary>Feeding the sick and the downed scans the hungry, without the inmates nobody would feed by hand.</summary>
    [HarmonyPatch(typeof(WorkGiver_FeedPatient), nameof(WorkGiver_FeedPatient.PotentialWorkThingsGlobal))]
    public static class Patch_WorkGiver_FeedPatient_Candidates
    {
        public static void Postfix(WorkGiver_FeedPatient __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (PrisonGame.Instance == null || pawn?.Map is not { } map) return;
            if (__instance.def.feedHumanlikesOnly) __result = InmateCandidates.Shared.FeedPatient(map, __result);
            else if (__instance.def.feedAnimalsOnly) __result = InmateCandidates.Shared.FeedAnimals(map, __result);
        }
    }

    [HarmonyPatch(typeof(WorkGiver_RescueDowned), nameof(WorkGiver_RescueDowned.PotentialWorkThingsGlobal))]
    public static class Patch_WorkGiver_RescueDowned_Candidates
    {
        public static void Postfix(Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (PrisonGame.Instance != null && pawn?.Map is { } map) __result = InmateCandidates.Shared.RescueDowned(map, __result);
        }
    }
}
