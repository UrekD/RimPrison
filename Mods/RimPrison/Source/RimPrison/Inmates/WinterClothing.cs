using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Winter jackets (policy, default on): in a cold month every inmate is issued a jacket over the uniform, and it is
    /// handed back when the weather turns. Jackets are bought once and kept in the prison store.
    /// </summary>
    public static class WinterClothing
    {
        public const float ColdBelow = 12f;

        private static ThingDef Jacket => RPVanillaDefOf.Apparel_Jacket;

        public static bool ColdSeason(Map map) =>
            GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, GenLocalDate.Twelfth(map)) < ColdBelow;

        /// <summary>An inmate leaving custody hands the issued jacket back to the store.</summary>
        public static void Collect(Pawn p, PrisonGame game)
        {
            if (p.apparel == null) return;
            foreach (Apparel a in p.apparel.WornApparel.ToList())
                if (a.def == Jacket && p.apparel.IsLocked(a))
                {
                    p.apparel.Remove(a);
                    a.Destroy();
                    game.jacketStock++;
                }
        }

        /// <summary>Morning: issue or collect jackets for everyone.</summary>
        public static void Morning(Map map, PrisonGame game, bool? coldOverride = null)
        {
            ThingDef jacket = Jacket;
            if (jacket == null) return;
            bool wanted = game.winterJackets && (coldOverride ?? ColdSeason(map));
            int bought = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.apparel == null || p.Inmate() is not { activeFlag: true } c) continue;
                Apparel worn = p.apparel.WornApparel.Find(a => a.def == jacket);
                if (!wanted)
                {
                    if (worn == null) continue;
                    p.apparel.Remove(worn);
                    worn.Destroy();
                    game.jacketStock++;
                    continue;
                }
                if (worn != null || !jacket.apparel.PawnCanWear(p)) continue;
                if (game.jacketStock > 0) game.jacketStock--;
                else bought++;
                var a = (Apparel)ThingMaker.MakeThing(jacket, ThingDefOf.Cloth);
                a.SetColor(c.security?.color ?? Color.gray, reportFailure: false);
                p.apparel.Wear(a, dropReplacedApparel: false, locked: true);
            }
            if (bought > 0)
                game.ledger.Add(RPDefOf.RP_Supplies, -bought * SupplySystem.Price(jacket, 1), "RP_JacketsNote".Translate(bought));
        }
    }
}
