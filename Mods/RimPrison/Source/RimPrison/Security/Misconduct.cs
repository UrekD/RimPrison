using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>A personality trait of inmates that matters inside: volatile, instigator, snitch, escape artist...</summary>
    public class ReputationDef : Def
    {
        public float commonality = 1f;
        /// <summary>Hidden until revealed (snitches, escape artists): the warden does not know at intake.</summary>
        public bool hidden;
        /// <summary>Multiplier on tension gain.</summary>
        public float tensionFactor = 1f;
        /// <summary>Multipliers on misconduct weights, per act.</summary>
        public List<MisconductFactor> misconductFactors = new List<MisconductFactor>();
        /// <summary>More likely for these security levels (factor 3), less for others.</summary>
        public List<SecurityLevelDef> typicalFor = new List<SecurityLevelDef>();
        public List<ReputationDef> conflictsWith = new List<ReputationDef>();
        /// <summary>Active behaviour (dealing, extortion, lawsuits), run hourly.</summary>
        public System.Type behaviorClass;
        /// <summary>Others prey on them; they frighten easily (informants, ex-cops, the notorious).</summary>
        public bool vulnerable;

        [Unsaved] private ReputationBehavior behaviorInt;

        public ReputationBehavior Behavior
        {
            get
            {
                if (behaviorInt == null && behaviorClass != null)
                {
                    behaviorInt = (ReputationBehavior)System.Activator.CreateInstance(behaviorClass);
                    behaviorInt.def = this;
                }
                return behaviorInt;
            }
        }
    }

    public class MisconductFactor
    {
        public MisconductDef misconduct;
        public float factor = 1f;
    }

    [DefOf]
    public static class ReputationDefOf
    {
        public static ReputationDef RP_Volatile;
        public static ReputationDef RP_Instigator;
        public static ReputationDef RP_ModelPrisoner;
        public static ReputationDef RP_Tough;
        public static ReputationDef RP_EscapeArtist;
        public static ReputationDef RP_Snitch;

        static ReputationDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(ReputationDefOf));
    }

    /// <summary>Something an inmate does when tension boils over.</summary>
    public class MisconductDef : Def
    {
        public float baseWeight = 1f;
        public float minTension = 30f;
        /// <summary>1 minor .. 5 grave; drives default punishment and reputation impact.</summary>
        public int severity = 1;
        /// <summary>Default hours of the sanction when caught (0 = none): solitary, unless <see cref="defaultSanction"/> says otherwise.</summary>
        public int punishmentHours;
        /// <summary>The sanction the policy starts with for this act (the warden can change it on the Policy tab).</summary>
        public Sanction defaultSanction = Sanction.Solitary;
        /// <summary>Seen by staff anywhere nearby without line of sight (fights, riots).</summary>
        public bool loud;
        /// <summary>Violence against a person (counted as an assault in the statistics).</summary>
        public bool assault;
        /// <summary>The days a court adds when the warden's sheet charges the inmate with it (an offence with none cannot be charged: see <see cref="Adjudication"/>, <see cref="Trials.ChargeDays"/>).</summary>
        public IntRange chargeDays;
        /// <summary>What it is on the danger map when it is seen (none: not violence to a person; see <see cref="DangerMap"/>).</summary>
        public DangerKind danger;
        /// <summary>The warden's sanction policy for this act is the one of another (a gang ambush is punished as a fight): no row of its own.</summary>
        public MisconductDef sanctionAs;
        /// <summary>Tension released by acting out.</summary>
        public float tensionRelief = 25f;
        public bool letter;
        /// <summary>
        /// A mark on the file, not a misconduct (the count's write-up): the warden's policy still decides what it brings, but it never pushes an older record out of the file, restarts no clean days (remission, the
        /// supermax step-down), bars no mentor, is not the offence a restraint was for, and is not counted in the statistics or on the desk board.
        /// </summary>
        public bool mark;
        public Type workerClass = typeof(MisconductWorker);

        [Unsaved] private MisconductWorker workerInt;

        public MisconductWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (MisconductWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }
    }

    public class MisconductRecord : IExposable
    {
        public MisconductDef def;
        public int tick;
        public string cause;
        public bool punished;
        /// <summary>Witnessed by staff or cameras (only seen misconduct counts at parole).</summary>
        public bool seen;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref cause, "cause");
            Scribe_Values.Look(ref punished, "punished");
            Scribe_Values.Look(ref seen, "seen");
        }
    }

    /// <summary>Behavior of one misconduct act. Return true if the act happened.</summary>
    public class MisconductWorker
    {
        public MisconductDef def;

        public virtual bool CanDo(Pawn p, CompInmate c) => true;

        public virtual bool TryExecute(Pawn p, CompInmate c) => false;

        /// <summary>The inmate in reach they most want to hit: the one a grudge is against, else whoever they like least.</summary>
        internal static Pawn NearbyInmate(Pawn p, float radius, Func<Pawn, bool> validator = null)
        {
            CompInmate pc = p.Inmate();
            Pawn best = null;
            float bestScore = float.MinValue;
            foreach (Pawn other in p.Map.mapPawns.PrisonersOfColonySpawned)
            {
                // (The distance first: it says no to nearly everyone.)
                if (other == p || !other.Position.InHorDistOf(p.Position, radius) || other.Downed || !InmateUtility.IsActiveInmate(other)) continue;
                if (validator != null && !validator(other)) continue;
                if (!GenSight.LineOfSight(p.Position, other.Position, p.Map)) continue;
                // Prefer people they dislike, and rival gang members.
                float score = -p.relations.OpinionOf(other) + Rand.Value * 20f;
                if (Wants.IsInmateTarget(pc, other)) score += 1000f;
                if (GangUtility.AreRivals(p.Inmate()?.gang, other.Inmate()?.gang)) score += 40f;
                // Predators pick on the frightened and the vulnerable.
                if (other.Inmate() is { } oc) score += oc.fear * 0.3f + (FearSystem.Vulnerable(oc) ? 25f : 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = other;
                }
            }
            return best;
        }

        /// <summary>The member of staff in reach they go for: the one a grudge is against, else the nearest.</summary>
        internal static Pawn NearbyStaff(Pawn p, float radius)
        {
            Pawn best = null;
            float bestDist = float.MaxValue;
            foreach (Pawn s in p.Map.mapPawns.FreeColonistsSpawned)
            {
                float d = (s.Position - p.Position).LengthHorizontalSquared;
                if (s.Downed || d > radius * radius || d >= bestDist || HostageCrisis.IsHeld(s)) continue;
                if (!p.CanReach(s, PathEndMode.Touch, Danger.Deadly)) continue;
                best = s;
                bestDist = d;
            }
            // (Only an inmate with a score to settle against a member of staff looks for that one: everybody else asked for the nearest, and was done.)
            if (p.Inmate()?.want is not { Resolved: false, targetIsStaff: true, targetId: >= 0 }) return best;
            return Wants.StaffTarget(p.Inmate(), p.Map.mapPawns.FreeColonistsSpawned.Where(s => !s.Downed && !HostageCrisis.IsHeld(s) && (s.Position - p.Position).LengthHorizontalSquared <= radius * radius
                                                                                                && p.CanReach(s, PathEndMode.Touch, Danger.Deadly))) ?? best;
        }
    }

    /// <summary>Verbal abuse: a vanilla insult at whoever they dislike most nearby.</summary>
    public class MisconductWorker_Insult : MisconductWorker
    {
        public override bool TryExecute(Pawn p, CompInmate c)
        {
            Pawn target = NearbyInmate(p, 8f);
            // (An insult does not settle a score: only a blow that lands does, see Patch_Pawn_PostApplyDamage_Wants.)
            return target != null && p.interactions.TryInteractWith(target, InteractionDefOf.Insult);
        }
    }

    /// <summary>Fist fight with a disliked inmate nearby (vanilla social fight).</summary>
    public class MisconductWorker_Fight : MisconductWorker
    {
        public override bool TryExecute(Pawn p, CompInmate c)
        {
            if (!p.Awake() || p.WorkTagIsDisabled(WorkTags.Violent)) return false;
            Pawn target = NearbyInmate(p, 6f, o => p.interactions.SocialFightPossible(o));
            if (target == null) return false;
            ContrabandUtility.Notify_ViolenceStarting(p);
            p.interactions.StartSocialFight(target);
            if (target.Inmate() is { } tc) FearSystem.Scare(tc, 10f);
            return p.InMentalState;
        }
    }

    /// <summary>Smashes a piece of furniture.</summary>
    public class MisconductWorker_Vandalism : MisconductWorker
    {
        public override bool TryExecute(Pawn p, CompInmate c)
        {
            if (!p.Awake() || p.WorkTagIsDisabled(WorkTags.Violent)) return false;
            Building target = null;
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(p.Position, 5f, true))
            {
                if (!cell.InBounds(p.Map)) continue;
                Building b = cell.GetFirstBuilding(p.Map);
                if (b == null || b.Faction != Faction.OfPlayer || b.def.building == null || b.def.IsWall || b is Building_Door || !VandalismUtility.Smashable(b)) continue;
                if (!p.CanReach(b, PathEndMode.Touch, Danger.Deadly)) continue;
                target = b;
                break;
            }
            if (target == null) return false;
            return p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Vandalizing, null, forced: true, otherPawn: null) && SetTarget(p, target);
        }

        private static bool SetTarget(Pawn p, Thing t)
        {
            if (p.MentalState is MentalState_Vandalizing v) v.target = t;
            return true;
        }
    }

    /// <summary>Attacks a member of staff.</summary>
    public class MisconductWorker_AssaultStaff : MisconductWorker
    {
        public override bool TryExecute(Pawn p, CompInmate c)
        {
            if (!p.Awake() || p.WorkTagIsDisabled(WorkTags.Violent)) return false;
            // The one they have a score with, else the one they hate, if they are within reach; else whoever is nearest.
            List<Pawn> reachable = p.Map.mapPawns.FreeColonistsSpawned.Where(s => !s.Downed && !HostageCrisis.IsHeld(s) && s.Position.InHorDistOf(p.Position, 12f)
                                                                                 && p.CanReach(s, PathEndMode.Touch, Danger.Deadly)).ToList();
            Pawn target = Wants.StaffTarget(c, reachable) ?? StaffMemories.HatedAmong(c, reachable) ?? NearbyStaff(p, 12f);
            if (target == null) return false;
            if (!p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: target)) return false;
            // In the workshop or the kitchen, at a low chance, the assault is a hostage-taking: the target is seized and the assailant holds the room.
            HostageCrisis.TryStartFromAssault(p, c, target);
            ContrabandUtility.Notify_ViolenceStarting(p); // (only once the state has started: a failed attempt must not leave the weapon drawn)
            return true;
        }
    }

    /// <summary>Makes a break for it: heads for the map edge, bashing doors on the way. Walls stop them.</summary>
    public class MisconductWorker_EscapeAttempt : MisconductWorker
    {
        public override bool CanDo(Pawn p, CompInmate c) => p.Awake() && !p.Downed;

        public override bool TryExecute(Pawn p, CompInmate c)
        {
            if (!EscapeUtility.TryFindEscapeRoute(p, out _)) return false;
            return CountSystem.StartEscaping(p, null, EscapeWay.Walked);
        }
    }

    /// <summary>A riot: several tense inmates in the same place go berserk against staff and property.</summary>
    public class MisconductWorker_Riot : MisconductWorker
    {
        public const float JoinTension = 55f;
        /// <summary>Rioted (or joined one) this recently: spent, not pulled into the next one.</summary>
        public const int RiotCooldownTicks = GenDate.TicksPerDay;

        public static bool RiotedRecently(CompInmate c)
        {
            int now = Find.TickManager.TicksGame;
            foreach (MisconductRecord r in c.records)
                if ((r.def == RPRiotDefOf.RP_Riot || r.def == RPRiotDefOf.RP_RiotJoined) && now - r.tick < RiotCooldownTicks) return true;
            return false;
        }

        /// <summary>A follower in a riot: armed if they can be, on record for it.</summary>
        public static bool Join(Pawn o, CompInmate oc, PrisonMap pm, string reason)
        {
            ContrabandUtility.DrawWeaponIfAny(o);
            if (!o.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, reason, forced: true)) return false;
            MisconductUtility.Record(o, oc, pm, RPRiotDefOf.RP_RiotJoined);
            return true;
        }

        public override bool CanDo(Pawn p, CompInmate c) => !RiotedRecently(c) && Participants(p).Count >= 3;

        public override bool TryExecute(Pawn p, CompInmate c)
        {
            if (RiotedRecently(c)) return false;
            List<Pawn> rioters = Participants(p);
            if (rioters.Count < 3) return false;
            int started = 0;
            PrisonMap pm = PrisonMap.Of(p.Map);
            foreach (Pawn r in rioters)
            {
                // The ringleader's record is written by the caller; followers get their own.
                if (r == p ? ContrabandUtility.DrawWeaponIfAny(r) && r.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true)
                           : Join(r, r.Inmate(), pm, null)) started++;
            }
            if (started > 0)
            {
                PrisonGame.Instance.stats.riots++;
                PrisonGame.Instance.story.lastRiotTick = Find.TickManager.TicksGame;
                Story.Record(RPChronicleDefOf.RP_Chr_Violence, 2, "RP_Chr_Riot".Translate(p.LabelShort, started - 1), p);
                PrisonGame.Instance.story.dramaToday += 2;
                Story.RecordFirst("riot", RPChronicleDefOf.RP_Chr_Violence, "RP_Chr_FirstRiot".Translate(p.LabelShort), p);
                // What the wing was saying when it began (the rumour mill): in the chronicle and the letter, the chain from cause to rumour to riot.
                string talk = Rumours.RiotLine(p.Map, c.security);
                if (talk != null) Story.Record(RPChronicleDefOf.RP_Chr_Violence, 1, talk, p);
                List<Pawn> rioting = rioters.FindAll(r => r.MentalStateDef == RPMentalStateDefOf.RP_Rioting);
                // The riot's record opens (see RiotReview): who is in it and who is seen, what the ringleader wants, what the prison lacked. The morning after a reckoning comes of it.
                NeedDef grievance = RiotNegotiation.Grievance(p, c);
                RiotReview.Open(p, c, rioting, grievance);
                // A ringleader with a member of staff in reach may take them hostage: then the warden is asked about the stand-off (see
                // HostageCrisis), and the demand is the crisis' to answer. Otherwise the ringleader shouts a demand: negotiate (it becomes
                // a promise) or let the guards handle it.
                if (HostageCrisis.TryStart(p, c, rioting, HostageCrisis.Riot) == null)
                {
                    ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_RiotNegotiation", "RP_RiotLabel".Translate(),
                        "RP_RiotText".Translate(started, p.LabelShort, TensionUtility.CauseText(c)) + (talk != null ? "\n\n" + talk : "") + "\n\n" + "RP_RiotNegotiate".Translate(p.LabelShort, grievance.label, DemandSystem.Hours),
                        p.Map, new LookTargets(rioters));
                    letter.need = grievance;
                    letter.amount = p.thingIDNumber; // the ringleader: the one a negotiated promise is made to
                    letter.pawns = rioting;
                    letter.StartTimeout(GenDate.TicksPerHour * 3);
                    Find.LetterStack.ReceiveLetter(letter);
                }
            }
            return started > 0;
        }

        private static List<Pawn> Participants(Pawn p)
        {
            var list = new List<Pawn>();
            foreach (Pawn other in p.Map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!other.Position.InHorDistOf(p.Position, 12f) || other.Downed || other.InMentalState || !other.Awake()) continue;
                if (other.Inmate() is not { } oc || oc.tension < JoinTension || !InmateUtility.IsActiveInmate(other)) continue;
                if (other != p && RiotedRecently(oc)) continue;
                if (other.WorkTagIsDisabled(WorkTags.Violent)) continue;
                list.Add(other);
            }
            return list;
        }
    }

    [DefOf]
    public static class RPMisconductDefOf
    {
        public static MisconductDef RP_Fight;
        public static MisconductDef RP_Ambush;
        public static MisconductDef RP_Tunneling;
        public static MisconductDef RP_Vandalism;

        static RPMisconductDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPMisconductDefOf));
    }

    [DefOf]
    public static class RPRiotDefOf
    {
        public static MisconductDef RP_Riot;
        public static MisconductDef RP_RiotJoined;

        static RPRiotDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPRiotDefOf));
    }

    /// <summary>Picks and runs misconduct, records it, and alerts staff if anyone saw it.</summary>
    public static class MisconductUtility
    {
        private static readonly List<MisconductDef> candidates = new List<MisconductDef>();
        private static readonly List<float> weights = new List<float>();

        public static void HourlyRoll(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (p.InMentalState || p.Downed || c.forcedActivity != null) return;
            float chance = TensionUtility.MisconductChancePerHour(c) * (c.security?.dangerFactor ?? 1f);
            if (PrisonGame.Instance.BlackoutActive) chance *= IncidentWorker_Blackout.DarkMisconductFactor;
            chance *= Remission.MisconductFactor(c);
            if (!Rand.Chance(chance)) return;
            TryMisbehave(p, c, pm);
        }

        /// <summary>What they could do now and how likely each is: the base weight, their personality and reputation, and their goal.</summary>
        internal static void BuildCandidates(Pawn p, CompInmate c, MisconductDef forced)
        {
            candidates.Clear();
            weights.Clear();
            foreach (MisconductDef def in DefDatabase<MisconductDef>.AllDefsListForReading)
            {
                if (forced != null && def != forced) continue;
                if (forced == null && c.tension < def.minTension) continue;
                if (!def.Worker.CanDo(p, c)) continue;
                float w = def.baseWeight * c.MisconductFactor(def) * Wants.MisconductFactor(c, def);
                // (A wing the lid has come off: its riots are likelier for the day the letter named.)
                if (def == RPRiotDefOf.RP_Riot) w *= Suppression.RiotFactor(c);
                if (w <= 0f) continue;
                candidates.Add(def);
                weights.Add(w);
            }
        }

        /// <summary>The weight the picker gave this misconduct in the last <see cref="BuildCandidates"/> (0: not a candidate).</summary>
        internal static float CandidateWeight(MisconductDef def)
        {
            int i = candidates.IndexOf(def);
            return i < 0 ? 0f : weights[i];
        }

        public static bool TryMisbehave(Pawn p, CompInmate c, PrisonMap pm, MisconductDef forced = null)
        {
            BuildCandidates(p, c, forced);
            for (int attempt = 0; attempt < 3 && candidates.Count > 0; attempt++)
            {
                int idx = PickWeighted();
                MisconductDef def = candidates[idx];
                if (def.Worker.TryExecute(p, c))
                {
                    Record(p, c, pm, def);
                    return true;
                }
                candidates.RemoveAt(idx);
                weights.RemoveAt(idx);
            }
            return false;
        }

        private static int PickWeighted()
        {
            float total = 0f;
            for (int i = 0; i < weights.Count; i++) total += weights[i];
            float r = Rand.Value * total;
            for (int i = 0; i < weights.Count; i++)
            {
                r -= weights[i];
                if (r <= 0f) return i;
            }
            return weights.Count - 1;
        }

        /// <param name="evidence">Found by staff directly (search, discovery): counts as seen wherever the inmate is.</param>
        /// <param name="chronicle">Write the generic "{inmate}: {misconduct}" line (false when the caller writes its own).</param>
        /// <param name="unseen">Nobody saw it, whatever watches the place now (a card game, played only where no guard can see; a camera over the table does not make it a sighting).</param>
        public static void Record(Pawn p, CompInmate c, PrisonMap pm, MisconductDef def, bool evidence = false, bool chronicle = true, bool unseen = false)
        {
            c.tension = Mathf.Max(0f, c.tension - def.tensionRelief);
            // (A runaway nobody has seen: their own record does not tell of the escape: no message, no tick on the desk board, and no sighting, whatever stands near. A guard who has them (evidence) is another thing.)
            bool secret = !evidence && CountSystem.IsHidden(p) && p.MentalStateDef == RPMentalStateDefOf.RP_Escaping;
            bool seen = !secret && !unseen && (evidence || def.loud || TensionUtility.Watched(p, pm));
            var rec = new MisconductRecord { def = def, tick = Find.TickManager.TicksGame, cause = c.mainCause, seen = seen };
            c.AddRecord(rec);
            CellQuality.Notify_Misconduct(c, def, seen);
            // (A temper the prison saw is a mental break for the psychologist's count; one nobody saw is not a fact the prison has.)
            if (seen && PsychWing.CountsAsTemper(def)) PsychWing.Notify_Break(c);
            if (!def.mark) PrisonGame.Instance.stats.misconducts++;
            if (!def.mark && !secret)
            {
                PrisonGame.Instance.today.NoteMisconduct(c.security);
                pm?.regime.NoteIncident(c.security ?? InmateUtility.DefaultSecurity, GenLocalDate.HourOfDay(pm.map));
            }
            if (def.loud && def.severity >= 2) PrisonGame.Instance.today.violent++;
            if (def.assault) PrisonGame.Instance.stats.assaults++;
            // Violence that was seen goes on the danger map (one that nobody saw is not on it).
            if (seen) DangerMap.Note(pm, p, def, evidence);
            if (seen && def.severity >= 2)
            {
                if (chronicle) Story.Record(def.loud ? RPChronicleDefOf.RP_Chr_Violence : RPChronicleDefOf.RP_Chr_Security, def.severity >= 4 ? 2 : 1,
                    "RP_Chr_Misconduct".Translate(p.LabelShort, def.label), p);
                GangUtility.Reveal(c);
            }
            // (A fight or an assault that was seen, by a member of a gang that has a deal with the warden, is a breach of it.)
            if (seen && def.assault) GangDeals.Notify_Breach(p, c);
            // (The policy decides, also for a misconduct whose default is no punishment but the warden set hours.)
            if (seen) Sanctions.Apply(p, c, def, evidence);
            if (!secret && (def.letter || def.severity >= 3))
            {
                string cause = c.mainCause != null ? TensionUtility.CauseText(c).ToString() : "?";
                Messages.Message("RP_MisconductMessage".Translate(p.LabelShort, def.label, cause), p, def.severity >= 3 ? MessageTypeDefOf.ThreatSmall : MessageTypeDefOf.NegativeEvent);
            }
        }
    }
}
