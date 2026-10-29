using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPOldAgeDefOf
    {
        public static ThoughtDef RP_ChaplainAtBedside;
        public static MailKindDef RP_Mail_CondolenceOld;
        public static MailKindDef RP_Mail_CompassionateRelease;

        static RPOldAgeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPOldAgeDefOf));
    }

    public enum CareChoice : byte
    {
        /// <summary>The letter is out and not answered yet.</summary>
        Pending,
        Treat,
        Transfer,
        Release,
        Nothing,
    }

    /// <summary>What the warden decided about one chronic condition of one inmate (kept on the inmate: <see cref="CompInmate.care"/>).</summary>
    public class CareDecision : IExposable
    {
        /// <summary>The hediff's def name.</summary>
        public string condition;
        public CareChoice choice;
        /// <summary>When the condition was found (the letter went out) and, once it is answered, when it was.</summary>
        public int foundTick = -1, tick = -1;
        /// <summary>What the letter said: a cure exists (a vanilla surgery), and the condition kills if it is left.</summary>
        public bool treatable, terminal;
        /// <summary>The body part the condition sat on (its index in the body), -1 for a condition of the whole body: a carcinoma that destroys its part leaves a missing part as the culprit of the death, and this is how it is known.</summary>
        public int partIndex = -1;
        /// <summary>The hospice: when the chaplain last came (-1 never), and whether they have been put in a bed of the wing.</summary>
        public int visitTick = -1;
        public bool admitted;

        /// <summary>Care was chosen for a dying inmate with no cure to give: comfort, the hospice bed and the chaplain.</summary>
        public bool Palliative => choice == CareChoice.Treat && terminal && !treatable;

        public void ExposeData()
        {
            Scribe_Values.Look(ref condition, "condition");
            Scribe_Values.Look(ref choice, "choice");
            Scribe_Values.Look(ref foundTick, "found", -1);
            Scribe_Values.Look(ref tick, "tick", -1);
            Scribe_Values.Look(ref treatable, "treatable");
            Scribe_Values.Look(ref terminal, "terminal");
            Scribe_Values.Look(ref partIndex, "part", -1);
            Scribe_Values.Look(ref visitTick, "visit", -1);
            Scribe_Values.Look(ref admitted, "admitted");
        }

        public string Describe() => $"{condition}:{choice}:{foundTick}:{tick}:{treatable}{terminal}:{partIndex}:{visitTick}:{admitted}";
    }

    /// <summary>One chronic condition that matters (vanilla 1.7 Core, no DLC): whether it kills if it is left, the surgery that cures it, and how far gone it is when the hospice takes them.</summary>
    public class ChronicCondition
    {
        public string defName;
        public bool terminal;
        /// <summary>The recipe that removes it (a vanilla surgery), or null when nothing cures it.</summary>
        public string recipe;
        /// <summary>Severity from which a dying inmate is in the hospice bed (terminal conditions).</summary>
        public float dyingAt = 0.5f;

        public HediffDef Def => DefDatabase<HediffDef>.GetNamedSilentFail(defName);
    }

    /// <summary>
    /// Growing old inside (gap analysis 5, 2.4). An inmate who gains a chronic condition that matters (a vanilla chronic disease or age condition, not a scar or a missing part) is the warden's decision, once per
    /// inmate and condition: treat them here (a surgery bill where vanilla has one, else medicine and care; for a dying inmate with no cure, palliative care and the hospice bed), send them to the state's hospital
    /// (a transfer, with the cause on the record), release them on compassionate grounds (an old or a dying inmate who has served enough), or do nothing. A death of that condition after "nothing" when a cure existed is neglect
    /// (the inquiry, as with the heart attack the paramedics were refused); after "nothing" when nothing could be done, or after care, it is not. A death of age or illness after years inside is a milestone in the chronicle,
    /// the friends inside grieve and the family writes (see <see cref="CondolenceSystem"/>).
    /// </summary>
    public static class OldAge
    {
        public const int OldYears = 65;
        /// <summary>A lifer may be released on compassionate grounds after this many days; another inmate after this share of the sentence.</summary>
        public const int LiferStretchDays = 365;
        public const float ServedShare = 0.5f;
        /// <summary>A death of age or illness after this many days served (or as a lifer) is a milestone.</summary>
        public const int MilestoneDays = 365;
        public const int SurgeryCost = 800, CareCost = 300;
        /// <summary>At most this many letters in one hourly pass (a prison full of old inmates is not answered all at once).</summary>
        public const int MaxLettersPerPass = 2;
        public const int ChaplainEveryTicks = GenDate.TicksPerDay;

        public static readonly ChronicCondition[] Conditions =
        {
            new ChronicCondition { defName = "Carcinoma", terminal = true, recipe = "ExciseCarcinoma", dyingAt = 0.6f },
            new ChronicCondition { defName = "HeartArteryBlockage", terminal = true, dyingAt = 0.5f },
            new ChronicCondition { defName = "Asthma" },
            new ChronicCondition { defName = "BadBack" },
            new ChronicCondition { defName = "Frail" },
            new ChronicCondition { defName = "Cataract" },
            new ChronicCondition { defName = "Blindness" },
            new ChronicCondition { defName = "HearingLoss" },
            new ChronicCondition { defName = "Dementia" },
            new ChronicCondition { defName = "Alzheimers" },
        };

        public static ChronicCondition Row(string defName)
        {
            foreach (ChronicCondition row in Conditions)
                if (row.defName == defName) return row;
            return null;
        }

        public static CareDecision Record(CompInmate c, string condition)
        {
            foreach (CareDecision d in c.care)
                if (d.condition == condition) return d;
            return null;
        }

        public static RecipeDef RecipeOf(ChronicCondition row) => row.recipe == null ? null : DefDatabase<RecipeDef>.GetNamedSilentFail(row.recipe);

        /// <summary>A vanilla surgery cures it now: the recipe is there, its research done, and it can be done on the part the condition is on.</summary>
        public static bool Treatable(Pawn p, Hediff h, ChronicCondition row)
        {
            RecipeDef recipe = RecipeOf(row);
            return recipe != null && recipe.AvailableNow && h.Part != null && recipe.Worker.GetPartsToApplyOn(p, recipe).Contains(h.Part);
        }

        // ---- the diagnosis ------------------------------------------------------------------------------------------------

        /// <summary>Hourly: an inmate with a condition that matters and no decision about it gets the letter; the dying are looked after.</summary>
        /// <param name="only">Just this inmate (for checks that must not write letters for the real ones).</param>
        public static void Hourly(PrisonGame game, Map map, Pawn only = null)
        {
            int sent = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (only != null && p != only) continue;
                if (p.Dead || p.Inmate() is not { activeFlag: true } c || ShuttleSystem.OnTheWayOut(p)) continue;
                if (sent < MaxLettersPerPass && Diagnose(game, map, p, c)) sent++;
                Hospice.Hourly(map, p, c);
            }
        }

        private static bool Diagnose(PrisonGame game, Map map, Pawn p, CompInmate c)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs)
            {
                if (Row(h.def.defName) is not { } row || Record(c, row.defName) != null) continue;
                SendLetter(game, map, p, c, h, row);
                return true;
            }
            return false;
        }

        private static void SendLetter(PrisonGame game, Map map, Pawn p, CompInmate c, Hediff h, ChronicCondition row)
        {
            bool treatable = Treatable(p, h, row);
            c.care.Add(new CareDecision { condition = row.defName, foundTick = Find.TickManager.TicksGame, treatable = treatable, terminal = row.terminal, partIndex = h.Part == null ? -1 : p.RaceProps.body.GetIndexOfPart(h.Part) });
            float served = c.DaysServed;
            string sentence = c.IsLifer ? "RP_OldAge_SentenceLife".Translate(served.ToString("F0")).Resolve()
                : "RP_OldAge_SentenceTerm".Translate(c.TermDays, served.ToString("F0"), Mathf.Max(0f, c.TermDays - served).ToString("F0")).Resolve();
            string what = (treatable ? "RP_OldAge_CanTreat" : row.terminal ? "RP_OldAge_Palliative" : "RP_OldAge_Lived").Translate().Resolve();
            string release = ReleaseRefusal(game, p, c, row.terminal) is { } why ? "RP_OldAge_ReleaseNo".Translate(why).Resolve() : "RP_OldAge_ReleaseYes".Translate().Resolve();
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_OldAge", "RP_OldAgeLabel".Translate(p.LabelShort, h.def.label),
                "RP_OldAgeText".Translate(p.LabelShort, p.ageTracker.AgeBiologicalYears, h.def.label, sentence, what, Money.Format(treatable ? SurgeryCost : CareCost), Money.Format(InmateUtility.TransferCost), release),
                map, p);
            letter.pawns.Add(p);
            letter.note = row.defName;
            letter.subjectId = p.thingIDNumber;
            letter.StartTimeout(3 * GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_Diagnosed".Translate(p.LabelShort, h.def.label), p);
        }

        // ---- compassionate release ------------------------------------------------------------------------------------------------

        /// <summary>Why this inmate cannot be released on compassionate grounds (the reason, for the button and the letter), or null when they can: old or dying, and served enough of the sentence.</summary>
        public static string ReleaseRefusal(PrisonGame game, Pawn p, CompInmate c, bool dying)
        {
            if (ShuttleSystem.OnTheWayOut(p)) return "RP_OldAge_NoGone".Translate();
            if (p.ageTracker.AgeBiologicalYears < OldYears && !dying) return "RP_OldAge_NoYoung".Translate(OldYears);
            if (Trials.OnRemand(game, p)) return "RP_OldAge_NoRemand".Translate();
            if (DeathRowSystem.Ext(c) != null || c.executing || c.executionTick >= 0) return "RP_OldAge_NoDeathRow".Translate();
            float served = c.DaysServed;
            if (c.IsLifer && served < LiferStretchDays) return "RP_OldAge_NoLifer".Translate(LiferStretchDays, served.ToString("F0"));
            if (!c.IsLifer && served < c.TermDays * ServedShare) return "RP_OldAge_NoShare".Translate(ServedShare.ToStringPercent(), served.ToString("F0"), c.TermDays);
            // (Vanilla's release walks them to the gate, which a pawn on the floor cannot do: it waits until they are up, as a served sentence does.)
            if (p.Downed) return "RP_OldAge_NoDowned".Translate();
            return null;
        }

        // ---- the choices ------------------------------------------------------------------------------------------------

        /// <summary>Treat them here. Returns false (nothing done) when the prison cannot pay.</summary>
        public static bool Treat(PrisonGame game, Pawn p, CompInmate c, CareDecision rec, Hediff h)
        {
            ChronicCondition row = Row(rec.condition);
            // (The condition is gone since the letter: nothing to treat, nothing charged. And asked again at the answer: a cure that has gone since the letter is care only, and the death of it is not the prison's neglect.)
            if (h == null) rec.treatable = false;
            if (h == null) return false;
            rec.treatable = rec.treatable && row != null && Treatable(p, h, row);
            int cost = rec.treatable ? SurgeryCost : CareCost;
            if (!game.ledger.TrySpend(RPDefOf.RP_Supplies, cost, "RP_OldAgeNote".Translate(p.LabelShort, h.def.label))) return false;
            if (rec.treatable)
            {
                RecipeDef recipe = RecipeOf(row);
                if (!p.BillStack.Bills.Any(b => b.recipe == recipe)) HealthCardUtility.CreateSurgeryBill(p, recipe, h.Part, null, sendMessages: false);
            }
            // Medicine and care: the doctors tend them with at least the ordinary medicine.
            if (p.playerSettings != null && p.playerSettings.medCare < MedicalCareCategory.NormalOrWorse) p.playerSettings.medCare = MedicalCareCategory.NormalOrWorse;
            rec.choice = CareChoice.Treat;
            rec.tick = Find.TickManager.TicksGame;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, (rec.Palliative ? "RP_Chr_OldAgePalliative" : rec.treatable ? "RP_Chr_OldAgeSurgery" : "RP_Chr_OldAgeCare").Translate(p.LabelShort, h.def.label), p);
            return true;
        }

        public static bool Transfer(PrisonGame game, Pawn p, CompInmate c, CareDecision rec)
        {
            if (ShuttleSystem.OnTheWayOut(p) || game.ledger.funds < InmateUtility.TransferCost) return false;
            InmateUtility.TransferOut(p, c, "RP_Cause_StateHospital");
            // (TransferOut refuses quietly; they are on the way out, or off the map, only when it went through.)
            if (p.Spawned && !ShuttleSystem.OnTheWayOut(p)) return false;
            rec.choice = CareChoice.Transfer;
            rec.tick = Find.TickManager.TicksGame;
            return true;
        }

        public static bool CompassionateRelease(PrisonGame game, Pawn p, CompInmate c, CareDecision rec, Hediff h)
        {
            if (ReleaseRefusal(game, p, c, rec.terminal) != null) return false;
            rec.choice = CareChoice.Release;
            rec.tick = Find.TickManager.TicksGame;
            bool lifer = c.IsLifer;
            string name = p.LabelShort, days = c.DaysServed.ToString("F0"), condition = h?.def.label ?? rec.condition;
            // The family's letter (a living relative who is still in touch): written to the warden, a day or three after.
            string from = Relatives.Of(c).Find(r => r.CanVisit)?.Label;
            ReleaseSystem.Release(game, p, record: false, cause: "RP_Cause_Compassionate");
            // A lifer who walks out is a big story beat: a milestone in the chronicle.
            Story.Record(RPChronicleDefOf.RP_Chr_Release, lifer ? 3 : 2, (lifer ? "RP_Chr_ReleasedCompassionateLifer" : "RP_Chr_ReleasedCompassionate").Translate(name, days, condition), p);
            if (from != null)
                game.story.condolences.Add(new Condolence
                {
                    kind = RPOldAgeDefOf.RP_Mail_CompassionateRelease,
                    name = name,
                    from = from,
                    dueTick = Find.TickManager.TicksGame + Rand.RangeInclusive(CondolenceSystem.MinDays, CondolenceSystem.MaxDays) * GenDate.TicksPerDay,
                });
            return true;
        }

        public static void DoNothing(Pawn p, CareDecision rec, Hediff h)
        {
            rec.choice = CareChoice.Nothing;
            rec.tick = Find.TickManager.TicksGame;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_OldAgeNothing".Translate(p.LabelShort, h?.def.label ?? rec.condition), p);
        }

        // ---- a death ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Whether this is a death of age or illness and not of a wound, violence or want (the dying hediff is a disease, a chronic condition, a heart attack or the part a condition destroyed, or there is none and the inmate
        /// has one of the conditions and no injury), and the decision about the condition it was of (null when none was made about it).
        /// </summary>
        /// <param name="ofCondition">The cause is one of the conditions that matter (and not another disease): only that, or old age, makes it a death of age.</param>
        public static bool IsNatural(Pawn victim, Hediff culprit, DamageInfo? dinfo, out CareDecision cause, out bool ofCondition)
        {
            cause = null;
            ofCondition = false;
            if (dinfo.HasValue || victim.Inmate() is not { } c) return false;
            if (culprit is Hediff_MissingPart missing)
            {
                // Vanilla's carcinoma, at its last stage, destroys the part it is on and the death comes from the missing part: the part the warden was told about is on the record.
                cause = DestroyedBy(victim, c, missing);
                ofCondition = cause != null;
                return ofCondition;
            }
            if (culprit != null)
            {
                if (DeathAftermath.IsNeglect(culprit.def) || culprit is Hediff_Injury) return false;
                string name = culprit.def == IncidentWorker_MedicalEmergency.HeartAttack ? "HeartArteryBlockage" : culprit.def.defName;
                ofCondition = Row(name) != null || culprit.def == IncidentWorker_MedicalEmergency.HeartAttack;
                if (!ofCondition && !(culprit.def.makesSickThought && culprit.def.lethalSeverity > 0f)) return false;
                cause = Record(c, name);
                return true;
            }
            if (victim.health.hediffSet.hediffs.Any(h => h is Hediff_Injury)) return false;
            // No hediff named: the worst of the conditions it could have been (the one a cure was owed for first).
            foreach (Hediff h in victim.health.hediffSet.hediffs)
                if (Row(h.def.defName) is { } row && Record(c, row.defName) is { } rec && (cause == null || rec.treatable && !cause.treatable)) cause = rec;
            ofCondition = cause != null || victim.health.hediffSet.hediffs.Any(h => Row(h.def.defName) != null);
            return ofCondition;
        }

        /// <summary>The decision about the condition that sat on the destroyed part or on one of its parents (null when none did).</summary>
        private static CareDecision DestroyedBy(Pawn victim, CompInmate c, Hediff_MissingPart missing)
        {
            BodyDef body = victim.RaceProps.body;
            CareDecision found = null;
            foreach (CareDecision d in c.care)
            {
                if (d.partIndex < 0 || Row(d.condition) == null) continue;
                BodyPartRecord sat = body.GetPartAtIndex(d.partIndex);
                for (BodyPartRecord part = missing.Part; part != null; part = part.parent)
                    if (part == sat)
                    {
                        if (found == null || d.treatable && !found.treatable) found = d;
                        break;
                    }
            }
            return found;
        }

        /// <summary>A death of the condition after "nothing" when a cure was possible: the prison's neglect (the inquiry).</summary>
        public static bool Neglected(Pawn victim, Hediff culprit, DamageInfo? dinfo) =>
            IsNatural(victim, culprit, dinfo, out CareDecision cause, out _) && cause is { choice: CareChoice.Nothing, treatable: true };

        /// <summary>
        /// A death of age or illness that is not the prison's neglect: after years inside it is a milestone, the friends inside grieve and the family writes (see <see cref="CondolenceSystem"/>). It is a death of age when
        /// they were old, or the cause was one of the conditions that matter; a young inmate dead of a plain illness keeps the ordinary condolence.
        /// </summary>
        public static void Notify_Died(Pawn victim, Hediff culprit, DamageInfo? dinfo)
        {
            if (!IsNatural(victim, culprit, dinfo, out CareDecision cause, out bool ofCondition) || victim.Inmate() is not { } c) return;
            if (!ofCondition && victim.ageTracker.AgeBiologicalYears < OldYears) return;
            if (!c.IsLifer && c.DaysServed < MilestoneDays) return;
            c.diedOfAge = true;
            string what = cause != null ? DefDatabase<HediffDef>.GetNamedSilentFail(cause.condition)?.label ?? cause.condition : culprit?.def.label ?? "RP_Chr_UnknownCause".Translate().ToString();
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 3, (c.IsLifer ? "RP_Chr_DiedOfAgeLifer" : "RP_Chr_DiedOfAge").Translate(victim.LabelShort, what, c.DaysServed.ToString("F0"), victim.ageTracker.AgeBiologicalYears), victim);
            if (victim.relations != null)
                foreach (DirectPawnRelation r in victim.relations.DirectRelations)
                    if (r.def == RPRelationDefOf.RP_Friend && r.otherPawn is { Spawned: true } friend && friend.IsPrisonerOfColony)
                        friend.needs.mood?.thoughts.memories.TryGainMemory(RPRelationDefOf.RP_FriendDied, victim);
        }

        // ---- gone ------------------------------------------------------------------------------------------------

        /// <summary>Whether the decision about this inmate can still be made: they are here, in custody, and not on the way out.</summary>
        public static bool Here(Pawn p) => p is { Dead: false, Spawned: true } && p.IsPrisonerOfColony && p.Inmate() is { activeFlag: true } && !ShuttleSystem.OnTheWayOut(p);

        /// <summary>They left for good (released, transferred, escaped, dead): the letters about them that are still open (on the stack or waiting for the next day) are closed, unanswered. The records stay as they are.</summary>
        public static void Notify_Leaving(StorySystem story, Pawn p)
        {
            if (p.Inmate() is not { care.Count: > 0 }) return;
            bool Open(ChoiceLetter_PrisonDecision l) => l is { answered: false } && l.Is("RP_Decision_OldAge") && (l.subjectId == p.thingIDNumber || l.pawns.Contains(p));
            foreach (ChoiceLetter_PrisonDecision open in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(Open).ToList())
            {
                open.answered = true;
                Find.LetterStack.RemoveLetter(open);
            }
            foreach (ChoiceLetter_PrisonDecision waiting in story.deferredLetters.Where(Open).ToList())
            {
                waiting.answered = true;
                story.deferredLetters.Remove(waiting);
            }
        }
    }

    /// <summary>
    /// The dying inmate's bed: vanilla's own patient logic puts them in a medical bed (<see cref="Patch_HealthAIUtility_ShouldSeekMedicalRest_Hospice"/> makes the dying need one, and the wardens take them there if they are on the
    /// floor), the search for it prefers their own wing's, and the chaplain sits at the bedside.
    /// </summary>
    public static class Hospice
    {
        /// <summary>The record of the palliative care that makes this inmate dying (care was chosen for a dying inmate with no cure to give, and the condition has come far enough), or null when none does.</summary>
        public static CareDecision DyingRecord(Pawn p, CompInmate c)
        {
            // (Asked for every medical-rest question the game has: no allocation.)
            List<CareDecision> care = c.care;
            for (int i = 0; i < care.Count; i++)
            {
                CareDecision d = care[i];
                if (!d.Palliative || OldAge.Row(d.condition) is not { } row) continue;
                List<Hediff> hediffs = p.health.hediffSet.hediffs;
                for (int j = 0; j < hediffs.Count; j++)
                    if (hediffs[j].Severity >= row.dyingAt && hediffs[j].def.defName == d.condition) return d;
            }
            return null;
        }

        public static bool Dying(Pawn p, CompInmate c) => c.care.Count > 0 && DyingRecord(p, c) != null;

        /// <summary>Whether a medical bed of the inmate's own wing is valid for them now, by vanilla's own rules (reachable for the one who carries them, free, not reserved, not forbidden).</summary>
        public static bool WingBedValid(Pawn sleeper, Pawn traveler, CompInmate c)
        {
            Map map = sleeper.MapHeld;
            if (map == null || c.security == null) return false;
            foreach (Building_Bed bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
                if (bed.Medical && bed.ForPrisoners && IntakeSystem.WingOf(bed) == c.security && !PunishmentUtility.IsSolitaryBed(bed)
                    && RestUtility.IsValidBedFor(bed, sleeper, traveler ?? sleeper, checkSocialProperness: false, guestStatus: GuestStatus.Prisoner)) return true;
            return false;
        }

        /// <summary>
        /// A dying inmate is offered the medical beds of their own wing first: another wing's is refused while one of their own is valid for them, and allowed when none is (the wing has none, or it is out of
        /// reach, or taken). Cheap for everybody else: nothing is looked up for an inmate who is not dying. The own wing's beds are never asked about (so the validity question below does not come back here).
        /// </summary>
        public static bool Restricts(Building_Bed bed, Pawn sleeper, CompInmate c, Pawn traveler = null) =>
            c.care.Count > 0 && IntakeSystem.WingOf(bed) != c.security && Dying(sleeper, c) && WingBedValid(sleeper, traveler, c);

        public static void Hourly(Map map, Pawn p, CompInmate c)
        {
            if (c.care.Count == 0 || !p.Spawned || DyingRecord(p, c) is not { } rec) return;
            // In a medical bed, however they got there (vanilla took them): the hospice has them, once.
            if (!rec.admitted && p.CurrentBed() is { Medical: true } bed)
            {
                rec.admitted = true;
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_Hospice".Translate(p.LabelShort, (IntakeSystem.WingOf(bed) ?? c.security).label), p);
            }
            Chaplain(map, p, c, rec);
        }

        /// <summary>The chaplain, if the prison has one, sits with them once a day: a good memory, a little calmer, faith a little restored.</summary>
        private static void Chaplain(Map map, Pawn p, CompInmate c, CareDecision rec)
        {
            int now = Find.TickManager.TicksGame;
            if (rec.visitTick >= 0 && now - rec.visitTick < OldAge.ChaplainEveryTicks) return;
            if (!StaffCommute.AllStaff(map).Any(s => s.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain)) return;
            bool first = rec.visitTick < 0;
            rec.visitTick = now;
            p.needs.mood?.thoughts.memories.TryGainMemory(RPOldAgeDefOf.RP_ChaplainAtBedside);
            c.tension = Mathf.Max(0f, c.tension - 5f);
            if (FaithUtility.Need(p) is { } faith) faith.CurLevel = Mathf.Min(1f, faith.CurLevel + 0.2f);
            if (first) Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_ChaplainAtBedside".Translate(p.LabelShort), p);
        }
    }

    /// <summary>A dying inmate on palliative care needs a medical bed (vanilla's patient logic takes them there, and the mod's regime leaves them in it): the hospice goes through vanilla, with no order of ours.</summary>
    [HarmonyLib.HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.ShouldSeekMedicalRest))]
    public static class Patch_HealthAIUtility_ShouldSeekMedicalRest_Hospice
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            // (Asked all the time, for every pawn: the answer already yes, and the comp, before anything else.)
            if (__result || !pawn.Spawned || pawn.Inmate() is not { activeFlag: true, care.Count: > 0 } c) return;
            if (Hospice.DyingRecord(pawn, c) != null) __result = true;
        }
    }

    public class OldAgeDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_OldAge_Treat", "RP_OldAge_Transfer", "RP_OldAge_Release", "RP_OldAge_Nothing" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        /// <summary>An unanswered letter is "nothing": recorded as such.</summary>
        public override int TimeoutChoice => 3;

        /// <summary>The inmate and the record the letter is about, or nothing when they are gone (released, transferred, escaped, dead: the letter cannot decide about them any more).</summary>
        private static (Pawn p, CompInmate c, CareDecision rec) Subject(ChoiceLetter_PrisonDecision l)
        {
            Pawn p = l.pawns.FirstOrDefault(x => x != null);
            if (p == null || l.note == null || !OldAge.Here(p) || p.Inmate() is not { } c) return default;
            return (p, c, OldAge.Record(c, l.note));
        }

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            (Pawn p, CompInmate c, CareDecision rec) = Subject(l);
            if (rec == null) return "RP_OldAge_Gone".Translate();
            if (option == 3) return null;
            PrisonGame game = PrisonGame.Instance;
            switch (option)
            {
                case 0:
                    int cost = rec.treatable ? OldAge.SurgeryCost : OldAge.CareCost;
                    return game.ledger.funds < cost ? "RP_CannotAfford".Translate(Money.Format(cost)).ToString() : null;
                case 1:
                    return game.ledger.funds < InmateUtility.TransferCost ? "RP_CannotAfford".Translate(Money.Format(InmateUtility.TransferCost)).ToString() : null;
                case 2:
                    return OldAge.ReleaseRefusal(game, p, c, rec.terminal);
                default:
                    return null;
            }
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            (Pawn p, CompInmate c, CareDecision rec) = Subject(l);
            // (A letter that lapses for somebody who has gone leaves the record as it is: unanswered.)
            if (rec == null || rec.choice != CareChoice.Pending) return;
            PrisonGame game = PrisonGame.Instance;
            Hediff h = p.health.hediffSet.hediffs.Find(x => x.def.defName == rec.condition);
            bool done = option switch
            {
                0 => OldAge.Treat(game, p, c, rec, h),
                1 => OldAge.Transfer(game, p, c, rec),
                2 => OldAge.CompassionateRelease(game, p, c, rec, h),
                _ => false,
            };
            // (Nothing was chosen, or what was chosen could not be done: the money was not there, the release was refused. It is recorded as nothing.)
            if (!done) OldAge.DoNothing(p, rec, h);
        }
    }
}
