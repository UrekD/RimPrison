using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Clean uniforms. Inmates swap their uniform at shower time (or when it is filthy) from the prison's stock; the
    /// stock is filled by inmates working the laundry, and whatever is missing at midnight is sent out to a laundry
    /// service at a price. A laundry room pays for itself and gives inmates work.
    /// </summary>
    public class LaundrySystem : IExposable
    {
        public const int OutsourcedPrice = 9;
        public const float SwapBelow = 0.5f;

        public int stock = 10;
        /// <summary>Uniforms washed in-house since the start (stat).</summary>
        public int washed;
        /// <summary>Uniforms sewn at the tailor bench since the start (stat).</summary>
        public int sewn;
        /// <summary>Uniforms sewn beyond what the stock needs: sold at the next export run (see <see cref="Tailoring"/>).</summary>
        public int surplus;

        public void Add(int n)
        {
            stock += n;
            washed += n;
        }

        /// <summary>Two days' worth of clean uniforms: below it there is washing (and sewing) to do.</summary>
        public static int Goal(Map map) => 2 * map.mapPawns.PrisonersOfColonySpawnedCount;

        /// <summary>A cycle at the tailor bench: the uniforms wait for the export run (or for the stock to run out, see <see cref="Take"/>).</summary>
        public void AddSewn(int n)
        {
            surplus += n;
            sewn += n;
        }

        /// <summary>The stock takes up to <paramref name="n"/> of the sewn surplus (not paid for again); returns how many.</summary>
        public int Take(int n)
        {
            int taken = Mathf.Clamp(n, 0, surplus);
            surplus -= taken;
            stock += taken;
            return taken;
        }

        /// <summary>The export run takes every surplus uniform.</summary>
        public int TakeSurplus()
        {
            int n = surplus;
            surplus = 0;
            return n;
        }

        /// <summary>Hourly per inmate: fresh uniform at shower time, or any time once it is filthy.</summary>
        public static void Hourly(Pawn p, CompInmate c)
        {
            if (p.needs?.TryGetNeed(RPDefOf.RP_CleanClothes) is not { } need || need.CurLevel >= SwapBelow) return;
            LaundrySystem laundry = PrisonGame.Instance.laundry;
            bool showerTime = PrisonGame.Instance.CurrentActivity(p) == RPDefOf.RP_ShowerTime;
            if (!showerTime && need.CurLevel >= 0.15f) return;
            // (An empty stock takes a uniform from the tailors' surplus before the inmate goes without.)
            if (laundry.stock <= 0) laundry.Take(1);
            if (laundry.stock <= 0) return;
            laundry.stock--;
            need.CurLevel = 1f;
        }

        /// <summary>Midnight: top the stock up to one uniform per inmate, from the tailors' surplus first, then from an outside laundry service.</summary>
        public void DailyTopUp(PrisonGame game, int inmates)
        {
            int missing = inmates - stock;
            if (missing <= 0) return;
            missing -= Take(missing);
            if (missing <= 0) return;
            int cost = missing * OutsourcedPrice;
            if (!game.ledger.TrySpend(RPDefOf.RP_Laundry, cost, "RP_LaundryNote".Translate(missing))) return;
            stock += missing;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref stock, "stock", 10);
            Scribe_Values.Look(ref washed, "washed");
            Scribe_Values.Look(ref sewn, "sewn");
            Scribe_Values.Look(ref surplus, "surplus");
        }
    }
}
