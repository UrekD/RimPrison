using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Simple utilities (setting, default on): the prison is on the mains. Every building that needs power is
    /// powered without generators or conduits, and the prison pays a daily utility bill for the power it draws.
    /// Off: vanilla power grids.
    /// </summary>
    public static class UtilitySystem
    {
        /// <summary>Dollars per 100 W of draw per day.</summary>
        public const float RatePer100W = 1.5f;

        public static bool Active => RimPrisonMod.Settings.simpleUtilities && PrisonGame.Instance != null;

        /// <summary>Every 250 ticks: switch on every powered building that wants to be on.</summary>
        public static void Tick(Map map)
        {
            if (!Active) return;
            bool blackout = PrisonGame.Instance.BlackoutActive;
            // A hostage crisis with power and water cut: the held rooms go dark.
            PrisonMap pm = PrisonMap.Of(map);
            HashSet<int> cut = pm != null && pm.heldPowerCut ? pm.heldCells : null;
            // (The map's power traders, not every building asked for a power comp: nearly all of a prison's buildings are walls.)
            List<Thing> traders = map.listerThings.ThingsInGroup(ThingRequestGroup.PowerTrader);
            for (int i = 0; i < traders.Count; i++)
            {
                if (traders[i] is not Building b || b.Faction != Faction.OfPlayer || b.GetComp<CompPowerTrader>() is not { } power) continue;
                if (blackout)
                {
                    if (power.PowerOn && power.Props.PowerConsumption > 0f) power.PowerOn = false;
                    continue;
                }
                if (cut != null && cut.Contains(map.cellIndices.CellToIndex(b.Position)))
                {
                    if (power.PowerOn && power.Props.PowerConsumption > 0f) power.PowerOn = false;
                    continue;
                }
                if (power.PowerOn || power.PowerOutput > 0f) continue;
                if (!FlickUtility.WantsToBeOn(b) || b.IsBrokenDown()) continue;
                power.PowerOn = true;
            }
        }

        /// <summary>What the prison draws right now.</summary>
        public static float Draw(Map map)
        {
            float answer = DrawFast(map);
            if (AutoTest.Active) Shadow.CheckDraw(map, answer);
            return answer;
        }

        private static float DrawFast(Map map)
        {
            float watts = 0f;
            List<Thing> traders = map.listerThings.ThingsInGroup(ThingRequestGroup.PowerTrader);
            for (int i = 0; i < traders.Count; i++)
                if (traders[i] is Building b && b.Faction == Faction.OfPlayer && b.GetComp<CompPowerTrader>() is { PowerOn: true } power && power.Props.PowerConsumption > 0f) watts += power.Props.PowerConsumption;
            return watts;
        }

        /// <summary>Hourly: one more sample of the draw for today's bill.</summary>
        public static void SampleHourly(Map map, PrisonGame game)
        {
            game.today.powerSamples++;
            game.today.powerWattSum += Draw(map);
        }

        /// <summary>The day's bill: the average draw over the hours sampled (sun lamps and what is off at midnight are billed for the hours they were on); with none sampled, the draw now.</summary>
        public static int DailyBill(Map map)
        {
            DailyIncidents today = PrisonGame.Instance.today;
            float watts = today.powerSamples > 0 ? today.powerWattSum / today.powerSamples : Draw(map);
            return Mathf.RoundToInt(watts / 100f * RatePer100W);
        }
    }

    /// <summary>With simple utilities, power nets never brown out (they would switch everything off).</summary>
    [HarmonyPatch(typeof(PowerNet), nameof(PowerNet.PowerNetTick))]
    public static class Patch_PowerNet_PowerNetTick
    {
        public static bool Prefix() => !UtilitySystem.Active;
    }
}
