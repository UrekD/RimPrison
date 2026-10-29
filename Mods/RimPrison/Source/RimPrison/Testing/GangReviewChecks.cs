using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The review of the gang structure (gap analysis 4, section 1.2): one check for each defect it found, each of which failed with its fix reverted: a returning leader or lieutenant
    /// (H1), re-recruiting an ex-member (M1), a leader made during a contest (M2), a renouncement during a challenge or a war (M3), what the Social tab gives away (M4, in
    /// <c>CheckExMember</c>), the enforcer's cause (M5, in <c>CheckGangLeaving</c>), and the small ones (L1-L8, L11, L12). Called from <c>CheckGangStructure</c>; the same scenes
    /// (<c>GangScene</c>) as the checks they follow.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckGangReview(Map map)
        {
            CheckGangReturn(map);
            CheckGangContestGuards(map);
            CheckGangRecruitExMember(map);
            CheckGangRenounceDuringStories(map);
            CheckGangLeavingLimits(map);
            CheckGangRankSmall(map);
        }

        private static int GangLeaders(Map map, GangDef gang) =>
            map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate() is { } c && c.gang == gang && GangRanks.Of(c) == GangRank.Leader);

        /// <summary>Who is in the gang, spawned (for a failure's message).</summary>
        private static string GangRoster(Map map, GangDef gang) =>
            string.Join(", ", map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate()?.gang == gang).Select(x => $"{x.LabelShort}#{x.thingIDNumber} {GangRanks.Of(x.Inmate())} active {x.Inmate().activeFlag}"));

        /// <summary>A leader goes for good: the story hears of it, and they are not in the prison any more (a gang's leader who is still standing there holds the post).</summary>
        private static void GangLeaves(PrisonGame g, Pawn p)
        {
            g.story.Notify_Leaving(p);
            if (p.Spawned) p.DeSpawn();
        }

        /// <summary>The way back in, as the news of a rearrest finds them (out of the map, then the real <c>Reoffenders.Return</c>).</summary>
        private static void GangBack(PrisonGame g, Map map, Pawn p)
        {
            if (p.Spawned) p.DeSpawn();
            Reoffenders.Return(g, map, p, p.Inmate(), bed: null);
        }

        // ---- H1: a returning leader or lieutenant ----------------------------------------------------------------------------------------

        private static void CheckGangReturn(Map map)
        {
            Guarded("gang return", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens, saints = s.saints;
                // The leader goes, the lieutenant leads; the old leader is rearrested and comes back: a member, one leader.
                Pawn leader = s.Inmate(ravens, GangRank.Leader, days: 40f), lieutenant = s.Inmate(ravens, GangRank.Lieutenant, days: 20f);
                s.g.story.Notify_Leaving(leader);
                if (!lieutenant.Inmate().gangLeader) GangFail("return check: the lieutenant did not take over (set-up)");
                GangBack(s.g, map, leader);
                int leaders = GangLeaders(map, ravens);
                if (leaders != 1 || GangRanks.Of(leader.Inmate()) != GangRank.Member || leader.Inmate().gangLeader || !lieutenant.Inmate().gangLeader)
                    GangFail($"an ex-leader came back after the lieutenant took over: {leaders} leaders of the Ravens, the ex-leader is {GangRanks.Of(leader.Inmate())} (flag {leader.Inmate().gangLeader})");
                // A returning lieutenant is not an extra one (the small gang has its lieutenant already).
                Pawn sitting = s.Inmate(ravens, GangRank.Lieutenant, days: 10f), away = s.Inmate(ravens, GangRank.Lieutenant, days: 15f);
                GangBack(s.g, map, away);
                if (GangRanks.Of(away.Inmate()) != GangRank.Member || GangRanks.Of(sitting.Inmate()) != GangRank.Lieutenant)
                    GangFail($"a returning lieutenant came back as {GangRanks.Of(away.Inmate())} (expected member), the one who stayed is {GangRanks.Of(sitting.Inmate())}");
                // A gang with nobody to take over still has its leader back (as it always had).
                Pawn lone = s.Inmate(saints, GangRank.Leader);
                s.g.story.Notify_Leaving(lone);
                GangBack(s.g, map, lone);
                if (GangRanks.Of(lone.Inmate()) != GangRank.Leader || GangLeaders(map, saints) != 1) GangFail($"a leader back to a gang with no leader is {GangRanks.Of(lone.Inmate())} (expected leader; the Saints: {GangRoster(map, saints)}; a contest running {SuccessionArcOf(PrisonGame.Instance) != null}; lone #{lone.thingIDNumber} gang {lone.Inmate().gang?.defName} flag {lone.Inmate().gangLeader})");
                // One with no gang is no leader, whatever a stray flag says.
                Pawn stray = s.Inmate(null);
                stray.Inmate().gangLeader = true;
                GangBack(s.g, map, stray);
                if (stray.Inmate().gangLeader) GangFail("somebody with no gang came back a leader");
            });
            Guarded("gang return contested", map, r =>
            {
                var s = new GangScene(map, r);
                Pawn leader = s.Inmate(s.ravens, GangRank.Leader, days: 40f), a = s.Inmate(s.ravens, GangRank.Lieutenant, days: 20f), b = s.Inmate(s.ravens, GangRank.Lieutenant, days: 10f);
                s.g.story.Notify_Leaving(leader);
                if (SuccessionArcOf(s.g) == null) GangFail("return check: no contest (set-up)");
                GangBack(s.g, map, leader);
                if (GangLeaders(map, s.ravens) != 0 || GangRanks.Of(leader.Inmate()) != GangRank.Member)
                    GangFail($"a leader came back in the middle of a contest for the post: {GangLeaders(map, s.ravens)} leaders, they are {GangRanks.Of(leader.Inmate())}");
            });
        }

        // ---- M2: a contest's window ---------------------------------------------------------------------------------------------------------

        private static void CheckGangContestGuards(Map map)
        {
            Guarded("gang contest window", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens;
                Pawn leader = s.Inmate(ravens, GangRank.Leader, days: 40f), a = s.Inmate(ravens, GangRank.Lieutenant, days: 20f), b = s.Inmate(ravens, GangRank.Lieutenant, days: 10f);
                GangLeaves(s.g, leader);
                StoryArc arc = SuccessionArcOf(s.g);
                if (arc == null)
                {
                    GangFail("contest window: no contest (set-up)");
                    return;
                }
                // An arrival rolling into the Ravens while the post is fought over is never made its leader (every other gang is made unlikely, the rolls many).
                GangDef[] all = DefDatabase<GangDef>.AllDefsListForReading.ToArray();
                float[] commonality = all.Select(x => x.commonality).ToArray();
                r.Add(() =>
                {
                    for (int i = 0; i < all.Length; i++) all[i].commonality = commonality[i];
                });
                foreach (GangDef gang in all) gang.commonality = gang == ravens ? 1f : 0f;
                Pawn arrival = s.Inmate(null);
                arrival.Inmate().security = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderByDescending(x => x.dangerFactor).First();
                int joined = 0, led = 0;
                for (int i = 0; i < 400; i++)
                {
                    arrival.Inmate().gang = null;
                    GangRanks.SetRank(arrival.Inmate(), GangRank.Member);
                    GangUtility.RollAtIntake(arrival, arrival.Inmate());
                    if (arrival.Inmate().gang != ravens) continue;
                    joined++;
                    if (arrival.Inmate().gangLeader) led++;
                }
                arrival.Inmate().gang = null;
                GangRanks.SetRank(arrival.Inmate(), GangRank.Member);
                if (joined < 50) GangFail($"contest window: only {joined} of 400 arrivals rolled into the Ravens (the check did not exercise it)");
                if (led > 0) GangFail($"{led} arrivals were made leader of a gang whose post is being fought over");

                // And if somebody leads anyway by the time it is settled, the contest ends without a second crown.
                s.Inmate(ravens, GangRank.Leader);
                arc.nextTick = s.Now - GenDate.TicksPerHour;
                arc.def.Worker.Hourly(arc, map);
                int leaders = GangLeaders(map, ravens);
                if (!arc.done || leaders != 1 || a.Inmate().gangLeader || b.Inmate().gangLeader)
                    GangFail($"a contest settled while somebody else led: done {arc.done}, {leaders} leaders, a contender leads {a.Inmate().gangLeader || b.Inmate().gangLeader}");
            });
            Guarded("gang contest window, a contender leaves", map, r =>
            {
                var s = new GangScene(map, r);
                Pawn leader = s.Inmate(s.ravens, GangRank.Leader, days: 40f), a = s.Inmate(s.ravens, GangRank.Lieutenant, days: 20f), b = s.Inmate(s.ravens, GangRank.Lieutenant, days: 10f);
                GangLeaves(s.g, leader);
                StoryArc arc = SuccessionArcOf(s.g);
                if (arc == null)
                {
                    GangFail("contest window (leaving): no contest (set-up)");
                    return;
                }
                s.Inmate(s.ravens, GangRank.Leader);
                s.g.story.Notify_Leaving(a);
                if (b.Inmate().gangLeader || GangLeaders(map, s.ravens) != 1) GangFail($"a contender left with somebody leading: the other leads {b.Inmate().gangLeader}, {GangLeaders(map, s.ravens)} leaders");
            });
        }

        // ---- M1: the old gang recruiting an ex-member ----------------------------------------------------------------------------------------

        private static void CheckGangRecruitExMember(Map map)
        {
            Guarded("gang recruit ex-member", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens;
                // (Nobody who dislikes the leader: a recruit's opinion of them must not be below zero. A leader whom every pawn dislikes - an ugly one with creepy breathing and an annoying voice,
                // about 1 in 80 rolls - is one nobody can be found for in any number of tries, and the recruit check then reads as if the recruiting did nothing: such a leader is replaced.)
                Pawn leader = null, exMember = null, plain = null;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    leader = s.Inmate(ravens, GangRank.Leader);
                    exMember = Liked(leader);
                    plain = Liked(leader);
                    if (exMember.relations.OpinionOf(leader) >= 0 && plain.relations.OpinionOf(leader) >= 0) break;
                    foreach (Pawn p in new[] { leader, exMember, plain }) p.Destroy(DestroyMode.Vanish);
                }
                Pawn Liked(Pawn of)
                {
                    Pawn p = s.Inmate(null);
                    for (int tries = 0; tries < 8 && p.relations.OpinionOf(of) < 0; tries++)
                    {
                        p.Destroy(DestroyMode.Vanish);
                        p = s.Inmate(null);
                    }
                    return p;
                }
                (exMember.Inmate().exGang, exMember.Inmate().exGangTick) = (ravens, s.Now);
                foreach (Pawn p in new[] { exMember, plain })
                {
                    p.Inmate().tension = 60f;
                    s.Place(p, leader.Position);
                }
                // (A recruit needs the leader's roll to pass: many tries, the unaffiliated one proves the recruiting works.)
                for (int i = 0; i < 600; i++) GangUtility.TryRecruit(leader, leader.Inmate());
                if (plain.Inmate().gang != ravens) GangFail($"recruit check: the Ravens' leader recruited nobody in 600 tries (the check did not exercise it): the unaffiliated inmate's opinion of the leader {plain.relations.OpinionOf(leader)}, tension {plain.Inmate().tension}, gang {plain.Inmate().gang?.defName}, {plain.Position.DistanceTo(leader.Position):0.#} cells away, the leader leads {leader.Inmate().gangLeader}");
                if (exMember.Inmate().gang != null) GangFail("the old gang's leader recruited the ex-member straight back");
                // Another gang's leader may take them (the green light outlives that).
                GangDef saints = s.saints;
                Pawn other = s.Inmate(saints, GangRank.Leader);
                for (int tries = 0; tries < 20 && exMember.relations.OpinionOf(other) < 0; tries++)
                {
                    other.Destroy(DestroyMode.Vanish);
                    other = s.Inmate(saints, GangRank.Leader);
                }
                s.Place(other, exMember.Position);
                for (int i = 0; i < 600 && exMember.Inmate().gang == null; i++) GangUtility.TryRecruit(other, other.Inmate());
                if (exMember.Inmate().gang != saints) GangFail("another gang's leader could not recruit the ex-member");
            });
        }

        // ---- M3: a renouncement during a challenge or a war ----------------------------------------------------------------------------------

        private static void CheckGangRenounceDuringStories(Map map)
        {
            Guarded("gang renounce during a challenge", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens;
                Pawn boss = s.Inmate(ravens, GangRank.Leader), challenger = s.Inmate(ravens), mate = s.Inmate(ravens);
                challenger.Inmate().tension = 40f;
                s.g.story.lastArcStart.Remove(RPBeatDefOf.RP_Arc_GangChallenge);
                StoryArc arc = s.g.story.TryStartArc(RPBeatDefOf.RP_Arc_GangChallenge, challenger, boss);
                if (arc == null)
                {
                    GangFail("challenge check: no arc could be started (set-up)");
                    return;
                }
                arc.note = ravens.label;
                if (!arc.def.Worker.Valid(arc)) GangFail("a challenge in a gang is not valid while both are in it");
                GangExit.Renounce(challenger, challenger.Inmate());
                if (arc.def.Worker.Valid(arc)) GangFail("a challenge went on after the challenger walked out of the gang");
                // The flag with no gang is never set (the old code crowned them, leaving the boss a member).
                GangRanks.SetRank(challenger.Inmate(), GangRank.Leader);
                if (challenger.Inmate().gangLeader || challenger.Inmate().gang != null) GangFail("a gangless ex-member was made a leader");
                // The boss losing the post ends it too.
                Pawn boss2 = s.Inmate(s.saints, GangRank.Leader), up = s.Inmate(s.saints);
                s.g.story.lastArcStart.Remove(RPBeatDefOf.RP_Arc_GangChallenge);
                StoryArc second = s.g.story.TryStartArc(RPBeatDefOf.RP_Arc_GangChallenge, up, boss2);
                if (second == null) return;
                second.note = s.saints.label;
                GangRanks.SetRank(boss2.Inmate(), GangRank.Member);
                if (second.def.Worker.Valid(second)) GangFail("a challenge went on against somebody who no longer leads");
            });
            Guarded("gang renounce during a war", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens, saints = s.saints;
                // Two Ravens and a Saint, no leaders: the war's cast is a member of each.
                Pawn r1 = s.Inmate(ravens), r2 = s.Inmate(ravens), sa = s.Inmate(saints);
                StoryArc war = GangWarArc.Start(map, ravens, saints);
                if (war == null)
                {
                    GangFail("war check: no war could be started (set-up)");
                    return;
                }
                Pawn rep = war.pawns.FirstOrDefault(x => x.Inmate()?.gang == ravens);
                if (rep == null)
                {
                    GangFail("war check: the war has no Raven in its cast (set-up)");
                    return;
                }
                GangUtility.Reveal(r1.Inmate());
                GangUtility.Reveal(r2.Inmate());
                GangExit.Renounce(rep, rep.Inmate());
                if (war.pawns.Contains(rep)) GangFail("a member who quit the gang is still its representative in the war");
                if (war.pawns.Any(x => x.Inmate()?.gang == null)) GangFail("the war's cast has somebody with no gang");
            });
        }

        // ---- L4, L8, L7, L11, L12: leaving ----------------------------------------------------------------------------------------------

        private static void CheckGangLeavingLimits(Map map)
        {
            Guarded("gang leaving limits", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens;
                InmateWantDef leave = RPGangStructureDefOf.RP_Want_LeaveGang;
                // L4: nobody of the gang who can reach them (the only other member is in protective custody, on another wing): no threat, no letter, no promised beating.
                Pawn lonely = s.Inmate(ravens, known: false);
                s.Inmate(ravens, known: false).Inmate().security = RPSecurityDefOf.RP_Protective;
                InmateWant w = Wants.Form(lonely, lonely.Inmate(), leave);
                if (w == null)
                {
                    GangFail("leaving limits: could not form the goal (set-up)");
                    return;
                }
                w.step = 1;
                w.stepTick = s.Now;
                int entries = r.NewEntries.Count();
                int letters = r.NewLetters.Count();
                Wants.Hourly(lonely, lonely.Inmate());
                List<string> told = r.NewEntries.Skip(entries).Select(e => e.text).ToList();
                if (lonely.Inmate().exGang != ravens || lonely.Inmate().exMarked || r.NewLetters.Count() != letters || told.Contains("RP_Chr_GreenLight".Translate(ravens.label, lonely.LabelShort).Resolve()))
                    GangFail($"a renouncement with nobody of the gang to reach them: ex-gang {lonely.Inmate().exGang?.defName}, marked {lonely.Inmate().exMarked}, new letters {r.NewLetters.Count() - letters}, a green light told {told.Count > 0 && told.Any(t => t.Contains("green light"))}");
                // L11: the want's detail names the gang after the renouncement, not "?".
                if (!w.def.Worker.Detail(lonely, lonely.Inmate(), w).ToString().Contains(ravens.label)) GangFail($"the resolved want's detail does not name the gang: \"{w.def.Worker.Detail(lonely, lonely.Inmate(), w)}\"");
                // L8: somebody in protective custody is offered nothing (the gang is on their wing, but they are out of reach).
                Pawn boss = s.Inmate(ravens, GangRank.Leader, known: false), inCustody = s.Inmate(ravens, known: false);
                boss.Inmate().security = inCustody.Inmate().security = RPSecurityDefOf.RP_Protective;
                s.Place(boss, inCustody.Position);
                letters = r.NewLetters.Count();
                GangExit.Renounce(inCustody, inCustody.Inmate());
                if (inCustody.Inmate().exMarked || r.NewLetters.Count() != letters) GangFail("somebody already in protective custody was put under a green light and offered protective custody");

                // L7: a letter that sat for a day: an answer that can no longer be carried out says so, and moves nobody.
                Pawn mate = s.Inmate(ravens, GangRank.Leader, known: false), leaver = s.Inmate(ravens, known: false);
                s.Place(mate, leaver.Position);
                letters = r.NewLetters.Count();
                GangExit.Renounce(leaver, leaver.Inmate());
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_LeftGang") && l.pawns.Contains(leaver));
                if (letter == null) GangFail("leaving limits: no letter for the warden (set-up)");
                else
                {
                    SecurityLevelDef level = leaver.Inmate().security;
                    leaver.Inmate().security = null;
                    int live = LiveMessages().Count;
                    letter.decision.Worker.Choose(letter, map, LetterOption(letter, "RP_LeftGang_Wing"));
                    string refusal = "RP_LeftGang_NoWing".Translate().ToString();
                    bool told1 = LiveMessages().Skip(Mathf.Max(0, live - 5)).Any(m => m.text == refusal);
                    leaver.Inmate().security = level;
                    if (!told1) GangFail("an answer that could not be carried out (no other wing) was dropped without a word");
                }

                // L12: a hidden wish to leave that times out gives no "setback" memory.
                Pawn hidden = s.Inmate(ravens, known: false);
                InmateWant hw = Wants.Form(hidden, hidden.Inmate(), leave);
                if (hw != null)
                {
                    Wants.Thwart(hidden, hidden.Inmate(), hw, Wants.TimeOutReason);
                    if (hidden.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPWantDefOf.RP_WantThwarted)) GangFail("a wish to leave a gang the warden has not found gave a memory of a setback on a goal");
                }
            });
        }

        // ---- L1-L3, L5, L6: ranks, causes and a war's death --------------------------------------------------------------------------------------

        private static void CheckGangRankSmall(Map map)
        {
            Guarded("gang rank details", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens, saints = s.saints;
                // L3: the loser of a contest is the leader's enemy, and is not the next day's lieutenant (the next most tenured is).
                Pawn leader = s.Inmate(ravens, GangRank.Leader), loser = s.Inmate(ravens, days: 30f), next = s.Inmate(ravens, days: 5f);
                PrisonRelations.MakeEnemies(leader, loser);
                GangRanks.Daily(map);
                if (GangRanks.Of(loser.Inmate()) != GangRank.Member || GangRanks.Of(next.Inmate()) != GangRank.Lieutenant)
                    GangFail($"a leader's enemy was made lieutenant: the enemy is {GangRanks.Of(loser.Inmate())}, the next member {GangRanks.Of(next.Inmate())} (expected member and lieutenant)");
                // L5: a member who wants out is not made a lieutenant.
                Pawn seeker = s.Inmate(saints, days: 30f), steady = s.Inmate(saints, days: 5f);
                Wants.Form(seeker, seeker.Inmate(), RPGangStructureDefOf.RP_Want_LeaveGang);
                if (!GangExit.Seeking(seeker.Inmate())) GangFail("rank details: the goal to leave was not formed (set-up)");
                GangRanks.Daily(map);
                if (GangRanks.Of(seeker.Inmate()) != GangRank.Member || GangRanks.Of(steady.Inmate()) != GangRank.Lieutenant)
                    GangFail($"a member who wants out was made lieutenant: the seeker is {GangRanks.Of(seeker.Inmate())}, the steady member {GangRanks.Of(steady.Inmate())} (gang {steady.Inmate().gang?.defName}, active {steady.Inmate().activeFlag}, crime {steady.Inmate().crime?.defName}/{steady.Inmate().crime?.violent}, way out {ShuttleSystem.OnTheWayOut(steady)}, row {DeathRowSystem.Ext(steady.Inmate()) != null}, want {steady.Inmate().want?.def?.defName}; the Saints: {GangRoster(map, saints)})");
                // L2: a contest that cannot be held (a contender is in another story) is no fight: the more tenured takes the post, and the line says so.
                foreach (Pawn p in new[] { leader, loser, next, seeker, steady }) p.Inmate().gang = null;
                Pawn boss = s.Inmate(ravens, GangRank.Leader), a = s.Inmate(ravens, GangRank.Lieutenant, days: 20f), b = s.Inmate(ravens, GangRank.Lieutenant, days: 10f), filler = s.Inmate(ravens);
                s.g.story.lastArcStart.Remove(RPBeatDefOf.RP_Arc_GangChallenge);
                StoryArc busy = s.g.story.TryStartArc(RPBeatDefOf.RP_Arc_GangChallenge, a, filler);
                if (busy == null)
                {
                    GangFail("rank details: could not put a contender in another story (set-up)");
                    return;
                }
                int entries = r.NewEntries.Count();
                s.g.story.Notify_Leaving(boss);
                List<string> told = r.NewEntries.Skip(entries).Select(e => e.text).ToList();
                if (!a.Inmate().gangLeader || SuccessionArcOf(s.g) != null) GangFail($"a contest that could not be held: the more tenured leads {a.Inmate().gangLeader}, an arc was started {SuccessionArcOf(s.g) != null}");
                if (!told.Contains("RP_Chr_GangSuccession".Translate(a.LabelShort, ravens.label).Resolve()) || told.Contains("RP_Chr_GangSuccessionWon".Translate(a.LabelShort, ravens.label).Resolve()))
                    GangFail($"a succession that was no fight is told as one: {string.Join(" | ", told)}");
                busy.done = true;

                // L6: the war's leader dies: their friends in the gang doubt, though the war ended with them.
                foreach (Pawn p in new[] { boss, a, b, filler }) p.Inmate().gang = null;
                Pawn warLeader = s.Inmate(ravens, GangRank.Leader), friend = s.Inmate(ravens), foe = s.Inmate(saints);
                friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, warLeader);
                StoryArc war = GangWarArc.Start(map, ravens, saints);
                if (war == null)
                {
                    GangFail("rank details: no war could be started (set-up)");
                    return;
                }
                s.g.story.Notify_Leaving(warLeader);
                GangExit.Notify_Died(warLeader, map);
                if (friend.Inmate().gangDoubtTick < 0) GangFail("a war leader's death, which ended the war, gave their friend no doubts");
            });
        }
    }
}
