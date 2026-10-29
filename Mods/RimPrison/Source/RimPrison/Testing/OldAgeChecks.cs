using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for growing old inside (<see cref="OldAge"/>, gap analysis 5, 2.4): the diagnosis letter once per inmate and condition, what each of the four answers does (treat, transfer, compassionate release, nothing),
    /// the neglect rule, the hospice bed in the inmate's own wing, and a lifer's death of age (the milestone, the friend's grief, the family's letter). Each is self-contained, on throwaway pawns, rolled back
    /// (<see cref="Rollback"/>), through the game's own paths (the hourly pass, the letter's own choice, the release, the transfer, the kill). The saveload plan's seed and snapshot are at the end.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckOldAge(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            // (The staff at home too, a night guard between shifts: the hospice runs real ticks, in which they come back through the gate, and one who came in then was "left behind" by the checks, "old age checks
            // left 1 pawn(s) behind: Tzaeru", a guard of the plan's own. As the roster's and the agreements' checks take them.)
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns.Concat(g.OffSite));
            // (And the world's: only a pawn made during the checks is one left behind.)
            pawnsBefore.UnionWith(Find.WorldPawns.AllPawnsAlive);
            PrisonMap pm = PrisonMap.Of(map);
            // (The deaths of the checks write future-dated state outside the rollback's reach: the day of the last death, which the storyteller reads as days since, and the danger map's marks, which stay "seen" for 30 days
            // of the clock they were written at. Both are put back at the end, the marks as DeathDangerChecks does.)
            int lastDeath0 = g.story.lastDeathTick;
            List<IncidentMark> marks0 = pm?.incidents;
            if (pm != null) pm.incidents = new List<IncidentMark>();
            try
            {
                // (The checks live in the first days of a game and the throwaways have served up to 800 days: the product reads days served as the clock minus the admission tick, and an admission tick below 0
                // means "never admitted" (0 days served), so the clock is moved on to day 900 for the checks and put back at the end. No tick runs in between; the throwaways and what they write are rolled back.)
                int clock0 = Find.TickManager.TicksGame;
                Find.TickManager.DebugSetTicksGame(Math.Max(clock0, OaClockDays * OaDay));
                try
                {
                    CheckOldAgeLetter(map);
                    CheckOldAgeTreat(map);
                    CheckOldAgeTransfer(map);
                    CheckOldAgeRelease(map);
                    CheckOldAgeReleaseRefused(map);
                    CheckOldAgeNothing(map);
                    CheckOldAgeNeglect(map);
                    CheckOldAgeGoneIsNotTheirs(map);
                    CheckOldAgeLeftWithLetterOpen(map);
                    CheckOldAgeNewStay(map);
                    CheckOldAgeMilestone(map);
                }
                finally
                {
                    Find.TickManager.DebugSetTicksGame(clock0);
                }
                // (The hospice waits real ticks for a walk to a bed, on the real clock; it needs no days served.)
                CheckOldAgeHospice(map);
            }
            finally
            {
                g.story.lastDeathTick = lastDeath0;
                if (pm != null) pm.incidents = marks0;
            }
            Pawn[] left = LeftBehind(map, pawnsBefore).ToArray();
            if (left.Length > 0) problems0.Add($"old age checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        /// <summary>A seed whose first <c>Rand.Value</c> is below the chance (a roll that says yes), found by trying.</summary>
        private static int OaSeedRolling(float chance, bool below)
        {
            for (int seed = 1; seed < 100000; seed++)
            {
                Rand.PushState(seed);
                float v = Rand.Value;
                Rand.PopState();
                if (v < chance == below) return seed;
            }
            return 1;
        }

        private static void OaFail(string check, string what) => problems0.Add($"old age ({check}): {what}");

        private const int OaDay = GenDate.TicksPerDay;

        /// <summary>The day the clock is moved on to for the old-age checks: later than any stay they give a throwaway (the milestone's lifer has served 800 days).</summary>
        private const int OaClockDays = 900;

        /// <summary>What the last <see cref="OaAnswer"/> did: the funds it took (negative: it paid in), the ledger lines it wrote, and the reason the letter's own button gave for refusing the answer (null: it was open).</summary>
        private static int oaCharged;
        private static string oaLedger, oaRefusal;

        /// <summary>The inmate as the product reads them: age, the sentence fields, the days served by the clock, and the conditions with their severity.</summary>
        private static string OaState(Pawn p)
        {
            CompInmate c = p.Inmate();
            string hediffs = string.Join(", ", p.health.hediffSet.hediffs.Select(h => $"{h.def.defName} {h.Severity:F2}{(h.Part != null ? " on " + h.Part.def.defName : "")}"));
            return $"{p.LabelShort}, age {p.ageTracker.AgeBiologicalYears}, sentence {c.sentenceDays} days{(c.IsLifer ? " (life)" : "")}, admitted tick {c.admittedTick} (clock {Find.TickManager.TicksGame}), {c.DaysServed:F1} days served, "
                   + $"conditions [{hediffs}], care [{string.Join(" ", c.care.Select(d => d.Describe()))}]";
        }

        /// <summary>A throwaway inmate of this age, serving this sentence (-1 life) and this many days into it.</summary>
        private static Pawn OaInmate(Map map, Rollback r, int age, int sentence, int served)
        {
            Pawn p = Throwaway(map, r);
            p.ageTracker.AgeBiologicalTicks = (long)age * 3600000L;
            CompInmate c = p.Inmate();
            c.sentenceDays = sentence;
            c.admittedTick = Find.TickManager.TicksGame - served * OaDay;
            c.innocent = true;
            c.RefreshActiveFlag();
            // (The generator may have given them a chronic condition of its own; the hourly pass writes the letter for the first it finds, which would not be the one a check adds.)
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(x => OldAge.Row(x.def.defName) != null).ToList()) p.health.RemoveHediff(h);
            // (Healthy apart from the condition a check adds: the inmate generator's wounds and addictions (a scratch of 6 and a bite of 5 and a wake-up addiction, stability11-s11d) can leave a throwaway on the floor, and the release is
            // refused for one who cannot be walked to the gate - the product's rule, see OldAge.ReleaseRefusal. The check of that rule makes its own downed inmate.)
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(x => x is Hediff_Injury or Hediff_Addiction).ToList()) p.health.RemoveHediff(h);
            if (p.Downed) OaFail("premise", $"the throwaway is down without a wound or an addiction on them: {OaState(p)}");
            if (c.DaysServed < served - 0.5f) OaFail("premise", $"the throwaway was meant to have served {served} days and the product reads {c.DaysServed:F1} (admitted tick {c.admittedTick}, clock {Find.TickManager.TicksGame})");
            return p;
        }

        private static Hediff OaCondition(Pawn p, string defName, float severity = -1f)
        {
            HediffDef def = DefDatabase<HediffDef>.GetNamed(defName);
            // (A part for the two that sit on one; the age conditions are on the whole body.)
            BodyPartDef partDef = defName == "HeartArteryBlockage" ? BodyPartDefOf.Heart : defName == "Carcinoma" ? DefDatabase<BodyPartDef>.GetNamed("Liver") : null;
            BodyPartRecord part = partDef == null ? null : p.RaceProps.body.GetPartsWithDef(partDef).First();
            Hediff h = HediffMaker.MakeHediff(def, p, part);
            if (severity >= 0f) h.Severity = severity;
            // (Nothing else on the part: a generated pawn's own cirrhosis and a carcinoma at 0.9 on the same liver leave it no efficiency, and vanilla kills the pawn on the spot, inside AddHediff, with the hediff
            // as the culprit: the death the checks then made by hand was a second one, "set dead while already dead".)
            if (part != null)
                foreach (Hediff other in p.health.hediffSet.hediffs.Where(x => x.Part == part).ToList()) p.health.RemoveHediff(other);
            p.health.AddHediff(h);
            return h;
        }

        /// <summary>
        /// The death the check makes by hand: of the condition, once. A pawn that is dead already (the condition killed it as it was added, or a premise of the throwaway is not what it was) is said, not killed again:
        /// vanilla logs an error for that, and the check goes on reading the aftermath of a death that was another's.
        /// </summary>
        private static bool OaKill(string check, Pawn p, Hediff culprit)
        {
            if (p.Dead)
            {
                OaFail(check, $"the check's own premise: {p.LabelShort} was dead before the kill the check meant to make (hediffs [{string.Join(", ", p.health.hediffSet.hediffs.Select(x => x.def.defName + (x.Part != null ? " on " + x.Part.def.defName : "")))}])");
                return false;
            }
            p.Kill(null, culprit);
            return true;
        }

        private static ChoiceLetter_PrisonDecision OaLetter(Rollback r, Pawn p) =>
            r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_OldAge") && l.pawns.Contains(p));

        /// <summary>The hourly pass, for this inmate alone, and the letter it wrote (null when none).</summary>
        private static ChoiceLetter_PrisonDecision OaDiagnose(Map map, Rollback r, Pawn p)
        {
            OldAge.Hourly(PrisonGame.Instance, map, p);
            return OaLetter(r, p);
        }

        private static void OaAnswer(ChoiceLetter_PrisonDecision l, int option)
        {
            PrisonGame g = PrisonGame.Instance;
            var linesBefore = new HashSet<LedgerEntry>(g.ledger.recent);
            int funds0 = g.ledger.funds;
            oaRefusal = l.decision.Worker.Disabled(l, option);
            l.answered = true;
            PrisonDecisions.Choose(l, option);
            Find.LetterStack.RemoveLetter(l);
            oaCharged = funds0 - g.ledger.funds;
            oaLedger = string.Join("; ", g.ledger.recent.Where(e => !linesBefore.Contains(e)).Select(e => $"{e.category?.defName} {e.amount} \"{e.note}\""));
            if (oaLedger.Length == 0) oaLedger = "no ledger lines";
        }

        /// <summary>An inmate with the condition, the letter written and the warden's answer given; the funds are plenty.</summary>
        private static (Pawn p, ChoiceLetter_PrisonDecision letter, CareDecision rec) OaDecided(Map map, Rollback r, string check, string condition, int option, int age = 70, int sentence = 200, int served = 150, int? exactFunds = null)
        {
            PrisonGame g = PrisonGame.Instance;
            g.ledger.funds = exactFunds ?? Math.Max(g.ledger.funds, 50000);
            Pawn p = OaInmate(map, r, age, sentence, served);
            OaCondition(p, condition, condition == "HeartArteryBlockage" ? 0.7f : -1f);
            ChoiceLetter_PrisonDecision letter = OaDiagnose(map, r, p);
            CareDecision rec = OldAge.Record(p.Inmate(), condition);
            if (letter == null || rec == null)
            {
                OaFail(check, $"{condition} on {p.LabelShort} gave no letter for that condition in the hourly pass (a letter {letter != null} for \"{letter?.note}\", a record for {condition} {rec != null}): {OaState(p)}");
                return (p, null, rec);
            }
            r.Add(() =>
            {
                g.shuttle.Forget(p);
                Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            });
            OaAnswer(letter, option);
            return (p, letter, rec);
        }

        // ---- the letter ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeLetter(Map map)
        {
            Guarded("old age: a chronic condition gives the letter, once", map, r =>
            {
                const string check = "letter";
                PrisonGame g = PrisonGame.Instance;
                Pawn p = OaInmate(map, r, 72, 200, 150);
                CompInmate c = p.Inmate();
                // A scar is not a condition that matters, and nothing is wrong with them yet.
                if (OaDiagnose(map, r, p) != null) OaFail(check, "a healthy inmate got a letter");
                p.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Burn"), p.RaceProps.body.GetPartsWithDef(BodyPartDefOf.Hand).First());
                if (OaDiagnose(map, r, p) != null || c.care.Count > 0) OaFail(check, "a burn gave a diagnosis");
                OaCondition(p, "Carcinoma");
                ChoiceLetter_PrisonDecision letter = OaDiagnose(map, r, p);
                CareDecision rec = OldAge.Record(c, "Carcinoma");
                if (letter == null || rec == null || rec.choice != CareChoice.Pending || !rec.treatable || !rec.terminal)
                {
                    OaFail(check, $"a carcinoma gave letter {letter != null}, record {rec?.Describe() ?? "none"} (expected a pending, treatable, terminal one): {OaState(p)}");
                    return;
                }
                string text = letter.Text.ToString();
                // (The letter is several lines, with the prices on the last: compared against the whole resolved text, which the message gives in full, the lines joined with " / ". The days are matched as the one formatted phrase: "50" alone is inside "150".)
                var missing = new List<string>();
                foreach ((string what, string wanted) in new[] { ("the note", "Carcinoma"), ("the name", p.LabelShort), ("the age", "72"), ("the days served and left", "RP_OldAge_SentenceTerm".Translate(200, "150", "50").Resolve()),
                                                                  ("the surgery price", Money.Format(OldAge.SurgeryCost)), ("the transfer price", Money.Format(InmateUtility.TransferCost)) })
                    if (!(what == "the note" ? letter.note : text).Contains(wanted)) missing.Add($"{what} ({wanted})");
                if (missing.Count > 0)
                    OaFail(check, $"the letter lacks {string.Join(", ", missing)}; the inmate: {OaState(p)}; the full text: {text.Replace("\r", "").Replace("\n", " / ")}");
                if (letter.decision.Worker.Options(letter).Count != 4) OaFail(check, "the letter does not offer four answers");
                if (letter.decision.Worker.TimeoutChoice != 3) OaFail(check, "an unanswered letter is not 'nothing'");
                // Once: the next pass (the letter still unanswered, then answered) writes no second letter for the condition.
                OldAge.Hourly(g, map, p);
                if (r.NewLetters.Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_OldAge") && d.pawns.Contains(p)) != 1) OaFail(check, "a condition with its letter still unanswered gave a second letter");
                OaAnswer(letter, 3);
                OldAge.Hourly(g, map, p);
                if (OaLetter(r, p) != null || c.care.Count != 1) OaFail(check, "a condition already decided gave a second letter");
                // A second condition is a second letter.
                OaCondition(p, "Frail");
                ChoiceLetter_PrisonDecision second = OaDiagnose(map, r, p);
                if (second == null || second.note != "Frail" || c.care.Count != 2 || OldAge.Record(c, "Frail").treatable) OaFail(check, "a second condition did not give its own letter (no cure for it)");
                // The condition is the letter's own note, and a letter answered by timing out is 'nothing'.
                if (second != null)
                {
                    second.Lapse();
                    if (OldAge.Record(c, "Frail").choice != CareChoice.Nothing) OaFail(check, "a letter that timed out was not recorded as nothing");
                }
                RPLog.Message("[RimPrison:test] old age letter: one letter per condition, with the age, the sentence and the prices; a timed-out letter is nothing");
            });
        }

        // ---- treat ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeTreat(Map map)
        {
            Guarded("old age: treating them here", map, r =>
            {
                const string check = "treat";
                PrisonGame g = PrisonGame.Instance;
                // A cure exists: the surgery is queued and paid for.
                (Pawn p, ChoiceLetter_PrisonDecision letter, CareDecision rec) = OaDecided(map, r, check, "Carcinoma", 0);
                if (letter == null) return;
                RecipeDef excise = DefDatabase<RecipeDef>.GetNamed("ExciseCarcinoma");
                if (rec.choice != CareChoice.Treat || rec.tick < 0 || !p.BillStack.Bills.Any(b => b.recipe == excise))
                    OaFail(check, $"treating a carcinoma left record {rec.Describe()} and bills [{string.Join(",", p.BillStack.Bills.Select(b => b.recipe.defName))}] (expected Treat and the excision queued)");
                // (The answer's own charge: the funds before and after that answer alone, with the lines it wrote. Not the difference from earlier in the check, which a later OaDecided resets.)
                if (oaCharged != OldAge.SurgeryCost) OaFail(check, $"the surgery cost {oaCharged} (expected {OldAge.SurgeryCost}); ledger lines of the answer: {oaLedger}");
                if (p.playerSettings != null && p.playerSettings.medCare < MedicalCareCategory.NormalOrWorse) OaFail(check, "medicine was not given to the patient");
                if (rec.Palliative) OaFail(check, "a condition with a cure was treated as palliative care");
                string surgery = "RP_Chr_OldAgeSurgery".Translate(p.LabelShort, DefDatabase<HediffDef>.GetNamed("Carcinoma").label).Resolve();
                if (!r.NewEntries.Any(e => e.cat == RPChronicleDefOf.RP_Chr_Life && e.text == surgery)) OaFail(check, $"the surgery is not in the chronicle under life (expected \"{surgery}\")");

                // No cure and dying: palliative care, paid as care.
                (Pawn q, ChoiceLetter_PrisonDecision letterQ, CareDecision recQ) = OaDecided(map, r, check, "HeartArteryBlockage", 0);
                if (letterQ == null) return;
                if (recQ.choice != CareChoice.Treat || !recQ.Palliative || recQ.treatable) OaFail(check, $"care for a blockage with no cure left record {recQ.Describe()} (expected palliative care)");
                if (oaCharged != OldAge.CareCost) OaFail(check, $"palliative care cost {oaCharged} (expected {OldAge.CareCost}); ledger lines of the answer: {oaLedger}");
                if (q.BillStack.Bills.Count > 0) OaFail(check, "a surgery was queued for a condition with no cure");
                string palliative = "RP_Chr_OldAgePalliative".Translate(q.LabelShort, DefDatabase<HediffDef>.GetNamed("HeartArteryBlockage").label).Resolve();
                if (!r.NewEntries.Any(e => e.cat == RPChronicleDefOf.RP_Chr_Life && e.text == palliative)) OaFail(check, $"the palliative care is not in the chronicle under life (expected \"{palliative}\")");

                // Not paid for: the money is not there, and it is recorded as nothing.
                (Pawn broke, ChoiceLetter_PrisonDecision letterB, CareDecision recB) = OaDecided(map, r, check, "Carcinoma", 0, exactFunds: OldAge.SurgeryCost - 1);
                if (letterB != null && (recB.choice != CareChoice.Nothing || broke.BillStack.Bills.Count > 0 || g.ledger.funds != OldAge.SurgeryCost - 1))
                    OaFail(check, $"treating with no money left record {recB.Describe()}, bills {broke.BillStack.Bills.Count}, funds {g.ledger.funds}");
                RPLog.Message("[RimPrison:test] old age treat: the excision queued and paid; palliative care for the dying with no cure; nothing when the money is not there");
            });
        }

        // ---- transfer ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeTransfer(Map map)
        {
            Guarded("old age: transfer to the state's hospital", map, r =>
            {
                const string check = "transfer";
                PrisonGame g = PrisonGame.Instance;
                int out0 = g.stats.transferredOut;
                (Pawn p, ChoiceLetter_PrisonDecision letter, CareDecision rec) = OaDecided(map, r, check, "Carcinoma", 1);
                if (letter == null) return;
                OutboundEntry e = g.shuttle.OutboundOf(p);
                if (rec.choice != CareChoice.Transfer) OaFail(check, $"the record says {rec.Describe()}");
                // (On the way out on the shuttle, or taken off at once when the map has no place for one: either way they have left the prison's custody.)
                if (e == null ? p.Spawned : e.kind != OutboundKind.Transferred || e.cause != "RP_Cause_StateHospital") OaFail(check, $"no transfer with the cause on the way out ({(e == null ? "no entry, still on the map" : e.kind + " " + e.cause)})");
                if (g.stats.transferredOut != out0 + 1 || oaCharged != InmateUtility.TransferCost) OaFail(check, $"the transfer cost {oaCharged} (expected {InmateUtility.TransferCost}) and counted {g.stats.transferredOut - out0}; ledger lines of the answer: {oaLedger}");
                if (!r.NewEntries.Any(en => en.text == "RP_Chr_TransferredCause".Translate(p.LabelShort, "RP_Cause_StateHospital".Translate()).Resolve())) OaFail(check, "the cause is not written in the chronicle");
                RPLog.Message("[RimPrison:test] old age transfer: sent out on the shuttle with the cause on the record, the fee charged");
            });
        }

        // ---- release ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeRelease(Map map)
        {
            Guarded("old age: compassionate release", map, r =>
            {
                const string check = "release";
                PrisonGame g = PrisonGame.Instance;
                var condolences0 = g.story.condolences.ToList();
                r.Add(() =>
                {
                    g.story.condolences.Clear();
                    g.story.condolences.AddRange(condolences0);
                });
                int released0 = g.stats.released;

                // An old man with half of a term served, family outside: out on the shuttle with the cause; the register notes it and he does not come back.
                Pawn old = OaInmate(map, r, 70, 200, 120);
                old.Inmate().innocent = false;
                old.Inmate().relatives.Clear(); // (the throwaway's own relatives first: the letter goes to the first who can visit)
                old.Inmate().relatives.Add(Relatives.Make("RP_Kin_Daughter"));
                old.Inmate().familyCount = 1;
                OaCondition(old, "Frail");
                ChoiceLetter_PrisonDecision letter = OaDiagnose(map, r, old);
                CareDecision rec = OldAge.Record(old.Inmate(), "Frail");
                if (letter == null) { OaFail(check, "no letter for the old inmate"); return; }
                r.Add(() =>
                {
                    g.shuttle.Forget(old);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(old);
                });
                int rearrests0 = g.rearrests.Count;
                string refusal = letter.decision.Worker.Disabled(letter, 2);
                if (refusal != null) OaFail(check, $"the release was refused for an old man with 60% of the term served: {refusal}; the inmate: {OaState(old)}");
                // (The reoffence is a roll of ReoffendChance, 5% to 90%: without the cause guard the check would pass whenever the roll said no. The roll is forced to say yes, and the control below shows it does.)
                int yes = OaSeedRolling(ParoleSystem.ReoffendChance(old.Inmate()), below: true);
                Rand.PushState(yes);
                try
                {
                    OaAnswer(letter, 2);
                }
                finally
                {
                    Rand.PopState();
                }
                OutboundEntry e = g.shuttle.OutboundOf(old);
                if (rec.choice != CareChoice.Release || e == null || e.kind != OutboundKind.Released || e.cause != "RP_Cause_Compassionate")
                    OaFail(check, $"the release left record {rec.Describe()} and the entry {(e == null ? "none" : e.kind + " " + e.cause)} (the letter's button said: {oaRefusal ?? "open"}); the inmate: {OaState(old)}");
                FormerInmate former = g.story.formerInmates.LastOrDefault();
                if (former?.name != old.LabelShort || former.cause != "RP_Cause_Compassionate") OaFail(check, $"the register does not note the cause (last entry {former?.name}, cause {former?.cause ?? "none"}; expected {old.LabelShort}, RP_Cause_Compassionate)");
                if (g.rearrests.Count != rearrests0 || e is { reoffends: true }) OaFail(check, "somebody let go on compassionate grounds was given a reoffence to come, with the roll forced to yes");
                // The control: the same roll, a release with no cause, is a reoffence (so the check above could have failed).
                Pawn control = OaInmate(map, r, 70, 200, 120);
                control.Inmate().innocent = false;
                r.Add(() =>
                {
                    g.shuttle.Forget(control);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(control);
                });
                Rand.PushState(yes);
                ReleaseSystem.LegacyReoffendRoll = true;
                try
                {
                    ReleaseSystem.Release(g, control, record: false);
                }
                finally
                {
                    ReleaseSystem.LegacyReoffendRoll = false;
                    Rand.PopState();
                }
                if (g.shuttle.OutboundOf(control) is not { reoffends: true }) OaFail(check, "premise: with the roll forced to yes an ordinary release did not give a reoffence, so the check on the compassionate one proves nothing");
                // And the compassionate one released directly under the same roll, with nothing before the roll in the way.
                Pawn direct = OaInmate(map, r, 70, 200, 120);
                direct.Inmate().innocent = false;
                r.Add(() =>
                {
                    g.shuttle.Forget(direct);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(direct);
                });
                Rand.PushState(yes);
                ReleaseSystem.LegacyReoffendRoll = true;
                try
                {
                    ReleaseSystem.Release(g, direct, record: false, cause: "RP_Cause_Compassionate");
                }
                finally
                {
                    ReleaseSystem.LegacyReoffendRoll = false;
                    Rand.PopState();
                }
                if (g.shuttle.OutboundOf(direct) is not { reoffends: false, cause: "RP_Cause_Compassionate" }) OaFail(check, "a release on compassionate grounds, with the roll forced to yes, was given a reoffence (or lost its cause)");
                if (!r.NewEntries.Any(en => en.text.Contains(old.LabelShort) && en.cat == RPChronicleDefOf.RP_Chr_Release && en.importance == 2 && en.text == "RP_Chr_ReleasedCompassionate".Translate(old.LabelShort, "120", DefDatabase<HediffDef>.GetNamed("Frail").label).Resolve()))
                    OaFail(check, $"no chronicle line for the compassionate release (expected \"{"RP_Chr_ReleasedCompassionate".Translate(old.LabelShort, "120", DefDatabase<HediffDef>.GetNamed("Frail").label).Resolve()}\" at importance 2; the new lines: {string.Join(" | ", r.NewEntries.Select(en => en.importance + " " + en.text))})");
                Condolence family = g.story.condolences.FirstOrDefault(x => x.name == old.LabelShort);
                if (family?.kind != RPOldAgeDefOf.RP_Mail_CompassionateRelease || family.from == null || !family.from.Contains("daughter") && !family.from.Contains("Daughter")) OaFail(check, $"the family's letter is {family?.Describe() ?? "not queued"}");
                else
                {
                    float reputation0 = g.reputation;
                    int lettersBefore = r.NewLetters.Count();
                    CondolenceSystem.Deliver(g, family);
                    if (r.NewLetters.Count() != lettersBefore + 1 || g.reputation <= reputation0) OaFail(check, "the family's letter did not come as a letter that lifts the prison's name");
                }

                // A lifer past a long stretch: released as a big story beat (a milestone).
                Pawn lifer = OaInmate(map, r, 68, -1, OldAge.LiferStretchDays + 10);
                OaCondition(lifer, "Carcinoma");
                ChoiceLetter_PrisonDecision lifeLetter = OaDiagnose(map, r, lifer);
                if (lifeLetter == null) { OaFail(check, "no letter for the lifer"); return; }
                r.Add(() =>
                {
                    g.shuttle.Forget(lifer);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(lifer);
                });
                OaAnswer(lifeLetter, 2);
                CareDecision lifeRec = OldAge.Record(lifer.Inmate(), "Carcinoma");
                if (lifeRec.choice != CareChoice.Release || g.shuttle.OutboundOf(lifer) is not { kind: OutboundKind.Released })
                    OaFail(check, $"a lifer past the long stretch was not released: record {lifeRec.Describe()}, the letter's button said: {oaRefusal ?? "open"}; the inmate: {OaState(lifer)}");
                if (!r.NewEntries.Any(en => en.importance == 3 && en.cat == RPChronicleDefOf.RP_Chr_Release && en.text.Contains(lifer.LabelShort)))
                    OaFail(check, $"a lifer's release is not a milestone (no line of importance 3 in the release category naming {lifer.LabelShort}; the new lines: {string.Join(" | ", r.NewEntries.Select(en => en.cat.defName + " " + en.importance + " " + en.text))})");
                // The inmate who is only dying, young but terminal: allowed (terminally ill), with the share of a term served.
                Pawn dying = OaInmate(map, r, 40, 100, 60);
                OaCondition(dying, "Carcinoma");
                ChoiceLetter_PrisonDecision dyingLetter = OaDiagnose(map, r, dying);
                string dyingRefusal = dyingLetter?.decision.Worker.Disabled(dyingLetter, 2);
                if (dyingLetter == null) OaFail(check, $"no letter for the young inmate with a terminal condition: {OaState(dying)}");
                else if (dyingRefusal != null) OaFail(check, $"a young inmate with a terminal condition and 60% of the term served was refused: {dyingRefusal}; the letter is for \"{dyingLetter.note}\" (record {OldAge.Record(dying.Inmate(), dyingLetter.note)?.Describe() ?? "none"}); the inmate: {OaState(dying)}");
                RPLog.Message($"[RimPrison:test] old age release: out with the cause, the register noted it, the family's letter queued; a lifer's release is a milestone (released {g.stats.released - released0} so far)");
            });
        }

        private static void CheckOldAgeReleaseRefused(Map map)
        {
            Guarded("old age: a compassionate release is refused when it is not due", map, r =>
            {
                const string check = "release refused";
                PrisonGame g = PrisonGame.Instance;
                g.ledger.funds = Math.Max(g.ledger.funds, 50000);
                // Young, not dying, early in a short sentence.
                Pawn young = OaInmate(map, r, 30, 100, 10);
                OaCondition(young, "Asthma");
                ChoiceLetter_PrisonDecision letter = OaDiagnose(map, r, young);
                if (letter == null) { OaFail(check, "no letter for the young inmate"); return; }
                string why = letter.decision.Worker.Disabled(letter, 2);
                if (why == null) OaFail(check, "the release was offered for a young inmate early in a short sentence");
                int released0 = g.stats.released;
                OaAnswer(letter, 2);
                CareDecision rec = OldAge.Record(young.Inmate(), "Asthma");
                if (g.shuttle.OutboundOf(young) != null || rec.choice != CareChoice.Nothing || g.stats.released != released0)
                    OaFail(check, $"a refused release still sent them out or was not recorded as nothing ({rec.Describe()})");
                // Old, but only a sixth of the term served; a lifer under the long stretch; and one on the floor, who cannot be walked to the gate.
                var cases = new List<(string what, Pawn p)>
                {
                    ("old and early in the term", OaInmate(map, r, 70, 600, 100)),
                    ("a lifer not yet past the stretch", OaInmate(map, r, 70, -1, OldAge.LiferStretchDays - 5)),
                };
                foreach ((string what, Pawn p) in cases)
                {
                    OaCondition(p, "Frail");
                    ChoiceLetter_PrisonDecision l = OaDiagnose(map, r, p);
                    if (l == null) { OaFail(check, $"no letter for {what}"); continue; }
                    if (l.decision.Worker.Disabled(l, 2) == null) OaFail(check, $"the release was offered to an inmate who is {what}");
                }
                // A condemned inmate in the appeals (no date yet) who has served enough and is old: never offered release.
                SecurityLevelDef deathRow = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(d => d.GetModExtension<DeathRowExtension>() != null);
                if (deathRow == null) RPLog.Message($"[RimPrison:test] old age ({check}): no death row level in the defs, the condemned are not checked");
                else
                {
                    Pawn condemned = OaInmate(map, r, 70, -1, OldAge.LiferStretchDays + 10);
                    condemned.Inmate().security = deathRow;
                    OaCondition(condemned, "Frail");
                    ChoiceLetter_PrisonDecision cl = OaDiagnose(map, r, condemned);
                    if (cl == null) OaFail(check, "no letter for the condemned inmate");
                    else if (condemned.Inmate().executionTick >= 0 || condemned.Inmate().executing) OaFail(check, "premise: the condemned throwaway has an execution date");
                    else if (cl.decision.Worker.Disabled(cl, 2) == null) OaFail(check, "the release was offered to an old condemned inmate in the appeals (no execution date yet)");
                }
                Pawn downed = OaInmate(map, r, 70, 100, 90);
                OaCondition(downed, "Frail");
                ChoiceLetter_PrisonDecision dl = OaDiagnose(map, r, downed);
                downed.health.forceDowned = true;
                downed.health.CheckForStateChange(null, null);
                if (dl != null && downed.Downed && dl.decision.Worker.Disabled(dl, 2) == null) OaFail(check, "the release was offered to somebody on the floor, who cannot be walked to the gate");
                if (dl == null) OaFail(check, "no letter for the one on the floor");
                RPLog.Message("[RimPrison:test] old age release refused: young and early, early in the term and a lifer under the stretch are refused; a refused answer is nothing");
            });
        }

        // ---- nothing, and the neglect rule ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeNothing(Map map)
        {
            Guarded("old age: doing nothing is recorded", map, r =>
            {
                const string check = "nothing";
                PrisonGame g = PrisonGame.Instance;
                int funds0 = g.ledger.funds = Math.Max(g.ledger.funds, 50000);
                (Pawn p, ChoiceLetter_PrisonDecision letter, CareDecision rec) = OaDecided(map, r, check, "Carcinoma", 3);
                if (letter == null) return;
                if (rec.choice != CareChoice.Nothing || rec.tick < 0 || g.ledger.funds != funds0 || p.BillStack.Bills.Count > 0 || g.shuttle.OutboundOf(p) != null)
                    OaFail(check, $"nothing left record {rec.Describe()}, funds {funds0}->{g.ledger.funds}, bills {p.BillStack.Bills.Count}");
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_OldAgeNothing".Translate(p.LabelShort, DefDatabase<HediffDef>.GetNamed("Carcinoma").label).Resolve())) OaFail(check, "nothing is not in the chronicle");
            });
        }

        /// <summary>The inmate dies of the condition (the hediff names it); whether the state opened an inquiry.</summary>
        private static bool OaDiesOpensInquiry(Map map, Rollback r, Pawn p, string condition)
        {
            PrisonGame g = PrisonGame.Instance;
            BodyPartDef partDef = condition == "HeartArteryBlockage" ? BodyPartDefOf.Heart : DefDatabase<BodyPartDef>.GetNamed("Liver");
            Hediff h = HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed(condition), p, p.RaceProps.body.GetPartsWithDef(partDef).First());
            int inquiries = g.stats.inquiries;
            r.Add(() => CondolenceSystem.ClearFault(p));
            DeathAftermath.Notify_Died(p, null, h, null);
            return g.stats.inquiries == inquiries + 1;
        }

        /// <summary>
        /// The way vanilla's carcinoma kills (it has no lethal severity; its last stage destroys the part): the hediff goes, a missing part is added on its part, and the death comes from that with the missing part as the
        /// culprit and no damage info (<c>Pawn.Kill(null, missingPart)</c>, from the health tracker's own state check; called here if the pawn is somehow still alive).
        /// </summary>
        private static void OaKillByMissingPart(Rollback r, Pawn p, string condition)
        {
            Hediff h = p.health.hediffSet.hediffs.First(x => x.def.defName == condition);
            BodyPartRecord part = h.Part;
            p.health.RemoveHediff(h);
            r.Add(() =>
            {
                if (p.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                CondolenceSystem.ClearFault(p);
            });
            Hediff missing = HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, p, part);
            p.health.AddHediff(missing, part);
            if (!p.Dead) p.Kill(null, missing);
        }

        private static void CheckOldAgeNeglect(Map map)
        {
            Guarded("old age: a death of the condition after nothing, when it could be cured, is neglect", map, r =>
            {
                const string check = "neglect";
                PrisonGame g = PrisonGame.Instance;
                // Nothing, and a cure existed: neglect.
                (Pawn a, ChoiceLetter_PrisonDecision la, _) = OaDecided(map, r, check, "Carcinoma", 3, age: 70, sentence: 100, served: 10);
                if (la != null && !OaDiesOpensInquiry(map, r, a, "Carcinoma")) OaFail(check, "a death of a carcinoma after 'nothing', when it could be cut out, was not neglect");
                // Treated: not.
                (Pawn b, ChoiceLetter_PrisonDecision lb, _) = OaDecided(map, r, check, "Carcinoma", 0, age: 70, sentence: 100, served: 10);
                if (lb != null && OaDiesOpensInquiry(map, r, b, "Carcinoma")) OaFail(check, "a death of a carcinoma that was treated was called neglect");
                // Nothing, and nothing could be done: not.
                (Pawn c, ChoiceLetter_PrisonDecision lc, CareDecision rc) = OaDecided(map, r, check, "HeartArteryBlockage", 3, age: 70, sentence: 100, served: 10);
                if (lc != null && (rc.treatable || OaDiesOpensInquiry(map, r, c, "HeartArteryBlockage"))) OaFail(check, "a death after 'nothing' when nothing could have been done was called neglect");
                // Palliative care: not.
                (Pawn d, ChoiceLetter_PrisonDecision ld, CareDecision rd) = OaDecided(map, r, check, "HeartArteryBlockage", 0, age: 70, sentence: 100, served: 10);
                if (ld != null && (!rd.Palliative || OaDiesOpensInquiry(map, r, d, "HeartArteryBlockage"))) OaFail(check, "a death under palliative care was called neglect");
                // Never asked about (no letter), and a death of another illness while 'nothing' was chosen about a different condition: not.
                Pawn e = OaInmate(map, r, 70, 100, 10);
                if (OaDiesOpensInquiry(map, r, e, "HeartArteryBlockage")) OaFail(check, "a death of a condition nobody had written about opened an inquiry");
                // The same by the way vanilla kills with a carcinoma (the missing part of its last stage is the culprit, and the hediff is gone): the warden who said nothing is asked for it, the one who treated is not.
                PrisonGame gg = PrisonGame.Instance;
                (Pawn m1, ChoiceLetter_PrisonDecision lm1, _) = OaDecided(map, r, check, "Carcinoma", 3, age: 70, sentence: 100, served: 10);
                if (lm1 != null)
                {
                    int inquiries = gg.stats.inquiries;
                    OaKillByMissingPart(r, m1, "Carcinoma");
                    if (!m1.Dead || gg.stats.inquiries != inquiries + 1) OaFail(check, $"a carcinoma that destroyed its part after 'nothing' (dead {m1.Dead}) opened {gg.stats.inquiries - inquiries} inquiries, expected 1");
                }
                (Pawn m2, ChoiceLetter_PrisonDecision lm2, _) = OaDecided(map, r, check, "Carcinoma", 0, age: 70, sentence: 100, served: 10);
                if (lm2 != null)
                {
                    int inquiries = gg.stats.inquiries;
                    OaKillByMissingPart(r, m2, "Carcinoma");
                    if (!m2.Dead || gg.stats.inquiries != inquiries) OaFail(check, $"a carcinoma that destroyed its part after treatment (dead {m2.Dead}) opened an inquiry");
                }
                // A part lost to a wound is no carcinoma's: a missing hand of a pawn who said nothing about a carcinoma of the liver is not neglect.
                (Pawn m3, ChoiceLetter_PrisonDecision lm3, _) = OaDecided(map, r, check, "Carcinoma", 3, age: 70, sentence: 100, served: 10);
                if (lm3 != null && OldAge.Neglected(m3, HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, m3, m3.RaceProps.body.GetPartsWithDef(BodyPartDefOf.Hand).First()), null))
                    OaFail(check, "a missing hand was called the death of a carcinoma of the liver");
                RPLog.Message("[RimPrison:test] old age neglect: nothing with a cure is neglect (by the carcinoma's own death too); treated, palliative, nothing with no cure and no letter are not");
            });
        }

        /// <summary>A death after the release or the transfer is not the prison's: they are on the way out, no custody death, no inquiry.</summary>
        private static void CheckOldAgeGoneIsNotTheirs(Map map)
        {
            Guarded("old age: a death after release or transfer is not the prison's", map, r =>
            {
                const string check = "after they left";
                PrisonGame g = PrisonGame.Instance;
                foreach (int option in new[] { 1, 2 })
                {
                    string how = option == 1 ? "transfer" : "release";
                    (Pawn p, ChoiceLetter_PrisonDecision letter, CareDecision rec) = OaDecided(map, r, check, "Carcinoma", option, age: 70, sentence: 100, served: 80);
                    if (letter == null) continue;
                    if (rec.choice == CareChoice.Nothing) { OaFail(check, $"the {how} was not done ({rec.Describe()}; the letter's button said: {oaRefusal ?? "open"}); the inmate: {OaState(p)}"); continue; }
                    if (!ShuttleSystem.OnTheWayOut(p)) { RPLog.Message($"[RimPrison:test] old age ({check}): the {how} left no one on the map to die on the way out, skipped"); continue; }
                    int inquiries = g.stats.inquiries, deaths = g.stats.deaths;
                    Hediff h = p.health.hediffSet.hediffs.First(x => x.def.defName == "Carcinoma");
                    r.Add(() =>
                    {
                        if (p.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                    });
                    p.Kill(null, h);
                    if (g.stats.inquiries != inquiries || g.stats.deaths != deaths) OaFail(check, $"a death after the {how} was counted against the prison (inquiries {g.stats.inquiries - inquiries}, deaths {g.stats.deaths - deaths})");
                    // (The rule itself says so too: only 'nothing' with a cure is neglect.)
                    if (OldAge.Neglected(p, h, null)) OaFail(check, $"the neglect rule reads a death after the {how} as neglect");
                }
                RPLog.Message("[RimPrison:test] old age: a death after the release or the transfer is not counted against the prison");
            });
        }

        // ---- the letter outlives the inmate ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeLeftWithLetterOpen(Map map)
        {
            Guarded("old age: a letter open when the inmate leaves is closed and decides nothing", map, r =>
            {
                const string check = "letter after they left";
                PrisonGame g = PrisonGame.Instance;
                g.ledger.funds = Math.Max(g.ledger.funds, 50000);
                Pawn p = OaInmate(map, r, 70, 200, 150);
                OaCondition(p, "Carcinoma");
                ChoiceLetter_PrisonDecision letter = OaDiagnose(map, r, p);
                CareDecision rec = OldAge.Record(p.Inmate(), "Carcinoma");
                if (letter == null || rec == null) { OaFail(check, $"no letter for the carcinoma: {OaState(p)}"); return; }
                r.Add(() =>
                {
                    g.shuttle.Forget(p);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                });
                if (letter.decision.Worker.Disabled(letter, 1) != null) OaFail(check, "premise: the transfer is not offered with the letter open and the funds there");
                // The warden sends them off by the ordinary transfer while the letter waits.
                int out0 = g.stats.transferredOut;
                InmateUtility.TransferOut(p, p.Inmate());
                if (g.stats.transferredOut != out0 + 1 || !(ShuttleSystem.OnTheWayOut(p) || !p.Spawned)) { OaFail(check, "premise: the plain transfer did not send them out"); return; }
                if (!letter.answered || Find.LetterStack.LettersListForReading.Contains(letter)) OaFail(check, $"the letter is still open after the inmate left (answered {letter.answered}, on the stack {Find.LetterStack.LettersListForReading.Contains(letter)})");
                // (Had it been left on the desk: every answer is refused, and answering does nothing: no second transfer, no second charge, no release, no surgery.)
                for (int option = 0; option < 4; option++)
                    if (letter.decision.Worker.Disabled(letter, option) == null) OaFail(check, $"answer {option} is open for an inmate who has left");
                int funds0 = g.ledger.funds, released0 = g.stats.released, former0 = g.story.formerInmates.Count;
                foreach (int option in new[] { 0, 1, 2, 3 }) PrisonDecisions.Choose(letter, option);
                if (g.stats.transferredOut != out0 + 1 || g.ledger.funds != funds0 || g.stats.released != released0 || g.story.formerInmates.Count != former0 || p.BillStack.Bills.Count > 0)
                    OaFail(check, $"answering for an inmate who left did something (transfers {g.stats.transferredOut - out0}, funds {g.ledger.funds - funds0}, released {g.stats.released - released0}, register {g.story.formerInmates.Count - former0}, bills {p.BillStack.Bills.Count})");
                // A letter that lapses for somebody who is gone leaves the record unanswered.
                letter.answered = false;
                letter.Lapse();
                if (rec.choice != CareChoice.Pending) OaFail(check, $"a lapsed letter for an inmate who left recorded {rec.Describe()}");
                RPLog.Message("[RimPrison:test] old age: a letter still open when the inmate left is closed, every answer refused, no second transfer or charge, a lapse leaves the record unanswered");
            });
        }

        private static void CheckOldAgeNewStay(Map map)
        {
            Guarded("old age: a new stay asks again about what was left", map, r =>
            {
                const string check = "new stay";
                PrisonGame g = PrisonGame.Instance;
                Pawn p = OaInmate(map, r, 70, 200, 150);
                CompInmate c = p.Inmate();
                foreach ((string condition, CareChoice choice) in new[] { ("Carcinoma", CareChoice.Nothing), ("Frail", CareChoice.Pending), ("Asthma", CareChoice.Treat), ("BadBack", CareChoice.Transfer), ("Cataract", CareChoice.Release) })
                    c.care.Add(new CareDecision { condition = condition, choice = choice, foundTick = 1, tick = 2 });
                c.ResetForNewStay();
                string kept = string.Join(",", c.care.Select(d => d.condition + ":" + d.choice));
                if (kept != "Asthma:Treat,BadBack:Transfer,Cataract:Release") OaFail(check, $"a new stay kept [{kept}] (expected only the answered Treat, Transfer and Release)");
                // Dropped, the condition gets its letter again.
                g.ledger.funds = Math.Max(g.ledger.funds, 50000);
                OaCondition(p, "Carcinoma");
                if (OaDiagnose(map, r, p) is not { } letter || letter.note != "Carcinoma") OaFail(check, "a carcinoma the warden left alone in the last stay got no letter in the new one");
                RPLog.Message("[RimPrison:test] old age new stay: 'nothing' and unanswered are asked again, the answered ones stay");
            });
        }

        // ---- the hospice ------------------------------------------------------------------------------------------------

        /// <summary>A hospital bed for prisoners at a free cell, in the wing zone of this level (put back by the rollback).</summary>
        private static Building_Bed OaHospiceBed(Map map, Rollback r, PrisonMap pm, Pawn near, SecurityLevelDef level, Func<IntVec3, bool> where)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed("HospitalBed");
            PrisonZoneDef zone = DefDatabase<PrisonZoneDef>.AllDefsListForReading.First(z => z.security == level);
            bool Free(IntVec3 c) => GenAdj.OccupiedRect(c, Rot4.North, def.Size).All(x => x.InBounds(map) && x.Standable(map) && x.GetEdifice(map) == null && x.GetFirstItem(map) == null
                                                                                         && x.GetThingList(map).All(t => t.def.category != ThingCategory.Building && t.def.category != ThingCategory.Plant)) && where(c);
            if (!CellFinder.TryFindRandomCellNear(near.Position, map, 25, Free, out IntVec3 cell)) return null;
            var bed = (Building_Bed)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
            bed.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(bed, cell, map, Rot4.North);
            bed.ForPrisoners = true;
            foreach (IntVec3 c in bed.OccupiedRect())
            {
                PrisonZoneDef was = pm.zones.ZoneAt(c);
                r.Add(() => pm.zones.Set(c, was));
                pm.zones.Set(c, zone);
            }
            r.Add(() =>
            {
                if (!bed.Destroyed) bed.Destroy(DestroyMode.Vanish);
                pm.Notify_BedsChanged();
            });
            pm.Notify_BedsChanged();
            return bed;
        }

        private static void CheckOldAgeHospice(Map map)
        {
            Guarded("old age: the dying inmate goes to a medical bed through vanilla, their own wing's first", map, r =>
            {
                const string check = "hospice";
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                // (On the real clock, with real ticks: they have served no days, which the hospice does not read.)
                (Pawn p, ChoiceLetter_PrisonDecision letter, CareDecision rec) = OaDecided(map, r, check, "HeartArteryBlockage", 0, served: 0);
                if (letter == null) return;
                CompInmate c = p.Inmate();
                SecurityLevelDef own = c.security, other = DefDatabase<SecurityLevelDef>.AllDefsListForReading.First(l => l != own && l.wingOnly == false && DefDatabase<PrisonZoneDef>.AllDefsListForReading.Any(z => z.security == l));
                if (!Hospice.Dying(p, c)) OaFail(check, "an inmate on palliative care with the condition far gone is not dying for the hospice");
                // Vanilla's own question: the dying need a medical bed, the others (and the dying before they chose care) do not.
                if (!HealthAIUtility.ShouldSeekMedicalRest(p)) OaFail(check, "vanilla does not send the dying inmate to a medical bed (ShouldSeekMedicalRest)");
                Pawn well = OaInmate(map, r, 30, 100, 0);
                if (HealthAIUtility.ShouldSeekMedicalRest(well)) OaFail(check, "an inmate who is not dying is sent to a medical bed");
                (Pawn untreated, _, _) = OaDecided(map, r, check, "HeartArteryBlockage", 3, served: 0);
                if (untreated != null && HealthAIUtility.ShouldSeekMedicalRest(untreated)) OaFail(check, "an inmate with the same condition, left alone and not on palliative care, is sent to a medical bed");
                // The wing of another level has the nearer bed; their own wing's is farther off.
                Building_Bed wrong = OaHospiceBed(map, r, pm, p, other, _ => true);
                float wrongDist = wrong == null ? 0f : (wrong.Position - p.Position).LengthHorizontal;
                Building_Bed right = OaHospiceBed(map, r, pm, p, own, cell => (cell - p.Position).LengthHorizontal >= wrongDist + 3f);
                if (wrong == null || right == null)
                {
                    RPLog.Message($"[RimPrison:test] old age ({check}): no room for two beds near the intake (other wing {wrong != null}, own wing {right != null}), skipped");
                    return;
                }
                if (IntakeSystem.WingOf(right) != own || IntakeSystem.WingOf(wrong) != other || !right.Medical || !wrong.Medical)
                    OaFail(check, $"premise: the beds are not medical beds of the two wings (own {IntakeSystem.WingOf(right)?.defName}, other {IntakeSystem.WingOf(wrong)?.defName})");
                bool Valid(Building_Bed bed) => RestUtility.IsValidBedFor(bed, p, p, checkSocialProperness: false, guestStatus: GuestStatus.Prisoner);
                // An inmate who is not dying is still offered either (the rule is the hospice's alone).
                if (!BedWings.Allows(wrong, well, well.Inmate())) OaFail(check, "an inmate who is not dying was kept from a medical bed of another wing");
                if (!p.CanReach(right, Verse.AI.PathEndMode.OnCell, Danger.Some))
                {
                    // (Vanilla then offers them what it can reach, whatever the wing: the preference is not a wall.)
                    if (Hospice.WingBedValid(p, null, c)) OaFail(check, "a medical bed of their own wing that they cannot reach is called valid for them");
                    RPLog.Message($"[RimPrison:test] old age ({check}): not a failure, the map gives no walk: the hospice bed at {right.Position} cannot be reached from the inmate at {p.Position} (the intake), so the walk to it is not checked");
                    return;
                }
                if (!Valid(right)) { RPLog.Message($"[RimPrison:test] old age ({check}): not a failure, the bed of their own wing is not valid for the inmate by vanilla's rules (reserved or forbidden), skipped"); return; }
                if (Valid(wrong) || !BedWings.Allows(right, p, c)) OaFail(check, "the search for a bed for them is not kept to their own wing's medical bed while it is free");
                Building_Bed found = RestUtility.FindBedFor(p, p, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner) as Building_Bed;
                if (found != right) OaFail(check, $"vanilla's search gave {(found == null ? "no bed" : "the bed of the " + IntakeSystem.WingOf(found)?.defName + " wing")}, not the medical bed of their own wing (the other wing's was nearer)");

                // The hourly pass gives no order and does not touch their job (the bed is vanilla's to take them to), and it does not write the hospice until they lie in it.
                bool chaplain = StaffCommute.AllStaff(map).Any(s => s.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain);
                if (!chaplain) ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Chaplain);
                chaplain = StaffCommute.AllStaff(map).Any(s => s.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain);
                float tension0 = c.tension = 30f;
                Verse.AI.Job job0 = p.CurJob;
                OldAge.Hourly(g, map, p);
                if (!chaplain) RPLog.Message($"[RimPrison:test] old age ({check}): the throwaway chaplain is not counted as staff, the visit is not checked");
                if (chaplain && (p.needs.mood?.thoughts.memories.GetFirstMemoryOfDef(RPOldAgeDefOf.RP_ChaplainAtBedside) == null || rec.visitTick < 0 || c.tension >= tension0))
                    OaFail(check, $"the chaplain did not sit with them (memory {p.needs.mood?.thoughts.memories.GetFirstMemoryOfDef(RPOldAgeDefOf.RP_ChaplainAtBedside) != null}, visit {rec.visitTick}, tension {tension0}->{c.tension})");
                if (p.CurJob != job0) OaFail(check, $"the hourly pass changed the dying inmate's job ({job0?.def.defName} -> {p.CurJob?.def.defName})");
                string line = "RP_Chr_Hospice".Translate(p.LabelShort, own.label).Resolve();
                if (rec.admitted || r.NewEntries.Any(e => e.text == line)) OaFail(check, "the hospice was written before they were in a bed");
                // Vanilla's patient logic takes them: the job they have is ended (as a new activity of the regime ends it), the think tree gives them a bed, and the ticks run until they lie in it.
                p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                int start = Find.TickManager.TicksGame;
                while (Find.TickManager.TicksGame - start < OaHospiceTicks && !(p.CurrentBed() is { Medical: true })) Find.TickManager.DoSingleTick();
                Building_Bed lying = p.CurrentBed();
                if (lying != right)
                    OaFail(check, $"after {Find.TickManager.TicksGame - start} ticks the dying inmate is {(lying == null ? "not in a bed (job " + p.CurJob?.def.defName + ")" : "in a bed of the " + IntakeSystem.WingOf(lying)?.defName + " wing")}, not in the medical bed of their own wing at {right.Position} (they are at {p.Position})");
                else
                {
                    // In the bed: the hospice is written, once, and the regime leaves them there.
                    OldAge.Hourly(g, map, p);
                    if (!rec.admitted || r.NewEntries.Count(e => e.text == line) != 1) OaFail(check, $"the hospice is not in the chronicle once they lie in the bed (admitted {rec.admitted}, lines {r.NewEntries.Count(e => e.text == line)}; expected \"{line}\")");
                    OldAge.Hourly(g, map, p);
                    if (r.NewEntries.Count(e => e.text == line) != 1) OaFail(check, "the hospice was written twice");
                    if (InmateUtility.WouldInterruptForRegimeChange(p, false)) OaFail(check, "the regime would pull the dying inmate out of the hospice bed");
                }
                int visit = rec.visitTick;
                c.tension = tension0;
                OldAge.Hourly(g, map, p);
                if (chaplain && (rec.visitTick != visit || c.tension != tension0)) OaFail(check, "the chaplain came twice in a day");
                rec.visitTick -= OaDay;
                OldAge.Hourly(g, map, p);
                if (chaplain && rec.visitTick == visit - OaDay) OaFail(check, "the chaplain did not come the next day");

                // Their own wing's bed is taken from them (forbidden here: vanilla finds it invalid) and they are not shut out of the others: the preference is only a preference.
                // (Not by forbidding it: a prisoner is not of the player's faction, and vanilla's forbidden test does not apply to them. A bed for colonists is not valid for a prisoner.)
                right.ForOwnerType = BedOwnerType.Colonist; // (not ForPrisoners = false: vanilla logs an error and asks which owner type)
                r.Add(() => { if (!right.Destroyed) right.ForOwnerType = BedOwnerType.Prisoner; });
                if (Valid(right) || Hospice.WingBedValid(p, null, c) || !BedWings.Allows(wrong, p, c))
                    OaFail(check, $"with the medical bed of their own wing invalid for them the dying inmate was shut out of the other wings' beds (own bed valid {Valid(right)}, own wing valid {Hospice.WingBedValid(p, null, c)}, the other wing's bed allowed {BedWings.Allows(wrong, p, c)})");
                right.ForOwnerType = BedOwnerType.Prisoner;
                // And with no medical bed in their wing at all.
                right.Destroy(DestroyMode.Vanish);
                pm.Notify_BedsChanged();
                if (Hospice.WingBedValid(p, null, c) || !BedWings.Allows(wrong, p, c)) OaFail(check, "with no medical bed in their own wing the dying inmate was shut out of the other wings' beds");
                RPLog.Message("[RimPrison:test] old age hospice: vanilla's patient logic takes the dying inmate to the medical bed of their own wing (not the nearer one of another), the hospice written once they lie in it, the chaplain once a day, the other wings' beds when theirs is not valid");
            });
        }

        /// <summary>How long the hospice check lets the dying inmate walk to the bed.</summary>
        private const int OaHospiceTicks = 3000;

        // ---- a death of age ------------------------------------------------------------------------------------------------

        private static void CheckOldAgeMilestone(Map map)
        {
            Guarded("old age: a lifer's death of age is a milestone and the family writes", map, r =>
            {
                const string check = "milestone";
                PrisonGame g = PrisonGame.Instance;
                var condolences0 = g.story.condolences.ToList();
                r.Add(() =>
                {
                    g.story.condolences.Clear();
                    g.story.condolences.AddRange(condolences0);
                });
                g.ledger.funds = Math.Max(g.ledger.funds, 50000);
                Pawn lifer = OaInmate(map, r, 71, -1, 800);
                CompInmate lc = lifer.Inmate();
                lc.relatives.Clear(); // (the throwaway's own relatives first: the letter goes to the first who can visit)
                lc.relatives.Add(Relatives.Make("RP_Kin_Daughter"));
                lc.familyCount = 1;
                Pawn friend = Throwaway(map, r);
                lifer.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, friend);
                Hediff h = OaCondition(lifer, "Carcinoma", 0.9f);
                ChoiceLetter_PrisonDecision letter = OaDiagnose(map, r, lifer);
                if (letter != null) OaAnswer(letter, 0); // treated: the death is of age and illness, not neglect
                r.Add(() =>
                {
                    if (lifer.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                });
                int inquiries = g.stats.inquiries;
                if (!OaKill(check, lifer, h)) return;
                if (!lifer.Dead) { OaFail(check, "the lifer did not die"); return; }
                string what = DefDatabase<HediffDef>.GetNamed("Carcinoma").label;
                string line = "RP_Chr_DiedOfAgeLifer".Translate(lifer.LabelShort, what, lc.DaysServed.ToString("F0"), lifer.ageTracker.AgeBiologicalYears).Resolve();
                if (!r.NewEntries.Any(e => e.text == line && e.importance == 3 && e.cat == RPChronicleDefOf.RP_Chr_Death)) OaFail(check, $"no milestone for a lifer's death of age (expected \"{line}\")");
                if (!lc.diedOfAge) OaFail(check, "the death of age was not marked for the family's letter");
                if (g.stats.inquiries != inquiries) OaFail(check, "a death of age after treatment opened an inquiry");
                if (friend.needs.mood?.thoughts.memories.GetFirstMemoryOfDef(RPRelationDefOf.RP_FriendDied) == null) OaFail(check, "the friend inside did not grieve");
                Corpse body = lifer.Corpse;
                if (body == null) { OaFail(check, "no body to send home"); return; }
                CondolenceSystem.Send(g, body);
                Condolence family = g.story.condolences.FirstOrDefault(x => x.name == lifer.LabelShort);
                if (family?.kind != RPOldAgeDefOf.RP_Mail_CondolenceOld || family.from == null || family.staff) OaFail(check, $"the family's letter is {family?.Describe() ?? "not queued"}");
                else
                {
                    int lettersBefore = r.NewLetters.Count();
                    CondolenceSystem.Deliver(g, family);
                    if (r.NewLetters.Count() != lettersBefore + 1 || r.NewLetters.Last().Label.ToString() != "RP_Mail_CondolenceOld_Label".Translate(lifer.LabelShort).ToString())
                        OaFail(check, "the family's letter did not come with the dead one's name");
                }

                // A short stay and a death of illness is no milestone (and no letter of that kind).
                Pawn brief = OaInmate(map, r, 71, 400, 20);
                brief.Inmate().relatives.Clear(); // (the throwaway's own relatives first: the letter goes to the first who can visit)
                brief.Inmate().relatives.Add(Relatives.Make("RP_Kin_Son"));
                brief.Inmate().familyCount = 1;
                Hediff bh = OaCondition(brief, "Carcinoma", 0.9f);
                int entries = r.NewEntries.Count(e => e.importance == 3 && e.cat == RPChronicleDefOf.RP_Chr_Death);
                r.Add(() =>
                {
                    if (brief.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                });
                OaKill(check, brief, bh);
                if (brief.Inmate().diedOfAge || r.NewEntries.Count(e => e.importance == 3 && e.cat == RPChronicleDefOf.RP_Chr_Death) != entries) OaFail(check, "a death after 20 days of a 400-day sentence was a milestone");
                // The vanilla carcinoma's death (the destroyed part is the culprit): a lifer's milestone as well.
                Pawn viaPart = OaInmate(map, r, 71, -1, 800);
                OaCondition(viaPart, "Carcinoma", 0.9f);
                ChoiceLetter_PrisonDecision partLetter = OaDiagnose(map, r, viaPart);
                if (partLetter != null) OaAnswer(partLetter, 0);
                OaKillByMissingPart(r, viaPart, "Carcinoma");
                string partLine = "RP_Chr_DiedOfAgeLifer".Translate(viaPart.LabelShort, what, viaPart.Inmate().DaysServed.ToString("F0"), viaPart.ageTracker.AgeBiologicalYears).Resolve();
                if (!viaPart.Inmate().diedOfAge || !r.NewEntries.Any(e => e.text == partLine && e.importance == 3 && e.cat == RPChronicleDefOf.RP_Chr_Death))
                    OaFail(check, $"a lifer's death of a carcinoma that destroyed its part was not a milestone (dead {viaPart.Dead}, marked {viaPart.Inmate().diedOfAge}, expected \"{partLine}\")");
                // A young lifer dead of a plain disease is not a death of age, however long they were inside: no old-age line, no old-age letter; an old one is.
                HediffDef plague = DefDatabase<HediffDef>.GetNamed("Plague");
                Pawn young = OaInmate(map, r, 24, -1, 800);
                Hediff youngPlague = HediffMaker.MakeHediff(plague, young);
                r.Add(() =>
                {
                    if (young.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                });
                OaKill(check, young, youngPlague);
                if (young.Inmate().diedOfAge || r.NewEntries.Any(e => e.importance == 3 && e.cat == RPChronicleDefOf.RP_Chr_Death && e.text.Contains(young.LabelShort)))
                    OaFail(check, $"a 24-year-old lifer dead of the plague was written up as a death of age (marked {young.Inmate().diedOfAge}; the lines: {string.Join(" | ", r.NewEntries.Where(e => e.text.Contains(young.LabelShort)).Select(e => e.importance + " " + e.text))})");
                Pawn elder = OaInmate(map, r, 80, -1, 800);
                Hediff elderPlague = HediffMaker.MakeHediff(plague, elder);
                r.Add(() =>
                {
                    if (elder.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                });
                OaKill(check, elder, elderPlague);
                if (!elder.Dead || !elder.Inmate().diedOfAge) OaFail(check, "an 80-year-old lifer dead of the plague was not a death of age");
                // A death by a wound is never a death of age, however long they were inside.
                Pawn hurt = OaInmate(map, r, 71, -1, 800);
                var dinfo = new DamageInfo(DamageDefOf.Cut, 500f, 0f, -1f, null, hurt.RaceProps.body.corePart);
                DeathAftermath.Notify_Died(hurt, null, null, dinfo);
                if (hurt.Inmate().diedOfAge || OldAge.IsNatural(hurt, null, dinfo, out _, out _)) OaFail(check, "a death by a wound was called a death of age");
                RPLog.Message("[RimPrison:test] old age milestone: a lifer's death of age is a milestone with the friend's grief and the family's letter; a short stay and a wound are not");
            });
        }

        // ---- the saveload plan -----------------------------------------------------------------------------------

        /// <summary>The care decisions kept on the inmates, the mark of a death of age, and the register's notes, as one comparable string.</summary>
        private static string OldAgeDescribe(PrisonGame g, Map map)
        {
            string inmates = string.Join(",", map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { } c && (c.care.Count > 0 || c.diedOfAge)).OrderBy(x => x.thingIDNumber)
                .Select(x => $"{x.thingIDNumber}:{x.Inmate().diedOfAge}:{string.Join("+", x.Inmate().care.Select(d => d.Describe()))}"));
            string register = string.Join(",", g.story.formerInmates.Where(e => e.cause != null).Select(e => $"{e.name}:{e.cause}"));
            return $"{inmates}/{register}";
        }

        /// <summary>One inmate with a decision of every kind (every field off its default) and the mark of a death of age; a register entry with a cause.</summary>
        private static void SeedOldAgeForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).OrderBy(x => x.thingIDNumber).FirstOrDefault();
            if (p == null)
            {
                problems0.Add("saveload: no inmate to seed a care decision on");
                return;
            }
            CompInmate c = p.Inmate();
            int now = Find.TickManager.TicksGame;
            c.care.RemoveAll(d => d.condition == "Dementia" || d.condition == "Cataract");
            c.care.Add(new CareDecision { condition = "Dementia", choice = CareChoice.Nothing, foundTick = now - 4000, tick = now - 3000, treatable = true, terminal = true, partIndex = 3, visitTick = now - 2000, admitted = true });
            c.care.Add(new CareDecision { condition = "Cataract", choice = CareChoice.Pending, foundTick = now - 1000 });
            c.diedOfAge = true;
            g.story.formerInmates.Add(new FormerInmate { name = "Zoldage", number = 99996, releasedTick = now - 5 * OaDay, cause = "RP_Cause_Compassionate" });
        }
    }
}
