using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>When the overview's report card and to-do list were worked out, and for which map: they are worked out again after <see cref="Frames"/> frames or on another map.</summary>
    public class OverviewCache
    {
        public const int Frames = 120;

        private Map map;
        private int frame = -999;

        public void Stamp(Map forMap, int atFrame)
        {
            map = forMap;
            frame = atFrame;
        }

        public bool Stale(Map forMap, int atFrame) => forMap != map || atFrame - frame > Frames;
    }

    /// <summary>Things the warden should fix, shown in the overview (a band of lines, each opening the screen where it is fixed).</summary>
    public static class PrisonDiagnostics
    {
        /// <summary>The rooms a prison cannot run without: the canteen, the yard, the showers, holding for the arrivals and the kitchen (with a stove). The Rooms screen marks them, the "prison incomplete" alert and the overview list them while they are not painted. (The deliveries zone is not here: the crates land on the pad.)</summary>
        public static IEnumerable<PrisonZoneDef> EssentialZones() => new[] { RPDefOf.RP_Canteen, RPDefOf.RP_Yard, RPDefOf.RP_Shower, RPDefOf.RP_Holding, RPLaborDefOf.RP_Kitchen };

        public static bool IsEssential(PrisonZoneDef zone) => zone != null && EssentialZones().Contains(zone);

        /// <summary>The kitchen is painted and nothing cooks in it (a stove of the player's standing in the kitchen zone).</summary>
        public static bool KitchenWithoutStove(Map map, PrisonMap pm) => pm.zones.Any(RPLaborDefOf.RP_Kitchen) && !InmateJobWorker_Kitchen.KitchenStoves(map, pm).Any();

        public static IEnumerable<string> Problems(Map map) => Items(map).Select(i => i.text);

        /// <summary>One thing to fix and the screen to open for it (null: none). The structural ones carry a <paramref name="key"/> the inspector writes them down under (see <see cref="InspectionReport"/>); the rest have none.</summary>
        public readonly struct Item
        {
            public readonly string text;
            public readonly MainTabWindow_Prison.Tab? tab;
            public readonly string key;

            public Item(string text, MainTabWindow_Prison.Tab? tab, string key = null)
            {
                this.text = text;
                this.tab = tab;
                this.key = key;
            }
        }

        public static IEnumerable<Item> Items(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) yield break;
            const MainTabWindow_Prison.Tab rooms = MainTabWindow_Prison.Tab.Rooms;
            // In the order the welcome letter of a new facility tells the warden to build in: the pad, holding, the cells, the canteen and kitchen (with its stove), showers, the yard.
            if (!ShuttleSystem.HasPad(map)) yield return new Item("RP_Problem_NoPad".Translate(), null);
            if (!pm.zones.Any(RPDefOf.RP_Holding)) yield return new Item("RP_Problem_NoZone".Translate(RPDefOf.RP_Holding.label), rooms);
            if (IntakeSystem.Capacity(map) == 0) yield return new Item("RP_Problem_NoCells".Translate(), rooms);
            foreach (PrisonZoneDef z in BuildOrder)
                if (!pm.zones.Any(z)) yield return new Item("RP_Problem_NoZone".Translate(z.label), rooms, InspectionReport.NoZoneKey + z.defName);
            if (KitchenWithoutStove(map, pm)) yield return new Item("RP_Problem_NoStove".Translate(), rooms, InspectionReport.NoStoveKey);
            if (!pm.Fixtures.Any(f => f.Props.need == RPDefOf.RP_Hygiene)) yield return new Item("RP_Problem_NoShowers".Translate(), rooms, InspectionReport.NoShowersKey);
            if (!pm.Fixtures.Any(f => f.Props.need == RPDefOf.RP_Bladder)) yield return new Item("RP_Problem_NoToilets".Translate(), rooms, InspectionReport.NoToiletsKey);
            if (!StaffCommute.AllStaff(map).Any(GuardUtility.IsGuard)) yield return new Item("RP_Problem_NoGuards".Translate(), MainTabWindow_Prison.Tab.Staff);
            // The inspector's findings come after the rooms a prison cannot run without (the first-hour order) and before the rest: they carry a deadline and a sum.
            foreach (Item todo in InspectionReport.Todo(map)) yield return todo;
            PrisonGame game = PrisonGame.Instance;
            if (game.ledger.funds < 0) yield return new Item("RP_Problem_Debt".Translate(), MainTabWindow_Prison.Tab.Finance);
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            int hungry = inmates.Count(p => p.needs?.food is { CurCategory: >= HungerCategory.UrgentlyHungry });
            // (Hungry with meals on the counters: the way to them is the fix, and that is Rooms; with bare counters it is the supply.)
            if (hungry > 0) yield return new Item(HungryLine(map, hungry), MealsOnCounters(map) ? rooms : MainTabWindow_Prison.Tab.Supply);
            int noBed = inmates.Count(p => InmateUtility.IsActiveInmate(p) && p.ownership?.OwnedBed == null);
            if (noBed > 0) yield return new Item("RP_Problem_NoBed".Translate(noBed), MainTabWindow_Prison.Tab.Deployment);
            int religious = inmates.Count(p => p.Inmate() is { religious: true });
            if (religious > 0 && !pm.zones.Any(RPFaithDefOf.RP_Chapel)) yield return new Item("RP_Problem_NoChapel".Translate(religious), rooms);
            int bodies = Morgue.Uncollected(map, pm);
            if (bodies > 0 && !pm.zones.Any(RPMorgueDefOf.RP_Morgue)) yield return new Item("RP_Problem_Bodies".Translate(bodies), rooms);
            // A morgue that does not take them (full, out of reach, or a body outside the home area that vanilla forbade): the line stays until they are collected.
            else if (pm.zones.Any(RPMorgueDefOf.RP_Morgue) && Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks) is var stuck and > 0) yield return new Item("RP_Problem_BodiesStuck".Translate(stuck), rooms);
            if (inmates.Any(p => p.Inmate() is { } c && DeathRowSystem.ExecutionDue(c)) && DeathRowSystem.FindTable(map, null) == null)
                yield return new Item("RP_Problem_NoExecutionTable".Translate(), rooms);
            if (map.mapPawns.FreeColonistsSpawned.Any(IncidentWorker_StaffStrike.Striking)) yield return new Item("RP_Problem_Strike".Translate(), MainTabWindow_Prison.Tab.Staff);
            if (game.bureaucracy.active != null && BureaucracySystem.Stalled(map))
                yield return new Item("RP_Problem_BureaucracyStalled".Translate(game.bureaucracy.active.label), MainTabWindow_Prison.Tab.Bureaucracy);
            if (game.ledger.Yesterday is { } y && y.totals.TryGetValue(RPDefOf.RP_Laundry, out int laundry) && laundry < -50)
                yield return new Item("RP_Problem_LaundryBill".Translate(Money.Format(-laundry)), rooms);
            if (game.mail.Backlog(map, out int mailOverdue) != MailBacklog.None) yield return new Item("RP_Problem_Mail".Translate(MailSystem.Letters(mailOverdue)), rooms);
            // (The warden tried to build or paint outside the grounds: where to buy the land, or why it cannot be bought now; nothing when there is none to buy.)
            if (pm.grounds is { TriedOutsideRecently: true } grounds)
            {
                if (SpecialMeasures.Running) yield return new Item("RP_Problem_OutsideGroundsMeasures".Translate(), MainTabWindow_Prison.Tab.Land);
                else if (grounds.AnyForSale) yield return new Item("RP_Problem_OutsideGrounds".Translate(), MainTabWindow_Prison.Tab.Land);
            }
            foreach ((string line, MainTabWindow_Prison.Tab? tab) in PrisonServices.Items(map)) yield return new Item(line, tab);
        }

        /// <summary>The essential rooms after holding, in the order a new facility is built.</summary>
        private static IEnumerable<PrisonZoneDef> BuildOrder => new[] { RPDefOf.RP_Canteen, RPLaborDefOf.RP_Kitchen, RPDefOf.RP_Shower, RPDefOf.RP_Yard };

        private static bool MealsOnCounters(Map map) => PrisonMap.Of(map) is { } pm && PrisonServices.MealsInCanteen(map, pm) > 0;

        /// <summary>Hungry inmates, and what to look at: the kitchen when the counters are bare, the way to the counters when they are not.</summary>
        public static TaggedString HungryLine(Map map, int hungry)
        {
            int meals = PrisonMap.Of(map) is { } pm ? PrisonServices.MealsInCanteen(map, pm) : 0;
            return meals > 0 ? "RP_Problem_HungryMealsThere".Translate(hungry, meals) : "RP_Problem_Hungry".Translate(hungry);
        }
    }
}
