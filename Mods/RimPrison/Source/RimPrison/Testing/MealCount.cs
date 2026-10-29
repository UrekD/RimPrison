using HarmonyLib;
using RimWorld;

namespace RimPrison
{
    /// <summary>Test diagnostics: counts the food bills the cooks finish (see <see cref="AutoTest.mealsCooked"/>).</summary>
    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.Notify_IterationCompleted))]
    [TestOnly]
    public static class Patch_BillProduction_Count
    {
        public static void Postfix(Bill_Production __instance)
        {
            if (AutoTest.Active && __instance.recipe?.ProducedThingDef is { IsIngestible: true }) AutoTest.mealsCooked++;
        }
    }
}
