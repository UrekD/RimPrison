using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPRelationDefOf
    {
        public static PawnRelationDef RP_Enemy;
        public static PawnRelationDef RP_Friend;
        public static StoryArcDef RP_Arc_Feud;
        public static ThoughtDef RP_MadePeace;
        public static ThoughtDef RP_FriendReleased;
        public static ThoughtDef RP_CellRequestRefused;
        public static ThoughtDef RP_FriendSteppedIn;
        public static ThoughtDef RP_FriendDied;

        static RPRelationDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRelationDefOf));
    }

    /// <summary>
    /// Lasting ties between inmates, as vanilla relations (Social tab, opinion): enemies (a feud) and friends. Most of
    /// the consequences come for free through opinion, which fight targeting, cellmate tension and fear already read;
    /// friends also step into each other's fights, calm each other's fear and miss each other when one leaves.
    /// </summary>
    public static class PrisonRelations
    {
        public static bool Friends(Pawn a, Pawn b) => a.relations != null && a.relations.DirectRelationExists(RPRelationDefOf.RP_Friend, b);

        public static bool Enemies(Pawn a, Pawn b) => a.relations != null && a.relations.DirectRelationExists(RPRelationDefOf.RP_Enemy, b);

        public static void MakeEnemies(Pawn a, Pawn b)
        {
            if (a.relations == null || b.relations == null || Enemies(a, b)) return;
            if (Friends(a, b)) a.relations.RemoveDirectRelation(RPRelationDefOf.RP_Friend, b);
            a.relations.AddDirectRelation(RPRelationDefOf.RP_Enemy, b);
        }

        private static bool inFight;

        /// <summary>A fight between two inmates (ours or vanilla's insult-turned-fight): friends of the one attacked
        /// step in; it may start a feud.</summary>
        public static void Notify_Fight(Pawn attacker, Pawn target)
        {
            if (inFight || target.Inmate() == null || attacker.Inmate() == null || !attacker.IsPrisonerOfColony || !target.IsPrisonerOfColony) return;
            inFight = true;
            try
            {
                GangTruces.Notify_Fight(attacker, target);
                Respond(attacker, target);
            }
            finally
            {
                inFight = false;
            }
        }

        private static void Respond(Pawn attacker, Pawn target)
        {
            IReadOnlyList<Pawn> inmates = target.Map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn f = inmates[i];
                if (f == target || f == attacker || !f.Awake() || f.InMentalState || f.Downed || !f.Position.InHorDistOf(target.Position, 6f)) continue;
                if (!Friends(f, target) || f.WorkTagIsDisabled(WorkTags.Violent)) continue;
                // A targeted attack on the aggressor (a vanilla social fight would end at once: the attacker is
                // already fighting someone else).
                if (!f.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: attacker)) continue;
                target.needs.mood?.thoughts.memories.TryGainMemory(RPRelationDefOf.RP_FriendSteppedIn, f);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FriendSteppedIn".Translate(f.LabelShort, target.LabelShort, attacker.LabelShort), f, target, attacker);
                break;
            }
            // Already enemies: the enmity just goes on (no new letter for the same pair).
            if (!Enemies(attacker, target) && Rand.Chance(0.5f)) FeudArc.Begin(attacker, target, "RP_FeudCause_Fight".Translate(attacker.LabelShort));
        }

        /// <summary>Fear: friends around make an inmate feel safer.</summary>
        public static int FriendsNear(Pawn p, float radius)
        {
            if (p.relations == null) return 0;
            int n = 0;
            foreach (DirectPawnRelation r in p.relations.DirectRelations)
                if (r.def == RPRelationDefOf.RP_Friend && r.otherPawn.Spawned && r.otherPawn.Map == p.Map && r.otherPawn.Position.InHorDistOf(p.Position, radius)) n++;
            return n;
        }

        /// <summary>Released or transferred: their friends inside feel it.</summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p.relations == null) return;
            foreach (DirectPawnRelation r in p.relations.DirectRelations)
                if (r.def == RPRelationDefOf.RP_Friend && r.otherPawn.Spawned && r.otherPawn.IsPrisonerOfColony)
                    r.otherPawn.needs.mood?.thoughts.memories.TryGainMemory(RPRelationDefOf.RP_FriendReleased, p);
        }

        /// <summary>
        /// Daily: friendships grow between people who spend their days together (cellmates, the same job, the same
        /// gang) and already like each other. A handful of tries per day, not a pair scan.
        /// </summary>
        public static void Daily(Map map)
        {
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            if (inmates.Count < 2) return;
            int tries = Mathf.Clamp(inmates.Count / 4, 1, 12);
            for (int t = 0; t < tries; t++)
            {
                Pawn a = inmates[Rand.Range(0, inmates.Count)];
                CompInmate ac = a.Inmate();
                if (ac == null || !ac.activeFlag || a.relations == null) continue;
                Pawn best = null;
                float bestOpinion = 4f;
                for (int i = 0; i < inmates.Count; i++)
                {
                    Pawn b = inmates[i];
                    if (b == a || b.Inmate() is not { activeFlag: true } bc || !TogetherDaily(a, ac, b, bc)) continue;
                    if (Friends(a, b) || Enemies(a, b)) continue;
                    float opinion = Mathf.Min(a.relations.OpinionOf(b), b.relations.OpinionOf(a));
                    if (opinion <= bestOpinion) continue;
                    bestOpinion = opinion;
                    best = b;
                }
                // Liking each other is a start; the more they do, the likelier it sticks.
                if (best == null || !Rand.Chance(Mathf.Clamp01(0.2f + bestOpinion / 100f))) continue;
                a.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, best);
                PrisonGame.Instance.stats.friendships++;
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_Friends".Translate(a.LabelShort, best.LabelShort), a, best);
            }
        }

        private static bool TogetherDaily(Pawn a, CompInmate ac, Pawn b, CompInmate bc) =>
            (InmateUtility.CellOf(a) is { } cell && cell == InmateUtility.CellOf(b))
            || (ac.job != null && ac.job == bc.job)
            || (ac.gang != null && ac.gang == bc.gang);
    }

    /// <summary>
    /// A feud between two inmates, from a fight or a grudge: they are enemies now, it simmers (tension), and unless
    /// the warden steps in it comes to a head in a day or three. Separate them (a bed in another cell), pay for
    /// mediation (a psychologist or chaplain makes it likely to work), or let them settle it.
    /// </summary>
    public class FeudArc : StoryArcWorker
    {
        public const int MediationCost = 150;
        private const int Simmer = 0, Separated = 1;
        private static readonly string[] Keys = { "RP_Feud_Settle", "RP_Feud_Separate", "RP_Feud_Mediate" };

        /// <summary>Starts a feud between two active inmates who actually meet (same wing or cell).</summary>
        public static StoryArc Begin(Pawn a, Pawn b, TaggedString cause)
        {
            if (!InmateUtility.IsActiveInmate(a) || !InmateUtility.IsActiveInmate(b) || a.Map != b.Map) return null;
            StoryArc arc = PrisonGame.Instance?.story.TryStartArc(RPRelationDefOf.RP_Arc_Feud, a, b);
            if (arc == null) return null;
            PrisonRelations.MakeEnemies(a, b);
            arc.note = cause.Resolve();
            arc.nextTick = Find.TickManager.TicksGame + Rand.RangeInclusive(24, 72) * GenDate.TicksPerHour;
            PrisonGame.Instance.stats.feuds++;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_FeudStarted".Translate(a.LabelShort, b.LabelShort, arc.note), a, b);
            ((FeudArc)arc.def.Worker).AskWarden(arc, a.Map, "RP_FeudLabel".Translate(a.LabelShort, b.LabelShort),
                "RP_FeudText".Translate(a.LabelShort, b.LabelShort, arc.note, Money.Format(MediationCost), MediatorLine(a.Map)));
            return arc;
        }

        private static string MediatorLine(Map map) =>
            Mediator(map) is { } m ? "RP_FeudMediator".Translate(m.LabelShort).ToString() : "RP_FeudNoMediator".Translate().ToString();

        /// <summary>A psychologist (best) or chaplain on the staff.</summary>
        private static Pawn Mediator(Map map)
        {
            Pawn chaplain = null;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                StaffRoleDef role = p.GetComp<CompStaff>()?.role;
                if (role == RPStaffRoleDefOf.RP_Psychologist) return p;
                if (role == RPStaffRoleDefOf.RP_Chaplain) chaplain = p;
            }
            return chaplain;
        }

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override string Disabled(StoryArc arc, int option)
        {
            if (option == 1 && FreeBedAway(arc) == null) return "RP_FeudNoBed".Translate();
            if (option == 2 && PrisonGame.Instance.ledger.funds < MediationCost) return "RP_CannotAfford".Translate(Money.Format(MediationCost));
            return null;
        }

        /// <summary>A free bed for B in their own wing, away from A's cell (and not their current cell).</summary>
        private static Building_Bed FreeBedAway(StoryArc arc)
        {
            Pawn a = arc.A, b = arc.B;
            if (a == null || b == null || !b.Spawned || b.Inmate()?.security == null) return null;
            Room aCell = Cellmates.Home(a, a.Inmate()), bCell = Cellmates.Home(b, b.Inmate());
            return Cellmates.BestFree(b, b.Inmate(), allowed: bed => bed.GetRoom() is var r && r != aCell && r != bCell);
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            Pawn a = arc.A, b = arc.B;
            switch (option)
            {
                case 0:
                    // Letting them settle it is the warden's choice too: the daily review does not split them behind their back.
                    Cellmates.PlacedByWarden(a.Inmate());
                    Cellmates.PlacedByWarden(b.Inmate());
                    break;
                case 1:
                    CompInmate bc = b.Inmate();
                    if (FreeBedAway(arc) is not { } bed || !Cellmates.Move(b, bc, bed))
                    {
                        Messages.Message("RP_FeudNoBed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                        return;
                    }
                    Cellmates.PlacedByWarden(bc);
                    arc.stage = Separated;
                    arc.nextTick = Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay;
                    Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FeudSeparated".Translate(a.LabelShort, b.LabelShort), a, b);
                    break;
                case 2:
                    if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Wages, MediationCost, "RP_MediationNote".Translate(a.LabelShort, b.LabelShort))) return;
                    Cellmates.PlacedByWarden(a.Inmate());
                    Cellmates.PlacedByWarden(b.Inmate());
                    Pawn mediator = Mediator(map);
                    float chance = mediator == null ? 0.25f : mediator.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Psychologist ? 0.7f : 0.55f;
                    if (Rand.Chance(chance))
                    {
                        a.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, b);
                        a.needs.mood?.thoughts.memories.TryGainMemory(RPRelationDefOf.RP_MadePeace, b);
                        b.needs.mood?.thoughts.memories.TryGainMemory(RPRelationDefOf.RP_MadePeace, a);
                        PrisonGame.Instance.stats.feudsMediated++;
                        Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_FeudMediated".Translate(a.LabelShort, b.LabelShort, mediator?.LabelShort ?? "RP_Chr_NoOne".Translate().ToString()), a, b, mediator);
                        arc.done = true;
                    }
                    else
                    {
                        // Talking made it worse: it comes to a head sooner.
                        arc.nextTick = Mathf.Min(arc.nextTick, Find.TickManager.TicksGame + 12 * GenDate.TicksPerHour);
                        Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FeudMediationFailed".Translate(a.LabelShort, b.LabelShort), a, b);
                    }
                    break;
            }
        }

        public override void Hourly(StoryArc arc, Map map)
        {
            Pawn a = arc.A, b = arc.B;
            int now = Find.TickManager.TicksGame;
            if (arc.stage == Separated)
            {
                if (now < arc.nextTick) return;
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FeudCooled".Translate(a.LabelShort, b.LabelShort), a, b);
                arc.done = true;
                return;
            }
            // Simmering (the tension it causes is added by TensionUtility, see Simmering).
            CompInmate ac = a.Inmate();
            if (now < arc.nextTick) return;
            // Flashpoint: when they cross paths unwatched (gives up two days after it was due).
            if (a.Awake() && b.Awake() && !a.InMentalState && !b.InMentalState && !a.Downed && !b.Downed && a.GetRoom() == b.GetRoom()
                && a.Position.InHorDistOf(b.Position, 12f) && !a.WorkTagIsDisabled(WorkTags.Violent) && a.interactions.SocialFightPossible(b))
            {
                ContrabandUtility.Notify_ViolenceStarting(a);
                a.interactions.StartSocialFight(b);
                PrisonGame.Instance.stats.feudFlashpoints++;
                Story.Record(RPChronicleDefOf.RP_Chr_Violence, 2, "RP_Chr_FeudFlashpoint".Translate(a.LabelShort, b.LabelShort), a, b);
                MisconductUtility.Record(a, ac, PrisonMap.Of(map), RPMisconductDefOf.RP_Fight);
                arc.done = true;
                return;
            }
            if (now - arc.nextTick > 2 * GenDate.TicksPerDay)
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FeudFizzled".Translate(a.LabelShort, b.LabelShort), a, b);
                arc.done = true;
            }
        }

        /// <summary>In a simmering feud with someone of the same wing (they keep running into each other).</summary>
        public static bool Simmering(Pawn p, CompInmate c)
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null) return false;
            foreach (StoryArc a in arcs)
            {
                if (a.done || a.stage != Simmer || a.def != RPRelationDefOf.RP_Arc_Feud) continue;
                Pawn other = a.A == p ? a.B : a.B == p ? a.A : null;
                if (other?.Inmate() is { } oc && oc.security == c.security) return true;
            }
            return false;
        }

        public override void Cancelled(StoryArc arc)
        {
            if (arc.A != null && arc.B != null)
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_FeudEnded".Translate(arc.A.LabelShort, arc.B.LabelShort), arc.A, arc.B);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.StartSocialFight))]
    public static class Patch_StartSocialFight
    {
        public static void Postfix(Pawn ___pawn, Pawn otherPawn)
        {
            if (___pawn.Spawned && otherPawn != null) PrisonRelations.Notify_Fight(___pawn, otherPawn);
        }
    }
}
