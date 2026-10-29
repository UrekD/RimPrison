using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>The day's meal counts for the warden's desk (<see cref="DeskBoard"/>): meals the stoves' bills finish, here (the inmate cooks count theirs in their own job) and meals inmates eat there.</summary>
    public static class MealTally
    {
        /// <summary>A finished bill of this recipe: the meals it makes (a bulk recipe makes four).</summary>
        public static void Cooked(PrisonGame game, RecipeDef recipe)
        {
            if (recipe?.products == null) return;
            foreach (ThingDefCountClass product in recipe.products)
                if (MealCounters.IsMeal(product.thingDef)) game.today.mealsCooked += product.count;
        }

        /// <summary>A meal eaten by this pawn: counted when they are an inmate.</summary>
        public static void Eaten(PrisonGame game, Pawn eater, Thing food)
        {
            // (The inmates the desk counts: not one on the way out, as the grade and the wing rows leave them out.)
            if (!eater.IsPrisonerOfColony || ShuttleSystem.OnTheWayOut(eater) || !MealCounters.IsMeal(food)) return;
            game.today.mealsEaten++;
            // (And when, for the regime tab's missed-meal count.)
            if (eater.MapHeld is { } map) PrisonMap.Of(map)?.regime.NoteMeal(eater);
        }
    }

    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.Notify_IterationCompleted))]
    public static class Patch_BillProduction_MealsCooked
    {
        public static void Postfix(Bill_Production __instance)
        {
            if (PrisonGame.Instance is { } game) MealTally.Cooked(game, __instance.recipe);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
    public static class Patch_Thing_Ingested_MealsEaten
    {
        public static void Postfix(Thing __instance, Pawn ingester, float __result)
        {
            if (__result > 0f && PrisonGame.Instance is { } game) MealTally.Eaten(game, ingester, __instance);
        }
    }
}
