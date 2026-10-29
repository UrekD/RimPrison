using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>How closely the mailroom reads the post (Policy).</summary>
    public enum MailPolicy : byte
    {
        SpotChecks,
        ReadAll,
    }

    /// <summary>Why the post is piling up (nothing can sort it, or too little is being sorted).</summary>
    public enum MailBacklog : byte
    {
        None,
        NoMailroom,
        NoSorter,
        Slow,
    }

    [DefOf]
    public static class RPMailDefOf
    {
        public static PrisonZoneDef RP_Mailroom;
        public static JobDef RP_SortMail;
        public static WorkGiverDef RP_SortMail_WorkGiver;
        public static MailKindDef RP_Mail_Ordinary;
        public static MailKindDef RP_Mail_DearJohn;
        public static MailKindDef RP_Mail_Death;
        public static MailKindDef RP_Mail_Birth;
        public static MailKindDef RP_Mail_Lawyer;
        public static MailKindDef RP_Mail_Cellmate;

        static RPMailDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPMailDefOf));
    }

    /// <summary>
    /// A kind of letter: how likely it is, what it does to the one who reads it (mood, tension, the family need), the line
    /// it leaves in their history, and what has to be true of them for it to come (a partner, children, a reputation, a
    /// former inmate to write). Anything a plain list of numbers cannot say (a death changes the family) is in the worker.
    /// </summary>
    public class MailKindDef : Def
    {
        public float weight = 1f;
        /// <summary>The mood memory the letter leaves.</summary>
        public ThoughtDef thought;
        /// <summary>Added to tension (negative calms).</summary>
        public float tension;
        /// <summary>Translation key of the cause shown for raised tension.</summary>
        public string cause;
        /// <summary>Added to the family need (0-1).</summary>
        public float familyNeed;
        /// <summary>Keyed line for the chronicle and the inmate's life log (null: only the file's letter list).</summary>
        public string chronicleKey;
        public int importance = 1;
        /// <summary>The warden is told at once (a message), not only when reading the chronicle.</summary>
        public bool warden;
        /// <summary>In-touch family needed to send it (a death needs a second person to be left).</summary>
        public int minFamily = 1;
        /// <summary>An in-touch relative of one of these kinds ("RP_Kin_Partner"...) must exist.</summary>
        public List<string> requiresKinAny = new List<string>();
        public ReputationDef requiresReputation;
        /// <summary>Somebody who did their time here must be on the register.</summary>
        public bool requiresFormerInmate;
        /// <summary>Written by somebody in the family (false: a lawyer, a former inmate). Only a family sender is named when something is found in it.</summary>
        public bool fromFamily = true;
        /// <summary>Added to the prison's reputation when a letter of the kind is read (a family's condolence letter that blames the prison costs a little: see <see cref="CondolenceSystem"/>).</summary>
        public float reputation;
        public Type workerClass = typeof(MailKindWorker);

        [Unsaved] private MailKindWorker workerInt;

        public MailKindWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (MailKindWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }
    }

    /// <summary>What one kind of letter does when it is delivered.</summary>
    public class MailKindWorker
    {
        public MailKindDef def;

        public virtual bool CanSend(Pawn p, CompInmate c)
        {
            if (c.familyCount < def.minFamily) return false;
            if (def.requiresReputation != null && !c.HasReputation(def.requiresReputation)) return false;
            if (def.requiresFormerInmate && PrisonGame.Instance.story.formerInmates.Count == 0) return false;
            if (def.requiresKinAny.Count == 0) return true;
            foreach (Relative r in Relatives.Of(c))
                if (r.CanVisit && def.requiresKinAny.Contains(r.kind)) return true;
            return false;
        }

        /// <summary>Who of the family wrote it: somebody who is still in touch (the name noted when the letter was written), else nobody.</summary>
        public virtual Relative Sender(CompInmate c, string from)
        {
            if (!def.fromFamily || from == null) return null;
            return Relatives.Of(c).Find(r => r.CanVisit && r.Label == from);
        }

        /// <summary>
        /// Changes to the family and the outside world the letter brings; returns the words for its line, or null when its news
        /// no longer applies (the people it is about are gone): nothing has been changed then.
        /// </summary>
        protected virtual string[] Open(Pawn p, CompInmate c, Relative sender) => new[] { p.LabelShort };

        /// <summary>Delivered: the news lands (family, mood, tension, the need) and is written down. False: the news no longer applies, nothing happened.</summary>
        public bool Deliver(Pawn p, CompInmate c, Relative sender)
        {
            string[] words = Open(p, c, sender);
            if (words == null) return false;
            if (def.thought != null) p.needs?.mood?.thoughts.memories.TryGainMemory(def.thought);
            if (def.tension != 0f) c.tension = Mathf.Clamp(c.tension + def.tension, 0f, 100f);
            if (def.cause != null && def.tension > 0f) c.mainCause = def.cause;
            if (def.familyNeed != 0f && p.needs?.TryGetNeed(RPVisitDefOf.RP_Family) is { } family) family.CurLevel += def.familyNeed;
            if (def.chronicleKey != null)
            {
                var args = new NamedArgument[words.Length];
                for (int i = 0; i < words.Length; i++) args[i] = words[i];
                TaggedString line = def.chronicleKey.Translate(args);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, def.importance, line, p);
                if (def.warden) Messages.Message(line, p, MessageTypeDefOf.NegativeEvent);
            }
            Wants.Notify_Mail(p, c, def);
            // (A partner who ends it, a death in the family: what can put them at risk, see WatchList.)
            if (def == RPMailDefOf.RP_Mail_DearJohn) WatchList.Notify_Trigger(p, RiskTrigger.DearJohn);
            else if (def == RPMailDefOf.RP_Mail_Death) WatchList.Notify_Trigger(p, RiskTrigger.FamilyDeath);
            return true;
        }

        protected static Relative RandomInTouch(CompInmate c, Relative except = null)
        {
            var pool = new List<Relative>();
            foreach (Relative r in Relatives.Of(c))
                if (r.CanVisit && r != except) pool.Add(r);
            return pool.Count > 0 ? pool.RandomElement() : null;
        }
    }

    /// <summary>A death in the family: one of the people who write is gone (grief is the tension and the memory).</summary>
    public class MailKindWorker_Death : MailKindWorker
    {
        protected override string[] Open(Pawn p, CompInmate c, Relative sender)
        {
            // (Not the one who is writing.)
            Relative who = RandomInTouch(c, sender);
            if (who == null) return null;
            who.alive = false;
            Relatives.Recount(c);
            return new[] { p.LabelShort, who.Label };
        }
    }

    /// <summary>A new baby: it joins the family by name (and can be visited with the rest).</summary>
    public class MailKindWorker_Birth : MailKindWorker
    {
        protected override string[] Open(Pawn p, CompInmate c, Relative sender)
        {
            Relative writer = sender ?? RandomInTouch(c);
            if (writer == null) return null;
            Relative baby = Relatives.Make(Rand.Bool ? "RP_Kin_Grandchild" : "RP_Kin_Niece");
            Relatives.Of(c).Add(baby);
            Relatives.Recount(c);
            return new[] { p.LabelShort, baby.name, writer.Label };
        }
    }

    /// <summary>"Dear John": the partner has gone. They stay in the family list, but estranged.</summary>
    public class MailKindWorker_DearJohn : MailKindWorker
    {
        /// <summary>Not the partner who is leaving: nothing they send is named.</summary>
        public override Relative Sender(CompInmate c, string from)
        {
            Relative r = base.Sender(c, from);
            return r?.kind == "RP_Kin_Partner" ? null : r;
        }

        protected override string[] Open(Pawn p, CompInmate c, Relative sender)
        {
            Relative partner = Relatives.Of(c).Find(r => r.kind == "RP_Kin_Partner" && r.CanVisit);
            if (partner == null) return null;
            partner.estranged = true;
            Relatives.Recount(c);
            return new[] { p.LabelShort, partner.Label };
        }
    }

    /// <summary>A line from somebody who did their time here (a friend of theirs, if there was one).</summary>
    public class MailKindWorker_Cellmate : MailKindWorker
    {
        protected override string[] Open(Pawn p, CompInmate c, Relative sender)
        {
            List<FormerInmate> register = PrisonGame.Instance.story.formerInmates;
            if (register.Count == 0) return null;
            FormerInmate who = register.Find(e => e.friendIds.Contains(p.thingIDNumber)) ?? register.RandomElement();
            return new[] { p.LabelShort, who.name };
        }
    }

    /// <summary>One thing in the post: who it is for, what kind of letter, and what (if anything) is hidden in it.</summary>
    public class MailLetter : IExposable
    {
        public Pawn inmate;
        public MailKindDef kind;
        /// <summary>A drug (or anything else that fits in an envelope) tucked inside.</summary>
        public ThingDef contraband;
        /// <summary>Who wrote it ("daughter Ines"), for the sender noted when something is found.</summary>
        public string from;
        public int arrivedTick;

        public void ExposeData()
        {
            Scribe_References.Look(ref inmate, "inmate");
            Scribe_Defs.Look(ref kind, "kind");
            Scribe_Defs.Look(ref contraband, "contraband");
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref arrivedTick, "arrived");
        }
    }

    /// <summary>A letter an inmate has had, for their file: its kind and the day.</summary>
    public class MailRecord : IExposable
    {
        public MailKindDef kind;
        public int tick;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref kind, "kind");
            Scribe_Values.Look(ref tick, "tick");
        }
    }

    /// <summary>
    /// Mail: inmates with family get a letter every few days and write back now and then. Incoming post goes through the
    /// mailroom (a painted zone with a table): a guard, or a trusted inmate clerk, sorts it one letter at a time and, as the
    /// warden's mail policy says, reads and searches every letter or only some. A letter can hide drugs: found, they are
    /// confiscated and the family's next visit is searched thoroughly; missed, they reach the inmate. News in the letters
    /// (a birth, a death, a partner leaving) moves mood, tension and the family need and goes in the inmate's file. Without
    /// a mailroom or a sorter the post waits, nothing is delivered, and the need falls with every day it does.
    /// </summary>
    public class MailSystem : IExposable
    {
        /// <summary>Letters per day: a base rate plus a little for each relative in touch.</summary>
        public const float BaseArrival = 0.2f, ArrivalPerRelative = 0.1f, MaxArrival = 0.5f;
        /// <summary>A chance per day that an inmate with family writes back, and what it does for the need.</summary>
        public const float WriteChance = 0.25f, WriteLift = 0.05f;
        /// <summary>The most letters that wait for one inmate (the sack is only so big).</summary>
        public const int MaxWaitingPerInmate = 4;
        /// <summary>Letters waiting longer than this are overdue (the alert) and cost the family need.</summary>
        public const int OverdueTicks = GenDate.TicksPerDay;
        public const float OverduePenalty = 0.03f;
        /// <summary>Reading every letter: the share found and the family need lost to the loss of privacy, per letter.</summary>
        public const float ReadAllChance = 0.8f, ReadAllPrivacyPenalty = 0.06f;
        /// <summary>Spot checks open this share of the post.</summary>
        public const float SpotCheckShare = 0.3f;
        /// <summary>An inmate clerk is not as sharp-eyed as an officer.</summary>
        public const float InmateSorterFactor = 0.7f;
        public const int ReadAllTicks = 600, SpotCheckTicks = 240;
        public const int MaxLog = 5;
        public const float MaxFindChance = 0.95f;
        /// <summary>The share of what a sorter on the inmate's payroll would have found that they still find.</summary>
        public const float CorruptFindShare = 0.15f;
        /// <summary>The chance a letter hides something, before who it is for (dealers, gangs, drug offenders) raises it.</summary>
        public const float BaseContrabandChance = 0.02f;

        /// <summary>Undelivered post, oldest first.</summary>
        public List<MailLetter> waiting = new List<MailLetter>();

        public static float ArrivalChance(CompInmate c) => Mathf.Min(MaxArrival, BaseArrival + ArrivalPerRelative * c.familyCount);

        /// <summary>Dealers, gang members and drug offenders are likelier to have something sent in.</summary>
        public static float ContrabandChance(CompInmate c)
        {
            // Taken out of a smuggling ring lately: the post to them is watched, nothing is sent.
            if (c.Pawn is { } owner && RingSystem.SupplyCut(owner)) return 0f;
            float chance = BaseContrabandChance + c.tension / 1000f;
            if (c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer))) chance *= 4f;
            if (c.gang != null) chance *= 2f;
            if (c.crime != null && c.crime.drugRelated) chance *= 2f;
            // Someone who arranged something to pay a debt has the family send it.
            if (DebtSystem.Smuggling(c)) chance *= 4f;
            return Mathf.Min(0.5f, chance);
        }

        /// <summary>The chance a sorter finds what a letter hides: the policy's reach, then who is looking (their wits, their morale).</summary>
        public static float FindChance(Pawn sorter, MailPolicy policy)
        {
            float chance = ReadAllChance * (policy == MailPolicy.SpotChecks ? SpotCheckShare : 1f);
            chance *= Mathf.Lerp(0.8f, 1.2f, (sorter.skills?.GetSkill(SkillDefOf.Intellectual)?.Level ?? 5) / 20f);
            if (sorter.GetComp<CompStaff>()?.role != null) chance *= StaffMorale.SearchFactor(sorter);
            else chance *= InmateSorterFactor;
            return Mathf.Min(MaxFindChance, chance);
        }

        /// <summary>"1 letter", "3 letters".</summary>
        public static TaggedString Letters(int n) => (n == 1 ? "RP_Letters_One" : "RP_Letters_Many").Translate(n);

        public static int SortTicks(MailPolicy policy) => policy == MailPolicy.ReadAll ? ReadAllTicks : SpotCheckTicks;

        /// <summary>Somebody on the outside can still write to them, and they can still be handed a letter.</summary>
        private static bool Deliverable(Pawn p) => p is { Spawned: true, Dead: false } && p.IsPrisonerOfColony && p.Inmate() != null;

        private static bool Gone(Pawn p) => p == null || p.Destroyed || p.Dead || !p.IsPrisonerOfColony || p.Inmate() == null;

        // ---- the mailroom ---------------------------------------------------------------------------------------

        /// <summary>The tables in the mailroom zone: where the post is sorted.</summary>
        public static List<Thing> Tables(Map map)
        {
            var tables = new List<Thing>();
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return tables;
            foreach (IntVec3 cell in pm.zones.CellsOf(RPMailDefOf.RP_Mailroom))
                if (cell.GetEdifice(map) is { def.surfaceType: SurfaceType.Eat } table && !tables.Contains(table)) tables.Add(table);
            return tables;
        }

        public static bool HasMailroom(Map map) => Tables(map).Count > 0;

        /// <summary>A trusted inmate can work the mailroom only through an Auto door into it (as in the kitchen).</summary>
        public static int InmateClerkSlots(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || !HasMailroom(map)) return 0;
            foreach (CompPrisonDoor d in pm.Doors)
                if (d.workZone == RPMailDefOf.RP_Mailroom) return 1;
            return 0;
        }

        /// <summary>A guard who can sort the post now: on site, working (not on leave or suspended) and with Warden work switched on.</summary>
        public static bool IsSorter(Pawn s) =>
            s is { Spawned: true } && GuardUtility.IsGuard(s) && s.GetComp<CompStaff>() is { } sc && StaffCareers.Available(sc)
            && !s.WorkTypeIsDisabled(WorkTypeDefOf.Warden) && s.workSettings?.WorkIsActive(WorkTypeDefOf.Warden) == true;

        /// <summary>Somebody who could sort the post: a guard on site with Warden work on, or an inmate clerk (a guard at home or with the work off is not "somebody": the alert would say the post is slow when nobody is sorting it).</summary>
        public static bool HasSorter(Map map)
        {
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
                if (IsSorter(s)) return true;
            return HasClerk(map);
        }

        /// <summary>An inmate is on mailroom duty.</summary>
        public static bool HasClerk(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && c.job?.workerClass == typeof(InmateJobWorker_Mailroom)) return true;
            return false;
        }

        // ---- the post -------------------------------------------------------------------------------------------

        public int WaitingFor(Pawn p)
        {
            int n = 0;
            for (int i = 0; i < waiting.Count; i++)
                if (waiting[i].inmate == p) n++;
            return n;
        }

        public int Overdue(Pawn p)
        {
            int now = Find.TickManager.TicksGame, n = 0;
            for (int i = 0; i < waiting.Count; i++)
                if (waiting[i].inmate == p && now - waiting[i].arrivedTick > OverdueTicks) n++;
            return n;
        }

        /// <summary>Post for somebody on this map that a sorter could pick up now (nobody sorts their own; a guard leaves a letter to the clerk on duty for <see cref="GuardTakesOverTicks"/>).</summary>
        public bool AnyWaiting(Map map, Pawn sorter = null)
        {
            bool? onDuty = null;
            for (int i = 0; i < waiting.Count; i++)
                if (Sortable(waiting[i], map, sorter, ref onDuty)) return true;
            return false;
        }

        /// <summary>How long a guard leaves post to an inmate clerk who is on mailroom duty (the clerk's shift is a few hours of the day: letters that came in the night wait for it, and the guards take over what is still there after this).</summary>
        public const int GuardTakesOverTicks = GenDate.TicksPerHour * 12;

        /// <summary>The letter is for somebody here, not the sorter's own, and the sorter may take it now (<paramref name="onDuty"/>: whether a clerk is on the job, looked up once per question and only when a guard asks).</summary>
        private bool Sortable(MailLetter l, Map map, Pawn sorter, ref bool? onDuty)
        {
            if (l.inmate == sorter || !Deliverable(l.inmate) || l.inmate.Map != map) return false;
            if (sorter == null || sorter.IsPrisonerOfColony || Find.TickManager.TicksGame - l.arrivedTick >= GuardTakesOverTicks) return true;
            onDuty ??= InmateClerkSlots(map) > 0 && HasClerk(map);
            return onDuty != true;
        }

        /// <summary>Letters that have waited too long on this map, and why (null reason: none).</summary>
        public MailBacklog Backlog(Map map, out int overdue)
        {
            int now = Find.TickManager.TicksGame;
            overdue = 0;
            for (int i = 0; i < waiting.Count; i++)
                if (waiting[i].inmate is { } p && p.Map == map && now - waiting[i].arrivedTick > OverdueTicks) overdue++;
            if (overdue == 0) return MailBacklog.None;
            if (!HasMailroom(map)) return MailBacklog.NoMailroom;
            return HasSorter(map) ? MailBacklog.Slow : MailBacklog.NoSorter;
        }

        /// <summary>A letter for this inmate (kind drawn by weight from those that can come to them), with something hidden in it now and then.</summary>
        public MailLetter Compose(Pawn p, CompInmate c, MailKindDef kind = null, bool? hides = null)
        {
            kind ??= PickKind(p, c);
            if (kind == null) return null;
            bool hidden = hides ?? Rand.Chance(ContrabandChance(c));
            return new MailLetter
            {
                inmate = p,
                kind = kind,
                contraband = hidden ? ContrabandUtility.PickSmuggled(p, ContrabandKind.Drug) : null,
                from = kind.fromFamily ? Relatives.RandomVisitor(c)?.Label : null,
                arrivedTick = Find.TickManager.TicksGame,
            };
        }

        private static MailKindDef PickKind(Pawn p, CompInmate c)
        {
            var possible = new List<MailKindDef>();
            foreach (MailKindDef k in DefDatabase<MailKindDef>.AllDefsListForReading)
                if (k.Worker.CanSend(p, c)) possible.Add(k);
            return possible.TryRandomElementByWeight(k => k.weight, out MailKindDef picked) ? picked : null;
        }

        /// <summary>The post comes in: the letter joins the queue to be sorted.</summary>
        public MailLetter Arrive(Pawn p, CompInmate c, MailKindDef kind = null, bool? hides = null)
        {
            MailLetter letter = Compose(p, c, kind, hides);
            if (letter != null) waiting.Add(letter);
            return letter;
        }

        /// <summary>An outgoing letter: nothing to sort, a small lift.</summary>
        public static void WriteBack(Pawn p)
        {
            if (p.needs?.TryGetNeed(RPVisitDefOf.RP_Family) is { } family) family.CurLevel += WriteLift;
            PrisonGame.Instance.stats.lettersWritten++;
        }

        /// <summary>Post stuck in the mailroom is felt: every overdue letter (up to three) costs a day's bit of the family need.</summary>
        public void FeelBacklog(Pawn p)
        {
            int overdue = Overdue(p);
            if (overdue > 0 && p.needs?.TryGetNeed(RPVisitDefOf.RP_Family) is { } family) family.CurLevel -= OverduePenalty * Mathf.Min(overdue, 3);
        }

        /// <summary>Midnight, one inmate: the waiting post is felt, a letter may come and they may write back.</summary>
        public void DailyFor(Pawn p, CompInmate c)
        {
            // (No family: the lawyer's and the former inmates' letters still come to them, and they have nobody to write back to.)
            FeelBacklog(p);
            if (WaitingFor(p) < MaxWaitingPerInmate && Rand.Chance(ArrivalChance(c))) Arrive(p, c);
            if (c.familyCount > 0 && Rand.Chance(WriteChance)) WriteBack(p);
        }

        public void Daily(Map map)
        {
            waiting.RemoveAll(l => Gone(l.inmate));
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c) DailyFor(p, c);
        }

        // ---- sorting ----------------------------------------------------------------------------------------------

        /// <summary>The sorter finishes the oldest letter on their map that they may take (not one of their own; a guard leaves the newest post to a clerk on duty).</summary>
        public bool SortNext(Pawn sorter)
        {
            waiting.RemoveAll(l => Gone(l.inmate));
            bool? onDuty = null;
            foreach (MailLetter l in waiting)
                if (Sortable(l, sorter.Map, sorter, ref onDuty))
                {
                    Sort(l, sorter);
                    return true;
                }
            return false;
        }

        /// <summary>
        /// One letter sorted: searched as the policy says (a found item is confiscated, a missed one reaches the inmate), then
        /// delivered. <paramref name="roll"/> stands in for the dice (tests): below the find chance finds it. The kind was drawn when
        /// the letter arrived, so its news is checked again here: one that no longer applies (the partner has left, the family is
        /// gone) becomes an ordinary letter, and with nobody left to write the letter is dropped.
        /// </summary>
        public void Sort(MailLetter letter, Pawn sorter, float roll = -1f)
        {
            waiting.Remove(letter);
            Pawn p = letter.inmate;
            if (!Deliverable(p) || p.Inmate() is not { } c) return;
            PrisonGame game = PrisonGame.Instance;
            MailKindDef kind = letter.kind.Worker.CanSend(p, c) ? letter.kind : RPMailDefOf.RP_Mail_Ordinary;
            if (!kind.Worker.CanSend(p, c)) return;
            Relative sender = kind.Worker.Sender(c, letter.from);
            if (letter.contraband != null)
            {
                float chance = FindChance(sorter, game.mailPolicy);
                float dice = roll >= 0f ? roll : Rand.Value;
                bool found = dice < chance;
                // A guard on the inmate's payroll does not look hard at their post (and takes their cut, as at a visit).
                if (found && CorruptionSystem.LooksAway(sorter, p) && dice >= chance * CorruptFindShare && sorter.GetComp<CompStaff>() is { } sc)
                {
                    found = false;
                    sc.bribesTaken++;
                    game.stats.bribes++;
                }
                if (found) Confiscate(letter, sorter, c, sender);
                else if (ContrabandUtility.Give(p, letter.contraband, forDebt: true) != null)
                {
                    game.stats.mailContrabandMissed++;
                    RingSystem.Notify_BroughtIn(p, letter.from, letter.contraband);
                }
            }
            if (game.mailPolicy == MailPolicy.ReadAll && p.needs?.TryGetNeed(RPVisitDefOf.RP_Family) is { } family) family.CurLevel -= ReadAllPrivacyPenalty;
            if (!kind.Worker.Deliver(p, c, sender))
            {
                kind = RPMailDefOf.RP_Mail_Ordinary;
                kind.Worker.Deliver(p, c, sender);
            }
            c.mail.Add(new MailRecord { kind = kind, tick = Find.TickManager.TicksGame });
            if (c.mail.Count > MaxLog) c.mail.RemoveAt(0);
            game.stats.lettersDelivered++;
        }

        private static void Confiscate(MailLetter letter, Pawn sorter, CompInmate c, Relative sender)
        {
            // An inmate clerk is nobody's informant: they pull what they find out of the post and say nothing.
            if (sorter.IsPrisonerOfColony) return;
            PrisonGame game = PrisonGame.Instance;
            Pawn p = letter.inmate;
            game.stats.mailContrabandFound++;
            game.today.contrabandFound++;
            c.suspected = true;
            c.tension = Mathf.Min(100f, c.tension + 10f);
            // The sender, when the family wrote it, is noted: their next visit is searched thoroughly (the flag a phone tap sets).
            if (sender != null) c.nextVisitFlagged = true;
            MisconductUtility.Record(p, c, PrisonMap.Of(p.Map), RPContrabandDefOf.RP_ContrabandPossession, evidence: true, chronicle: false);
            Messages.Message("RP_MailContrabandFound".Translate(sorter.LabelShort, letter.contraband.label, p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
            TaggedString line = sender != null
                ? "RP_Chr_MailContrabandFound".Translate(sorter.LabelShort, letter.contraband.label, p.LabelShort, sender.Label)
                : "RP_Chr_MailContrabandFoundNoSender".Translate(sorter.LabelShort, letter.contraband.label, p.LabelShort);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, line, p, sorter);
            StaffMemories.Note(c, sorter, StaffMemories.FriskFound);
            PrisonMap.Of(p.Map)?.AddFind(sorter.Position, ContrabandUtility.Ext(letter.contraband).kind, FindSource.Mail);
            RingSystem.Notify_FoundOnWay(p, sender?.Label, "post");
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref waiting, "waiting", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                waiting ??= new List<MailLetter>();
                waiting.RemoveAll(l => l?.kind == null);
            }
        }
    }
}
