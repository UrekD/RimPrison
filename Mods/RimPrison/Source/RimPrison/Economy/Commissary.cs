using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>What the commissary sells and for how much (inmate dollars).</summary>
    public class CommissaryItemExtension : DefModExtension
    {
        public int price = 5;
        public ThoughtDef thought;
    }

    /// <summary>
    /// Inmate money: wages for prison jobs (paid by the prison at midnight) and family deposits at visits. Spent at
    /// commissary counters on small luxuries — profit for the prison, comfort for the inmate.
    /// </summary>
    public static class CommissarySystem
    {
        public static int PriceOf(ThingDef def) => def.GetModExtension<CommissaryItemExtension>()?.price ?? 0;

        /// <summary>Hourly while working.</summary>
        public static void PayWage(CompInmate c)
        {
            if (c.job == null || c.job.wagePerHour <= 0) return;
            c.cash += c.job.wagePerHour;
            PrisonGame.Instance.today.inmateWages += c.job.wagePerHour;
        }

        public static void FamilyDeposit(CompInmate c)
        {
            int amount = Rand.RangeInclusive(5, 25);
            c.cash += amount;
        }

        /// <summary>
        /// The inmate pays for one of the stack and has it in their pockets. False when they cannot pay; <paramref name="eat"/> is the job to enjoy it on the spot (null when
        /// there is nothing to eat, or no room for it in the pockets, in which case it lies where they stand).
        /// </summary>
        public static bool Purchase(Pawn pawn, Thing stack, out Job eat)
        {
            eat = null;
            CompInmate c = pawn.Inmate();
            int price = PriceOf(stack.def);
            if (c == null || c.cash < price) return false;
            Thing one = stack.SplitOff(1);
            ThingDef def = one.def;
            c.cash -= price;
            PrisonGame game = PrisonGame.Instance;
            game.today.commissarySales += price;
            game.stats.commissarySales += price;
            if (!pawn.inventory.innerContainer.TryAdd(one))
            {
                GenPlace.TryPlaceThing(one, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                return true;
            }
            if (def.GetModExtension<CommissaryItemExtension>()?.thought is { } thought) pawn.needs.mood?.thoughts.memories.TryGainMemory(thought);
            c.tension = Mathf.Max(0f, c.tension - 3f);
            if (!def.IsIngestible) return true;
            // One that joined a stack they already carried is gone as a thing of its own (a job on it would find nothing): they eat from the stack.
            Thing food = one.Destroyed || one.holdingOwner != pawn.inventory.innerContainer ? pawn.inventory.innerContainer.FirstOrDefault(t => t.def == def) : one;
            if (food == null) return true;
            eat = JobMaker.MakeJob(JobDefOf.Ingest, food);
            eat.count = 1;
            return true;
        }

        private static List<ThingDef> itemDefs;

        /// <summary>Whether anything the commissary sells lies on the map (the counters hold the stock).</summary>
        public static bool AnyStock(Map map)
        {
            if (itemDefs == null)
            {
                itemDefs = new List<ThingDef>();
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (d.HasModExtension<CommissaryItemExtension>()) itemDefs.Add(d);
            }
            for (int i = 0; i < itemDefs.Count; i++)
                if (map.listerThings.ThingsOfDef(itemDefs[i]).Count > 0) return true;
            return false;
        }

        /// <summary>Free time: an inmate with money and an appetite for a treat goes shopping.</summary>
        public static Job TryShop(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (c.cash < 3 || HungerStrike.Fasting(c) || Sanctions.Banned(c, Sanction.LoseCommissary) || !Rand.Chance(0.35f)) return null;
            if (p.needs.joy != null && p.needs.joy.CurLevel > 0.85f) return null;
            // (Their own errand: the counter of a patch kept for other levels is not theirs, and with no other there is no shopping, nobody needs a treat to live.)
            // Nothing on sale anywhere: no sweep of the haulables within forty cells (it could only find nothing).
            if (!AnyStock(p.Map)) return null;
            Thing item = GenClosest.ClosestThingReachable(p.Position, p.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver), PathEndMode.ClosestTouch,
                TraverseParms.For(p), 40f, t => t.def.HasModExtension<CommissaryItemExtension>() && PriceOf(t.def) <= c.cash
                                                && t.Position.GetFirstBuilding(t.Map)?.def == RPCommissaryDefOf.RP_CommissaryCounter && KeptRooms.AllowsTouch(pm, t.Position, p) && p.CanReserve(t));
            if (item == null) return null;
            Job job = JobMaker.MakeJob(RPCommissaryDefOf.RP_BuyCommissary, item);
            job.count = 1;
            return job;
        }
    }

    [DefOf]
    public static class RPCommissaryDefOf
    {
        public static ThingDef RP_CommissaryCounter;
        public static JobDef RP_BuyCommissary;

        static RPCommissaryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCommissaryDefOf));
    }

    /// <summary>Walk to the counter, pay, take one, and enjoy it on the spot.</summary>
    public class JobDriver_BuyCommissary : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, 1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
            Toil buy = ToilMaker.MakeToil("Buy");
            buy.initAction = () =>
            {
                if (!CommissarySystem.Purchase(pawn, job.targetA.Thing, out Job eat)) EndJobWith(JobCondition.Incompletable);
                else if (eat != null) pawn.jobs.jobQueue.EnqueueFirst(eat);
            };
            yield return buy;
        }
    }
}
