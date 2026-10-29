using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public enum GangRank : byte
    {
        Member,
        Lieutenant,
        Leader,
    }

    [DefOf]
    public static class RPGangStructureDefOf
    {
        public static StoryArcDef RP_Arc_Succession;
        public static InmateWantDef RP_Want_LeaveGang;
        public static ThoughtDef RP_ExMember;
        public static ThoughtDef RP_MarkedByGang;
        public static ThoughtDef RP_BeatenForLeaving;
        public static JobDef RP_GangExitSession;

        static RPGangStructureDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPGangStructureDefOf));
    }

    /// <summary>
    /// Gang ranks: a member, a lieutenant (the most-tenured violent member, picked daily; a big gang has two) and the leader. The rank is
    /// shown only where the gang is (the file, the inspect line: a known gang's). When the leader leaves the prison (released, transferred,
    /// dead, escaped) the lieutenant takes over, with a chronicle line once the gang is known; two lieutenants contest it in a short arc
    /// (<see cref="SuccessionArc"/>). The leader flag itself stays <see cref="CompInmate.gangLeader"/>, which the rest of the code reads:
    /// <see cref="SetRank"/> keeps the two in step.
    /// </summary>
    public static class GangRanks
    {
        /// <summary>A gang with this many members inside picks a second lieutenant.</summary>
        public const int BigGang = 6;
        /// <summary>The showdown of a contested succession is due this many hours after the leader goes, and is given up (the stronger simply takes it) after the contenders have failed to meet for this long.</summary>
        public const int ShowdownMinHours = 18, ShowdownMaxHours = 42, ShowdownGiveUpDays = 3;
        public const float ContestTension = 15f;

        private static int Now => Find.TickManager.TicksGame;

        public static GangRank Of(CompInmate c) => c?.gang == null ? GangRank.Member : c.gangLeader ? GangRank.Leader : c.gangRank == (byte)GangRank.Lieutenant ? GangRank.Lieutenant : GangRank.Member;

        public static void SetRank(CompInmate c, GangRank rank)
        {
            // (Nobody leads a gang they are not in: a flag with no gang is read raw by the code that does not go through Of.)
            if (rank == GangRank.Leader && c.gang == null) rank = GangRank.Member;
            c.gangLeader = rank == GangRank.Leader;
            c.gangRank = (byte)rank;
        }

        public static string RankText(CompInmate c) => ("RP_Gang" + Of(c)).Translate();

        /// <summary>The suffix the file's gang chip shows after the gang's name (the leader's star, a lieutenant's mark).</summary>
        public static string ChipSuffix(CompInmate c) => Of(c) switch
        {
            GangRank.Leader => " ★",
            GangRank.Lieutenant => "RP_GangLieutenantSuffix".Translate().ToString(),
            _ => "",
        };

        // ---- the daily pick --------------------------------------------------------------------------------------

        private static bool CanBeLieutenant(Pawn p, CompInmate c) =>
            c.activeFlag && c.gang != null && Of(c) == GangRank.Member && c.crime is { violent: true } && !GangExit.Seeking(c) && DeathRowSystem.Ext(c) == null && !ShuttleSystem.OnTheWayOut(p);

        /// <summary>
        /// Daily: every gang with a violent member and without its full complement of lieutenants (one, two from <see cref="BigGang"/> members) makes
        /// the most-tenured violent member a lieutenant (one a day), never one who wants out of the gang nor an enemy of its leader (the loser of a contest).
        /// </summary>
        public static void Daily(Map map)
        {
            var members = new Dictionary<GangDef, (int total, int lieutenants, Pawn best)>();
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            var leaders = new Dictionary<GangDef, Pawn>();
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { gang: { } g, gangLeader: true, activeFlag: true }) leaders[g] = inmates[i];
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (p.Inmate() is not { gang: { } gang } c || !c.activeFlag) continue;
                members.TryGetValue(gang, out var m);
                m.total++;
                if (Of(c) == GangRank.Lieutenant) m.lieutenants++;
                if (CanBeLieutenant(p, c) && !(leaders.TryGetValue(gang, out Pawn boss) && PrisonRelations.Enemies(p, boss)) && (m.best == null || Better(p, c, m.best))) m.best = p;
                members[gang] = m;
            }
            // (In def order: a dictionary's order follows hashes and changes from run to run.)
            foreach (GangDef gang in DefDatabase<GangDef>.AllDefsListForReading)
            {
                if (!members.TryGetValue(gang, out var m) || m.best == null || m.lieutenants >= (m.total >= BigGang ? 2 : 1)) continue;
                CompInmate c = m.best.Inmate();
                SetRank(c, GangRank.Lieutenant);
                if (c.gangKnown) Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_GangLieutenant".Translate(m.best.LabelShort, gang.label), m.best);
            }
        }

        private static bool Better(Pawn p, CompInmate c, Pawn other)
        {
            CompInmate oc = other.Inmate();
            float a = c.DaysServed, b = oc.DaysServed;
            return a > b || a == b && p.thingIDNumber < other.thingIDNumber;
        }

        // ---- succession ------------------------------------------------------------------------------------------

        /// <summary>
        /// The leader is leaving for good (called last of everything that hears of it): the gang's lieutenant takes over. (Their own flag is left as it is: they are on
        /// their way out, and a war arc that ends with its leader has read it already. A second word of the same departure finds a leader inside, or a contest, and does nothing.)
        /// </summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p.Inmate() is not { gang: { } gang, gangLeader: true } || (p.MapHeld ?? Find.AnyPlayerHomeMap) is not { } map) return;
            if (GangUtility.HasLeader(map, gang, p) || Contested(gang)) return;
            Succeed(map, gang, p);
        }

        internal static bool Contested(GangDef gang)
        {
            foreach (StoryArc a in PrisonGame.Instance.story.arcs)
                if (!a.done && a.def == RPGangStructureDefOf.RP_Arc_Succession && a.note == gang.defName) return true;
            return false;
        }

        private static bool Inside(Pawn p, Pawn leaving) =>
            p != leaving && !p.Dead && p.Inmate() is { activeFlag: true } && !p.guest.Released && !ShuttleSystem.OnTheWayOut(p);

        /// <summary>The lieutenants of a gang who are still inside, the most tenured first.</summary>
        public static List<Pawn> Lieutenants(Map map, GangDef gang, Pawn leaving)
        {
            var found = new List<Pawn>();
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (Inside(inmates[i], leaving) && inmates[i].Inmate() is { } c && c.gang == gang && Of(c) == GangRank.Lieutenant) found.Add(inmates[i]);
            found.Sort((a, b) => a == b ? 0 : Better(a, a.Inmate(), b) ? -1 : 1);
            return found;
        }

        /// <summary>
        /// Back from outside (rearrested, or a fugitive brought back): they keep the rank only if there is nobody in it. A leader leads a gang with no leader and no contest
        /// for the post; anybody else is a plain member (the daily pass picks a lieutenant again). <see cref="CompInmate.ResetForNewStay"/> keeps the gang and its flags.
        /// </summary>
        public static void Notify_Returned(Map map, Pawn p, CompInmate c) =>
            SetRank(c, c.gang != null && c.gangLeader && !GangUtility.HasLeader(map, c.gang, p) && !Contested(c.gang) ? GangRank.Leader : GangRank.Member);

        public static void Succeed(Map map, GangDef gang, Pawn leaving)
        {
            List<Pawn> lieutenants = Lieutenants(map, gang, leaving);
            if (lieutenants.Count == 0) return;
            // (A contest that cannot be held, its contenders being in other stories, is no fight: the most tenured takes the post, and the line does not tell of one.)
            if (lieutenants.Count == 1 || SuccessionArc.Start(map, gang, lieutenants[0], lieutenants[1]) == null) Crown(map, gang, lieutenants[0], contested: false);
        }

        /// <summary>Whether the warden may be told who they are: the gang is known, and so is this inmate's membership.</summary>
        private static bool Tellable(Map map, GangDef gang, params Pawn[] who)
        {
            if (!GangTurf.GangKnown(map, gang)) return false;
            foreach (Pawn p in who)
                if (p.Inmate() is not { gangKnown: true }) return false;
            return true;
        }

        /// <summary>This inmate leads the gang now (the line says so once the gang is known).</summary>
        public static void Crown(Map map, GangDef gang, Pawn leader, bool contested)
        {
            SetRank(leader.Inmate(), GangRank.Leader);
            leader.Inmate().tension = Mathf.Max(0f, leader.Inmate().tension - 5f);
            if (!GangTurf.GangKnown(map, gang)) return;
            if (Tellable(map, gang, leader)) Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, (contested ? "RP_Chr_GangSuccessionWon" : "RP_Chr_GangSuccession").Translate(leader.LabelShort, gang.label), leader);
            else Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangSuccessionUnnamed".Translate(gang.label));
        }
    }

    /// <summary>
    /// A contested succession: the leader is gone and two lieutenants want the post. The gang has no leader meanwhile; both are wound up. Some
    /// hours later they settle it where they meet (a fight, the stronger with luck wins), or, when they keep apart, the stronger takes it. The loser is
    /// a plain member and the winner's enemy. If one of them leaves first the other leads.
    /// </summary>
    public class SuccessionArc : StoryArcWorker
    {
        public static StoryArc Start(Map map, GangDef gang, Pawn a, Pawn b)
        {
            StoryArc arc = PrisonGame.Instance.story.TryStartArc(RPGangStructureDefOf.RP_Arc_Succession, a, b);
            if (arc == null) return null;
            int now = Find.TickManager.TicksGame;
            arc.nextTick = now + Rand.RangeInclusive(GangRanks.ShowdownMinHours, GangRanks.ShowdownMaxHours) * GenDate.TicksPerHour;
            arc.note = gang.defName;
            foreach (Pawn p in arc.pawns)
            {
                CompInmate c = p.Inmate();
                c.tension = Mathf.Min(100f, c.tension + GangRanks.ContestTension);
                c.mainCause = c.gangKnown ? "RP_Cause_Succession" : "RP_Cause_WingTrouble";
            }
            if (GangTurf.GangKnown(map, gang))
            {
                bool named = a.Inmate().gangKnown && b.Inmate().gangKnown;
                Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, (named ? "RP_Chr_GangSuccessionContested" : "RP_Chr_GangSuccessionContestedUnnamed").Translate(gang.label, a.LabelShort, b.LabelShort),
                    named ? new[] { a, b } : new Pawn[0]);
            }
            return arc;
        }

        private static GangDef GangOf(StoryArc arc) => DefDatabase<GangDef>.GetNamedSilentFail(arc.note ?? "");

        /// <summary>Both are still inmates and still of the gang (one who left it, or was moved off, is out of the contest).</summary>
        public override bool Valid(StoryArc arc)
        {
            if (!base.Valid(arc) || GangOf(arc) is not { } gang) return false;
            foreach (Pawn p in arc.pawns)
                if (p.Inmate()?.gang != gang) return false;
            return true;
        }

        private static float Strength(Pawn p) => (p.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0) + Rand.Range(0f, 8f) + Mathf.Min(6f, p.Inmate().DaysServed * 0.2f);

        public override void Hourly(StoryArc arc, Map map)
        {
            if (arc.A is not { } a || arc.B is not { } b || GangOf(arc) is not { } gang) return;
            int now = Find.TickManager.TicksGame;
            if (now < arc.nextTick) return;
            // Somebody leads the gang already (a leader who came back, say): the contest is over, and crowns nobody.
            if (GangUtility.HasLeader(map, gang, null))
            {
                arc.done = true;
                return;
            }
            // They meet: a fight, which the record counts as any other.
            bool met = a.Spawned && b.Spawned && !a.Downed && !b.Downed && a.Awake() && b.Awake() && !a.InMentalState && !b.InMentalState && a.GetRoom() == b.GetRoom();
            if (!met && now - arc.nextTick < GangRanks.ShowdownGiveUpDays * GenDate.TicksPerDay) return;
            if (met)
            {
                if (!a.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: b)) return;
                ContrabandUtility.Notify_ViolenceStarting(a);
                MisconductUtility.Record(a, a.Inmate(), PrisonMap.Of(map), RPMisconductDefOf.RP_Fight, chronicle: false);
            }
            Pawn winner = Strength(a) >= Strength(b) ? a : b, loser = winner == a ? b : a;
            arc.done = true;
            GangRanks.SetRank(loser.Inmate(), GangRank.Member);
            PrisonRelations.MakeEnemies(winner, loser);
            GangRanks.Crown(map, gang, winner, contested: true);
        }

        /// <summary>One of the two leaves (released, transferred, dead): the other leads.</summary>
        public override void Notify_Leaving(StoryArc arc, Pawn p)
        {
            if (!arc.pawns.Contains(p)) return;
            arc.pawns.Remove(p);
            arc.done = true;
            Finish(arc);
        }

        /// <summary>Someone in it is gone without a farewell (discarded): whoever is left leads.</summary>
        public override void Cancelled(StoryArc arc)
        {
            GangDef gang = GangOf(arc);
            arc.pawns.RemoveAll(x => x == null || x.Dead || x.Destroyed || !x.IsPrisonerOfColony || x.guest.Released || x.Inmate()?.gang != gang);
            Finish(arc);
        }

        private static void Finish(StoryArc arc)
        {
            if (arc.pawns.Count != 1 || GangOf(arc) is not { } gang || (arc.pawns[0].MapHeld ?? Find.AnyPlayerHomeMap) is not { } map) return;
            Pawn winner = arc.pawns[0];
            if (winner.Inmate() is { } c && c.gang == gang && !c.gangLeader && !GangUtility.HasLeader(map, gang, null)) GangRanks.Crown(map, gang, winner, contested: true);
        }
    }
}
