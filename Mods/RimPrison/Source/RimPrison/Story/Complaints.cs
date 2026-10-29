using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPComplaintDefOf
    {
        public static ThoughtDef RP_ComplaintUpheldOnMe;
        public static ThoughtDef RP_ComplaintColleague;
        public static ThoughtDef RP_ComplaintDismissedOfficer;
        public static ThoughtDef RP_WardenListened;
        public static ThoughtDef RP_WardenListensWing;
        public static ThoughtDef RP_WardenIgnored;
        public static MisconductDef RP_FalseComplaint;
        /// <summary>The made-up reason on the file of an inmate a guard locked up for no reason.</summary>
        public static MisconductDef RP_Insult;

        static RPComplaintDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPComplaintDefOf));
    }

    /// <summary>What the inmate says the officer did (what the warden reads; whether it is true is not part of it).</summary>
    public enum ComplaintKind : byte { Force, Beating, Harassment, Punishment }

    public enum ComplaintStage : byte
    {
        /// <summary>Filed, but not yet on the warden's desk (a few hours).</summary>
        Filed,
        /// <summary>The warden's letter is out.</summary>
        Waiting,
        /// <summary>Under investigation until the due tick.</summary>
        Investigating,
        Closed,
    }

    public enum ComplaintOutcome : byte { None, Upheld, Dismissed, FoundFalse, NoFinding }

    /// <summary>
    /// One inmate's complaint against one officer, by id and name (both outlive the people). Whether it is true, and what the evidence is (a camera over the spot, who
    /// saw it) were fixed when it was filed and stay hidden until an investigation, a lawsuit or the warden's own decision shows it.
    /// </summary>
    public class Complaint : IExposable
    {
        public int id;
        public int inmateId = -1, officerId = -1;
        public string inmateName, officerName;
        public ComplaintKind kind;
        /// <summary>Hidden: the officer really did it (a false complaint is a lie, or a grievance about something they were right to do).</summary>
        public bool isTrue;
        public ComplaintStage stage;
        public ComplaintOutcome outcome;
        /// <summary>When the letter is due (Filed), when it went out (Waiting), when the investigation reports (Investigating).</summary>
        public int filedTick, dueTick, closedTick = -1;
        /// <summary>A lawsuit over a true complaint that was dismissed against an officer with a pattern is due at this tick (-1 none).</summary>
        public int lawsuitTick = -1;
        public IntVec3 spot;
        /// <summary>A camera covered the spot, and who was in sight of it (staff, inmates): what an investigation has to go on.</summary>
        public bool camera;
        public List<int> staffSaw = new List<int>(), inmatesSaw = new List<int>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref inmateId, "inmateId", -1);
            Scribe_Values.Look(ref officerId, "officerId", -1);
            Scribe_Values.Look(ref inmateName, "inmateName");
            Scribe_Values.Look(ref officerName, "officerName");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref isTrue, "isTrue");
            Scribe_Values.Look(ref stage, "stage");
            Scribe_Values.Look(ref outcome, "outcome");
            Scribe_Values.Look(ref filedTick, "filed");
            Scribe_Values.Look(ref dueTick, "due");
            Scribe_Values.Look(ref closedTick, "closed", -1);
            Scribe_Values.Look(ref lawsuitTick, "lawsuit", -1);
            Scribe_Values.Look(ref spot, "spot");
            Scribe_Values.Look(ref camera, "camera");
            Scribe_Collections.Look(ref staffSaw, "staffSaw", LookMode.Value);
            Scribe_Collections.Look(ref inmatesSaw, "inmatesSaw", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                staffSaw ??= new List<int>();
                inmatesSaw ??= new List<int>();
            }
        }

        public bool Open => stage != ComplaintStage.Closed;
    }

    /// <summary>
    /// Complaints against officers. An inmate hurt by an officer (a restraint that went too far, a beating by a guard with a brutal streak, harassment, a lock-up the policy did not
    /// allow) may file one; so may a manipulative or vindictive inmate who was merely searched, restrained or found with contraband, and who lies. The warden is asked: uphold (the
    /// officer is disciplined: a warning, then a day's pay docked, then a suspension of a few days at home, and the colleagues and the wing see it), dismiss (the inmate's tension rises, and
    /// their friends'; a true complaint against an officer with a pattern, dismissed, goes to court) or investigate (a day or two, with the officer stood down on full pay meanwhile and at
    /// most two investigations at once: the camera over the spot, who saw it and the officer's record decide what comes out, and then the truth is told as far as the evidence shows).
    /// Left unanswered a complaint is a soft dismissal. Officers remember (an upheld complaint slows promotion); inmates remember (an upheld complaint is trust in the warden, and the wing
    /// notices). Complaints are not internal affairs' business (that is corruption). What is true stays out of every letter, line and file until it comes out: a lie can be about any
    /// kind of thing (after a search or a frisk a hard word or a beating, never a restraint), can come with an old injury, and any guard on a bad day may abuse, so neither traits nor
    /// injuries nor the file settle which complaints are true.
    /// </summary>
    public static class ComplaintSystem
    {
        // Chances: that an incident becomes a complaint, that a lie is told.
        public const float FileForce = 0.5f, FileBeating = 0.65f, FileHarassment = 0.4f, FilePunishment = 0.7f, ManipulativeFileFactor = 1.3f;
        // (A frisk is lied about less than anything else: at 300 inmates, with a quarter of them bearing grudges, the lies come to about the number of true complaints a day.)
        public const float FalseAfterRestraint = 0.08f, FalseAfterFind = 0.25f, FalseAfterFind_Vindictive = 0.2f, FalseAfterSearch = 0.04f, FalseAnyone = 0.02f;
        // A lie sometimes comes with an injury from before (a fight earlier): an injury is not proof.
        public const float OldInjuryChance = 0.4f;
        // Force beyond need in a restraint, and the abuse of a guard with a brutal streak (per hour, times their brutality). Any other guard on a bad day (low morale) abuses at
        // BadDayAbuse times the hourly rate: now and then, whatever their traits.
        public const float RoughBase = 0.03f, RoughPerBrutality = 0.1f, AbuseFrom = 0.6f, AbusePerHour = 0.008f, AbuseWitnessedFactor = 0.3f, BadDayAbuse = 0.3f;
        public const int MaxWaiting = 2, CooldownDays = 3, ClosedKeepDays = 30, FileLineDays = 10, UpheldFadeDays = 30;
        public const int InvestigateMinDays = 1, InvestigateMaxDays = 2, UpheldPromotionDays = 8, MaxInvestigations = 2;
        /// <summary>A cell searched while its owner is within this many cells of the search (in it or nearby) may be complained of.</summary>
        public const float CellSearchNear = 10f;
        /// <summary>An unanswered letter is a soft dismissal: this share of the rise in tension, and the warden did not ignore anyone (they did not see it).</summary>
        public const float UnansweredFactor = 0.5f;
        public const float FindingAt = 0.4f, CameraEvidence = 0.5f, StaffEvidence = 0.25f, InmateEvidence = 0.15f, PatternEvidence = 0.2f, CleanOfficerEvidence = 0.1f, LiarEvidence = 0.1f;
        public const int Settlement = 1500;
        public const float SightRadius = 12f;

        private static int Now => Find.TickManager.TicksGame;

        // ---- lookups ---------------------------------------------------------------------------------------------

        public static Complaint ById(PrisonGame game, int id) => game?.story.complaints.Find(c => c.id == id);

        /// <summary>The inmate's open complaint (filed, waiting or being investigated), if any.</summary>
        public static Complaint OpenBy(PrisonGame game, int inmateId) => game.story.complaints.Find(c => c.Open && c.inmateId == inmateId);

        private static bool RecentlyClosed(PrisonGame game, int inmateId) =>
            game.story.complaints.Exists(c => !c.Open && c.inmateId == inmateId && Now - c.closedTick < CooldownDays * GenDate.TicksPerDay);

        private static int Waiting(PrisonGame game) => game.story.complaints.Count(c => c.stage == ComplaintStage.Waiting);

        public static string Allegation(Complaint c) => ("RP_ComplaintAllege_" + c.kind).Translate(c.officerName).Resolve();

        // ---- who does what -----------------------------------------------------------------------------------------

        /// <summary>A guard's streak: Bloodlust, Psychopath, Brawler and Abrasive make for rough handling, Kind for gentle; a demoralised guard is shorter-tempered. Under <see cref="AbuseFrom"/> they do not abuse, bar a bad day (<see cref="BadDay"/>).</summary>
        public static float Brutality(Pawn guard)
        {
            float b = 0f;
            if (guard.story?.traits is { } traits)
            {
                if (traits.HasTrait(TraitDefOf.Bloodlust)) b += 1f;
                if (traits.HasTrait(TraitDefOf.Psychopath)) b += 0.6f;
                if (traits.HasTrait(TraitDefOf.Brawler)) b += 0.4f;
                if (traits.HasTrait(TraitDefOf.Abrasive)) b += 0.4f;
                if (traits.HasTrait(TraitDefOf.Kind)) b -= 0.6f;
            }
            if (StaffMorale.Band(guard) == MoraleBand.Demoralised) b += 0.5f;
            return b;
        }

        /// <summary>A guard having a bad day (low or no morale, or burnt out and left to it): whatever their traits they may lose their temper with an inmate now and then.</summary>
        public static bool BadDay(Pawn guard) => StaffMorale.Band(guard) is MoraleBand.Demoralised or MoraleBand.Low || guard.GetComp<CompStaff>() is { } sc && StaffCareers.BurntOut(guard, sc);

        /// <summary>A manipulative inmate (an instigator, a jailhouse lawyer, an extortionist, a psychopath or an abrasive one) or a vindictive one (volatile, or hates this officer).</summary>
        public static bool Manipulative(Pawn p, CompInmate c) =>
            c.HasReputation(ReputationDefOf.RP_Instigator) || c.HasReputation(RPReputationDefOf.RP_JailhouseLawyer) || c.HasReputation(RPReputationDefOf.RP_Extortionist)
            || p.story?.traits is { } t && (t.HasTrait(TraitDefOf.Psychopath) || t.HasTrait(TraitDefOf.Abrasive));

        public static bool Vindictive(CompInmate c, Pawn officer) => c.HasReputation(ReputationDefOf.RP_Volatile) || StaffMemories.ScoreFor(c, officer) <= StaffMemories.HatedAt;

        internal static float FileChance(ComplaintKind kind, Pawn inmate, CompInmate c, Pawn officer)
        {
            float f = kind switch { ComplaintKind.Force => FileForce, ComplaintKind.Beating => FileBeating, ComplaintKind.Harassment => FileHarassment, _ => FilePunishment };
            if (Manipulative(inmate, c)) f *= ManipulativeFileFactor;
            if (c.HasReputation(ReputationDefOf.RP_ModelPrisoner)) f *= 0.5f;
            // Too frightened of what comes after.
            if (c.fear > 60f) f *= 0.6f;
            // Their wing's unit officer is someone they know: less often taken to the desk (true or not).
            f *= UnitOfficers.ComplaintFactor(officer, c);
            return Mathf.Clamp01(f);
        }

        // ---- what happens that may be complained of -------------------------------------------------------------------------

        /// <summary>Well enough to be roughed up: not someone already badly hurt (this is rough handling, or a threat made good, not a killing).</summary>
        internal static bool Fit(Pawn inmate) => !inmate.Dead && inmate.health.summaryHealth.SummaryHealthPercent >= 0.6f;

        private static bool Hurt(Pawn inmate, Pawn guard, int amount)
        {
            if (!Fit(inmate)) return false;
            // (No instigator: vanilla gives the victim a "Harmed me" memory naming whoever did the damage, which would show in the mood and social tabs which complaints are true,
            // as a lie's old injury has none. The guard's part is the complaint's own.)
            inmate.TakeDamage(new DamageInfo(DamageDefOf.Blunt, amount));
            return true;
        }

        /// <summary>An injury they had from before (a fight earlier), which a liar may show: it looks no different from one an officer gave.</summary>
        private static void OldInjury(Pawn inmate)
        {
            if (!Fit(inmate)) return;
            int before = inmate.health.hediffSet.hediffs.Count;
            inmate.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Rand.RangeInclusive(2, 6)));
            for (int i = before; i < inmate.health.hediffSet.hediffs.Count; i++)
                inmate.health.hediffSet.hediffs[i].ageTicks = Rand.RangeInclusive(GenDate.TicksPerDay / 2, 3 * GenDate.TicksPerDay);
        }

        /// <summary>A restraint went through (a successful subdue): maybe more force than was needed, maybe a liar's complaint about a restraint that was fine.</summary>
        public static void Notify_Restrained(Pawn guard, Pawn inmate, CompInmate c, bool wasAggro)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || c == null || guard.GetComp<CompStaff>() is not { role: not null, Protected: false }) return;
            float rough = RoughBase + RoughPerBrutality * Mathf.Max(0f, Brutality(guard));
            if (wasAggro) rough *= 0.5f;
            if (Rand.Chance(rough) && Hurt(inmate, guard, Rand.RangeInclusive(2, 6)))
            {
                StaffMemories.Note(c, guard, -10f);
                Notify_Incident(game, guard, inmate, ComplaintKind.Force);
                return;
            }
            if ((Manipulative(inmate, c) || Vindictive(c, guard)) && Rand.Chance(FalseAfterRestraint)) Lie(game, guard, inmate, c, afterSearch: false);
        }

        /// <summary>
        /// An officer searched an inmate (a frisk, or their cell turned over): found something or not. A manipulative or vindictive inmate may say the officer abused them. A cell
        /// search is complained of only by an owner who was in the cell or near it (<see cref="CellSearchNear"/>, as <paramref name="cellAt"/> says where the search was), and what
        /// there is to see of it is read at the cell. At most one lie per inmate per shakedown.
        /// </summary>
        public static void Notify_Searched(Pawn guard, Pawn inmate, bool found, IntVec3? cellAt = null)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || inmate.Inmate() is not { } c || guard.GetComp<CompStaff>() is not { role: not null, Protected: false }) return;
            if (cellAt is { } at && (inmate.MapHeld != guard.MapHeld || !inmate.PositionHeld.InHorDistOf(at, CellSearchNear))) return;
            if (LiedInThisShakedown(game, inmate.thingIDNumber)) return;
            bool grudge = Manipulative(inmate, c) || Vindictive(c, guard);
            float chance = !grudge ? FalseAnyone * (found ? 1f : 0f) : found ? (Manipulative(inmate, c) ? FalseAfterFind : FalseAfterFind_Vindictive) : FalseAfterSearch;
            if (chance > 0f && Rand.Chance(chance)) Lie(game, guard, inmate, c, afterSearch: true, cellAt);
        }

        /// <summary>This inmate already filed a lie during the shakedown that is on (a shakedown frisks everyone and turns every cell over: one lie each at most).</summary>
        private static bool LiedInThisShakedown(PrisonGame game, int inmateId) =>
            game.shakedownUntilTick > Now && game.story.complaints.Exists(x => !x.isTrue && x.inmateId == inmateId && x.filedTick >= game.shakedownUntilTick - PrisonGame.ShakedownHours * GenDate.TicksPerHour);

        /// <summary>What a liar says: after a restraint any of the four; after a search or a frisk a hard word or a beating, never a restraint.</summary>
        public static ComplaintKind LieKind(bool afterSearch)
        {
            float v = Rand.Value;
            if (afterSearch) return v < 0.6f ? ComplaintKind.Harassment : ComplaintKind.Beating;
            return v < 0.4f ? ComplaintKind.Force : v < 0.65f ? ComplaintKind.Harassment : v < 0.85f ? ComplaintKind.Beating : ComplaintKind.Punishment;
        }

        /// <summary>A lie, told (it still has to be filed: one inmate in a few does not bother). Sometimes with an old injury to show.</summary>
        internal static Complaint Lie(PrisonGame game, Pawn guard, Pawn inmate, CompInmate c, bool afterSearch, IntVec3? spot = null)
        {
            ComplaintKind kind = LieKind(afterSearch);
            if (!Rand.Chance(FileChance(kind, inmate, c, guard))) return null;
            Complaint cm = File(game, inmate.MapHeld ?? Find.AnyPlayerHomeMap, inmate, guard, kind, isTrue: false, spot);
            if (cm != null && Rand.Chance(OldInjuryChance)) OldInjury(inmate);
            return cm;
        }

        /// <summary>Something an officer really did to an inmate: they may complain.</summary>
        public static Complaint Notify_Incident(PrisonGame game, Pawn guard, Pawn inmate, ComplaintKind kind)
        {
            if (inmate.Inmate() is not { } c || !Rand.Chance(FileChance(kind, inmate, c, guard))) return null;
            return File(game, inmate.MapHeld ?? Find.AnyPlayerHomeMap, inmate, guard, kind, isTrue: true);
        }

        /// <summary>
        /// A guard with a brutal streak, out of other staff's sight more often than not, beats, harasses or locks up an inmate they have no business to; any other guard on a bad day does
        /// it now and then too. (Hourly. Not during a riot or a lockdown: the guards have other things to do.)
        /// </summary>
        public static void HourlyGuards(PrisonGame game, Map map)
        {
            // (Nor with someone held hostage: the guards have other things to do.)
            if (game.LockdownActive || RiotPolice.RiotOngoing(map) || HostageCrisis.Running(map) != null) return;
            // (A copy: what a guard does to an inmate runs the game's own damage and job code, which may change the colony's lists under the loop.)
            foreach (Pawn g in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                if (!GuardUtility.IsGuard(g) || g.GetComp<CompStaff>() is not { Protected: false } || !g.Awake() || g.Downed || g.InMentalState) continue;
                float b = Brutality(g);
                bool brute = b >= AbuseFrom;
                float streak = brute ? b : BadDay(g) ? BadDayAbuse : 0f;
                if (streak <= 0f || !Rand.Chance(AbusePerHour * streak)) continue;
                // (Rolled before looking: the camera and line-of-sight loop is the dear part.)
                if (Witnessed(g, map) && !Rand.Chance(AbuseWitnessedFactor)) continue;
                Pawn target = AbuseTarget(g, map, game);
                if (target == null) continue;
                ComplaintKind kind = Rand.Chance(brute ? (b >= 1f ? 0.5f : 0f) : 0.1f) ? ComplaintKind.Beating : Rand.Chance(0.7f) ? ComplaintKind.Harassment : ComplaintKind.Punishment;
                Abuse(game, g, target, kind);
            }
        }

        private static bool Witnessed(Pawn guard, Map map)
        {
            if (CourtUtil.CameraOver(map, guard.Position)) return true;
            foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                if (o != guard && o.GetComp<CompStaff>()?.role != null && o.Awake() && CourtUtil.InSight(o, guard.Position, map, SightRadius)) return true;
            return false;
        }

        /// <summary>Someone in the guard's sight who is theirs to abuse: not in a mental state (a riot, a hostage-taking), not in protective custody, not under lockdown, not already confined.</summary>
        internal static Pawn AbuseTarget(Pawn guard, Map map, PrisonGame game)
        {
            var weights = new List<(Pawn p, float w)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c && InmateUtility.IsActiveInmate(p) && c.forcedActivity == null && !p.Downed && !p.InMentalState && !Trials.IsProtected(c) && !game.LockedDown(c) && !game.story.InArc(p)
                    && CourtUtil.InSight(p, guard.Position, map, 6f))
                    weights.Add((p, 1f + Mathf.Max(0f, -StaffMemories.ScoreFor(c, guard) / 20f)));
            return weights.Count == 0 ? null : weights.RandomElementByWeight(x => x.w).p;
        }

        /// <summary>What a guard does to an inmate they should not have (also called directly by the checks). Returns the complaint if the inmate files one.</summary>
        public static Complaint Abuse(PrisonGame game, Pawn guard, Pawn inmate, ComplaintKind kind)
        {
            CompInmate c = inmate.Inmate();
            if (c == null) return null;
            switch (kind)
            {
                case ComplaintKind.Beating:
                    if (!Hurt(inmate, guard, Rand.RangeInclusive(4, 10))) return null;
                    FearSystem.Scare(c, 15f);
                    c.tension = Mathf.Min(100f, c.tension + 15f);
                    break;
                case ComplaintKind.Harassment:
                    FearSystem.Scare(c, 8f);
                    c.tension = Mathf.Min(100f, c.tension + 10f);
                    break;
                case ComplaintKind.Punishment:
                    // A lock-up nobody ordered, written up as an ordinary sanction for a made-up reason: the warden's own policy for that offence decides it (through the ordinary
                    // record, so the file, the chronicle, the statistics and the tension are what the real offence would have left), and the inmate's complaint is that it was without cause.
                    MisconductUtility.Record(inmate, c, PrisonMap.Of(inmate.MapHeld ?? guard.MapHeld), AbuseOffence(game), evidence: true);
                    break;
            }
            StaffMemories.Note(c, guard, kind == ComplaintKind.Beating ? -25f : -12f);
            return Notify_Incident(game, guard, inmate, kind);
        }

        /// <summary>
        /// The made-up offence an abuse lock-up is written up as: verbal abuse, if the warden's policy gives it a sanction; else the mildest ordinary offence (not an assault) whose policy
        /// is a lock-up, so the lock-up never shows as the one sanction nobody would have given.
        /// </summary>
        internal static MisconductDef AbuseOffence(PrisonGame game)
        {
            PunishmentPolicy policy = game.punishments;
            if (Gives(policy, RPComplaintDefOf.RP_Insult)) return RPComplaintDefOf.RP_Insult;
            MisconductDef best = null;
            foreach (MisconductDef d in DefDatabase<MisconductDef>.AllDefsListForReading)
            {
                if (d.severity > 2 || d.assault || d.baseWeight <= 0f || policy.KindFor(d) is not (Sanction.Solitary or Sanction.CellConfinement) || !Gives(policy, d)) continue;
                if (best == null || d.severity < best.severity || d.severity == best.severity && policy.HoursFor(d) < policy.HoursFor(best)) best = d;
            }
            return best ?? RPComplaintDefOf.RP_Insult;
        }

        /// <summary>The policy gives a sanction for this offence (what <see cref="Sanctions.Apply"/> would impose): hours, or a warning.</summary>
        private static bool Gives(PunishmentPolicy policy, MisconductDef def) => policy.KindFor(def) == Sanction.Warning || policy.HoursFor(def) > 0;

        // ---- filing --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Files a complaint (one open at a time per inmate, one in <see cref="CooldownDays"/> days): the evidence is read off the spot (where it happened: the inmate's own place, or the
        /// cell that was searched) as it is now. It reaches the warden's desk in a few hours.
        /// </summary>
        public static Complaint File(PrisonGame game, Map map, Pawn inmate, Pawn officer, ComplaintKind kind, bool isTrue, IntVec3? at = null)
        {
            if (map == null || inmate.Inmate() is not { } c || officer.GetComp<CompStaff>() is not { role: not null, Protected: false } sc) return null;
            if (OpenBy(game, inmate.thingIDNumber) != null || RecentlyClosed(game, inmate.thingIDNumber)) return null;
            var cm = new Complaint
            {
                id = game.story.nextComplaintId++, inmateId = inmate.thingIDNumber, inmateName = inmate.LabelShort, officerId = officer.thingIDNumber, officerName = officer.LabelShort,
                kind = kind, isTrue = isTrue, stage = ComplaintStage.Filed, filedTick = Now, dueTick = Now + Rand.RangeInclusive(2, 6) * GenDate.TicksPerHour, spot = at ?? inmate.Position,
            };
            cm.camera = CourtUtil.CameraOver(map, cm.spot);
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == inmate || p == officer || p.Dead || p.Downed || !p.RaceProps.Humanlike || !p.Awake() || !CourtUtil.InSight(p, cm.spot, map, SightRadius)) continue;
                if (p.IsPrisonerOfColony && InmateUtility.IsActiveInmate(p) && cm.inmatesSaw.Count < 3) cm.inmatesSaw.Add(p.thingIDNumber);
                else if (!p.IsPrisonerOfColony && p.GetComp<CompStaff>()?.role != null && cm.staffSaw.Count < 3) cm.staffSaw.Add(p.thingIDNumber);
            }
            game.story.complaints.Add(cm);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:complaint] {cm.inmateName} files against {cm.officerName} ({kind}, {(isTrue ? "true" : "false")}): camera {cm.camera}, staff {cm.staffSaw.Count}, inmates {cm.inmatesSaw.Count}");
            return cm;
        }

        // ---- the warden's desk -----------------------------------------------------------------------------------------

        /// <summary>Hourly: complaints reach the desk, investigations report, lawsuits are filed, old ones are forgotten.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            List<Complaint> list = game.story.complaints;
            if (list.Count == 0) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                Complaint cm = list[i];
                // On another of the player's home maps: that map's pass looks after it (this one would find nobody and drop it).
                if (CourtUtil.OnAnotherHomeMap(map, cm.inmateId)) continue;
                switch (cm.stage)
                {
                    case ComplaintStage.Filed:
                        if (Now < cm.dueTick) break;
                        if (Waiting(game) >= MaxWaiting) cm.dueTick += GenDate.TicksPerHour;
                        else Raise(game, map, cm);
                        break;
                    case ComplaintStage.Waiting:
                        // A letter that went missing is a letter nobody answered: a soft dismissal.
                        if (Now - cm.dueTick > 2 * GenDate.TicksPerDay && !LetterOpen(cm)) Decide(game, map, cm, 1, unanswered: true);
                        break;
                    case ComplaintStage.Investigating:
                        if (Now >= cm.dueTick) Conclude(game, map, cm);
                        break;
                    default:
                        if (cm.lawsuitTick >= 0 && Now >= cm.lawsuitTick) Lawsuit(game, map, cm);
                        else if (cm.lawsuitTick < 0 && Now - cm.closedTick > ClosedKeepDays * GenDate.TicksPerDay) list.RemoveAt(i);
                        break;
                }
            }
        }

        private static bool LetterOpen(Complaint cm)
        {
            // (A letter held back for the next day, over the budget of decisions, is open: nobody has had the chance to answer it.)
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is("RP_Decision_Complaint") && d.amount == cm.id) return true;
            return false;
        }

        /// <summary>
        /// The warden on their walk meets an inmate whose complaint is filed and not yet on the desk: they say it to the warden's face, and it is on the desk now (the same letter, the same words,
        /// nothing of whether it is so). Returns whether one was raised.
        /// </summary>
        public static bool RaiseInPerson(PrisonGame game, Map map, Pawn inmate)
        {
            foreach (Complaint cm in game.story.complaints)
            {
                if (cm.stage != ComplaintStage.Filed || cm.inmateId != inmate.thingIDNumber) continue;
                // (As on the hourly pass, not past the letters already waiting.)
                if (Waiting(game) >= MaxWaiting) return false;
                Raise(game, map, cm);
                return cm.stage == ComplaintStage.Waiting;
            }
            return false;
        }

        /// <summary>The letter: who complains of whom, and what they say. Nothing of whether it is so.</summary>
        private static void Raise(PrisonGame game, Map map, Complaint cm)
        {
            Pawn inmate = CourtUtil.FindInmate(map, cm.inmateId), officer = CourtUtil.FindStaff(map, cm.officerId);
            if (inmate == null || officer == null || !InmateUtility.IsActiveInmate(inmate))
            {
                game.story.complaints.Remove(cm);
                return;
            }
            cm.stage = ComplaintStage.Waiting;
            cm.dueTick = Now;
            game.stats.complaintsFiled++;
            Concepts.Teach(Concepts.Complaint);
            // (Their count of true complaints, a permanent pattern whatever came of it: counted when it reaches the desk, not when it is filed and may yet be dropped.)
            if (cm.isTrue && officer.GetComp<CompStaff>() is { } sc) sc.trueComplaints++;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Complaint", "RP_ComplaintLabel".Translate(cm.inmateName, cm.officerName), LetterText(game, map, cm), map, new LookTargets(inmate));
            letter.amount = cm.id;
            letter.pawns.Add(inmate);
            letter.pawns.Add(officer);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>What the letter says (a function of the complaint's public part only: the same words for a true complaint and a false one).</summary>
        public static string LetterText(PrisonGame game, Map map, Complaint cm)
        {
            CompStaff sc = CourtUtil.FindStaff(map, cm.officerId)?.GetComp<CompStaff>();
            int before = game.story.complaints.Count(x => x != cm && x.inmateId == cm.inmateId);
            int found = game.story.complaints.Count(x => x != cm && x.inmateId == cm.inmateId && x.outcome == ComplaintOutcome.FoundFalse);
            string officerLine = sc != null && sc.complaintsUpheld > 0 ? "\n" + "RP_ComplaintOfficerRecord".Translate(cm.officerName, sc.complaintsUpheld).Resolve() : "";
            string inmateLine = before > 0 ? "\n" + "RP_ComplaintInmateRecord".Translate(cm.inmateName, before, found).Resolve() : "";
            return "RP_ComplaintText".Translate(cm.inmateName, Allegation(cm), officerLine + inmateLine, cm.officerName).Resolve();
        }

        /// <summary>The warden's answer: 0 uphold, 1 dismiss, 2 investigate. <paramref name="unanswered"/>: nobody answered the letter in time (a soft dismissal).</summary>
        public static void Decide(PrisonGame game, Map map, Complaint cm, int option, bool unanswered = false)
        {
            if (cm.stage != ComplaintStage.Waiting && cm.stage != ComplaintStage.Filed) return;
            switch (option)
            {
                case 0:
                    Uphold(game, map, cm, "RP_Chr_ComplaintUpheld");
                    break;
                case 2:
                    if (CannotInvestigate(game, map, cm) == null) Investigate(game, map, cm);
                    break;
                default:
                    Dismiss(game, map, cm, noFinding: false, unanswered);
                    break;
            }
        }

        private static int Investigating(PrisonGame game) => game.story.complaints.Count(c => c.stage == ComplaintStage.Investigating);

        /// <summary>Why the warden cannot send this one for investigation now (a keyed phrase, resolved), or null: nobody left to question, or two investigations already under way.</summary>
        public static string CannotInvestigate(PrisonGame game, Map map, Complaint cm)
        {
            if (map == null || CourtUtil.FindInmate(map, cm.inmateId) == null || CourtUtil.FindStaff(map, cm.officerId) == null) return "RP_ComplaintGone".Translate().Resolve();
            if (Investigating(game) >= MaxInvestigations) return "RP_ComplaintInvestigationsFull".Translate(MaxInvestigations).Resolve();
            // (An officer stood down is home on full pay until it reports: not the only guard on their shifts.)
            return CorruptionSystem.CanSpare(CourtUtil.FindStaff(map, cm.officerId), map) ? null : "RP_ComplaintNoCover".Translate(cm.officerName).Resolve();
        }

        private static void Close(Complaint cm, ComplaintOutcome outcome)
        {
            cm.stage = ComplaintStage.Closed;
            cm.outcome = outcome;
            cm.closedTick = Now;
        }

        /// <summary>The complaint is upheld (by the warden, or by what an investigation found): the officer is disciplined, the colleagues and the wing see it, the inmate is heard.</summary>
        public static string Uphold(PrisonGame game, Map map, Complaint cm, string chronicleKey)
        {
            Close(cm, ComplaintOutcome.Upheld);
            game.stats.complaintsUpheld++;
            Pawn inmate = CourtUtil.FindInmate(map, cm.inmateId), officer = CourtUtil.FindStaff(map, cm.officerId);
            // (An officer who has gone cannot be disciplined: the inmate is heard all the same.)
            string how = "RP_DisciplineGone".Translate().Resolve();
            if (officer?.GetComp<CompStaff>() is { role: not null } sc) how = Discipline(game, map, officer, sc);
            if (inmate?.Inmate() is { } c)
            {
                c.tension = Mathf.Max(0f, c.tension - 15f);
                inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPComplaintDefOf.RP_WardenListened);
                // The wing notices: their friends, and those who have had trouble with the same officer.
                foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
                    if (o != inmate && o.Inmate() is { } oc && (PrisonRelations.Friends(o, inmate) || officer != null && StaffMemories.ScoreFor(oc, officer) <= -20f))
                    {
                        o.needs?.mood?.thoughts.memories.TryGainMemory(RPComplaintDefOf.RP_WardenListensWing);
                        oc.tension = Mathf.Max(0f, oc.tension - 3f);
                    }
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, chronicleKey.Translate(cm.inmateName, cm.officerName, how), inmate, officer);
            return how;
        }

        /// <summary>
        /// A warning on the record, then a day's pay docked (the prison keeps it: wages not paid, not money made, and no more than once a month), then days at home (a disciplinary suspension of
        /// its own: it is no business of internal affairs, which looks for corruption), with the colleagues' mood and the officer's own.
        /// </summary>
        private static string Discipline(PrisonGame game, Map map, Pawn officer, CompStaff sc)
        {
            int prior = sc.complaintsUpheld;
            sc.complaintsUpheld++;
            sc.lastUpheldTick = Now;
            string how;
            if (prior == 0) how = "RP_Discipline_Warning".Translate().Resolve();
            else if (prior == 1)
            {
                int fine = StaffCareers.Wage(sc);
                game.ledger.Add(RPDefOf.RP_Wages, fine, "RP_DisciplineFineNote".Translate(officer.LabelShort));
                how = "RP_Discipline_Fine".Translate(Money.Format(fine)).Resolve();
            }
            else if (!CorruptionSystem.CanSpare(officer, map))
            {
                // The only guard on some of their hours is not sent home: the pay is docked instead.
                int fine = StaffCareers.Wage(sc);
                game.ledger.Add(RPDefOf.RP_Wages, fine, "RP_DisciplineFineNote".Translate(officer.LabelShort));
                how = "RP_Discipline_FineNoCover".Translate(Money.Format(fine)).Resolve();
            }
            else
            {
                CorruptionSystem.Discipline(officer, sc);
                how = "RP_Discipline_Suspended".Translate(CorruptionSystem.SuspensionDays).Resolve();
            }
            officer.needs?.mood?.thoughts.memories.TryGainMemory(RPComplaintDefOf.RP_ComplaintUpheldOnMe);
            foreach (Pawn o in StaffCommute.AllStaff(map))
                if (o != officer) o.needs?.mood?.thoughts.memories.TryGainMemory(RPComplaintDefOf.RP_ComplaintColleague);
            return how;
        }

        /// <summary>
        /// Not upheld (the warden's word, for want of evidence, or because nobody answered the letter: the last two are softer): the inmate's tension rises, and their friends'. A true
        /// complaint against an officer with a pattern goes to court.
        /// </summary>
        public static void Dismiss(PrisonGame game, Map map, Complaint cm, bool noFinding, bool unanswered = false)
        {
            Close(cm, noFinding ? ComplaintOutcome.NoFinding : ComplaintOutcome.Dismissed);
            game.stats.complaintsDismissed++;
            Pawn inmate = CourtUtil.FindInmate(map, cm.inmateId), officer = CourtUtil.FindStaff(map, cm.officerId);
            float rise = noFinding ? 8f : unanswered ? 15f * UnansweredFactor : 15f;
            if (inmate?.Inmate() is { } c)
            {
                c.tension = Mathf.Min(100f, c.tension + rise);
                c.mainCause = "RP_Cause_Complaint";
                if (!noFinding && !unanswered) inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPComplaintDefOf.RP_WardenIgnored);
                foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
                    if (o != inmate && o.Inmate() is { } oc && PrisonRelations.Friends(o, inmate)) oc.tension = Mathf.Min(100f, oc.tension + rise / 2f);
            }
            officer?.needs?.mood?.thoughts.memories.TryGainMemory(RPComplaintDefOf.RP_ComplaintDismissedOfficer);
            // True, and not the first time: the inmate takes it to court (a dismissal the warden chose, or a letter nobody answered: an investigation that found nothing is no dismissal, the warden did what a
            // warden should, and a suit whose letter says "you had dismissed it" would punish that).
            if (!noFinding && cm.isTrue && officer?.GetComp<CompStaff>() is { trueComplaints: >= 2 }) cm.lawsuitTick = Now + Rand.RangeInclusive(1, 2) * GenDate.TicksPerDay;
        }

        /// <summary>The warden sends it for investigation: a day or two, with the officer stood down (home on full pay) until it reports.</summary>
        public static void Investigate(PrisonGame game, Map map, Complaint cm)
        {
            cm.stage = ComplaintStage.Investigating;
            cm.dueTick = Now + Rand.RangeInclusive(InvestigateMinDays, InvestigateMaxDays) * GenDate.TicksPerDay;
            if (CourtUtil.FindStaff(map, cm.officerId) is { } officer && officer.GetComp<CompStaff>() is { } sc) CorruptionSystem.StandDown(officer, sc, cm.dueTick);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_ComplaintInvestigating".Translate(cm.inmateName, cm.officerName));
        }

        // ---- the investigation --------------------------------------------------------------------------------------------

        /// <summary>
        /// What an investigation has to go on: the camera over the spot, the staff and inmates who were in sight (still here), the officer's record (a pattern backs a true complaint, a
        /// clean record backs the officer against a liar), the complainant's own, and kinship (<see cref="Kin.ComplaintShift"/>). Lines say what was looked at, the same whatever is true.
        /// </summary>
        public static float Evidence(Complaint cm, Map map, List<string> lines = null)
        {
            Pawn officer = CourtUtil.FindStaff(map, cm.officerId);
            CompStaff sc = officer?.GetComp<CompStaff>();
            Pawn inmate = CourtUtil.FindInmate(map, cm.inmateId);
            float e = 0f;
            if (cm.camera)
            {
                e += CameraEvidence;
                lines?.Add("RP_ComplaintLook_Camera".Translate().Resolve());
            }
            var staffNames = new List<string>();
            foreach (int id in cm.staffSaw)
                if (CourtUtil.FindStaff(map, id) is { } s) staffNames.Add(s.LabelShort);
            e += Mathf.Min(2, staffNames.Count) * StaffEvidence;
            if (staffNames.Count > 0) lines?.Add("RP_ComplaintLook_Staff".Translate(string.Join(", ", staffNames)).Resolve());
            var inmateNames = new List<string>();
            foreach (int id in cm.inmatesSaw)
                if (CourtUtil.FindInmate(map, id) is { } p) inmateNames.Add(p.LabelShort);
            e += Mathf.Min(3, inmateNames.Count) * InmateEvidence;
            if (inmateNames.Count > 0) lines?.Add("RP_ComplaintLook_Inmates".Translate(string.Join(", ", inmateNames)).Resolve());
            if (sc != null)
            {
                if (sc.complaintsUpheld > 0) lines?.Add("RP_ComplaintLook_Record".Translate(cm.officerName, sc.complaintsUpheld).Resolve());
                if (cm.isTrue && sc.trueComplaints >= 2) e += PatternEvidence;
                else if (!cm.isTrue && sc.trueComplaints == 0 && sc.complaintsUpheld == 0) e += CleanOfficerEvidence;
            }
            // A liar's tells: a manipulative complainant who has lied before, a model prisoner who has not.
            if (inmate?.Inmate() is { } c)
            {
                if (!cm.isTrue && Manipulative(inmate, c)) e += LiarEvidence;
                if (cm.isTrue && c.HasReputation(ReputationDefOf.RP_ModelPrisoner)) e += LiarEvidence;
            }
            // Kin: an officer who is kin of the complainant's enemy had a motive (a true complaint is easier to believe), one accused of abusing their own kin is not thought to have (it is harder). See Kin.
            if (officer != null && inmate != null) e = Mathf.Max(0f, e + Kin.ComplaintShift(cm.isTrue, officer, inmate, lines));
            return e;
        }

        /// <summary>The investigation reports: it finds the truth (upheld, or the complaint false: the inmate is put on report) when the evidence is enough, and otherwise nothing, which is a dismissal.</summary>
        public static void Conclude(PrisonGame game, Map map, Complaint cm)
        {
            // The officer is back from the stand-down (unless another complaint against them is still being looked into).
            if (CourtUtil.FindStaff(map, cm.officerId)?.GetComp<CompStaff>() is { } standingDown && !game.story.complaints.Exists(x => x != cm && x.stage == ComplaintStage.Investigating && x.officerId == cm.officerId))
                standingDown.standDownUntilTick = -1;
            var lines = new List<string>();
            float e = Evidence(cm, map, lines);
            string looked = lines.Count > 0 ? CourtUtil.Bullets(lines) : "RP_ComplaintLook_Nothing".Translate().Resolve();
            Pawn inmate = CourtUtil.FindInmate(map, cm.inmateId);
            if (e < FindingAt)
            {
                Dismiss(game, map, cm, noFinding: true);
                Find.LetterStack.ReceiveLetter("RP_ComplaintResultLabel".Translate(cm.inmateName, cm.officerName), "RP_ComplaintResultNone".Translate(cm.inmateName, cm.officerName, looked),
                    LetterDefOf.NeutralEvent, inmate);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_ComplaintNoFinding".Translate(cm.inmateName, cm.officerName), inmate);
                return;
            }
            if (cm.isTrue)
            {
                string how = Uphold(game, map, cm, "RP_Chr_ComplaintSubstantiated");
                Find.LetterStack.ReceiveLetter("RP_ComplaintResultLabel".Translate(cm.inmateName, cm.officerName),
                    "RP_ComplaintResultTrue".Translate(cm.inmateName, cm.officerName, looked, how), LetterDefOf.PositiveEvent, inmate);
                return;
            }
            // False, and found out: the officer is cleared, the inmate is on report for it.
            Close(cm, ComplaintOutcome.FoundFalse);
            game.stats.complaintsFalseFound++;
            Pawn officer = CourtUtil.FindStaff(map, cm.officerId);
            officer?.needs?.mood?.thoughts.memories.TryGainMemory(RPCorruptionDefOf.RP_Exonerated);
            if (inmate?.Inmate() is { } c)
            {
                MisconductUtility.Record(inmate, c, PrisonMap.Of(map), RPComplaintDefOf.RP_FalseComplaint, evidence: true, chronicle: false);
                c.tension = Mathf.Min(100f, c.tension + 5f);
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_ComplaintFalse".Translate(cm.inmateName, cm.officerName), inmate, officer);
            Find.LetterStack.ReceiveLetter("RP_ComplaintResultLabel".Translate(cm.inmateName, cm.officerName), "RP_ComplaintResultFalse".Translate(cm.inmateName, cm.officerName, looked),
                LetterDefOf.PositiveEvent, inmate);
        }

        // ---- the court --------------------------------------------------------------------------------------------------------

        /// <summary>A true complaint, dismissed, against an officer who had one before: the inmate's lawyer takes it to court, and the truth comes out.</summary>
        public static void Lawsuit(PrisonGame game, Map map, Complaint cm)
        {
            cm.lawsuitTick = -1;
            game.ledger.Add(RPDefOf.RP_Fines, -Settlement, "RP_ComplaintLawsuitNote".Translate(cm.inmateName));
            game.reputation = Mathf.Max(0f, game.reputation - CourtUtil.ReputationHit);
            game.stats.lawsuits++;
            Pawn inmate = CourtUtil.FindInmate(map, cm.inmateId);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_ComplaintLawsuit".Translate(cm.inmateName, cm.officerName), inmate);
            Find.LetterStack.ReceiveLetter("RP_ComplaintLawsuitLabel".Translate(cm.inmateName), "RP_ComplaintLawsuitText".Translate(cm.inmateName, cm.officerName, Money.Format(Settlement)), LetterDefOf.NegativeEvent, inmate);
        }

        // ---- officers and inmates remember --------------------------------------------------------------------------------------

        /// <summary>Daily: an upheld complaint fades from an officer's record after a month without another.</summary>
        public static void Daily(Map map)
        {
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>() is { complaintsUpheld: > 0 } sc && Now - sc.lastUpheldTick >= UpheldFadeDays * GenDate.TicksPerDay)
                {
                    sc.complaintsUpheld--;
                    sc.lastUpheldTick = Now;
                }
        }

        /// <summary>Days an officer's promotion is put off by the complaints against them that were upheld.</summary>
        public static int PromotionDelayDays(CompStaff sc) => sc.complaintsUpheld * UpheldPromotionDays;

        /// <summary>For the officer's record: upheld complaints, and any that are open.</summary>
        public static string StaffLine(Pawn officer, CompStaff sc)
        {
            PrisonGame game = PrisonGame.Instance;
            int open = game == null ? 0 : game.story.complaints.Count(c => c.Open && c.officerId == officer.thingIDNumber && c.stage != ComplaintStage.Filed);
            string line = "";
            if (sc.complaintsUpheld > 0) line += "\n" + "RP_StaffComplaintsUpheld".Translate(sc.complaintsUpheld).Resolve();
            if (open > 0) line += "\n" + "RP_StaffComplaintsOpen".Translate(open).Resolve();
            return line;
        }

        /// <summary>For the inmate's file: a complaint of theirs that is open, or was decided in the last days (what was decided, not what was true).</summary>
        public static IEnumerable<string> FileLines(Pawn p)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || game.story.complaints.Count == 0) yield break;
            foreach (Complaint cm in game.story.complaints)
            {
                if (cm.inmateId != p.thingIDNumber) continue;
                if (cm.stage == ComplaintStage.Filed) continue;
                if (cm.stage == ComplaintStage.Waiting) yield return "RP_FileComplaintWaiting".Translate(cm.officerName).Resolve();
                else if (cm.stage == ComplaintStage.Investigating)
                    yield return "RP_FileComplaintInvestigating".Translate(cm.officerName, Mathf.Max(0, (cm.dueTick - Now) / GenDate.TicksPerHour)).Resolve();
                else if (Now - cm.closedTick < FileLineDays * GenDate.TicksPerDay)
                    yield return ("RP_FileComplaint_" + cm.outcome).Translate(cm.officerName, Trials.DayOf(cm.closedTick)).Resolve();
            }
        }

        /// <summary>Everything saved about the complaints in one string (the saveload plan compares it).</summary>
        public static string Describe(Complaint c) =>
            $"{c.id}:{c.inmateId}:{c.inmateName}:{c.officerId}:{c.officerName}:{c.kind}{c.isTrue}{c.camera}:{c.stage}:{c.outcome}:{c.filedTick}:{c.dueTick}:{c.closedTick}:{c.lawsuitTick}:{c.spot.x},{c.spot.z}:{string.Join("+", c.staffSaw)}:{string.Join("+", c.inmatesSaw)}";
    }

    /// <summary>Uphold, dismiss or investigate. Unanswered: a soft dismissal.</summary>
    public class PrisonDecisionWorker_Complaint : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Complaint_Uphold", "RP_Complaint_Dismiss", "RP_Complaint_Investigate" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            Complaint cm = ComplaintSystem.ById(PrisonGame.Instance, l.amount);
            if (cm == null || cm.stage != ComplaintStage.Waiting) return "RP_ComplaintDecided".Translate().Resolve();
            return option == 2 ? ComplaintSystem.CannotInvestigate(PrisonGame.Instance, l.map ?? Find.AnyPlayerHomeMap, cm) : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            // (A dismissal the letter's timeout made is a letter nobody answered.)
            if (ComplaintSystem.ById(game, l.amount) is { stage: ComplaintStage.Waiting } cm) ComplaintSystem.Decide(game, map, cm, option, unanswered: option == 1 && l.TimeoutActive && l.TimeoutPassed);
        }
    }
}
