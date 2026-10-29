using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The prison has a name from day one: the player faction and the home settlement take it, so vanilla never asks
    /// to "name your colony". Chosen in the new prison dialog, otherwise made up (also for older saves).
    /// </summary>
    public static class PrisonNaming
    {
        /// <summary>Set by the new prison dialog for the game it is about to start.</summary>
        public static string Pending;

        public static string Generate()
        {
            string[] places = "RP_PrisonNamePlaces".Translate().ToString().Split(';');
            string[] kinds = "RP_PrisonNameKinds".Translate().ToString().Split(';');
            return places.RandomElement().Trim() + " " + kinds.RandomElement().Trim();
        }

        public static void Apply(PrisonGame game)
        {
            // The dialog's name is for the game it started (a new game still has its init data), never a later load.
            string chosen = Current.Game?.InitData != null ? Pending?.Trim() : null;
            Pending = null;
            if (game.prisonName.NullOrEmpty())
            {
                // An older save keeps the colony's name if the player gave it one.
                Settlement home = Find.WorldObjects.Settlements.FirstOrDefault(s => s.Faction == Faction.OfPlayer && s.namedByPlayer && !s.Name.NullOrEmpty());
                game.prisonName = !chosen.NullOrEmpty() ? chosen
                    : Faction.OfPlayerSilentFail is { HasName: true } named && !named.Name.Trim().NullOrEmpty() ? named.Name
                    : home != null ? home.Name : Generate();
            }
            if (Faction.OfPlayerSilentFail is { } player && !player.HasName) player.Name = game.prisonName;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Faction != Faction.OfPlayer || s.namedByPlayer) continue;
                s.Name = game.prisonName;
                s.namedByPlayer = true;
            }
        }
    }
}
