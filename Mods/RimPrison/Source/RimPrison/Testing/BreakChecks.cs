using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The break (<see cref="EscapePlans"/>, gap analysis 7, item 4). A made-up plan (<see cref="BreakScene"/>): a crew of three throwaways with names of their own (so that a line can be searched for them), a tool in
    /// the holder's pockets and a corrupt guard on the crew's payroll, the live plan set aside. Who may start one and when; a frisk of the holder finds the tool and no other link (nothing told or shown names
    /// the leader, the third or the officer), with the one letter; the tool count's shortage says where the tool went and not who has it; a tip, a tap and a session tell one link each; the answers (let it run
    /// with the way unknown and known, the night's hours, turning the inside man, foiling with the cases as suspicion only); readiness, the night and a blackout. Real ticks: "let it run" with a post on the way
    /// and a guard at it catches all three, restrained, with an officer's evidence on the sheet; with no post (the count's scene) the three go unseen and the count finds three missing. The saveload plan's seed
    /// is at the end. Round 2 (the review's fixes, each a check that fails without its fix): the post follows the way, no night through a way that is gone, the inside man tells nothing of his own plan, the marker is not lit
    /// by a part gained out of sight, an inside man turned by internal affairs tells his own part first, caught crew are taken back in, a filled tunnel is not the next way, a night moved on is told as the new day.
    /// Round 4 (the wave review's findings): what the warden knows of the tool's holder and of the night is state of its own, not read back from the words or from the true plan; the gates hold for the whole plan;
    /// a carried member stays; a refused escape start puts the pawn back; a second blackout re-times; a known tunnel filled in says so; a caught crew's camera is read at the way.
    /// Round 5 (the re-review's findings): the tool's row never goes out for a hand-over the warden did not see (the true holder leaving or out of reach); a later find on someone else keeps the first known holder; the
    /// hidden-information helper reads messages and look targets too; the start fails when its own crew is not picked; the camera check's arrival cell is fixed; the saveload seed's plan is held against the hourly pass.
    /// Written without a game run: none of it has been run yet.
    /// </summary>
    public static partial class AutoTest
    {
        private static void BreakFail(string check, string what) => problems0.Add($"break ({check}): {what}");

        private static void CheckBreak(Map map)
        {
            CheckBreakStart(map);
            CheckBreakFrisk(map);
            CheckBreakToolCount(map);
            CheckBreakTold(map);
            CheckBreakAnswers(map);
            CheckBreakReadiness(map);
            CheckBreakCaught(map);
            CheckBreakCaughtCases(map, held: false);
            CheckBreakCaughtCases(map, held: true);
            CheckBreakControl(map);
            // (Round 2: the review's fixes, each a check that fails without its fix.)
            CheckBreakPostFollowsWay(map);
            CheckBreakDeadWay(map);
            CheckBreakInsideManTip(map);
            CheckBreakMarker(map);
            CheckBreakSourceOwnFirst(map);
            CheckBreakTakenBack(map);
            CheckBreakFilledTunnel(map);
            CheckBreakNightMoved(map);
            // (Round 4: the reviews' findings, each a check that fails without its change.)
            CheckBreakKnownHolder(map);
            CheckBreakKnownHolderLeaves(map);
            CheckBreakCountOnlyHolder(map);
            CheckBreakKnownNight(map);
            CheckBreakEligibility(map);
            CheckBreakCarried(map);
            CheckBreakStartRefused(map);
            CheckBreakSecondBlackout(map);
            CheckBreakKnownTunnel(map);
            // (Round 5: the re-review's findings.)
            CheckBreakKnownHolderOut(map);
            CheckBreakLaterFind(map);
        }

        /// <summary>The made-up plan: its crew, its tool, its inside man, and the arc.</summary>
        private sealed class BreakScene
        {
            public Pawn leader, holder, third, officer;
            public Thing tool;
            public StoryArc arc;
            public readonly List<Pawn> cast = new List<Pawn>();

            public EscapePlan Plan => arc.plan;
            public List<Pawn> Crew => new List<Pawn> { leader, holder, third };
            public PlanLink Link(PlanRole role) => arc.plan.links.Find(l => l.role == role);
            public PlanLink LinkOf(Pawn p) => arc.plan.links.Find(l => l.Crew && l.pawnId == p.thingIDNumber);
            public int Known => arc.plan.links.Count(l => l.known);
        }

        private static int breakNames;

        /// <summary>
        /// The live plans, the arcs' clock of starts, the adjudication book, a blackout and the plan's seams are set aside (the plan may take in only <paramref name="cast"/>), and put back by the rollback. The next arc
        /// id is put back only past every arc there is (a check that runs real ticks may start real arcs meanwhile).
        /// </summary>
        private static void BreakSetAside(Rollback r, List<Pawn> cast)
        {
            PrisonGame g = PrisonGame.Instance;
            List<StoryArc> live = g.story.arcs.Where(a => a.plan != null).ToList();
            g.story.arcs.RemoveAll(a => a.plan != null);
            int nextArc0 = g.story.nextArcId;
            var lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
            (AdjudicationBook book0, bool on0, int blackout0) = (g.adjudication, g.adjudicate, g.blackoutUntilTick);
            g.adjudication = new AdjudicationBook();
            g.blackoutUntilTick = -1;
            System.Func<Pawn, bool> pool0 = EscapePlans.Pool;
            EscapePlans.Pool = p => cast.Contains(p);
            r.Add(() =>
            {
                g.story.arcs.RemoveAll(a => a.plan != null);
                g.story.arcs.AddRange(live);
                g.story.nextArcId = System.Math.Max(nextArc0, g.story.arcs.Count == 0 ? 1 : g.story.arcs.Max(a => a.id) + 1);
                g.story.lastArcStart = lastStart0;
                (g.adjudication, g.adjudicate, g.blackoutUntilTick) = (book0, on0, blackout0);
                EscapePlans.Pool = pool0;
                EscapePlans.StartRoll = null;
                EscapePlans.ForceRoll = null;
                EscapePlans.TestOn = false;
            });
        }

        /// <summary>An inmate of the scene: on their feet and processed, nothing on them, no reputation, of the gang, tense, with a long sentence.</summary>
        private static Pawn BreakInmate(Map map, Rollback r, string role, GangDef gang, System.Func<Pawn> maker)
        {
            Pawn p = maker != null ? maker() : Throwaway(map, r);
            p.Name = new NameSingle($"Break{role}{++breakNames}");
            CompInmate c = p.Inmate();
            (c.processed, c.awaitingSearch, c.gang, c.gangKnown, c.gangLeader, c.tension, c.snitchExposed, c.suspected, c.cellSearchPending) = (true, false, gang, false, false, 70f, false, false, false);
            (c.sentenceDays, c.daysAdded, c.admittedTick) = (90, 0, Find.TickManager.TicksGame);
            c.reputations = new List<ReputationDef>();
            c.revealed = new List<ReputationDef>();
            p.inventory?.innerContainer.ClearAndDestroyContents();
            c.RefreshActiveFlag();
            return p;
        }

        private static ThingDef BreakToolDef => ContrabandUtility.AllContraband.FirstOrDefault(d => ContrabandUtility.Ext(d).kind == ContrabandKind.Tool);

        /// <summary>The scene and its plan (the way the fence at <paramref name="way"/>, or where the leader stands); null, a problem noted, when a premise did not take.</summary>
        private static BreakScene MakeBreakScene(Map map, Rollback r, string check, IntVec3? way = null, bool officer = true, bool tool = true, System.Func<Pawn> maker = null)
        {
            var s = new BreakScene();
            Messages.Clear();
            BreakSetAside(r, s.cast);
            GangDef gang = DefDatabase<GangDef>.GetNamedSilentFail("RP_Gang_Ravens") ?? DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
            s.leader = BreakInmate(map, r, "Leader", gang, maker);
            s.holder = BreakInmate(map, r, "Holder", gang, maker);
            s.third = BreakInmate(map, r, "Third", gang, maker);
            s.cast.AddRange(s.Crew);
            if (officer)
            {
                s.officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.officer.Name = new NameSingle($"BreakOfficer{++breakNames}");
                CompStaff sc = s.officer.GetComp<CompStaff>();
                (sc.corrupt, sc.doubleAgent, sc.exposedCorrupt, sc.patron, sc.patronGang, sc.integrity, sc.suspicion) = (true, false, false, s.leader, gang, 20f, 0f);
                sc.evidence.Clear();
                s.cast.Add(s.officer);
            }
            if (tool)
            {
                s.tool = ContrabandUtility.Give(s.holder, BreakToolDef);
                if (s.tool == null)
                {
                    BreakFail(check, $"premise: no tool to give the holder ({BreakToolDef?.defName})");
                    return null;
                }
            }
            // (The way's cell, unless the check gives one: near the leader, with no post near it, so that the live prison's posts catch nobody.)
            IntVec3 cell = way ?? (CellFinder.TryFindRandomCellNear(s.leader.Position, map, 20, c => c.Standable(map) && !GuardPosts.PostNear(map, c, EscapePlans.PostRadius + 1f), out IntVec3 free) ? free : s.leader.Position);
            s.arc = EscapePlans.Make(map, s.leader, new[] { s.holder, s.third }, PlanWay.Fence, cell, -1, s.officer, s.tool);
            bool links = s.Link(PlanRole.Leader) != null && s.Plan.links.Count(l => l.role == PlanRole.Member) == 2 && s.Link(PlanRole.Way) != null
                         && (s.Link(PlanRole.Officer) != null) == officer && (s.Link(PlanRole.Tool)?.thingId == s.tool?.thingIDNumber || !tool && s.Link(PlanRole.Tool) == null);
            if (!links || s.Known != 0 || EscapePlans.Current() != s.arc)
            {
                BreakFail(check, $"premise: the plan did not take ({s.Plan.Describe()}, the running plan is it {EscapePlans.Current() == s.arc})");
                return null;
            }
            return s;
        }

        /// <summary>
        /// Everything the warden has been told since the check began (the chronicle, the letters, the messages), and what Intel shows of the plan: the words a hidden link must not appear in. The scene starts with the
        /// live messages cleared (<see cref="MakeBreakScene"/>), so a message here is the check's own.
        /// </summary>
        private static string BreakTold(Rollback r, StoryArc arc) =>
            string.Join("\n", r.NewEntries.Select(e => e.text).Concat(LiveMessages().Select(m => m.text)).Concat(r.NewLetters.Select(l => l.Label.ToString() + " " + ((l as ChoiceLetter)?.Text.ToString() ?? "")))
                .Concat(EscapePlans.Chain(arc).Select(p => p.text)));

        /// <summary>
        /// The inmates of the scene the warden is pointed at since the check began: the look targets of every message and letter, and the pawns a plan's letter carries. A link nobody found must not be one, and a tool
        /// known from the count alone points at nobody.
        /// </summary>
        private static List<Pawn> BreakPointedAt(Rollback r, BreakScene s) =>
            LiveMessages().Select(m => m.lookTargets).Concat(r.NewLetters.Select(l => l.lookTargets)).Where(t => t != null).SelectMany(t => t.targets).Select(t => t.Thing).OfType<Pawn>()
                .Concat(BreakLetters(r, s.arc).SelectMany(l => l.pawns)).Where(s.cast.Contains).Distinct().ToList();

        private static List<ChoiceLetter_PrisonDecision> BreakLetters(Rollback r, StoryArc arc) => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.arcId == arc.id).ToList();

        // ---- who may start one, and when ----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The leader's gates (tension 60, 20 days left, an escape artist's name, a gang or a ring; a lifer has all the days), never in the first seven days, the day's dice, one plan at a time; a plan that starts has the
        /// mate as crew and a way out, and nothing of it known.
        /// </summary>
        private static void CheckBreakStart(Map map)
        {
            const string check = "the start";
            Guarded("the break: who may start a plan, and when", map, r =>
            {
                var cast = new List<Pawn>();
                BreakSetAside(r, cast);
                GangDef gang = DefDatabase<GangDef>.GetNamedSilentFail("RP_Gang_Ravens") ?? DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                Pawn lead = BreakInmate(map, r, "Starter", gang, null), mate = BreakInmate(map, r, "Mate", gang, null);
                cast.AddRange(new[] { lead, mate });
                // (The mate is under the leader's tension: one who comes along, not a leader of their own.)
                mate.Inmate().tension = EscapePlans.MinTension - 10f;
                CompInmate lc = lead.Inmate();
                EscapePlans.StartRoll = true;
                EscapePlans.TestOn = true;
                // (The def's days between plans: a real plan's start must not be what refuses this one.)
                // (BreakSetAside, above, put the clock of starts back at the end: its copy is the one this removal is undone from.)
                PrisonGame.Instance.story.lastArcStart.Remove(RPBreakDefOf.RP_Arc_Break);
                int clock0 = Find.TickManager.TicksGame;
                if (clock0 < EscapePlans.FirstDays * GenDate.TicksPerDay)
                {
                    if (EscapePlans.TryStart(map) != null) BreakFail(check, $"a plan started on day {clock0 / GenDate.TicksPerDay + 1}, inside the first {EscapePlans.FirstDays} days");
                }
                else RPLog.Message("[RimPrison:test] break: the game is past its first days, the start's clock not judged");
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(System.Math.Max(clock0, (EscapePlans.FirstDays + 1) * GenDate.TicksPerDay));
                foreach (Pawn p in cast) p.Inmate().admittedTick = Find.TickManager.TicksGame;
                // The gates, one at a time.
                lc.tension = EscapePlans.MinTension - 1f;
                bool calm = EscapePlans.Leads(lead, lc);
                lc.tension = EscapePlans.MinTension;
                bool tense = EscapePlans.Leads(lead, lc);
                lc.sentenceDays = 19;
                bool shortTerm = EscapePlans.Leads(lead, lc);
                lc.sentenceDays = -1;
                bool lifer = EscapePlans.Leads(lead, lc);
                lc.sentenceDays = 90;
                lc.gang = null;
                bool alone = EscapePlans.Leads(lead, lc);
                lc.reputations.Add(ReputationDefOf.RP_EscapeArtist);
                bool artist = EscapePlans.Leads(lead, lc);
                lc.reputations.Clear();
                lc.gang = gang;
                if (calm || !tense || shortTerm || !lifer || alone || !artist)
                    BreakFail(check, $"the leader's gates: tension 59 {calm}, 60 {tense}, 19 days left {shortTerm}, a lifer {lifer}, no gang, ring or name {alone}, an escape artist {artist} (want F,T,F,T,F,T)");
                EscapePlans.StartRoll = false;
                if (EscapePlans.TryStart(map) != null) BreakFail(check, "a plan started with the day's dice against it");
                EscapePlans.StartRoll = true;
                StoryArc arc = EscapePlans.TryStart(map);
                if (arc == null)
                {
                    // (The crew is the check's own, the mate who comes along is made here: only a prison with no way out for them is a premise the live prison may not give.)
                    int crew = EscapePlans.PickCrew(map, lead).Count;
                    bool way = EscapePlans.PickWay(map, lead, cast, out _, out _, out _);
                    if (crew == 0 || way) BreakFail(check, $"no plan started with the dice for it, a leader who leads, {crew} to come along (want the mate) and a way out for them: {way}");
                    else RPLog.Message("[RimPrison:test] break: no way out for the crew the check made, the start not judged further");
                    return;
                }
                PlanLink leaderLink = arc.plan.links.Find(l => l.role == PlanRole.Leader);
                if (arc.A != lead || leaderLink?.pawnId != lead.thingIDNumber || !arc.plan.links.Exists(l => l.role == PlanRole.Member && l.pawnId == mate.thingIDNumber) || arc.plan.Find(PlanRole.Way) == null
                    || arc.plan.links.Exists(l => l.known) || !arc.plan.moreShown || arc.def.occupiesCast)
                    BreakFail(check, $"the plan that started: leader {arc.A?.LabelShort} (want {lead.LabelShort}), {arc.plan.Describe()}");
                if (EscapePlans.TryStart(map, force: true) != null) BreakFail(check, "a second plan started while one runs");
                RPLog.Message($"[RimPrison:test] break: a plan started ({arc.plan.way}, {arc.plan.links.Count} links)");
            });
        }

        // ---- the proof: a frisk finds that link and no other ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A frisk of the holder (the tool in their pockets: a frisk searches pockets, a stash is a cell search's) finds the tool: that link known, out of the plan, and no other; nothing told or shown names the leader, the
        /// third or the inside man; the one letter, whose options are foil (the holder is known) and let it run, not turn; the marker for the rest.
        /// </summary>
        private static void CheckBreakFrisk(Map map)
        {
            const string check = "a frisk";
            Guarded("the break: a frisk of the tool holder finds that link and no other", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                Pawn searcher = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                int found = ContrabandUtility.Search(searcher, s.holder, 1f);
                PlanLink tool = s.Link(PlanRole.Tool);
                if (found == 0 || tool is not { known: true, handled: true } || s.Known != 1)
                    BreakFail(check, $"the frisk found {found} thing(s); the tool link {tool?.Describe()}; {s.Known} link(s) known (want the tool's alone): {s.Plan.Describe()}");
                string told = BreakTold(r, s.arc);
                foreach (Pawn hidden in new[] { s.leader, s.third, s.officer })
                    if (Mentions(told, hidden.LabelShort)) BreakFail(check, $"{hidden.LabelShort}, a link nobody found, is named in what the warden was told or is shown: {told}");
                if (!Mentions(told, s.holder.LabelShort)) BreakFail(check, $"the find does not name the holder {s.holder.LabelShort}: {told}");
                List<Pawn> pointed = BreakPointedAt(r, s);
                if (pointed.Count != 1 || pointed[0] != s.holder) BreakFail(check, $"the messages and the letter point at [{BreakNames(pointed)}] (want the holder {s.holder.LabelShort} alone: the find's message, the letter's targets)");
                List<ChoiceLetter_PrisonDecision> letters = BreakLetters(r, s.arc);
                StoryArcWorker w = s.arc.def.Worker;
                string foil = w.Disabled(s.arc, EscapePlanArc.Foil), watch = w.Disabled(s.arc, EscapePlanArc.Watch), turn = w.Disabled(s.arc, EscapePlanArc.TurnIt);
                if (letters.Count != 1 || foil != null || watch != null || turn == null)
                    BreakFail(check, $"{letters.Count} letter(s) for the plan (want 1); foil off '{foil}', let it run off '{watch}', turn off '{turn}' (want on, on, off)");
                if (!EscapePlans.Chain(s.arc).Any(p => !p.Known)) BreakFail(check, "Intel shows no marker for the links still hidden");
                // A second find says nothing new: no second letter.
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Way), "tip", map);
                if (BreakLetters(r, s.arc).Count != 1) BreakFail(check, $"a second link found sent another letter ({BreakLetters(r, s.arc).Count})");
                RPLog.Message($"[RimPrison:test] break: the frisk found the tool ({tool?.shown}), and nothing else");
            });
        }

        /// <summary>The tool count's shortage names the room and the period, not the holder (nobody is known crew, foil is off); a frisk of the holder then names them, and foil is on.</summary>
        private static void CheckBreakToolCount(Map map)
        {
            const string check = "the tool count";
            Guarded("the break: the tool count says where the tool went, not who has it", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check, tool: false);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                ToolLedgers tools0 = g.tools;
                g.tools = new ToolLedgers();
                r.Add(() => g.tools = tools0);
                var pool = new ToolPool { id = g.tools.nextId++, zone = RPLaborDefOf.RP_Workshop, benches = 1, owned = ToolCount.WorkshopPerBench, period = 3 };
                g.tools.pools.Add(pool);
                Thing tool = ContrabandUtility.Give(s.holder, BreakToolDef, tracedTo: pool);
                EscapePlans.DailyPlan(s.arc, map);
                PlanLink link = s.Link(PlanRole.Tool);
                if (tool == null || link == null || link.thingId != tool.thingIDNumber)
                {
                    BreakFail(check, $"premise: the plan did not take the traced tool ({tool?.thingIDNumber}, link {link?.Describe()})");
                    return;
                }
                ToolCount.Counted(pool, null);
                string want = "RP_BreakLine_ToolCounted".Translate(pool.zone.label, 3).Resolve();
                string told = BreakTold(r, s.arc);
                EscapePlans.MaybeAsk(s.arc, map, force: true);
                List<Pawn> pointed = BreakPointedAt(r, s);
                if (!link.known || link.handled || link.shown != want || Mentions(told, s.holder.LabelShort) || EscapePlans.KnownCrew(s.arc, map).Count != 0 || EscapePlans.FoilWhy(s.arc, map) == null || pointed.Count != 0)
                    BreakFail(check, $"after the count the tool link is {link.Describe()} (want known, shown \"{want}\"), the holder named {Mentions(told, s.holder.LabelShort)}, known crew {EscapePlans.KnownCrew(s.arc, map).Count}, the messages and the letter point at [{BreakNames(pointed)}] (want nobody)");
                ContrabandUtility.Search(ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), s.holder, 1f);
                if (!link.handled || !Mentions(link.shown, s.holder.LabelShort) || !EscapePlans.KnownCrew(s.arc, map).Contains(s.holder) || EscapePlans.FoilWhy(s.arc, map) != null)
                    BreakFail(check, $"after a frisk of the holder the tool link is {link.Describe()}, known crew {string.Join(",", EscapePlans.KnownCrew(s.arc, map).Select(p => p.LabelShort))}");
            });
        }

        /// <summary>A tip near the crew tells the way first, in the tip's words; a member of the crew tells nothing, nor a teller with nobody of the crew near; a tap tells the inside man; a session the member's own part, then the leader; one letter for all of it.</summary>
        private static void CheckBreakTold(Map map)
        {
            const string check = "told";
            Guarded("the break: a tip, a tap and a session each tell one link", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                EscapePlans.ForceRoll = true;
                Pawn teller = BreakInmate(map, r, "Teller", null, null);
                string none = EscapePlans.TryTip(teller, new List<Pawn> { teller }), own = EscapePlans.TryTip(s.third, new List<Pawn> { s.holder });
                string tip = EscapePlans.TryTip(teller, new List<Pawn> { s.holder });
                PlanLink way = s.Link(PlanRole.Way);
                if (none != null || own != null || tip == null || !way.known || s.Known != 1 || tip != "RP_TipBreak".Translate(EscapePlans.LinkLine(way)).Resolve())
                    BreakFail(check, $"the tips: nobody of the crew near \"{none}\", from the crew \"{own}\", near the holder \"{tip}\"; the way {way.Describe()}; {s.Known} known (want null, null, the way's words, 1)");
                bool outsider = EscapePlans.Notify_Tapped(teller), tapped = EscapePlans.Notify_Tapped(s.third);
                if (outsider || !tapped || s.Link(PlanRole.Officer) is not { known: true } || s.Known != 2)
                    BreakFail(check, $"the tap: on someone outside the crew {outsider}, on the crew {tapped}; the inside man {s.Link(PlanRole.Officer)?.Describe()}; {s.Known} known (want F, T, known, 2)");
                EscapePlans.Notify_Assessed(s.third, 1f);
                bool ownFirst = s.LinkOf(s.third).known && s.Known == 3;
                EscapePlans.Notify_Assessed(s.third, 1f);
                if (!ownFirst || !s.LinkOf(s.leader).known || s.Known != 4)
                    BreakFail(check, $"the sessions: the member's own part first {ownFirst}, then the leader {s.LinkOf(s.leader).known}; {s.Known} known (want 4): {s.Plan.Describe()}");
                if (BreakLetters(r, s.arc).Count != 1) BreakFail(check, $"{BreakLetters(r, s.arc).Count} letters for one plan (want 1)");
                string told = BreakTold(r, s.arc);
                if (Mentions(told, s.holder.LabelShort)) BreakFail(check, $"the holder {s.holder.LabelShort}, never found, is named: {told}");
            });
        }

        // ---- the answers ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Let it run with the way unknown puts nothing up; with the way known a post goes up on it, for the small hours, and for the night once it is known; turning the inside man makes him a double agent who tells a
        /// link a day; foil flags and searches the known crew, keeps them apart, puts a case of each on the sheet as suspicion only, leaves the unknown member alone, ends the plan and takes the post down.
        /// </summary>
        private static void CheckBreakAnswers(Map map)
        {
            const string check = "the answers";
            Guarded("the break: let it run, turn the inside man, foil it", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                StoryArcWorker w = s.arc.def.Worker;
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Officer), "tap", map);
                w.Choose(s.arc, map, EscapePlanArc.Watch);
                if (EscapePlans.PlanPost(s.arc, map) != null || s.Plan.answer != PlanAnswer.Watch) BreakFail(check, $"let it run with the way unknown: a post {EscapePlans.PlanPost(s.arc, map) != null}, answer {s.Plan.answer}");
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Way), "tip", map);
                Thing post = EscapePlans.LetItRun(s.arc, map);
                if (post != null) r.Add(() => { if (!post.Destroyed) post.Destroy(); });
                CompGuardPost comp = post?.TryGetComp<CompGuardPost>();
                if (comp == null || !post.Position.InHorDistOf(s.Plan.wayCell, GuardPosts.NearRadius) || (comp.fromHour, comp.toHour) != (EscapePlans.SmallFrom, EscapePlans.SmallTo) || !s.Plan.posts.Contains(post.thingIDNumber))
                    BreakFail(check, $"let it run with the way known: post at {post?.Position} for the way at {s.Plan.wayCell}, hours {comp?.fromHour}-{comp?.toHour} (want {EscapePlans.SmallFrom}-{EscapePlans.SmallTo})");
                EscapePlans.ChooseNight(s.arc, map);
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Night), "tip", map);
                int h = EscapePlans.HourAt(map, s.Plan.nightTick);
                Thing again = EscapePlans.LetItRun(s.arc, map);
                if (again != post || comp != null && (comp.fromHour, comp.toHour) != ((h + 23) % 24, (h + 3) % 24))
                    BreakFail(check, $"the night known ({h}:00): the same post {again == post}, hours {comp?.fromHour}-{comp?.toHour} (want {(h + 23) % 24}-{(h + 3) % 24})");
                // Turn the inside man: a double agent, and the next day one more link.
                CompStaff sc = s.officer.GetComp<CompStaff>();
                w.Choose(s.arc, map, EscapePlanArc.TurnIt);
                int known0 = s.Known;
                EscapePlans.DailyPlan(s.arc, map);
                if (!sc.doubleAgent || !s.Link(PlanRole.Officer).turned || s.Plan.answer != PlanAnswer.Turn || w.Disabled(s.arc, EscapePlanArc.TurnIt) == null || s.Known != known0 + 1 || !s.LinkOf(s.leader).known)
                    BreakFail(check, $"turned: double agent {sc.doubleAgent}, link {s.Link(PlanRole.Officer).Describe()}, answer {s.Plan.answer}, turn still on {w.Disabled(s.arc, EscapePlanArc.TurnIt) == null}, known {known0} -> {s.Known}, the leader told {s.LinkOf(s.leader).known}");
                // Foil it: the leader and the third known, the holder not.
                EscapePlans.Reveal(s.arc, s.LinkOf(s.third), "tip", map);
                w.Choose(s.arc, map, EscapePlanArc.Foil);
                CompInmate lc = s.leader.Inmate(), tc = s.third.Inmate(), hc = s.holder.Inmate();
                List<AdjudicationCase> cases = g.adjudication.cases.Where(k => k.offence == RPBreakDefOf.RP_EscapePlot).ToList();
                bool flagged = lc.suspected && lc.cellSearchPending && tc.suspected && tc.cellSearchPending && !hc.suspected && !hc.cellSearchPending;
                bool apart = KeepApart.OrderBetween(s.leader, s.third) != null && KeepApart.OrderBetween(s.leader, s.holder) == null;
                bool thin = cases.Count == 2 && cases.All(k => !k.officer && !k.camera && k.officerName == null && k.inmateId != s.holder.thingIDNumber);
                if (!flagged || !apart || !thin || !s.arc.done || s.Plan.answer != PlanAnswer.Foil || post is { Destroyed: false })
                    BreakFail(check, $"foiled: flagged {flagged}, kept apart {apart}, cases {string.Join(" | ", cases.Select(k => k.Describe()))} (want two, no evidence), over {s.arc.done}, answer {s.Plan.answer}, the post down {post?.Destroyed}");
            });
        }

        /// <summary>
        /// Readiness grows a day by the parts it has and stops short of 1 without a tool; with one it reaches 1 and the night is chosen (an hour of the nights, at least twelve hours off, unknown and not shown); a
        /// blackout while it is ready is the night, now: the break goes. A plan past its days falls apart.
        /// </summary>
        private static void CheckBreakReadiness(Map map)
        {
            const string check = "readiness";
            Guarded("the break: readiness, the night, a blackout", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check, officer: false, tool: false);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                EscapePlans.DailyPlan(s.arc, map);
                float day1 = s.Plan.readiness, want1 = EscapePlans.ReadyBase + EscapePlans.ReadyPerPart;
                for (int i = 0; i < 30; i++) EscapePlans.DailyPlan(s.arc, map);
                if (System.Math.Abs(day1 - want1) > 0.001f || System.Math.Abs(s.Plan.readiness - EscapePlans.CapShort) > 0.001f || s.Plan.nightTick >= 0)
                    BreakFail(check, $"with the way alone readiness was {day1:F3} after a day (want {want1:F3}) and {s.Plan.readiness:F3} after 31 (want {EscapePlans.CapShort}), the night {s.Plan.nightTick}");
                Thing tool = ContrabandUtility.Give(s.holder, BreakToolDef);
                int now = Find.TickManager.TicksGame;
                EscapePlans.DailyPlan(s.arc, map);
                PlanLink night = s.Link(PlanRole.Night);
                int hour = s.Plan.nightTick >= 0 ? EscapePlans.HourAt(map, s.Plan.nightTick) : -1;
                bool anHour = hour == EscapePlans.DarkHour || hour == EscapePlans.StormHour || hour == EscapePlans.HandoverHour;
                if (tool == null || s.Link(PlanRole.Tool) == null || s.Plan.readiness < 1f || night == null || night.known || s.Plan.nightTick < now + EscapePlans.NightLeadHours * GenDate.TicksPerHour || !anHour
                    || EscapePlans.Chain(s.arc).Any(p => p.link == night))
                    BreakFail(check, $"with a tool: readiness {s.Plan.readiness:F3}, the night {night?.Describe()} at {s.Plan.nightTick} (now {now}, hour {hour}), shown {EscapePlans.Chain(s.arc).Any(p => p.link == night)}");
                g.blackoutUntilTick = now + 3 * GenDate.TicksPerHour;
                EscapePlans.Hourly(s.arc, map);
                List<Pawn> gone = s.Crew.Where(p => p.MentalStateDef == RPMentalStateDefOf.RP_Escaping).ToList();
                if (!s.arc.done || s.Plan.night != PlanNight.Blackout || gone.Count != 3)
                    BreakFail(check, $"a blackout while ready: over {s.arc.done}, the night {s.Plan.night}, {gone.Count} of 3 making a break for it");
                foreach (Pawn p in s.Crew) p.MentalState?.RecoverFromState();
                g.blackoutUntilTick = -1;
                // A plan past its days, known in part: it comes to nothing, said once.
                StoryArc old = EscapePlans.Make(map, s.leader, new[] { s.third }, PlanWay.Fence, s.leader.Position);
                EscapePlans.Reveal(old, old.plan.links.Find(l => l.role == PlanRole.Leader), "tip", map);
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + (EscapePlans.MaxDays + 1) * GenDate.TicksPerDay);
                EscapePlans.DailyPlan(old, map);
                string ended = "RP_Chr_BreakEndedLeader".Translate(s.leader.LabelShort).Resolve();
                if (!old.done || r.NewEntries.Count(e => e.text == ended) != 1) BreakFail(check, $"a plan past its {EscapePlans.MaxDays} days: over {old.done}, the line {r.NewEntries.Count(e => e.text == ended)} time(s)");
            });
        }

        // ---- the proof: the night, caught or not ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Let it run at a seeded fence with a post put up on it and a scene guard standing there (the post scene's guard, kept on shift): the night is the next hour's pass, and real ticks run to it. The crew is kept
        /// calm and on its feet all along (<see cref="BreakKeepCrew"/>: a throwaway's own vandalism, restrained by a guard before the night, is a member who cannot go, and caught nobody). Every one who could go
        /// is restrained where they came out, with an escape attempt on file and a case on the sheet with the officer's evidence (the guard named); the caught letter once; the post taken down. A failure
        /// says per member which step dropped.
        /// </summary>
        private static void CheckBreakCaught(Map map)
        {
            const string check = "caught in the act";
            Guarded("the break: let it run, a post on the way, caught in the act (real ticks)", map, r =>
            {
                var ps = new PostScene(map, r);
                Pawn guard = ps.Guard(IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
                IntVec3 site = ps.Site(guard);
                if (!site.IsValid)
                {
                    RPLog.Message("[RimPrison:test] break: no cell for a post that a guard can reach, caught in the act not judged");
                    return;
                }
                BreakScene s = MakeBreakScene(map, r, check, way: site);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                // (Calm from the start: the post scene's ticks cross hours, and a tense crew rolls misconduct in each.)
                var couldGo = new Dictionary<Pawn, string>();
                BreakKeepCrew(s, couldGo);
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Way), "tip", map);
                s.arc.def.Worker.Choose(s.arc, map, EscapePlanArc.Watch);
                Thing postThing = EscapePlans.PlanPost(s.arc, map);
                if (postThing == null)
                {
                    BreakFail(check, $"premise: let it run put no post up on the way at {site}");
                    return;
                }
                r.Add(() => { if (!postThing.Destroyed) postThing.Destroy(); });
                CompGuardPost post = postThing.TryGetComp<CompGuardPost>();
                ps.Open(post);
                ps.Man(post, out bool _, guard);
                ps.Run(() => PostScene.Standing(post), 1500, () => { ps.Open(post); BreakKeepCrew(s, couldGo); });
                if (!PostScene.Standing(post) || post.Stander != guard)
                {
                    RPLog.Message($"[RimPrison:test] break: the scene guard did not stand at the post ({PostWhy(map, post)}), caught in the act not judged");
                    return;
                }
                // The night is the next hour's pass; the crew who go must be in their right mind then (calm: a tense throwaway's own misconduct roll in that pass would take one off the night).
                BreakKeepCrew(s, couldGo);
                s.Plan.readiness = 1f;
                (s.Plan.night, s.Plan.nightTick) = (PlanNight.Dark, Find.TickManager.TicksGame + 1);
                s.Plan.links.Add(new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) });
                int ran = ps.Run(() => s.arc.done, GenDate.TicksPerHour + 200, () => { ps.Open(post); BreakKeepCrew(s, couldGo); });
                if (!s.arc.done)
                {
                    BreakFail(check, $"no break in {ran} real ticks after the night ({s.Plan.Describe()}, {ps.HourText})");
                    return;
                }
                // (Who could go is what the night's pass saw: the last look before it. Of those, every step is judged; one who could not is told, not judged.)
                List<Pawn> goers = s.Crew.Where(p => couldGo.TryGetValue(p, out string why) && why == null).ToList();
                List<AdjudicationCase> cases = g.adjudication.cases.Where(k => k.offence == RPShuttleDefOf.RP_EscapeAttempt && s.Crew.Any(p => p.thingIDNumber == k.inmateId)).ToList();
                string steps = string.Join("; ", s.Crew.Select(p => BreakCaughtSteps(p, goers.Contains(p), couldGo.GetValueOrDefault(p), cases, guard)));
                if (goers.Count == 0)
                {
                    BreakFail(check, $"premise: nobody could go at the night - {steps}");
                    return;
                }
                if (goers.Count < 3) RPLog.Message($"[RimPrison:test] break: {goers.Count} of 3 could go at the night, the rest judged not - {steps}");
                int caughtLetters = r.NewLetters.Count(l => l.Label.Resolve() == "RP_BreakCaughtLabel".Translate().Resolve());
                bool letterNames = r.NewLetters.Where(l => l.Label.Resolve() == "RP_BreakCaughtLabel".Translate().Resolve()).All(l => goers.All(p => ((l as ChoiceLetter)?.Text.Resolve() ?? "").Contains(p.LabelShort)));
                bool stepsDone = goers.All(p => BreakCaughtSteps(p, true, null, cases, guard).EndsWith("ok"));
                if (!stepsDone || cases.Count != goers.Count || caughtLetters != 1 || !letterNames || !postThing.Destroyed)
                    BreakFail(check, $"after the night: {goers.Count} could go, cases {cases.Count} (want {goers.Count}), the letter {caughtLetters} naming them {letterNames}, the post down {postThing.Destroyed} - {steps}");
                RPLog.Message($"[RimPrison:test] break: caught in the act after {ran} real ticks, {goers.Count} went, {cases.Count} cases with an officer's evidence");
            });
        }

        /// <summary>
        /// The catch itself, with no real ticks: the crew who can go (<see cref="EscapePlans.CanGo"/>, as the night's pass takes them) are caught by a guard at the post, and each of them - every one - is restrained,
        /// has the escape attempt on file and a case with the guard's evidence, with one letter naming them. With <paramref name="held"/> one member was restrained before the night for some other offence: they do not go,
        /// get no case and are not named, and the other two have theirs.
        /// </summary>
        private static void CheckBreakCaughtCases(Map map, bool held)
        {
            string check = held ? "caught in the act, one already restrained" : "caught in the act, all three";
            Guarded($"the break: caught in the act, a case for each who went ({(held ? "one held back" : "all three")})", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                Pawn catcher = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                catcher.Name = new NameSingle($"BreakCatcher{++breakNames}");
                foreach (Pawn p in s.Crew)
                {
                    if (p.InMentalState) p.MentalState.RecoverFromState();
                    p.Inmate().tension = 10f;
                }
                if (held) s.holder.health.AddHediff(RPSecurityDefOf.RP_Restrained);
                List<Pawn> goers = s.Crew.Where(EscapePlans.CanGo).ToList();
                if (goers.Count != (held ? 2 : 3) || held && goers.Contains(s.holder))
                {
                    BreakFail(check, $"premise: {goers.Count} could go ({string.Join(", ", s.Crew.Select(p => p.LabelShort + (EscapePlans.CanGo(p) ? "" : " " + BreakWhyNot(p))))})");
                    return;
                }
                EscapePlans.CaughtInTheAct(s.arc, map, goers, catcher);
                List<AdjudicationCase> cases = PrisonGame.Instance.adjudication.cases.Where(k => k.offence == RPShuttleDefOf.RP_EscapeAttempt && s.Crew.Any(p => p.thingIDNumber == k.inmateId)).ToList();
                string steps = string.Join("; ", s.Crew.Select(p => BreakCaughtSteps(p, goers.Contains(p), goers.Contains(p) ? null : BreakWhyNot(p), cases, catcher)));
                string caughtLabel = "RP_BreakCaughtLabel".Translate().Resolve();
                List<Letter> letters = r.NewLetters.Where(l => l.Label.Resolve() == caughtLabel).ToList();
                string text = (letters.FirstOrDefault() as ChoiceLetter)?.Text.Resolve() ?? "";
                bool namesRight = s.Crew.All(p => text.Contains(p.LabelShort) == goers.Contains(p));
                bool filed = s.Crew.All(p => p.Inmate().records.Any(x => x.def == RPShuttleDefOf.RP_EscapeAttempt && x.seen) == goers.Contains(p));
                bool each = goers.All(p => BreakCaughtSteps(p, true, null, cases, catcher).EndsWith("ok"));
                if (!each || !filed || cases.Count != goers.Count || cases.Any(k => goers.All(p => p.thingIDNumber != k.inmateId)) || letters.Count != 1 || !namesRight)
                    BreakFail(check, $"{goers.Count} caught: cases {cases.Count}, the letter {letters.Count} naming only them {namesRight}, escape attempts on file for only them {filed} - {steps}");
                RPLog.Message($"[RimPrison:test] break: {goers.Count} caught at the post, {cases.Count} cases ({(held ? "one held back" : "all three")})");
            });
        }

        /// <summary>
        /// The crew of the scene kept as the night needs them: calm (tension under the misconduct gate, no mental state), not restrained (a restraint from some other offence is taken off, with its punishment hours),
        /// and what keeps each from going noted in <paramref name="couldGo"/> (null: could go) as the night's pass would see it.
        /// </summary>
        private static void BreakKeepCrew(BreakScene s, Dictionary<Pawn, string> couldGo)
        {
            foreach (Pawn p in s.Crew)
            {
                CompInmate c = p.Inmate();
                if (p.InMentalState) p.MentalState.RecoverFromState();
                c.tension = 10f;
                if (p.health.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained) is { } restraint)
                {
                    p.health.RemoveHediff(restraint);
                    c.pendingPunishmentHours = 0;
                }
                couldGo[p] = EscapePlans.CanGo(p) ? null : BreakWhyNot(p);
            }
        }

        /// <summary>What keeps a crew member from going tonight, the way <see cref="EscapePlans.CanGo"/> reads them.</summary>
        private static string BreakWhyNot(Pawn p)
        {
            if (!p.Spawned || p.Dead) return "not on the map";
            if (p.Downed) return p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained) ? "restrained (and so downed)" : "downed";
            if (p.InMentalState) return "in " + p.MentalStateDef.defName;
            if (p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) return "restrained";
            return "out of reach, held, in the hole or on the way out";
        }

        /// <summary>
        /// One crew member's line for the caught check: whether they went, and for one who did, each step (restrained where they came out, the escape attempt on file, the case, the officer's evidence) - the first one
        /// missing names where the case was lost; "ok" ends the line when all were done. A restraint downs (it sets Moving to 0), so "downed" after one is the restraint and no more.
        /// </summary>
        private static string BreakCaughtSteps(Pawn p, bool went, string why, List<AdjudicationCase> cases, Pawn guard)
        {
            if (!went) return $"{p.LabelShort} did not go ({why ?? "not seen"})";
            AdjudicationCase k = cases.Find(x => x.inmateId == p.thingIDNumber);
            string line = $"{p.LabelShort} went";
            if (!p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) return line + ", not restrained (no subdue)";
            if (!p.Inmate().records.Any(x => x.def == RPShuttleDefOf.RP_EscapeAttempt && x.seen)) return line + ", restrained, no escape attempt on file (the misconduct record)";
            if (k == null) return line + $", restrained, on file, no case queued (Queue refused: prisoner of the colony {p.IsPrisonerOfColony}, map {p.MapHeld != null})";
            if (!k.officer || k.officerName != guard.LabelShort) return line + $", restrained, on file, case {k.id} without {guard.LabelShort}'s evidence (officer {k.officer} {k.officerName})";
            return line + ", restrained, on file, case with the officer's evidence ok";
        }

        /// <summary>The control, in the count's scene (nothing watches the way, counts on): no post, the night comes, the three go unseen (no letter), leave the map, and the count finds three missing.</summary>
        private static void CheckBreakControl(Map map)
        {
            const string check = "no post";
            Guarded("the break: no post, the crew go unseen and the count finds them", map, r =>
            {
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                BreakScene s = MakeBreakScene(map, r, check, way: t.s.cell, maker: () => t.Inmate(t.s.cell));
                if (s == null) return;
                s.Plan.readiness = 1f;
                (s.Plan.night, s.Plan.nightTick) = (PlanNight.Dark, Find.TickManager.TicksGame);
                s.Plan.links.Add(new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) });
                if (EscapePlans.Catcher(map, s.Plan) is { } real)
                {
                    RPLog.Message($"[RimPrison:test] break: {real.LabelShort} stands at a post by the count scene's spot, the control not judged");
                    return;
                }
                EscapePlans.Break(s.arc, map);
                List<Pawn> hidden = s.Crew.Where(p => p.Spawned && CountSystem.IsHidden(p) && p.MentalStateDef == RPMentalStateDefOf.RP_Escaping).ToList();
                int seenLetters = r.NewLetters.Count(l => l.Label.Resolve() == "RP_BreakSeenLabel".Translate().Resolve());
                if (!s.arc.done || hidden.Count != 3 || seenLetters != 0 || t.pm.count.hidden.Count != 3)
                {
                    BreakFail(check, $"the night with no post: over {s.arc.done}, {hidden.Count} of 3 gone unseen, {t.pm.count.hidden.Count} held for the count, the seen letter {seenLetters} (told: {string.Join(" // ", t.LettersTold())})");
                    return;
                }
                foreach (Pawn p in hidden) p.ExitMap(false, Rot4.Invalid);
                if (!t.CountEveryWing(t.Guard())) return;
                List<string> missing = t.pm.count.unaccounted.Select(e => e.name).ToList();
                if (missing.Count != 3 || s.Crew.Any(p => !missing.Contains(p.LabelShort)))
                    BreakFail(check, $"the count found {missing.Count} unaccounted ({string.Join(", ", missing)}), want the three of the crew");
                RPLog.Message($"[RimPrison:test] break: with no post the crew went unseen and the count found {missing.Count} missing");
            });
        }

        // ---- round 2: the review's fixes -----------------------------------------------------------------------------------------------------------------

        /// <summary>The scene's way becomes a tunnel from <paramref name="owner"/>'s cell, and a search of that cell finds and fills it (the roll decided): true when the way is out of the plan, known.</summary>
        private static bool BreakFillTunnel(BreakScene s, Pawn owner, Map map)
        {
            PlanLink way = s.Plan.Find(PlanRole.Way);
            if (way == null) return false;
            s.Plan.way = PlanWay.Tunnel;
            (way.pawnId, way.name) = (owner.thingIDNumber, EscapePlans.WayText(map, PlanWay.Tunnel, s.Plan.wayCell, owner.thingIDNumber));
            bool? roll0 = EscapePlans.ForceRoll;
            EscapePlans.ForceRoll = true;
            EscapePlans.Notify_CellSearched(owner);
            EscapePlans.ForceRoll = roll0;
            return way.known && way.handled && s.Plan.Find(PlanRole.Way) == null;
        }

        /// <summary>A new way for the plan, as the daily pass takes one: the fence at <paramref name="cell"/>, unknown.</summary>
        private static PlanLink BreakNewWay(BreakScene s, Map map, IntVec3 cell)
        {
            (s.Plan.way, s.Plan.wayCell) = (PlanWay.Fence, cell);
            var link = new PlanLink { role = PlanRole.Way, name = EscapePlans.WayText(map, PlanWay.Fence, cell, -1) };
            s.Plan.links.Add(link);
            return link;
        }

        /// <summary>
        /// The post follows the way (review must-fix 1). Let it run puts a post on the known way; a cell search finds and fills the way's tunnel, the plan takes a new way far from it and a tip tells it. Post guards is on again (not
        /// "a post is up"), and it takes the old post down and puts one up on the new way, the plan's only post.
        /// </summary>
        private static void CheckBreakPostFollowsWay(Map map)
        {
            const string check = "the post follows the way";
            Guarded("the break: a post on a way the plan gave up comes down, and goes up on the new way", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                IntVec3 first = s.Plan.wayCell;
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Way), "tip", map, announce: false);
                Thing old = EscapePlans.LetItRun(s.arc, map);
                if (old == null)
                {
                    RPLog.Message($"[RimPrison:test] break: no cell for a post on the way at {first}, the post following the way not judged");
                    return;
                }
                r.Add(() =>
                {
                    if (!old.Destroyed) old.Destroy();
                });
                if (!BreakFillTunnel(s, s.holder, map))
                {
                    BreakFail(check, $"premise: a search of the tunnel's cell did not take the way out ({s.Plan.Describe()})");
                    return;
                }
                float apart = EscapePlans.PostRadius * 2f + 2f;
                if (!CellFinder.TryFindRandomCellNear(first, map, 40, c => c.Standable(map) && !c.InHorDistOf(first, apart) && !GuardPosts.PostNear(map, c, EscapePlans.PostRadius + 1f), out IntVec3 second))
                {
                    RPLog.Message($"[RimPrison:test] break: no cell for a second way {apart:F0}+ cells from {first}, the post following the way not judged");
                    return;
                }
                EscapePlans.Reveal(s.arc, BreakNewWay(s, map, second), "tip", map, announce: false);
                string why = EscapePlans.PostWhy(s.arc, map);
                Thing post = EscapePlans.LetItRun(s.arc, map);
                if (post != null && post != old)
                    r.Add(() =>
                    {
                        if (!post.Destroyed) post.Destroy();
                    });
                if (why == null && post == null && old.Destroyed)
                {
                    RPLog.Message($"[RimPrison:test] break: the old post came down but there is no cell for a post on the new way at {second}, the new post not judged");
                    return;
                }
                if (why != null || post == null || post == old || !post.Position.InHorDistOf(second, EscapePlans.PostRadius) || !old.Destroyed || s.Plan.posts.Contains(old.thingIDNumber) || EscapePlans.PlanPost(s.arc, map) != post)
                    BreakFail(check, $"the way moved from {first} to {second}: Post guards off \"{why}\" (want on), the post {post?.Position} (the old one at {old.Position}: the same {post == old}, down {old.Destroyed}, still the plan's {s.Plan.posts.Contains(old.thingIDNumber)})");
            });
        }

        /// <summary>
        /// No night through a way that is gone (review must-fix 2). The night comes, known to the warden, after the way's tunnel was found and filled and before any new way: nobody goes, the plan runs on with the night off
        /// (readiness under 1, the known night out). With a new way the next daily pass chooses a night again, at least twelve hours off, unknown, and the post's hours do not read it through the old night (the small hours).
        /// </summary>
        private static void CheckBreakDeadWay(Map map)
        {
            const string check = "a dead way";
            Guarded("the break: no night through a way that is gone", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                // (Should they go after all, nothing of it outlives the check.)
                r.Add(() =>
                {
                    foreach (Pawn p in s.Crew) p.MentalState?.RecoverFromState();
                    PrisonMap.Of(map)?.count.hidden.RemoveAll(h => s.Crew.Contains(h.pawn));
                });
                IntVec3 cell = s.Plan.wayCell;
                if (!BreakFillTunnel(s, s.holder, map))
                {
                    BreakFail(check, $"premise: a search of the tunnel's cell did not take the way out ({s.Plan.Describe()})");
                    return;
                }
                s.Plan.readiness = 1f;
                (s.Plan.night, s.Plan.nightTick) = (PlanNight.Dark, Find.TickManager.TicksGame);
                var night = new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) };
                s.Plan.links.Add(night);
                EscapePlans.Reveal(s.arc, night, "tip", map, announce: false);
                EscapePlans.Hourly(s.arc, map);
                int went = s.Crew.Count(p => p.MentalStateDef == RPMentalStateDefOf.RP_Escaping || p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained));
                if (s.arc.done || went > 0 || s.Plan.nightTick >= 0 || s.Plan.readiness >= 1f || !night.handled || s.Plan.Find(PlanRole.Night) != null)
                    BreakFail(check, $"the night with the way gone: over {s.arc.done}, {went} of 3 went, the night at {s.Plan.nightTick}, readiness {s.Plan.readiness:F3}, the known night {night.Describe()} (want not over, none, -1, under 1, out)");
                BreakNewWay(s, map, cell);
                EscapePlans.DailyPlan(s.arc, map);
                PlanLink next = s.Plan.Find(PlanRole.Night);
                (int from, int to) hours = EscapePlans.PostHours(map, s.Plan);
                if (next == null || next.known || s.Plan.nightTick < Find.TickManager.TicksGame + EscapePlans.NightLeadHours * GenDate.TicksPerHour || hours != (EscapePlans.SmallFrom, EscapePlans.SmallTo))
                    BreakFail(check, $"with a new way the next day: the night {next?.Describe()} at {s.Plan.nightTick} (now {Find.TickManager.TicksGame}), the post's hours {hours.from}-{hours.to} (want a new night, unknown, the small hours {EscapePlans.SmallFrom}-{EscapePlans.SmallTo})");
            });
        }

        /// <summary>
        /// The inside man does not tell on his own plan (review must-fix 3). The unit officer of the wing of a crew member, on the crew's payroll: neither his wing's tip (the unit officer's own path) nor the plan's tip tells a
        /// link; turned, he tells one.
        /// </summary>
        private static void CheckBreakInsideManTip(Map map)
        {
            const string check = "the inside man's tip";
            Guarded("the break: the inside man, unit officer of the crew's wing, tells nothing of the plan until turned", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                EscapePlans.ForceRoll = true;
                CompStaff sc = s.officer.GetComp<CompStaff>();
                System.Func<Pawn, bool> tipPool0 = InformantSystem.TipPool;
                // (The wing's tip is about the third alone: the holder's tool would be the wing's contraband tip first.)
                InformantSystem.TipPool = p => p == s.third;
                r.Add(() => InformantSystem.TipPool = tipPool0);
                sc.unitWing = s.third.Inmate().security;
                string wingTip = UnitOfficers.StaffTip(s.officer, sc, map);
                string planTip = EscapePlans.TryTip(s.officer, new List<Pawn> { s.third }, map: map);
                if (s.Known != 0 || planTip != null)
                    BreakFail(check, $"the inside man told of his own plan: his wing's tip \"{wingTip}\", the plan's tip \"{planTip}\"; {s.Known} known (want none): {s.Plan.Describe()}");
                s.Link(PlanRole.Officer).turned = true;
                string turned = EscapePlans.TryTip(s.officer, new List<Pawn> { s.third }, map: map);
                if (turned == null || s.Known != 1) BreakFail(check, $"the inside man turned: his tip \"{turned}\", {s.Known} known (want a link)");
            });
        }

        /// <summary>
        /// The marker is never lit by a part gained out of sight (review must-fix 4). Every link found and the night not chosen: the marker reads the same before and after the night is chosen. The night found too: off. Then
        /// the tool is taken; at the next daily pass the marker reads the same whether or not the crew has got another tool.
        /// </summary>
        private static void CheckBreakMarker(Map map)
        {
            const string check = "the marker";
            Guarded("the break: the marker does not show a night chosen or a tool gained out of sight", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                foreach (PlanLink l in s.Plan.links.ToList()) EscapePlans.Reveal(s.arc, l, "tip", map, announce: false);
                s.Plan.readiness = 0.5f;
                EscapePlans.DailyPlan(s.arc, map);
                bool unchosen = s.Plan.moreShown;
                s.Plan.readiness = 0.99f;
                EscapePlans.DailyPlan(s.arc, map);
                PlanLink night = s.Plan.Find(PlanRole.Night);
                if (night is not { known: false } || s.Known != s.Plan.links.Count - 1)
                {
                    BreakFail(check, $"premise: every link but the night known and the night chosen at readiness 1 ({s.Plan.Describe()})");
                    return;
                }
                bool chosen = s.Plan.moreShown;
                if (unchosen != chosen) BreakFail(check, $"every link found: the marker {unchosen} the day before the night was chosen and {chosen} the day it was (the warden reads the night off it)");
                EscapePlans.Reveal(s.arc, night, "tip", map, announce: false);
                bool allFound = s.Plan.moreShown;
                ContrabandUtility.Search(ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), s.holder, 1f);
                if (s.Plan.Find(PlanRole.Tool) != null)
                {
                    BreakFail(check, $"premise: a frisk of the holder did not take the tool out ({s.Plan.Describe()})");
                    return;
                }
                EscapePlans.DailyPlan(s.arc, map);
                bool noTool = s.Plan.moreShown;
                Thing again = ContrabandUtility.Give(s.holder, BreakToolDef);
                EscapePlans.DailyPlan(s.arc, map);
                if (again == null || s.Plan.Find(PlanRole.Tool) is not { known: false } gained || gained.thingId != again.thingIDNumber)
                {
                    BreakFail(check, $"premise: the plan did not take up another tool given to the holder ({again?.thingIDNumber}, {s.Plan.Describe()})");
                    return;
                }
                if (allFound || noTool != s.Plan.moreShown)
                    BreakFail(check, $"with everything found the marker is {allFound} (want off); the tool taken, the next day it is {noTool} with no other tool and {s.Plan.moreShown} with one gained out of sight (want alike)");
            });
        }

        /// <summary>
        /// An inside man turned by internal affairs, not through the plan, tells his own part first (review must-fix 5): the next daily pass reveals him as the inside man and nothing else, the one after that the leader.
        /// </summary>
        private static void CheckBreakSourceOwnFirst(Map map)
        {
            const string check = "turned by internal affairs";
            Guarded("the break: an inside man internal affairs turned tells his own part first", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                CorruptionSystem.Turn(s.officer, s.officer.GetComp<CompStaff>(), map);
                EscapePlans.DailyPlan(s.arc, map);
                PlanLink officer = s.Link(PlanRole.Officer);
                string told = BreakTold(r, s.arc);
                if (officer is not { known: true, turned: true } || s.Known != 1 || !Mentions(told, s.officer.LabelShort) || Mentions(told, s.leader.LabelShort))
                    BreakFail(check, $"the day after internal affairs turned the inside man: his link {officer?.Describe()}, {s.Known} known (want his own alone): {told}");
                EscapePlans.DailyPlan(s.arc, map);
                if (s.Known != 2 || !s.LinkOf(s.leader).known) BreakFail(check, $"the next day {s.Known} known (want 2, the leader too): {s.Plan.Describe()}");
            });
        }

        /// <summary>
        /// Caught crew are taken back in (review should-fix 6): set out from a prison cell, caught at a way outside the wall, each lies restrained back in the cell, not outside where a restraint run out is a breach.
        /// </summary>
        private static void CheckBreakTakenBack(Map map)
        {
            const string check = "taken back in";
            Guarded("the break: caught crew are taken back in", map, r =>
            {
                Building_Bed bed = EmptyCellBed(map);
                IntVec3 home = bed?.GetRoom()?.Cells.FirstOrDefault(c => c.Standable(map) && c.GetEdifice(map) == null) ?? IntVec3.Invalid;
                if (!home.IsValid || !EscapePlans.FindOutside(map, home, unlit: false, out IntVec3 way))
                {
                    RPLog.Message($"[RimPrison:test] break: no empty prison cell with a way out near it (cell {home}), the crew taken back not judged");
                    return;
                }
                BreakScene s = MakeBreakScene(map, r, check, way: way);
                if (s == null) return;
                foreach (Pawn p in s.Crew) CountScene.Place(p, home);
                // (A camera over the way and none over the cell they set out from: the case reads what saw them where they were caught, not where they are put back.)
                PrisonMap pm = PrisonMap.Of(map);
                List<CompSecurityCamera> cameras0 = pm.Cameras.ToList();
                r.Add(() =>
                {
                    pm.Cameras.Clear();
                    pm.Cameras.AddRange(cameras0);
                });
                pm.Cameras.Clear();
                bool lit0 = TensionUtility.TestGlow != null;
                TensionUtility.TestGlow ??= new Dictionary<IntVec3, float>();
                TensionUtility.TestGlow[way] = 1f;
                r.Add(() =>
                {
                    if (!lit0) TensionUtility.TestGlow = null;
                    else TensionUtility.TestGlow.Remove(way);
                });
                // (They come out at the way itself, the cell the camera and the light were checked at: not at a random cell two off it, where a corner or a wall may hide them.)
                EscapePlans.ArriveAt = way;
                r.Add(() => EscapePlans.ArriveAt = null);
                CompSecurityCamera cam = SpawnDarkCamera(map, r, CellFinder.StandableCellNear(way, map, 3f));
                bool watched = cam != null && CourtUtil.CameraOver(map, way) && !CourtUtil.CameraOver(map, home);
                if (!watched) RPLog.Message($"[RimPrison:test] break: no camera over the way at {way} and none over {home}, the camera's evidence not judged");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                EscapePlans.CaughtInTheAct(s.arc, map, s.Crew, guard);
                List<AdjudicationCase> filed = PrisonGame.Instance.adjudication.cases.Where(k => k.offence == RPShuttleDefOf.RP_EscapeAttempt && s.Crew.Any(p => p.thingIDNumber == k.inmateId)).ToList();
                if (watched && (filed.Count != 3 || filed.Any(k => !k.camera)))
                    BreakFail(check, $"a camera covers the way at {way} (not {home}, where they set out from): {filed.Count} case(s) filed, {filed.Count(k => k.camera)} with the camera's evidence (want 3 of 3)");
                foreach (Pawn p in s.Crew)
                    if (!p.Spawned || p.Position != home || !p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained) || p.GetDistrict()?.TouchesMapEdge == true)
                        BreakFail(check, $"{p.LabelShort}, caught at the way at {way}, lies at {p.Position} (want {home}, the cell they set out from), restrained {p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)}, outside the wall {p.GetDistrict()?.TouchesMapEdge}");
            });
        }

        /// <summary>
        /// A filled tunnel does not come straight back (review should-fix 7): an inmate of the live prison whose cell a tunnel could be dug from (only read) is made the owner of the plan's tunnel; a search finds and fills it,
        /// and the ways the plan may take next have no tunnel from that inmate.
        /// </summary>
        private static void CheckBreakFilledTunnel(Map map)
        {
            const string check = "a filled tunnel";
            Guarded("the break: a tunnel found and filled is not the plan's next way", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                Pawn digger = map.mapPawns.PrisonersOfColonySpawned.Where(p => !s.cast.Contains(p) && InmateUtility.CellOf(p) is { IsPrisonCell: true }).Take(20)
                    .FirstOrDefault(p => EscapePlans.WayOptions(map, p, new List<Pawn> { p }).Exists(o => o.way == PlanWay.Tunnel && o.owner == p.thingIDNumber));
                if (digger == null)
                {
                    RPLog.Message("[RimPrison:test] break: no inmate in a cell a tunnel could be dug from, the filled tunnel not judged");
                    return;
                }
                if (!BreakFillTunnel(s, digger, map))
                {
                    BreakFail(check, $"premise: a search of {digger.LabelShort}'s cell did not take the tunnel out ({s.Plan.Describe()})");
                    return;
                }
                var options = EscapePlans.WayOptions(map, digger, new List<Pawn> { digger }, s.Plan);
                if (options.Exists(o => o.way == PlanWay.Tunnel && o.owner == digger.thingIDNumber))
                    BreakFail(check, $"the tunnel from {digger.LabelShort}'s cell was found and filled, and is again a way the plan may take: {string.Join(", ", options.Select(o => $"{o.way} {o.cell}"))}");
            });
        }

        /// <summary>
        /// The night's words follow the night (review should-fix 8): nobody of the crew able to go (all in the hole), the night moves a day on, and the unknown night link a tip would tell names the new day.
        /// </summary>
        private static void CheckBreakNightMoved(Map map)
        {
            const string check = "the night moved";
            Guarded("the break: a night moved a day on is told as the new day", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                int now = Find.TickManager.TicksGame;
                s.Plan.readiness = 1f;
                (s.Plan.night, s.Plan.nightTick) = (PlanNight.Dark, now);
                var night = new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) };
                s.Plan.links.Add(night);
                string before = night.name;
                foreach (Pawn p in s.Crew) p.Inmate().forcedActivity = RPSecurityDefOf.RP_Solitary;
                EscapePlans.Hourly(s.arc, map);
                foreach (Pawn p in s.Crew) p.Inmate().forcedActivity = null;
                if (s.arc.done || s.Plan.nightTick != now + GenDate.TicksPerDay || night.name == before || night.name != EscapePlans.NightText(map, s.Plan))
                    BreakFail(check, $"nobody able to go: over {s.arc.done}, the night at {s.Plan.nightTick} (want {now + GenDate.TicksPerDay}), its words \"{night.name}\" (were \"{before}\", want \"{EscapePlans.NightText(map, s.Plan)}\")");
            });
        }

        // ---- round 4: the wave review's findings --------------------------------------------------------------------------------------------------------

        /// <summary>The scene's inmate under another name, and the same name in every link of theirs (a link keeps the name it was made with).</summary>
        private static void BreakRename(BreakScene s, Pawn p, string name)
        {
            p.Name = new NameSingle(name);
            foreach (PlanLink l in s.Plan.links)
                if (l.pawnId == p.thingIDNumber && l.role is PlanRole.Leader or PlanRole.Member or PlanRole.Tool) l.name = name;
        }

        /// <summary>The tool leaves one pair of pockets for another's, with nobody taking it (no frisk, no search): nothing tells the plan's link until its daily pass.</summary>
        private static bool BreakPassTool(BreakScene s, Pawn to) => s.tool.holdingOwner is { } from && from.TryTransferToContainer(s.tool, to.inventory.innerContainer) && s.tool.holdingOwner == to.inventory.innerContainer;

        /// <summary>
        /// Alan holds the tool, the warden has been told so (a tip), and it passes unseen to Al, a word of "Alan" (the daily pass moves the true holder): the scene for the holder checks. The leader is Zed.
        /// </summary>
        private static BreakScene BreakKnownHolderScene(Map map, Rollback r, string check)
        {
            BreakScene s = MakeBreakScene(map, r, check);
            if (s == null) return null;
            BreakRename(s, s.holder, "Alan");
            BreakRename(s, s.third, "Al");
            BreakRename(s, s.leader, "Zed");
            PlanLink tool = s.Link(PlanRole.Tool);
            EscapePlans.Reveal(s.arc, tool, "tip", map);
            if (tool.knownHolderId != s.holder.thingIDNumber || tool.shown != "RP_BreakLine_Tool".Translate("Alan").Resolve())
            {
                BreakFail(check, $"premise: a tip about the tool did not make Alan its known holder ({tool.Describe()})");
                return null;
            }
            if (!BreakPassTool(s, s.third))
            {
                BreakFail(check, "premise: the tool did not pass to the third's pockets");
                return null;
            }
            EscapePlans.Sync(s.arc, map);
            if (tool.pawnId != s.third.thingIDNumber || tool.name != "Al" || tool.knownHolderId != s.holder.thingIDNumber)
            {
                BreakFail(check, $"premise: after the unseen hand-over the true holder is Al and the known one stays Alan ({tool.Describe()})");
                return null;
            }
            return s;
        }

        private static string BreakNames(IEnumerable<Pawn> pawns) => string.Join(",", pawns.Select(p => p.LabelShort));

        /// <summary>
        /// What the warden knows of the tool's holder is state, not a word of the line (review BR-2, opus 1). Alan is the known holder and the tool passes unseen to Al (a word of "Alan": the old test read the line for the
        /// holder's name), then to Zed (no word of it): each time the known crew, foil, Intel's rows and focus, and the letter's targets are Alan and only Alan; foiling flags Alan, not Al or Zed, and its words name Alan alone.
        /// </summary>
        private static void CheckBreakKnownHolder(Map map)
        {
            const string check = "the known holder";
            Guarded("the break: a tool passed on unseen leaves the known holder known, and the new one unknown (Al and Alan)", map, r =>
            {
                BreakScene s = BreakKnownHolderScene(map, r, check);
                if (s == null) return;
                PlanLink tool = s.Link(PlanRole.Tool);
                void Judge(string when)
                {
                    List<Pawn> crew = EscapePlans.KnownCrew(s.arc, map);
                    Pawn focus = EscapePlans.Focus(s.arc, tool, map);
                    var rows = EscapePlans.Chain(s.arc).Where(p => p.Known).Select(p => EscapePlans.Focus(s.arc, p.link, map)).Where(p => p != null).ToList();
                    EscapePlans.MaybeAsk(s.arc, map, force: true);
                    List<Pawn> targets = BreakLetters(r, s.arc).Last().pawns;
                    List<Pawn> pointed = BreakPointedAt(r, s);
                    if (crew.Count != 1 || crew[0] != s.holder || EscapePlans.FoilWhy(s.arc, map) != null || focus != s.holder || rows.Any(p => p != s.holder) || targets.Count != 1 || targets[0] != s.holder
                        || pointed.Count != 1 || pointed[0] != s.holder)
                        BreakFail(check, $"{when}: known crew [{BreakNames(crew)}], foil off \"{EscapePlans.FoilWhy(s.arc, map)}\", focus {focus?.LabelShort}, Intel's rows point at [{BreakNames(rows)}], the letter's targets [{BreakNames(targets)}], the messages and letters point at [{BreakNames(pointed)}] (want Alan alone); {tool.Describe()}");
                }
                Judge("passed to Al");
                if (!BreakPassTool(s, s.leader))
                {
                    BreakFail(check, "premise: the tool did not pass to the leader's pockets");
                    return;
                }
                EscapePlans.Sync(s.arc, map);
                if (tool.pawnId != s.leader.thingIDNumber || tool.knownHolderId != s.holder.thingIDNumber) BreakFail(check, $"premise: after the second hand-over the true holder is Zed ({tool.Describe()})");
                Judge("passed on to Zed");
                EscapePlans.Foil(s.arc, map);
                string foiled = "RP_Chr_BreakFoiled".Translate("Alan").Resolve();
                bool flagged = s.holder.Inmate().suspected && s.holder.Inmate().cellSearchPending;
                bool others = s.third.Inmate().suspected || s.leader.Inmate().suspected || KeepApart.OrderBetween(s.holder, s.third) != null || KeepApart.OrderBetween(s.holder, s.leader) != null
                              || PrisonGame.Instance.adjudication.cases.Any(k => k.inmateId == s.third.thingIDNumber || k.inmateId == s.leader.thingIDNumber);
                List<Pawn> pointedAfter = BreakPointedAt(r, s);
                bool messaged = LiveMessages().Any(m => string.Equals(m.text, "RP_BreakFoiled".Translate("Alan").Resolve(), System.StringComparison.OrdinalIgnoreCase));
                if (!flagged || others || r.NewEntries.Count(e => e.text == foiled) != 1 || !messaged || pointedAfter.Count != 1 || pointedAfter[0] != s.holder)
                    BreakFail(check, $"foiled: Alan flagged {flagged}, Al or Zed flagged, kept apart or put on the sheet {others}, the chronicle line naming Alan alone {r.NewEntries.Count(e => e.text == foiled)} time(s) (want T, F, 1), the message naming Alan alone {messaged}, the messages and letters point at [{BreakNames(pointedAfter)}] (want Alan)");
            });
        }

        /// <summary>The holder the warden knew of leaves (released, transferred, dead): the tool's link no longer knows a holder, whoever has it now - nobody is left known, and foil is off again.</summary>
        private static void CheckBreakKnownHolderLeaves(Map map)
        {
            const string check = "the known holder leaves";
            Guarded("the break: a known holder who leaves is not known any more", map, r =>
            {
                BreakScene s = BreakKnownHolderScene(map, r, check);
                if (s == null) return;
                PlanLink tool = s.Link(PlanRole.Tool);
                s.arc.def.Worker.Notify_Leaving(s.arc, s.holder);
                if (tool.knownHolderId != -1 || EscapePlans.KnownCrew(s.arc, map).Count != 0 || EscapePlans.FoilWhy(s.arc, map) == null || tool.pawnId != s.third.thingIDNumber || tool.handled)
                    BreakFail(check, $"Alan, the known holder, left: the link {tool.Describe()} (want no known holder, the tool still Al's), known crew [{BreakNames(EscapePlans.KnownCrew(s.arc, map))}], foil off \"{EscapePlans.FoilWhy(s.arc, map)}\" (want on)");
            });
        }

        /// <summary>
        /// A tool known from the count alone names nobody, even when the holder's nickname is a word of the count's line (review BR-2): the holder is called "tool", as the line is ("the tool for it went missing from the
        /// workshop"), and still nobody is known crew, foil is off, and the tool's row points at nobody.
        /// </summary>
        private static void CheckBreakCountOnlyHolder(Map map)
        {
            const string check = "the count and a nickname";
            Guarded("the break: the tool count names nobody, whatever the holder is called", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check, tool: false);
                if (s == null) return;
                BreakRename(s, s.holder, "tool");
                PrisonGame g = PrisonGame.Instance;
                ToolLedgers tools0 = g.tools;
                g.tools = new ToolLedgers();
                r.Add(() => g.tools = tools0);
                var pool = new ToolPool { id = g.tools.nextId++, zone = RPLaborDefOf.RP_Workshop, benches = 1, owned = ToolCount.WorkshopPerBench, period = 3 };
                g.tools.pools.Add(pool);
                Thing tool = ContrabandUtility.Give(s.holder, BreakToolDef, tracedTo: pool);
                EscapePlans.DailyPlan(s.arc, map);
                PlanLink link = s.Link(PlanRole.Tool);
                if (tool == null || link == null || link.thingId != tool.thingIDNumber)
                {
                    BreakFail(check, $"premise: the plan did not take the traced tool ({tool?.thingIDNumber}, link {link?.Describe()})");
                    return;
                }
                ToolCount.Counted(pool, null);
                if (!link.known || link.shown == null || !Mentions(link.shown, "tool"))
                {
                    BreakFail(check, $"premise: the count's line should hold the word tool ({link.Describe()})");
                    return;
                }
                EscapePlans.MaybeAsk(s.arc, map, force: true);
                List<Pawn> targets = BreakLetters(r, s.arc).Last().pawns;
                List<Pawn> pointed = BreakPointedAt(r, s);
                if (link.knownHolderId != -1 || EscapePlans.KnownCrew(s.arc, map).Count != 0 || EscapePlans.FoilWhy(s.arc, map) == null || EscapePlans.Focus(s.arc, link, map) != null || targets.Count != 0 || pointed.Count != 0)
                    BreakFail(check, $"after the count the holder (called tool) is known: the link {link.Describe()}, known crew [{BreakNames(EscapePlans.KnownCrew(s.arc, map))}], foil off \"{EscapePlans.FoilWhy(s.arc, map)}\", focus {EscapePlans.Focus(s.arc, link, map)?.LabelShort}, letter targets [{BreakNames(targets)}], the messages and letters point at [{BreakNames(pointed)}] (want nobody)");
            });
        }

        /// <summary>
        /// The night the warden knows is kept apart from the true night (review BR-1, opus 2). A night known at an hour of its own, then a blackout with nobody able to go: the true night is now the blackout's, a day on;
        /// the post's hours, their text, "post guards" (a post is up already), the post's own hours when let run again and the letter's all stay the hours the warden was told.
        /// </summary>
        private static void CheckBreakKnownNight(Map map)
        {
            const string check = "the known night";
            Guarded("the break: a known night, then a blackout - Intel's post hours and the letter keep the night the warden knows", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame, hour = (EscapePlans.HourAt(map, now) + 8) % 24;
                s.Plan.readiness = 1f;
                (s.Plan.night, s.Plan.nightTick) = (PlanNight.Handover, EscapePlans.NextAt(map, hour, EscapePlans.NightLeadHours * GenDate.TicksPerHour));
                var night = new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) };
                s.Plan.links.Add(night);
                EscapePlans.Reveal(s.arc, s.Link(PlanRole.Way), "tip", map, announce: false);
                EscapePlans.Reveal(s.arc, night, "tip", map, announce: false);
                if (night.knownHour != hour)
                {
                    BreakFail(check, $"premise: the night learned of is at {hour}:00 ({night.Describe()})");
                    return;
                }
                (int from, int to) told = EscapePlans.PostHours(map, s.Plan);
                string toldText = EscapePlans.PostHoursText(map, s.Plan);
                Thing post = EscapePlans.LetItRun(s.arc, map);
                if (post != null)
                    r.Add(() =>
                    {
                        if (!post.Destroyed) post.Destroy();
                    });
                foreach (Pawn p in s.Crew) p.Inmate().forcedActivity = RPSecurityDefOf.RP_Solitary;
                g.blackoutUntilTick = now + 3 * GenDate.TicksPerHour;
                EscapePlans.Hourly(s.arc, map);
                foreach (Pawn p in s.Crew) p.Inmate().forcedActivity = null;
                g.blackoutUntilTick = -1;
                if (s.arc.done || s.Plan.night != PlanNight.Blackout || EscapePlans.HourAt(map, s.Plan.nightTick) == hour)
                {
                    BreakFail(check, $"premise: the blackout moved the true night off the known hour {hour}:00 and nobody went ({s.Plan.Describe()})");
                    return;
                }
                (int from, int to) hours = EscapePlans.PostHours(map, s.Plan);
                string text = EscapePlans.PostHoursText(map, s.Plan);
                string why = EscapePlans.PostWhy(s.arc, map);
                CompGuardPost comp = post?.TryGetComp<CompGuardPost>();
                Thing again = post != null ? EscapePlans.LetItRun(s.arc, map) : null;
                EscapePlans.MaybeAsk(s.arc, map, force: true);
                string letter = BreakLetters(r, s.arc).Last().Text.ToString();
                if (hours != told || text != toldText || post != null && (why == null || again != post || comp != null && (comp.fromHour, comp.toHour) != told) || !letter.Contains(toldText))
                    BreakFail(check, $"the true night is now the blackout's ({EscapePlans.HourAt(map, s.Plan.nightTick)}:00), the warden was told {hour}:00: post hours {hours.from}-{hours.to} (want {told.from}-{told.to}), \"{text}\" (want \"{toldText}\"), Post guards off \"{why}\" (want on with a post up: {post != null}), the post let run again {comp?.fromHour}-{comp?.toHour}, the letter has the told hours {letter.Contains(toldText)}");
            });
        }

        /// <summary>A scene whose night is due now (the next hourly pass is the break, an unknown night): what the crew do should they go is undone by the rollback.</summary>
        private static BreakScene BreakDueScene(Map map, Rollback r, string check, IntVec3? way = null)
        {
            BreakScene s = MakeBreakScene(map, r, check, way);
            if (s == null) return null;
            r.Add(() =>
            {
                foreach (Pawn p in s.Crew) p.MentalState?.RecoverFromState();
                PrisonMap.Of(map)?.count.hidden.RemoveAll(h => s.Crew.Contains(h.pawn));
            });
            s.Plan.readiness = 1f;
            (s.Plan.night, s.Plan.nightTick) = (PlanNight.Dark, Find.TickManager.TicksGame);
            s.Plan.links.Add(new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) });
            return s;
        }

        /// <summary>
        /// Eligibility holds for the whole plan (review BR-3, opus 3). Reclassified to protective custody or supermax, before the daily pass and after it: after it the night itself keeps them in (the leader in custody:
        /// the other two go; a member in supermax and one in custody: the leader goes alone); before it the daily pass takes the members out (the leader alone goes) and a leader in supermax ends the plan (nobody goes).
        /// </summary>
        private static void CheckBreakEligibility(Map map)
        {
            const string check = "eligibility";
            Guarded("the break: custody and supermax keep the crew in, and a leader out of reach ends the plan", map, r =>
            {
                SecurityLevelDef custody = RPSecurityDefOf.RP_Protective, supermax = RPSupermaxDefOf.RP_Supermax;
                void Case(string what, bool afterDaily, System.Func<BreakScene, (Pawn who, SecurityLevelDef level)[]> change, System.Func<BreakScene, Pawn[]> goes)
                {
                    BreakScene s = BreakDueScene(map, r, check);
                    if (s == null) return;
                    if (afterDaily) EscapePlans.DailyPlan(s.arc, map);
                    foreach ((Pawn who, SecurityLevelDef level) in change(s)) who.Inmate().security = level;
                    if (!afterDaily) EscapePlans.DailyPlan(s.arc, map);
                    EscapePlans.Hourly(s.arc, map);
                    List<Pawn> gone = s.Crew.Where(p => p.MentalStateDef == RPMentalStateDefOf.RP_Escaping || p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)).ToList();
                    List<Pawn> want = goes(s).ToList();
                    if (!s.arc.done || gone.Count != want.Count || want.Any(p => !gone.Contains(p)))
                        BreakFail(check, $"{what}: over {s.arc.done}, went [{BreakNames(gone)}] (want [{BreakNames(want)}]); {s.Plan.Describe()}");
                }
                Case("the leader in protective custody after the daily pass", true, s => new[] { (s.leader, custody) }, s => new[] { s.holder, s.third });
                Case("a member in supermax and one in custody after the daily pass", true, s => new[] { (s.holder, supermax), (s.third, custody) }, s => new[] { s.leader });
                Case("the leader in supermax before the daily pass", false, s => new[] { (s.leader, supermax) }, s => new Pawn[0]);
                Case("a member in custody and one in supermax before the daily pass", false, s => new[] { (s.holder, custody), (s.third, supermax) }, s => new[] { s.leader });
            });
        }

        /// <summary>A member carried to a bed or the hole across the daily pass stays in the plan (review BR-4): only a member who is gone leaves it; while carried they cannot go, put down they can.</summary>
        private static void CheckBreakCarried(Map map)
        {
            const string check = "a carried member";
            Guarded("the break: a member carried across midnight stays in the plan", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                Pawn carrier = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                carrier.Position = s.third.Position;
                carrier.Notify_Teleported();
                if (carrier.carryTracker.TryStartCarry(s.third, 1, reserve: false) <= 0 || s.third.Spawned)
                {
                    BreakFail(check, "premise: the guard did not pick the third up");
                    return;
                }
                r.Add(() =>
                {
                    if (carrier.carryTracker.CarriedThing == s.third) carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out Thing _);
                });
                EscapePlans.DailyPlan(s.arc, map);
                bool inPlan = s.Plan.links.Exists(l => l.role == PlanRole.Member && l.pawnId == s.third.thingIDNumber && !l.handled);
                bool goesCarried = EscapePlans.CanGo(s.third);
                carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out Thing _);
                EscapePlans.DailyPlan(s.arc, map);
                bool stillIn = s.Plan.links.Exists(l => l.role == PlanRole.Member && l.pawnId == s.third.thingIDNumber && !l.handled);
                if (!inPlan || goesCarried || !s.third.Spawned || !stillIn || !EscapePlans.CanGo(s.third))
                    BreakFail(check, $"the third carried across the daily pass: in the plan {inPlan} (want T), could go while carried {goesCarried} (want F); put down: spawned {s.third.Spawned}, in the plan {stillIn} (want T), can go {EscapePlans.CanGo(s.third)} (want T); {s.Plan.Describe()}");
            });
        }

        /// <summary>
        /// A start the game refuses puts the pawn back (opus 4): the escape's start is refused for the crew who go, and each is where they set out from (a way a few cells off), no escape on file, not in the count's book,
        /// and no letter of a break.
        /// </summary>
        private static void CheckBreakStartRefused(Map map)
        {
            const string check = "a refused start";
            Guarded("the break: a crew member whose escape does not start is put back where they set out from", map, r =>
            {
                IntVec3 from0 = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                if (!CellFinder.TryFindRandomCellNear(from0, map, 25, c => c.Standable(map) && !c.InHorDistOf(from0, 8f) && !GuardPosts.PostNear(map, c, EscapePlans.PostRadius + 1f), out IntVec3 way))
                {
                    RPLog.Message($"[RimPrison:test] break: no cell a few cells from the intake at {from0}, the refused start not judged");
                    return;
                }
                BreakScene s = BreakDueScene(map, r, check, way);
                if (s == null) return;
                var from = s.Crew.ToDictionary(p => p, p => p.Position);
                EscapePlans.RefuseEscape = true;
                r.Add(() => EscapePlans.RefuseEscape = false);
                EscapePlans.Hourly(s.arc, map);
                List<string> wrong = s.Crew.Where(p => !p.Spawned || p.Position != from[p] || p.MentalStateDef == RPMentalStateDefOf.RP_Escaping || CountSystem.IsHidden(p)).Select(p => $"{p.LabelShort} at {(p.Spawned ? p.Position.ToString() : "(not spawned)")} (set out from {from[p]})").ToList();
                int letters = r.NewLetters.Count(l => l.Label.Resolve() == "RP_BreakSeenLabel".Translate().Resolve());
                if (!s.arc.done || wrong.Count > 0 || letters != 0)
                    BreakFail(check, $"the night with the escape's start refused (the way at {way}): over {s.arc.done}, not back where they set out from [{string.Join("; ", wrong)}], {letters} letter(s) of a break (want none)");
            });
        }

        /// <summary>
        /// A second blackout re-times a blackout night moved on (opus 6): a blackout is the night with nobody able to go, so it goes a day on; the blackout ends, the crew can go again, a new blackout comes - that is the
        /// night, now: they go. (A night left a day on would wait for the old hour.)
        /// </summary>
        private static void CheckBreakSecondBlackout(Map map)
        {
            const string check = "a second blackout";
            Guarded("the break: a second blackout after a blackout night moved on is the night again", map, r =>
            {
                BreakScene s = BreakDueScene(map, r, check);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame;
                s.Plan.nightTick = now + 12 * GenDate.TicksPerHour;
                foreach (Pawn p in s.Crew) p.Inmate().forcedActivity = RPSecurityDefOf.RP_Solitary;
                g.blackoutUntilTick = now + GenDate.TicksPerHour;
                EscapePlans.Hourly(s.arc, map);
                foreach (Pawn p in s.Crew) p.Inmate().forcedActivity = null;
                g.blackoutUntilTick = -1;
                if (s.arc.done || s.Plan.night != PlanNight.Blackout || s.Plan.nightTick != now + GenDate.TicksPerDay)
                {
                    BreakFail(check, $"premise: the first blackout, with nobody able to go, moved the night a day on ({s.Plan.Describe()}, now {now})");
                    return;
                }
                g.blackoutUntilTick = now + 3 * GenDate.TicksPerHour;
                EscapePlans.Hourly(s.arc, map);
                g.blackoutUntilTick = -1;
                List<Pawn> gone = s.Crew.Where(p => p.MentalStateDef == RPMentalStateDefOf.RP_Escaping).ToList();
                if (!s.arc.done || gone.Count != 3)
                    BreakFail(check, $"a second blackout after the first moved the night on: over {s.arc.done}, {gone.Count} of 3 making a break for it, the night at {s.Plan.nightTick} (now {now})");
            });
        }

        /// <summary>A tunnel the warden knew of, found by a cell search and filled in, says so (opus 7): one chronicle line with the way's own words, once, and the way is out of the plan.</summary>
        private static void CheckBreakKnownTunnel(Map map)
        {
            const string check = "a known tunnel filled";
            Guarded("the break: a known tunnel filled in by a search is told", map, r =>
            {
                BreakScene s = MakeBreakScene(map, r, check);
                if (s == null) return;
                PlanLink way = s.Link(PlanRole.Way);
                s.Plan.way = PlanWay.Tunnel;
                (way.pawnId, way.name) = (s.holder.thingIDNumber, EscapePlans.WayText(map, PlanWay.Tunnel, s.Plan.wayCell, s.holder.thingIDNumber));
                EscapePlans.Reveal(s.arc, way, "tip", map, announce: false);
                if (!BreakFillTunnel(s, s.holder, map))
                {
                    BreakFail(check, $"premise: a search of the tunnel's cell did not take the way out ({s.Plan.Describe()})");
                    return;
                }
                string line = "RP_BreakTunnelFilled".Translate(EscapePlans.LinkLine(way)).Resolve();
                int told = r.NewEntries.Count(e => e.text == line);
                if (told != 1) BreakFail(check, $"the known tunnel was filled in and the chronicle has the line {told} time(s) (want 1): \"{line}\"");
            });
        }

        // ---- round 5: the re-review's findings ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The true holder going is not the warden's to see while they know another (opus S-1). Alan is the known holder, the tool passed unseen to Al; Al is released, or reclassified to protective custody and the daily pass
        /// reads it (once with Al's link unknown, once with every link known, so that the marker could light): Alan's row is not out, he is still known crew and foil is on, the tool is no longer the plan's, and the marker
        /// is as it was. Control: the holder the warden knows leaving does put the row out.
        /// </summary>
        private static void CheckBreakKnownHolderOut(Map map)
        {
            const string check = "the tool row, out";
            Guarded("the break: the true holder leaving unseen does not put the known holder's row out", map, r =>
            {
                void Case(string what, bool allKnown, System.Action<BreakScene> act)
                {
                    BreakScene s = BreakKnownHolderScene(map, r, check);
                    if (s == null) return;
                    if (allKnown)
                    {
                        s.Plan.readiness = 1f;
                        (s.Plan.night, s.Plan.nightTick) = (PlanNight.Dark, EscapePlans.NextAt(map, 3, EscapePlans.NightLeadHours * GenDate.TicksPerHour));
                        s.Plan.links.Add(new PlanLink { role = PlanRole.Night, name = EscapePlans.NightText(map, s.Plan) });
                        foreach (PlanLink l in s.Plan.links.Where(l => !l.known).ToList()) EscapePlans.Reveal(s.arc, l, "tip", map, announce: false);
                    }
                    EscapePlans.DailyPlan(s.arc, map);
                    bool marker0 = s.Plan.moreShown;
                    if (allKnown && marker0)
                    {
                        BreakFail(check, $"premise: every part found and still a marker ({s.Plan.Describe()})");
                        return;
                    }
                    PlanLink tool = s.Link(PlanRole.Tool);
                    act(s);
                    EscapePlans.DailyPlan(s.arc, map);
                    var row = EscapePlans.Chain(s.arc).FirstOrDefault(p => p.link == tool);
                    if (tool.handled || row.link == null || row.link.handled || tool.knownHolderId != s.holder.thingIDNumber || tool.shown != "RP_BreakLine_Tool".Translate("Alan").Resolve() || s.Plan.Find(PlanRole.Tool) != null
                        || !EscapePlans.KnownCrew(s.arc, map).Contains(s.holder) || EscapePlans.FoilWhy(s.arc, map) != null || s.Plan.moreShown != marker0)
                        BreakFail(check, $"{what}: Alan's row {tool.Describe()} (want not out, still Alan's, no tool of the plan's behind it: {s.Plan.Find(PlanRole.Tool) == null}), in Intel {row.link != null}, known crew [{BreakNames(EscapePlans.KnownCrew(s.arc, map))}], foil off \"{EscapePlans.FoilWhy(s.arc, map)}\", the marker {s.Plan.moreShown} (was {marker0})");
                }
                Case("Al, who has the tool now, released", false, s => s.arc.def.Worker.Notify_Leaving(s.arc, s.third));
                Case("Al in protective custody at the daily pass", false, s => s.third.Inmate().security = RPSecurityDefOf.RP_Protective);
                Case("Al released, every part found", true, s => s.arc.def.Worker.Notify_Leaving(s.arc, s.third));
                Case("Al in protective custody, every part found", true, s => s.third.Inmate().security = RPSecurityDefOf.RP_Protective);
                // (The control: the tool is in the hands the warden knows of, and they leave: that is seen.)
                BreakScene c = MakeBreakScene(map, r, check);
                if (c == null) return;
                PlanLink own = c.Link(PlanRole.Tool);
                EscapePlans.Reveal(c.arc, own, "tip", map, announce: false);
                c.arc.def.Worker.Notify_Leaving(c.arc, c.holder);
                if (!own.handled || own.Ghost) BreakFail(check, $"the holder the warden knew of left, and the row is {own.Describe()} (want out)");
            });
        }

        /// <summary>
        /// A later find on someone else keeps the first (opus M-1): Alan was known to hold the tool, it passed to Al, a frisk of Al finds it. Both are known crew now (and the letter's targets and what foil touches), Zed is not;
        /// foil flags Alan and Al and no one else.
        /// </summary>
        private static void CheckBreakLaterFind(Map map)
        {
            const string check = "a later find";
            Guarded("the break: a find on someone else keeps the holder known before", map, r =>
            {
                BreakScene s = BreakKnownHolderScene(map, r, check);
                if (s == null) return;
                ContrabandUtility.Search(ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), s.third, 1f);
                List<Pawn> crew = EscapePlans.KnownCrew(s.arc, map);
                EscapePlans.MaybeAsk(s.arc, map, force: true);
                List<Pawn> targets = BreakLetters(r, s.arc).Last().pawns;
                List<Pawn> pointed = BreakPointedAt(r, s);
                bool both = crew.Count == 2 && crew.Contains(s.holder) && crew.Contains(s.third) && targets.Count == 2 && targets.Contains(s.holder) && targets.Contains(s.third);
                if (!both || pointed.Contains(s.leader)) BreakFail(check, $"after a find on Al, Alan being known: known crew [{BreakNames(crew)}], the letter's targets [{BreakNames(targets)}], pointed at [{BreakNames(pointed)}] (want Alan and Al); {s.Plan.Describe()}");
                EscapePlans.Foil(s.arc, map);
                bool flagged = s.holder.Inmate().suspected && s.third.Inmate().suspected && s.holder.Inmate().cellSearchPending && s.third.Inmate().cellSearchPending;
                if (!flagged || s.leader.Inmate().suspected || KeepApart.OrderBetween(s.holder, s.third) == null || KeepApart.OrderBetween(s.holder, s.leader) != null)
                    BreakFail(check, $"foiled after a find on Al, Alan being known: both flagged {flagged}, the leader flagged {s.leader.Inmate().suspected}, Alan and Al kept apart {KeepApart.OrderBetween(s.holder, s.third) != null}");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------------------

        private static int breakSeedArcId = -1;
        private static string breakSeed;

        /// <summary>A plan of real inmates part-way, every field of the plan and of a link off its default (the known holder and the known hour too); taken out after the reload. It is held against the arc's hourly and daily passes (<see cref="EscapePlans.HoldArc"/>) until then: a blackout at an hour's pass in the ticked checks would re-time its night, and the snapshot would say so.</summary>
        private static void SeedBreakForReload(Map map)
        {
            // (Not the two the count's round trip is judged on, which SeedCountForReload picked first: the inmate it counted, with a hidden escape that has left the map, and the runaway still on it. A plan led by an
            // inmate who is one of the count's hidden escapes is a state the game cannot have - the night ends the plan in the call that starts the escape - and the count's check reads these two last, after every ticked
            // check of the plan. No other seed's pick changes.)
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { security: not null } && p.thingIDNumber != countSeed.pawnId && p.thingIDNumber != countSeedWalker)
                .OrderBy(p => p.thingIDNumber).Take(2).ToList();
            if (inmates.Count < 2)
            {
                problems0.Add("saveload: no two inmates to seed an escape plan with");
                return;
            }
            int t = Find.TickManager.TicksGame;
            StoryArc arc = EscapePlans.Make(map, inmates[0], new[] { inmates[1] }, PlanWay.Ramp, new IntVec3(12, 0, 13));
            EscapePlan plan = arc.plan;
            plan.links.Clear();
            plan.links.Add(new PlanLink { role = PlanRole.Leader, pawnId = inmates[0].thingIDNumber, name = inmates[0].LabelShort, known = true, shown = "seed leader line" });
            plan.links.Add(new PlanLink { role = PlanRole.Member, pawnId = inmates[1].thingIDNumber, name = inmates[1].LabelShort });
            plan.links.Add(new PlanLink { role = PlanRole.Member, pawnId = 535353, name = "Seedgone", known = true, handled = true, shown = "Seedgone is in the crew" });
            plan.links.Add(new PlanLink { role = PlanRole.Tool, pawnId = inmates[1].thingIDNumber, name = inmates[1].LabelShort, known = true, handled = true, thingId = 515151, shown = "the seed tool", knownHolderId = inmates[0].thingIDNumber });
            plan.links.Add(new PlanLink { role = PlanRole.Officer, pawnId = 424242, name = "Seedofficer", known = true, turned = true, shown = "Seedofficer is the inside man" });
            plan.links.Add(new PlanLink { role = PlanRole.Way, pawnId = 616161, name = "the seed way", known = true, shown = "the way out is the seed way" });
            plan.links.Add(new PlanLink { role = PlanRole.Night, name = "the seed night", known = true, shown = "the night is the seed night", knownHour = 5 });
            (plan.readiness, plan.nightTick, plan.night, plan.answer, plan.asked, plan.moreShown) = (0.47f, t + 3 * GenDate.TicksPerDay + 77, PlanNight.Blackout, PlanAnswer.Turn, true, true);
            plan.posts = new List<int> { 727272 };
            (breakSeedArcId, breakSeed) = (arc.id, $"{inmates[0].thingIDNumber}:{arc.startTick}:{plan.Describe()}");
            // (Held as it is: the ticked checks after the load run the arcs' hourly pass, and a blackout in them would re-time the night - an escape of two real inmates through a way the plan has.)
            EscapePlans.HoldArc = arc.id;
            RPLog.Message($"[RimPrison:test] saveload break: {inmates[0].LabelShort}'s plan {breakSeed} (the count's seeded inmates, {countSeed.pawnId} and {countSeedWalker}, are not in it)");
        }

        /// <summary>The seeded plan came back whole, on the same arc and leader; then it goes.</summary>
        private static void CheckBreakAfterReload(Map map)
        {
            if (breakSeedArcId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            StoryArc arc = g.story.arcs.FirstOrDefault(a => a.id == breakSeedArcId && a.def == RPBreakDefOf.RP_Arc_Break);
            string after = arc?.plan == null ? "(gone)" : $"{arc.A?.thingIDNumber}:{arc.startTick}:{arc.plan.Describe()}";
            RPLog.Message($"[RimPrison:test] saveload break: {(after == breakSeed ? "intact" : "changed")} ({after})");
            if (after != breakSeed) reloadProblems.Add($"an escape plan changed in the round trip: {breakSeed} -> {after}");
            if (arc != null) g.story.arcs.Remove(arc);
            (breakSeedArcId, breakSeed) = (-1, null);
            EscapePlans.HoldArc = -1;
            // (For the count's round trip, judged last: where its inmate is at the load, before any tick. The line tells a failure there from one that came later, in the ticked checks.)
            if (countSeed.pawnId >= 0)
            {
                Pawn counted = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == countSeed.pawnId);
                RPLog.Message($"[RimPrison:test] saveload break: the count's inmate {countSeed.pawnId} at the load: spawned {counted != null}, prisoner {counted?.IsPrisonerOfColony}, inmate {counted?.Inmate() != null}, counted at {counted?.Inmate()?.lastCountedTick} (want {countSeed.counted}), job {counted?.CurJobDef?.defName}");
            }
        }
    }
}
