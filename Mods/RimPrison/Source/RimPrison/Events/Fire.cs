using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// A kitchen fire: a pan left on, fat in the burner. The staff fight it like any colonist would; call the fire
    /// brigade (paid) and every fire on the grounds is out by the next hour, whatever else is going on.
    /// </summary>
    public class IncidentWorker_KitchenFire : IncidentWorker_Prison
    {
        public const int BrigadeCost = 400;

        public static IEnumerable<Building> KitchenStoves(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) yield break;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
                if (InmateJobWorker_Kitchen.IsStove(b) && pm.zones.ZoneAt(b.Position) == RPLaborDefOf.RP_Kitchen) yield return b;
        }

        /// <summary>Not while the brigade is on its way or the kitchen is already burning.</summary>
        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && PrisonGame.Instance.story.fireBrigadeTick < 0 && KitchenStoves((Map)parms.target).Any()
            && !KitchenBurning((Map)parms.target);

        public static bool KitchenBurning(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            return pm != null && map.listerThings.ThingsOfDef(ThingDefOf.Fire).Any(f => pm.zones.ZoneAt(f.Position) == RPLaborDefOf.RP_Kitchen);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Building stove = KitchenStoves(map).RandomElementWithFallback();
            if (stove == null) return false;
            // On the stove's own cells or right beside it (nothing to burn there, no fire: no event).
            IntVec3 cell = IntVec3.Invalid;
            foreach (IntVec3 c in GenAdj.CellsOccupiedBy(stove).Concat(GenAdj.CellsAdjacent8Way(stove)).Where(c => c.InBounds(map)).InRandomOrder())
                if (FireUtility.TryStartFireIn(c, map, 0.6f, null))
                {
                    cell = c;
                    break;
                }
            if (!cell.IsValid) return false;
            PrisonGame.Instance.stats.fires++;
            bool inmateCooks = KitchenShift.InmatesCooking(map);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_KitchenFire", "RP_KitchenFireLabel".Translate(),
                "RP_KitchenFireText".Translate((inmateCooks ? "RP_KitchenFireInmateCook" : "RP_KitchenFireStaffCook").Translate(), Money.Format(BrigadeCost)),
                map, new TargetInfo(cell, map));
            letter.StartTimeout(GenDate.TicksPerHour);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_KitchenFire".Translate());
            return true;
        }
    }

    public static class FireBrigade
    {
        /// <summary>Hourly: the brigade, called since the last hour, arrives and puts out every fire on the grounds.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            if (game.story.fireBrigadeTick < 0 || Find.TickManager.TicksGame < game.story.fireBrigadeTick) return;
            game.story.fireBrigadeTick = -1;
            int fires = PutOut(map);
            Messages.Message("RP_FireBrigadeDone".Translate(fires), MessageTypeDefOf.NeutralEvent);
        }

        public static int PutOut(Map map)
        {
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).ToList();
            foreach (Thing f in fires) f.Destroy();
            return fires.Count;
        }
    }

    public class KitchenFireDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_KitchenFire_Brigade", "RP_KitchenFire_Staff" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 0) return null;
            if ((l.map ?? Find.AnyPlayerHomeMap)?.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0) return "RP_FireBrigadePointless".Translate();
            return PrisonGame.Instance.ledger.funds < IncidentWorker_KitchenFire.BrigadeCost
                ? "RP_CannotAfford".Translate(Money.Format(IncidentWorker_KitchenFire.BrigadeCost)).ToString() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option != 0 || map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0) return;
            PrisonGame game = PrisonGame.Instance;
            if (!game.ledger.TrySpend(RPDefOf.RP_Supplies, IncidentWorker_KitchenFire.BrigadeCost, "RP_FireBrigadeNote".Translate()))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(IncidentWorker_KitchenFire.BrigadeCost)), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            game.story.fireBrigadeTick = Find.TickManager.TicksGame; // they arrive at the next hour
        }
    }
}
