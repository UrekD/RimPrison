using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>A prison gang. Members back each other up; rival gangs in the same room raise tension.</summary>
    public class GangDef : Def
    {
        public Color color = Color.white;
        public List<GangDef> rivals = new List<GangDef>();
        public List<CrimeDef> typicalCrimes = new List<CrimeDef>();
        public float commonality = 1f;
        /// <summary>The chance a member carries the gang's mark from intake: a search or a frisk shows it (see <see cref="GangUtility.Notify_Frisked"/>).</summary>
        public float tattooChance = 0.4f;
    }

    public static class GangUtility
    {
        /// <summary>The warden knows both inmates' gangs (a gang label may be shown about the pair).</summary>
        public static bool BothKnown(Pawn a, Pawn b) => a.Inmate() is { gangKnown: true } && b.Inmate() is { gangKnown: true };

        public static bool AreRivals(GangDef a, GangDef b) =>
            a != null && b != null && a != b && (a.rivals.Contains(b) || b.rivals.Contains(a)) && !GangTruces.InTruce(a, b);

        /// <summary>The gang has a leader among the inmates (not counting <paramref name="except"/>, nor anyone on the way out of the prison: a leader waiting for the shuttle does not hold the post).</summary>
        public static bool HasLeader(Map map, GangDef gang, Pawn except)
        {
            // (A plain loop: it is asked for every new inmate, and a lambda over a hundred prisoners allocates and calls through a delegate for each.)
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
                if (prisoners[i] != except && prisoners[i].Inmate() is { gangLeader: true } oc && oc.gang == gang && !ShuttleSystem.OnTheWayOut(prisoners[i])) return true;
            return false;
        }

        /// <summary>Gang membership at intake: more likely for higher security, biased by crime.</summary>
        public static void RollAtIntake(Pawn p, CompInmate c)
        {
            float chance = Mathf.Lerp(0.1f, 0.5f, Mathf.InverseLerp(0.5f, 2f, c.security?.dangerFactor ?? 1f));
            if (!Rand.Chance(chance)) return;
            if (!DefDatabase<GangDef>.AllDefsListForReading.TryRandomElementByWeight(g => g.commonality * (c.crime != null && g.typicalCrimes.Contains(c.crime) ? 3f : 1f), out GangDef gang)) return;
            c.gang = gang;
            c.gangTattoo = Rand.Chance(gang.tattooChance);
            // One leader per gang: a new member only leads a gang with no leader inside, and none being fought over.
            Map map = p.MapHeld ?? Find.AnyPlayerHomeMap;
            bool led = map != null && (HasLeader(map, gang, p) || GangRanks.Contested(gang));
            GangRanks.SetRank(c, !led && Rand.Chance(0.15f) ? GangRank.Leader : GangRank.Member);
        }

        /// <summary>
        /// A holding search or a frisk is over: the mark of a gang they carry shows (the warden learns the gang, with a line that says how).
        /// Returns whether it gave a gang away.
        /// </summary>
        public static bool Notify_Frisked(Pawn p, CompInmate c)
        {
            if (c?.gang == null || !c.gangTattoo || c.gangKnown) return false;
            Reveal(c, "RP_Chr_GangTattoo");
            Messages.Message("RP_GangTattooFound".Translate(p.LabelShort, c.gang.label), p, MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        /// <summary>
        /// Hourly: tension from rival gang members nearby and from standing on a rival's turf (<see cref="GangTurf.TensionTerm"/>, never more than
        /// one rival member's worth), comfort from your own gang nearby and on your own turf. <paramref name="turfCause"/> is how to name the
        /// turf tension (null when there is none).
        /// </summary>
        public static float TensionFromGangs(Pawn p, CompInmate c, PrisonMap pm, out bool rivalsNear, out string turfCause)
        {
            rivalsNear = false;
            turfCause = null;
            // An ex-member has no gang, but the one they left is out to get them: its members near them count as rivals, and turf of theirs is rival turf.
            GangDef hated = c.gang == null && GangExit.ExActive(c) ? c.exGang : null;
            if (c.gang == null && hated == null) return 0f;
            int rivals = 0, friends = 0;
            IReadOnlyList<Pawn> inmates = p.Map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                if (o == p || !o.Position.InHorDistOf(p.Position, 10f)) continue;
                GangDef og = o.Inmate()?.gang;
                if (og == null) continue;
                if (og == c.gang) friends++;
                else if (hated != null ? og == hated : AreRivals(og, c.gang)) rivals++;
                // (Four rivals and three friends are all that count below.)
                if (rivals >= 4 && friends >= 3) break;
            }
            // (The gang an ex-member left is no rival: its members are "trouble on the wing", never named.)
            rivalsNear = hated == null && rivals > 0;
            float turf = GangTurf.TensionTerm(p, c, pm, out turfCause);
            if (hated != null && rivals > 0) turfCause ??= "RP_Cause_WingTrouble";
            // Another gang's deal with the warden: its rivals' members on that wing are wound up by it ("the Ravens run the yard").
            float deal = GangDeals.RivalTension(p, c, pm, out string dealCause);
            if (deal > 0f && !rivalsNear) turfCause ??= dealCause;
            return Mathf.Min(4, rivals) * 1.2f - Mathf.Min(3, friends) * 0.4f + turf + deal;
        }

        /// <summary>Hourly: gang leaders recruit tense, unaffiliated inmates around them.</summary>
        public static void TryRecruit(Pawn leader, CompInmate lc)
        {
            if (!lc.gangLeader || lc.gang == null || GangDeals.NoRecruiting(lc.gang)) return;
            if (!Rand.Chance(GangTurf.OnOwnTurf(leader, lc, PrisonMap.Of(leader.Map)) ? GangTurf.RecruitChanceOwnTurf : GangTurf.RecruitChance)) return;
            IReadOnlyList<Pawn> inmates = leader.Map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                CompInmate oc = o.Inmate();
                if (o == leader || oc == null || oc.gang != null || oc.tension < 40f || !o.Position.InHorDistOf(leader.Position, 8f)) continue;
                // (Not one who walked out of this gang: it hates them, and would turn on its own member.)
                if (GangExit.ExActive(oc) && oc.exGang == lc.gang) continue;
                if (o.relations.OpinionOf(leader) < 0) continue;
                oc.gang = lc.gang;
                PrisonGame.Instance.stats.gangRecruits++;
                if (lc.gangKnown)
                {
                    Messages.Message("RP_GangRecruited".Translate(o.LabelShort, lc.gang.label), o, MessageTypeDefOf.NegativeEvent, historical: false);
                    Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_GangRecruited".Translate(o.LabelShort, lc.gang.label), o, leader);
                }
                return;
            }
        }

        /// <summary>Gang affiliation becomes known to the warden (caught acting together, informants).</summary>
        public static void Reveal(CompInmate c, string lineKey = "RP_Chr_GangRevealed")
        {
            if (c.gang == null || c.gangKnown) return;
            c.gangKnown = true;
            if (c.parent is Pawn p) Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, lineKey.Translate(p.LabelShort, c.gang.label), p);
        }
    }

    /// <summary>Opinion between inmates of the same gang (only while both are inmates).</summary>
    public class ThoughtWorker_SameGang : ThoughtWorker
    {
        protected override ThoughtState CurrentSocialStateInternal(Pawn p, Pawn other)
        {
            if (!p.IsPrisonerOfColony || !other.IsPrisonerOfColony) return false;
            GangDef a = p.Inmate()?.gang;
            if (a == null || a != other.Inmate()?.gang) return false;
            return ThoughtState.ActiveAtStage(GangUtility.BothKnown(p, other) ? 1 : 0);
        }
    }

    /// <summary>Opinion between inmates of rival gangs.</summary>
    public class ThoughtWorker_RivalGang : ThoughtWorker
    {
        protected override ThoughtState CurrentSocialStateInternal(Pawn p, Pawn other) =>
            p.IsPrisonerOfColony && other.IsPrisonerOfColony && GangUtility.AreRivals(p.Inmate()?.gang, other.Inmate()?.gang)
                ? ThoughtState.ActiveAtStage(GangUtility.BothKnown(p, other) ? 1 : 0) : false;
    }

    /// <summary>Two gangs go to war: everyone in both gets wound up, and a war arc plays it out (see <see cref="GangWarArc"/>).</summary>
    public class IncidentWorker_GangWar : IncidentWorker_Prison
    {
        protected override int MinInmates => 4;

        protected override bool CanFireNowSub(IncidentParms parms) => base.CanFireNowSub(parms) && FindPair((Map)parms.target, out _, out _);

        private static bool FindPair(Map map, out GangDef a, out GangDef b)
        {
            a = b = null;
            var present = new HashSet<GangDef>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate()?.gang is { } g) present.Add(g);
            // In def order (a HashSet's order follows object hashes and changes from run to run).
            foreach (GangDef g in DefDatabase<GangDef>.AllDefsListForReading)
            {
                if (!present.Contains(g)) continue;
                foreach (GangDef r in g.rivals)
                    if (present.Contains(r) && !GangTruces.InTruce(g, r) && GangWarArc.CanStart(map, g, r))
                    {
                        a = g;
                        b = r;
                        return true;
                    }
            }
            return false;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            if (!FindPair(map, out GangDef a, out GangDef b)) return false;
            StoryArc war = GangWarArc.Start(map, a, b);
            if (war == null) return false;
            var members = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c?.gang != a && c?.gang != b) continue;
                c.tension = Mathf.Min(100f, c.tension + 25f);
                c.mainCause = "RP_Cause_GangWar";
                GangUtility.Reveal(c);
                members.Add(p);
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangWar".Translate(a.label, b.label, members.Count));
            ((GangWarArc)war.def.Worker).Declare(war, map, members.Count); // the war's only letter
            return true;
        }
    }
}
