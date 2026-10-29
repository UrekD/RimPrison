using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Gang ranks and a way out (events plan; gap analysis 4, section 1.2): the daily lieutenant, the succession when the leader goes (plain and contested), the mark a search or a frisk finds,
    /// the doubts that turn into the goal to leave, the session, the renouncement, the green light on the debts' schedule, the warden's letter, and the ex-member (hated for thirty days,
    /// left out of turf presence). Self-contained: every check works on throwaway inmates and staff, makes the real inmates nobody's for its length, and puts back everything it
    /// touches. Called once from the events plan, before <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The seeded gang structure of the saveload plan: the inmate who got it (to show it came back).</summary>
        private static int gangStructureSeedId = -1;

        private static void CheckGangStructure(Map map)
        {
            CheckGangRanks(map);
            CheckGangSuccession(map);
            CheckGangTattoos(map);
            CheckGangDoubts(map);
            CheckGangLeaving(map);
            CheckExMember(map);
            CheckGangReview(map);
        }

        /// <summary>A scene of throwaway gang members on an otherwise gangless prison: the real inmates are nobody's, and the story's arcs are put back.</summary>
        private sealed class GangScene
        {
            public readonly Map map;
            public readonly Rollback r;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
            private readonly List<IntVec3> cells;
            private int next;

            public GangScene(Map map, Rollback r)
            {
                this.map = map;
                this.r = r;
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null)
                    .Select(c => (c, c.gang, c.gangKnown, c.gangLeader, c.gangRank, c.activeFlag, c.exGang, c.exGangTick, c.exMarked, c.gangDoubtTick)).ToList();
                r.Add(() =>
                {
                    foreach (var (c, gang, known, leader, rank, active, ex, exTick, marked, doubt) in real)
                        (c.gang, c.gangKnown, c.gangLeader, c.gangRank, c.activeFlag, c.exGang, c.exGangTick, c.exMarked, c.gangDoubtTick) = (gang, known, leader, rank, active, ex, exTick, marked, doubt);
                });
                foreach (var (c, _, _, _, _, _, _, _, _, _) in real) (c.gang, c.gangKnown, c.gangLeader, c.gangRank, c.activeFlag, c.exGang, c.exMarked, c.gangDoubtTick) = (null, false, false, 0, false, null, false, -1);
                List<StoryArc> arcs0 = g.story.arcs;
                Dictionary<StoryArcDef, int> lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                int nextArc0 = g.story.nextArcId;
                r.Add(() =>
                {
                    g.story.arcs = arcs0;
                    g.story.lastArcStart = lastStart0;
                    g.story.nextArcId = nextArc0;
                });
                g.story.arcs = arcs0.Where(a => a.def != RPGangWarDefOf.RP_Arc_GangWar && a.def != RPGangStructureDefOf.RP_Arc_Succession).ToList();
                IntVec3 anchor = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                Room room = anchor.GetRoom(map);
                cells = GenRadial.RadialCellsAround(anchor, 8f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == room && c.GetFirstPawn(map) == null).ToList();
                if (cells.Count == 0) cells.Add(anchor);
            }

            public int Now => Find.TickManager.TicksGame;

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>
            /// A throwaway inmate of the gang (or none), on their feet and at the intake with the others. <paramref name="days"/> is the tenure in units of twenty ticks (not days: the events plan
            /// runs this early in a new game, and an admission before tick zero reads as no tenure at all).
            /// </summary>
            public Pawn Inmate(GangDef gang, GangRank rank = GangRank.Member, bool known = true, bool violent = true, float days = 3f)
            {
                // (One who cannot fight would never beat anyone: another is made, and the first is gone: a made-up inmate rolls a gang at intake, and one left standing could lead or be
                // the lieutenant of a gang under test.)
                Pawn p = Throwaway(map, r);
                for (int tries = 0; tries < 12 && p.WorkTagIsDisabled(WorkTags.Violent); tries++)
                {
                    p.Destroy(DestroyMode.Vanish);
                    p = Throwaway(map, r);
                }
                CompInmate c = p.Inmate();
                c.want = null;
                c.processed = true;
                // (Seen by the psychologist already: a real one would walk up to them and tell the gang.)
                c.assessed = true;
                c.tension = 0f;
                c.fear = 0f;
                c.gang = gang;
                c.gangKnown = known && gang != null;
                GangRanks.SetRank(c, rank);
                c.gangTattoo = false;
                c.crime = DefDatabase<CrimeDef>.AllDefsListForReading.First(d => d.violent == violent);
                c.admittedTick = Mathf.Max(1, Now - (int)(days * 20f));
                c.RefreshActiveFlag();
                c.activeFlag = true;
                Place(p, cells[next++ % cells.Count]);
                return p;
            }

            public List<string> NewTexts() => r.NewEntries.Select(e => e.text).ToList();

            public static bool Names(IEnumerable<string> texts, params string[] words) => texts.Any(t => words.Any(w => w.Length > 0 && t.Contains(w)));
        }

        private static void GangFail(string what) => problems0.Add("gang structure: " + what);

        // ---- ranks ---------------------------------------------------------------------------------------------------------------

        /// <summary>The daily pass makes the most-tenured violent member a lieutenant (one a day; a big gang has two), never a non-violent one and never a leader.</summary>
        private static void CheckGangRanks(Map map)
        {
            Guarded("gang ranks", map, r =>
            {
                var s = new GangScene(map, r);
                // The Ravens: a leader and six members; three violent ones (5, 12 and 8 days inside) and three who are not (one of them 30 days).
                Pawn leader = s.Inmate(s.ravens, GangRank.Leader, days: 40f);
                Pawn v5 = s.Inmate(s.ravens, days: 5f), v12 = s.Inmate(s.ravens, days: 12f, known: true), v8 = s.Inmate(s.ravens, days: 8f, known: false);
                Pawn n30 = s.Inmate(s.ravens, violent: false, days: 30f), n2 = s.Inmate(s.ravens, violent: false, days: 2f), n1 = s.Inmate(s.ravens, violent: false, days: 1f);
                // The Saints: three, one of them violent.
                Pawn sv = s.Inmate(s.saints, days: 4f), sn1 = s.Inmate(s.saints, violent: false, days: 9f), sn2 = s.Inmate(s.saints, violent: false, days: 7f);
                GangRank Rank(Pawn p) => GangRanks.Of(p.Inmate());
                if (Rank(leader) != GangRank.Leader || !leader.Inmate().gangLeader) GangFail("a leader is not ranked leader (rank and flag out of step)");
                GangRanks.Daily(map);
                if (Rank(v12) != GangRank.Lieutenant || Rank(v8) != GangRank.Member || Rank(v5) != GangRank.Member || Rank(n30) != GangRank.Member)
                    GangFail($"the first daily pick of the Ravens: 12-day violent member {Rank(v12)}, 8-day {Rank(v8)}, 5-day {Rank(v5)}, the 30-day non-violent {Rank(n30)} (expected the 12-day violent one alone)");
                if (Rank(sv) != GangRank.Lieutenant || Rank(sn1) != GangRank.Member || Rank(sn2) != GangRank.Member)
                    GangFail($"the Saints' pick: the violent member {Rank(sv)}, the others {Rank(sn1)}/{Rank(sn2)}");
                // A second day: the big gang (seven inside) picks a second lieutenant, the small one does not.
                GangRanks.Daily(map);
                if (Rank(v8) != GangRank.Lieutenant || Rank(v5) != GangRank.Member || Rank(sn1) != GangRank.Member)
                    GangFail($"the second daily pick: the Ravens' 8-day violent member is {Rank(v8)} (expected lieutenant), the 5-day {Rank(v5)} (member), the Saints' 9-day non-violent {Rank(sn1)}");
                GangRanks.Daily(map);
                if (Rank(v5) != GangRank.Member || Rank(leader) != GangRank.Leader || Rank(n2) != GangRank.Member || Rank(n1) != GangRank.Member)
                    GangFail($"a third day gave a third lieutenant or changed the leader: 5-day {Rank(v5)}, leader {Rank(leader)}");
                // Only a lieutenant the warden knows is in a line (the known one is in the chronicle, the unknown one is not).
                List<string> told = s.NewTexts();
                string known12 = "RP_Chr_GangLieutenant".Translate(v12.LabelShort, s.ravens.label).Resolve();
                if (!told.Contains(known12) || GangScene.Names(told, v8.LabelShort))
                    GangFail($"the lieutenant lines: known one told {told.Contains(known12)}, the unknown one named {GangScene.Names(told, v8.LabelShort)} (the lines naming {v8.LabelShort}: {string.Join(" | ", told.Where(t => GangScene.Names(new List<string> { t }, v8.LabelShort)))})");
                // The rank shows in the file only once the gang is known (the inspect line is the file's text).
                if (!GangRanks.RankText(v12.Inmate()).Equals("RP_GangLieutenant".Translate().ToString()) || !GangRanks.ChipSuffix(v12.Inmate()).Contains("lt"))
                    GangFail($"the lieutenant's rank text \"{GangRanks.RankText(v12.Inmate())}\" or chip suffix \"{GangRanks.ChipSuffix(v12.Inmate())}\"");
            });
        }

        // ---- succession ----------------------------------------------------------------------------------------------------------

        private static StoryArc SuccessionArcOf(PrisonGame g) => g.story.arcs.FirstOrDefault(a => !a.done && a.def == RPGangStructureDefOf.RP_Arc_Succession);

        private static void CheckGangSuccession(Map map)
        {
            Guarded("gang succession", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens;
                string Known(Pawn p) => p.LabelShort;
                // 1. One lieutenant: the leader is released, the lieutenant leads, and the chronicle says so (the gang is known).
                Pawn leader = s.Inmate(ravens, GangRank.Leader), lieutenant = s.Inmate(ravens, GangRank.Lieutenant), member = s.Inmate(ravens);
                int before = r.NewEntries.Count();
                s.g.story.Notify_Leaving(leader);
                string line = "RP_Chr_GangSuccession".Translate(lieutenant.LabelShort, ravens.label).Resolve();
                if (GangRanks.Of(lieutenant.Inmate()) != GangRank.Leader || !lieutenant.Inmate().gangLeader)
                    GangFail($"the leader left: the lieutenant is {GangRanks.Of(lieutenant.Inmate())} (expected leader)");
                // (A second word of the same departure finds a leader already: nobody else is crowned.)
                Pawn spare = s.Inmate(ravens, GangRank.Lieutenant);
                s.g.story.Notify_Leaving(leader);
                if (spare.Inmate().gangLeader) GangFail("a second word of the same departure crowned another lieutenant");
                if (!s.NewTexts().Contains(line)) GangFail("no chronicle line for the lieutenant taking over a known gang");
                if (SuccessionArcOf(s.g) != null) GangFail("a succession arc for an uncontested succession");
                if (GangRanks.Of(member.Inmate()) != GangRank.Member) GangFail("a plain member was ranked up by the succession");

                // 2. Hidden: nobody on file in a gang the warden has not found: no line names the gang or the new leader.
                Pawn leader2 = s.Inmate(ravens, GangRank.Leader, known: false), lieutenant2 = s.Inmate(ravens, GangRank.Lieutenant, known: false);
                // (The first gang's members are out of it, so no member of the Ravens is known.)
                foreach (Pawn p in new[] { leader, lieutenant, member, spare }) p.Inmate().gang = null;
                before = r.NewEntries.Count();
                s.g.story.Notify_Leaving(leader2);
                List<string> told = r.NewEntries.Skip(before).Select(e => e.text).ToList();
                if (!lieutenant2.Inmate().gangLeader) GangFail("a hidden gang's lieutenant did not take over");
                if (GangScene.Names(told, ravens.label, Known(lieutenant2), Known(leader2))) GangFail($"a line named a gang the warden has not found or its members: {string.Join(" | ", told)}");

                // 3. Contested: two lieutenants. The gang has no leader meanwhile; the arc settles it; the loser is a member and the winner's enemy.
                foreach (Pawn p in new[] { leader2, lieutenant2 }) p.Inmate().gang = null;
                Pawn leader3 = s.Inmate(ravens, GangRank.Leader), a = s.Inmate(ravens, GangRank.Lieutenant, days: 20f), b = s.Inmate(ravens, GangRank.Lieutenant, days: 10f);
                GangLeaves(s.g, leader3);
                StoryArc arc = SuccessionArcOf(s.g);
                if (arc == null || arc.A != a && arc.B != a || arc.A != b && arc.B != b) GangFail($"two lieutenants and no succession arc between them (arc {arc != null})");
                else
                {
                    if (a.Inmate().gangLeader || b.Inmate().gangLeader) GangFail("a contested gang had a leader before the arc settled it");
                    s.g.story.Notify_Leaving(leader3);
                    if (a.Inmate().gangLeader || b.Inmate().gangLeader || s.g.story.arcs.Count(x => !x.done && x.def == RPGangStructureDefOf.RP_Arc_Succession) != 1)
                        GangFail("a second word of the same departure crowned a lieutenant or started a second contest");
                    if (!s.NewTexts().Contains("RP_Chr_GangSuccessionContested".Translate(ravens.label, a.LabelShort, b.LabelShort).Resolve()) && !s.NewTexts().Contains("RP_Chr_GangSuccessionContested".Translate(ravens.label, b.LabelShort, a.LabelShort).Resolve()))
                        GangFail("no chronicle line for a contested succession in a known gang");
                    float tensionA = a.Inmate().tension;
                    if (tensionA < GangRanks.ContestTension - 0.1f) GangFail($"a contender's tension did not rise ({tensionA})");
                    // Not due yet: nothing happens. Due, and the two have kept apart for days: the stronger takes it without a fight.
                    arc.def.Worker.Hourly(arc, map);
                    if (arc.done) GangFail("the succession arc settled before it was due");
                    arc.nextTick = s.Now - (GangRanks.ShowdownGiveUpDays + 1) * GenDate.TicksPerDay;
                    s.Place(b, map.AllCells.First(c => c.Standable(map) && c.DistanceTo(a.Position) > 30f && b.CanReach(c, PathEndMode.OnCell, Danger.Deadly)));
                    arc.def.Worker.Hourly(arc, map);
                    int leaders = new[] { a, b }.Count(x => x.Inmate().gangLeader);
                    Pawn winner = a.Inmate().gangLeader ? a : b, loser = winner == a ? b : a;
                    if (!arc.done || leaders != 1 || GangRanks.Of(loser.Inmate()) != GangRank.Member || !PrisonRelations.Enemies(winner, loser))
                        GangFail($"the contested succession: done {arc.done}, leaders {leaders}, loser {GangRanks.Of(loser.Inmate())}, enemies {PrisonRelations.Enemies(winner, loser)}");
                    if (!s.NewTexts().Contains("RP_Chr_GangSuccessionWon".Translate(winner.LabelShort, ravens.label).Resolve())) GangFail("no chronicle line for who won the contest");
                }

                // 4. One of the two leaves before it is settled: the other leads, at once.
                foreach (Pawn p in new[] { leader3, a, b }) p.Inmate().gang = null;
                Pawn leader4 = s.Inmate(ravens, GangRank.Leader), c1 = s.Inmate(ravens, GangRank.Lieutenant, days: 20f), c2 = s.Inmate(ravens, GangRank.Lieutenant, days: 10f);
                GangLeaves(s.g, leader4);
                StoryArc second = SuccessionArcOf(s.g);
                if (second == null) GangFail("no succession arc for the second contest");
                else
                {
                    s.g.story.Notify_Leaving(c1);
                    if (!second.done || !c2.Inmate().gangLeader) GangFail($"a contender left: the arc is done {second.done}, the other leads {c2.Inmate().gangLeader}");
                }
            });
        }

        // ---- tattoos -------------------------------------------------------------------------------------------------------------

        private static void CheckGangTattoos(Map map)
        {
            Guarded("gang tattoos", map, r =>
            {
                var s = new GangScene(map, r);
                // Intake: a gang with a sure mark marks every member the roll makes, one with none marks nobody.
                GangDef[] all = DefDatabase<GangDef>.AllDefsListForReading.ToArray();
                float[] chances = all.Select(x => x.tattooChance).ToArray();
                r.Add(() =>
                {
                    for (int i = 0; i < all.Length; i++) all[i].tattooChance = chances[i];
                });
                foreach (float chance in new[] { 1f, 0f })
                {
                    foreach (GangDef gang in all) gang.tattooChance = chance;
                    Pawn rolled = null;
                    for (int tries = 0; tries < 400 && rolled == null; tries++)
                    {
                        Pawn candidate = s.Inmate(null);
                        candidate.Inmate().security = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderByDescending(x => x.dangerFactor).First();
                        GangUtility.RollAtIntake(candidate, candidate.Inmate());
                        if (candidate.Inmate().gang != null) rolled = candidate;
                    }
                    if (rolled == null) GangFail("no inmate rolled into a gang in 400 tries");
                    else if (rolled.Inmate().gangTattoo != (chance >= 1f)) GangFail($"intake with a tattoo chance of {chance}: marked {rolled.Inmate().gangTattoo}");
                }

                // The search: a marked member of a gang the warden does not know is found out by the frisk, with its own line; an unmarked one is not; a marked one already known says nothing new.
                Pawn marked = s.Inmate(s.saints, known: false), unmarked = s.Inmate(s.saints, known: false), knownMark = s.Inmate(s.saints, known: true);
                (marked.Inmate().gangTattoo, knownMark.Inmate().gangTattoo) = (true, true);
                int before = r.NewEntries.Count();
                bool a = GangUtility.Notify_Frisked(unmarked, unmarked.Inmate()), b = GangUtility.Notify_Frisked(knownMark, knownMark.Inmate()), c = GangUtility.Notify_Frisked(marked, marked.Inmate());
                if (a || b || !c || !marked.Inmate().gangKnown || unmarked.Inmate().gangKnown)
                    GangFail($"a frisk: unmarked found {a}, a known mark found {b}, the marked member found {c} (known {marked.Inmate().gangKnown}), the unmarked one now known {unmarked.Inmate().gangKnown}");
                if (!r.NewEntries.Skip(before).Any(e => e.text == "RP_Chr_GangTattoo".Translate(marked.LabelShort, s.saints.label).Resolve())) GangFail("the chronicle has no line for the mark found");
                // Nobody else was given away.
                if (unmarked.Inmate().gangKnown) GangFail("a frisk of one gave away another's gang");

                // The real thing, through ticks: a guard walks up to a new arrival waiting for the holding search, and the job's end reveals the mark.
                Pawn arrival = s.Inmate(s.ravens, known: false);
                arrival.Inmate().gangTattoo = true;
                arrival.Inmate().awaitingSearch = true;
                r.Add(() => arrival.Inmate().awaitingSearch = false);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Place(guard, arrival.Position);
                guard.jobs.StartJob(JobMaker.MakeJob(RPContrabandDefOf.RP_Frisk, arrival), JobCondition.InterruptForced);
                int ticks = 0;
                while (ticks < 900 && !arrival.Inmate().gangKnown)
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                if (!arrival.Inmate().gangKnown) GangFail($"the holding search did not show the mark in {ticks} ticks (guard job {guard.CurJobDef?.defName ?? "none"})");
            });
        }

        // ---- doubts and the want -------------------------------------------------------------------------------------------------

        private static void CheckGangDoubts(Map map)
        {
            Guarded("gang doubts", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens, saints = s.saints;
                InmateWantDef leave = RPGangStructureDefOf.RP_Want_LeaveGang;
                Pawn Doubter(bool known = true) => s.Inmate(ravens, known: known);
                bool Doubts(Pawn p) => p.Inmate().gangDoubtTick >= 0;

                // A birth letter (the mail's own worker), a mentor, a reform program, a friend killed in a war: each gives a member doubts, never the leader nor somebody with no gang.
                Pawn birth = Doubter(), leaderB = s.Inmate(ravens, GangRank.Leader), loner = s.Inmate(null);
                foreach (Pawn p in new[] { birth, leaderB, loner }) Wants.Notify_Mail(p, p.Inmate(), RPMailDefOf.RP_Mail_Birth);
                if (!Doubts(birth) || Doubts(leaderB) || Doubts(loner)) GangFail($"a birth letter: member doubts {Doubts(birth)}, leader {Doubts(leaderB)}, no gang {Doubts(loner)}");
                Pawn program = Doubter(), loner2 = s.Inmate(null);
                ProgramDef pass = DefDatabase<ProgramDef>.AllDefsListForReading.First();
                Wants.Notify_ProgramPassed(program, program.Inmate(), pass);
                Wants.Notify_ProgramPassed(loner2, loner2.Inmate(), pass);
                if (!Doubts(program) || Doubts(loner2)) GangFail($"a program passed: member doubts {Doubts(program)}, no gang {Doubts(loner2)}");
                Pawn mentor = s.Inmate(null, days: 20f), mentee = Doubter(), mentee2 = s.Inmate(null);
                r.Add(() =>
                {
                    Mentors.Clear(mentee.Inmate());
                    Mentors.Clear(mentee2.Inmate());
                });
                Mentors.Start(mentor, mentee);
                Mentors.Start(mentor, mentee2);
                if (!Doubts(mentee) || Doubts(mentee2)) GangFail($"a mentor pairing: member doubts {Doubts(mentee)}, no gang {Doubts(mentee2)}");
                // A war death: only with the war on, only the dead one's friends of the same gang.
                Pawn victim = Doubter(), friend = Doubter(), stranger = Doubter(), rival = s.Inmate(saints);
                friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, victim);
                GangExit.Notify_Died(victim, map);
                if (Doubts(friend)) GangFail("a death with no war on gave a friend doubts");
                StoryArc war = GangWarArc.Start(map, ravens, saints);
                if (war == null) GangFail("could not start a war between the Ravens and the Saints to test a war death");
                else
                {
                    GangExit.Notify_Died(victim, map);
                    if (!Doubts(friend) || Doubts(stranger) || Doubts(rival)) GangFail($"a war death: the friend doubts {Doubts(friend)}, a gang mate who was no friend {Doubts(stranger)}, a rival {Doubts(rival)}");
                }

                // The doubt turns into the goal once (the odds are the check's own), and only while fresh.
                Pawn chosen = Doubter(known: false);
                chosen.Inmate().gangDoubtTick = s.Now;
                int entries = r.NewEntries.Count();
                InmateWant w = GangExit.RollDoubt(chosen, chosen.Inmate(), 1f);
                if (w == null || w.def != leave || chosen.Inmate().want != w || chosen.Inmate().gangDoubtTick != -1 || w.step != 0 || w.deadlineTick < 0)
                    GangFail($"a doubt did not become the goal to leave (want {w?.def?.defName}, doubt kept {chosen.Inmate().gangDoubtTick})");
                // Hidden: while the gang is not on the file the goal is not shown and nothing says it exists.
                if (Wants.Shown(chosen.Inmate()) != null || r.NewEntries.Skip(entries).Any(e => e.text.Contains(ravens.label) || e.text.Contains(chosen.LabelShort)))
                    GangFail("a wish to leave a gang the warden has not found was shown, or told in the chronicle");
                if (w != null) Wants.Lose(chosen, chosen.Inmate(), w, Wants.TimeOutReason, thwarted: false, flip: false);
                if (r.NewEntries.Skip(entries).Any(e => e.text.Contains(ravens.label) || e.text.Contains(chosen.LabelShort))) GangFail("giving up a hidden wish to leave told the chronicle about it");
                GangUtility.Reveal(chosen.Inmate());
                chosen.Inmate().want = null;
                Wants.Form(chosen, chosen.Inmate(), leave);
                if (Wants.Shown(chosen.Inmate()) == null) GangFail("a wish to leave a gang that is known is still hidden");

                Pawn none = Doubter(), stale = Doubter(), leaderD = s.Inmate(ravens, GangRank.Leader), busy = Doubter();
                none.Inmate().gangDoubtTick = s.Now;
                stale.Inmate().gangDoubtTick = s.Now - (GangExit.DoubtDays + 1) * GenDate.TicksPerDay;
                leaderD.Inmate().gangDoubtTick = s.Now;
                busy.Inmate().gangDoubtTick = s.Now;
                // (A goal already in progress with a lesser priority is put aside by the wish to leave; one that cannot be replaced is not.)
                Wants.Form(busy, busy.Inmate(), RPWantDefOf.RP_Want_Family, x => x.note = "test");
                InmateWant family = busy.Inmate().want;
                bool noOdds = GangExit.RollDoubt(none, none.Inmate(), 0f) == null && none.Inmate().want?.def != leave && none.Inmate().gangDoubtTick == -1;
                bool staleNo = GangExit.RollDoubt(stale, stale.Inmate(), 1f) == null && stale.Inmate().want?.def != leave;
                bool leaderNo = GangExit.RollDoubt(leaderD, leaderD.Inmate(), 1f) == null && leaderD.Inmate().want?.def != leave;
                InmateWant replaced = GangExit.RollDoubt(busy, busy.Inmate(), 1f);
                if (!noOdds || !staleNo || !leaderNo) GangFail($"doubt rolls: no odds gave a goal {!noOdds}, a stale doubt {!staleNo}, a leader {!leaderNo}");
                if (family != null && (replaced == null || busy.Inmate().want != replaced || family.status != WantStatus.Lost)) GangFail("the wish to leave did not put aside a family goal in progress");
                // The daily pass takes a doubt (whatever the roll), the odds being the game's own.
                Pawn daily = Doubter();
                daily.Inmate().gangDoubtTick = s.Now;
                GangExit.Daily(map);
                if (Doubts(daily)) GangFail("the daily pass left a doubt standing");
                // Somebody who is a leader now, or who left the gang, no longer wants to leave it.
                Pawn promoted = Doubter();
                Wants.Form(promoted, promoted.Inmate(), leave);
                if (promoted.Inmate().want?.def != leave) GangFail("could not form the goal for the no-longer-applies check");
                else
                {
                    GangRanks.SetRank(promoted.Inmate(), GangRank.Leader);
                    Wants.Refresh(promoted, promoted.Inmate());
                    if (promoted.Inmate().want.status != WantStatus.Lost) GangFail("a member who became leader still wants out");
                }
            });
        }

        // ---- the session, the renouncement and the green light -----------------------------------------------------------------

        private static void CheckGangLeaving(Map map)
        {
            Guarded("gang leaving", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens;
                InmateWantDef leave = RPGangStructureDefOf.RP_Want_LeaveGang;
                PrisonMap pm = PrisonMap.Of(map);
                Pawn boss = s.Inmate(ravens, GangRank.Leader, known: false), mate = s.Inmate(ravens, known: false), leaver = s.Inmate(ravens, known: false);
                CompInmate lc = leaver.Inmate();
                // Everyone near, in one room (the beating needs both).
                s.Place(boss, leaver.Position);
                s.Place(mate, leaver.Position);
                InmateWant w = Wants.Form(leaver, lc, leave);
                if (w == null) { GangFail("could not form the goal to leave a gang"); return; }

                // The session: the chaplain's work giver finds the inmate and offers the job (a member with no such goal is not offered); a counsellor does the same.
                InmateCandidates.Shared.Invalidate();
                Pawn chaplain = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Chaplain), counsellor = ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
                // (The real ticks of the talk run the staff's hourly pass: a weak-integrity officer is approached by a fixer, and the boss is one; the bribe, reported, is a seen misconduct that gives the gang away.)
                chaplain.GetComp<CompStaff>().integrity = counsellor.GetComp<CompStaff>().integrity = 100f;
                var giver = new WorkGiver_GangExitSession();
                bool offered = giver.HasJobOnThing(chaplain, leaver) && giver.JobOnThing(chaplain, leaver)?.def == RPGangStructureDefOf.RP_GangExitSession && giver.HasJobOnThing(counsellor, leaver);
                bool listed = InmateCandidates.Shared.ExitSession(map).Contains(leaver) && !InmateCandidates.Shared.ExitSession(map).Contains(mate);
                if (!offered || !listed || giver.HasJobOnThing(chaplain, mate)) GangFail($"the session is offered: chaplain and counsellor {offered}, listed {listed}, offered for a member who does not want out {giver.HasJobOnThing(chaplain, mate)}");
                // The work types: the chaplain's ministry and the counsellor's counselling carry the giver.
                if (!DefDatabase<WorkGiverDef>.AllDefsListForReading.Any(d => d.giverClass == typeof(WorkGiver_GangExitSession) && d.workType == chaplain.GetComp<CompStaff>().role.workTypes.FirstOrDefault())
                    || !DefDatabase<WorkGiverDef>.AllDefsListForReading.Any(d => d.giverClass == typeof(WorkGiver_GangExitSession) && counsellor.GetComp<CompStaff>().role.workTypes.Contains(d.workType)))
                    GangFail("no work giver for the session under the chaplain's or the counsellor's work type");

                // Real ticks: the chaplain walks up, sits down with them, and the end of the talk starts the renouncement.
                int entries = r.NewEntries.Count();
                s.Place(chaplain, leaver.Position);
                chaplain.jobs.StartJob(JobMaker.MakeJob(RPGangStructureDefOf.RP_GangExitSession, leaver), JobCondition.InterruptForced);
                int ticks = 0;
                while (ticks < 2500 && w.step == 0)
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                if (w.step != 1)
                {
                    GangFail($"the chaplain's session did not end in {ticks} ticks (job {chaplain.CurJobDef?.defName ?? "none"}, the inmate is doing {leaver.CurJobDef?.defName ?? "none"} at {leaver.Position} and the chaplain at {chaplain.Position})");
                    return;
                }
                if (!lc.gangKnown || lc.gang != ravens || w.stepTick < s.Now || w.stepTick > s.Now + (GangExit.RenounceHours + 1) * GenDate.TicksPerHour)
                    GangFail($"after the session: gang known {lc.gangKnown}, still a member {lc.gang == ravens}, renouncement at {w.stepTick - s.Now} ticks");
                List<string> told = r.NewEntries.Skip(entries).Select(e => e.text).ToList();
                if (!told.Contains("RP_Chr_GangSession".Translate(chaplain.LabelShort, chaplain.GetComp<CompStaff>().role.label, leaver.LabelShort, ravens.label).Resolve())) GangFail("no chronicle line for the session");
                if (Wants.Shown(lc) == null) GangFail("after the session the goal is still hidden");
                if (boss.Inmate().gangKnown || mate.Inmate().gangKnown) GangFail("the session gave away the gang's other members");
                if (WorkGiver_GangExitSession.Candidate(leaver)) GangFail("the inmate is offered a second session");

                // (The talk took a while: they are all together again, as the beating needs.)
                s.Place(boss, leaver.Position);
                s.Place(mate, leaver.Position);
                // Before the hour the renouncement is not made; at the hour it is: an ex-member, hated, marked, the warden asked.
                Wants.Hourly(leaver, lc);
                if (lc.gang != ravens || w.Resolved) GangFail("renounced before the hours were up");
                w.stepTick = s.Now;
                float tension0 = lc.tension;
                entries = r.NewEntries.Count();
                Wants.Hourly(leaver, lc);
                told = r.NewEntries.Skip(entries).Select(e => e.text).ToList();
                if (lc.gang != null || lc.exGang != ravens || lc.exGangTick != s.Now || !lc.exMarked || lc.gangKnown || GangRanks.Of(lc) != GangRank.Member || w.status != WantStatus.Fulfilled)
                    GangFail($"the renouncement: gang {lc.gang?.defName}, ex-gang {lc.exGang?.defName}, marked {lc.exMarked}, want {w.status}");
                if (!told.Contains("RP_Chr_GangRenounced".Translate(leaver.LabelShort, ravens.label).Resolve()) || !told.Contains("RP_Chr_GreenLight".Translate(ravens.label, leaver.LabelShort).Resolve()))
                    GangFail($"the chronicle lines for the renouncement and the green light are missing ({string.Join(" | ", told)})");
                if (lc.tension <= tension0 || lc.mainCause != "RP_Cause_GreenLight") GangFail($"the threat did not raise tension ({tension0} to {lc.tension}, cause {lc.mainCause})");
                if (!leaver.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPGangStructureDefOf.RP_MarkedByGang)) GangFail("no memory of the threat");
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_LeftGang"));
                if (letter == null || !letter.pawns.Contains(leaver) || letter.decision.Worker.Options(letter).Count != 4 || letter.decision.Worker.TimeoutChoice != 3)
                    GangFail("no decision letter for the warden with its four choices (the last, \"do nothing\", the timeout's) after the renouncement");
                if (boss.Inmate().gangKnown || mate.Inmate().gangKnown) GangFail("the renouncement gave away the gang's other members");

                // The green light on the debts' schedule: nothing before it is due; then the highest-ranked member who can reach them beats them (a recorded fight with the gang's cause),
                // and the next is a few days off.
                GangExit.Hourly(map);
                if (boss.InMentalState || mate.InMentalState || lc.exBeatings != 0) GangFail("a beating before it was due");
                int dueIn = lc.exNextTick - s.Now;
                if (dueIn != DebtSystem.BeatingDelayDays * GenDate.TicksPerDay) GangFail($"the first beating is due in {dueIn} ticks, the debts' schedule is {DebtSystem.BeatingDelayDays * GenDate.TicksPerDay}");
                r.Add(() =>
                {
                    boss.mindState.mentalStateHandler.Reset();
                    mate.mindState.mentalStateHandler.Reset();
                });
                s.Place(boss, leaver.Position);
                s.Place(mate, leaver.Position);
                // (Overdue by half a day, so the guards' eyes do not matter here.)
                lc.exNextTick = s.Now - 13 * GenDate.TicksPerHour;
                int records0 = boss.Inmate().records.Count;
                entries = r.NewEntries.Count();
                if (GangExit.Enforcer(leaver, lc, map) != boss) GangFail("the gang's leader is not the one who goes for the ex-member");
                GangExit.Hourly(map);
                told = r.NewEntries.Skip(entries).Select(e => e.text).ToList();
                if (!boss.InMentalState || boss.MentalStateDef != RPMentalStateDefOf.RP_AttackingInmate || lc.exBeatings != 1 || lc.exNextTick - s.Now != DebtSystem.RepeatDays * GenDate.TicksPerDay)
                    GangFail($"the beating: the leader's state {boss.MentalStateDef?.defName ?? "none"}, beatings {lc.exBeatings}, the next in {lc.exNextTick - s.Now} ticks (expected {DebtSystem.RepeatDays * GenDate.TicksPerDay})");
                // The enforcer's own file does not say "a green light from their old gang": it is not theirs (and would tie an unnamed one to the gang).
                if (boss.Inmate().mainCause == "RP_Cause_GreenLight") GangFail("the enforcer's tension cause is the ex-member's green light");
                if (boss.Inmate().records.Count <= records0 || boss.Inmate().records.Last().def.defName != "RP_Fight") GangFail("the beating is not a recorded fight");
                if (!PrisonRelations.Enemies(leaver, boss) || !leaver.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPGangStructureDefOf.RP_BeatenForLeaving)) GangFail("the beating left no feud or memory");
                // The line names the enforcer only if the warden knows their gang (a fight the staff saw gives it away, as any fight does; one nobody saw does not).
                // (The bystander is looked for as a whole word: a short name like "Al" is inside "Albert".)
                string beating = told.FirstOrDefault(t => t.Contains(leaver.LabelShort) && t.Contains(ravens.label) && t.Contains("walking out"));
                if (beating == null || beating.Contains(boss.LabelShort) != boss.Inmate().gangKnown || told.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{System.Text.RegularExpressions.Regex.Escape(mate.LabelShort)}\b")))
                    GangFail($"the beating line names an enforcer the warden does not know, hides one they know, or is missing (enforcer known {boss.Inmate().gangKnown}: {string.Join(" | ", told)})");
                GangExit.Hourly(map);
                if (lc.exBeatings != 1) GangFail("a second beating straight after the first");
                // Three beatings at the most; then it is dropped.
                lc.exBeatings = (byte)DebtSystem.MaxBeatings;
                lc.exNextTick = s.Now - 13 * GenDate.TicksPerHour;
                boss.mindState.mentalStateHandler.Reset();
                GangExit.Hourly(map);
                if (lc.exMarked || lc.exBeatings != DebtSystem.MaxBeatings) GangFail("the gang went on after its third beating");

                // Protective custody puts them out of reach: no beating, and the daily pass ends the green light with a line.
                lc.exMarked = true;
                lc.exBeatings = 0;
                lc.exNextTick = s.Now - 13 * GenDate.TicksPerHour;
                SecurityLevelDef level0 = lc.security, pc = RPSecurityDefOf.RP_Protective;
                r.Add(() => lc.security = level0);
                lc.security = pc;
                boss.mindState.mentalStateHandler.Reset();
                GangExit.Hourly(map);
                if (boss.InMentalState || lc.exBeatings != 0) GangFail("a beating in protective custody");
                entries = r.NewEntries.Count();
                GangExit.Daily(map);
                if (lc.exMarked || !r.NewEntries.Skip(entries).Any(e => e.text == "RP_Chr_GreenLightOff".Translate(ravens.label, leaver.LabelShort).Resolve())) GangFail("protective custody did not end the green light");
                lc.security = level0;
                // Out of the gang's reach another way: nobody of it on their wing.
                lc.exMarked = true;
                (boss.Inmate().security, mate.Inmate().security) = (pc, pc);
                SecurityLevelDef bossLevel = level0;
                r.Add(() => (boss.Inmate().security, mate.Inmate().security) = (bossLevel, bossLevel));
                // (Somebody of the Ravens who arrived during the check's ticks may be on the wing: then the gang can reach them, and this is not tested.)
                bool otherMembers = map.mapPawns.PrisonersOfColonySpawned.Any(x => x != boss && x != mate && x != leaver && x.Inmate() is { activeFlag: true } xc && xc.gang == ravens && xc.security == level0);
                GangExit.Daily(map);
                if (lc.exMarked && !otherMembers) GangFail("the green light went on with the whole gang off the leaver's wing");
                (boss.Inmate().security, mate.Inmate().security) = (level0, level0);

                // The hatred and the green light end after thirty days.
                lc.exMarked = true;
                lc.exGangTick = s.Now - (GangExit.HatedDays + 1) * GenDate.TicksPerDay;
                GangExit.Daily(map);
                if (lc.exGang != null || lc.exMarked || GangExit.ExActive(lc)) GangFail($"thirty days on, still an ex-member of {lc.exGang?.defName} (marked {lc.exMarked})");

                // The warden's answers. A letter for somebody who is not an ex-member offers nothing; protective custody and another wing move them when there is a bed; nothing leaves them to it.
                lc.exGang = ravens;
                lc.exGangTick = s.Now;
                lc.exMarked = true;
                if (letter != null)
                {
                    PrisonDecisionWorker worker = letter.decision.Worker;
                    int pcAt = LetterOption(letter, "RP_LeftGang_PC"), wingAt = LetterOption(letter, "RP_LeftGang_Wing"), nothingAt = LetterOption(letter, "RP_LeftGang_Nothing");
                    bool pcOk = worker.Disabled(letter, pcAt) == null, wingOk = worker.Disabled(letter, wingAt) == null;
                    SecurityLevelDef otherWing = GangExit.OtherWing(leaver, lc);
                    if (wingOk != (otherWing != null)) GangFail($"the other-wing choice is {(wingOk ? "open" : "shut")} but the wing is {otherWing?.defName ?? "none"}");
                    // (Nothing leaves them as they are.)
                    worker.Choose(letter, map, nothingAt);
                    if (lc.security != level0 || !lc.exMarked) GangFail("doing nothing changed something");
                    if (pcOk)
                    {
                        worker.Choose(letter, map, pcAt);
                        if (lc.security != pc) GangFail("choosing protective custody did not move them there");
                    }
                    else RPLog.Message("[RimPrison:test] gang leaving: no protective custody bed in this prison, the warden's first choice is not exercised");
                    lc.security = level0;
                    if (wingOk)
                    {
                        worker.Choose(letter, map, wingAt);
                        if (lc.security != otherWing) GangFail($"choosing another wing put them at {lc.security?.defName}, expected {otherWing.defName}");
                    }
                    else RPLog.Message("[RimPrison:test] gang leaving: no other wing with room and none of the gang, the warden's second choice is not exercised");
                }
            });
        }

        // ---- the ex-member -------------------------------------------------------------------------------------------------------

        private static void CheckExMember(Map map)
        {
            Guarded("ex-member", map, r =>
            {
                var s = new GangScene(map, r);
                GangDef ravens = s.ravens, saints = s.saints;
                PrisonMap pm = PrisonMap.Of(map);
                TurfBook book = pm.turf;
                // The thoughts: the old gang's members whose gang the warden knows hate them for thirty days, nobody else does (a hidden member's grudge would point them out in the Social tab).
                Pawn exMember = s.Inmate(null), oldMate = s.Inmate(ravens, known: true), hiddenMate = s.Inmate(ravens, known: false), rival = s.Inmate(saints);
                CompInmate ec = exMember.Inmate();
                (ec.exGang, ec.exGangTick) = (ravens, s.Now);
                ThoughtDef thought = RPGangStructureDefOf.RP_ExMember;
                ThoughtState Of(Pawn p) => thought.Worker.CurrentSocialState(p, exMember);
                ThoughtState known = Of(oldMate), hidden = Of(hiddenMate), rivalState = Of(rival);
                if (!known.Active || known.StageIndex != 0 || hidden.Active || rivalState.Active || thought.stages.Count != 1 || thought.stages[0].label.Contains(ravens.label))
                    GangFail($"the old gang's hatred: known mate {known.Active}/{known.StageIndex}, hidden mate {hidden.Active}, a rival {rivalState.Active}, {thought.stages.Count} stages, first stage \"{thought.stages[0].label}\"");
                // (The opinion itself is vanilla's cached sum of such thoughts, which is only refreshed as the game ticks: the worker's state is what is checked.)
                bool lateStillHated;
                ec.exGangTick = s.Now - (GangExit.HatedDays - 1) * GenDate.TicksPerDay;
                bool nearEndHated = Of(oldMate).Active;
                ec.exGangTick = s.Now - (GangExit.HatedDays + 1) * GenDate.TicksPerDay;
                lateStillHated = Of(oldMate).Active;
                ec.exGangTick = s.Now;
                if (!nearEndHated || lateStillHated || thought.stages[0].baseOpinionOffset > -20f) GangFail($"the hatred: still there at day 29 {nearEndHated}, still there at day 31 {lateStillHated}, weight {thought.stages[0].baseOpinionOffset}");

                // Turf: the biggest yard patch, a crew of the Saints, an ex-member and a plain unaffiliated inmate. The ex-member is not one of the people counted there.
                List<ZonePatch> patches = pm.zones.Patches;
                int yardIndex = -1;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone == RPDefOf.RP_Yard && (yardIndex < 0 || patches[i].cells > patches[yardIndex].cells)) yardIndex = i;
                if (yardIndex < 0)
                {
                    GangFail("ex-member turf check: the prison has no yard patch");
                    return;
                }
                ZonePatch yard = patches[yardIndex];
                List<IntVec3> yardCells = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => pm.zones.PatchAt(c) == yardIndex && c.Standable(map)).ToList();
                if (yardCells.Count < 3)
                {
                    GangFail($"ex-member turf check: the yard patch has {yardCells.Count} standable cells");
                    return;
                }
                List<TurfClaim> claims0 = book.claims.ToList();
                var tallies0 = book.tallies.ToList();
                r.Add(() =>
                {
                    book.Replace(claims0);
                    book.tallies.Clear();
                    foreach (KeyValuePair<int, TurfBook.PatchTally> kv in tallies0) book.tallies[kv.Key] = kv.Value;
                });
                book.Replace(new TurfClaim[0]);
                book.tallies.Clear();
                var crew = new List<Pawn>();
                for (int i = 0; i < 4; i++) crew.Add(s.Inmate(saints));
                Pawn plain = s.Inmate(null);
                foreach (Pawn p in crew.Concat(new[] { plain, exMember })) s.Place(p, yardCells[crew.IndexOf(p) >= 0 ? crew.IndexOf(p) % yardCells.Count : 0]);
                int PeopleThere()
                {
                    book.tallies.Clear();
                    GangTurf.Sample(map, pm);
                    return book.tallies.TryGetValue(yard.anchor, out TurfBook.PatchTally t) ? t.people : -1;
                }
                int withEx = PeopleThere();
                ec.exGangTick = s.Now - (GangExit.HatedDays + 1) * GenDate.TicksPerDay;
                int afterWindow = PeopleThere();
                ec.exGangTick = s.Now;
                if (withEx != 5 || afterWindow != 6) GangFail($"turf presence: {withEx} people counted with the ex-member (expected 5: four of the Saints and one unaffiliated), {afterWindow} once the hatred is over (expected 6)");

                // On the old gang's ground they are afraid and tense, as on a rival's; not anywhere else, and not for somebody who never was in the gang.
                book.Set(pm.zones, RPDefOf.RP_Yard, yard.anchor, ravens, s.Now);
                float term = GangTurf.TensionTerm(exMember, ec, pm, out string cause), fear = GangTurf.FearTerm(exMember, ec, pm);
                float plainTerm = GangTurf.TensionTerm(plain, plain.Inmate(), pm, out _), plainFear = GangTurf.FearTerm(plain, plain.Inmate(), pm);
                if (!Mathf.Approximately(term, GangTurf.RivalTurfTension) || !Mathf.Approximately(fear, GangTurf.RivalTurfFear) || cause != "RP_Cause_WingTrouble" || plainTerm != 0f || plainFear != 0f)
                    GangFail($"an ex-member on the old gang's turf: tension {term} (cause {cause}), fear {fear}; an unaffiliated inmate's {plainTerm}/{plainFear}");
                book.Set(pm.zones, RPDefOf.RP_Yard, yard.anchor, saints, s.Now);
                if (GangTurf.TensionTerm(exMember, ec, pm, out _) != 0f || GangTurf.FearTerm(exMember, ec, pm) != 0f) GangFail("an ex-member on somebody else's turf was tense or afraid");
                book.Set(pm.zones, RPDefOf.RP_Yard, yard.anchor, ravens, s.Now);
                ec.exGangTick = s.Now - (GangExit.HatedDays + 1) * GenDate.TicksPerDay;
                if (GangTurf.TensionTerm(exMember, ec, pm, out _) != 0f || GangTurf.FearTerm(exMember, ec, pm) != 0f) GangFail("an ex-member is still afraid on the old gang's turf thirty days on");
                ec.exGangTick = s.Now;
                // Old gang members near them count as rivals; a plain inmate's own surroundings do not change.
                Pawn near = s.Inmate(ravens);
                s.Place(near, exMember.Position);
                s.Place(oldMate, exMember.Position);
                book.Replace(new TurfClaim[0]);
                GangUtility.TensionFromGangs(exMember, ec, pm, out bool rivalsNear, out string nearCause);
                float tensionNear = GangUtility.TensionFromGangs(exMember, ec, pm, out _, out _);
                ec.exGangTick = s.Now - (GangExit.HatedDays + 1) * GenDate.TicksPerDay;
                float tensionLater = GangUtility.TensionFromGangs(exMember, ec, pm, out bool rivalsLater, out _);
                // (Named "trouble on the wing", never a rival gang: the old gang is not one.)
                if (rivalsNear || nearCause != "RP_Cause_WingTrouble" || tensionNear < 2f || rivalsLater || tensionLater != 0f)
                    GangFail($"an ex-member near their old gang: rival gang cause {rivalsNear}, cause {nearCause}, tension {tensionNear:F1}; thirty days on: rivals {rivalsLater}, tension {tensionLater:F1}");
            });
        }

        // ---- the saveload plan ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// The saveload plan's seed: a lieutenant with a tattoo, an ex-member with the green light on and a leaving goal one step in, and a contested succession arc part-way
        /// (what the snapshot's "gx" and want strings and the arcs carry across the reload).
        /// </summary>
        private static void SeedGangStructure(Map map)
        {
            List<Pawn> pool = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null && !PrisonGame.Instance.story.InArc(x)).Take(4).ToList();
            if (pool.Count < 4)
            {
                RPLog.Message("[RimPrison:test] saveload gang structure: fewer than four inmates to seed with");
                return;
            }
            GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
            int now = Find.TickManager.TicksGame;
            CompInmate lieutenant = pool[0].Inmate(), ex = pool[1].Inmate();
            (lieutenant.gang, lieutenant.gangKnown) = (saints, true);
            GangRanks.SetRank(lieutenant, GangRank.Lieutenant);
            (lieutenant.gangTattoo, lieutenant.gangDoubtTick) = (true, now - 777);
            (ex.exGang, ex.exGangTick, ex.exMarked, ex.exNextTick, ex.exBeatings) = (ravens, now - 4321, true, now + 8765, 2);
            ex.want = new InmateWant { def = RPGangStructureDefOf.RP_Want_LeaveGang, status = WantStatus.OnTrack, progress = 0.7f, formedTick = now - 2000, deadlineTick = now + 90000, announced = true, step = 1, stepTick = now + 4000 };
            // A contested succession of the Saints between two lieutenants, due after the run.
            foreach (Pawn p in new[] { pool[2], pool[3] })
            {
                (p.Inmate().gang, p.Inmate().gangKnown) = (saints, true);
                GangRanks.SetRank(p.Inmate(), GangRank.Lieutenant);
            }
            StoryArc arc = PrisonGame.Instance.story.TryStartArc(RPGangStructureDefOf.RP_Arc_Succession, pool[2], pool[3]);
            if (arc != null)
            {
                arc.note = saints.defName;
                arc.nextTick = now + 5 * GenDate.TicksPerDay;
            }
            gangStructureSeedId = pool[0].thingIDNumber;
            RPLog.Message($"[RimPrison:test] saveload gang structure: a lieutenant with a tattoo, an ex-member of the Ravens ({ex.exBeatings} beatings), a leaving goal at step 1, a succession arc {(arc != null)}");
        }
    }
}
