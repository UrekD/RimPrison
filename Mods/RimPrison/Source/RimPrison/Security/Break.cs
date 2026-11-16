using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPBreakDefOf
    {
        public static StoryArcDef RP_Arc_Break;
        public static MisconductDef RP_EscapePlot;

        static RPBreakDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPBreakDefOf));
    }

    /// <summary>What a link of a plan is: one of the crew, or one of the four parts.</summary>
    public enum PlanRole : byte
    {
        /// <summary>The inmate the plan is built around (the arc's cast).</summary>
        Leader,
        Member,
        /// <summary>A tool a member holds (by thing id), pockets or stash.</summary>
        Tool,
        /// <summary>The inside man: a corrupt officer on the crew's payroll.</summary>
        Officer,
        Way,
        /// <summary>The night: there is a link for it only once it is chosen.</summary>
        Night,
    }

    public enum PlanWay : byte
    {
        /// <summary>The fence at an unlit stretch.</summary>
        Fence,
        /// <summary>A tunnel from a member's cell (the link's pawn).</summary>
        Tunnel,
        /// <summary>The shuttle ramp, in pad duty's blind hour.</summary>
        Ramp,
        /// <summary>The gate, in a guard's uniform from the laundry.</summary>
        Uniform,
    }

    public enum PlanNight : byte
    {
        /// <summary>The darkest stretch of the night.</summary>
        Dark,
        /// <summary>The guards' handover hour.</summary>
        Handover,
        Storm,
        Blackout,
    }

    /// <summary>The warden's answer to the plan (the letter's, or Intel's buttons after it).</summary>
    public enum PlanAnswer : byte
    {
        None,
        Foil,
        /// <summary>Let it run (and, with the way known, a post on it for the night).</summary>
        Watch,
        Turn,
    }

    /// <summary>
    /// One link of a plan, by thing id and name (it outlives the pawn on the map). Nothing shown or written may name a link that is not <see cref="known"/>; what the warden knows of a known one is
    /// <see cref="shown"/>, set when it is found (a tool known from the tool count names the room it went missing from, not who has it).
    /// </summary>
    public class PlanLink : IExposable
    {
        public PlanRole role;
        /// <summary>The inmate (leader, member), who holds the tool, the officer, the member a tunnel is dug from; -1 none.</summary>
        public int pawnId = -1;
        public string name;
        public bool known;
        /// <summary>Out of the plan: the tool taken, the way closed, a member gone; known, so kept for the warden to see.</summary>
        public bool handled;
        /// <summary>The inside man works for the warden (the plan does not know).</summary>
        public bool turned;
        /// <summary>The tool's thing id (-1 for every other link).</summary>
        public int thingId = -1;
        public string shown;
        /// <summary>
        /// A tool link: the inmate the warden has learned holds it (a tip, a find on them), -1 while the tool is known from the count alone. What the warden knows, kept apart from <see cref="pawnId"/>, the true holder,
        /// who changes hands unseen (<see cref="EscapePlans.Sync"/> moves that one, never this).
        /// </summary>
        public int knownHolderId = -1;
        /// <summary>A night link: the hour of day the warden was told (a blackout or a night put off later moves the true night, never this); -1 while the night is not known.</summary>
        public int knownHour = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref role, "role");
            Scribe_Values.Look(ref pawnId, "pawn", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref known, "known");
            Scribe_Values.Look(ref handled, "handled");
            Scribe_Values.Look(ref turned, "turned");
            Scribe_Values.Look(ref thingId, "thing", -1);
            Scribe_Values.Look(ref shown, "shown");
            Scribe_Values.Look(ref knownHolderId, "knownHolder", -1);
            Scribe_Values.Look(ref knownHour, "knownHour", -1);
        }

        public bool Crew => role is PlanRole.Leader or PlanRole.Member;

        /// <summary>
        /// A tool link with no tool behind it (no thing id, no true holder): what the warden was told of a tool or its holder, kept as they know it after the tool left the crew's hands unseen, or after a later find on
        /// someone else (<see cref="EscapePlans.Reveal"/>). Not the plan's tool (<see cref="EscapePlan.Find"/> skips it), still read by the known crew, Intel's rows, the letter and the look targets.
        /// </summary>
        public bool Ghost => role == PlanRole.Tool && thingId < 0;

        public string Describe() => $"{role}:{pawnId}:{name}:{known}{handled}{turned}:{thingId}:{shown}:{knownHolderId}:{knownHour}";
    }

    /// <summary>The plan behind an <see cref="StoryArc"/> (<see cref="StoryArc.plan"/>, one deep object, null for every other arc): the links, the way, how ready it is, the night and the warden's answer.</summary>
    public class EscapePlan : IExposable
    {
        public List<PlanLink> links = new List<PlanLink>();
        public PlanWay way;
        /// <summary>Where the crew comes out (the tunnel's exit, the stretch of fence, by the pad, outside the gate).</summary>
        public IntVec3 wayCell = IntVec3.Invalid;
        /// <summary>0..1; at 1 (with a tool and a way) the night is chosen.</summary>
        public float readiness;
        public int nightTick = -1;
        public PlanNight night;
        public PlanAnswer answer;
        /// <summary>The one letter has gone out.</summary>
        public bool asked;
        /// <summary>
        /// Intel's "more of it suspected" marker, read at the daily pass (never live), cleared by a find that leaves nothing unknown. A part the plan has not got yet counts as unknown (<see cref="EscapePlans.HasUnknown"/>),
        /// so a part it gains out of sight (the night chosen, a new tool) never lights it up.
        /// </summary>
        public bool moreShown;
        /// <summary>The guard posts "let it run" put up (thing ids): taken down when the plan is over.</summary>
        public List<int> posts = new List<int>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref links, "links", LookMode.Deep);
            Scribe_Values.Look(ref way, "way");
            Scribe_Values.Look(ref wayCell, "wayCell", IntVec3.Invalid);
            Scribe_Values.Look(ref readiness, "readiness");
            Scribe_Values.Look(ref nightTick, "nightTick", -1);
            Scribe_Values.Look(ref night, "night");
            Scribe_Values.Look(ref answer, "answer");
            Scribe_Values.Look(ref asked, "asked");
            Scribe_Values.Look(ref moreShown, "moreShown");
            Scribe_Collections.Look(ref posts, "posts", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                links ??= new List<PlanLink>();
                links.RemoveAll(l => l == null);
                posts ??= new List<int>();
            }
        }

        public PlanLink Find(PlanRole role) => links.Find(l => l.role == role && !l.handled && !l.Ghost);

        public string Describe() =>
            $"{way}:{wayCell}:{readiness:F3}:{nightTick}:{night}:{answer}:{asked}{moreShown}:{string.Join("+", posts)}:[{string.Join(",", links.Select(l => l.Describe()))}]";
    }

    /// <summary>One line of what Intel shows of a plan: a known link, or the one marker for what is still hidden.</summary>
    public readonly struct PlanPart
    {
        public readonly string text;
        public readonly PlanLink link;

        public PlanPart(string text, PlanLink link)
        {
            this.text = text;
            this.link = link;
        }

        public bool Known => link != null;
    }

    /// <summary>
    /// The break (gap analysis 7, item 4): a planned escape uncovered piece by piece. Now and then (<see cref="StartChancePerDay"/>, at most one plan at a time, never in the first <see cref="FirstDays"/> days) a tense inmate with
    /// a long sentence and an escape artist's name, a gang or a ring behind them starts a plan with up to two others: a background story arc (<see cref="RPBreakDefOf.RP_Arc_Break"/>, the leader its cast) carrying an
    /// <see cref="EscapePlan"/>. Its parts are what the game already has: a tool a member holds, a corrupt officer on the crew's payroll (the inside man), a way (a tunnel from a member's cell, the fence at an unlit
    /// stretch, the shuttle ramp, the gate in a laundered uniform) and, once it is ready, a night (a blackout, the handover hour, a storm, the darkest stretch). Readiness grows daily with the parts it has.
    /// Every link is hidden until found the way the ring's links are: a frisk or a search turns up the tool, the tool count's shortage says where it went, a snitch's tip or a tasked informant, a phone tap, the
    /// psychologist, a unit officer and the turned inside man tell one link each. Once one is known, one letter: foil it now, let it run (a guard post on the known way for the night), or turn the inside man.
    /// On the night the crew go for the way through the escape machinery (<see cref="CountSystem.StartEscaping"/>: a break nobody sees is the count's to find), unless a post on the way is manned: then they are
    /// caught in the act, restrained and charged with an officer's evidence. Arc-driven: the hourly pass is a few compares, the daily one O(crew + staff); nothing per tick.
    /// </summary>
    public static class EscapePlans
    {
        public const int FirstDays = 7, MaxDays = 30, NightLeadHours = 12, CrewOthers = 2;
        public const float StartChancePerDay = 0.03f, MinTension = 60f, MinDaysLeft = 20f;
        /// <summary>Readiness a day: this, and this much more for each part it has (a tool, the inside man, a way); short of a tool or a way it stops at <see cref="CapShort"/>.</summary>
        public const float ReadyBase = 0.04f, ReadyPerPart = 0.04f, CapShort = 0.95f;
        public const float TipChance = 0.5f, TapChance = 0.6f, CellFindChance = 0.85f, HandoverShare = 0.35f;
        /// <summary>The hours the nights fall at: the darkest stretch, under a storm, and the guards' handover (the day shift's end).</summary>
        public const int DarkHour = 2, StormHour = 1;
        public static int HandoverHour => ShiftSystem.DayShiftEnd;
        /// <summary>A post this near the way catches the crew when it is manned.</summary>
        public const float PostRadius = GuardPosts.NearRadius + 1f;
        /// <summary>The hours a post on the way is manned when the night is not known: the small hours.</summary>
        public const int SmallFrom = 23, SmallTo = 5;

        /// <summary>For a check: the inmates and staff a plan may take in (crew, inside man; null: everyone).</summary>
        internal static System.Func<Pawn, bool> Pool;
        /// <summary>For a check: the start's dice, and every other roll of the plan (a tip, a tap, a session, a cell search), decided.</summary>
        internal static bool? StartRoll, ForceRoll;
        /// <summary>For a check: plans start of themselves under <see cref="AutoTest.Active"/> (off: a test run starts none unless a check makes one).</summary>
        internal static bool TestOn;
        /// <summary>For a check: the escape's start is refused for everyone who goes (what a mutant or a tutorial run does), so that the pawn must be put back.</summary>
        internal static bool RefuseEscape;
        /// <summary>For a check: the id of an arc kept exactly as it is (the saveload round trip's seeded plan: a daily or hourly pass in the reload's ticked checks - a blackout re-timing its night - would change what the snapshot compares); -1 none.</summary>
        internal static int HoldArc = -1;
        /// <summary>For a check: the crew come out at exactly this cell (the way's), not at a random one within two of it.</summary>
        internal static IntVec3? ArriveAt;

        private static int Now => Find.TickManager.TicksGame;

        private static bool Roll(float chance) => ForceRoll ?? Rand.Chance(chance);

        private static bool InPool(Pawn p) => Pool == null || Pool(p);

        // ---- lookups ---------------------------------------------------------------------------------------------

        /// <summary>The plan running now (there is one at most), or null.</summary>
        public static StoryArc Current()
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null || RPBreakDefOf.RP_Arc_Break == null) return null;
            for (int i = 0; i < arcs.Count; i++)
                if (!arcs[i].done && arcs[i].plan != null && arcs[i].def == RPBreakDefOf.RP_Arc_Break) return arcs[i];
            return null;
        }

        public static bool Visible(StoryArc arc) => arc.plan.links.Exists(l => l.known);

        /// <summary>The plan Intel shows: the running one, once something of it is known.</summary>
        public static StoryArc VisiblePlan() => Current() is { } arc && Visible(arc) ? arc : null;

        /// <summary>
        /// Something of the plan the warden has not found: a link nobody knows, or a part it does not have yet (a tool, the inside man, a way, the night). A part the plan has not got counts as hidden, so that
        /// gaining one out of sight (a night chosen, a new tool, an officer paid) never turns the marker on: it was on already.
        /// </summary>
        public static bool HasUnknown(StoryArc arc) => arc.plan.links.Exists(l => !l.known && !l.handled) || Lacks(arc.plan);

        // (A tool the warden was told of whose tool is gone unseen still counts as one the plan has: its going is not theirs to see.)
        private static bool Lacks(EscapePlan plan) =>
            !plan.links.Exists(l => l.role == PlanRole.Tool && !l.handled) || plan.Find(PlanRole.Officer) == null || plan.Find(PlanRole.Way) == null || plan.Find(PlanRole.Night) == null;

        public static bool MoreSuspected(StoryArc arc) => arc.plan.moreShown;

        /// <summary>The crew link of this inmate (still in it), or null.</summary>
        public static PlanLink CrewLink(StoryArc arc, Pawn p) => p == null ? null : arc.plan.links.Find(l => l.Crew && !l.handled && l.pawnId == p.thingIDNumber);

        private static Pawn InmateOf(Map map, int id) => map == null ? null : CourtUtil.FindInmate(map, id);

        private static Pawn StaffOf(Map map, int id) => map == null ? null : CourtUtil.FindStaff(map, id);

        /// <summary>The pawn a line or a letter may point at for a link: a crew member or the tool's holder (an inmate), the officer (staff).</summary>
        private static Pawn PawnOf(Map map, PlanLink l) => l.role == PlanRole.Officer ? StaffOf(map, l.pawnId) : l.role is PlanRole.Way or PlanRole.Night ? null : InmateOf(map, l.pawnId);

        public static LookTargets LookAt(Pawn p) => p is { Spawned: true } ? new LookTargets(p) : LookTargets.Invalid;

        /// <summary>The crew still in it, on the map (leader first).</summary>
        public static List<Pawn> Crew(StoryArc arc, Map map)
        {
            var list = new List<Pawn>();
            foreach (PlanLink l in arc.plan.links)
                if (l.Crew && !l.handled && InmateOf(map, l.pawnId) is { } p) list.Add(p);
            return list;
        }

        /// <summary>The crew the warden knows of: a known leader or member, and whoever they learned holds the tool (<see cref="PlanLink.knownHolderId"/>: not the true holder, who may have changed unseen).</summary>
        public static List<Pawn> KnownCrew(StoryArc arc, Map map)
        {
            var list = new List<Pawn>();
            foreach (PlanLink l in arc.plan.links)
            {
                if (!l.known || l.role is not (PlanRole.Leader or PlanRole.Member or PlanRole.Tool) || l.role != PlanRole.Tool && l.handled) continue;
                if (KnownPawn(map, l) is { } p && InmateUtility.IsActiveInmate(p) && !list.Contains(p)) list.Add(p);
            }
            return list;
        }

        /// <summary>The pawn a known link names to the warden: a crew member, the officer, the holder of a tool they learned of (a tool known from the count alone names nobody), or null.</summary>
        private static Pawn KnownPawn(Map map, PlanLink l) => l.role == PlanRole.Tool ? InmateOf(map, l.knownHolderId) : PawnOf(map, l);

        /// <summary>Out of the plan's reach: on death row, in protective custody or in supermax. Holds for the whole plan (the start, the daily pass, the night).</summary>
        private static bool OutOfReach(CompInmate c) => DeathRowSystem.Ext(c) != null || DebtSystem.InCustody(c) || c.security is { escortOnly: true };

        // ---- who may lead, who comes along -----------------------------------------------------------------------

        public static float DaysLeft(CompInmate c) => c.IsLifer ? float.MaxValue : c.TermDays - c.DaysServed;

        /// <summary>
        /// May lead a plan: active and not out of reach (death row, custody, supermax, on the way out), tension <see cref="MinTension"/> or more, <see cref="MinDaysLeft"/> days or more still to serve, and an escape
        /// artist's name, a gang or a ring behind them.
        /// </summary>
        public static bool Leads(Pawn p, CompInmate c)
        {
            if (c == null || !c.activeFlag || !InmateUtility.IsActiveInmate(p) || c.tension < MinTension || DaysLeft(c) < MinDaysLeft) return false;
            if (OutOfReach(c) || ShuttleSystem.OnTheWayOut(p)) return false;
            return c.HasReputation(ReputationDefOf.RP_EscapeArtist) || c.gang != null || RingSystem.ArcOf(p) != null;
        }

        /// <summary>Who comes along: the leader's gang, their ring, their cellmates, and tense inmates of their level (never a known or suspected informant, nor anyone out of reach); the two best, ties at random.</summary>
        public static List<Pawn> PickCrew(Map map, Pawn leader)
        {
            CompInmate lc = leader.Inmate();
            StoryArc ring = RingSystem.ArcOf(leader);
            var scored = new List<(Pawn p, float w)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == leader || !InPool(p) || p.Inmate() is not { activeFlag: true } c || !InmateUtility.IsActiveInmate(p)) continue;
                if (c.HasReputation(ReputationDefOf.RP_Snitch) || c.snitchExposed || OutOfReach(c) || ShuttleSystem.OnTheWayOut(p)) continue;
                float w = 0f;
                if (lc.gang != null && c.gang == lc.gang) w += 3f;
                if (ring != null && RingSystem.InRing(ring, p)) w += 3f;
                if (Cellmates.Share(leader, p)) w += 2f;
                if (c.security == lc.security && c.tension >= 45f) w += 1f;
                if (w <= 0f) continue;
                if (c.HasReputation(ReputationDefOf.RP_EscapeArtist)) w += 1f;
                scored.Add((p, w + Rand.Value * 0.5f));
            }
            return scored.OrderByDescending(x => x.w).Take(CrewOthers).Select(x => x.p).ToList();
        }

        // ---- the way -------------------------------------------------------------------------------------------

        /// <summary>An outdoor cell outside inmate space, from which the map's edge can be reached, near <paramref name="from"/> (and in the dark at night when <paramref name="unlit"/>).</summary>
        public static bool FindOutside(Map map, IntVec3 from, bool unlit, out IntVec3 cell)
        {
            PrisonMap pm = PrisonMap.Of(map);
            cell = IntVec3.Invalid;
            if (pm == null) return false;
            return CellFinder.TryFindRandomCellNear(from, map, 40, c =>
                c.Standable(map) && !c.Roofed(map) && pm.zones.ZoneAt(c) is not { inmateAccess: true } && c.GetRoom(map) is { } r && (r.TouchesMapEdge || r.PsychologicallyOutdoors)
                && (!unlit || map.glowGrid.GroundGlowAt(c, ignoreSky: true) < TensionUtility.DarkGlow) && map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.NoPassClosedDoors)), out cell);
        }

        /// <summary>
        /// The way the prison offers this crew, weighted: a tunnel from a member's cell, the fence at an unlit stretch, the shuttle ramp, the gate in a laundered uniform. False when it offers none. <paramref name="plan"/>: the
        /// plan looking for a new way (a member whose tunnel for it was found and filled does not dig it again).
        /// </summary>
        public static bool PickWay(Map map, Pawn leader, List<Pawn> crew, out PlanWay way, out IntVec3 cell, out int owner, EscapePlan plan = null)
        {
            List<(PlanWay way, IntVec3 cell, int owner, float w)> options = WayOptions(map, leader, crew, plan);
            (way, cell, owner) = (PlanWay.Fence, IntVec3.Invalid, -1);
            if (!options.TryRandomElementByWeight(o => o.w, out var pick)) return false;
            (way, cell, owner) = (pick.way, pick.cell, pick.owner);
            return true;
        }

        /// <summary>A tunnel of the plan's from this member was found and filled (a handled way link names its tunnel's owner; every other way's is -1).</summary>
        public static bool TunnelFilled(EscapePlan plan, Pawn p) => plan != null && plan.links.Exists(l => l.role == PlanRole.Way && l.handled && l.pawnId == p.thingIDNumber);

        /// <summary>The ways <see cref="PickWay"/> chooses among, with their weights.</summary>
        internal static List<(PlanWay way, IntVec3 cell, int owner, float w)> WayOptions(Map map, Pawn leader, List<Pawn> crew, EscapePlan plan = null)
        {
            var options = new List<(PlanWay way, IntVec3 cell, int owner, float w)>();
            foreach (Pawn p in crew)
            {
                if (TunnelFilled(plan, p) || InmateUtility.CellOf(p) is not { IsPrisonCell: true } || !TunnelSystem.TryFindExit(p, out IntVec3 exit)) continue;
                CompInmate c = p.Inmate();
                options.Add((PlanWay.Tunnel, exit, p.thingIDNumber, c.tunnelProgress > 0f || c.HasReputation(ReputationDefOf.RP_EscapeArtist) ? 2f : 1f));
                break;
            }
            if (FindOutside(map, leader.Position, unlit: true, out IntVec3 fence)) options.Add((PlanWay.Fence, fence, -1, 2f));
            if (ShuttleSystem.Pads(map).FirstOrDefault() is { } pad && FindOutside(map, pad.Position, unlit: false, out IntVec3 ramp)) options.Add((PlanWay.Ramp, ramp, -1, 1f));
            if (PrisonMap.Of(map) is { } pm && pm.zones.Any(RPLaborDefOf.RP_Laundry) && crew.Exists(p => p.Inmate()?.job?.zone == RPLaborDefOf.RP_Laundry)
                && FindOutside(map, leader.Position, unlit: false, out IntVec3 gate))
                options.Add((PlanWay.Uniform, gate, -1, 1f));
            return options;
        }

        public static string WayText(Map map, PlanWay way, IntVec3 cell, int owner) => way switch
        {
            PlanWay.Tunnel => InmateOf(map, owner)?.ownership?.OwnedBed is { } bed && Cellmates.CellAt(map, bed, fresh: true) is { number: > 0 } at
                ? "RP_BreakWay_Tunnel".Translate(at.number).Resolve() : "RP_BreakWay_TunnelNoNumber".Translate().Resolve(),
            PlanWay.Ramp => "RP_BreakWay_Ramp".Translate().Resolve(),
            PlanWay.Uniform => "RP_BreakWay_Uniform".Translate().Resolve(),
            _ => "RP_BreakWay_Fence".Translate(cell.x, cell.z).Resolve(),
        };

        // ---- starting ------------------------------------------------------------------------------------------

        /// <summary>The daily pass: the plan's day, or (with none running) the start's roll.</summary>
        public static void Daily(Map map)
        {
            if (PrisonGame.Instance == null || RPBreakDefOf.RP_Arc_Break == null || map == null) return;
            if (Current() is { } arc) DailyPlan(arc, map);
            else TryStart(map);
        }

        /// <summary>
        /// A plan starts: with none running, not in the first <see cref="FirstDays"/> days, on the day's roll; the tensest inmate who <see cref="Leads"/> and has at least one other to come along and a way out. Through
        /// <see cref="StorySystem.TryStartArc"/>, so the def's days between plans hold. <paramref name="force"/> skips the clock and the dice (not the gates on the leader).
        /// </summary>
        public static StoryArc TryStart(Map map, bool force = false)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || RPBreakDefOf.RP_Arc_Break == null || Current() != null) return null;
            // (Not of itself in an autotest run, unless a check turns it on: a long plan's own break would be an escape its checks never asked for.)
            if (!force && (AutoTest.Active && !TestOn || Now < FirstDays * GenDate.TicksPerDay || !(StartRoll ?? Rand.Chance(StartChancePerDay)))) return null;
            var leaders = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InPool(p) && Leads(p, p.Inmate())) leaders.Add(p);
            foreach (Pawn leader in leaders.OrderByDescending(p => p.Inmate().tension))
            {
                List<Pawn> members = PickCrew(map, leader);
                if (members.Count == 0) continue;
                var crew = new List<Pawn> { leader };
                crew.AddRange(members);
                if (!PickWay(map, leader, crew, out PlanWay way, out IntVec3 cell, out int owner)) continue;
                StoryArc arc = game.story.TryStartArc(RPBreakDefOf.RP_Arc_Break, leader);
                if (arc == null) return null;
                Fill(arc, map, leader, members, way, cell, owner, null, null);
                return arc;
            }
            return null;
        }

        /// <summary>A plan made by hand (the checks, the tour): not through the arc's limits. <paramref name="officer"/> and <paramref name="tool"/> are its inside man and tool; null finds them as a new plan would.</summary>
        internal static StoryArc Make(Map map, Pawn leader, IEnumerable<Pawn> members, PlanWay way, IntVec3 cell, int owner = -1, Pawn officer = null, Thing tool = null)
        {
            PrisonGame game = PrisonGame.Instance;
            var arc = new StoryArc { def = RPBreakDefOf.RP_Arc_Break, id = game.story.nextArcId++, startTick = Now, nextTick = Now };
            arc.pawns.Add(leader);
            game.story.arcs.Add(arc);
            Fill(arc, map, leader, members, way, cell, owner, officer, tool);
            return arc;
        }

        private static void Fill(StoryArc arc, Map map, Pawn leader, IEnumerable<Pawn> members, PlanWay way, IntVec3 cell, int owner, Pawn officer, Thing tool)
        {
            var plan = new EscapePlan { way = way, wayCell = cell };
            arc.plan = plan;
            plan.links.Add(new PlanLink { role = PlanRole.Leader, pawnId = leader.thingIDNumber, name = leader.LabelShort });
            foreach (Pawn m in members) plan.links.Add(new PlanLink { role = PlanRole.Member, pawnId = m.thingIDNumber, name = m.LabelShort });
            plan.links.Add(new PlanLink { role = PlanRole.Way, pawnId = owner, name = WayText(map, way, cell, owner) });
            if (officer != null) plan.links.Add(new PlanLink { role = PlanRole.Officer, pawnId = officer.thingIDNumber, name = officer.LabelShort });
            else SyncOfficer(arc, map);
            if (tool != null && HolderOf(arc, map, tool.thingIDNumber) is { } holder) plan.links.Add(new PlanLink { role = PlanRole.Tool, pawnId = holder.thingIDNumber, name = holder.LabelShort, thingId = tool.thingIDNumber });
            else FindTool(arc, map);
            plan.moreShown = HasUnknown(arc);
        }

        // ---- the parts -----------------------------------------------------------------------------------------

        /// <summary>The crew member who holds this thing (pockets, equipment or stash), or null.</summary>
        private static Pawn HolderOf(StoryArc arc, Map map, int thingId)
        {
            foreach (Pawn p in Crew(arc, map))
            {
                if (p.inventory != null)
                    foreach (Thing t in p.inventory.innerContainer)
                        if (t.thingIDNumber == thingId) return p;
                if (p.equipment?.Primary is { } eq && eq.thingIDNumber == thingId) return p;
                if (p.Inmate() is { } c)
                    foreach (Thing t in c.stash)
                        if (t.thingIDNumber == thingId) return p;
            }
            return null;
        }

        private static bool IsTool(Thing t) => ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Tool };

        /// <summary>A tool the crew has (one stolen at work and traced first): the plan's tool link, unknown. Nothing when they have none.</summary>
        private static void FindTool(StoryArc arc, Map map)
        {
            Thing best = null;
            Pawn holder = null;
            foreach (Pawn p in Crew(arc, map))
            {
                IEnumerable<Thing> things = (p.inventory?.innerContainer ?? Enumerable.Empty<Thing>()).Concat(p.Inmate()?.stash ?? Enumerable.Empty<Thing>());
                foreach (Thing t in things)
                {
                    if (!IsTool(t) || best != null && (ToolCount.Traced(best) || !ToolCount.Traced(t))) continue;
                    (best, holder) = (t, p);
                }
            }
            if (best != null) arc.plan.links.Add(new PlanLink { role = PlanRole.Tool, pawnId = holder.thingIDNumber, name = holder.LabelShort, thingId = best.thingIDNumber });
        }

        /// <summary>The inside man: a corrupt officer (not one working for the warden) whose patron is one of the crew or their gang. A link that no longer holds is taken off (known: kept as handled), a turned one marked.</summary>
        private static void SyncOfficer(StoryArc arc, Map map)
        {
            EscapePlan plan = arc.plan;
            for (int i = plan.links.Count - 1; i >= 0; i--)
            {
                PlanLink l = plan.links[i];
                if (l.role != PlanRole.Officer || l.handled) continue;
                CompStaff sc = StaffOf(map, l.pawnId)?.GetComp<CompStaff>();
                if (sc != null && sc.doubleAgent) l.turned = true;
                else if (sc == null || !sc.corrupt || !Pays(arc, map, sc))
                {
                    if (l.known) l.handled = true;
                    else plan.links.RemoveAt(i);
                }
            }
            if (plan.Find(PlanRole.Officer) != null) return;
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (InPool(s) && s.GetComp<CompStaff>() is { corrupt: true, doubleAgent: false } sc && Pays(arc, map, sc) && !plan.links.Exists(l => l.role == PlanRole.Officer && l.pawnId == s.thingIDNumber))
                {
                    plan.links.Add(new PlanLink { role = PlanRole.Officer, pawnId = s.thingIDNumber, name = s.LabelShort });
                    return;
                }
        }

        private static bool Pays(StoryArc arc, Map map, CompStaff sc)
        {
            foreach (Pawn p in Crew(arc, map))
                if (sc.patron == p || CorruptionSystem.IsClient(sc, p, p.Inmate())) return true;
            return false;
        }

        /// <summary>
        /// The links read from the game: the leader out of reach (death row, protective custody, supermax) ends the plan as coming to nothing; a member gone, or out of reach, leaves (known: kept as handled; one carried to a
        /// bed or the hole is still in it, and <see cref="CanGo"/> decides tonight); the tool follows the crew's hands (gone from them, it is out); the inside man is checked.
        /// </summary>
        public static void Sync(StoryArc arc, Map map)
        {
            EscapePlan plan = arc.plan;
            PlanLink head = plan.links.Find(l => l.role == PlanRole.Leader && !l.handled);
            if (head != null && InmateOf(map, head.pawnId) is { } lp && lp.Inmate() is { } lc && OutOfReach(lc))
            {
                FallApart(arc, map);
                return;
            }
            for (int i = plan.links.Count - 1; i >= 0; i--)
            {
                PlanLink l = plan.links[i];
                if (l.handled || l.role != PlanRole.Member) continue;
                if (InmateOf(map, l.pawnId) is { } p && p.Inmate() is { } c && !OutOfReach(c)) continue;
                if (l.known) l.handled = true;
                else plan.links.RemoveAt(i);
            }
            if (plan.Find(PlanRole.Tool) is { } tool)
            {
                // (Passed to another of the crew: theirs now, whoever the warden thinks has it - what they know is the link's known holder, which stays.)
                if (HolderOf(arc, map, tool.thingId) is { } holder) (tool.pawnId, tool.name) = (holder.thingIDNumber, holder.LabelShort);
                else if (tool.known) ToolOut(tool);
                else plan.links.Remove(tool);
            }
            SyncOfficer(arc, map);
        }

        /// <summary>
        /// The tool left the plan's hands and its link is known. The warden sees it go only when it was in the hands they knew of (<see cref="PlanLink.knownHolderId"/> is the true holder): the row goes out. Otherwise
        /// the true holder is dropped silently and the row stays as the warden knows it, with no tool behind it (<see cref="PlanLink.Ghost"/>): the row going out would tell them the tool had changed hands.
        /// </summary>
        internal static void ToolOut(PlanLink tool)
        {
            if (tool.knownHolderId == tool.pawnId)
            {
                tool.handled = true;
                return;
            }
            (tool.pawnId, tool.thingId, tool.name) = (-1, -1, null);
        }

        /// <summary>The parts the plan has now, of the three it gathers (a tool, the inside man, a way): what readiness grows with.</summary>
        public static int Parts(EscapePlan plan) => (plan.Find(PlanRole.Tool) != null ? 1 : 0) + (plan.Find(PlanRole.Officer) != null ? 1 : 0) + (plan.Find(PlanRole.Way) != null ? 1 : 0);

        public static bool Equipped(EscapePlan plan) => plan.Find(PlanRole.Tool) != null && plan.Find(PlanRole.Way) != null;

        // ---- the daily and hourly passes -----------------------------------------------------------------------

        /// <summary>
        /// The plan's day: its links read again, a tool or a way found anew when the old one is out, readiness grown (and, at 1, the night chosen), the turned inside man tells one more link, and the marker is read.
        /// A plan still not out after <see cref="MaxDays"/> days falls apart.
        /// </summary>
        public static void DailyPlan(StoryArc arc, Map map)
        {
            if (arc.done || arc.plan == null || arc.id == HoldArc) return;
            EscapePlan plan = arc.plan;
            Sync(arc, map);
            if (arc.done) return;
            if (Now - arc.startTick > MaxDays * GenDate.TicksPerDay)
            {
                FallApart(arc, map);
                return;
            }
            if (plan.Find(PlanRole.Tool) == null) FindTool(arc, map);
            if (plan.Find(PlanRole.Way) == null && arc.A is { } leader && PickWay(map, leader, Crew(arc, map), out PlanWay way, out IntVec3 cell, out int owner, plan))
            {
                (plan.way, plan.wayCell) = (way, cell);
                plan.links.Add(new PlanLink { role = PlanRole.Way, pawnId = owner, name = WayText(map, way, cell, owner) });
            }
            if (plan.nightTick < 0)
            {
                plan.readiness = Mathf.Min(Equipped(plan) ? 1f : CapShort, plan.readiness + ReadyBase + ReadyPerPart * Parts(plan));
                if (plan.readiness >= 1f) ChooseNight(arc, map);
            }
            // The turned inside man tells a link a day: his own first (one internal affairs turned, not the plan, is not known yet as the plan's: the warden learns that before anything he tells of it).
            if (plan.links.Find(l => l.role == PlanRole.Officer && l.turned && !l.handled) is { } source)
            {
                if (!source.known) Reveal(arc, source, "source", map);
                else RevealNext(arc, map, "source", SourceOrder);
            }
            plan.moreShown = HasUnknown(arc);
        }

        /// <summary>The arc's hour: a blackout while the plan is ready is the night, now; the night come, the break.</summary>
        public static void Hourly(StoryArc arc, Map map)
        {
            EscapePlan plan = arc.plan;
            if (arc.done || plan == null || plan.nightTick < 0 || arc.id == HoldArc) return;
            // (A blackout while the night is set is the night, now; so is each hour of one that outlasts a night nobody could go on, which moved a day on: they go the first hour one of them can.)
            if (PrisonGame.Instance.BlackoutActive && (plan.night != PlanNight.Blackout || plan.nightTick > Now))
            {
                plan.night = PlanNight.Blackout;
                plan.nightTick = Now;
                // (A night the warden knows stays as they know it: the plan does not know they know, and the posts set for it are now the wrong ones.)
                if (plan.Find(PlanRole.Night) is { known: false } n) n.name = NightText(map, plan);
            }
            if (Now >= plan.nightTick) Break(arc, map);
        }

        private static bool Storming(Map map) => map.weatherManager?.curWeather is { } w && w.rainRate >= 0.5f;

        /// <summary>The first tick at the hour (by the map's clock) at least <paramref name="lead"/> ticks from now.</summary>
        public static int NextAt(Map map, int hour, int lead)
        {
            int now = Now, start = now - GenLocalDate.DayTick(map) % GenDate.TicksPerHour;
            int t = start + ((hour - GenLocalDate.HourOfDay(map)) % 24 + 24) % 24 * GenDate.TicksPerHour;
            while (t < now + lead) t += GenDate.TicksPerDay;
            return t;
        }

        /// <summary>The map's hour at a tick from now on.</summary>
        public static int HourAt(Map map, int tick)
        {
            int start = Now - GenLocalDate.DayTick(map) % GenDate.TicksPerHour;
            return (GenLocalDate.HourOfDay(map) + Mathf.Max(0, tick - start) / GenDate.TicksPerHour) % 24;
        }

        /// <summary>The plan is ready: the night is chosen (a blackout now; else a storm's small hours, the handover hour or the darkest stretch, at least <see cref="NightLeadHours"/> hours off), an unknown link.</summary>
        public static void ChooseNight(StoryArc arc, Map map)
        {
            EscapePlan plan = arc.plan;
            if (plan.nightTick >= 0) return;
            int lead = NightLeadHours * GenDate.TicksPerHour;
            if (PrisonGame.Instance.BlackoutActive) (plan.night, plan.nightTick) = (PlanNight.Blackout, Now);
            else if (Storming(map)) (plan.night, plan.nightTick) = (PlanNight.Storm, NextAt(map, StormHour, lead));
            else if (Rand.Chance(HandoverShare)) (plan.night, plan.nightTick) = (PlanNight.Handover, NextAt(map, HandoverHour, lead));
            else (plan.night, plan.nightTick) = (PlanNight.Dark, NextAt(map, DarkHour, lead));
            plan.links.RemoveAll(l => l.role == PlanRole.Night && !l.known);
            plan.links.Add(new PlanLink { role = PlanRole.Night, name = NightText(map, plan) });
        }

        public static string NightText(Map map, EscapePlan plan)
        {
            int day = ChronicleEntry.DayOf(plan.nightTick) + 1;
            string at = HourAt(map, plan.nightTick).ToString("00") + ":00";
            return ("RP_BreakNight_" + plan.night).Translate(day, at).Resolve();
        }

        // ---- the night -----------------------------------------------------------------------------------------

        /// <summary>A crew member who can go tonight: on the map, on their feet and in their right mind, not restrained, not in the hole, not held in a stand-off, not on the way out, not out of reach (<see cref="OutOfReach"/>: a reclassification since the daily pass counts).</summary>
        public static bool CanGo(Pawn p) =>
            p is { Spawned: true, Dead: false, Downed: false } && !p.InMentalState && p.Inmate() is { } c && InmateUtility.IsActiveInmate(p) && c.forcedActivity != RPSecurityDefOf.RP_Solitary
            && !p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained) && !HostageCrisis.IsHeld(p) && !ShuttleSystem.OnTheWayOut(p) && !OutOfReach(c);

        /// <summary>The guard standing at a post on the way (within <see cref="PostRadius"/>), awake and on their feet, or null.</summary>
        public static Pawn Catcher(Map map, EscapePlan plan)
        {
            if (!plan.wayCell.IsValid) return null;
            List<Thing> posts = map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
            for (int i = 0; i < posts.Count; i++)
            {
                if (!posts[i].Position.InHorDistOf(plan.wayCell, PostRadius) || posts[i].TryGetComp<CompGuardPost>() is not { } post) continue;
                if (post.Stander is { Downed: false } g && g.Position == posts[i].Position && g.Awake()) return g;
            }
            return null;
        }

        /// <summary>
        /// The night: the crew who can go come out at the way (as a tunnel's breakout puts them outside). A manned post on it catches them all (<see cref="CaughtInTheAct"/>); else each makes a break for it through the
        /// escape machinery, seen or not (a break nobody sees is the count's to find). With nobody able to go, the night moves a day on; with no way left, the night is off (<see cref="PutOff"/>).
        /// </summary>
        public static void Break(StoryArc arc, Map map)
        {
            EscapePlan plan = arc.plan;
            // (The way is gone - its tunnel found and filled since the night was set - and no other found yet: no night through a dead way. They wait for a new one.)
            if (plan.Find(PlanRole.Way) == null)
            {
                PutOff(plan);
                return;
            }
            var goers = Crew(arc, map).Where(CanGo).ToList();
            if (goers.Count == 0)
            {
                plan.nightTick += GenDate.TicksPerDay;
                // (The night a tip would tell is the one now set; a night the warden knows stays as they know it.)
                if (plan.Find(PlanRole.Night) is { known: false } n) n.name = NightText(map, plan);
                return;
            }
            if (!plan.wayCell.IsValid || !plan.wayCell.InBounds(map))
            {
                FallApart(arc, map);
                return;
            }
            // (No letter for a plan going out now: what the night shows, the break's own letter says.)
            plan.asked = true;
            if (Catcher(map, plan) is { } catcher) CaughtInTheAct(arc, map, goers, catcher);
            else Out(arc, map, goers);
            End(arc, map);
        }

        /// <summary>
        /// The night is off: not set, readiness back under 1 (<see cref="CapShort"/>), so the next daily pass that has a way chooses another. The night nobody knew goes; one the warden knew stays in Intel as out
        /// (handled): the post's hours must not read the next night through it.
        /// </summary>
        public static void PutOff(EscapePlan plan)
        {
            plan.nightTick = -1;
            plan.readiness = Mathf.Min(plan.readiness, CapShort);
            for (int i = plan.links.Count - 1; i >= 0; i--)
            {
                PlanLink l = plan.links[i];
                if (l.role != PlanRole.Night || l.handled) continue;
                if (l.known) l.handled = true;
                else plan.links.RemoveAt(i);
            }
        }

        private static IntVec3 Arrive(Pawn p, Map map, IntVec3 at)
        {
            IntVec3 from = p.Position;
            p.jobs.StopAll();
            p.DeSpawn();
            GenSpawn.Spawn(p, ArriveAt ?? CellFinder.RandomClosewalkCellNear(at, map, 2), map);
            return from;
        }

        /// <summary>Taken back where they set out from (a spot beside it when that is taken).</summary>
        private static void PutBack(Pawn p, IntVec3 from, Map map)
        {
            if (!p.Spawned || !from.IsValid || !from.InBounds(map)) return;
            p.DeSpawn();
            GenSpawn.Spawn(p, from.Standable(map) ? from : CellFinder.RandomClosewalkCellNear(from, map, 2), map);
        }

        /// <summary>
        /// A post on the way, manned: every one of them is restrained where they come out, an escape attempt on their file, and a case on the adjudication sheet with the officer's evidence; then they are taken back in,
        /// to where they set out from (left lying outside the wall, a restraint that wore off before anyone carried them in would be a breach walked through at once).
        /// </summary>
        internal static void CaughtInTheAct(StoryArc arc, Map map, List<Pawn> goers, Pawn catcher)
        {
            int now = Now;
            PrisonGame game = PrisonGame.Instance;
            foreach (Pawn p in goers)
            {
                IntVec3 from = Arrive(p, map, arc.plan.wayCell);
                CompInmate c = p.Inmate();
                c.AddRecord(new MisconductRecord { def = RPShuttleDefOf.RP_EscapeAttempt, tick = now, cause = c.mainCause, seen = true });
                PunishmentUtility.ResolveSubdue(catcher, p, alwaysSucceeds: true);
                // (The case reads what watched them where they were caught - the camera over the way - so it is queued before they are taken back.)
                if (Adjudication.Queue(p, c, RPShuttleDefOf.RP_EscapeAttempt, evidence: true))
                {
                    AdjudicationCase k = game.adjudication.cases[game.adjudication.cases.Count - 1];
                    (k.officer, k.officerName) = (true, catcher.LabelShort);
                }
                PutBack(p, from, map);
                if (CrewLink(arc, p) is { } l && !l.known) (l.known, l.shown) = (true, DefaultShown(map, arc.plan, l));
            }
            string names = string.Join(", ", goers.Select(p => p.LabelShort));
            string way = WayText(map, arc.plan.way, arc.plan.wayCell, arc.plan.Find(PlanRole.Way)?.pawnId ?? -1);
            var look = new List<Pawn>(goers) { catcher };
            Find.LetterStack.ReceiveLetter("RP_BreakCaughtLabel".Translate(), "RP_BreakCaughtText".Translate(names, way, catcher.LabelShort), LetterDefOf.PositiveEvent, new LookTargets(look));
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 3, "RP_Chr_BreakCaught".Translate(names, catcher.LabelShort), look.ToArray());
        }

        /// <summary>No post on the way: each makes a break for it from there. Those something saw are named in a letter; the rest are nobody's news until the count.</summary>
        private static void Out(StoryArc arc, Map map, List<Pawn> goers)
        {
            EscapeWay how = arc.plan.way == PlanWay.Tunnel ? EscapeWay.Tunnel : EscapeWay.Walked;
            var seen = new List<Pawn>();
            foreach (Pawn p in goers)
            {
                IntVec3 from = Arrive(p, map, arc.plan.wayCell);
                // (A start the game refuses leaves them no escape on file: back where they set out from, not outside the wall as an ordinary prisoner.)
                if (RefuseEscape || !CountSystem.StartEscaping(p, "RP_BreakReason".Translate(), how, goers.Count, from))
                {
                    PutBack(p, from, map);
                    continue;
                }
                if (!CountSystem.IsHidden(p)) seen.Add(p);
            }
            if (seen.Count == 0) return;
            string names = string.Join(", ", seen.Select(p => p.LabelShort));
            Find.LetterStack.ReceiveLetter("RP_BreakSeenLabel".Translate(), "RP_BreakSeenText".Translate(names), LetterDefOf.ThreatBig, new LookTargets(seen));
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_BreakSeen".Translate(names), seen.ToArray());
        }

        /// <summary>The plan is over: done, and the posts it had put up taken down.</summary>
        public static void End(StoryArc arc, Map map)
        {
            arc.done = true;
            EscapePlan plan = arc.plan;
            if (plan == null || plan.posts.Count == 0 || map == null) return;
            foreach (Thing post in map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost).Where(t => plan.posts.Contains(t.thingIDNumber)).ToList())
                if (!post.Destroyed) post.Destroy();
            plan.posts.Clear();
        }

        /// <summary>The plan comes to nothing (its days run out, its leader gone): said once if the warden knew of it, naming nothing they did not know.</summary>
        public static void FallApart(StoryArc arc, Map map)
        {
            End(arc, map ?? arc.A?.MapHeld ?? Find.AnyPlayerHomeMap);
            if (arc.plan == null || !Visible(arc)) return;
            PlanLink leader = arc.plan.links.Find(l => l.role == PlanRole.Leader);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, leader is { known: true } ? "RP_Chr_BreakEndedLeader".Translate(leader.name) : "RP_Chr_BreakEnded".Translate());
        }

        // ---- what the warden learns ----------------------------------------------------------------------------

        private static readonly PlanRole[] TipOrder = { PlanRole.Way, PlanRole.Night, PlanRole.Member, PlanRole.Leader, PlanRole.Tool, PlanRole.Officer };
        private static readonly PlanRole[] TapOrder = { PlanRole.Officer, PlanRole.Night, PlanRole.Way, PlanRole.Member, PlanRole.Leader, PlanRole.Tool };
        private static readonly PlanRole[] SessionOrder = { PlanRole.Leader, PlanRole.Member, PlanRole.Way, PlanRole.Night, PlanRole.Tool, PlanRole.Officer };
        private static readonly PlanRole[] SourceOrder = { PlanRole.Leader, PlanRole.Member, PlanRole.Way, PlanRole.Night, PlanRole.Tool };

        private static string How(string how) => ("RP_BreakHow_" + how).Translate();

        /// <summary>What the warden knows of a link found by being told (or seen): its own words, naming that link only.</summary>
        public static string DefaultShown(Map map, EscapePlan plan, PlanLink l) => l.role switch
        {
            PlanRole.Leader => "RP_BreakLine_Leader".Translate(l.name).Resolve(),
            PlanRole.Member => "RP_BreakLine_Member".Translate(l.name).Resolve(),
            PlanRole.Tool => "RP_BreakLine_Tool".Translate(l.name).Resolve(),
            PlanRole.Officer => "RP_BreakLine_Officer".Translate(l.name).Resolve(),
            PlanRole.Way => "RP_BreakLine_Way".Translate(l.name).Resolve(),
            _ => "RP_BreakLine_Night".Translate(l.name).Resolve(),
        };

        public static string LinkLine(PlanLink l) => l.shown ?? "?";

        /// <summary>
        /// The warden learns this link (once; a tool known only from the count, or whose known holder is not the one it is found on, is learned again when it is found on someone, which names them): a chronicle line, a
        /// message when <paramref name="announce"/>, and the plan's one letter if it is the first. Names this link only. A tool learned of by words that name its holder (<paramref name="namesHolder"/>: not the count's)
        /// makes its true holder the known one (a holder known before stays known, as a tool row of its own: <see cref="PlanLink.Ghost"/>), and a night learned of fixes the hour the warden was told.
        /// </summary>
        public static bool Reveal(StoryArc arc, PlanLink link, string how, Map map, bool announce = true, string shown = null, bool namesHolder = true)
        {
            if (link == null || map == null) return false;
            string text = shown ?? DefaultShown(map, arc.plan, link);
            if (link.known && (link.role != PlanRole.Tool || shown == null || text == link.shown || !namesHolder || link.knownHolderId == link.pawnId)) return false;
            // (A find on someone else while another was known as the holder: the first stays known, as a row of its own with no tool behind it, for the known crew and foil.)
            if (link.role == PlanRole.Tool && namesHolder && link.known && link.knownHolderId >= 0 && link.knownHolderId != link.pawnId)
                arc.plan.links.Add(new PlanLink { role = PlanRole.Tool, known = true, shown = link.shown, knownHolderId = link.knownHolderId, name = InmateOf(map, link.knownHolderId)?.LabelShort });
            (link.known, link.shown) = (true, text);
            if (link.role == PlanRole.Tool && namesHolder) link.knownHolderId = link.pawnId;
            if (link.role == PlanRole.Night && arc.plan.nightTick >= 0) link.knownHour = HourAt(map, arc.plan.nightTick);
            if (!HasUnknown(arc)) arc.plan.moreShown = false;
            Pawn p = KnownPawn(map, link);
            TaggedString line = "RP_BreakLinkFound".Translate(text, How(how));
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, line, p);
            if (announce) Messages.Message(line, LookAt(p), MessageTypeDefOf.NeutralEvent, historical: false);
            MaybeAsk(arc, map);
            return true;
        }

        /// <summary>One link nobody knew of is told, the first unknown one in this order.</summary>
        private static PlanLink RevealNext(StoryArc arc, Map map, string how, PlanRole[] order, PlanLink not = null, bool announce = true)
        {
            foreach (PlanRole role in order)
            {
                PlanLink l = arc.plan.links.Find(x => x.role == role && !x.known && !x.handled && x != not);
                if (l != null && Reveal(arc, l, how, map, announce)) return l;
            }
            return null;
        }

        /// <summary>
        /// A thing the staff took from an inmate (a frisk, a search, a tunnel, a bust; the hook in <see cref="ToolCount.Notify_Taken"/>, which every taking calls): the plan's tool is found, on whom, and it is out of the plan.
        /// Handed in at an amnesty it is out, and nothing is learned (no questions asked).
        /// </summary>
        public static void Notify_ToolTaken(Thing t, ToolSource source, Pawn from = null)
        {
            if (t == null || Current() is not { } arc || arc.plan.links.Find(l => l.role == PlanRole.Tool && !l.handled && l.thingId == t.thingIDNumber) is not { } link) return;
            Map map = arc.A?.MapHeld ?? Find.AnyPlayerHomeMap;
            if (source != ToolSource.Amnesty)
            {
                // (Found on whoever it was taken from: passed on since the daily pass, the link follows it.)
                if (from != null) (link.pawnId, link.name) = (from.thingIDNumber, from.LabelShort);
                string shown = "RP_BreakLine_ToolFound".Translate(link.name, t.LabelNoCount).Resolve();
                Reveal(arc, link, SourceHow(source), map, shown: shown);
            }
            link.handled = true;
        }

        private static string SourceHow(ToolSource source) => source switch
        {
            ToolSource.Stash => "stash",
            ToolSource.Tunnel => "tunnel",
            ToolSource.Raid => "raid",
            _ => "find",
        };

        /// <summary>
        /// The tool count found a room short (<see cref="ToolCount.Counted"/>, <paramref name="countedFrom"/> the order the count before it had reached): if the plan's tool is one of the tools it now counts missing, the
        /// warden knows the plan has a tool from that room and period - not who has it.
        /// </summary>
        public static void Notify_ToolCounted(ToolPool pool, int countedFrom)
        {
            if (Current() is not { } arc || arc.plan.Find(PlanRole.Tool) is not { known: false } link || ToolCount.Book is not { } book) return;
            if (!book.byThing.TryGetValue(link.thingId, out ToolTrace trace) || trace.poolId != pool.id || trace.order < countedFrom || trace.order >= pool.countedOrder) return;
            Reveal(arc, link, "count", arc.A?.MapHeld ?? Find.AnyPlayerHomeMap, shown: "RP_BreakLine_ToolCounted".Translate(pool.zone.label, trace.period).Resolve(), namesHolder: false);
        }

        /// <summary>
        /// A snitch's tip, a tasked informant's or a unit officer's (<see cref="InformantSystem.TryTip"/>, <see cref="InformantSystem.StaffTip"/>): near someone in the crew, the teller may tell one link. Returns the tip's words, or null.
        /// Nobody in the plan tells of it (the crew, the tool's holder, the inside man as the unit officer of the crew's wing), unless it is the inside man turned.
        /// </summary>
        public static string TryTip(Pawn teller, List<Pawn> near, float chance = TipChance, Map map = null)
        {
            map ??= teller.MapHeld;
            if (map == null || Current() is not { } arc || arc.plan.links.Exists(l => l.pawnId == teller.thingIDNumber && !(l.role == PlanRole.Officer && l.turned))) return null;
            if (!arc.plan.links.Exists(l => l.Crew && !l.handled && near.Exists(o => o.thingIDNumber == l.pawnId)) || !Roll(chance)) return null;
            PlanLink told = RevealNext(arc, map, "tip", TipOrder, announce: false);
            return told == null ? null : "RP_TipBreak".Translate(LinkLine(told));
        }

        /// <summary>A phone tap on someone in the crew: one link (the inside man first, then the night, the way). Returns whether one was told.</summary>
        public static bool Notify_Tapped(Pawn inmate)
        {
            if (Current() is not { } arc || CrewLink(arc, inmate) == null || inmate.MapHeld is not { } map || !Roll(TapChance)) return false;
            return RevealNext(arc, map, "tap", TapOrder) != null;
        }

        /// <summary>A session with the psychologist: someone in the crew says more than they meant to (their own part first, then another).</summary>
        public static bool Notify_Assessed(Pawn inmate, float chance)
        {
            if (Current() is not { } arc || CrewLink(arc, inmate) is not { } mine || inmate.MapHeld is not { } map || !Roll(chance)) return false;
            return Reveal(arc, mine, "session", map) || RevealNext(arc, map, "session", SessionOrder, mine) != null;
        }

        /// <summary>A cell searched (<see cref="TunnelSystem.SearchCell"/>): the plan's tunnel from this inmate's cell may be found, and is filled in; the plan looks for another way.</summary>
        public static void Notify_CellSearched(Pawn owner)
        {
            if (Current() is not { } arc || arc.plan.way != PlanWay.Tunnel || arc.plan.Find(PlanRole.Way) is not { } way || way.pawnId != owner.thingIDNumber || owner.MapHeld is not { } map || !Roll(CellFindChance)) return;
            // (A tunnel the warden knew of is filled in with a word of it: Reveal has nothing new to tell of a known way.)
            if (!Reveal(arc, way, "search", map) && way.known)
            {
                TaggedString line = "RP_BreakTunnelFilled".Translate(LinkLine(way));
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, line);
                Messages.Message(line, MessageTypeDefOf.NeutralEvent, historical: false);
            }
            way.handled = true;
        }

        // ---- the warden's moves --------------------------------------------------------------------------------

        public static string FoilWhy(StoryArc arc, Map map) => arc.done ? "RP_BreakOver".Translate().Resolve() : KnownCrew(arc, map).Count == 0 ? "RP_BreakNoCrewKnown".Translate().Resolve() : null;

        public static string TurnWhy(StoryArc arc, Map map) => arc.done ? "RP_BreakOver".Translate().Resolve() : InsideMan(arc, map) == null ? "RP_BreakNoInsideMan".Translate().Resolve() : null;

        /// <summary>Why no post can go up on the way from Intel: the plan over, the way not known, a post already up.</summary>
        public static string PostWhy(StoryArc arc, Map map)
        {
            if (arc.done) return "RP_BreakOver".Translate().Resolve();
            if (arc.plan.Find(PlanRole.Way) is not { known: true }) return "RP_IntelBreakNoWay".Translate().Resolve();
            return PlanPost(arc, map) is { } post && OnWay(arc.plan, post) && post.TryGetComp<CompGuardPost>() is { } comp && PostHours(map, arc.plan) == (comp.fromHour, comp.toHour)
                ? "RP_IntelBreakPosted".Translate(PostHoursText(map, arc.plan)).Resolve() : null;
        }

        /// <summary>The post stands on the plan's way as it is now (near enough to catch the crew there: <see cref="Catcher"/>'s test).</summary>
        public static bool OnWay(EscapePlan plan, Thing post) => plan.wayCell.IsValid && post.Position.InHorDistOf(plan.wayCell, PostRadius);

        /// <summary>The known inside man the warden can turn: still corrupt, still on the crew's payroll, not turned.</summary>
        public static Pawn InsideMan(StoryArc arc, Map map) =>
            arc.plan.Find(PlanRole.Officer) is { known: true, turned: false } l && StaffOf(map, l.pawnId) is { } s && s.GetComp<CompStaff>() is { corrupt: true, doubleAgent: false } ? s : null;

        /// <summary>
        /// Foil it now: the crew the warden knows of are to be frisked and their cells searched (the guards' own work: flagged and due a search), kept apart from each other, and each has a case on the adjudication sheet as
        /// suspicion only (no officer, no camera: the evidence is thin); a known inside man draws internal affairs' suspicion. The plan falls apart.
        /// </summary>
        public static int Foil(StoryArc arc, Map map)
        {
            if (FoilWhy(arc, map) != null) return 0;
            PrisonGame game = PrisonGame.Instance;
            List<Pawn> known = KnownCrew(arc, map);
            int now = Now;
            foreach (Pawn p in known)
            {
                CompInmate c = p.Inmate();
                (c.suspected, c.cellSearchPending) = (true, true);
                c.AddRecord(new MisconductRecord { def = RPBreakDefOf.RP_EscapePlot, tick = now, cause = c.mainCause, seen = false });
                if (Adjudication.Queue(p, c, RPBreakDefOf.RP_EscapePlot, evidence: false))
                {
                    AdjudicationCase k = game.adjudication.cases[game.adjudication.cases.Count - 1];
                    (k.officer, k.officerName, k.camera, k.weapon) = (false, null, false, false);
                    k.record = c.records.Count(r => r.seen && r.def.severity >= 2);
                }
            }
            for (int i = 0; i < known.Count; i++)
                for (int j = i + 1; j < known.Count; j++)
                    KeepApart.Add(known[i], known[j], "break", silent: true);
            if (arc.plan.Find(PlanRole.Officer) is { known: true } officer && StaffOf(map, officer.pawnId) is { } staff && staff.GetComp<CompStaff>() is { corrupt: true } sc)
                CorruptionSystem.AddSuspicion(staff, sc, 60f, "RP_Sus_Break");
            InmateCandidates.Shared.Invalidate();
            arc.plan.answer = PlanAnswer.Foil;
            string names = string.Join(", ", known.Select(p => p.LabelShort));
            Messages.Message("RP_BreakFoiled".Translate(names), new LookTargets(known), MessageTypeDefOf.PositiveEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_BreakFoiled".Translate(names), known.ToArray());
            End(arc, map);
            return known.Count;
        }

        /// <summary>The hour of the night the warden was told (<see cref="PlanLink.knownHour"/>, never the true night's: a blackout or a day gained moves that one unseen), or -1 when the night is not known.</summary>
        private static int KnownNightHour(EscapePlan plan) => plan.Find(PlanRole.Night) is { known: true, knownHour: >= 0 } n ? n.knownHour : -1;

        /// <summary>The hours a post on the way is manned: from the hour before the known night to two hours after it, else the small hours every night.</summary>
        public static (int from, int to) PostHours(Map map, EscapePlan plan)
        {
            int h = KnownNightHour(plan);
            return h >= 0 ? ((h + 23) % 24, (h + 3) % 24) : (SmallFrom, SmallTo);
        }

        public static string PostHoursText(Map map, EscapePlan plan)
        {
            (int from, int to) = PostHours(map, plan);
            bool night = KnownNightHour(plan) >= 0;
            return (night ? "RP_BreakPostHoursNight" : "RP_BreakPostHoursSmall").Translate(GuardPosts.HourLabel(from), GuardPosts.HourLabel(to)).Resolve();
        }

        /// <summary>The post the plan put up, still standing (one on the way as it is now first), or null.</summary>
        public static Thing PlanPost(StoryArc arc, Map map)
        {
            if (arc.plan.posts.Count == 0) return null;
            Thing any = null;
            foreach (Thing t in map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost))
            {
                if (!arc.plan.posts.Contains(t.thingIDNumber)) continue;
                if (OnWay(arc.plan, t)) return t;
                any ??= t;
            }
            return any;
        }

        /// <summary>
        /// Let it run: the guards wait for the night. With the way known a guard post goes up on it (or the one up on it has its hours set again; one the plan put up on a way it no longer takes comes down), manned for
        /// the known night or else the small hours; a crew that comes out by a manned post is caught in the act. Returns the post, or null (the way not known, or no cell for one).
        /// </summary>
        public static Thing LetItRun(StoryArc arc, Map map)
        {
            if (arc.done) return null;
            EscapePlan plan = arc.plan;
            if (plan.answer != PlanAnswer.Turn) plan.answer = PlanAnswer.Watch;
            if (plan.Find(PlanRole.Way) is not { known: true } || !plan.wayCell.IsValid)
            {
                Messages.Message("RP_BreakWatching".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
                return null;
            }
            foreach (Thing old in map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost).Where(t => plan.posts.Contains(t.thingIDNumber) && !OnWay(plan, t)).ToList())
            {
                plan.posts.Remove(old.thingIDNumber);
                if (!old.Destroyed) old.Destroy();
            }
            Thing post = PlanPost(arc, map);
            if (post == null)
            {
                post = GuardPosts.Place(map, plan.wayCell);
                if (post == null) return null;
                plan.posts.Add(post.thingIDNumber);
            }
            if (post.TryGetComp<CompGuardPost>() is { } comp) (comp.fromHour, comp.toHour) = PostHours(map, plan);
            Messages.Message("RP_BreakPosted".Translate(GuardPosts.PlaceOf(post.TryGetComp<CompGuardPost>()), PostHoursText(map, plan)), post, MessageTypeDefOf.NeutralEvent, historical: false);
            return post;
        }

        /// <summary>Turn the inside man: a double agent (as internal affairs would make them), who tells the warden one more link of the plan a day.</summary>
        public static bool Turn(StoryArc arc, Map map)
        {
            if (TurnWhy(arc, map) != null || InsideMan(arc, map) is not { } staff) return false;
            CorruptionSystem.Turn(staff, staff.GetComp<CompStaff>(), map);
            arc.plan.Find(PlanRole.Officer).turned = true;
            arc.plan.answer = PlanAnswer.Turn;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_BreakTurned".Translate(staff.LabelShort), staff);
            Messages.Message("RP_BreakTurned".Translate(staff.LabelShort), LookAt(staff), MessageTypeDefOf.PositiveEvent, historical: false);
            return true;
        }

        /// <summary>The one letter, when the first link is known: what is known, and the three answers.</summary>
        public static bool MaybeAsk(StoryArc arc, Map map, bool force = false)
        {
            EscapePlan plan = arc.plan;
            if (arc.done || map == null || !Visible(arc) || plan.asked && !force) return false;
            plan.asked = true;
            string lines = string.Join("\n", plan.links.Where(l => l.known).Select(l => "  - " + LinkLine(l)));
            List<Pawn> targets = KnownCrew(arc, map).Where(p => p.Spawned).ToList();
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "RP_BreakAskLabel".Translate(), "RP_BreakAskText".Translate(lines, PostHoursText(map, plan)), map, new LookTargets(targets));
            letter.arcId = arc.id;
            letter.pawns.AddRange(targets);
            letter.StartTimeout(GenDate.TicksPerHour * arc.def.decisionHours);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }

        // ---- what Intel draws ----------------------------------------------------------------------------------

        private static readonly PlanRole[] ChainOrder = { PlanRole.Leader, PlanRole.Member, PlanRole.Tool, PlanRole.Officer, PlanRole.Way, PlanRole.Night };

        /// <summary>The known links, in order (the crew, the tool, the inside man, the way, the night), then one marker while more was suspected at the last daily pass: never a mark per link, nor a count of them.</summary>
        public static List<PlanPart> Chain(StoryArc arc)
        {
            var parts = new List<PlanPart>();
            foreach (PlanRole role in ChainOrder)
                foreach (PlanLink l in arc.plan.links)
                    if (l.role == role && l.known) parts.Add(new PlanPart(LinkLine(l), l));
            if (MoreSuspected(arc)) parts.Add(new PlanPart("RP_IntelBreakMore".Translate(), null));
            return parts;
        }

        /// <summary>The pawn to jump to for a known link (a crew member, the officer, whoever the warden learned holds the tool), or null.</summary>
        public static Pawn Focus(StoryArc arc, PlanLink l, Map map) => l == null ? null : KnownPawn(map, l);

        /// <summary>For the snapshot of a save/load round trip: every plan, in one string.</summary>
        public static string Describe()
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null) return "";
            var sb = new StringBuilder();
            foreach (StoryArc a in arcs.Where(a => a.plan != null).OrderBy(a => a.id))
                sb.Append($"{a.id}:{a.done}:{a.A?.thingIDNumber}:{a.startTick}:{a.plan.Describe()};");
            return sb.ToString();
        }
    }

    /// <summary>The story arc of one plan: the leader is its cast, the rest is read from the game (see <see cref="EscapePlans"/>).</summary>
    public class EscapePlanArc : StoryArcWorker
    {
        public const int Foil = 0, Watch = 1, TurnIt = 2;
        private static readonly string[] Keys = { "RP_Break_Foil", "RP_Break_Watch", "RP_Break_Turn" };

        public override bool Valid(StoryArc arc) => arc.plan != null && base.Valid(arc);

        public override void Hourly(StoryArc arc, Map map) => EscapePlans.Hourly(arc, map);

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        /// <summary>Unanswered: let it run (with the way known, the post goes up).</summary>
        public override int TimeoutChoice(StoryArc arc) => Watch;

        public override string Disabled(StoryArc arc, int option)
        {
            if (arc.plan == null) return null;
            Map map = arc.A?.MapHeld ?? Find.AnyPlayerHomeMap;
            return option switch
            {
                Foil => EscapePlans.FoilWhy(arc, map),
                TurnIt => EscapePlans.TurnWhy(arc, map),
                _ => arc.done ? "RP_BreakOver".Translate().Resolve() : null,
            };
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            if (arc.plan == null || arc.done) return;
            switch (option)
            {
                case Foil:
                    EscapePlans.Foil(arc, map);
                    break;
                case TurnIt:
                    EscapePlans.Turn(arc, map);
                    break;
                default:
                    EscapePlans.LetItRun(arc, map);
                    break;
            }
        }

        /// <summary>The leader's leaving ends it; anyone else leaving takes their link with them (a known one is kept, out), and a tool in their hands goes with them.</summary>
        public override void Notify_Leaving(StoryArc arc, Pawn p)
        {
            if (arc.A == p)
            {
                base.Notify_Leaving(arc, p);
                return;
            }
            if (arc.plan == null) return;
            List<PlanLink> links = arc.plan.links;
            for (int i = links.Count - 1; i >= 0; i--)
            {
                PlanLink l = links[i];
                bool seen = l.role == PlanRole.Tool && l.knownHolderId == l.pawnId;
                // (The holder the warden knew of is gone: nothing of the tool's holder is known any more, whoever has it now.)
                if (l.role == PlanRole.Tool && l.knownHolderId == p.thingIDNumber) l.knownHolderId = -1;
                if (l.handled || l.pawnId != p.thingIDNumber || l.role is not (PlanRole.Member or PlanRole.Tool)) continue;
                // (The true holder leaves: the warden sees the tool go only when it was in the hands they knew of.)
                if (l.role == PlanRole.Tool && l.known && !seen) EscapePlans.ToolOut(l);
                else if (l.known) l.handled = true;
                else links.RemoveAt(i);
            }
        }

        /// <summary>The leader is gone: the plan comes to nothing, said once if the warden knew of it.</summary>
        public override void Cancelled(StoryArc arc) => EscapePlans.FallApart(arc, null);
    }
}
