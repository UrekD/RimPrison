using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Phone calls: inmates who want something brought in arrange it over the phone, which makes their next visitor a
    /// smuggler. With the phone-tap project done and monitoring switched on, calls on the payphones are listened to:
    /// arranged handovers get the next visit flagged (thorough search), and loose talk about stashes, tunnels and gangs
    /// turns into intel. Smuggled cellphones call out from the cell, where nobody listens.
    /// </summary>
    public static class PhoneCalls
    {
        public const string TapFeature = "PhoneTaps";
        /// <summary>Daily cost per monitored payphone (an officer with headphones).</summary>
        public const int TapCostPerPhone = 30;
        /// <summary>Monitoring can't catch every call.</summary>
        public const float ListenChance = 0.6f;

        public static bool TapsActive(PrisonGame game) => game.phoneTaps && Unlocks.Feature(TapFeature);

        /// <summary>Wants something brought in: dealing, digging, angry enough to want a weapon.</summary>
        public static bool WantsDelivery(CompInmate c) =>
            c.familyCount > 0 && (c.tension >= 40f || c.tunnelProgress > 0f || c.HasReputation(ReputationDefOf.RP_EscapeArtist)
                                  || c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer)));

        /// <summary>A call ended (payphone: <paramref name="tappable"/>; cellphone: not).</summary>
        public static void Notify_Call(Pawn p, CompInmate c, bool tappable)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return;
            game.stats.phoneCalls++;
            bool arranged = !c.smuggleArranged && WantsDelivery(c) && Rand.Chance(0.35f);
            if (arranged) c.smuggleArranged = true;
            if (!tappable || !TapsActive(game) || !Rand.Chance(ListenChance)) return;
            string key = null;
            if (arranged)
            {
                c.nextVisitFlagged = true;
                CorruptionSystem.Notify_TappedArrangement(p, c);
                key = "RP_TapSmuggle";
            }
            else if (c.stash.Count > 0 && !c.cellSearchPending && Rand.Chance(0.3f))
            {
                c.cellSearchPending = true;
                key = "RP_TapStash";
            }
            else if (c.tunnelProgress > 0.1f && !c.tunnelKnown && !c.cellSearchPending && Rand.Chance(0.3f))
            {
                c.cellSearchPending = true;
                key = "RP_TapTunnel";
            }
            else if (c.gang != null && !c.gangKnown && Rand.Chance(0.15f))
            {
                c.gangKnown = true;
                key = "RP_TapGang";
            }
            if (key == null) return;
            game.stats.phoneTapIntel++;
            Messages.Message(key.Translate(p.LabelShort), p, MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <summary>Rare tick, own cell, unwatched: a smuggled cellphone keeps them in touch (and off the taps).</summary>
        public static void TryCellphoneCall(Pawn p, CompInmate c)
        {
            if (c.familyCount <= 0 || !Rand.Chance(0.03f)) return;
            Need family = p.needs.TryGetNeed(RPVisitDefOf.RP_Family);
            if (family == null || family.CurLevel > 0.6f) return;
            if (!StashUtility.Has(c, ContrabandKind.Phone) && !CarriesPhone(p)) return;
            family.CurLevel = Mathf.Min(1f, family.CurLevel + 0.15f);
            Notify_Call(p, c, tappable: false);
        }

        private static bool CarriesPhone(Pawn p)
        {
            if (p.inventory == null) return false;
            foreach (Thing t in p.inventory.innerContainer)
                if (ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Phone }) return true;
            return false;
        }

        public static int MonitoredPhones(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            int n = 0;
            foreach (CompFixture f in pm.Fixtures)
                if (f.Props.need == RPVisitDefOf.RP_Family) n++;
            return n;
        }
    }
}
