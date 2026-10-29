using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Things the supply company sells. Prices are market value × markup.</summary>
    public class SupplyCatalogDef : Def
    {
        public float markup = 1.1f;
        public List<SupplyCatalogEntry> entries = new List<SupplyCatalogEntry>();
    }

    public class SupplyCatalogEntry
    {
        public ThingDef thing;
        public int packSize = 10;
        /// <summary>Default "keep in stock" level for automatic restocking (0 = manual only).</summary>
        public int defaultStock;
        /// <summary>Restocking keeps at least this much per inmate (food, fuel scale with the population).</summary>
        public float stockPerInmate;
    }

    public class SupplyOrder : IExposable
    {
        public ThingDef thing;
        public int count;
        public int arriveTick;
        public int cost;
        /// <summary>Blueprint this order was placed for (refunded if the blueprint is cancelled before delivery).</summary>
        public int blueprintId = -1;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref thing, "thing");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref arriveTick, "arriveTick");
            Scribe_Values.Look(ref cost, "cost");
            Scribe_Values.Look(ref blueprintId, "blueprintId", -1);
        }
    }

    /// <summary>
    /// Supplies: orders (manual, automatic restock, blueprint materials) are paid on ordering and ready two hours later; the state shuttle (the daily
    /// one, or a cargo run between the daily ones, see <see cref="ShuttleSystem"/>) carries what is ready and unloads it on the pad or at the Deliveries zone.
    /// </summary>
    public class SupplySystem : IExposable
    {
        /// <summary>How long after the order the state has the goods ready for a shuttle.</summary>
        public const int DeliveryDelayTicks = 2 * GenDate.TicksPerHour;
        public const int RestockHour = 6;

        public List<SupplyOrder> pending = new List<SupplyOrder>();
        public Dictionary<ThingDef, int> stockTargets = new Dictionary<ThingDef, int>();
        private bool stockDefaultsApplied;

        public static SupplyCatalogDef Catalog => RPDefOf.RP_SupplyCatalog;

        public static float UnitPrice(ThingDef def) => def.BaseMarketValue * Catalog.markup;

        public static SupplyCatalogEntry EntryFor(ThingDef def)
        {
            foreach (SupplyCatalogEntry e in Catalog.entries)
                if (e.thing == def) return e;
            return null;
        }

        /// <summary>The level restocking aims for: the set target, raised to the per-inmate level of a bigger prison.</summary>
        public int EffectiveTarget(ThingDef def, Map map)
        {
            if (!stockTargets.TryGetValue(def, out int target) || target <= 0) return 0;
            float perInmate = EntryFor(def)?.stockPerInmate ?? 0f;
            return Mathf.Max(target, Mathf.CeilToInt(perInmate * map.mapPawns.PrisonersOfColonySpawnedCount));
        }

        /// <summary>Price for <paramref name="count"/> units, rounded up to whole dollars.</summary>
        public static int Price(ThingDef def, int count) => Mathf.Max(count > 0 ? 1 : 0, Mathf.CeilToInt(UnitPrice(def) * count));

        public void EnsureDefaults()
        {
            if (stockDefaultsApplied) return;
            stockDefaultsApplied = true;
            foreach (SupplyCatalogEntry e in Catalog.entries)
                if (e.thing != null && e.defaultStock > 0 && !stockTargets.ContainsKey(e.thing)) stockTargets[e.thing] = e.defaultStock;
        }

        public bool TryOrder(ThingDef def, int count, LedgerCategoryDef category, int blueprintId = -1, bool silent = false)
        {
            if (count <= 0) return true;
            int cost = Price(def, count);
            if (!PrisonGame.Instance.ledger.TrySpend(category, cost, def.LabelCap + " x" + count))
            {
                if (!silent) Messages.Message("RP_CannotAfford".Translate(Money.Format(cost)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            pending.Add(new SupplyOrder { thing = def, count = count, cost = cost, arriveTick = Find.TickManager.TicksGame + DeliveryDelayTicks, blueprintId = blueprintId });
            return true;
        }

        /// <summary>Ordered and not yet unloaded: waiting for a shuttle, or aboard one that has not set it down.</summary>
        public int PendingCount(ThingDef def)
        {
            int n = 0;
            for (int i = 0; i < pending.Count; i++) if (pending[i].thing == def) n += pending[i].count;
            if (ShuttleSystem.Current is { } visit)
                for (int i = 0; i < visit.cargo.Count; i++) if (visit.cargo[i].thing == def) n += visit.cargo[i].count;
            return n;
        }

        /// <summary>Cancels undelivered orders placed for a blueprint and refunds them.</summary>
        public void CancelForBlueprint(int blueprintId)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                SupplyOrder o = pending[i];
                if (o.blueprintId != blueprintId) continue;
                PrisonGame.Instance.ledger.Add(RPDefOf.RP_Construction, o.cost, "RP_Refund".Translate());
                pending.RemoveAt(i);
            }
            // Loaded on a shuttle that has not unloaded yet: refunded too, the crates are not set down.
            if (ShuttleSystem.Current is { } visit)
                for (int i = visit.cargo.Count - 1; i >= 0; i--)
                {
                    if (visit.cargo[i].blueprintId != blueprintId) continue;
                    PrisonGame.Instance.ledger.Add(RPDefOf.RP_Construction, visit.cargo[i].cost, "RP_Refund".Translate());
                    visit.cargo.RemoveAt(i);
                }
        }

        /// <summary>Sets <paramref name="count"/> of a def down near the delivery spot (what the old supply truck did; the shuttle unloads stack by stack on its own).</summary>
        public static void Deliver(Map map, ThingDef def, int count, List<Thing> delivered)
        {
            IntVec3 root = DeliverySpot(map);
            while (count > 0)
            {
                Thing t = def.MadeFromStuff ? ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)) : ThingMaker.MakeThing(def);
                t.stackCount = Mathf.Min(count, def.stackLimit);
                count -= t.stackCount;
                if (GenPlace.TryPlaceThing(t, root, map, ThingPlaceMode.Near, out Thing placed)) delivered?.Add(placed);
            }
        }

        public static IntVec3 DeliverySpot(Map map)
        {
            List<IntVec3> zone = PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Deliveries);
            if (zone != null && zone.Count > 0) return zone.RandomElement();
            return IntakeSystem.IntakeSpot(map);
        }

        public void Notify_HourChanged(Map map, int hour) => Restock(map);

        /// <summary>Orders whatever is below its auto-stock level (checked every hour, PA style).</summary>
        public void Restock(Map map)
        {
            EnsureDefaults();
            foreach (KeyValuePair<ThingDef, int> kv in stockTargets)
            {
                if (kv.Key == null || kv.Value <= 0) continue;
                int target = EffectiveTarget(kv.Key, map);
                int have = CountOnMap(map, kv.Key) + PendingCount(kv.Key);
                if (have >= target) continue;
                TryOrder(kv.Key, target - have, RPDefOf.RP_Supplies, silent: true);
            }
        }

        /// <summary>Spawned stacks of a def anywhere on the map (stockpiled or not).</summary>
        public static int CountOnMap(Map map, ThingDef def)
        {
            int n = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++) n += things[i].stackCount;
            return n;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pending", LookMode.Deep);
            Scribe_Collections.Look(ref stockTargets, "stockTargets", LookMode.Def, LookMode.Value);
            Scribe_Values.Look(ref stockDefaultsApplied, "stockDefaultsApplied");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pending ??= new List<SupplyOrder>();
                pending.RemoveAll(o => o?.thing == null);
                stockTargets ??= new Dictionary<ThingDef, int>();
                stockTargets.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    /// <summary>
    /// PA-style construction: placing a blueprint pays for its materials, which the state shuttle then delivers.
    /// Placement is refused when the prison cannot afford it.
    /// </summary>
    public static class BlueprintProcurement
    {
        public static bool Active => RimPrisonMod.Settings.autoProcureBlueprints && PrisonGame.Instance != null && Current.ProgramState == ProgramState.Playing;

        public static int CostOf(BuildableDef def, ThingDef stuff)
        {
            int total = 0;
            List<ThingDefCountClass> costs = def.CostListAdjusted(stuff, errorOnNullStuff: false);
            for (int i = 0; i < costs.Count; i++) total += SupplySystem.Price(costs[i].thingDef, costs[i].count);
            return total;
        }
    }

    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.CanDesignateCell))]
    public static class Patch_Designator_Build_CanDesignateCell
    {
        public static void Postfix(Designator_Build __instance, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || !BlueprintProcurement.Active || DebugSettings.godMode) return;
            int cost = BlueprintProcurement.CostOf(__instance.PlacingDef, __instance.StuffDef);
            if (!PrisonGame.Instance.ledger.CanAfford(cost)) __result = "RP_CannotAfford".Translate(Money.Format(cost));
        }
    }

    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.PlaceBlueprintForBuild))]
    public static class Patch_GenConstruct_PlaceBlueprintForBuild
    {
        public static void Postfix(BuildableDef sourceDef, ThingDef stuff, Faction faction, Blueprint_Build __result)
        {
            if (__result == null || faction != Faction.OfPlayer || !BlueprintProcurement.Active || DebugSettings.godMode) return;
            int total = BlueprintProcurement.CostOf(sourceDef, stuff);
            if (!PrisonGame.Instance.ledger.CanAfford(total))
            {
                __result.Destroy(DestroyMode.Vanish);
                Messages.Message("RP_CannotAfford".Translate(Money.Format(total)), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            List<ThingDefCountClass> costs = sourceDef.CostListAdjusted(stuff, errorOnNullStuff: false);
            for (int i = 0; i < costs.Count; i++)
                PrisonGame.Instance.supply.TryOrder(costs[i].thingDef, costs[i].count, RPDefOf.RP_Construction, __result.thingIDNumber, silent: true);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
    public static class Patch_Thing_Destroy_RefundBlueprint
    {
        public static void Prefix(Thing __instance, DestroyMode mode)
        {
            if (mode == DestroyMode.Cancel && __instance is Blueprint_Build && PrisonGame.Instance != null)
                PrisonGame.Instance.supply.CancelForBlueprint(__instance.thingIDNumber);
        }
    }
}
