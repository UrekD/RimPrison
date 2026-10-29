using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPGangDealDefOf
    {
        public static StoryArcDef RP_Arc_GangDeal;

        static RPGangDealDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPGangDealDefOf));
    }

    /// <summary>What the warden gives the gang for its quiet (see <see cref="GangDeals"/>): the leader's pick of the four.</summary>
    public enum GangDealTerm : byte
    {
        /// <summary>A yard or common room patch kept as the gang's turf.</summary>
        Turf,
        /// <summary>Two visits a week for its members.</summary>
        Visits,
        /// <summary>The commissary open to its members, whatever ban they are under.</summary>
        Commissary,
        /// <summary>A job slot for the member the leader names.</summary>
        Job,
    }

    /// <summary>How a deal ended (every way writes the same cooldown; the last two cost the leader's trust, see <see cref="GangDeals.End"/>).</summary>
    public enum GangDealEnd : byte
    {
        /// <summary>The ten days ran out.</summary>
        Lapsed,
        /// <summary>The warden called it off (on a breach, or from Intel).</summary>
        Warden,
        /// <summary>A second breach.</summary>
        Breach,
        /// <summary>The leader is gone, no longer leads, is off the deal's wing or is on an escort-only level (no trust cost).</summary>
        Gone,
    }

    /// <summary>
    /// The shot-caller's deal as the arc carries it (<see cref="StoryArc.deal"/>): who it is with, what the gang got for its quiet, how long, and the bookkeeping the term needs to be taken back. The arc's
    /// cast is the leader and <see cref="StoryArc.stake"/> is the kept patch's name (its first cell's index), which the turf's reconcile moves with the patch like a war's stake.
    /// </summary>
    public class GangDeal : IExposable
    {
        public GangDef gang;
        /// <summary>The leader (thing id), and their name for the lines that outlive them.</summary>
        public int leaderId = -1;
        public string leaderName;
        public GangDealTerm term;
        /// <summary>The leader's wing when it was struck: the zone whose members misbehave less and whose other gangs are wound up.</summary>
        public PrisonZoneDef wing;
        public int untilTick = -1;
        public int breaches;
        /// <summary>The deal made the claim on the kept patch (the gang did not hold it): the deal's end lets it go. A patch the gang held already is left to the daily verdict.</summary>
        public bool claimMade;
        /// <summary>The member holding the job slot, and the job: locked in for the deal's length.</summary>
        public int jobMemberId = -1;
        public string jobMemberName;
        public InmateJobDef jobDef;
        /// <summary>The member's own job and lock before the deal gave them the slot: put back when it ends.</summary>
        public InmateJobDef prevJob;
        public bool prevJobLocked;
        /// <summary>A breach's letter is open (the first one, unanswered): another breach in the meantime is part of the same trouble, and is not counted.</summary>
        public bool breachOpen;
        /// <summary>The press has had its story of this deal.</summary>
        public bool pressDone;

        /// <summary>The terms are taken back already (not saved: a deal that is over is forgotten the same hour).</summary>
        [Unsaved] internal bool withdrawn;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref gang, "gang");
            Scribe_Values.Look(ref leaderId, "leaderId", -1);
            Scribe_Values.Look(ref leaderName, "leaderName");
            Scribe_Values.Look(ref term, "term");
            Scribe_Defs.Look(ref wing, "wing");
            Scribe_Values.Look(ref untilTick, "until", -1);
            Scribe_Values.Look(ref breaches, "breaches");
            Scribe_Values.Look(ref claimMade, "claimMade");
            Scribe_Values.Look(ref jobMemberId, "jobMemberId", -1);
            Scribe_Values.Look(ref jobMemberName, "jobMemberName");
            Scribe_Defs.Look(ref jobDef, "jobDef");
            Scribe_Defs.Look(ref prevJob, "prevJob");
            Scribe_Values.Look(ref prevJobLocked, "prevJobLocked");
            Scribe_Values.Look(ref breachOpen, "breachOpen");
            Scribe_Values.Look(ref pressDone, "pressDone");
        }
    }

    /// <summary>
    /// The shot-caller's deal (gap analysis 6, item 5). The warden's word with a known gang's known leader offers "keep your people quiet": for ten days the gang's members on the leader's wing misbehave at
    /// <see cref="CalmFactor"/>, the gang starts no war and recruits nobody, and the other gangs' members on that wing are wound up ("the Ravens run the yard"), against one term of the leader's pick (see
    /// <see cref="GangDealTerm"/>). It is paid for: the inspector's score is lower by <see cref="InspectionPoints"/> while a deal runs, and the press may tell it once. A fight or an assault a member is seen in is a breach: the
    /// warden overlooks the first (once) or ends the deal; the second ends it. Ending it early costs the leader's trust. A deal is one arc (<see cref="GangDealArc"/>, background: it keeps nobody out of other stories) with its
    /// payload (<see cref="GangDeal"/>); the five days before the next deal with a gang are counted from <see cref="StorySystem.gangDealEnded"/>, since an arc is forgotten once it is over. The hot reads
    /// (<see cref="MisconductFactor"/>, <see cref="Waives"/>, <see cref="Holds"/>) are one dictionary lookup by gang, and nothing at all while no deal runs.
    /// </summary>
    public static class GangDeals
    {
        public const int DealDays = 10, CooldownDays = 5;
        /// <summary>The members' misconduct chance on the wing while the deal runs.</summary>
        public const float CalmFactor = 0.6f;
        /// <summary>The inspector's score is lower by this while a deal runs (<see cref="Inspections.Score"/>), and an open interview with the press is by <see cref="PressPenalty"/> reputation.</summary>
        public const int InspectionPoints = 5;
        public const float PressPenalty = 3f;
        /// <summary>Hourly tension of the other gangs' members on the deal's wing.</summary>
        public const float RivalTensionPerHour = 1f;
        /// <summary>The chance a day that the press hears of the deal (once per deal).</summary>
        public const float PressChancePerDay = 0.25f;
        /// <summary>The leader's trust of the warden: a refused offer (and the tension it leaves), and a deal the warden calls off.</summary>
        public const float RefusedTrust = -4f, RefusedTension = 3f, EndedTrust = -12f;
        /// <summary>Days a member can go without a visit under a visits term (two a week).</summary>
        public const float VisitGapDays = 3.5f;
        /// <summary>The leader's odds of taking an offer: this, and up to 0.4 more (or less) with their trust of the warden (the shape of <see cref="WardenSystem.InformantChance"/>, which is nothing for a leader).</summary>
        public const float BaseChance = 0.35f;

        private static int Now => Find.TickManager.TicksGame;

        // ---- the running deals, by gang ----------------------------------------------------------------------------------

        private static readonly Dictionary<GangDef, StoryArc> running = new Dictionary<GangDef, StoryArc>();
        private static List<StoryArc> seenArcs;
        private static int seenCount = -1, seenVersion = -1, version;

        /// <summary>The deals changed (one started or ended): the lookup is built again at the next read. It also notices a new arc list, and one more or fewer arcs in it.</summary>
        internal static void Changed() => version++;

        private static Dictionary<GangDef, StoryArc> Running
        {
            get
            {
                List<StoryArc> arcs = PrisonGame.Instance?.story?.arcs;
                if (arcs == null)
                {
                    running.Clear();
                    seenArcs = null;
                    return running;
                }
                if (!ReferenceEquals(arcs, seenArcs) || arcs.Count != seenCount || version != seenVersion)
                {
                    running.Clear();
                    for (int i = 0; i < arcs.Count; i++)
                        if (!arcs[i].done && arcs[i].def == RPGangDealDefOf.RP_Arc_GangDeal && arcs[i].deal?.gang != null) running[arcs[i].deal.gang] = arcs[i];
                    (seenArcs, seenCount, seenVersion) = (arcs, arcs.Count, version);
                }
                return running;
            }
        }

        /// <summary>Some gang has a deal running.</summary>
        public static bool Any => Running.Count > 0;

        /// <summary>The gang's running deal (the arc), or null.</summary>
        public static StoryArc Of(GangDef gang) => gang != null && Running.TryGetValue(gang, out StoryArc arc) ? arc : null;

        /// <summary>The gang has a deal running: it starts no war and recruits nobody.</summary>
        public static bool Holds(GangDef gang) => gang != null && Running.ContainsKey(gang);

        public static bool NoRecruiting(GangDef gang) => Holds(gang);

        /// <summary>The inspector's score is lower by this now (<see cref="InspectionPoints"/> while a deal runs).</summary>
        public static int InspectionPenalty => Any ? InspectionPoints : 0;

        /// <summary>The gang's members on the wing of its deal: 0.6 for them, 1 for everyone else and for any gang with no deal.</summary>
        public static float MisconductFactor(CompInmate c)
        {
            if (c.gang == null || Running.Count == 0 || !Running.TryGetValue(c.gang, out StoryArc arc)) return 1f;
            return OnWing(c.Pawn, arc.deal.wing) ? CalmFactor : 1f;
        }

        private static bool OnWing(Pawn p, PrisonZoneDef wing)
        {
            if (p == null || wing == null || p.MapHeld is not { } map || PrisonMap.Of(map) is not { } pm) return false;
            return WardenSystem.InWing(p, wing, pm);
        }

        /// <summary>
        /// Hourly tension of a member of another gang on the wing of a deal ("the Ravens run the yard"), with the cause to name it by (the gang's name only for a member whose own gang is on file: see
        /// <see cref="TensionUtility.ShownCause"/>). Nothing for a gang's own members or for those of no gang.
        /// </summary>
        public static float RivalTension(Pawn p, CompInmate c, PrisonMap pm, out string cause)
        {
            cause = null;
            if (c.gang == null || Running.Count == 0) return 0f;
            foreach (KeyValuePair<GangDef, StoryArc> kv in Running)
            {
                if (kv.Key == c.gang || kv.Value.deal.wing == null || !WardenSystem.InWing(p, kv.Value.deal.wing, pm)) continue;
                // (The cause is stored and read back in places that do not check what the warden knows: it names the gang only for one whose own gang is on file.)
                cause = c.gangKnown ? CauseKey(kv.Key) : "RP_Cause_WingTrouble";
                return RivalTensionPerHour;
            }
            return 0f;
        }

        public const string CausePrefix = "RP_Cause_Deal";
        private static readonly Dictionary<GangDef, string> causeKeys = new Dictionary<GangDef, string>();

        /// <summary>"the Ravens run the yard": a keyed cause of the gang's own where there is one, else the plain one.</summary>
        public static string CauseKey(GangDef gang)
        {
            if (!causeKeys.TryGetValue(gang, out string key))
            {
                key = CausePrefix + "_" + gang.defName;
                causeKeys[gang] = key = key.CanTranslate() ? key : CausePrefix;
            }
            return key;
        }

        public static bool IsDealCause(string cause) => cause != null && cause.StartsWith(CausePrefix, System.StringComparison.Ordinal);

        /// <summary>The deal's term is this one for the inmate's gang (a visits term, a commissary term): its members' sanctions and odds read it.</summary>
        public static bool Waives(CompInmate c, GangDealTerm term) =>
            c.gang != null && Running.Count > 0 && Running.TryGetValue(c.gang, out StoryArc arc) && arc.deal.term == term;

        /// <summary>A member of a gang with a visits term is owed a visit: none from a relative for <see cref="VisitGapDays"/> days (from the deal's start, or the last such visit).</summary>
        public static bool VisitOwed(CompInmate c)
        {
            if (c.gang == null || Running.Count == 0 || !Running.TryGetValue(c.gang, out StoryArc arc) || arc.deal.term != GangDealTerm.Visits) return false;
            return Now - Mathf.Max(c.lastVisitTick, arc.startTick) >= VisitGapDays * GenDate.TicksPerDay;
        }

        /// <summary>The patch (its anchor) is one a deal keeps for a gang: the daily verdict of the turf leaves it alone.</summary>
        public static bool KeepsPatch(int anchor)
        {
            if (Running.Count == 0) return false;
            foreach (KeyValuePair<GangDef, StoryArc> kv in Running)
                if (kv.Value.deal.term == GangDealTerm.Turf && kv.Value.stake == anchor) return true;
            return false;
        }

        public static float DaysLeft(StoryArc arc) => Mathf.Max(0f, (arc.deal.untilTick - Now) / (float)GenDate.TicksPerDay);

        // ---- who can be offered one --------------------------------------------------------------------------------------

        /// <summary>
        /// The warden's word with this inmate has the line: a known gang's known leader (the leader's gang and rank are on the file, as Intel lists them), and nobody else. <paramref name="why"/> is why it cannot be
        /// said now (a keyed phrase, resolved), or null.
        /// </summary>
        public static bool LineFor(Pawn inmate, CompInmate c, out string why)
        {
            why = null;
            if (c.gang == null || !c.gangKnown || !c.gangLeader || inmate.MapHeld is not { } map || PrisonGame.Instance == null) return false;
            why = Blocker(map, inmate, c);
            return true;
        }

        /// <summary>Why no deal can be struck with this leader now, or null: one runs, five days have not passed, the leader is out of reach (the hole, the supermax, a hostage-taker, a rioter, the gallows, the shuttle), is in another story, the gang is at war, or the leader sleeps in no wing.</summary>
        public static string Blocker(Map map, Pawn leader, CompInmate c)
        {
            if (c.gang == null || !c.gangKnown || !c.gangLeader || !InmateUtility.IsActiveInmate(leader)) return "RP_GangDealWhy_Gone".Translate().Resolve();
            if (Of(c.gang) is { } arc) return "RP_GangDealWhy_Running".Translate(DaysLeft(arc).ToString("0.0")).Resolve();
            if (CooldownLeft(c.gang) is { } days) return "RP_GangDealWhy_Cooldown".Translate(days.ToString("0.0")).Resolve();
            if (DeathRowSystem.Ext(c) != null || c.forcedActivity == RPSecurityDefOf.RP_Solitary || c.security is { escortOnly: true } || HostageCrisis.IsHeld(leader) || leader.InMentalState || ShuttleSystem.OnTheWayOut(leader))
                return "RP_GangDealWhy_Away".Translate(leader.LabelShort).Resolve();
            if (PrisonGame.Instance.story.InArc(leader)) return "RP_GangDealWhy_Story".Translate(leader.LabelShort).Resolve();
            if (AtWar(c.gang)) return "RP_GangDealWhy_War".Translate(c.gang.label).Resolve();
            if (PrisonMap.Of(map) is not { } pm || WingOf(leader, pm) == null) return "RP_GangDealWhy_NoWing".Translate(leader.LabelShort).Resolve();
            return null;
        }

        /// <summary>Days still to wait for a new deal with the gang (null: none to wait).</summary>
        public static float? CooldownLeft(GangDef gang)
        {
            if (!PrisonGame.Instance.story.gangDealEnded.TryGetValue(gang, out int ended)) return null;
            int left = ended + CooldownDays * GenDate.TicksPerDay - Now;
            return left > 0 ? left / (float)GenDate.TicksPerDay : null;
        }

        private static bool AtWar(GangDef gang)
        {
            List<StoryArc> arcs = PrisonGame.Instance.story.arcs;
            for (int i = 0; i < arcs.Count; i++)
                if (!arcs[i].done && arcs[i].def == RPGangWarDefOf.RP_Arc_GangWar && GangWarArc.Gangs(arcs[i], out GangDef a, out GangDef b) && (a == gang || b == gang)) return true;
            return false;
        }

        /// <summary>The leader's wing: the wing zone over their bed, else the one they stand in; null for a leader who sleeps and stands in none.</summary>
        public static PrisonZoneDef WingOf(Pawn leader, PrisonMap pm)
        {
            if (leader.ownership?.OwnedBed is { Spawned: true } bed && pm.zones.ZoneAt(bed.Position) is { security: not null } zone) return zone;
            return leader.Spawned && pm.zones.ZoneAt(leader.Position) is { security: not null } here ? here : null;
        }

        // ---- the terms ---------------------------------------------------------------------------------------------------

        /// <summary>Why the leader's gang cannot be given this term now (a keyed phrase, resolved), or null.</summary>
        public static string TermWhy(Map map, Pawn leader, GangDealTerm term)
        {
            if (leader.Inmate() is not { gang: not null } c || PrisonMap.Of(map) is not { } pm) return "RP_GangDealWhy_Gone".Translate().Resolve();
            return term switch
            {
                GangDealTerm.Turf when PickPatch(leader, c, pm) < 0 => "RP_GangDealWhy_NoPatch".Translate(c.gang.label).Resolve(),
                GangDealTerm.Job when !PickJob(map, leader, c, out _, out _) => "RP_GangDealWhy_NoJob".Translate(c.gang.label).Resolve(),
                GangDealTerm.Visits when !pm.zones.Any(RPVisitDefOf.RP_Visitation) || !AnyWithFamily(map, c.gang) => "RP_GangDealWhy_NoVisits".Translate(c.gang.label).Resolve(),
                _ => null,
            };
        }

        /// <summary>Somebody of the gang has family who could come (the visits hook calls only those).</summary>
        private static bool AnyWithFamily(Map map, GangDef gang)
        {
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { } c && c.gang == gang && c.familyCount > 0) return true;
            return false;
        }

        /// <summary>
        /// The patch of the yard or the common room the leader's wing uses that is nearest their bed (their own cell, else where they stand), that no other gang holds and their level may use: its index in the zone grid's
        /// patches, -1 for none. A patch the gang holds already is the nearest it can be.
        /// </summary>
        public static int PickPatch(Pawn leader, CompInmate c, PrisonMap pm)
        {
            ZoneGrid zones = pm.zones;
            GangTurf.Reconcile(pm);
            IntVec3 from = leader.ownership?.OwnedBed is { Spawned: true } bed ? bed.Position : leader.Position;
            List<ZonePatch> patches = zones.Patches;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int pi = 0; pi < patches.Count; pi++)
            {
                ZonePatch patch = patches[pi];
                if (patch.zone != RPDefOf.RP_Yard && patch.zone != RPExerciseDefOf.RP_CommonRoom) continue;
                GangDef holder = pm.turf.At(patch.anchor)?.gang;
                if (holder != null && holder != c.gang || patch.zone.yardFor != null && patch.zone.yardFor != c.security || !zones.Allows(pi, c.security)) continue;
                float dx = patch.center.x - from.x, dz = patch.center.z - from.z, d = dx * dx + dz * dz;
                if (d >= bestDist) continue;
                (best, bestDist) = (pi, d);
            }
            return best;
        }

        /// <summary>
        /// The member of the leader's gang with the best fit for a job with a place free (their own place counted free: the deal keeps them in it): the highest skill in what the job asks, a member who holds the job already first
        /// of equals. Never the leader, nor anyone the warden has set a job for by hand; only a job they may hold (<see cref="LaborUtility.Eligible"/>).
        /// </summary>
        public static bool PickJob(Map map, Pawn leader, CompInmate lc, out Pawn member, out InmateJobDef job)
        {
            member = null;
            job = null;
            List<InmateJobDef> jobs = DefDatabase<InmateJobDef>.AllDefsListForReading;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            var used = new Dictionary<InmateJobDef, int>();
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate()?.job is { } held) used[held] = (used.TryGetValue(held, out int n) ? n : 0) + 1;
            var capacity = new Dictionary<InmateJobDef, int>();
            // (The jobs held, by thing id, when the warden keeps some inmates apart: a job in a room a member's partner works in is not theirs to be given, locked jobs being left alone by the separation.)
            Dictionary<int, InmateJobDef> heldBy = null;
            if (KeepApart.Any)
            {
                heldBy = new Dictionary<int, InmateJobDef>();
                for (int i = 0; i < inmates.Count; i++)
                    if (inmates[i].Inmate()?.job is { } hj) heldBy[inmates[i].thingIDNumber] = hj;
            }
            float bestFit = -1f;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (p == leader || p.Inmate() is not { } c || c.gang != lc.gang || c.jobLocked || !InmateUtility.IsActiveInmate(p)) continue;
                foreach (InmateJobDef j in jobs)
                {
                    if (!LaborUtility.Eligible(c, j) || heldBy != null && KeepApart.PartnerHolds(p, j.zone, heldBy)) continue;
                    if (!capacity.TryGetValue(j, out int cap)) capacity[j] = cap = j.Worker.Capacity(map);
                    if ((used.TryGetValue(j, out int inJob) ? inJob : 0) - (c.job == j ? 1 : 0) >= cap) continue;
                    // (Half a point for holding it already: of equals the member who needs no move.)
                    float fit = (j.skill != null && p.skills != null ? p.skills.GetSkill(j.skill).Level : 0) + (c.job == j ? 0.5f : 0f);
                    if (fit <= bestFit) continue;
                    (member, job, bestFit) = (p, j, fit);
                }
            }
            return member != null;
        }

        /// <summary>The term in words, for the lines (the kept place, the member and their job).</summary>
        public static string TermText(Map map, StoryArc arc)
        {
            GangDeal deal = arc.deal;
            return deal.term switch
            {
                GangDealTerm.Turf => "RP_GangDealTerm_Turf".Translate(GangTurf.StakeName(map, arc.stake) ?? "RP_GangDealTurfGone".Translate().ToString(), deal.gang.label).Resolve(),
                GangDealTerm.Visits => "RP_GangDealTerm_Visits".Translate().Resolve(),
                GangDealTerm.Commissary => "RP_GangDealTerm_Commissary".Translate().Resolve(),
                _ => "RP_GangDealTerm_Job".Translate(deal.jobMemberName ?? "?", deal.jobDef?.label ?? "?").Resolve(),
            };
        }

        /// <summary>The term in a word or two, for the Intel row (the full words are <see cref="TermText"/>).</summary>
        public static string TermShort(GangDealTerm term) => ("RP_GangDealTermShort_" + term).Translate().Resolve();

        /// <summary>The deal's line on Intel: the term in a word, the days left, the breaches (<paramref name="full"/>: the term in full words, for the tooltip).</summary>
        public static string IntelLine(Map map, StoryArc arc, bool full)
        {
            GangDeal deal = arc.deal;
            string breaches = (deal.breaches == 1 ? "RP_IntelGangDealBreach" : "RP_IntelGangDealBreaches").Translate(deal.breaches).Resolve();
            return (full ? "RP_IntelGangDealFull" : "RP_IntelGangDeal").Translate(full ? TermText(map, arc) : TermShort(deal.term), DaysLeft(arc).ToString("0.0"), breaches).Resolve();
        }

        // ---- the offer and the answer ----------------------------------------------------------------------------------------

        /// <summary>The leader's odds of taking a deal: their trust of the warden moves it, as it does an informant's.</summary>
        public static float AcceptChance(Pawn warden, CompInmate c) =>
            Mathf.Clamp(BaseChance + 0.4f * Mathf.Clamp(StaffMemories.ScoreFor(c, warden) / 50f, -1f, 1f), 0.05f, 0.9f);

        /// <summary>
        /// The word is said (called from <see cref="WardenSystem.Word"/>): the letter with the four terms and "not now". Returns the line the warden reads. The letter is never held back by the day's budget
        /// (<c>urgent</c> on its decision): the warden asked for it.
        /// </summary>
        public static string Offer(Pawn warden, Pawn leader, CompInmate c)
        {
            Map map = leader.MapHeld;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_GangDeal", "RP_GangDealOfferLabel".Translate(c.gang.LabelCap),
                "RP_GangDealOfferText".Translate(leader.LabelShort, c.gang.label, DealDays, (1f - CalmFactor).ToStringPercent(), InspectionPoints, AcceptChance(warden, c).ToStringPercent()), map, leader);
            letter.pawns.Add(leader);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
            return "RP_WardenWord_GangDealTold".Translate(warden.LabelShort, leader.LabelShort);
        }

        /// <summary>
        /// The warden picked a term: the leader's dice (<see cref="WardenSystem.ForcedRoll"/> for a test) say whether they take it. A leader who will not costs the warden their trust, as an informant who will not
        /// does. Returns the deal's arc, null when it was refused (or could not start).
        /// </summary>
        public static StoryArc Answer(Map map, Pawn leader, GangDealTerm term, Pawn warden)
        {
            CompInmate c = leader.Inmate();
            if (c?.gang == null || Blocker(map, leader, c) != null || TermWhy(map, leader, term) != null) return null;
            if (!(WardenSystem.ForcedRoll ?? Rand.Chance(AcceptChance(warden, c))))
            {
                if (warden != null) StaffMemories.Note(c, warden, RefusedTrust);
                c.tension = Mathf.Min(100f, c.tension + RefusedTension);
                Messages.Message("RP_GangDealRefused".Translate(leader.LabelShort, c.gang.label, warden?.LabelShort ?? "RP_GangDealTheWarden".Translate().ToString()), leader, MessageTypeDefOf.NegativeEvent, historical: false);
                return null;
            }
            return Start(map, leader, term);
        }

        /// <summary>Strikes the deal: the arc, the term applied, the line. Null when it cannot (see <see cref="Blocker"/>, <see cref="TermWhy"/>).</summary>
        public static StoryArc Start(Map map, Pawn leader, GangDealTerm term)
        {
            CompInmate c = leader.Inmate();
            if (c?.gang == null || PrisonMap.Of(map) is not { } pm || Blocker(map, leader, c) != null) return null;
            int patchIndex = -1;
            Pawn member = null;
            InmateJobDef job = null;
            if (term == GangDealTerm.Turf && (patchIndex = PickPatch(leader, c, pm)) < 0 || term == GangDealTerm.Job && !PickJob(map, leader, c, out member, out job)) return null;
            StoryArc arc = PrisonGame.Instance.story.TryStartArc(RPGangDealDefOf.RP_Arc_GangDeal, leader);
            if (arc == null) return null;
            int now = Now;
            var deal = new GangDeal { gang = c.gang, leaderId = leader.thingIDNumber, leaderName = leader.LabelShort, term = term, wing = WingOf(leader, pm), untilTick = now + DealDays * GenDate.TicksPerDay };
            arc.deal = deal;
            arc.note = c.gang.defName;
            arc.nextTick = deal.untilTick;
            switch (term)
            {
                case GangDealTerm.Turf:
                    ZonePatch patch = pm.zones.Patches[patchIndex];
                    deal.claimMade = pm.turf.At(patch.anchor)?.gang != c.gang;
                    if (deal.claimMade) pm.turf.Set(pm.zones, patch.zone, patch.anchor, c.gang, now);
                    arc.stake = patch.anchor;
                    break;
                case GangDealTerm.Job:
                    CompInmate mc = member.Inmate();
                    (deal.prevJob, deal.prevJobLocked) = (mc.job, mc.jobLocked);
                    mc.job = job;
                    mc.jobLocked = true;
                    (deal.jobMemberId, deal.jobMemberName, deal.jobDef) = (member.thingIDNumber, member.LabelShort, job);
                    break;
            }
            Changed();
            TaggedString line = "RP_Chr_GangDealStart".Translate(leader.LabelShort, c.gang.label, DealDays, TermText(map, arc));
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, line, leader);
            Messages.Message(line, leader, MessageTypeDefOf.NeutralEvent, historical: false);
            return arc;
        }

        // ---- the end ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The deal is over, whichever way: the terms are taken back (the kept patch let go, the job slot unlocked), the five days before the next one start, and the chronicle says how. The warden's calling it off, or a
        /// second breach, costs the leader's trust (the gang ranks have no standing of their own to lower). Safe to call twice.
        /// </summary>
        public static void End(StoryArc arc, GangDealEnd how, Map map)
        {
            arc.done = true;
            GangDeal deal = arc.deal;
            if (deal == null || deal.withdrawn) return;
            deal.withdrawn = true;
            map ??= arc.A?.MapHeld ?? Find.AnyPlayerHomeMap;
            Pawn leader = arc.A is { Dead: false, Destroyed: false } a ? a : map != null ? CourtUtil.FindInmate(map, deal.leaderId) : null;
            Withdraw(arc, map);
            Changed();
            // (A deal whose gang is no longer defined, after a load with a mod removed, has nothing to write the cooldown or the line for.)
            if (deal.gang == null) return;
            PrisonGame.Instance.story.gangDealEnded[deal.gang] = Now;
            if (how is GangDealEnd.Warden or GangDealEnd.Breach && leader?.Inmate() is { } lc && map != null && WardenSystem.InPost(map) is { } warden) StaffMemories.Note(lc, warden, EndedTrust);
            TaggedString line = ("RP_Chr_GangDealEnd_" + how).Translate(deal.leaderName, deal.gang.label);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, line, leader != null ? new[] { leader } : new Pawn[0]);
            if (leader != null) Messages.Message(line, leader, MessageTypeDefOf.NeutralEvent, historical: false);
            else Messages.Message(line, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The term is taken back. The visits and the commissary were only ever read while the arc ran; the kept patch and the job slot are put away.</summary>
        private static void Withdraw(StoryArc arc, Map map)
        {
            GangDeal deal = arc.deal;
            if (map == null) return;
            switch (deal.term)
            {
                case GangDealTerm.Turf:
                    // (Only a claim the deal made, and only while it is still the gang's: a patch the gang held anyway is left to the daily verdict.)
                    if (deal.claimMade && arc.stake >= 0 && PrisonMap.Of(map) is { } pm && pm.turf.At(arc.stake)?.gang == deal.gang) pm.turf.Remove(arc.stake);
                    break;
                case GangDealTerm.Job:
                    // (Only the job the deal locked, if the warden has not changed it since: the member's own job and lock are put back, as they were before the deal.)
                    if (CourtUtil.FindInmate(map, deal.jobMemberId)?.Inmate() is { } mc && mc.job == deal.jobDef && mc.jobLocked)
                    {
                        mc.job = deal.prevJob;
                        mc.jobLocked = deal.prevJobLocked;
                    }
                    break;
            }
        }

        /// <summary>
        /// A seen fight or assault by this inmate: if their gang has a deal it is a breach. The first asks the warden (overlook it, or end the deal: see <see cref="GangDealArc"/>); the second ends the deal with no letter.
        /// While the first one's letter is unanswered nothing else counts (a gang's ambush, or a riot, records several members in the same hour: that is one breach, and the warden's answer to it is the point).
        /// </summary>
        public static void Notify_Breach(Pawn p, CompInmate c)
        {
            if (c.gang == null || Running.Count == 0 || !Running.TryGetValue(c.gang, out StoryArc arc)) return;
            if (arc.deal.breachOpen) return;
            Map map = p.MapHeld ?? Find.AnyPlayerHomeMap;
            arc.deal.breaches++;
            if (arc.deal.breaches >= 2) End(arc, GangDealEnd.Breach, map);
            else
            {
                arc.deal.breachOpen = true;
                ((GangDealArc)arc.def.Worker).AskBreach(arc, map, p);
            }
        }

        /// <summary>The press hears of the deal (once per deal): its letter, on this topic.</summary>
        public static void TellPress(StoryArc arc, Map map)
        {
            arc.deal.pressDone = true;
            IncidentWorker_PressInterest.Send(map, "RP_PressTopicGangDeal".Translate(arc.deal.leaderName, arc.deal.gang.label));
        }

        /// <summary>Every deal as one comparable string, and the clocks of the cooldowns (the saveload plan's snapshot).</summary>
        public static string Describe(PrisonGame g)
        {
            var sb = new StringBuilder();
            foreach (StoryArc a in g.story.arcs)
                if (a.def == RPGangDealDefOf.RP_Arc_GangDeal && a.deal is { } d)
                    sb.Append($"{a.id}:{a.done}:{a.startTick}:{a.stake}:{string.Join("+", a.pawns.ConvertAll(p => p.thingIDNumber))}:{d.gang?.defName}:{d.leaderId}:{d.leaderName}:{d.term}:{d.wing?.defName}:{d.untilTick}:{d.breaches}:{d.claimMade}:{d.jobMemberId}:{d.jobMemberName}:{d.jobDef?.defName}:{d.prevJob?.defName}:{d.prevJobLocked}:{d.breachOpen}:{d.pressDone};");
            foreach (KeyValuePair<GangDef, int> kv in g.story.gangDealEnded.OrderBy(kv => kv.Key.defName))
                sb.Append($"{kv.Key.defName}={kv.Value};");
            return sb.ToString();
        }
    }

    /// <summary>
    /// The deal's arc: a background story (it keeps nobody out of other stories) that lapses at its tenth day and ends when the leader is gone, no longer leads the gang, is off the wing it was struck on or is sent to an escort-only level. A breach asks the warden once
    /// (overlook it, or end the deal); the second breach ends the deal in <see cref="GangDeals.Notify_Breach"/> with no letter.
    /// </summary>
    public class GangDealArc : StoryArcWorker
    {
        private static readonly string[] Keys = { "RP_GangDeal_Overlook", "RP_GangDeal_End" };

        /// <summary>
        /// The deal is the leader's: it ends (as <see cref="GangDealEnd.Gone"/>) when the leader is gone, no longer leads, is no longer on the wing it was struck on (moved, reclassified) or is on a level that is escort-only.
        /// </summary>
        public override bool Valid(StoryArc arc)
        {
            if (arc.deal?.gang == null || !base.Valid(arc) || arc.A?.Inmate() is not { gangLeader: true } c || c.gang != arc.deal.gang || c.security is { escortOnly: true }) return false;
            return arc.A.MapHeld is not { } map || PrisonMap.Of(map) is not { } pm || GangDeals.WingOf(arc.A, pm) == arc.deal.wing;
        }

        public override void Hourly(StoryArc arc, Map map)
        {
            GangDeal deal = arc.deal;
            if (Find.TickManager.TicksGame >= deal.untilTick)
            {
                GangDeals.End(arc, GangDealEnd.Lapsed, map);
                return;
            }
            if (!deal.pressDone && Rand.Chance(GangDeals.PressChancePerDay / 24f)) GangDeals.TellPress(arc, map);
        }

        /// <summary>The leader is gone, has lost the gang, or is off the deal's wing: the deal ends (its terms are taken back).</summary>
        public override void Cancelled(StoryArc arc) => GangDeals.End(arc, GangDealEnd.Gone, null);

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        /// <summary>An unanswered letter is a deal overlooked: the warden did nothing.</summary>
        public override int TimeoutChoice(StoryArc arc) => 0;

        public override void Choose(StoryArc arc, Map map, int option)
        {
            if (arc.deal == null) return;
            arc.deal.breachOpen = false;
            if (option == 1) GangDeals.End(arc, GangDealEnd.Warden, map);
            else Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_GangDealOverlooked".Translate(arc.deal.leaderName, arc.deal.gang.label), arc.pawns.ToArray());
        }

        /// <summary>The first breach: a member of the gang was seen in a fight.</summary>
        public void AskBreach(StoryArc arc, Map map, Pawn offender)
        {
            GangDeal deal = arc.deal;
            AskWarden(arc, map, "RP_GangDealBreachLabel".Translate(deal.gang.LabelCap), "RP_GangDealBreachText".Translate(offender.LabelShort, deal.gang.label, deal.leaderName, GangDeals.TermText(map, arc)));
        }
    }

    /// <summary>The warden's letter with the terms (see <see cref="GangDeals.Offer"/>): the four terms and "not now"; the leader's dice follow the pick.</summary>
    public class GangDealOfferDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_GangDeal_Turf", "RP_GangDeal_Visits", "RP_GangDeal_Commissary", "RP_GangDeal_Job", "RP_GangDeal_NotNow" };

        private const int NotNow = 4;

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => NotNow;

        private static Pawn LeaderOf(ChoiceLetter_PrisonDecision l) => l.pawns.Count > 0 ? l.pawns[0] : null;

        /// <summary>Why this term cannot be taken now: the leader is out of reach, a deal runs or was lately over, or the term has nothing to give.</summary>
        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option == NotNow) return null;
            Pawn leader = LeaderOf(l);
            Map map = l.map ?? leader?.MapHeld;
            if (leader?.Inmate() is not { } c || map == null) return "RP_GangDealWhy_Gone".Translate().Resolve();
            return GangDeals.Blocker(map, leader, c) ?? GangDeals.TermWhy(map, leader, (GangDealTerm)option);
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option == NotNow) return;
            Pawn leader = LeaderOf(l);
            // (Looked at again: what was true when the letter came may not be now.)
            string why = leader == null ? "RP_GangDealWhy_Gone".Translate().Resolve() : Disabled(l, option);
            if (why != null)
            {
                Messages.Message(why, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            GangDeals.Answer(map, leader, (GangDealTerm)option, WardenSystem.InPost(map));
        }
    }
}
