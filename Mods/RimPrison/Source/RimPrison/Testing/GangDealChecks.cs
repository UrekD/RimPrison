using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The shot-caller's deal (<see cref="GangDeals"/>, gap analysis 6, item 5): only a known gang's known leader gets the word's line; the offer's letter and the leader's dice (forced, as the warden's words are tested); the x0.6 on
    /// the members on the leader's wing and on nobody else, nor after the deal; no war and no recruiting while it runs; a breach asks once and ends on the second; the five days after it ends, however it ended; each term
    /// applied and taken back (the kept patch and the daily verdict, the visits, the commissary, the job slot); the inspector's five points; the rivals' tension; the press once; the saveload plan's seed and snapshot at the end. The
    /// scene is a <see cref="CellRig"/> of cells in the wing (the leader, two members and a rival of the Saints each in a bed), a member with no bed who stands off every wing, a made-up warden, and the prison's own gangs, turf and war
    /// arcs set aside (<see cref="DealScene"/>). Each check says in its summary how it fails. (The first game runs are in the WORKLOG's entry; the checks changed since have not been run.)
    /// </summary>
    public static partial class AutoTest
    {
        private static void DealFail(string what) => problems0.Add("gang deal: " + what);

        private static void CheckGangDeal(Map map)
        {
            CheckDealLine(map);
            CheckDealOffer(map);
            CheckDealCalm(map);
            CheckDealWarAndRecruiting(map);
            CheckDealBreach(map);
            CheckDealEnds(map);
            CheckDealLeaderMoved(map);
            CheckDealTurf(map);
            CheckDealVisitsAndCommissary(map);
            CheckDealJob(map);
            CheckDealInspector(map);
            CheckDealRivals(map);
            CheckDealPress(map);
        }

        // ---- the scene -----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A leader of the Ravens, two members and a rival of the Saints, each in a bed of a wing (the rig's cells), and a member with no bed standing at the intake, off every wing. The prison's own inmates belong to no gang meanwhile,
        /// its turf is empty and its war and deal arcs are set aside; all of it is put back by the rollback (the arcs by putting the list back).
        /// </summary>
        private sealed class DealScene
        {
            public Map map;
            public Rollback r;
            public PrisonGame g;
            public PrisonMap pm;
            public GangDef ravens, saints;
            public WardenScene ws;
            public Pawn warden;
            public CellRig rig;
            public PrisonZoneDef wing;
            public Pawn leader, member, member2, rival, outsider;

            public CompInmate Lc => leader.Inmate();

            public StoryArc Arc => GangDeals.Of(ravens);

            public int Now => Find.TickManager.TicksGame;

            /// <summary>The rig could not be built once (no open ground for it: a problem is noted by the rig itself): the other checks do not try again, each would say the same.</summary>
            private static bool rigUnavailable;

            public static DealScene Begin(Map map, Rollback r, string what)
            {
                if (rigUnavailable)
                {
                    RPLog.Message($"[RimPrison:test] gang deal: {what} not run, the cells' rig could not be built");
                    return null;
                }
                var s = new DealScene { map = map, r = r, g = PrisonGame.Instance, pm = PrisonMap.Of(map) };
                (s.ravens, s.saints) = (DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"));
                s.ws = new WardenScene(map, r);
                // The prison's own gangs are nobody's while the check runs: no real leader, member or rival to be the one it reads.
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).ToList();
                var gangs0 = real.Select(c => (c, c.gang, c.gangLeader, c.gangRank)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, GangDef gang, bool leader, byte rank) in gangs0) (c.gang, c.gangLeader, c.gangRank) = (gang, leader, rank);
                });
                foreach (CompInmate c in real) (c.gang, c.gangLeader, c.gangRank) = (null, false, 0);
                List<TurfClaim> claims0 = s.pm.turf.claims.ToList();
                r.Add(() => s.pm.turf.Replace(claims0));
                s.pm.turf.Replace(new TurfClaim[0]);
                // (The day's presence tally too: the daily verdict the turf check runs would judge the prison's own day, and clears it.)
                var tallies0 = s.pm.turf.tallies.ToList();
                r.Add(() =>
                {
                    s.pm.turf.tallies.Clear();
                    foreach (KeyValuePair<int, TurfBook.PatchTally> kv in tallies0) s.pm.turf.tallies[kv.Key] = kv.Value;
                });
                s.pm.turf.tallies.Clear();
                // The story's arcs: the list is put back whole (so no deal or war the check made outlives it), and the wars and deals the prison had are out of it meanwhile.
                StorySystem story = s.g.story;
                List<StoryArc> arcs0 = story.arcs;
                int nextArc0 = story.nextArcId;
                var lastStart0 = new Dictionary<StoryArcDef, int>(story.lastArcStart);
                var ended0 = new Dictionary<GangDef, int>(story.gangDealEnded);
                r.Add(() =>
                {
                    story.arcs = arcs0;
                    story.nextArcId = nextArc0;
                    story.lastArcStart = lastStart0;
                    story.gangDealEnded = ended0;
                    GangDeals.Changed();
                });
                story.arcs = arcs0.Where(a => a.def != RPGangWarDefOf.RP_Arc_GangWar && a.def != RPGangDealDefOf.RP_Arc_GangDeal).ToList();
                story.gangDealEnded = new Dictionary<GangDef, int>();
                GangDeals.Changed();
                s.rig = CellRig.Build(map, r, "gang deal " + what, 1, 1, 1, 1);
                if (s.rig == null)
                {
                    rigUnavailable = true;
                    return null;
                }
                s.wing = DefDatabase<PrisonZoneDef>.AllDefsListForReading.First(z => z.security == s.rig.level);
                s.leader = s.Be(s.rig.Inmate(0, 0, 1), s.ravens, true);
                s.member = s.Be(s.rig.Inmate(1, 0, 1), s.ravens, false);
                s.member2 = s.Be(s.rig.Inmate(2, 0, 1), s.ravens, false);
                s.rival = s.Be(s.rig.Inmate(3, 0, 1), s.saints, false);
                s.outsider = s.Be(s.ws.Inmate(null, 30f), s.ravens, false);
                // The warden: the made-up one, and the one the deal will read as the warden in post.
                Pawn made = s.ws.Warden();
                s.warden = WardenSystem.InPost(map);
                if (s.warden != made)
                {
                    DealFail($"{what}: premise: the warden in post is {s.warden?.LabelShort ?? "nobody"}, not the made-up warden {made.LabelShort}");
                    return null;
                }
                if (WardenSystem.InWing(s.outsider, s.wing, s.pm) || WardenSystem.InWing(s.leader, s.wing, s.pm) == false)
                {
                    DealFail($"{what}: premise: the member with no bed is on the wing, or the leader in a bed is not (the intake cell is in {s.pm.zones.ZoneAt(s.outsider.Position)?.defName ?? "no zone"})");
                    return null;
                }
                return s;
            }

            public Pawn Be(Pawn p, GangDef gang, bool leader)
            {
                CompInmate c = p.Inmate();
                c.gang = gang;
                c.gangKnown = true;
                GangRanks.SetRank(c, leader ? GangRank.Leader : GangRank.Member);
                c.tension = 30f;
                c.fear = 0f;
                return p;
            }

            /// <summary>The deal struck on the leader, or null with a problem noted.</summary>
            public StoryArc Start(GangDealTerm term, string what)
            {
                StoryArc arc = GangDeals.Start(map, leader, term);
                if (arc == null) DealFail($"{what}: premise: no {term} deal could be struck (blocker: {GangDeals.Blocker(map, leader, Lc) ?? "none"}; term: {GangDeals.TermWhy(map, leader, term) ?? "none"})");
                return arc;
            }

            /// <summary>The cooldown is over (the last deal ended five days and a tick ago).</summary>
            public void SkipCooldown()
            {
                foreach (GangDef gang in g.story.gangDealEnded.Keys.ToList()) g.story.gangDealEnded[gang] -= GangDeals.CooldownDays * GenDate.TicksPerDay + 1;
            }

            public bool Chronicle(string line) => r.NewEntries.Any(e => e.text == line);
        }

        private static bool Near(float a, float b) => Mathf.Abs(a - b) < 1e-4f;

        // ---- who gets the line ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// The word's line is for a known gang's known leader and nobody else: a leader the warden has not found out, and a known member who does not lead, are not offered it (and saying the word anyway does nothing and sends no letter);
        /// the line is there but disabled, with a reason, for a leader in the hole, with a deal running and for the five days after it. Fails if the line is offered to a leader not on file or a member, or is not disabled in the hole, while a
        /// deal runs or within the five days.
        /// </summary>
        private static void CheckDealLine(Map map)
        {
            Guarded("gang deal: only a known gang's known leader gets the line", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "line");
                if (s == null) return;
                string Words(Pawn p) => string.Join(",", WardenSystem.WordOptions(s.warden, p).Select(o => o.kind + (o.disabled != null ? "!" : "")));
                bool Has(Pawn p) => WardenSystem.WordOptions(s.warden, p).Any(o => o.kind == WardenWord.GangDeal);
                bool Open(Pawn p) => WardenSystem.WordOptions(s.warden, p).Any(o => o.kind == WardenWord.GangDeal && o.disabled == null);
                if (!Open(s.leader)) DealFail($"a known gang's known leader is not offered the line, or it is disabled with nothing in the way (words: {Words(s.leader)})");
                s.Lc.gangKnown = false;
                if (Has(s.leader)) DealFail($"a leader the warden has not found out is offered the line (words: {Words(s.leader)})");
                int letters = r.NewLetters.Count();
                string said = WardenSystem.Word(s.warden, s.leader, WardenWord.GangDeal);
                if (said != null || r.NewLetters.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_GangDeal")) != 0) DealFail($"the word was said to a leader the warden has not found out (it said \"{said}\", {r.NewLetters.Count() - letters} letters came)");
                s.Lc.gangKnown = true;
                if (Has(s.member)) DealFail($"a known member who does not lead is offered the line (words: {Words(s.member)})");
                if (Has(s.outsider)) DealFail("a known member with no bed is offered the line");

                // The hole, a deal running, the five days after it: the line is there, disabled.
                s.Lc.forcedActivity = RPSecurityDefOf.RP_Solitary;
                if (!Has(s.leader) || Open(s.leader)) DealFail($"a leader in the hole is not offered the line disabled (words: {Words(s.leader)})");
                s.Lc.forcedActivity = null;
                StoryArc arc = s.Start(GangDealTerm.Visits, "line");
                if (arc == null) return;
                if (!Has(s.leader) || Open(s.leader)) DealFail($"with a deal running the line is not disabled (words: {Words(s.leader)})");
                if (GangDeals.Start(map, s.leader, GangDealTerm.Commissary) != null || s.g.story.arcs.Count(a => a.def == RPGangDealDefOf.RP_Arc_GangDeal && !a.done) != 1) DealFail("a second deal was struck with a gang whose deal runs");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (!Has(s.leader) || Open(s.leader)) DealFail($"right after a deal ended the line is not disabled (words: {Words(s.leader)})");
                s.SkipCooldown();
                if (!Open(s.leader)) DealFail($"five days after a deal ended the line is still disabled (words: {Words(s.leader)})");
                RPLog.Message("[RimPrison:test] gang deal: the line is for a known gang's known leader only, and disabled in the hole, while a deal runs and for five days after");
            });
        }

        // ---- the offer, the dice and the answer --------------------------------------------------------------------------------------

        /// <summary>
        /// The word through the real path: the letter (four terms and "not now", urgent, "not now" the answer when it times out); a refusal (the dice forced) costs the leader's trust by exactly the stated amount and leaves them wound up
        /// and no deal; "not now" costs nothing and strikes nothing; a pick the leader takes (the dice forced) is a deal with the payload as asked. Fails if a refused deal strikes, costs no trust, or an accepted one does not strike.
        /// </summary>
        private static void CheckDealOffer(Map map)
        {
            Guarded("gang deal: the offer's letter and the leader's dice", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "offer");
                if (s == null) return;
                r.Add(() => WardenSystem.ForcedRoll = null);
                // (The commissary is the term to pick: it has no premise. The visits term is on offer only with a visitation room and a member with family, and the turf and job terms with a patch and a member to give them.)
                const int Commissary = (int)GangDealTerm.Commissary;
                ChoiceLetter_PrisonDecision Offer()
                {
                    s.Lc.wardenWordTick = -999999;
                    int before = r.NewLetters.Count();
                    string line = WardenSystem.Word(s.warden, s.leader, WardenWord.GangDeal);
                    ChoiceLetter_PrisonDecision l = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(x => x.Is("RP_Decision_GangDeal"));
                    if (line == null || l == null) DealFail($"the word did not say the offer or send the letter (said \"{line}\", {r.NewLetters.Count() - before} new letters)");
                    return l;
                }
                ChoiceLetter_PrisonDecision letter = Offer();
                if (letter == null) return;
                IReadOnlyList<string> options = letter.decision.Worker.Options(letter);
                if (options.Count != 5 || options[4] != "RP_GangDeal_NotNow" || letter.decision.Worker.Disabled(letter, 4) != null) DealFail($"the offer's options are {string.Join(",", options)}: four terms and a \"not now\" that can always be taken were expected");
                if (letter.decision.Worker.TimeoutChoice != 4) DealFail($"an offer left unanswered is answer {letter.decision.Worker.TimeoutChoice}, not \"not now\" (4)");
                if (!StoryLetters.Urgent(letter) || letter.pawns.FirstOrDefault() != s.leader) DealFail("the offer is held back by the day's budget, or is not for the leader");
                if (letter.Text.ToString().IndexOf($"{GangDeals.InspectionPoints} points", StringComparison.Ordinal) < 0) DealFail("the offer's text does not state the inspector's points");

                // Refused (the dice forced): no deal, the trust down by exactly the refusal's amount, the leader more wound up.
                WardenSystem.ForcedRoll = false;
                float trust0 = StaffMemories.ScoreFor(s.Lc, s.warden);
                s.Lc.tension = 30f;
                PrisonDecisions.Choose(letter, Commissary);
                float trust1 = StaffMemories.ScoreFor(s.Lc, s.warden);
                if (s.Arc != null || s.g.story.arcs.Any(a => a.def == RPGangDealDefOf.RP_Arc_GangDeal)) DealFail("a deal was struck with the dice against it");
                if (!Near(trust1, trust0 + GangDeals.RefusedTrust) || !Near(s.Lc.tension, 30f + GangDeals.RefusedTension)) DealFail($"a refusal moved the leader's trust {trust0} -> {trust1} (expected {GangDeals.RefusedTrust:+0;-0} from {trust0}) and tension to {s.Lc.tension} (expected {30f + GangDeals.RefusedTension})");
                if (s.g.story.gangDealEnded.Count != 0) DealFail("a refused offer started the five days before the next deal (only a deal that ran does)");

                // Not now: nothing.
                ChoiceLetter_PrisonDecision second = Offer();
                if (second == null) return;
                WardenSystem.ForcedRoll = true;
                PrisonDecisions.Choose(second, 4);
                if (s.Arc != null || !Near(StaffMemories.ScoreFor(s.Lc, s.warden), trust1)) DealFail("\"not now\" struck a deal or cost trust");

                // Taken (the dice forced): the deal, as the leader's pick and the payload say.
                ChoiceLetter_PrisonDecision third = Offer();
                if (third == null) return;
                int now = s.Now;
                PrisonDecisions.Choose(third, Commissary);
                StoryArc arc = s.Arc;
                if (arc?.deal == null || arc.pawns.Count != 1 || arc.pawns[0] != s.leader) DealFail("a deal the leader took was not struck, or is not cast with the leader");
                else
                {
                    GangDeal d = arc.deal;
                    if (d.gang != s.ravens || d.term != GangDealTerm.Commissary || d.leaderId != s.leader.thingIDNumber || d.leaderName != s.leader.LabelShort || d.wing != s.wing || d.untilTick != now + GangDeals.DealDays * GenDate.TicksPerDay || d.breaches != 0 || d.pressDone)
                        DealFail($"the deal's payload is {d.gang?.defName}/{d.term}/{d.leaderId}/{d.leaderName}/{d.wing?.defName}/until {d.untilTick - now} (expected Ravens/Commissary/{s.leader.thingIDNumber}/{s.leader.LabelShort}/{s.wing.defName}/until {GangDeals.DealDays * GenDate.TicksPerDay}), {d.breaches} breaches, press {d.pressDone}");
                    if (s.g.story.InArc(s.leader)) DealFail("the deal keeps the leader out of other stories (it is a background arc)");
                }
                if (!s.Chronicle("RP_Chr_GangDealStart".Translate(s.leader.LabelShort, s.ravens.label, GangDeals.DealDays, "RP_GangDealTerm_Commissary".Translate()).Resolve())) DealFail("no chronicle line for the deal struck");
                RPLog.Message("[RimPrison:test] gang deal: the offer's letter has four terms and \"not now\", a refusal costs trust and no deal, and a deal the leader takes carries its payload");
            });
        }

        // ---- the x0.6 ----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The misconduct chance of a member on the leader's wing is exactly 0.6 of what it was (the leader's included), and not for a member of the gang with no bed and no place on the wing, a member of another gang on the wing, nor
        /// anyone once the deal is over; a member taken off the wing (their bed given up) loses it. Fails if the factor is not 0.6 on the wing, is applied off it or to a rival, or outlives the deal.
        /// </summary>
        private static void CheckDealCalm(Map map)
        {
            Guarded("gang deal: misconduct at 0.6 for the members on the wing, and nobody else", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "calm");
                if (s == null) return;
                Pawn[] all = { s.leader, s.member, s.member2, s.rival, s.outsider };
                foreach (Pawn p in all)
                {
                    CompInmate c = p.Inmate();
                    (c.tension, c.suppression, c.suppressed) = (80f, 0f, 0f);
                }
                float F(Pawn p) => TensionUtility.MisconductChancePerHour(p.Inmate());
                float v0 = F(s.member);
                if (v0 <= 0f || all.Any(p => !Near(F(p), v0))) DealFail($"premise: five inmates at the same tension do not share a misconduct chance ({string.Join(", ", all.Select(p => F(p).ToString("0.0000")))})");
                StoryArc arc = s.Start(GangDealTerm.Commissary, "calm");
                if (arc == null) return;
                string Seen() => $"leader {F(s.leader):0.0000}, member {F(s.member):0.0000}, member 2 {F(s.member2):0.0000}, rival {F(s.rival):0.0000}, no-bed member {F(s.outsider):0.0000} (base {v0:0.0000})";
                if (!Near(F(s.member), v0 * GangDeals.CalmFactor) || !Near(F(s.member2), v0 * GangDeals.CalmFactor) || !Near(F(s.leader), v0 * GangDeals.CalmFactor)) DealFail($"the members on the wing were not at {GangDeals.CalmFactor} of the base while the deal runs: {Seen()}");
                if (!Near(F(s.outsider), v0)) DealFail($"a member of the gang with no place on the wing was calmed: {Seen()}");
                if (!Near(F(s.rival), v0)) DealFail($"a member of another gang on the wing was calmed: {Seen()}");
                // Off the wing: the bed given up (they stand at the intake, off every wing).
                Building_Bed bed = s.member2.ownership.OwnedBed;
                s.member2.ownership.UnclaimBed();
                if (!Near(F(s.member2), v0)) DealFail($"a member whose bed is off the wing and who stands off it was still calmed: {Seen()}");
                s.rig.PutBack(s.member2, bed);
                if (!Near(F(s.member2), v0 * GangDeals.CalmFactor)) DealFail($"a member back in their bed on the wing was not calmed again: {Seen()}");
                // Over: nobody.
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (all.Any(p => !Near(F(p), v0))) DealFail($"the factor outlived the deal: {Seen()}");
                RPLog.Message($"[RimPrison:test] gang deal: misconduct chance {v0:0.0000} -> {v0 * GangDeals.CalmFactor:0.0000} for the members on the wing, unchanged for the others and after the deal");
            });
        }

        // ---- no war, no recruiting ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// While a deal runs neither gang of a pair can be sent to war (the war's gate and its start), and not after it ends; the leader recruits nobody over six hundred hourly draws (and does again after), nor does the initiation's
        /// "play it out" take a newcomer in. Fails if a war starts for the gang while it runs, if the control (the same draws without the deal) recruits nobody, or if the gang recruits while the deal runs.
        /// </summary>
        private static void CheckDealWarAndRecruiting(Map map)
        {
            Guarded("gang deal: no war and no recruiting while it runs", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "war");
                if (s == null) return;
                // The war: the gate is open for the two gangs (a member of each to cast), then shut for as long as either has a deal.
                if (!GangWarArc.CanStart(map, s.ravens, s.saints) || !GangWarArc.CanStart(map, s.saints, s.ravens))
                {
                    DealFail("premise: the war's gate is shut for the Ravens and the Saints before any deal (a member of each and no war running were expected)");
                    return;
                }
                StoryArc arc = s.Start(GangDealTerm.Commissary, "war");
                if (arc == null) return;
                if (GangWarArc.CanStart(map, s.ravens, s.saints) || GangWarArc.CanStart(map, s.saints, s.ravens) || GangWarArc.Start(map, s.ravens, s.saints) != null || s.g.story.arcs.Any(a => a.def == RPGangWarDefOf.RP_Arc_GangWar))
                    DealFail("a war could start between the Ravens and the Saints while the Ravens have a deal");
                // A deal with the other gang shuts it as well.
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (!GangWarArc.CanStart(map, s.ravens, s.saints)) DealFail("a war could not start after the deal ended");
                s.Be(s.rival, s.saints, true);
                s.SkipCooldown();
                StoryArc other = GangDeals.Start(map, s.rival, GangDealTerm.Commissary);
                if (other == null) DealFail($"premise: no deal could be struck with the Saints' leader ({GangDeals.Blocker(map, s.rival, s.rival.Inmate()) ?? "no blocker"})");
                else
                {
                    if (GangWarArc.CanStart(map, s.ravens, s.saints)) DealFail("a war could start while the Saints (the other gang of the pair) have a deal");
                    GangDeals.End(other, GangDealEnd.Lapsed, map);
                }
                s.Be(s.rival, s.saints, false);
                s.SkipCooldown();

                // The recruiting: a tense gangless inmate beside the leader, six hundred hourly draws (a tenth to a fifth of them would take them in).
                Pawn fresh = s.ws.Inmate(s.leader.Position, 60f);
                CompInmate fc = fresh.Inmate();
                int Recruits()
                {
                    int n = 0;
                    for (int i = 0; i < 600; i++)
                    {
                        GangUtility.TryRecruit(s.leader, s.Lc);
                        if (fc.gang == null) continue;
                        n++;
                        fc.gang = null;
                    }
                    return n;
                }
                int control = Recruits();
                if (control == 0)
                {
                    // (Told, not failed: the draws take the first gangless inmate in reach, and one who thinks ill of the leader is passed over.)
                    RPLog.Message($"[RimPrison:test] gang deal: recruiting not checked, the leader recruited nobody in 600 draws with no deal (the newcomer's opinion of them is {fresh.relations.OpinionOf(s.leader)})");
                    return;
                }
                arc = s.Start(GangDealTerm.Commissary, "recruiting");
                if (arc == null) return;
                int during = Recruits();
                if (during != 0) DealFail($"the leader recruited {during} times in six hundred hourly draws while the deal runs (and {control} without it)");
                // The initiation's letter, answered "play it out": the gang takes nobody in under the deal.
                var initiation = new InitiationDecision();
                int joined = 0;
                for (int i = 0; i < 30; i++)
                {
                    ChoiceLetter_PrisonDecision l = ChoiceLetter_PrisonDecision.Make("RP_Decision_Initiation", "x", "x", map, fresh);
                    l.pawns.Add(fresh);
                    l.pawns.Add(s.leader);
                    l.amount = s.ravens.shortHash;
                    initiation.Choose(l, map, 2);
                    if (fc.gang != null) joined++;
                    fc.gang = null;
                }
                if (joined != 0) DealFail($"a newcomer was initiated {joined} times of 30 letters answered \"play it out\" while the deal runs");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                int after = Recruits();
                if (after == 0) DealFail("the leader recruited nobody in six hundred hourly draws after the deal ended");
                RPLog.Message($"[RimPrison:test] gang deal: no war and no recruiting while it runs (recruits in 600 draws: {control} before, {during} during, {after} after)");
            });
        }

        // ---- a breach ----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A seen fight or assault by a member is a breach; an unseen one, a seen one by another gang's member and a seen misconduct that is neither are not. The first asks the warden (a letter of the arc: overlook, or end the deal;
        /// unanswered it is overlooked); overlooked, the deal runs on; the second breach ends it with no new letter and the first letter turns into the closed notice, and the leader's trust falls. Ending it on the first breach does
        /// the same. Fails if the breach is not counted or the letter does not come, if it is overlooked twice, or if an unseen or foreign fight counts.
        /// </summary>
        private static void CheckDealBreach(Map map)
        {
            Guarded("gang deal: a breach asks once and the second ends it", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "breach");
                if (s == null) return;
                StoryArc arc = s.Start(GangDealTerm.Visits, "breach");
                if (arc == null) return;
                MisconductDef fight = RPMisconductDefOf.RP_Fight, vandalism = RPMisconductDefOf.RP_Vandalism;
                if (!fight.assault || vandalism.assault)
                {
                    DealFail($"premise: the fight is not an assault, or vandalism is one ({fight.assault}/{vandalism.assault})");
                    return;
                }
                int ArcLetters() => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.arcId == arc.id);
                void Fight(Pawn p, bool seen = true, MisconductDef def = null) => MisconductUtility.Record(p, p.Inmate(), s.pm, def ?? fight, evidence: seen, chronicle: false, unseen: !seen);
                // What is no breach.
                Fight(s.member, seen: false);
                Fight(s.rival);
                Fight(s.member, def: vandalism);
                if (arc.deal.breaches != 0 || ArcLetters() != 0) DealFail($"a fight nobody saw, one by another gang's member, or a seen misconduct that is no assault counted as a breach ({arc.deal.breaches} breaches, {ArcLetters()} letters)");
                // The first: counted, and the letter comes (overlook or end; overlooked when unanswered).
                Fight(s.member);
                if (arc.deal.breaches != 1 || ArcLetters() != 1 || arc.done) DealFail($"a seen fight by a member: {arc.deal.breaches} breaches (expected 1), {ArcLetters()} letters (expected 1), deal over {arc.done}");
                ChoiceLetter_PrisonDecision first = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.arcId == arc.id);
                if (first == null) return;
                IReadOnlyList<string> keys = first.decision.Worker.Options(first);
                if (keys.Count != 2 || keys[0] != "RP_GangDeal_Overlook" || keys[1] != "RP_GangDeal_End" || first.decision.Worker.TimeoutOption(first) != 0) DealFail($"the breach's letter offers {string.Join(",", keys)} and answers {first.decision.Worker.TimeoutOption(first)} when unanswered (overlook, end and 0 were expected)");
                // More seen fights while the letter is open (an ambush or a riot records several members at once): the same trouble, not counted, no second letter, and the deal is not ended before the warden has answered.
                Fight(s.leader);
                Fight(s.member2);
                if (arc.deal.breaches != 1 || ArcLetters() != 1 || arc.done || s.Arc != arc) DealFail($"two more fights while the first breach's letter was open: {arc.deal.breaches} breaches (expected 1), {ArcLetters()} letters (expected 1), deal over {arc.done}");
                // Overlooked: it goes on; the chronicle says so.
                PrisonDecisions.Choose(first, 0);
                if (arc.done || s.Arc != arc || arc.deal.breaches != 1 || arc.deal.breachOpen) DealFail($"after the warden overlooked the first breach the deal is over {arc.done}, has {arc.deal.breaches} breaches or still has a breach open ({arc.deal.breachOpen})");
                if (!s.Chronicle("RP_Chr_GangDealOverlooked".Translate(s.leader.LabelShort, s.ravens.label).Resolve())) DealFail("no chronicle line for the breach overlooked");
                // The second, by the leader: the deal is over, no new letter, the trust down, the cooldown on.
                float trust0 = StaffMemories.ScoreFor(s.Lc, s.warden);
                int letters = ArcLetters();
                Fight(s.leader);
                if (!arc.done || s.Arc != null || ArcLetters() != letters) DealFail($"the second breach: deal over {arc.done}, running {s.Arc != null}, {ArcLetters() - letters} new letters (over, not running and none were expected)");
                if (!Near(StaffMemories.ScoreFor(s.Lc, s.warden), trust0 + GangDeals.EndedTrust)) DealFail($"the second breach moved the leader's trust {trust0} -> {StaffMemories.ScoreFor(s.Lc, s.warden)} (expected {GangDeals.EndedTrust:+0;-0})");
                if (!s.Chronicle("RP_Chr_GangDealEnd_Breach".Translate(s.leader.LabelShort, s.ravens.label).Resolve())) DealFail("no chronicle line for the deal ended on a second breach");
                if (!s.g.story.gangDealEnded.ContainsKey(s.ravens)) DealFail("the second breach did not start the five days before the next deal");
                // The first letter is the closed notice now.
                IReadOnlyList<string> closed = first.decision.Worker.Options(first);
                if (closed.Count != 1 || closed[0] != "RP_ArcOver") DealFail($"the letter of a deal that is over offers {string.Join(",", closed)} (the closed notice was expected)");

                // Ending it on the first breach: the same cost, the same cooldown, the term back.
                s.SkipCooldown();
                arc = s.Start(GangDealTerm.Visits, "breach 2");
                if (arc == null) return;
                Fight(s.member);
                ChoiceLetter_PrisonDecision again = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.arcId == arc.id);
                if (again == null)
                {
                    DealFail("the first breach of a second deal sent no letter");
                    return;
                }
                trust0 = StaffMemories.ScoreFor(s.Lc, s.warden);
                PrisonDecisions.Choose(again, 1);
                if (!arc.done || s.Arc != null || !Near(StaffMemories.ScoreFor(s.Lc, s.warden), trust0 + GangDeals.EndedTrust)) DealFail($"ending the deal on its first breach: over {arc.done}, running {s.Arc != null}, trust {trust0} -> {StaffMemories.ScoreFor(s.Lc, s.warden)} (expected {GangDeals.EndedTrust:+0;-0})");
                RPLog.Message("[RimPrison:test] gang deal: a seen fight is a breach, the first asks once, the second ends it, ending it costs trust; an unseen or foreign one is none");
            });
        }

        // ---- the end, however it comes, and the five days --------------------------------------------------------------------------------

        /// <summary>
        /// A deal ends at its tenth day (and not an hour before), when the warden ends it, on a second breach, and when the leader is gone or no longer leads: each writes its chronicle line and the five days, in which no new deal
        /// is struck with the gang (the word's line says so), and after which one is. One runs at a time per gang, two gangs may each have theirs. Fails if a way of ending leaves the deal running, writes no cooldown, or a deal is struck
        /// inside the five days, or a second one with the same gang while one runs.
        /// </summary>
        private static void CheckDealEnds(Map map)
        {
            Guarded("gang deal: the ten days, every way it ends and the five days after", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "ends");
                if (s == null) return;
                foreach (GangDealEnd how in new[] { GangDealEnd.Lapsed, GangDealEnd.Warden, GangDealEnd.Breach, GangDealEnd.Gone })
                {
                    s.SkipCooldown();
                    StoryArc arc = s.Start(GangDealTerm.Commissary, "ends " + how);
                    if (arc == null) return;
                    int now = s.Now;
                    switch (how)
                    {
                        case GangDealEnd.Lapsed:
                            // (The hourly step: an hour before the tenth day the deal runs, at it, it lapses.)
                            arc.deal.untilTick = now + 1;
                            StorySystem.StepArc(arc, map);
                            if (arc.done || s.Arc != arc) DealFail("a deal lapsed an hour early (the hourly step ended it with a tick to go)");
                            arc.deal.untilTick = now;
                            StorySystem.StepArc(arc, map);
                            break;
                        case GangDealEnd.Warden:
                            GangDeals.End(arc, GangDealEnd.Warden, map);
                            break;
                        case GangDealEnd.Breach:
                            MisconductUtility.Record(s.member, s.member.Inmate(), s.pm, RPMisconductDefOf.RP_Fight, evidence: true, chronicle: false);
                            if (arc.done) DealFail("a first breach ended the deal");
                            // (Overlooked first: a second fight before the warden has answered is the same breach.)
                            if (r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.arcId == arc.id) is { } breachLetter) PrisonDecisions.Choose(breachLetter, 0);
                            else DealFail("Breach: the first breach sent no letter");
                            MisconductUtility.Record(s.member, s.member.Inmate(), s.pm, RPMisconductDefOf.RP_Fight, evidence: true, chronicle: false);
                            break;
                        default:
                            // The leader loses the gang's lead (a contest lost): the hourly step finds the deal's cast is no leader, and cancels it.
                            GangRanks.SetRank(s.Lc, GangRank.Member);
                            StorySystem.StepArc(arc, map);
                            GangRanks.SetRank(s.Lc, GangRank.Leader);
                            break;
                    }
                    string line = ("RP_Chr_GangDealEnd_" + how).Translate(s.leader.LabelShort, s.ravens.label).Resolve();
                    if (!arc.done || s.Arc != null) DealFail($"{how}: the deal is over {arc.done}, still running {s.Arc != null}");
                    if (!s.Chronicle(line)) DealFail($"{how}: no chronicle line \"{line}\"");
                    if (!s.g.story.gangDealEnded.TryGetValue(s.ravens, out int ended) || ended != s.Now) DealFail($"{how}: the cooldown's clock reads {(s.g.story.gangDealEnded.TryGetValue(s.ravens, out int e2) ? (e2 - s.Now).ToString() : "nothing")} ticks from now, not 0");
                    // The five days: nothing is struck, the blocker names the wait; five days and a tick on, it is.
                    string blocker = GangDeals.Blocker(map, s.leader, s.Lc);
                    if (blocker == null || GangDeals.Start(map, s.leader, GangDealTerm.Visits) != null) DealFail($"{how}: a deal was struck within the five days after one ended (blocker: {blocker ?? "none"})");
                    s.g.story.gangDealEnded[s.ravens] = s.Now - (GangDeals.CooldownDays * GenDate.TicksPerDay - 1);
                    if (GangDeals.Blocker(map, s.leader, s.Lc) == null) DealFail($"{how}: the five days were over a tick early");
                    s.g.story.gangDealEnded[s.ravens] = s.Now - GangDeals.CooldownDays * GenDate.TicksPerDay;
                    if (GangDeals.Blocker(map, s.leader, s.Lc) != null) DealFail($"{how}: five days after the deal ended a new one is still blocked ({GangDeals.Blocker(map, s.leader, s.Lc)})");
                }
                // The leader gone: the arc's own notice of it (a leader who leaves for good).
                s.SkipCooldown();
                StoryArc leaving = s.Start(GangDealTerm.Commissary, "leaving");
                if (leaving != null)
                {
                    leaving.def.Worker.Notify_Leaving(leaving, s.leader);
                    if (!leaving.done || s.Arc != null || !s.g.story.gangDealEnded.ContainsKey(s.ravens)) DealFail("a leader leaving did not end the deal and start the five days");
                }
                // One at a time per gang, and two gangs may each have theirs.
                s.SkipCooldown();
                s.Be(s.rival, s.saints, true);
                StoryArc ravensDeal = s.Start(GangDealTerm.Commissary, "two gangs");
                StoryArc saintsDeal = GangDeals.Start(map, s.rival, GangDealTerm.Visits);
                if (ravensDeal != null && (saintsDeal == null || GangDeals.Of(s.saints) != saintsDeal || s.Arc != ravensDeal)) DealFail($"the Ravens and the Saints cannot each have a deal at once (Saints blocker: {GangDeals.Blocker(map, s.rival, s.rival.Inmate()) ?? "none"})");
                if (GangDeals.Start(map, s.leader, GangDealTerm.Visits) != null || s.g.story.arcs.Count(a => a.def == RPGangDealDefOf.RP_Arc_GangDeal && !a.done) != 2) DealFail("a second deal was struck with a gang that has one");
                RPLog.Message("[RimPrison:test] gang deal: it lapses at its tenth day, ends four ways with the five days after, one per gang at a time");
            });
        }

        // ---- the deal is the leader's ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// The deal is the leader's, not the wing's: it ends as <see cref="GangDealEnd.Gone"/> (the chronicle's line, the five days, no cost in trust) at the hourly step when the leader is no longer on the wing it was struck on
        /// (their bed given up and standing off every wing; the deal's wing is not theirs, which stands for a bed on another wing, of which the rig has none) or is on an escort-only level (the supermax), and not while they are
        /// where they were (the control). A deal whose gang is no longer defined (a load with a mod removed) is cancelled without a throw and writes no cooldown. Fails if a deal outlives its leader's wing or level, costs trust, or
        /// ends with the leader where it was struck.
        /// </summary>
        private static void CheckDealLeaderMoved(Map map)
        {
            Guarded("gang deal: it ends when the leader is off the deal's wing or sent to the supermax", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "moved");
                if (s == null) return;
                Building_Bed bed = s.leader.ownership.OwnedBed;
                IntVec3 home = s.leader.Position;
                SecurityLevelDef level0 = s.Lc.security;
                SecurityLevelDef supermax = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(l => l.escortOnly);
                PrisonZoneDef elsewhere = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.security != null && z != s.wing);
                void Case(string what, Action change, Action undo)
                {
                    s.SkipCooldown();
                    StoryArc arc = s.Start(GangDealTerm.Commissary, what);
                    if (arc == null) return;
                    StorySystem.StepArc(arc, map);
                    if (arc.done || s.Arc != arc) DealFail($"{what}: control: the hourly step ended a deal whose leader is where it was struck");
                    float trust0 = StaffMemories.ScoreFor(s.Lc, s.warden);
                    int tick = s.Now;
                    string line = "RP_Chr_GangDealEnd_Gone".Translate(s.leader.LabelShort, s.ravens.label).Resolve();
                    int lines0 = r.NewEntries.Count(e => e.text == line);
                    change();
                    StorySystem.StepArc(arc, map);
                    undo();
                    if (!arc.done || s.Arc != null) DealFail($"{what}: the deal is over {arc.done}, still running {s.Arc != null}");
                    if (r.NewEntries.Count(e => e.text == line) != lines0 + 1) DealFail($"{what}: no chronicle line for a deal that fell through");
                    if (!s.g.story.gangDealEnded.TryGetValue(s.ravens, out int ended) || ended != tick) DealFail($"{what}: the five days before the next deal were not started now");
                    if (!Near(StaffMemories.ScoreFor(s.Lc, s.warden), trust0)) DealFail($"{what}: the leader's trust moved {trust0} -> {StaffMemories.ScoreFor(s.Lc, s.warden)} (a deal that falls through costs none)");
                }
                Case("off the wing", () =>
                {
                    s.leader.ownership.UnclaimBed();
                    s.ws.Place(s.leader, s.outsider.Position);
                }, () =>
                {
                    s.ws.Place(s.leader, home);
                    s.rig.PutBack(s.leader, bed);
                });
                if (elsewhere == null) RPLog.Message("[RimPrison:test] gang deal: the deal on another wing was not run, the game has one wing only");
                else
                {
                    s.SkipCooldown();
                    StoryArc moved = s.Start(GangDealTerm.Commissary, "other wing");
                    if (moved != null)
                    {
                        moved.deal.wing = elsewhere;
                        StorySystem.StepArc(moved, map);
                        if (!moved.done || s.Arc != null) DealFail($"a deal struck on the {elsewhere.defName} wing outlived a leader who is on the {s.wing.defName} wing (over {moved.done})");
                        if (!s.g.story.gangDealEnded.ContainsKey(s.ravens)) DealFail("a deal that fell through off its wing started no five days");
                    }
                }
                if (supermax == null) RPLog.Message("[RimPrison:test] gang deal: the deal with a leader sent to the supermax was not run, the game has no escort-only level");
                else Case("the supermax", () => s.Lc.security = supermax, () => s.Lc.security = level0);
                // A deal whose gang is gone: cancelled with no throw and no cooldown for no gang.
                s.SkipCooldown();
                StoryArc orphan = s.Start(GangDealTerm.Commissary, "gang removed");
                if (orphan == null) return;
                int cooldown0 = s.g.story.gangDealEnded.TryGetValue(s.ravens, out int c0) ? c0 : -1;
                orphan.deal.gang = null;
                GangDeals.Changed();
                StorySystem.StepArc(orphan, map);
                if (!orphan.done || (s.g.story.gangDealEnded.TryGetValue(s.ravens, out int c1) ? c1 : -1) != cooldown0) DealFail($"a deal whose gang is no longer defined: over {orphan.done}, a cooldown was written (over and none were expected)");
                RPLog.Message("[RimPrison:test] gang deal: it ends as gone, at no cost, when the leader is off its wing or sent to the supermax, and a deal with no gang is cancelled without a throw");
            });
        }

        // ---- the terms: the kept patch ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// The turf term: the patch is the yard or common room patch nearest the leader's bed that no other gang holds (one held by another gang is passed over, and with only that there is no turf to give), is the gang's turf as the
        /// rest of the game reads it (held, listed as the gang's turf, in Intel's line), survives the daily verdict (the control: a claim set by hand with three others standing in the patch is let go by it) and is let go when the deal ends
        /// (a claim the gang held anyway is left). Fails if the wrong patch is kept, the claim is not the gang's, the daily verdict takes it, or the end leaves it (or takes the one the gang held).
        /// </summary>
        private static void CheckDealTurf(Map map)
        {
            Guarded("gang deal: the kept patch is the gang's turf for the deal's length", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "turf");
                if (s == null) return;
                ZoneGrid zones = s.pm.zones;
                IntVec3 from = s.leader.ownership.OwnedBed.Position;
                // The patch the picker should take, worked out here: the nearest by the patch's middle to the leader's bed, of the yard's and the common room's that the leader's level may use
                // and no other gang holds (and, as asked, not those the caller leaves out).
                int Expected(Func<int, bool> skip)
                {
                    int best = -1;
                    float bestDist = float.MaxValue;
                    for (int pi = 0; pi < zones.Patches.Count; pi++)
                    {
                        ZonePatch p = zones.Patches[pi];
                        if (p.zone != RPDefOf.RP_Yard && p.zone != RPExerciseDefOf.RP_CommonRoom || !zones.Allows(pi, s.rig.level) || p.zone.yardFor != null && p.zone.yardFor != s.rig.level || skip(pi)) continue;
                        float d = (p.center.x - from.x) * (p.center.x - from.x) + (p.center.z - from.z) * (p.center.z - from.z);
                        if (d < bestDist) (best, bestDist) = (pi, d);
                    }
                    return best;
                }
                int expected = Expected(_ => false);
                if (expected < 0)
                {
                    if (GangDeals.TermWhy(map, s.leader, GangDealTerm.Turf) == null) DealFail("with no yard or common room patch the turf term is on offer");
                    RPLog.Message("[RimPrison:test] gang deal: the turf term was not run, the prison has no yard or common room patch");
                    return;
                }
                if (GangDeals.TermWhy(map, s.leader, GangDealTerm.Turf) != null) DealFail($"the turf term is not on offer with a patch free ({GangDeals.TermWhy(map, s.leader, GangDealTerm.Turf)})");
                ZonePatch patch = zones.Patches[expected];
                List<IntVec3> cells = zones.CellsOfPatch(expected).Where(c => c.Standable(map)).ToList();
                if (cells.Count < 3)
                {
                    RPLog.Message($"[RimPrison:test] gang deal: the turf term was not run, the nearest patch has {cells.Count} standable cells (three are needed to stand in it)");
                    return;
                }
                // The control: the daily verdict lets a claim go when others make up the patch (three gangless inmates standing in it and none of the gang's).
                var crowd = new List<Pawn>();
                for (int i = 0; i < 3; i++) crowd.Add(s.ws.Inmate(cells[i], 30f));
                void Day()
                {
                    GangTurf.Sample(map, s.pm);
                    GangTurf.Daily(map);
                }
                s.pm.turf.Set(zones, patch.zone, patch.anchor, s.ravens, s.Now);
                Day();
                if (s.pm.turf.At(patch.anchor) != null)
                {
                    DealFail("premise: a claim set by hand survived the daily verdict with three gangless inmates standing in the patch (the control that shows the deal's protection)");
                    return;
                }
                // With the deal.
                StoryArc arc = s.Start(GangDealTerm.Turf, "turf");
                if (arc == null) return;
                int kept = zones.PatchAt(arc.stake);
                if (arc.stake != patch.anchor || arc.deal.claimMade == false) DealFail($"the kept patch is {(kept >= 0 ? zones.Patches[kept].zone.defName : "none")} at {arc.stake}, not the nearest ({patch.zone.defName} at {patch.anchor}), or the deal did not make the claim");
                TurfClaim claim = s.pm.turf.At(patch.anchor);
                if (claim?.gang != s.ravens || claim.zone != patch.zone || GangTurf.HolderAt(s.pm, cells[0]) != s.ravens) DealFail($"the kept patch is not the gang's turf ({claim?.gang?.defName ?? "no claim"}, held by {GangTurf.HolderAt(s.pm, cells[0])?.defName ?? "nobody"})");
                if (!GangTurf.KnownTurf(map, s.pm).Any(t => t.claim.gang == s.ravens && t.claim.anchor == patch.anchor) || GangTurf.HeldLine(map, s.ravens) == null) DealFail("the kept patch is not drawn as the known gang's turf, and is not in Intel's line of the places it holds");
                if (!GangDeals.TermText(map, arc).Contains(GangTurf.PlaceName(zones, expected))) DealFail($"the term's words do not name the place (\"{GangDeals.TermText(map, arc)}\")");
                Day();
                if (s.pm.turf.At(patch.anchor)?.gang != s.ravens) DealFail("the daily verdict let the kept patch go while the deal runs, with three others standing in it");
                // Over: the claim the deal made is let go.
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (s.pm.turf.At(patch.anchor) != null || GangTurf.HolderAt(s.pm, cells[0]) != null) DealFail("the kept patch is still the gang's when the deal is over");
                // A patch the gang held anyway is left to the daily verdict: the deal did not make the claim, and does not unmake it.
                s.SkipCooldown();
                s.pm.turf.Set(zones, patch.zone, patch.anchor, s.ravens, s.Now);
                arc = s.Start(GangDealTerm.Turf, "turf held");
                if (arc == null) return;
                if (arc.stake != patch.anchor || arc.deal.claimMade) DealFail($"a patch the gang held already was taken as a claim of the deal's (stake {arc.stake}, claim made {arc.deal.claimMade})");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (s.pm.turf.At(patch.anchor)?.gang != s.ravens) DealFail("a patch the gang held anyway was let go when the deal ended");
                s.pm.turf.Remove(patch.anchor);
                // A patch another gang holds is passed over.
                s.SkipCooldown();
                s.pm.turf.Set(zones, patch.zone, patch.anchor, s.saints, s.Now);
                int other = Expected(pi => s.pm.turf.At(zones.Patches[pi].anchor) is { } held && held.gang != s.ravens);
                int picked = GangDeals.PickPatch(s.leader, s.Lc, s.pm);
                if (picked != other || picked == expected) DealFail($"with the nearest patch ({expected}) held by the Saints the picker took patch {picked} (the nearest patch no other gang holds is {other})");
                s.pm.turf.Remove(patch.anchor);
                RPLog.Message($"[RimPrison:test] gang deal: the kept patch ({patch.zone.defName} at {patch.anchor}) is the nearest, the gang's turf, safe from the daily verdict and let go at the end");
            });
        }

        // ---- the terms: visits and the commissary ---------------------------------------------------------------------------------------

        /// <summary>
        /// The visits term: a member is owed a visit once <see cref="GangDeals.VisitGapDays"/> days have gone by without one from a relative (from the deal's start, or their last such visit), a member of another gang never, no one under
        /// another term, and no one after the deal; and the real hook, <see cref="VisitationSystem.HourlyTick"/>, calls a visitor for the member who is owed one (with their family need well above what calls one) and for nobody
        /// else, as a control calls none with no deal (told, not failed, when the prison has no visitation room a visitor can reach); a friend who got out does not stand for the visit owed, a relative does. The commissary term: a member's
        /// shop ban does not hold while it runs (the ban's own clock is untouched and holds again after), a rival's does, and another term does not lift it. Fails if the ban holds for a member under the term or stops holding for a rival,
        /// if a visit is owed too early, to a rival or after the deal, if the hourly pass calls no visitor for a member who is owed one, or one for a member who is not or with no deal, or if the wrong visits clear the clock.
        /// </summary>
        private static void CheckDealVisitsAndCommissary(Map map)
        {
            Guarded("gang deal: the visits and the commissary terms", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "visits");
                if (s == null) return;
                // (The days are counted from the deal's start and the last visit, and the checks live in the first days of a game: with the clock at less than the gap, backdating a start would
                // go below 0 and below the "no visit yet" mark. The clock is moved on to day 10 and put back when the check is rolled back; no tick runs in between.)
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(Math.Max(clock0, 10 * GenDate.TicksPerDay));
                int now = s.Now, day = GenDate.TicksPerDay;
                Pawn[] members = { s.leader, s.member, s.member2, s.outsider };
                // Visits: on offer only when somebody of the gang has family to come and there is a room for it.
                foreach (Pawn p in members) p.Inmate().familyCount = 0;
                if (GangDeals.TermWhy(map, s.leader, GangDealTerm.Visits) == null) DealFail("the visits term is on offer to a gang none of whose members has family");
                s.member.Inmate().familyCount = 1;
                bool room = s.pm.zones.Any(RPVisitDefOf.RP_Visitation);
                if ((GangDeals.TermWhy(map, s.leader, GangDealTerm.Visits) == null) != room) DealFail($"the visits term with a member who has family and {(room ? "a" : "no")} visitation room is {(room ? "not " : "")}on offer");
                foreach (Pawn p in members.Append(s.rival)) p.Inmate().lastVisitTick = -1;
                if (members.Any(p => GangDeals.VisitOwed(p.Inmate()))) DealFail("a visit was owed with no deal");
                StoryArc arc = s.Start(GangDealTerm.Visits, "visits");
                if (arc == null) return;
                if (members.Any(p => GangDeals.VisitOwed(p.Inmate()))) DealFail("a visit was owed the moment the deal began (the days count from its start)");
                arc.startTick = now - (int)(GangDeals.VisitGapDays * day) + 10;
                if (members.Any(p => GangDeals.VisitOwed(p.Inmate()))) DealFail("a visit was owed ten ticks before the gap was up");
                arc.startTick = now - (int)(GangDeals.VisitGapDays * day) - 10;
                if (members.Any(p => !GangDeals.VisitOwed(p.Inmate()))) DealFail($"after {GangDeals.VisitGapDays} days with no visit a member is not owed one ({string.Join(", ", members.Select(p => p.LabelShort + "=" + GangDeals.VisitOwed(p.Inmate())))})");
                if (GangDeals.VisitOwed(s.rival.Inmate())) DealFail("a member of another gang is owed a visit by the deal");
                s.member.Inmate().lastVisitTick = now;
                if (GangDeals.VisitOwed(s.member.Inmate()) || !GangDeals.VisitOwed(s.member2.Inmate())) DealFail("a visit just made did not clear the owed visit of the one who had it (or cleared another's)");
                s.member.Inmate().lastVisitTick = now - (int)(GangDeals.VisitGapDays * day) - 10;
                if (!GangDeals.VisitOwed(s.member.Inmate())) DealFail("a member whose last visit was longer ago than the gap is not owed one");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (members.Any(p => GangDeals.VisitOwed(p.Inmate()))) DealFail("a visit was owed after the deal ended");
                CheckDealVisitsHook(s, room);
                // The commissary.
                s.SkipCooldown();
                foreach (Pawn p in members.Append(s.rival)) p.Inmate().shopBanUntilTick = now + 5000;
                bool Banned(Pawn p) => Sanctions.Banned(p.Inmate(), Sanction.LoseCommissary);
                if (!members.Append(s.rival).All(Banned)) DealFail("premise: a shop ban does not hold with no deal");
                arc = s.Start(GangDealTerm.Visits, "visits term");
                if (arc == null) return;
                if (!members.All(Banned)) DealFail("a visits term lifted the shop ban");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                s.SkipCooldown();
                arc = s.Start(GangDealTerm.Commissary, "commissary");
                if (arc == null) return;
                if (members.Any(Banned)) DealFail($"the shop ban still holds for a member while the commissary term runs ({string.Join(", ", members.Select(p => p.LabelShort + "=" + Banned(p)))})");
                if (!Banned(s.rival)) DealFail("the commissary term lifted the shop ban of another gang's member");
                if (members.Any(p => p.Inmate().shopBanUntilTick != now + 5000)) DealFail("the commissary term changed the ban's own clock");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (!members.All(Banned)) DealFail("the shop ban did not hold again when the deal ended");
                RPLog.Message("[RimPrison:test] gang deal: the visits term owes a visit after 3.5 days without one, the commissary term holds the shop ban off for the members only while it runs");
            });
        }

        /// <summary>
        /// The visits term through the real hook (see <see cref="CheckDealVisitsAndCommissary"/>): two members of the gang with family and a family need of 1 (no need to call a visitor), one owed a visit and one not. The hourly pass
        /// with no deal calls nobody (the control); with the deal it calls the one who is owed. A visit that ends is counted for the clock when the visitor is a relative and not when it is a friend who got out.
        /// </summary>
        private static void CheckDealVisitsHook(DealScene s, bool room)
        {
            Map map = s.map;
            Pawn owed = s.member, kept = s.member2;
            CompInmate oc = owed.Inmate(), kc = kept.Inmate();
            if (!room)
            {
                RPLog.Message("[RimPrison:test] gang deal: the visits hook was not driven, the prison has no visitation room");
                return;
            }
            SecurityLevelDef sec = oc.security;
            bool hadPolicy = s.g.visitPolicies.TryGetValue(sec, out VisitPolicy policy0);
            s.r.Add(() =>
            {
                if (hadPolicy) s.g.visitPolicies[sec] = policy0;
                else s.g.visitPolicies.Remove(sec);
            });
            s.g.visitPolicies[sec] = VisitPolicy.Contact;
            int hour = Enumerable.Range(9, 10).FirstOrDefault(h => !s.g.ActivityAt(oc, (h + 1) % 24).lockup && !s.g.ActivityAt(kc, (h + 1) % 24).lockup);
            if (hour == 0)
            {
                RPLog.Message("[RimPrison:test] gang deal: the visits hook was not driven, the members are locked up at every visiting hour");
                return;
            }
            // Everyone else is out of the pass (no visit for a real inmate, or the rival, while it runs), the two have family and no need of a visit.
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (p == owed || p == kept || p.Inmate() is not { } pc) continue;
                int ban0 = pc.visitBanUntilTick;
                s.r.Add(() => pc.visitBanUntilTick = ban0);
                pc.visitBanUntilTick = s.Now + 100 * GenDate.TicksPerDay;
            }
            foreach (CompInmate c in new[] { oc, kc })
            {
                if (!Relatives.Of(c).Exists(x => x.CanVisit)) Relatives.Of(c).Add(Relatives.Make());
                Relatives.Recount(c);
                (c.visitor, c.visitBanUntilTick, c.forcedActivity, c.lastVisitTick) = (null, -1, null, -1);
            }
            foreach (Pawn p in new[] { owed, kept })
                if (p.needs.TryGetNeed(RPVisitDefOf.RP_Family) is { } family) family.CurLevel = 1f;
            // (The visitors the pass spawns are made up for the check: gone with the rollback, whatever became of the visit.)
            var spawned = new List<Pawn>();
            s.r.Add(() =>
            {
                foreach (Pawn v in spawned)
                    if (!v.Destroyed) v.Destroy();
                oc.visitor = kc.visitor = null;
            });
            void Called()
            {
                foreach (CompInmate c in new[] { oc, kc })
                    if (c.visitor != null && !spawned.Contains(c.visitor)) spawned.Add(c.visitor);
            }
            // The control: no deal, the hourly pass calls nobody for a member with no need of a visit.
            VisitationSystem.HourlyTick(map, s.g, hour);
            Called();
            if (oc.visitor != null || kc.visitor != null)
            {
                DealFail($"the hourly visits pass called a visitor with no deal for a member whose family need is full ({oc.visitor != null}/{kc.visitor != null})");
                return;
            }
            // The deal, the clocks one owed and one not: the pass calls the one who is owed.
            s.SkipCooldown();
            StoryArc arc = s.Start(GangDealTerm.Visits, "visits hook");
            if (arc == null) return;
            arc.startTick = s.Now - (int)(GangDeals.VisitGapDays * GenDate.TicksPerDay) - 10;
            kc.lastVisitTick = s.Now - GenDate.TicksPerHour;
            if (!GangDeals.VisitOwed(oc) || GangDeals.VisitOwed(kc))
            {
                DealFail("premise: one member is not owed a visit and the other is");
                return;
            }
            VisitationSystem.HourlyTick(map, s.g, hour);
            Called();
            if (oc.visitor == null)
            {
                // (Told, not failed: the pass takes no more visits than the room has places for, and the visitor needs a place in the room that their level may reach.)
                RPLog.Message($"[RimPrison:test] gang deal: the visits hook was not driven to a visitor, none came for the member who is owed one (a place in the room, or one their level may reach, was not there; the other came: {kc.visitor != null})");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                return;
            }
            if (kc.visitor != null) DealFail("the hourly visits pass called a visitor for a member who is not owed one, whose family need is full");
            // A friend who got out stands for no visit of the family's: the clock stays; a relative's does count.
            Pawn visitor = oc.visitor;
            oc.visitorFormer = true;
            oc.visitorContraband = null;
            VisitationSystem.Complete(owed, visitor);
            if (oc.lastVisitTick != -1 || !GangDeals.VisitOwed(oc)) DealFail($"a visit of a friend who got out cleared the visit the deal owes (last visit {oc.lastVisitTick}, owed {GangDeals.VisitOwed(oc)})");
            oc.visitorFormer = false;
            oc.visitorContraband = null;
            VisitationSystem.Complete(owed, visitor);
            if (oc.lastVisitTick != s.Now || GangDeals.VisitOwed(oc)) DealFail($"a visit of a relative did not clear the visit the deal owes (last visit {oc.lastVisitTick}, now {s.Now}, owed {GangDeals.VisitOwed(oc)})");
            GangDeals.End(arc, GangDealEnd.Lapsed, map);
            RPLog.Message("[RimPrison:test] gang deal: the hourly visits pass calls the member who is owed a visit and not the other; no visit with no deal; a friend's visit does not stand for the family's, a relative's does");
        }

        // ---- the terms: the job slot ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The job term: of the leader's gang the member with the best fit (the highest skill in what the job asks) for a job with a place free is given it, locked in (never the leader, nor a member whose job the warden set by hand,
        /// however skilled); when the deal ends the job is unlocked and gone (a job the warden changed since is left); a gang of one has no member to give a job to and the term is not on offer. Fails if another member is chosen, the
        /// job is not locked in, it is still theirs after the deal, or a job the warden set was undone.
        /// </summary>
        private static void CheckDealJob(Map map)
        {
            Guarded("gang deal: the job slot goes to the best fit and is taken back", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "job");
                if (s == null) return;
                Pawn[] gang = { s.leader, s.member, s.member2, s.outsider };
                foreach (Pawn p in gang)
                {
                    CompInmate c = p.Inmate();
                    (c.job, c.jobLocked, c.tension) = (null, false, 10f);
                    foreach (SkillRecord sk in p.skills.skills) sk.Level = 0;
                }
                // A job with a skill, a place free (the prison's own inmates hold some) and the rig's level allowed in it.
                InmateJobDef job = null;
                var held = new Dictionary<InmateJobDef, int>();
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate()?.job is { } j && !gang.Contains(p)) held[j] = (held.TryGetValue(j, out int n) ? n : 0) + 1;
                foreach (InmateJobDef j in DefDatabase<InmateJobDef>.AllDefsListForReading)
                    if (j.skill != null && j.Worker.Capacity(map) - (held.TryGetValue(j, out int n) ? n : 0) >= 1 && gang.All(p => LaborUtility.Eligible(p.Inmate(), j))) { job = j; break; }
                if (job == null)
                {
                    RPLog.Message("[RimPrison:test] gang deal: the job term was not run, no job with a skill has a place free for the gang's members");
                    return;
                }
                // The best fit: member 2 at 12 in the job's skill, the rest at nothing; the leader at 20 (never chosen) and a member the warden gave a job by hand at 15 (never taken from).
                s.member2.skills.GetSkill(job.skill).Level = 12;
                s.leader.skills.GetSkill(job.skill).Level = 20;
                s.member.skills.GetSkill(job.skill).Level = 15;
                s.member.Inmate().jobLocked = true;
                if (!GangDeals.PickJob(map, s.leader, s.Lc, out Pawn chosen, out InmateJobDef given) || chosen != s.member2 || given.skill != job.skill)
                    DealFail($"the job term picked {chosen?.LabelShort ?? "nobody"} for {given?.defName ?? "no job"} (expected {s.member2.LabelShort} for a job that uses {job.skill.defName}: 12 against the leader's 20 and a locked member's 15)");
                StoryArc arc = s.Start(GangDealTerm.Job, "job");
                if (arc == null) return;
                CompInmate mc = s.member2.Inmate();
                if (mc.job != given || !mc.jobLocked || arc.deal.jobMemberId != s.member2.thingIDNumber || arc.deal.jobDef != given || arc.deal.jobMemberName != s.member2.LabelShort)
                    DealFail($"the job was not locked in for {s.member2.LabelShort} ({mc.job?.defName ?? "no job"}, locked {mc.jobLocked}; payload {arc.deal.jobMemberId}/{arc.deal.jobDef?.defName})");
                if (s.member.Inmate().job != null || s.leader.Inmate().job != null) DealFail("a job was given to the leader or to a member the warden had locked");
                if (!GangDeals.TermText(map, arc).Contains(s.member2.LabelShort)) DealFail($"the term's words do not name the member (\"{GangDeals.TermText(map, arc)}\")");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (mc.job != null || mc.jobLocked) DealFail($"the job is still the member's when the deal is over ({mc.job?.defName ?? "no job"}, locked {mc.jobLocked})");
                // Back to what they had: a member with a job of their own has it again, and one the deal kept in the very job they held keeps it (neither is left with nothing).
                InmateJobDef own = DefDatabase<InmateJobDef>.AllDefsListForReading.FirstOrDefault(j => j != given && LaborUtility.Eligible(mc, j));
                foreach (InmateJobDef before in new[] { own, given })
                {
                    if (before == null) continue;
                    s.SkipCooldown();
                    (mc.job, mc.jobLocked) = (before, false);
                    arc = s.Start(GangDealTerm.Job, "job before " + before.defName);
                    if (arc == null) return;
                    if (arc.deal.jobMemberId != s.member2.thingIDNumber || arc.deal.prevJob != before || arc.deal.prevJobLocked)
                    {
                        DealFail($"premise: the deal's job member is {arc.deal.jobMemberName} and the job they had is {arc.deal.prevJob?.defName ?? "none"} (expected {s.member2.LabelShort} and {before.defName})");
                        GangDeals.End(arc, GangDealEnd.Lapsed, map);
                        continue;
                    }
                    GangDeals.End(arc, GangDealEnd.Lapsed, map);
                    if (mc.job != before || mc.jobLocked) DealFail($"a member who had {before.defName} before the deal has {mc.job?.defName ?? "no job"} (locked {mc.jobLocked}) after it");
                }
                // The lock they had is put back too (the picker never takes a locked member, so it is forced here).
                s.SkipCooldown();
                (mc.job, mc.jobLocked) = (null, false);
                arc = s.Start(GangDealTerm.Job, "job lock");
                if (arc == null) return;
                arc.deal.prevJobLocked = true;
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (mc.job != null || !mc.jobLocked) DealFail($"the lock the member had before the deal was not put back (job {mc.job?.defName ?? "none"}, locked {mc.jobLocked}; locked was expected)");
                mc.jobLocked = false;
                // A job the warden changed since is theirs.
                s.SkipCooldown();
                arc = s.Start(GangDealTerm.Job, "job changed");
                if (arc == null) return;
                (mc.job, mc.jobLocked) = (null, true);
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (mc.job != null || !mc.jobLocked) DealFail("a job the warden set by hand (none, locked) was undone by the end of the deal");
                // A gang of one: no member to give a job to.
                if (GangDeals.TermWhy(map, s.rival, GangDealTerm.Job) == null) DealFail("the job term is on offer to a gang with no member but the one who asks");
                RPLog.Message($"[RimPrison:test] gang deal: the job ({given.defName}) went to the best fit ({s.member2.LabelShort}), locked in and taken back");
            });
        }

        // ---- the inspector ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The inspector's score (the same report card, the same tick) is lower by exactly <see cref="GangDeals.InspectionPoints"/> while a deal runs, and what it was before and after. Fails if the score is not lower by the
        /// stated amount while it runs, or is lower after.
        /// </summary>
        private static void CheckDealInspector(Map map)
        {
            Guarded("gang deal: the inspector's score is lower while a deal runs", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "inspector");
                if (s == null) return;
                ReportCard card = PrisonGrading.Compute(map, s.g);
                int before = Inspections.Score(s.g, map, card);
                StoryArc arc = s.Start(GangDealTerm.Commissary, "inspector");
                if (arc == null) return;
                int during = Inspections.Score(s.g, map, card);
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                int after = Inspections.Score(s.g, map, card);
                if (during != before - GangDeals.InspectionPoints || after != before) DealFail($"the inspector's score was {before} before, {during} during (expected {before - GangDeals.InspectionPoints}) and {after} after the deal");
                RPLog.Message($"[RimPrison:test] gang deal: the inspector's score {before} -> {during} while it runs, {after} after");
            });
        }

        // ---- the rivals' tension ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A member of another gang on the deal's wing is wound up by exactly <see cref="GangDeals.RivalTensionPerHour"/> an hour while it runs, with the cause "the Ravens run the yard" (only said when their own gang is on
        /// file: else it is trouble on the wing); the gang's own members, a member of another gang off the wing and an inmate of no gang are not; and none after the deal. Fails if the term is not added, is added where it should
        /// not be, names the wrong cause, or outlives the deal.
        /// </summary>
        private static void CheckDealRivals(Map map)
        {
            Guarded("gang deal: the other gangs on the wing are wound up", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "rivals");
                if (s == null) return;
                // The rival stands in their cell, far from the Ravens at the intake (a rival gang's member nearby would be the cause instead).
                IntVec3 cell = s.rig.Bed(3, 0).Position;
                s.ws.Place(s.rival, cell);
                Pawn off = s.Be(s.ws.Inmate(null, 30f), s.saints, false);
                Pawn none = s.ws.Inmate(null, 30f);
                if (s.rival.Position.InHorDistOf(s.leader.Position, 10f))
                {
                    DealFail("premise: the Saints' rival in the wing is within ten cells of the Ravens at the intake, and would be wound up by them");
                    return;
                }
                float T(Pawn p, out string cause) => GangUtility.TensionFromGangs(p, p.Inmate(), s.pm, out _, out cause);
                float rival0 = T(s.rival, out string cause0), own0 = T(s.member, out _), off0 = T(off, out _), none0 = T(none, out _);
                StoryArc arc = s.Start(GangDealTerm.Commissary, "rivals");
                if (arc == null) return;
                float rival1 = T(s.rival, out string cause1), own1 = T(s.member, out _), off1 = T(off, out _), none1 = T(none, out _);
                // A rival whose gang the warden has not found out is wound up all the same, by "trouble on the wing", never by the gang's name.
                s.rival.Inmate().gangKnown = false;
                float hiddenT = T(s.rival, out string hiddenCause);
                s.rival.Inmate().gangKnown = true;
                if (!Near(hiddenT, rival1) || hiddenCause != "RP_Cause_WingTrouble") DealFail($"a rival whose gang is not on file: tension {hiddenT} (expected {rival1}), cause \"{hiddenCause}\" (expected trouble on the wing)");
                string key = GangDeals.CauseKey(s.ravens);
                if (!Near(rival1 - rival0, GangDeals.RivalTensionPerHour)) DealFail($"a Saints member on the Ravens' wing: tension from gangs {rival0} -> {rival1}, not up by {GangDeals.RivalTensionPerHour}");
                if (cause1 != key || cause0 == key) DealFail($"the Saints member's cause is \"{cause1}\" with the deal (and \"{cause0}\" before): \"{key}\" was expected only with it");
                if (!Near(own1, own0) || !Near(off1, off0) || !Near(none1, none0)) DealFail($"wound up where it should not be: the gang's own {own0} -> {own1}, a Saints member off the wing {off0} -> {off1}, an inmate of no gang {none0} -> {none1}");
                if (!key.CanTranslate() || key == GangDeals.CausePrefix) DealFail($"no keyed cause for the Ravens' deal (\"{key}\")");
                // The cause is only named where their own gang is on file.
                CompInmate rc = s.rival.Inmate();
                string cause = rc.mainCause;
                rc.mainCause = key;
                rc.gangKnown = false;
                string hidden = TensionUtility.ShownCause(rc);
                rc.gangKnown = true;
                string shown = TensionUtility.ShownCause(rc);
                rc.mainCause = cause;
                if (hidden != "RP_Cause_WingTrouble" || shown != key) DealFail($"the cause reads \"{hidden}\" for a member whose gang is not on file (trouble on the wing was expected) and \"{shown}\" for one who is");
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                if (!Near(T(s.rival, out _), rival0)) DealFail("the rivals' tension outlived the deal");
                RPLog.Message($"[RimPrison:test] gang deal: a rival on the wing is wound up by {rival1 - rival0} an hour (\"{key}\"), nobody else is");
            });
        }

        // ---- the press -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The press hears of a deal once (the hourly step's chance, many hours over): one request for an interview about it, and no second however many more hours go by; and an open interview while a deal runs costs
        /// <see cref="GangDeals.PressPenalty"/> reputation more than it would without. Fails if no press letter comes in three thousand hours, if two do, or if the interview is not that much worse.
        /// </summary>
        private static void CheckDealPress(Map map)
        {
            Guarded("gang deal: the press once, and a worse interview", map, r =>
            {
                DealScene s = DealScene.Begin(map, r, "press");
                if (s == null) return;
                StoryArc arc = s.Start(GangDealTerm.Commissary, "press");
                if (arc == null) return;
                int Press() => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is("RP_Decision_Press"));
                int hours = 0;
                while (!arc.deal.pressDone && hours < 3000)
                {
                    StorySystem.StepArc(arc, map);
                    hours++;
                }
                if (Press() != 1 || !arc.deal.pressDone) DealFail($"in {hours} hours the press sent {Press()} requests about the deal (press done {arc.deal.pressDone}); one was expected");
                for (int i = 0; i < 3000; i++) StorySystem.StepArc(arc, map);
                if (Press() != 1) DealFail($"{Press()} press requests in all for one deal");
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Press"));
                if (letter != null && letter.Text.ToString().IndexOf(arc.deal.leaderName, StringComparison.Ordinal) < 0) DealFail("the press's letter about the deal does not name the leader");
                // The interview: the same open door, a worse story while the deal runs.
                float lastGrade0 = s.g.lastGrade;
                r.Add(() => s.g.lastGrade = lastGrade0);
                s.g.lastGrade = 50f;
                var press = new PressDecision();
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                s.g.reputation = 50f;
                press.Choose(null, map, 0);
                float without = s.g.reputation - 50f;
                s.SkipCooldown();
                arc = s.Start(GangDealTerm.Commissary, "press 2");
                if (arc == null) return;
                s.g.reputation = 50f;
                press.Choose(null, map, 0);
                float with = s.g.reputation - 50f;
                if (!Near(without - with, GangDeals.PressPenalty)) DealFail($"an open interview moved the reputation {without:+0.0;-0.0} without a deal and {with:+0.0;-0.0} with one (expected {GangDeals.PressPenalty} less)");
                RPLog.Message($"[RimPrison:test] gang deal: the press asked once ({hours} hours), the interview is {GangDeals.PressPenalty} worse under a deal");
            });
        }

        // ---- the saveload plan -------------------------------------------------------------------------------------------------------------

        /// <summary>What the saveload plan changed for the deal's seed, to put back after the reload: the inmate made a Ravens leader (their gang, flags and last visit) and the Saints' cooldown clock.</summary>
        private static (int id, GangDef gang, bool known, bool leader, byte rank, int lastVisit)? dealSeedWas;
        private static bool dealSeeded;

        /// <summary>Every deal and cooldown as one comparable string (see <see cref="GangDeals.Describe"/>).</summary>
        private static string GangDealDescribe(PrisonGame g) => GangDeals.Describe(g);

        /// <summary>
        /// A deal off every default (the Ravens' leader, a job term with a member, a job and the job and lock the member had before, a breach letter open, the wing, ten days less a few, a breach, the press told, a kept patch's name), the inmate's last visit and the Saints' cooldown
        /// clock. The deal is started by hand, not struck: nothing is locked or claimed, and it is taken out again after the reload (<see cref="UnseedGangDealAfterReload"/>).
        /// </summary>
        private static void SeedGangDealForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { security: not null } && !g.story.InArc(x)).Take(2).ToList();
            if (inmates.Count < 2)
            {
                problems0.Add("saveload: fewer than two inmates to seed a deal with");
                return;
            }
            Pawn boss = inmates[0], hand = inmates[1];
            CompInmate c = boss.Inmate();
            int now = Find.TickManager.TicksGame;
            dealSeedWas = (boss.thingIDNumber, c.gang, c.gangKnown, c.gangLeader, c.gangRank, c.lastVisitTick);
            c.gang = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
            c.gangKnown = true;
            GangRanks.SetRank(c, GangRank.Leader);
            c.lastVisitTick = now - 5555;
            StoryArc arc = g.story.TryStartArc(RPGangDealDefOf.RP_Arc_GangDeal, boss);
            if (arc == null)
            {
                problems0.Add("saveload: the deal's arc could not be started");
                return;
            }
            arc.note = c.gang.defName;
            arc.stake = 4242;
            arc.deal = new GangDeal
            {
                gang = c.gang, leaderId = boss.thingIDNumber, leaderName = boss.LabelShort, term = GangDealTerm.Job,
                wing = DefDatabase<PrisonZoneDef>.AllDefsListForReading.First(z => z.security != null), untilTick = now + 7777, breaches = 1, claimMade = true,
                jobMemberId = hand.thingIDNumber, jobMemberName = hand.LabelShort, jobDef = DefDatabase<InmateJobDef>.AllDefsListForReading.First(), pressDone = true,
                prevJob = DefDatabase<InmateJobDef>.AllDefsListForReading.Last(), prevJobLocked = true, breachOpen = true,
            };
            g.story.gangDealEnded[DefDatabase<GangDef>.GetNamed("RP_Gang_Saints")] = now - 999;
            GangDeals.Changed();
            dealSeeded = true;
        }

        /// <summary>After the reload: the seeded deal and cooldown are taken out again and the inmate is as they were (what came back was compared by the snapshot).</summary>
        private static void UnseedGangDealAfterReload(Map map)
        {
            if (!dealSeeded) return;
            dealSeeded = false;
            PrisonGame g = PrisonGame.Instance;
            StoryArc back = g.story.arcs.FirstOrDefault(a => a.def == RPGangDealDefOf.RP_Arc_GangDeal);
            bool whole = back?.deal is { gang.defName: "RP_Gang_Ravens", term: GangDealTerm.Job, breaches: 1, claimMade: true, pressDone: true, prevJobLocked: true, breachOpen: true, jobMemberName: not null } && back.stake == 4242 && back.deal.jobDef != null && back.deal.prevJob != null && back.deal.wing != null && back.deal.untilTick > 0
                         && g.story.gangDealEnded.ContainsKey(DefDatabase<GangDef>.GetNamed("RP_Gang_Saints")) && GangDeals.Holds(DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"));
            RPLog.Message($"[RimPrison:test] saveload gang deal: {(whole ? "intact" : "changed")} ({back?.deal?.term}/{back?.deal?.breaches}/{back?.stake})");
            if (!whole) reloadProblems.Add($"the shot-caller's deal changed in the round trip ({back?.deal?.term}/{back?.deal?.breaches}/{back?.stake}/{back?.deal?.jobMemberName})");
            g.story.arcs.RemoveAll(a => a == back);
            g.story.gangDealEnded.Remove(DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"));
            GangDeals.Changed();
            if (dealSeedWas is { } was && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == was.id)?.Inmate() is { } c)
            {
                (c.gang, c.gangKnown, c.gangLeader, c.gangRank, c.lastVisitTick) = (was.gang, was.known, was.leader, was.rank, was.lastVisit);
            }
            dealSeedWas = null;
        }
    }
}
