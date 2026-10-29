using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public enum WantStatus : byte
    {
        OnTrack,
        Slipping,
        Fulfilled,
        Lost,
    }

    /// <summary>What happened at home, for <see cref="Wants.Notify_FamilyNews"/>.</summary>
    public enum FamilyNews : byte
    {
        Contact,
        Birth,
        Death,
        Leaving,
    }

    [DefOf]
    public static class RPWantDefOf
    {
        public static InmateWantDef RP_Want_Parole;
        public static InmateWantDef RP_Want_Clean;
        public static InmateWantDef RP_Want_Diploma;
        public static InmateWantDef RP_Want_Family;
        public static InmateWantDef RP_Want_Score;
        public static InmateWantDef RP_Want_Debt;
        public static ThoughtDef RP_WantFulfilled;
        public static ThoughtDef RP_WantThwarted;

        static RPWantDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPWantDefOf));
    }

    /// <summary>
    /// A goal an inmate can have. The numbers are here, what the goal means is in the worker (it reads the systems that feed
    /// it), the words are keyed by the defName: <c>{defName}_Detail</c> for the file, <c>RP_Want_Reason_*</c> for what went
    /// wrong. A new goal is a new def, a worker and its keys.
    /// </summary>
    public class InmateWantDef : Def
    {
        public Type workerClass = typeof(InmateWantWorker);
        /// <summary>Weight when a goal is rolled for an inmate who has none (before the worker's say).</summary>
        public float commonality = 1f;
        /// <summary>False: never rolled, only formed by an event (a grudge).</summary>
        public bool rolled = true;
        /// <summary>An event's goal replaces the current one only when its priority is higher.</summary>
        public int priority;
        /// <summary>Days to reach it, from forming it and again after each setback it survives.</summary>
        public int durationDays = 20;
        /// <summary>Setbacks (and a missed deadline) it survives before it is given up.</summary>
        public int maxRenewals;
        /// <summary>When it is lost to a setback (not a missed deadline), the goal that takes its place if it can form.</summary>
        public InmateWantDef flipTo;
        /// <summary>The goal it flips to does not take its place while it is in progress (it is retried after a setback, and the flip comes when it is given up).</summary>
        public bool keepsUntilGivenUp;
        /// <summary>Tension lost an hour while the goal is on track.</summary>
        public float tensionRelief = 0.3f;
        /// <summary>Tension added by a setback.</summary>
        public float tensionOnThwarted = 15f;
        /// <summary>Education: the programs in the order they are taken (the first not yet passed is the diploma).</summary>
        public List<ProgramDef> programs = new List<ProgramDef>();
        /// <summary>Family: the letters and visits that make it.</summary>
        public int contacts = 3;

        [Unsaved] private InmateWantWorker workerInt;

        public InmateWantWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (InmateWantWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }
    }

    /// <summary>
    /// One inmate's goal: how far they are, by when, and how it stands. A finished goal (reached or given up) stays on the
    /// record until the next one replaces it. People are kept by thing ID and name, like the chronicle does.
    /// </summary>
    public class InmateWant : IExposable
    {
        public InmateWantDef def;
        public WantStatus status;
        /// <summary>0-1.</summary>
        public float progress;
        public int formedTick = -1, deadlineTick = -1, resolvedTick = -1;
        /// <summary>Setbacks survived so far.</summary>
        public int renewals;
        /// <summary>The "has a goal" line is written once the inmate is on the wing, and the milestones once each.</summary>
        public bool announced, halfway, slipLogged;
        /// <summary>The warden does not know of this goal (a debt nobody has told them of): it is neither written nor shown until they do.</summary>
        public bool secret;
        /// <summary>The person it is about (a grudge): thing ID, name, and whether they are staff.</summary>
        public int targetId = -1;
        public string targetName;
        public bool targetIsStaff;
        /// <summary>What it is about, in words: the relative, the program, the one the grudge is against.</summary>
        public string note;
        public ProgramDef program;
        /// <summary>Goal-specific counters: letters and visits (family); the addiction when it began and the lowest since (clean).</summary>
        public int count;
        public float baseline, last;
        /// <summary>Where a goal with steps is (leaving a gang: 0 waiting for the session, 1 renouncing at <see cref="stepTick"/>).</summary>
        public int step, stepTick = -1;
        /// <summary>The warden backed this goal with a word, or tried to talk a grudge down (once per goal, see <see cref="InmateWantWorker.Encourage"/>).</summary>
        public bool backed;

        public bool Resolved => status >= WantStatus.Fulfilled;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref status, "status");
            Scribe_Values.Look(ref progress, "progress");
            Scribe_Values.Look(ref formedTick, "formed", -1);
            Scribe_Values.Look(ref deadlineTick, "deadline", -1);
            Scribe_Values.Look(ref resolvedTick, "resolved", -1);
            Scribe_Values.Look(ref renewals, "renewals");
            Scribe_Values.Look(ref announced, "announced");
            Scribe_Values.Look(ref halfway, "halfway");
            Scribe_Values.Look(ref slipLogged, "slipLogged");
            Scribe_Values.Look(ref secret, "secret");
            Scribe_Values.Look(ref targetId, "targetId", -1);
            Scribe_Values.Look(ref targetName, "targetName");
            Scribe_Values.Look(ref targetIsStaff, "targetIsStaff");
            Scribe_Values.Look(ref note, "note");
            Scribe_Defs.Look(ref program, "program");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref baseline, "baseline");
            Scribe_Values.Look(ref last, "last");
            Scribe_Values.Look(ref step, "step");
            Scribe_Values.Look(ref stepTick, "stepTick", -1);
            Scribe_Values.Look(ref backed, "backed");
        }
    }

    /// <summary>A grudge that ended (settled, let go, cooled, or the target gone): the same person is not held against again until the cooldown is over.</summary>
    public class GrudgeRecord : IExposable
    {
        public int targetId;
        public int tick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref targetId, "targetId");
            Scribe_Values.Look(ref tick, "tick");
        }
    }

    /// <summary>
    /// Inmate wants: each inmate may have one goal, rolled at intake from who they are (the crime, the traits, an
    /// addiction, family, their sentence) and changed by what happens (a birth, a grudge). The systems that feed a goal
    /// tell it (a program passed, a letter, a hearing, a clean bill), and it is also checked once an hour. A goal that is
    /// on track calms them a little and makes them avoid trouble that would wreck it; a setback is a big negative beat,
    /// the goal is retried or given up, and a given-up goal can turn into another. Reaching, losing and the milestones go
    /// into the chronicle and the inmate's own history, and the storyteller prefers inmates whose goal is near a milestone.
    /// All work is an hourly check on the inmate's own Hourly, a daily sweep, and the events themselves: nothing per tick.
    /// </summary>
    public static class Wants
    {
        /// <summary>A staff or inmate memory this bad starts a grudge.</summary>
        public const float GrudgeAt = -60f;
        /// <summary>Misconduct that would be recorded as serious (severity 2+) is this much less likely while a goal is on track.</summary>
        public const float AvoidFactor = 0.8f;
        /// <summary>The outbursts that can reach the one a grudge is against are this much likelier.</summary>
        public const float ScoreFactor = 1.3f;
        /// <summary>Tension lost on reaching a goal.</summary>
        public const float FulfilledRelief = 10f;
        /// <summary>A goal is rolled this many days after the last one ended, and the same one is not rolled again for the longer wait.</summary>
        public const int RerollDays = 2, SameWantDays = 10;
        public const float RerollChance = 0.5f;
        /// <summary>How much likelier the storyteller is to pick an inmate whose goal is near a milestone.</summary>
        public const float CallbackWeight = 2.5f;
        public const int MilestoneDays = 3;
        /// <summary>Days a goal the warden backed is given on top (see <see cref="InmateWantWorker.Encourage"/>).</summary>
        public const int BackedDays = 3;
        public const string TimeOutReason = "RP_Want_Reason_TimeOut";
        /// <summary>The line of a grudge the warden could not talk them out of (see <see cref="WantWorker_Score.Encourage"/>).</summary>
        public const string TalkFailedKey = "RP_Want_Backed_Score_Failed";

        private static readonly List<InmateWantDef> candidates = new List<InmateWantDef>();
        private static int Now => Find.TickManager.TicksGame;

        public static string Title(InmateWant w) => w.note == null ? w.def.label : "RP_WantTitle".Translate(w.def.label, w.note).ToString();

        public static string StatusLabel(InmateWant w) => ("RP_WantStatus_" + w.status).Translate();

        // ---- forming ---------------------------------------------------------------------------------------------

        /// <summary>Whether a goal of this kind may take the place of what the inmate has now: only a finished one, or a lesser one (a goal in progress is not wiped for another of its own kind).</summary>
        public static bool CanReplace(InmateWant old, InmateWantDef def) =>
            old?.def == null || old.Resolved || old.def.priority < def.priority && !(old.def.keepsUntilGivenUp && old.def.flipTo == def);

        /// <summary>Whether this inmate held a grudge against that person that ended within the cooldown (however it ended).</summary>
        public static bool RecentGrudge(CompInmate c, int targetId) =>
            c.pastGrudges.Exists(g => g.targetId == targetId && Now - g.tick < SameWantDays * GenDate.TicksPerDay);

        /// <summary>Notes that a grudge against this person ended now (and forgets the ones whose cooldown is over).</summary>
        public static void NoteGrudgeEnded(CompInmate c, int targetId)
        {
            c.pastGrudges.RemoveAll(g => g.targetId == targetId || Now - g.tick >= SameWantDays * GenDate.TicksPerDay);
            c.pastGrudges.Add(new GrudgeRecord { targetId = targetId, tick = Now });
        }

        /// <summary>
        /// A grudge a member of a gang the warden has not found holds against staff is not the chronicle's to tell: it would name
        /// the gang to the player (a guard shooting a gang member dead gives every one of them the grudge at once).
        /// </summary>
        public static bool Quiet(CompInmate c, InmateWant w) =>
            w.secret || w.def == RPWantDefOf.RP_Want_Score && w.targetIsStaff && c.gang != null && !c.gangKnown
            // (A wish to leave a gang the warden has not found is the gang itself: the session that hears it out is what tells.)
            || w.def == RPGangStructureDefOf.RP_Want_LeaveGang && c.gang != null && !c.gangKnown;

        /// <summary>The goal a screen may show the warden (the inmate list and its sort and filters, the inmate's file, the week's report): not a secret one, and not one that would name an undiscovered gang (see <see cref="Quiet"/>).</summary>
        public static InmateWant Shown(CompInmate c) => c?.want is { def: not null } w && !Quiet(c, w) ? w : null;

        private static void Tell(CompInmate c, InmateWant w, int importance, TaggedString text, Pawn p)
        {
            if (!Quiet(c, w)) Story.Record(RPChronicleDefOf.RP_Chr_Life, importance, text, p);
        }

        /// <summary>
        /// Gives the inmate this goal if they could want it and it may replace what they have (a goal still in progress is put
        /// aside with a line). <paramref name="configure"/> runs after the worker has set it up, before it is announced.
        /// </summary>
        public static InmateWant Form(Pawn p, CompInmate c, InmateWantDef def, Action<InmateWant> configure = null)
        {
            if (p == null || c == null || def == null || !CanReplace(c.want, def) || !def.Worker.CanForm(p, c)) return null;
            InmateWant old = c.want;
            var w = new InmateWant { def = def, formedTick = Now };
            def.Worker.Begin(p, c, w);
            configure?.Invoke(w);
            if (old?.def != null && !old.Resolved)
            {
                old.status = WantStatus.Lost;
                old.resolvedTick = Now;
                old.def.Worker.Ended(p, c, old);
                // (Not when what replaces it may not be told: a grudge over a hidden gang would be given away by the line. A goal nobody knows of is no such thing: the line says
                // only that the one the file showed is gone, and without it the file would just lose it.)
                if (old.announced && (w.secret || !Quiet(c, w))) Tell(c, old, 1, "RP_Chr_WantSetAside".Translate(p.LabelShort, Title(old)), p);
            }
            c.want = w;
            TryAnnounce(p, c, w);
            return w;
        }

        /// <summary>Rolls a goal for an inmate with none: weighted by what the worker makes of them.</summary>
        public static InmateWant Roll(Pawn p, CompInmate c)
        {
            candidates.Clear();
            var weights = new List<float>();
            InmateWant last = c.want;
            foreach (InmateWantDef def in DefDatabase<InmateWantDef>.AllDefsListForReading)
            {
                if (!def.rolled || !CanReplace(last, def) || !def.Worker.CanForm(p, c)) continue;
                // Not the goal they just gave up on.
                if (last is { status: WantStatus.Lost } && last.def == def && Now - last.resolvedTick < SameWantDays * GenDate.TicksPerDay) continue;
                float weight = def.Worker.Weight(p, c);
                if (weight <= 0f) continue;
                candidates.Add(def);
                weights.Add(weight);
            }
            if (candidates.Count == 0) return null;
            float roll = Rand.Value * weights.Sum();
            InmateWantDef chosen = candidates[candidates.Count - 1];
            for (int i = 0; i < candidates.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0f)
                {
                    chosen = candidates[i];
                    break;
                }
            }
            candidates.Clear();
            return Form(p, c, chosen);
        }

        /// <summary>The "has a goal" line, once they are on the wing (a goal rolled at intake waits for that).</summary>
        private static void TryAnnounce(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.announced || !p.Spawned || PrisonGame.Instance == null) return;
            w.announced = true;
            // (The note about goals is for a goal a screen will show: not one that is kept quiet.)
            if (!Quiet(c, w)) Concepts.Teach(Concepts.Want);
            Tell(c, w, 1, "RP_Chr_WantFormed".Translate(p.LabelShort, Title(w)), p);
        }

        // ---- checking --------------------------------------------------------------------------------------------

        /// <summary>
        /// Brings the goal up to date: whether it still applies, how far along it is, and the milestones on the way. What
        /// a system has just told the goal is read here too.
        /// </summary>
        public static void Refresh(Pawn p, CompInmate c)
        {
            InmateWant w = c?.want;
            if (w?.def == null) return;
            TryAnnounce(p, c, w);
            if (w.Resolved) return;
            InmateWantWorker worker = w.def.Worker;
            if (!worker.StillApplies(p, c, w))
            {
                Lose(p, c, w, "RP_Want_Reason_NoLongerApplies", thwarted: false, flip: false);
                return;
            }
            worker.Evaluate(p, c, w);
            if (w.Resolved) return;
            w.progress = Mathf.Clamp01(w.progress);
            if (!w.halfway && w.progress >= 0.5f)
            {
                w.halfway = true;
                if (worker.TellsHalfway) Tell(c, w, 1, "RP_Chr_WantHalfway".Translate(p.LabelShort, Title(w)), p);
            }
            if (w.status == WantStatus.Slipping && !w.slipLogged)
            {
                w.slipLogged = true;
                Tell(c, w, 1, "RP_Chr_WantSlipping".Translate(p.LabelShort, Title(w)), p);
            }
        }

        /// <summary>Hourly, on the inmate's own Hourly: the goal is brought up to date and a missed deadline is a setback.</summary>
        public static void Hourly(Pawn p, CompInmate c)
        {
            Refresh(p, c);
            InmateWant w = c.want;
            if (w?.def == null || w.Resolved || w.deadlineTick < 0 || Now < w.deadlineTick) return;
            w.def.Worker.TimedOut(p, c, w);
        }

        /// <summary>Daily: a grudge starts a goal, and an inmate with none (or whose last one ended a while ago) may get one.</summary>
        public static void Daily(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate() is { security: not null } c) DailyFor(p, c);
        }

        public static void DailyFor(Pawn p, CompInmate c)
        {
            InmateWantDef grudge = RPWantDefOf.RP_Want_Score;
            if ((c.want?.def != grudge || c.want.Resolved) && Form(p, c, grudge) != null) return;
            InmateWant w = c.want;
            if (w?.def != null && !w.Resolved) return;
            if (w?.def != null && Now - w.resolvedTick < RerollDays * GenDate.TicksPerDay) return;
            if (Rand.Chance(RerollChance)) Roll(p, c);
        }

        // ---- outcomes --------------------------------------------------------------------------------------------

        public static void Fulfil(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.Resolved) return;
            w.status = WantStatus.Fulfilled;
            w.progress = 1f;
            w.resolvedTick = Now;
            w.announced = true;
            c.tension = Mathf.Max(0f, c.tension - FulfilledRelief);
            if (!w.secret) p.needs?.mood?.thoughts.memories.TryGainMemory(RPWantDefOf.RP_WantFulfilled);
            Tell(c, w, 2, "RP_Chr_WantFulfilled".Translate(p.LabelShort, Title(w)), p);
            w.def.Worker.Ended(p, c, w);
            w.def.Worker.Fulfilled(p, c, w);
        }

        /// <summary>
        /// A setback: a big negative beat. The goal is retried (a new deadline) while it has setbacks to spare, else given up,
        /// and a goal lost to a setback (not a missed deadline) may turn into another.
        /// </summary>
        /// <param name="final">The thing it hung on is gone (a death, a partner who left): no second try.</param>
        /// <param name="felt">The hearing that set it back has already hit them (its own thought, and its own tension when the warden opposed): not twice.</param>
        public static void Thwart(Pawn p, CompInmate c, InmateWant w, string reasonKey, bool final = false, bool felt = false)
        {
            if (w.Resolved) return;
            InmateWantDef def = w.def;
            // A deadline that merely passed is a disappointment, not a jolt: it can be the prison's doing (no doctor, no classroom), so it costs no tension.
            if (!felt && reasonKey != TimeOutReason)
            {
                c.tension = Mathf.Min(100f, c.tension + def.tensionOnThwarted);
                c.mainCause = "RP_Cause_WantThwarted";
            }
            // (A goal the warden does not know of gives no thought: its label would give it away. Nor does one that would name an undiscovered gang.)
            if (!felt && !Quiet(c, w)) p.needs?.mood?.thoughts.memories.TryGainMemory(RPWantDefOf.RP_WantThwarted);
            if (!final && w.renewals < def.maxRenewals && def.Worker.CanRenew(p, c, w))
            {
                // (Renew says whether the milestones start over: what is read from the systems, like the odds at the board, does not.)
                w.renewals++;
                w.status = WantStatus.OnTrack;
                def.Worker.Renew(p, c, w);
                w.announced = true;
                Tell(c, w, 2, "RP_Chr_WantRetry".Translate(p.LabelShort, Title(w), reasonKey.Translate()), p);
                return;
            }
            Lose(p, c, w, reasonKey, thwarted: true, flip: reasonKey != TimeOutReason);
        }

        /// <summary>Given up: for a setback (<paramref name="thwarted"/>) or because it stopped applying or mattering.</summary>
        public static void Lose(Pawn p, CompInmate c, InmateWant w, string reasonKey, bool thwarted, bool flip)
        {
            if (w.Resolved) return;
            // (A goal nobody was told about ends without a word, unless it was a setback.)
            bool told = w.announced || thwarted;
            w.status = WantStatus.Lost;
            w.resolvedTick = Now;
            w.announced = true;
            w.def.Worker.Ended(p, c, w);
            if (told) Tell(c, w, thwarted ? 2 : 1, "RP_Chr_WantLost".Translate(p.LabelShort, Title(w), reasonKey.Translate()), p);
            if (flip && w.def.flipTo != null) Form(p, c, w.def.flipTo);
        }

        // ---- what the systems tell it ----------------------------------------------------------------------------

        /// <summary>The warden's board let them go: called as the release begins.</summary>
        public static void Notify_Paroled(Pawn p, CompInmate c)
        {
            if (c?.want is { Resolved: false } w && w.def.Worker is WantWorker_Parole) Fulfil(p, c, w);
        }

        /// <summary>A hearing that did not go their way (the warden's desk and the board's own).</summary>
        /// <param name="heard">A hearing was held (the warden's desk): it gave them its own thought, and its own tension when the warden opposed.</param>
        public static void Notify_ParoleDenied(Pawn p, CompInmate c, bool heard = false)
        {
            if (c?.want is { Resolved: false } w && w.def.Worker is WantWorker_Parole) Thwart(p, c, w, "RP_Want_Reason_ParoleDenied", felt: heard);
        }

        /// <summary>
        /// A refusal at a hearing has its reasons on the file: an inmate with no goal in progress (or a lesser one) sets out to prove the board wrong. (One whose
        /// goal is to make parole keeps at it: that goal turns into this one only when its last chance is gone.)
        /// </summary>
        public static void Notify_ParoleReasons(Pawn p, CompInmate c) => Form(p, c, RPParoleDefOf.RP_Want_ProveWrong);

        public static void Notify_ProgramPassed(Pawn p, CompInmate c, ProgramDef prog)
        {
            // (A program can also cure an addiction: the goal reads that too.)
            Refresh(p, c);
            // (A reform program passed gives a gang member something to think about.)
            GangExit.Doubt(p, c);
            if (c?.want is { Resolved: false } w && w.def.Worker is WantWorker_Diploma && w.program == prog) Fulfil(p, c, w);
        }

        public static void Notify_ProgramFailed(Pawn p, CompInmate c, ProgramDef prog)
        {
            if (c?.want is { Resolved: false } w && w.def.Worker is WantWorker_Diploma && w.program == prog) Thwart(p, c, w, "RP_Want_Reason_ExamFailed");
        }

        public static void Notify_Overdose(Pawn p, CompInmate c)
        {
            if (c?.want is { Resolved: false } w && w.def.Worker is WantWorker_Clean) Thwart(p, c, w, "RP_Want_Reason_Overdose");
        }

        /// <summary>News from home, from the post or the incident: a letter or visit counts toward a family goal, a birth gives a new one, a death or a partner leaving may end it.</summary>
        public static void Notify_FamilyNews(Pawn p, CompInmate c, FamilyNews news)
        {
            if (c == null) return;
            switch (news)
            {
                case FamilyNews.Contact:
                    if (c.want is { Resolved: false } w && w.def.Worker is WantWorker_Family)
                    {
                        w.count++;
                        Refresh(p, c);
                    }
                    break;
                case FamilyNews.Birth:
                    // "Be there for the kid": the baby is the one they want to stay close to (a family goal already in progress is left alone: it cannot be replaced).
                    GangExit.Doubt(p, c);
                    Relative baby = c.relatives.Count > 0 ? c.relatives[c.relatives.Count - 1] : null;
                    if (baby != null) Form(p, c, RPWantDefOf.RP_Want_Family, x => (x.note, x.count) = (baby.Label, 0));
                    break;
                default:
                    Refresh(p, c);
                    break;
            }
        }

        /// <summary>A letter delivered: its kind says what it is for the goal.</summary>
        public static void Notify_Mail(Pawn p, CompInmate c, MailKindDef kind)
        {
            if (kind == RPMailDefOf.RP_Mail_Birth) Notify_FamilyNews(p, c, FamilyNews.Birth);
            else if (kind == RPMailDefOf.RP_Mail_Death) Notify_FamilyNews(p, c, FamilyNews.Death);
            else if (kind == RPMailDefOf.RP_Mail_DearJohn) Notify_FamilyNews(p, c, FamilyNews.Leaving);
            else if (kind.fromFamily && kind.tension <= 0f) Notify_FamilyNews(p, c, FamilyNews.Contact);
        }

        /// <summary>Somebody they hold a grudge against: a goal to settle it, if it can take the place of what they want now.</summary>
        public static void Notify_Grudge(CompInmate c)
        {
            if (c?.Pawn is not { } p || !InmateUtility.IsActiveInmate(p) || c.want is { Resolved: false, def: var current } && current == RPWantDefOf.RP_Want_Score) return;
            Form(p, c, RPWantDefOf.RP_Want_Score);
        }

        /// <summary>A blow of theirs landed on someone (see <see cref="Patch_Pawn_PostApplyDamage_Wants"/>): if it is the one they had a score with, it is settled. An insult, or an attack that is stopped on the way, does not.</summary>
        public static void Notify_Struck(Pawn p, CompInmate c, Pawn victim)
        {
            if (victim != null && c?.want is { Resolved: false } w && w.targetId == victim.thingIDNumber) Fulfil(p, c, w);
        }

        // ---- what the goal does to them --------------------------------------------------------------------------

        /// <summary>The weight of one kind of misconduct for this inmate, from their goal.</summary>
        public static float MisconductFactor(CompInmate c, MisconductDef def) =>
            c.want is { def: not null, Resolved: false } w ? w.def.Worker.MisconductFactor(def, c, w) : 1f;

        /// <summary>Tension lost this hour: a goal on track calms them.</summary>
        public static float TensionRelief(CompInmate c) =>
            c.want is { def: not null, status: WantStatus.OnTrack } w ? w.def.tensionRelief : 0f;

        /// <summary>Near a milestone: close to done, or close to the deadline, or slipping.</summary>
        public static bool NearMilestone(InmateWant w)
        {
            if (w?.def == null || w.Resolved) return false;
            if (w.progress >= 0.75f || w.status == WantStatus.Slipping) return true;
            return w.deadlineTick >= 0 && w.deadlineTick - Now <= MilestoneDays * GenDate.TicksPerDay;
        }

        /// <summary>The storyteller's weight for casting them: likelier with a goal at a milestone.</summary>
        public static float StoryWeight(CompInmate c) => NearMilestone(c?.want) ? CallbackWeight : 1f;

        /// <summary>Of the staff to choose from, the one their goal is against, if present.</summary>
        public static Pawn StaffTarget(CompInmate c, IEnumerable<Pawn> candidates)
        {
            if (c?.want is not { Resolved: false, targetIsStaff: true, targetId: >= 0 } w) return null;
            foreach (Pawn s in candidates)
                if (s.thingIDNumber == w.targetId) return s;
            return null;
        }

        /// <summary>Whether this inmate is the one their goal is against.</summary>
        public static bool IsInmateTarget(CompInmate c, Pawn other) =>
            c?.want is { Resolved: false, targetIsStaff: false, targetId: >= 0 } w && w.targetId == other.thingIDNumber;

        /// <summary>For the snapshot: everything saved about the goal in one string.</summary>
        public static string Describe(CompInmate c)
        {
            InmateWant w = c.want;
            string grudges = string.Join(",", c.pastGrudges.Select(g => $"{g.targetId}@{g.tick}"));
            if (w?.def == null) return "-/" + grudges;
            return $"{grudges}/{w.def.defName}:{w.status}:{w.progress:F2}:{w.formedTick}:{w.deadlineTick}:{w.resolvedTick}:{w.renewals}:{w.announced}{w.halfway}{w.slipLogged}{w.secret}:{w.targetId}:{w.targetName}:{w.targetIsStaff}:{w.note}:{w.program?.defName}:{w.count}:{w.baseline:F2}:{w.last:F2}:{w.step}@{w.stepTick}:{w.backed}";
        }
    }

    /// <summary>A blow that lands is what settles a score: an inmate's damage to the one their goal is against (an insult, or an attack a guard stops on the way, does not count).</summary>
    [HarmonyLib.HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Pawn_PostApplyDamage_Wants
    {
        public static void Postfix(Pawn __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt > 0f && dinfo.Instigator is Pawn attacker && attacker != __instance && attacker.IsPrisonerOfColony && attacker.Inmate() is { } c)
            {
                Wants.Notify_Struck(attacker, c, __instance);
                // The victim remembers who hurt them last, and where: a wound that bleeds out or festers later is still the attacker's murder (DeathAftermath).
                if (__instance.IsPrisonerOfColony && __instance.Inmate() is { } vc)
                    (vc.assailantId, vc.assaultTick, vc.assaultCell) = (attacker.thingIDNumber, Find.TickManager.TicksGame, __instance.PositionHeld);
                // (Staff have no assailant to charge, but a death of the wound is not the prison's neglect: see StaffDeaths.)
                else if (__instance.GetComp<CompStaff>() is { role: not null } sc)
                {
                    // (A blow to the warden is news, once for a fight.)
                    bool fresh = sc.assaultedTick < 0 || Find.TickManager.TicksGame - sc.assaultedTick > WardenSystem.StruckLetterTicks;
                    sc.assaultedTick = Find.TickManager.TicksGame;
                    StaffAgreements.Notify_StaffHurt();
                    if (sc.role.warden) WardenSystem.Notify_Struck(__instance, attacker, fresh);
                }
            }
        }
    }
}
