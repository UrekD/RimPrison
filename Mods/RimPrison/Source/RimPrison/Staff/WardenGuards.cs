using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Where vanilla reaches the warden without going through the mod's own gates (see <see cref="WardenSystem"/>): an extreme mental break that takes them off the floor, a work tab that opens
    /// every job to them, and a caravan or a pod that takes them off the map.
    /// </summary>
    public static class WardenGuards
    {
        /// <summary>The extreme breaks the warden cannot have: the walk off the map, the run into the wild (they would be a factionless wild man with the role still on them, and a prisoner for the taking), and the freeing of an inmate.</summary>
        public static bool BreakBarred(MentalBreakDef def) => def == RPVanillaDefOf.GiveUpExit || def == RPVanillaDefOf.RunWild || def == RPVanillaDefOf.Jailbreaker;
    }

    [HarmonyPatch(typeof(MentalBreakWorker), nameof(MentalBreakWorker.BreakCanOccur))]
    public static class Patch_MentalBreakWorker_BreakCanOccur_Warden
    {
        public static void Postfix(MentalBreakWorker __instance, Pawn pawn, ref bool __result)
        {
            if (!__result) return;
            if (WardenGuards.BreakBarred(__instance.def) && WardenSystem.IsWarden(pawn)) __result = false;
            // (Nor any member of staff the run into the wild: a guard who ran wild kept the role, was walked home by the commute and came back as a wild man (a showcase run), a prisoner for
            // the taking with the staff's keys. Their other breaks, the walk off the map among them, stand. A run into the wild forced past this test (a quest, the dev tools) is not caught here.)
            else if (__instance.def == RPVanillaDefOf.RunWild && pawn.GetComp<CompStaff>()?.role != null) __result = false;
        }
    }

    /// <summary>
    /// The warden has no work types but the emergencies and self-care (the ones every member of staff keeps): the Work tab greys the rest out and the right-click orders for them are not offered.
    /// Ticking Warden or Doctor on them would send them to subdue inmates and carry them to punishment, which no complaint filer has a thing to say about (see Complaints).
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetDisabledWorkTypes))]
    public static class Patch_Pawn_GetDisabledWorkTypes_Warden
    {
        public static void Postfix(Pawn __instance, List<WorkTypeDef> __result)
        {
            if (__result == null || !WardenSystem.IsWarden(__instance)) return;
            List<WorkTypeDef> all = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
                if (!StaffUtility.AlwaysAllowed(all[i]) && !__result.Contains(all[i])) __result.Add(all[i]);
        }
    }

    /// <summary>No caravan or pod takes the warden: the post would stand empty, and what the state would do about it is a second warden when they come back.</summary>
    [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllSendablePawns))]
    public static class Patch_CaravanFormingUtility_AllSendablePawns_Warden
    {
        public static void Postfix(List<Pawn> __result) => __result?.RemoveAll(WardenSystem.IsWarden);
    }
}
