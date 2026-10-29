using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Base for prison incidents: only on maps with inmates (unless the subclass says otherwise).</summary>
    public abstract class IncidentWorker_Prison : IncidentWorker
    {
        protected virtual int MinInmates => 1;

        protected override bool CanFireNowSub(IncidentParms parms) =>
            parms.target is Map map && PrisonGame.Instance != null && map.mapPawns.PrisonersOfColonySpawnedCount >= MinInmates
            && (parms.forced || StoryPacing.BudgetAllows(def, map));

        /// <summary>Weighted by the prison's state (StoryExt weights over the hourly pulse).</summary>
        public override float ChanceFactorNow(IIncidentTarget target)
        {
            float f = base.ChanceFactorNow(target);
            if (def.GetModExtension<StoryExt>() is { } ext && PrisonGame.Instance != null) f *= ext.Weight(PrisonGame.Instance.story.pulse);
            return f;
        }

        /// <summary>
        /// Casts an inmate for this event: anyone eligible, but those who starred in a story recently are much less
        /// likely to be picked again (StoryExt.perPawnCooldownDays). Marks the one picked.
        /// </summary>
        protected Pawn RandomInmate(Map map, System.Func<Pawn, bool> validator = null)
        {
            float cooldown = (def.GetModExtension<StoryExt>()?.perPawnCooldownDays ?? 0f) * GenDate.TicksPerDay;
            int now = Find.TickManager.TicksGame;
            Pawn picked = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && (validator == null || validator(p)))
                .RandomElementByWeightWithFallback(p => (now - p.Inmate().lastStoryTick < cooldown ? 0.1f : 1f) * Wants.StoryWeight(p.Inmate()));
            if (picked != null) picked.Inmate().lastStoryTick = now;
            return picked;
        }
    }

    /// <summary>A package is thrown over the wall into the yard. Whoever gets to it first...</summary>
    public class IncidentWorker_ContrabandDrop : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            List<IntVec3> yard = PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Yard);
            if (yard == null || yard.Count == 0) return false;
            IntVec3 cell = yard.RandomElement();
            int items = Rand.RangeInclusive(1, 3);
            var dropped = new List<Thing>();
            for (int i = 0; i < items; i++)
            {
                if (!ContrabandUtility.AllContraband.TryRandomElementByWeight(d => ContrabandUtility.Ext(d).smuggleWeight, out ThingDef def)) break;
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                t.stackCount = Mathf.Min(ContrabandUtility.Ext(def).stackCount, def.stackLimit);
                if (GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near, out Thing placed)) dropped.Add(placed);
            }
            if (dropped.Count == 0) return false;
            PrisonGame.Instance.stats.contrabandSmuggled += dropped.Count;
            PrisonGame.Instance.today.contrabandSmuggled += dropped.Count;
            SendStandardLetter(def.letterLabel, def.letterText, def.letterDef, parms, new LookTargets(dropped));
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_ContrabandDrop".Translate());
            return true;
        }
    }

    /// <summary>Two inmates fall out badly (debt, insult, old feud). Expect fights.</summary>
    public class IncidentWorker_Grudge : IncidentWorker_Prison
    {
        protected override int MinInmates => 2;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            // Two people who actually cross paths: the same wing (a cell, a canteen, a yard), not yet in a story.
            Pawn a = RandomInmate(map, x => !PrisonGame.Instance.story.InArc(x));
            if (a == null) return false;
            SecurityLevelDef sec = a.Inmate().security;
            Pawn b = RandomInmate(map, x => x != a && x.Inmate().security == sec && !PrisonRelations.Friends(a, x) && !PrisonGame.Instance.story.InArc(x));
            if (b == null) return false;
            string reason = ("RP_GrudgeReason" + Rand.RangeInclusive(1, 4)).Translate();
            // The grudge is the start of a feud (its own letter asks what to do about it).
            if (FeudArc.Begin(a, b, reason) == null) return false;
            ThoughtDef grudge = RPDefOf.RP_Grudge;
            a.needs.mood?.thoughts.memories.TryGainMemory(grudge, b);
            b.needs.mood?.thoughts.memories.TryGainMemory(grudge, a);
            a.Inmate().tension = Mathf.Min(100f, a.Inmate().tension + 15f);
            b.Inmate().tension = Mathf.Min(100f, b.Inmate().tension + 15f);
            return true;
        }
    }

    /// <summary>A notorious criminal is sentenced to your prison: big grant, big trouble.</summary>
    public class IncidentWorker_CelebrityInmate : IncidentWorker_Prison
    {
        protected override int MinInmates => 0;

        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && SpecialMeasures.IntakeLimit != 0 && Unlocks.IsUnlocked(Celebrities.Maximum)
            && IntakeSystem.FreeBedCount((Map)parms.target, Celebrities.Maximum) > 0;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Pawn p = Celebrities.Admit(map, "RP_Chr_AdmittedCelebrity", 4);
            if (p == null) return false;
            SendStandardLetter(def.letterLabel, def.letterText.Formatted(p.Named("PAWN"), Money.Format(Celebrities.Premium(Celebrities.Maximum, 4)).Named("GRANT")).AdjustedFor(p), def.letterDef, parms, p);
            return true;
        }
    }

    /// <summary>Notorious arrivals: the celebrity incident's, and the legendary transfers a flagship prison is sent (see <see cref="SpecialMeasures"/>).</summary>
    public static class Celebrities
    {
        public static SecurityLevelDef Maximum => RPSecurityDefOf.RP_Maximum;

        public static int Premium(SecurityLevelDef sec, int factor) => sec.intakeGrant * factor;

        /// <summary>A notorious inmate into a free maximum-security bed (their wing first), the state's premium paid (null: no bed).</summary>
        public static Pawn Admit(Map map, string chronicleKey, int premiumFactor)
        {
            SecurityLevelDef max = Maximum;
            // Only beds a maximum-security inmate may use (their wing first), and of the sex the wing policy has beds for.
            List<Building_Bed> beds = IntakeSystem.FreePrisonerBeds(map, max);
            if (beds.Count == 0) return null;
            Pawn p = InmateGenerator.Generate(max, PrisonGame.Instance.intake.nextInmateNumber++, IntakeSystem.SexWithABed(beds));
            beds = IntakeSystem.FreePrisonerBeds(map, max, p.gender);
            if (beds.Count == 0)
            {
                // (Not so while the sex was chosen from the beds: no bed of theirs is no one to admit.)
                if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(p);
                return null;
            }
            CompInmate c = p.Inmate();
            c.reputations.Clear();
            c.reputations.Add(ReputationDefOf.RP_Instigator);
            c.reputations.Add(RPDefOf.RP_Notorious);
            IntVec3 spot = IntakeSystem.IntakeSpot(map);
            GenSpawn.Spawn(p, spot, map);
            InmateGenerator.MakeInmate(p, beds[beds.Count - 1]);
            Story.Admitted(p, chronicleKey, 3);
            PrisonGame.Instance.ledger.Add(RPDefOf.RP_IntakeGrants, Premium(max, premiumFactor), "RP_CelebrityNote".Translate(p.LabelShort));
            PrisonGame.Instance.stats.admitted++;
            return p;
        }
    }

    /// <summary>An appeal succeeds: the inmate will be released at the next daily settlement.</summary>
    public class IncidentWorker_Appeal : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Pawn p = RandomInmate((Map)parms.target, x => !x.Inmate().IsLifer && x.Inmate().TermDays > 5);
            if (p == null) return false;
            CompInmate c = p.Inmate();
            c.sentenceDays = Mathf.Max(1, Mathf.CeilToInt(c.DaysServed));
            c.daysAdded = 0;
            SendStandardLetter(def.letterLabel, def.letterText.Formatted(p.Named("PAWN")).AdjustedFor(p), def.letterDef, parms, p);
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 2, "RP_Chr_Appeal".Translate(p.LabelShort), p);
            return true;
        }
    }

    /// <summary>The state changes what it pays per inmate for a while.</summary>
    public class IncidentWorker_FundingChange : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            PrisonGame game = PrisonGame.Instance;
            bool cut = Rand.Chance(0.55f);
            game.paymentModifier = cut ? 0.8f : 1.2f;
            game.paymentModifierUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay * Rand.RangeInclusive(3, 6);
            string text = (cut ? "RP_FundingCutText" : "RP_FundingRaiseText").Translate();
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, (cut ? "RP_Chr_FundingCut" : "RP_Chr_FundingRaise").Translate());
            SendStandardLetter((cut ? "RP_FundingCutLabel" : "RP_FundingRaiseLabel").Translate(), text, cut ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent, parms, LookTargets.Invalid);
            return true;
        }
    }

    /// <summary>The state asks you to take a batch of maximum-security transfers, now, for a premium.</summary>
    public class IncidentWorker_TransferRequest : IncidentWorker_Prison
    {
        protected override int MinInmates => 0;

        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && Unlocks.IsUnlocked(RPSecurityDefOf.RP_Maximum) && IntakeSystem.FreeBedCount((Map)parms.target, RPSecurityDefOf.RP_Maximum) >= 2;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
            int free = IntakeSystem.FreeBedCount(map, max);
            int count = Mathf.Clamp(Rand.RangeInclusive(2, 5), 2, free);
            int premium = count * max.intakeGrant * 2;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_TransferRequest", def.letterLabel,
                def.letterText.Formatted(count.Named("COUNT"), Money.Format(premium).Named("PREMIUM")), map);
            letter.amount = count;
            letter.money = premium;
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    /// <summary>A decision the warden is asked to make (decision letters). Behaviour lives in the worker.</summary>
    public class PrisonDecisionDef : Def
    {
        public System.Type workerClass = typeof(PrisonDecisionWorker);

        /// <summary>
        /// The letter warns of something with a deadline of its own (an inspection, a demand, a charge, a loan's night, a fight that is coming): it is never held back by the day's budget
        /// of decisions (<see cref="StoryLetters"/>), whatever its answer time is.
        /// </summary>
        public bool urgent;

        /// <summary>
        /// The warden may answer letters of this decision once and for all (<see cref="StandingOrders"/>): routine decisions only, whose answers mean the same whatever the case. Never an urgent
        /// decision, never one whose answers depend on the case.
        /// </summary>
        public bool standingOrderAllowed;

        /// <summary>The answers (option keys) a standing order may be for; empty: any of them (the adjudication sheet's "open the sheet" is no answer to give for good).</summary>
        public List<string> standingOrderOptions = new List<string>();

        [Unsaved] private PrisonDecisionWorker workerInt;

        public PrisonDecisionWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (PrisonDecisionWorker)System.Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }

        public static PrisonDecisionDef Named(string defName) => DefDatabase<PrisonDecisionDef>.GetNamed(defName);
    }

    public class PrisonDecisionWorker
    {
        public PrisonDecisionDef def;

        /// <summary>
        /// Keyed labels of the choices, for decisions with more than accept/decline (null: accept/decline). A choice
        /// runs <see cref="Choose"/> with its index; an unanswered letter that times out runs <see cref="TimeoutChoice"/>.
        /// </summary>
        public virtual IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => null;

        public virtual void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
        }

        public virtual int TimeoutChoice => 0;

        public virtual int TimeoutOption(ChoiceLetter_PrisonDecision l) => TimeoutChoice;

        /// <summary>A choice that cannot be taken right now (reason shown on the button), or null.</summary>
        public virtual string Disabled(ChoiceLetter_PrisonDecision l, int option) => null;

        public virtual void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
        }

        /// <summary>Refused, or left unanswered until the letter timed out.</summary>
        public virtual void Decline(ChoiceLetter_PrisonDecision l, Map map)
        {
        }
    }

    /// <summary>A letter with Accept / Decline that runs a prison decision.</summary>
    public class ChoiceLetter_PrisonDecision : ChoiceLetter
    {
        public PrisonDecisionDef decision;
        public int amount;
        public int money;
        public Map map;
        public bool answered;
        public NeedDef need;
        /// <summary>The story arc this letter belongs to (RP_Decision_Arc), or 0.</summary>
        public int arcId;
        /// <summary>A name the decision is about that outlives the pawn (the inquiry's victim), or null.</summary>
        public string note;
        public List<Pawn> pawns = new List<Pawn>();
        /// <summary>While the letter waits for the next day (see <see cref="StoryLetters"/>): the answer time it gets when delivered (-1: none).</summary>
        public int deferredTimeout = -1;
        /// <summary>The tick it was put off until the next day (-1: it was not).</summary>
        public int heldTick = -1;
        /// <summary>The thing id of the pawn the letter is about when it is one among its <see cref="pawns"/> (the informant of a case's letter), or -1. Ids outlive the pawns on the list, which are dropped from it when they are gone.</summary>
        public int subjectId = -1;

        /// <summary>
        /// The "do this from now on" box is ticked (<see cref="StandingOrders"/>): the answer given by hand becomes the decision's standing order. Not saved: a letter is unticked after a load (a letter
        /// postponed and reopened in the same session keeps its tick).
        /// </summary>
        public bool standingTicked;
        /// <summary>
        /// A standing order was weighed for this letter and could not answer it (its line is in the text): it is not weighed again when the letter is delivered after waiting. Saved: the line told
        /// the warden the letter is theirs, and a reload must not weigh it again (a second line, or an order answering a letter the warden was told to answer).
        /// </summary>
        public bool standingTried;
        /// <summary>The letter is being answered by a standing order (the chronicle says so).</summary>
        public bool byStandingOrder;

        public override bool CanDismissWithRightClick => false;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }
                IReadOnlyList<string> options = decision?.Worker.Options(this);
                bool standing = StandingOrders.Offered(this);
                if (options != null)
                {
                    for (int i = 0; i < options.Count; i++)
                    {
                        int index = i;
                        string key = options[i];
                        var opt = new DiaOption(key.Translate())
                        {
                            action = () =>
                            {
                                answered = true;
                                StandingOrders.AnsweredByHand(this, key);
                                PrisonDecisions.Choose(this, index);
                                Find.LetterStack.RemoveLetter(this);
                            },
                            resolveTree = true,
                        };
                        string why = decision.Worker.Disabled(this, i);
                        if (why != null) opt.Disable(why);
                        yield return opt;
                    }
                    if (standing) yield return new DiaOption_StandingOrder(this);
                    yield return Option_Postpone;
                    yield break;
                }
                var accept = new DiaOption(StandingOrders.AcceptKey.Translate())
                {
                    action = () =>
                    {
                        answered = true;
                        StandingOrders.AnsweredByHand(this, StandingOrders.AcceptKey);
                        PrisonDecisions.Accept(this);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                var decline = new DiaOption(StandingOrders.DeclineKey.Translate())
                {
                    action = () =>
                    {
                        answered = true;
                        StandingOrders.AnsweredByHand(this, StandingOrders.DeclineKey);
                        PrisonDecisions.Decline(this);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                yield return accept;
                yield return decline;
                if (standing) yield return new DiaOption_StandingOrder(this);
                yield return Option_Postpone;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                string decisionName = null;
                Scribe_Values.Look(ref decisionName, "decision");
                decision = ResolveDecision(decisionName);
            }
            else Scribe_Defs.Look(ref decision, "decision");
            Scribe_Values.Look(ref amount, "amount");
            Scribe_Values.Look(ref money, "money");
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref answered, "answered");
            Scribe_Defs.Look(ref need, "need");
            Scribe_Values.Look(ref arcId, "arcId");
            Scribe_Values.Look(ref note, "note");
            Scribe_Values.Look(ref deferredTimeout, "deferredTimeout", -1);
            Scribe_Values.Look(ref heldTick, "heldTick", -1);
            Scribe_Values.Look(ref subjectId, "subjectId", -1);
            Scribe_Values.Look(ref standingTried, "standingTried");
            if (Scribe.mode == LoadSaveMode.Saving) pawns?.RemoveAll(x => x == null || x.Destroyed);
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) pawns ??= new List<Pawn>();
        }

        /// <summary>Timed out unanswered: that is a no.</summary>
        public override void Removed()
        {
            base.Removed();
            if (!answered && TimeoutActive && TimeoutPassed) Lapse();
        }

        /// <summary>
        /// Decisions that no longer exist, and the one a letter of theirs still in a save becomes: the plain closed notice of an arc that is over (its only choice is "arc over", which does
        /// nothing). Done here and not with vanilla's back-compatibility converters, which are skipped for a save made in the same game build with the same mod list: the usual one.
        /// </summary>
        private static readonly Dictionary<string, string> RemovedDecisions = new Dictionary<string, string>
        {
            ["RP_Decision_TrialWitnesses"] = "RP_Decision_Arc",
            ["RP_Decision_TrialInformant"] = "RP_Decision_Arc",
        };

        /// <summary>The decision a saved name stands for (null for none, or one that is gone for good).</summary>
        public static PrisonDecisionDef ResolveDecision(string name)
        {
            if (name.NullOrEmpty() || name == "null") return null;
            if (RemovedDecisions.TryGetValue(name, out string replacement)) name = replacement;
            PrisonDecisionDef def = DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(name);
            if (def == null) Log.Warning($"[RimPrison] a decision letter in the save is for a decision that no longer exists ({name}): it will not offer anything.");
            return def;
        }

        /// <summary>A letter that waited too long for the day's budget: it is answered as one that timed out is, without ever reaching the desk.</summary>
        public void Lapse()
        {
            if (answered) return;
            answered = true;
            if (decision?.Worker.Options(this) != null) PrisonDecisions.Choose(this, decision.Worker.TimeoutOption(this));
            else PrisonDecisions.Decline(this);
        }

        /// <summary>Makes a decision letter (call ReceiveLetter on it after filling in the payload).</summary>
        public static ChoiceLetter_PrisonDecision Make(string decisionDef, TaggedString label, TaggedString text, Map map, LookTargets targets = null)
        {
            var letter = (ChoiceLetter_PrisonDecision)LetterMaker.MakeLetter(label, text, RPDefOf.RP_DecisionLetter, targets);
            letter.decision = PrisonDecisionDef.Named(decisionDef);
            letter.map = map;
            return letter;
        }

        public bool Is(string decisionDef) => decision?.defName == decisionDef;
    }

    public static class PrisonDecisions
    {
        public static void Choose(ChoiceLetter_PrisonDecision l, int option)
        {
            Map map = l.map ?? Find.AnyPlayerHomeMap;
            if (map == null) return;
            IReadOnlyList<string> options = l.decision?.Worker.Options(l);
            if (options != null && option >= 0 && option < options.Count) RecordDecision(l, options[option].Translate());
            l.decision?.Worker.Choose(l, map, option);
        }

        /// <summary>Every warden decision goes into the chronicle with what was chosen (or left to time out).</summary>
        private static void RecordDecision(ChoiceLetter_PrisonDecision l, TaggedString choice)
        {
            if (l.decision == null || (l.arcId != 0 && PrisonGame.Instance?.story.ArcById(l.arcId) is not { done: false })) return;
            string how = l.byStandingOrder ? "RP_Chr_DecisionStanding".Translate(choice) : l.TimeoutActive && l.TimeoutPassed ? "RP_Chr_DecisionTimedOut".Translate(choice) : choice.Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_Decision".Translate(l.Label, how), l.pawns?.ToArray() ?? new Pawn[0]);
        }

        public static void Decline(ChoiceLetter_PrisonDecision l)
        {
            RecordDecision(l, "RP_Chr_Declined".Translate());
            Map map = l.map ?? Find.AnyPlayerHomeMap;
            if (map != null) l.decision?.Worker.Decline(l, map);
        }

        public static void Accept(ChoiceLetter_PrisonDecision l)
        {
            RecordDecision(l, "RP_Chr_Accepted".Translate());
            Map map = l.map ?? Find.AnyPlayerHomeMap;
            if (map != null) l.decision?.Worker.Accept(l, map);
        }
    }

    public class PrisonDecisionWorker_TransferRequest : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
            SecurityLevelDef max = RPSecurityDefOf.RP_Maximum;
            List<Building_Bed> beds = IntakeSystem.FreePrisonerBeds(map, max);
            IntVec3 spot = IntakeSystem.IntakeSpot(map);
            var arrived = new List<Pawn>();
            int count = Mathf.Min(l.amount, beds.Count);
            // The premium is paid per inmate who actually arrives.
            if (count == 0)
            {
                Messages.Message("RP_TransferNoBeds".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }
            int premium = l.money * count / l.amount;
            for (int i = 0; i < count; i++)
            {
                // Each is of the sex the wing policy has beds for, in the best bed of theirs (the wing first).
                Pawn p = InmateGenerator.Generate(max, PrisonGame.Instance.intake.nextInmateNumber++, IntakeSystem.SexWithABed(beds));
                Building_Bed bed = Cellmates.PickBed(beds, p.Inmate());
                if (!CellFinder.TryFindRandomCellNear(spot, map, 4, c => c.Standable(map), out IntVec3 cell)) cell = spot;
                GenSpawn.Spawn(p, cell, map);
                InmateGenerator.MakeInmate(p, bed);
                Story.Admitted(p, "RP_Chr_AdmittedTransfer");
                Kin.OnArrived(p, map);
                arrived.Add(p);
            }
            PrisonGame.Instance.stats.admitted += arrived.Count;
            PrisonGame.Instance.ledger.Add(RPDefOf.RP_IntakeGrants, premium, "RP_TransferNote".Translate(arrived.Count));
            Messages.Message("RP_TransferArrived".Translate(arrived.Count, Money.Format(premium)), new LookTargets(arrived), MessageTypeDefOf.NeutralEvent);
        }
    }

    /// <summary>
    /// The staff's demand: pay the bonus, agree to fix what they are angry about (<see cref="StaffAgreements"/>) or refuse and they walk out - which is also what an unanswered letter is. Where the warden has broken their
    /// word over the grievance before the bonus is not offered (<see cref="StaffAgreements.BrokenBefore"/>).
    /// </summary>
    public class PrisonDecisionWorker_StaffStrike : PrisonDecisionWorker
    {
        private const string Pay = "RP_StaffStrike_Pay", Agree = "RP_StaffStrike_Agree", Refuse = "RP_StaffStrike_Refuse";
        private static readonly string[] WithBonus = { Pay, Agree, Refuse }, WithoutBonus = { Agree, Refuse };

        /// <summary>What the letter is about (the one saved with it; a letter from before agreements has none and is worked out again).</summary>
        public static StaffGrievance GrievanceOf(ChoiceLetter_PrisonDecision l, Map map)
        {
            if (StaffAgreements.ParseLetterNote(l.note, out StaffGrievance g, out _)) return g;
            return IncidentWorker_StaffStrike.Grievance(map, IncidentWorker_StaffStrike.Staff(map));
        }

        /// <summary>Who speaks for the staff in the promise: the one the letter named, if they still can; else whoever is the representative now.</summary>
        private static Pawn RepOf(ChoiceLetter_PrisonDecision l, Map map, StaffGrievance g) =>
            l.pawns.FirstOrDefault(p => p is { Dead: false } && p.GetComp<CompStaff>() is { } sc && StaffAgreements.Grieves(sc, g, sc.role)) ?? StaffAgreements.Representative(map, g);

        private static Map MapOf(ChoiceLetter_PrisonDecision l) => l.map ?? Find.AnyPlayerHomeMap;

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => MapOf(l) is { } map && StaffAgreements.BrokenBefore(GrievanceOf(l, map)) ? WithoutBonus : WithBonus;

        /// <summary>No answer is a refusal, the last of the choices.</summary>
        public override int TimeoutOption(ChoiceLetter_PrisonDecision l) => Options(l).Count - 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            IReadOnlyList<string> options = Options(l);
            return option >= 0 && option < options.Count && options[option] == Agree && MapOf(l) is { } map ? StaffAgreements.CannotAgree(map, GrievanceOf(l, map), RepOf(l, map, GrievanceOf(l, map))) : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            IReadOnlyList<string> options = Options(l);
            string choice = option >= 0 && option < options.Count ? options[option] : Refuse;
            if (choice == Pay) IncidentWorker_StaffStrike.PayBonus(map, l.money);
            else if (choice == Agree)
            {
                StaffGrievance g = GrievanceOf(l, map);
                Pawn rep = RepOf(l, map, g);
                StaffAgreements.ParseLetterNote(l.note, out _, out int[] baselines);
                if (StaffAgreements.CannotAgree(map, g, rep) != null || !StaffAgreements.Agree(map, PrisonGame.Instance, g, rep, baselines)) IncidentWorker_StaffStrike.Strike(map);
            }
            else IncidentWorker_StaffStrike.Strike(map);
        }
    }

    public class PrisonDecisionWorker_InmateDemand : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
            if (l.need != null) DemandSystem.AcceptLetter(PrisonGame.Instance, l.need, l.pawns.FirstOrDefault(p => p != null && !p.Dead), DefDatabase<SecurityLevelDef>.GetNamedSilentFail(l.note ?? ""), l.amount);
        }

        public override void Decline(ChoiceLetter_PrisonDecision l, Map map)
        {
            if (l.need != null) DemandSystem.Refuse(map, l.need, l.pawns.FirstOrDefault(p => p != null && !p.Dead));
        }
    }

    public class PrisonDecisionWorker_RiotNegotiation : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map) => RiotNegotiation.Negotiate(map, l);
    }
}
