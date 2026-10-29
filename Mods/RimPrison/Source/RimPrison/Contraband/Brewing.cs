using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>The mash in a cell stash: stolen fruit, left to work for <see cref="HoochUtility.FermentTicks"/> before it is hooch.</summary>
    public class CompProperties_HoochMash : CompProperties
    {
        public CompProperties_HoochMash() => compClass = typeof(CompHoochMash);
    }

    public class CompHoochMash : ThingComp
    {
        /// <summary>When the mash was started (a stack that takes in more keeps the older start).</summary>
        public int sinceTick;

        public override void PostPostMake()
        {
            base.PostPostMake();
            if (Current.ProgramState == ProgramState.Playing) sinceTick = Find.TickManager.TicksGame;
        }

        /// <summary>A piece split off a lot is as old as the lot (a split-off thing is made new, and <see cref="PostPostMake"/> stamps it with now).</summary>
        public override void PostSplitOff(Thing piece)
        {
            base.PostSplitOff(piece);
            if (piece.TryGetComp<CompHoochMash>() is { } other) other.sinceTick = sinceTick;
        }

        public override void PostExposeData() => Scribe_Values.Look(ref sinceTick, "mashSince");
    }

    /// <summary>
    /// Hooch: a kitchen worker nobody is watching pockets some of the kitchen's raw fruit and vegetables and hides the mash in their cell stash; two days on it is
    /// <c>RP_Hooch</c>, a drink like beer that they drink, sell or hand on as any drug. Both are contraband like the rest of the stash: a cell search finds them (the mash is the
    /// still), the buster of a ring takes them, an amnesty box holds them. Nothing here is shown anywhere until it is found.
    /// </summary>
    public static class HoochUtility
    {
        public const int FermentTicks = 2 * GenDate.TicksPerDay;
        /// <summary>Hooch one lot of mash makes.</summary>
        public const int HoochPerMash = 2;
        /// <summary>Per hour of kitchen work with nobody watching (and fruit in the kitchen).</summary>
        public const float StealChance = 0.08f;

        /// <summary>Hourly while an inmate works the kitchen: unwatched, they may take a piece of fruit from the kitchen stock and start a mash in their cell. Returns whether they did.</summary>
        public static bool Notify_WorkedInKitchen(Pawn p, CompInmate c, PrisonMap pm, bool force = false)
        {
            if (pm == null || c.stash == null || !force && (c.tension < 15f || TensionUtility.Watched(p, pm) || !Rand.Chance(StealChance))) return false;
            Thing fruit = KitchenFruit(p.MapHeld ?? p.Map, pm);
            if (fruit == null) return false;
            Thing mash = ThingMaker.MakeThing(RPContrabandDefOf.RP_HoochMash);
            mash.stackCount = 1;
            // (TryAdd refuses only a thing that is not fit to add, never for want of room: the stash has no stack limit. The thing is not left lying about if it ever does.)
            if (!c.stash.TryAdd(mash))
            {
                mash.Destroy();
                return false;
            }
            fruit.SplitOff(1).Destroy();
            return true;
        }

        /// <summary>A piece of raw fruit or vegetables lying in the kitchen.</summary>
        public static Thing KitchenFruit(Map map, PrisonMap pm)
        {
            if (map == null) return null;
            foreach (IntVec3 cell in pm.zones.CellsOf(RPLaborDefOf.RP_Kitchen))
            {
                List<Thing> things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                    if (IsFruit(things[i])) return things[i];
            }
            return null;
        }

        /// <summary>Raw fruit or vegetables (an item a person eats, of the fruit and vegetable kind).</summary>
        public static bool IsFruit(Thing t) =>
            t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible && t.def.ingestible is { HumanEdible: true } food && (food.foodType & FoodTypeFlags.VegetableOrFruit) != 0;

        /// <summary>The mash that has worked long enough becomes hooch, in the stash (every 250 ticks, with the rest of the stash).</summary>
        public static void Ferment(CompInmate c)
        {
            if (c.stash == null || c.stash.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = c.stash.Count - 1; i >= 0; i--)
            {
                Thing t = c.stash[i];
                if (t.def != RPContrabandDefOf.RP_HoochMash || t.TryGetComp<CompHoochMash>() is not { } mash || now - mash.sinceTick < FermentTicks) continue;
                int count = t.stackCount * HoochPerMash;
                c.stash.Remove(t);
                t.Destroy();
                Thing hooch = ThingMaker.MakeThing(RPContrabandDefOf.RP_Hooch);
                hooch.stackCount = count;
                // (As above: refused only for a thing not fit to add, and then not left lying about.)
                if (!c.stash.TryAdd(hooch)) hooch.Destroy();
            }
        }

        /// <summary>For the snapshot of a save/load round trip: what the stash holds, and since when the mash has worked.</summary>
        public static string Describe(CompInmate c)
        {
            var sb = new StringBuilder();
            foreach (Thing t in c.stash) sb.Append(t.def.defName).Append('x').Append(t.stackCount).Append('@').Append(t.TryGetComp<CompHoochMash>()?.sinceTick).Append('+');
            return sb.ToString();
        }
    }
}
