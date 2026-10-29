using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The Board is listed on the in-game storyteller page (its difficulty buttons are drawn only for a listed storyteller, and a warden who picked another one must be able to come back),
    /// but not in a new colony's list, where it would be preselected (it sorts first) and a prison would start from a colony page. The New prison dialog picks it by defName.
    /// </summary>
    public static class StorytellerList
    {
        /// <summary>The colony list of the main menu (the game is not running yet).</summary>
        public static bool InNewColonyList => Current.ProgramState == ProgramState.Entry;

        /// <summary>Takes the Board out of the list for the call that follows when it is the new colony's; the result is for <see cref="Show"/>.</summary>
        public static bool Hide()
        {
            if (!InNewColonyList || RPDefOf.RP_TheBoard is not { listVisible: true } board) return false;
            board.listVisible = false;
            return true;
        }

        public static void Show(bool hidden)
        {
            if (hidden) RPDefOf.RP_TheBoard.listVisible = true;
        }
    }

    [HarmonyPatch(typeof(StorytellerUI), nameof(StorytellerUI.DrawStorytellerSelectionInterface))]
    public static class Patch_StorytellerUI_DrawStorytellerSelectionInterface
    {
        public static void Prefix(out bool __state) => __state = StorytellerList.Hide();

        public static void Finalizer(bool __state) => StorytellerList.Show(__state);
    }

    /// <summary>A new colony's page preselects the first listed storyteller: not the Board.</summary>
    [HarmonyPatch(typeof(Page_SelectStoryteller), nameof(Page_SelectStoryteller.PreOpen))]
    public static class Patch_Page_SelectStoryteller_PreOpen
    {
        public static void Prefix(out bool __state) => __state = StorytellerList.Hide();

        public static void Finalizer(bool __state) => StorytellerList.Show(__state);
    }
}
