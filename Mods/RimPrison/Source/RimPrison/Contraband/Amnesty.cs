using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// A contraband amnesty (the warden's call, at most every ten days): for a day, inmates may hand in contraband with
    /// no questions asked. The calmer ones do, once each (what they carry and what is hidden in their cell), and feel a
    /// weight off; dealers, extortionists and the angry keep theirs. What they hand in is gone with its story: no tip
    /// left pending on it, no guard traced through it. The state sees a soft touch (reputation -1). The next one can be
    /// declared ten days after this one ends.
    /// </summary>
    public static class Amnesty
    {
        public const int Hours = 24, CooldownDays = 10;
        public const float MaxTension = 50f, Relief = 5f, ReputationCost = 1f;
        private static readonly List<Thing> tmp = new List<Thing>();

        public static bool Active(PrisonGame game) => game.amnestyUntilTick > Find.TickManager.TicksGame;

        public static float DaysUntilReady(PrisonGame game) =>
            Mathf.Max(0f, (game.lastAmnestyTick + Hours * GenDate.TicksPerHour + CooldownDays * GenDate.TicksPerDay - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay);

        public static void Declare(PrisonGame game)
        {
            if (Active(game) || DaysUntilReady(game) > 0f) return;
            int now = Find.TickManager.TicksGame;
            game.amnestyUntilTick = now + Hours * GenDate.TicksPerHour;
            game.lastAmnestyTick = now;
            game.amnestyHandedIn = 0;
            game.reputation = Mathf.Max(0f, game.reputation - ReputationCost);
            Messages.Message("RP_AmnestyDeclared".Translate(Hours), MessageTypeDefOf.NeutralEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_AmnestyDeclared".Translate());
        }

        /// <summary>Who will hand things in: calm enough, not in the trade, not already done this time.</summary>
        public static bool Willing(Pawn p, CompInmate c, PrisonGame game) =>
            InmateUtility.IsActiveInmate(p) && !p.InMentalState && !p.Downed && c.tension < MaxTension && c.amnestyHandedTick < game.lastAmnestyTick
            && !c.HasReputation(RPReputationDefOf.RP_Extortionist) && !c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer));

        /// <summary>Hourly: hand-ins while it lasts; the tally when it ends.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            if (game.amnestyUntilTick < 0) return;
            if (!Active(game))
            {
                game.amnestyUntilTick = -1;
                Messages.Message("RP_AmnestyOver".Translate(game.amnestyHandedIn), MessageTypeDefOf.NeutralEvent);
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_AmnestyOver".Translate(game.amnestyHandedIn));
                return;
            }
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { } c || !Willing(p, c, game)) continue;
                int n = HandIn(p, c);
                if (n == 0) continue;
                c.amnestyHandedTick = Find.TickManager.TicksGame;
                game.amnestyHandedIn += n;
                game.stats.amnestyItems += n;
                c.tension = Mathf.Max(0f, c.tension - Relief);
            }
        }

        /// <summary>Everything they carry and everything in their stash goes in the box.</summary>
        public static int HandIn(Pawn p, CompInmate c)
        {
            ContrabandUtility.CarriedContraband(p, tmp);
            int n = 0;
            foreach (Thing t in tmp)
            {
                n += t.stackCount;
                if (t.ParentHolder is Pawn_EquipmentTracker eq) eq.Remove((ThingWithComps)t);
                else p.inventory.innerContainer.Remove(t);
                ToolCount.Notify_Taken(t, ToolSource.Amnesty);
                t.Destroy();
            }
            tmp.Clear();
            tmp.AddRange(c.stash);
            foreach (Thing t in tmp)
            {
                n += t.stackCount;
                c.stash.Remove(t);
                ToolCount.Notify_Taken(t, ToolSource.Amnesty);
                t.Destroy();
            }
            tmp.Clear();
            if (n > 0)
            {
                // Nothing left for a tip to find (a tunnel tip still stands), and nothing to trace back to a guard.
                c.suspected = false;
                c.informedBy = null;
                if (c.tunnelProgress <= 0.05f) c.cellSearchPending = false;
                c.smuggledBy = null;
            }
            return n;
        }
    }
}
