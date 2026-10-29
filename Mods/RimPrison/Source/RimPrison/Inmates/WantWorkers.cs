using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// What one kind of goal means: who can want it, how far along they are (read from the systems that feed it), what
    /// it needs, and how it reads in their file. The base class is the plumbing: a deadline a few days out, retried the same way.
    /// </summary>
    public class InmateWantWorker
    {
        public InmateWantDef def;

        protected static int Now => Find.TickManager.TicksGame;

        /// <summary>Could they want this now?</summary>
        public virtual bool CanForm(Pawn p, CompInmate c) => true;

        /// <summary>How much they want it, among the goals that fit them (the def's commonality, bent by who they are).</summary>
        public virtual float Weight(Pawn p, CompInmate c) => def.commonality;

        /// <summary>False: it makes no sense any more (the sentence became life): it is dropped.</summary>
        public virtual bool StillApplies(Pawn p, CompInmate c, InmateWant w) => true;

        /// <summary>Sets up a new goal: its deadline, and what it is about.</summary>
        public virtual void Begin(Pawn p, CompInmate c, InmateWant w) => w.deadlineTick = Now + def.durationDays * GenDate.TicksPerDay;

        /// <summary>A setback it survives, or a missed deadline: start again.</summary>
        public virtual void Renew(Pawn p, CompInmate c, InmateWant w)
        {
            // Progress starts over, so do the milestones (a goal whose progress is read from elsewhere keeps them: see the parole goal).
            w.progress = 0f;
            w.halfway = w.slipLogged = false;
            Begin(p, c, w);
        }

        public virtual bool CanRenew(Pawn p, CompInmate c, InmateWant w) => true;

        /// <summary>Reads the systems: sets progress and status, and reaches or loses the goal when it is clear.</summary>
        public virtual void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
        }

        public virtual void TimedOut(Pawn p, CompInmate c, InmateWant w) => Wants.Thwart(p, c, w, Wants.TimeOutReason);

        /// <summary>Called when the goal is over, reached or given up (or put aside for another), before the chronicle line of a loss.</summary>
        public virtual void Ended(Pawn p, CompInmate c, InmateWant w)
        {
        }

        /// <summary>Whether the halfway mark is worth a line (a grudge only simmers: halfway through the days is nothing to tell).</summary>
        public virtual bool TellsHalfway => true;

        /// <summary>The key of the file's tooltip: what the goal is, and the day it is to be reached by.</summary>
        public virtual string DeadlineTipKey => "RP_FileWantTip";

        /// <summary>Called once the goal is reached (after the chronicle line).</summary>
        public virtual void Fulfilled(Pawn p, CompInmate c, InmateWant w)
        {
        }

        /// <summary>The line under the progress bar in the file: what is needed, what stands in the way.</summary>
        public virtual TaggedString Detail(Pawn p, CompInmate c, InmateWant w) => "";

        /// <summary>
        /// The warden's word backs this goal (see <see cref="WardenSystem"/>; offered only for a goal the warden knows of, once per goal): the days it takes are given more time, which lifts one
        /// that was slipping. Returns the keyed line the warden reads (the goal's title is its first argument).
        /// </summary>
        public virtual string Encourage(Pawn p, CompInmate c, InmateWant w, Pawn warden)
        {
            w.backed = true;
            if (w.deadlineTick >= 0) w.deadlineTick += Wants.BackedDays * GenDate.TicksPerDay;
            Wants.Refresh(p, c);
            return "RP_Want_Backed";
        }

        /// <summary>A goal on track makes them steer clear of trouble that would be on their record (x0.8 for the serious).</summary>
        public virtual float MisconductFactor(MisconductDef m, CompInmate c, InmateWant w) =>
            w.status == WantStatus.OnTrack && m.severity >= 2 ? Wants.AvoidFactor : 1f;

        /// <summary>Less than half the time left and not a quarter of the way: it is slipping.</summary>
        protected bool Behind(InmateWant w, float minProgress) =>
            w.deadlineTick >= 0 && w.deadlineTick - Now < def.durationDays * GenDate.TicksPerDay / 2 && w.progress < minProgress;

        /// <summary>The map the inmate is on (or held on); null for one on none (generated for a shuttle not yet landed). Never the map the player is looking at: game logic must not depend on the camera.</summary>
        protected static Map MapOf(Pawn p) => p.MapHeld;

        protected static bool HasTrait(Pawn p, TraitDef t) => t != null && p.story?.traits?.HasTrait(t) == true;
    }

    /// <summary>Make parole: be out at the hearing. Progress is how much of the sentence is served and how good the board's odds look.</summary>
    public class WantWorker_Parole : InmateWantWorker
    {
        /// <summary>The board's odds (see <see cref="ParoleSystem.ParoleChance"/>) that count as on track, and as all they could ask for.</summary>
        public const float GoodChance = 0.35f, FullChance = 0.6f;
        public const int RecentDays = 5;

        public static int EligibleTick(CompInmate c) => c.admittedTick + Mathf.RoundToInt(c.TermDays * 0.5f * GenDate.TicksPerDay);

        /// <summary>When the next hearing can be held (the earliest).</summary>
        public static int HearingTick(CompInmate c) => Mathf.Max(EligibleTick(c), c.lastHearingTick + ParoleSystem.HearingGapDays * GenDate.TicksPerDay);

        private static bool Parolable(CompInmate c) => !c.IsLifer && c.sentenceDays > 0 && c.admittedTick >= 0 && DeathRowSystem.Ext(c) == null;

        public override bool CanForm(Pawn p, CompInmate c) => Parolable(c);

        public override bool StillApplies(Pawn p, CompInmate c, InmateWant w) => Parolable(c);

        public override float Weight(Pawn p, CompInmate c)
        {
            float weight = def.commonality;
            if (c.crime is { violent: true }) weight *= 0.7f;
            if (c.HasReputation(ReputationDefOf.RP_ModelPrisoner)) weight *= 1.3f;
            return weight;
        }

        public override void Begin(Pawn p, CompInmate c, InmateWant w) => w.deadlineTick = Mathf.Max(Now, HearingTick(c)) + def.durationDays * GenDate.TicksPerDay;

        /// <summary>The next hearing, a new deadline. Progress and standing are the sentence served and the board's odds, not something a refusal resets: no milestone is told again.</summary>
        public override void Renew(Pawn p, CompInmate c, InmateWant w) => Begin(p, c, w);

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            float chance = ParoleSystem.ParoleChance(c);
            float served = Mathf.Clamp01(c.DaysServed / Mathf.Max(1f, c.TermDays * 0.5f));
            // (Never full before they are out.)
            w.progress = Mathf.Min(0.95f, 0.5f * served + 0.5f * Mathf.Clamp01(chance / FullChance));
            w.status = chance >= GoodChance ? WantStatus.OnTrack : WantStatus.Slipping;
        }

        /// <summary>What stands between them and a good hearing, in words ("a clean sheet and one more program").</summary>
        public static List<string> Needs(CompInmate c)
        {
            var needs = new List<string>();
            int now = Now;
            foreach (MisconductRecord r in c.records)
                if (r.seen && now - r.tick < GenDate.TicksPerDay * RecentDays && r.def.severity >= 2)
                {
                    needs.Add("RP_Want_Need_CleanSheet".Translate());
                    break;
                }
            if (c.tension > 50f) needs.Add("RP_Want_Need_Calm".Translate());
            if (ParoleSystem.ParoleChance(c) < FullChance)
            {
                needs.Add("RP_Want_Need_Program".Translate());
                if (c.hoursWorked < 100) needs.Add("RP_Want_Need_Work".Translate());
            }
            return needs;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w)
        {
            int day = Mathf.Max(Now, HearingTick(c)) / GenDate.TicksPerDay + 1;
            List<string> needs = Needs(c);
            return needs.Count == 0 ? "RP_Want_Parole_DetailGood".Translate(day) : "RP_Want_Parole_Detail".Translate(GenText.ToCommaList(needs, useAnd: true), day);
        }
    }

    /// <summary>
    /// Prove the board wrong: after a refusal with reasons, show the board a better hearing. Progress is how far their odds there have risen since the
    /// refusal (<see cref="Gain"/> is all they ask); a serious incident seen on their record ends it for good, and a window that passes is let go.
    /// Reaching it earns the next hearing a small lift (a speaker on the letter).
    /// </summary>
    public class WantWorker_ProveWrong : InmateWantWorker
    {
        /// <summary>The rise in the board's odds that proves it.</summary>
        public const float Gain = 0.12f;

        private static bool Parolable(CompInmate c) => !c.IsLifer && c.sentenceDays > 0 && c.admittedTick >= 0 && DeathRowSystem.Ext(c) == null && c.paroleDenialTick >= 0;

        public override bool CanForm(Pawn p, CompInmate c) => Parolable(c);

        public override bool StillApplies(Pawn p, CompInmate c, InmateWant w) => Parolable(c);

        /// <summary>
        /// What the inmate has made of themselves, for the board's odds: the file without the serious incidents of the last five days (they age out of
        /// the board's odds by themselves, and a goal that is met by waiting for that is not a goal).
        /// </summary>
        private static float Standing(CompInmate c) => ParoleSystem.ParoleChance(c, recentIncidents: false);

        /// <summary>The reason they were refused for: the first of those on the file.</summary>
        private static string Note(CompInmate c) => c.paroleDenial?.Split(',')[0].Trim();

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            w.baseline = Standing(c);
            w.note = Note(c);
        }

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            // Another serious incident, seen, since they set out: that is the end of it (no second try).
            if (c.records.Exists(r => r.seen && r.tick >= w.formedTick && r.def.severity >= 2))
            {
                Wants.Thwart(p, c, w, "RP_Want_Reason_ProveWrongRelapse", final: true);
                return;
            }
            float gain = Standing(c) - w.baseline;
            if (gain >= Gain)
            {
                Wants.Fulfil(p, c, w);
                return;
            }
            w.progress = Mathf.Clamp01(gain / Gain);
            w.status = Behind(w, 0.25f) ? WantStatus.Slipping : WantStatus.OnTrack;
        }

        /// <summary>They showed the board after the last refusal: the goal was reached, and no hearing has been held since.</summary>
        public static bool Proved(CompInmate c) =>
            c.want is { def: not null, status: WantStatus.Fulfilled } w && w.def == RPParoleDefOf.RP_Want_ProveWrong && c.paroleDenialTick >= 0 && w.resolvedTick >= c.paroleDenialTick;

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w) =>
            "RP_Want_ProveWrong_Detail".Translate(w.baseline.ToStringPercent(), Standing(c).ToStringPercent(), (w.baseline + Gain).ToStringPercent(), c.paroleDenial ?? "?");
    }

    /// <summary>
    /// Make amends: an inmate inside for a violent crime wants to face the victim's family (see <see cref="Amends"/>). The goal makes them eligible for the counsellor's proposal without a program
    /// passed; progress is how far the meeting has come (proposed, approved, the family on the way). A meeting that goes well reaches it, one that goes badly ends it, a refusal is a setback.
    /// </summary>
    public class WantWorker_Amends : InmateWantWorker
    {
        private static bool Applies(CompInmate c) => c.crime is { violent: true } && !Amends.Held(c);

        // (Not where no meeting could ever be held: supermax, a level seen only through glass or not at all, a prison with no visitation room.)
        public override bool CanForm(Pawn p, CompInmate c) => Applies(c) && Amends.CouldMeet(p, c);

        public override bool StillApplies(Pawn p, CompInmate c, InmateWant w) => c.crime is { violent: true };

        public override float Weight(Pawn p, CompInmate c)
        {
            float weight = def.commonality;
            if (HasTrait(p, TraitDefOf.Kind)) weight *= 1.5f;
            if (HasTrait(p, TraitDefOf.Psychopath)) weight *= 0.2f;
            if (HasTrait(p, TraitDefOf.Bloodlust)) weight *= 0.5f;
            return weight;
        }

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            if (Amends.Made(c))
            {
                Wants.Fulfil(p, c, w);
                return;
            }
            if (Amends.Hostile(c))
            {
                Wants.Thwart(p, c, w, "RP_Want_Reason_AmendsRefused", final: true, felt: true);
                return;
            }
            w.progress = c.amendsState switch
            {
                AmendsState.Proposed => 0.25f,
                AmendsState.Approved => 0.5f,
                AmendsState.Meeting => 0.75f,
                _ => 0f,
            };
            w.status = Behind(w, 0.25f) ? WantStatus.Slipping : WantStatus.OnTrack;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w) => ("RP_Want_Amends_Detail_" + c.amendsState).Translate(Money.Format(Amends.Fee));
    }

    /// <summary>Get clean: off every addiction. Progress is the addiction fading; a relapse or an overdose is the setback.</summary>
    public class WantWorker_Clean : InmateWantWorker
    {
        /// <summary>The addiction growing by more than this since its lowest is a relapse.</summary>
        public const float RelapseStep = 0.05f;

        public static float Total(Pawn p)
        {
            float total = 0f;
            foreach (Hediff h in p.health.hediffSet.hediffs)
                if (h is Hediff_Addiction) total += h.Severity;
            return total;
        }

        public static bool Addicted(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs)
                if (h is Hediff_Addiction) return true;
            return false;
        }

        public override bool CanForm(Pawn p, CompInmate c) => Addicted(p);

        public override float Weight(Pawn p, CompInmate c) => def.commonality * (c.crime is { drugRelated: true } ? 1.5f : 1f);

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            float total = Total(p);
            (w.baseline, w.last) = (Mathf.Max(0.1f, total), total);
        }

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            if (!Addicted(p))
            {
                Wants.Fulfil(p, c, w);
                return;
            }
            float total = Total(p);
            if (total > w.last + RelapseStep)
            {
                Wants.Thwart(p, c, w, "RP_Want_Reason_Relapse");
                return;
            }
            w.last = Mathf.Min(w.last, total);
            w.baseline = Mathf.Max(w.baseline, total);
            w.progress = 1f - total / w.baseline;
            w.status = Behind(w, 0.25f) ? WantStatus.Slipping : WantStatus.OnTrack;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w)
        {
            Map map = MapOf(p);
            bool supervised = Detox.Supervision(p) != null || map != null && Detox.DoctorOnSite(map);
            return (supervised ? "RP_Want_Clean_DetailSupervised" : "RP_Want_Clean_DetailUnsupervised").Translate();
        }
    }

    /// <summary>Earn a diploma: pass the next education program. Progress is the sessions of it they have sat; a failed exam is the setback.</summary>
    public class WantWorker_Diploma : InmateWantWorker
    {
        /// <summary>The first program of the def's ladder they have not passed and could take.</summary>
        public ProgramDef Next(Pawn p, CompInmate c) => def.programs.FirstOrDefault(x => !c.passedPrograms.Contains(x) && x.Suitable(p, c));

        public override bool CanForm(Pawn p, CompInmate c) => DeathRowSystem.Ext(c) == null && Next(p, c) != null;

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            w.program = Next(p, c);
            w.note = w.program?.label;
        }

        public override void Renew(Pawn p, CompInmate c, InmateWant w)
        {
            // (The same program again: the exam can be retaken. The sessions already sat still count, so "halfway" is not told twice, but a new deadline is not slipping.)
            w.progress = 0f;
            w.slipLogged = false;
            w.deadlineTick = Now + def.durationDays * GenDate.TicksPerDay;
        }

        public override bool StillApplies(Pawn p, CompInmate c, InmateWant w) => w.program != null && DeathRowSystem.Ext(c) == null;

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            if (c.passedPrograms.Contains(w.program))
            {
                Wants.Fulfil(p, c, w);
                return;
            }
            c.programProgress.TryGetValue(w.program, out int done);
            w.progress = Mathf.Clamp01(done / (float)Mathf.Max(1, w.program.sessions));
            w.status = Behind(w, 0.25f) ? WantStatus.Slipping : WantStatus.OnTrack;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.program == null) return ""; // (its program is no longer in the game: the next check drops the goal)
            c.programProgress.TryGetValue(w.program, out int done);
            TaggedString detail = "RP_Want_Diploma_Detail".Translate(w.program.LabelCap, done, w.program.sessions);
            // (A library book in the cell is reading up for the exam.)
            return w.program.literacyBonus > 0f && c.books > 0 ? detail + " " + "RP_Want_Diploma_Reading".Translate() : detail;
        }
    }

    /// <summary>Stay in touch with family: letters and visits from home. A partner who leaves or a death in the family is the setback.</summary>
    public class WantWorker_Family : InmateWantWorker
    {
        public override bool CanForm(Pawn p, CompInmate c) => c.familyCount > 0 && Relatives.Of(c).Exists(r => r.CanVisit);

        public override bool CanRenew(Pawn p, CompInmate c, InmateWant w) => CanForm(p, c);

        public override float Weight(Pawn p, CompInmate c)
        {
            float weight = def.commonality + 0.1f * Mathf.Min(3, c.familyCount);
            if (HasTrait(p, TraitDefOf.Psychopath)) weight *= 0.5f;
            if (HasTrait(p, TraitDefOf.Kind)) weight *= 1.3f;
            return weight;
        }

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            w.count = 0;
            // The partner is the one they most want to put right.
            Relative kin = Relatives.Of(c).FindAll(r => r.CanVisit).RandomElementByWeightWithFallback(r => r.kind == "RP_Kin_Partner" ? 2f : 1f);
            w.note = kin?.Label;
        }

        public override void Renew(Pawn p, CompInmate c, InmateWant w)
        {
            // The same person if they are still there, else somebody else.
            Relative kin = Relatives.Of(c).Find(r => r.Label == w.note);
            if (kin is { CanVisit: true })
            {
                w.progress = 0f;
                w.halfway = w.slipLogged = false;
                w.count = 0;
                w.deadlineTick = Now + def.durationDays * GenDate.TicksPerDay;
            }
            else base.Renew(p, c, w);
        }

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            Relative kin = Relatives.Of(c).Find(r => r.Label == w.note);
            if (kin != null && !kin.CanVisit)
            {
                Wants.Thwart(p, c, w, kin.alive ? "RP_Want_Reason_Estranged" : "RP_Want_Reason_Bereaved", final: true);
                return;
            }
            if (w.count >= def.contacts)
            {
                Wants.Fulfil(p, c, w);
                return;
            }
            w.progress = w.count / (float)Mathf.Max(1, def.contacts);
            w.status = Behind(w, 0.34f) ? WantStatus.Slipping : WantStatus.OnTrack;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w) =>
            "RP_Want_Family_Detail".Translate(w.count, def.contacts, w.deadlineTick / GenDate.TicksPerDay + 1);

        /// <summary>The warden lets them make the call: it counts as a contact.</summary>
        public override string Encourage(Pawn p, CompInmate c, InmateWant w, Pawn warden)
        {
            w.backed = true;
            w.count++;
            Wants.Refresh(p, c);
            return "RP_Want_Backed_Family";
        }
    }

    /// <summary>
    /// Settle a score: a staff member they have a bad history with (a memory of -60 or worse), or an enemy inside. Their
    /// outbursts go for that person; a blow that lands on them settles it (an insult does not). It simmers for a few days and is let go if the grudge cools.
    /// </summary>
    public class WantWorker_Score : InmateWantWorker
    {
        /// <summary>A grudge against an inmate: their opinion of them must be this bad, and cooling past the second one ends it.</summary>
        public const float EnemyOpinion = -60f, CooledOpinion = -40f;

        /// <summary>Whoever they hold the worst grudge against: staff first (the record of what was done to them), then an enemy inside.</summary>
        public static bool FindTarget(Pawn p, CompInmate c, out int id, out string name, out bool staff)
        {
            (id, name, staff) = (-1, null, false);
            Map map = MapOf(p);
            if (map == null) return false;
            List<Pawn> employed = null;
            foreach (StaffMemory m in c.staffMemories.OrderBy(x => x.score))
            {
                if (m.score > Wants.GrudgeAt) break;
                employed ??= StaffCommute.AllStaff(map);
                if (!employed.Exists(s => s.thingIDNumber == m.staffId) || Wants.RecentGrudge(c, m.staffId)) continue;
                (id, name, staff) = (m.staffId, m.name, true);
                return true;
            }
            float worst = EnemyOpinion;
            foreach (DirectPawnRelation r in p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
            {
                if (r.def != RPRelationDefOf.RP_Enemy || r.otherPawn is not { Dead: false } other || !other.IsPrisonerOfColony || other.Map != p.Map || other.Inmate() == null) continue;
                float opinion = p.relations.OpinionOf(other);
                if (opinion > worst || Wants.RecentGrudge(c, other.thingIDNumber)) continue;
                (id, name, staff, worst) = (other.thingIDNumber, other.LabelShort, false, opinion);
            }
            return id >= 0;
        }

        public override bool CanForm(Pawn p, CompInmate c) => FindTarget(p, c, out _, out _, out _);

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            if (FindTarget(p, c, out int id, out string name, out bool staff))
            {
                (w.targetId, w.targetName, w.targetIsStaff) = (id, name, staff);
                w.note = "RP_Want_ScoreNote".Translate(name);
            }
        }

        private static Pawn TargetPawn(Pawn p, InmateWant w)
        {
            Map map = MapOf(p);
            if (map == null) return null;
            if (w.targetIsStaff) return StaffCommute.AllStaff(map).Find(s => s.thingIDNumber == w.targetId);
            foreach (Pawn o in map.mapPawns.PrisonersOfColony)
                if (o.thingIDNumber == w.targetId && !o.Dead) return o;
            return null;
        }

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            Pawn target = TargetPawn(p, w);
            if (target == null)
            {
                Wants.Lose(p, c, w, "RP_Want_Reason_TargetGone", thwarted: false, flip: false);
                return;
            }
            bool cooled = w.targetIsStaff ? StaffMemories.ScoreFor(c, target) > StaffMemories.HatedAt : p.relations.OpinionOf(target) > CooledOpinion;
            if (cooled)
            {
                Wants.Lose(p, c, w, "RP_Want_Reason_Cooled", thwarted: false, flip: false);
                return;
            }
            // It simmers: the nearer the end of the window, the nearer they are to doing something about it.
            float window = def.durationDays * (float)GenDate.TicksPerDay;
            w.progress = Mathf.Min(0.9f, (Now - w.formedTick) / window);
            w.status = WantStatus.OnTrack;
        }

        /// <summary>Not the same person again within days, however it ended (a feeling takes time to fade): the record outlives the goal slot.</summary>
        public override void Ended(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.targetId >= 0) Wants.NoteGrudgeEnded(c, w.targetId);
        }

        public override bool TellsHalfway => false;

        public override string DeadlineTipKey => "RP_FileWantTipGrudge";

        /// <summary>A grudge is not a goal anyone hopes for: if the window passes they let it go.</summary>
        public override void TimedOut(Pawn p, CompInmate c, InmateWant w) => Wants.Lose(p, c, w, "RP_Want_Reason_LetGo", thwarted: false, flip: false);

        /// <summary>Getting their own back takes the edge off the hate.</summary>
        public override void Fulfilled(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.targetIsStaff && TargetPawn(p, w) is { } staff) StaffMemories.Note(c, staff, 30f);
        }

        /// <summary>Not afraid of trouble: they go looking for it, but only for the one thing.</summary>
        public override float MisconductFactor(MisconductDef m, CompInmate c, InmateWant w)
        {
            bool reaches = w.targetIsStaff ? m.Worker is MisconductWorker_AssaultStaff : m.Worker is MisconductWorker_Fight or MisconductWorker_Insult;
            return reaches ? Wants.ScoreFactor : 1f;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w) => "RP_Want_Score_Detail".Translate(w.targetName);

        /// <summary>Talking a grudge down: the chance starts at <see cref="TalkBase"/>, rises with the trust they have in the warden (up to <see cref="TalkTrust"/>), falls for a volatile one and for a deep grudge.</summary>
        public const float TalkBase = 0.3f, TalkTrust = 0.4f, TalkVolatile = 0.15f, TalkDeepest = 0.25f, TalkMin = 0.1f, TalkMax = 0.85f;
        /// <summary>What the warden's word for a staff member is worth to them when it works: a little (the hate is theirs, and a word does not undo what was done).</summary>
        public const float TalkVouch = 10f;
        /// <summary>The cost of a try that fails: they resent being told how to feel.</summary>
        public const float TalkFailTrust = -4f, TalkFailTension = 4f;

        /// <summary>How deep the grudge is, 0 (it has just formed) to 1 (as bad as it gets).</summary>
        public static float GrudgeDepth(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.targetIsStaff) return Mathf.Clamp01((Wants.GrudgeAt - (c.staffMemories.Find(m => m.staffId == w.targetId)?.score ?? Wants.GrudgeAt)) / (100f + Wants.GrudgeAt));
            Pawn target = TargetPawn(p, w);
            return target == null ? 0f : Mathf.Clamp01((EnemyOpinion - p.relations.OpinionOf(target)) / (100f + EnemyOpinion));
        }

        /// <summary>The chance the warden's word talks them out of it (see the constants).</summary>
        public static float TalkDownChance(Pawn p, CompInmate c, InmateWant w, Pawn warden)
        {
            float chance = TalkBase + TalkTrust * Mathf.Clamp(StaffMemories.ScoreFor(c, warden) / 50f, -1f, 1f) - TalkDeepest * GrudgeDepth(p, c, w);
            if (c.HasReputation(ReputationDefOf.RP_Volatile)) chance -= TalkVolatile;
            return Mathf.Clamp(chance, TalkMin, TalkMax);
        }

        /// <summary>
        /// A grudge is not one to back: the warden tries to talk them out of it, and it may work (<see cref="TalkDownChance"/>): the goal is let go and, if it was against a member of staff, the
        /// warden's word for them counts a little (<see cref="TalkVouch"/>). If it does not, the grudge stands, they think the less of the warden for it, and the warden has had their word
        /// on it (once per goal).
        /// </summary>
        public override string Encourage(Pawn p, CompInmate c, InmateWant w, Pawn warden)
        {
            w.backed = true;
            if (!(WardenSystem.ForcedRoll ?? Rand.Chance(TalkDownChance(p, c, w, warden))))
            {
                StaffMemories.Note(c, warden, TalkFailTrust);
                c.tension = Mathf.Min(100f, c.tension + TalkFailTension);
                return Wants.TalkFailedKey;
            }
            if (w.targetIsStaff && TargetPawn(p, w) is { } staff) StaffMemories.Note(c, staff, TalkVouch);
            Wants.Lose(p, c, w, "RP_Want_Reason_Talked", thwarted: false, flip: false);
            return "RP_Want_Backed_Score";
        }
    }
}
