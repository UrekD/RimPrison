using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// K9 unit: a dog handler (a guard) comes with a trained dog that sticks to them. The dog smells drugs on inmates it
    /// passes and fresh digging in cells, flags them for a search, and defends its handler. Dogs eat off-site like staff.
    /// </summary>
    public static class K9Utility
    {
        public const float SniffRadius = 3.9f;
        public const float DigSniffRadius = 5.9f;

        private static readonly AccessTools.FieldRef<Pawn_TrainingTracker, int> CountDecayFrom = AccessTools.FieldRefAccess<Pawn_TrainingTracker, int>("countDecayFrom");

        /// <summary>A K9 dog carries the K9 marker hediff (independent of whether its handler is still around).</summary>
        public static bool IsK9(Pawn animal) => animal.RaceProps.Animal && animal.health?.hediffSet.HasHediff(RPK9DefOf.RP_K9) == true;

        /// <summary>On hiring a handler (or retraining into the role): issue their dog.</summary>
        public static void IssueDog(Pawn handler, CompStaff sc)
        {
            if (sc.role?.companion == null || !handler.Spawned) return;
            Pawn dog = PawnGenerator.GeneratePawn(new PawnGenerationRequest(sc.role.companion, Faction.OfPlayer, forceGenerateNewPawn: true));
            dog.health.AddHediff(RPK9DefOf.RP_K9);
            sc.dog = dog;
            GenSpawn.Spawn(dog, CellFinder.StandableCellNear(handler.Position, handler.Map, 2f), handler.Map);
            KeepTrained(dog, handler);
            dog.needs?.AddOrRemoveNeedsAsAppropriate();
        }

        /// <summary>The handler keeps the dog sharp: training never decays, obedience points at the handler.</summary>
        public static void KeepTrained(Pawn dog, Pawn handler)
        {
            if (dog.training == null) return;
            CountDecayFrom(dog.training) = Find.TickManager.TicksGame;
            if (!dog.training.HasLearned(TrainableDefOf.Tameness)) dog.training.Train(TrainableDefOf.Tameness, handler, complete: true);
            if (!dog.training.HasLearned(TrainableDefOf.Obedience)) dog.training.Train(TrainableDefOf.Obedience, handler, complete: true);
            if (dog.playerSettings.Master != handler) dog.playerSettings.Master = handler;
        }

        /// <summary>The handler leaves (fired, retrained, dead): the dog goes back to the kennel.</summary>
        public static void Notify_HandlerGone(CompStaff sc)
        {
            Pawn dog = sc.dog;
            sc.dog = null;
            if (dog != null && !dog.Destroyed) dog.Destroy();
        }

        /// <summary>Every 250 ticks per dog: noses on inmates nearby.</summary>
        public static void Sniff(Pawn dog, Pawn handler)
        {
            if (dog.Downed || !dog.Awake()) return;
            Map map = dog.Map;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (!p.Position.InHorDistOf(dog.Position, DigSniffRadius)) continue;
                CompInmate c = p.Inmate();
                if (c == null || !c.activeFlag) continue;
                // A drug stash: the dog sits by the bunk.
                if (!c.cellSearchPending && StashUtility.Has(c, ContrabandKind.Drug) && InmateUtility.CellOf(p) is { } stashCell && dog.GetRoom() == stashCell && Rand.Chance(0.3f))
                {
                    c.cellSearchPending = true;
                    PrisonGame.Instance.stats.dogAlerts++;
                    Messages.Message("RP_DogStashAlert".Translate(dog.LabelShort, p.LabelShort), p, MessageTypeDefOf.CautionInput, historical: false);
                }
                // Fresh digging: the dog paws at the cell.
                if (c.tunnelProgress > 0.15f && !c.tunnelKnown && !c.cellSearchPending && p.GetRoom() == dog.GetRoom() && Rand.Chance(0.25f))
                {
                    c.cellSearchPending = true;
                    PrisonGame.Instance.stats.dogAlerts++;
                    Messages.Message("RP_DogDigAlert".Translate(dog.LabelShort, p.LabelShort), p, MessageTypeDefOf.CautionInput, historical: false);
                }
                if (c.suspected || !p.Position.InHorDistOf(dog.Position, SniffRadius) || !CarriesDrugs(p) || !Rand.Chance(0.6f)) continue;
                c.suspected = true;
                PrisonGame.Instance.stats.dogAlerts++;
                Messages.Message("RP_DogAlert".Translate(dog.LabelShort, p.LabelShort), p, MessageTypeDefOf.CautionInput, historical: false);
            }
        }

        private static bool CarriesDrugs(Pawn p)
        {
            if (p.inventory == null) return false;
            foreach (Thing t in p.inventory.innerContainer)
                if (ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Drug }) return true;
            return false;
        }

        private static readonly List<Pawn> tmpDogs = new List<Pawn>();

        /// <summary>Every 250 ticks from the map: sniff and keep trained; dogs whose handler is gone go home; a
        /// handler whose dog died gets a new one from the kennel a day later.</summary>
        public static void Tick(Map map)
        {
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < colonists.Count; i++)
            {
                if (colonists[i].GetComp<CompStaff>() is not { role.companion: not null } sc) continue;
                // Dogs from saves before the K9 marker existed.
                if (sc.dog is { Dead: false, Destroyed: false } old && !IsK9(old)) old.health.AddHediff(RPK9DefOf.RP_K9);
                if (sc.dog is { } d && (d.Dead || d.Destroyed))
                {
                    sc.dog = null;
                    sc.dogReplaceTick = now + GenDate.TicksPerDay;
                }
                if (sc.dog == null && now >= sc.dogReplaceTick) IssueDog(colonists[i], sc);
            }
            tmpDogs.Clear();
            foreach (Pawn a in map.mapPawns.SpawnedColonyAnimals)
                if (IsK9(a)) tmpDogs.Add(a);
            for (int i = 0; i < tmpDogs.Count; i++)
            {
                Pawn dog = tmpDogs[i];
                Pawn h = dog.playerSettings?.Master;
                CompStaff sc = h?.GetComp<CompStaff>();
                if (h == null || h.Dead || h.Destroyed || sc == null || sc.dog != dog || sc.role?.companion == null)
                {
                    if (sc != null && sc.dog == dog) sc.dog = null;
                    if (!dog.Destroyed) dog.Destroy();
                    continue;
                }
                if (!h.Spawned || h.Map != map) continue;
                KeepTrained(dog, h);
                Sniff(dog, h);
            }
        }
    }

    /// <summary>K9 dogs always stay with their handler (vanilla only follows drafted or fieldworking masters).</summary>
    [HarmonyPatch(typeof(ThinkNode_ConditionalShouldFollowMaster), nameof(ThinkNode_ConditionalShouldFollowMaster.ShouldFollowMaster))]
    public static class Patch_ShouldFollowMaster_K9
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result || !pawn.Spawned || !K9Utility.IsK9(pawn)) return;
            Pawn h = pawn.playerSettings.Master;
            if (h == null || pawn.needs?.rest is { CurLevel: < 0.15f }) return;
            __result = h.Spawned && h.Map == pawn.Map && pawn.CanReach(h, Verse.AI.PathEndMode.OnCell, Danger.Deadly);
        }
    }

    /// <summary>K9 dogs are trained to bite and hold: on inmates their attacks bruise rather than tear.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PreApplyDamage))]
    public static class Patch_Pawn_PreApplyDamage_K9
    {
        public static void Prefix(Pawn __instance, ref DamageInfo dinfo)
        {
            if (dinfo.Instigator is not Pawn dog || !dog.RaceProps.Animal || !__instance.IsPrisonerOfColony || !K9Utility.IsK9(dog)) return;
            dinfo.Def = DamageDefOf.Blunt;
            dinfo.SetAmount(dinfo.Amount * 0.6f);
        }
    }

    [DefOf]
    public static class RPK9DefOf
    {
        public static HediffDef RP_K9;

        static RPK9DefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPK9DefOf));
    }
}
