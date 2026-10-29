using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Standing orders and the desk (<see cref="StandingOrders"/>, <see cref="DeskBoard.Desk(PrisonGame, Map)"/>): which decisions allow an order, the letter's box, an order only from an answer by hand (and only an
    /// answer the decision takes for good), a later letter answered at once with no budget used (archived, a message with its review link, the chronicle), an order given while a letter waited, a disabled
    /// answer, a missing one and an urgent letter falling back with their line (once), lifting, a decision that allows none ignoring the book, and the desk's rows and budget. Self-contained (letters made by
    /// the check, the game's orders, held letters and day's count put back), run among the events plan's other checks.
    /// </summary>
    public static partial class AutoTest
    {
        private static void StandingFail(string check, string what) => problems0.Add($"standing orders ({check}): {what}");

        private static void CheckStandingOrders(Map map)
        {
            // (No check may leave an order in the book: it would answer the live game's letters, and the later checks'.)
            string book0 = StandingOrders.Describe(PrisonGame.Instance);
            CheckStandingDefs(map);
            CheckStandingByHand(map);
            CheckStandingAnswers(map);
            CheckStandingFallBack(map);
            CheckStandingMidnight(map);
            CheckStandingWorkers(map);
            CheckStandingDesk(map);
            string book1 = StandingOrders.Describe(PrisonGame.Instance);
            if (book1 != book0) StandingFail("rollback", $"the checks left the book at \"{book1}\" (it was \"{book0}\" before them)");
        }

        /// <summary>The decisions this work marks routine (the defs' <c>standingOrderAllowed</c>).</summary>
        private static readonly string[] StandingRoutine =
        {
            "RP_Decision_Funeral", "RP_Decision_Adjudication", "RP_Decision_CustodyContract", "RP_Decision_Mentor", "RP_Decision_TalentShow", "RP_Decision_FamilyDay",
        };

        /// <summary>Decisions whose answers depend on the case: never an order, whatever else changes. (The export contract: the same two answers, but each offer's terms are its own.)</summary>
        private static readonly string[] StandingNever =
        {
            "RP_Decision_TrialCase", "RP_Decision_DebtCrisis", "RP_Decision_StaffDebt", "RP_Decision_LeftGang", "RP_Decision_Kin", "RP_Decision_Arc", "RP_Decision_Parole", "RP_Decision_Complaint",
            "RP_Decision_Innocence", "RP_Decision_LastRequest", "RP_Decision_Inquiry", "RP_Decision_OldAge", "RP_Decision_SupermaxStepDown", "RP_Decision_ExportContract",
        };

        /// <summary>A decision's worker swapped for the check's own (its cached worker put back after it).</summary>
        private static void StandingSwapWorker(PrisonDecisionDef def, PrisonDecisionWorker worker, Rollback r)
        {
            System.Reflection.FieldInfo field = HarmonyLib.AccessTools.Field(typeof(PrisonDecisionDef), "workerInt");
            object was = field.GetValue(def);
            worker.def = def;
            field.SetValue(def, worker);
            r.Add(() => field.SetValue(def, was));
        }

        /// <summary>The mentorship's two answers, and what answering does left to the check.</summary>
        private sealed class StandingTestWorker : PrisonDecisionWorker
        {
            private static readonly string[] Keys = { "RP_Mentor_Allow", "RP_Mentor_Apart" };
            public System.Action<ChoiceLetter_PrisonDecision> onChoose;

            public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

            public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option) => onChoose?.Invoke(l);
        }

        /// <summary>What a standing-order check makes and changes: its letters (taken off the stack and out of the archive after it), the game's orders, the held letters, the day's count and the budget switch.</summary>
        private sealed class StandingKit
        {
            private readonly Map map;
            private int n;
            public readonly List<ChoiceLetter_PrisonDecision> made = new List<ChoiceLetter_PrisonDecision>();

            public StandingKit(Map map) => this.map = map;

            /// <summary>A letter of the decision with an answer time, its label its own (a message with the same words as a live one is not shown twice).</summary>
            public ChoiceLetter_PrisonDecision Make(string decision, int hours = 24)
            {
                ChoiceLetter_PrisonDecision l = ChoiceLetter_PrisonDecision.Make(decision, $"standing order test {++n}", "standing order test", map);
                if (hours > 0) l.StartTimeout(GenDate.TicksPerHour * hours);
                made.Add(l);
                return l;
            }

            public ChoiceLetter_PrisonDecision Send(string decision, int hours = 24)
            {
                ChoiceLetter_PrisonDecision l = Make(decision, hours);
                Find.LetterStack.ReceiveLetter(l);
                return l;
            }
        }

        private static StandingKit StandingSetup(Map map, Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            var orders0 = new Dictionary<string, string>(g.standingOrders);
            List<ChoiceLetter_PrisonDecision> answered0 = g.standingAnswered.ToList(), deferred0 = g.story.deferredLetters;
            int decisions0 = g.story.decisionsToday;
            bool budget0 = StoryLetters.TestBudget;
            var kit = new StandingKit(map);
            r.Add(() =>
            {
                g.standingOrders.Clear();
                foreach (KeyValuePair<string, string> kv in orders0) g.standingOrders[kv.Key] = kv.Value;
                g.standingAnswered.Clear();
                g.standingAnswered.AddRange(answered0);
                g.story.deferredLetters = deferred0;
                g.story.decisionsToday = decisions0;
                StoryLetters.TestBudget = budget0;
                foreach (ChoiceLetter_PrisonDecision l in kit.made)
                {
                    if (Find.LetterStack.LettersListForReading.Contains(l)) Find.LetterStack.RemoveLetter(l);
                    Find.Archive.Remove(l);
                }
            });
            g.standingOrders.Clear();
            g.story.deferredLetters = new List<ChoiceLetter_PrisonDecision>();
            g.story.decisionsToday = 0;
            return kit;
        }

        private static bool OnStack(Letter l) => Find.LetterStack.LettersListForReading.Contains(l);

        private static int Occurrences(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, System.StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, System.StringComparison.Ordinal)) count++;
            return count;
        }

        // ---- which decisions -----------------------------------------------------------------------------------------------------------------------------

        private static void CheckStandingDefs(Map map)
        {
            const string check = "defs";
            Guarded("standing orders: only the routine decisions allow one", map, r =>
            {
                foreach (string name in StandingRoutine)
                    if (DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(name) is not { } def || !StandingOrders.Allowed(def)) StandingFail(check, $"{name} does not allow a standing order");
                foreach (string name in StandingNever)
                    if (DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(name) is { } def && StandingOrders.Allowed(def)) StandingFail(check, $"{name} (its answers depend on the case) allows a standing order");
                var allowed = new List<string>();
                foreach (PrisonDecisionDef def in DefDatabase<PrisonDecisionDef>.AllDefsListForReading)
                {
                    if (def.urgent && def.standingOrderAllowed) StandingFail(check, $"{def.defName} is urgent and marked to allow standing orders");
                    if (def.urgent && StandingOrders.Allowed(def)) StandingFail(check, $"the urgent {def.defName} allows a standing order");
                    if (!StandingOrders.Allowed(def)) continue;
                    allowed.Add(def.defName);
                    // (Each answer the def names for good is one of its letters' answers.)
                    var blank = ChoiceLetter_PrisonDecision.Make(def.defName, "standing order test", "standing order test", map);
                    IReadOnlyList<string> keys = StandingOrders.AnswerKeys(blank);
                    foreach (string key in def.standingOrderOptions)
                        if (!keys.Contains(key)) StandingFail(check, $"{def.defName} allows the answer {key} for good, which its letter does not offer ({string.Join(",", keys)})");
                }
                PrisonDecisionDef sheet = DefDatabase<PrisonDecisionDef>.GetNamed("RP_Decision_Adjudication");
                if (StandingOrders.OptionAllowed(sheet, "RP_Adjudication_Open") || !StandingOrders.OptionAllowed(sheet, "RP_Adjudication_Policy"))
                    StandingFail(check, "the adjudication sheet's standing order is not the policy's sanction alone");
                // A marked decision made urgent allows none, whatever its XML says (no def is both, so the code's own guard is tried on one flipped for the check).
                PrisonDecisionDef mentor = DefDatabase<PrisonDecisionDef>.GetNamed("RP_Decision_Mentor");
                bool urgent0 = mentor.urgent;
                r.Add(() => mentor.urgent = urgent0);
                mentor.urgent = true;
                if (StandingOrders.Allowed(mentor)) StandingFail(check, "a marked decision made urgent still allows a standing order");
                mentor.urgent = urgent0;
                // The book keeps an order only for an answer the decision still offers and takes for good (the load's filter).
                if (!StandingOrders.Keeps(mentor, "RP_Mentor_Apart") || StandingOrders.Keeps(mentor, "RP_Mentor_NoSuchAnswer") || StandingOrders.Keeps(sheet, "RP_Adjudication_Open")
                    || StandingOrders.Keeps(DefDatabase<PrisonDecisionDef>.GetNamed("RP_Decision_Holiday"), "RP_Holiday_Ordinary") || StandingOrders.Keeps(null, "RP_Mentor_Apart"))
                    StandingFail(check, "the book's filter keeps a missing answer, one not taken for good, one for a decision with no orders or for none, or drops a good one");
                RPLog.Message($"[RimPrison:test] standing orders allowed for {allowed.Count}: {string.Join(", ", allowed)}");
            });
        }

        // ---- the box and an order by hand ----------------------------------------------------------------------------------------------------------------

        private static void CheckStandingByHand(Map map)
        {
            const string check = "by hand";
            Guarded("standing orders: the letter's box, an order only from an answer by hand", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                // A routine letter offers the box beside its answers; one that allows no order does not.
                ChoiceLetter_PrisonDecision first = kit.Send("RP_Decision_Mentor");
                if (!OnStack(first))
                {
                    StandingFail(check, "a mentorship letter with no order did not reach the desk (the check's premise)");
                    return;
                }
                List<DiaOption> choices = first.Choices.ToList();
                if (choices.OfType<DiaOption_StandingOrder>().Count() != 1) StandingFail(check, $"the mentorship letter shows {choices.OfType<DiaOption_StandingOrder>().Count()} \"do this from now on\" boxes (one)");
                ChoiceLetter_PrisonDecision holiday = kit.Send("RP_Decision_Holiday");
                if (holiday.Choices.OfType<DiaOption_StandingOrder>().Any()) StandingFail(check, "the holiday letter (no standing orders) shows the box");
                // Answered without the box: no order.
                choices[1].action();
                if (g.standingOrders.Count != 0) StandingFail(check, $"an answer without the box gave an order: {StandingOrders.Describe(g)}");
                // With the box: the answer's key.
                ChoiceLetter_PrisonDecision second = kit.Send("RP_Decision_Mentor");
                second.standingTicked = true;
                second.Choices.ToList()[1].action();
                if (StandingOrders.OrderFor(second.decision) != "RP_Mentor_Apart") StandingFail(check, $"an answer with the box ticked left the orders at \"{StandingOrders.Describe(g)}\" (RP_Decision_Mentor:RP_Mentor_Apart)");
                // A letter left to lapse (or time out) is no answer by hand.
                ChoiceLetter_PrisonDecision lapsed = kit.Send("RP_Decision_TalentShow");
                lapsed.standingTicked = true;
                lapsed.Lapse();
                if (g.standingOrders.ContainsKey("RP_Decision_TalentShow")) StandingFail(check, "a lapsed letter with the box ticked gave an order");
                // An answer the decision does not take for good (the sheet's "open it"): no order; its policy answer is one.
                ChoiceLetter_PrisonDecision sheet = kit.Send("RP_Decision_Adjudication");
                sheet.amount = 987_654;
                sheet.standingTicked = true;
                sheet.Choices.ToList()[0].action();
                if (g.standingOrders.ContainsKey("RP_Decision_Adjudication")) StandingFail(check, "opening the sheet with the box ticked gave an order");
                ChoiceLetter_PrisonDecision sheet2 = kit.Send("RP_Decision_Adjudication");
                sheet2.amount = 987_654;
                sheet2.standingTicked = true;
                sheet2.Choices.ToList()[1].action();
                if (StandingOrders.OrderFor(sheet2.decision) != "RP_Adjudication_Policy") StandingFail(check, $"the sheet's policy answer with the box ticked left the orders at \"{StandingOrders.Describe(g)}\"");
            });
        }

        // ---- answered at once ----------------------------------------------------------------------------------------------------------------------------

        private static void CheckStandingAnswers(Map map)
        {
            const string check = "answered";
            Guarded("standing orders: a later letter is answered at once and uses no budget", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                ChoiceLetter_PrisonDecision l = kit.Send("RP_Decision_Mentor");
                if (OnStack(l) || g.story.deferredLetters.Contains(l) || !l.answered) StandingFail(check, $"the letter was not answered at once (on the desk {OnStack(l)}, held {g.story.deferredLetters.Contains(l)}, answered {l.answered})");
                if (g.story.decisionsToday != 0) StandingFail(check, $"the answered letter used the day's budget ({g.story.decisionsToday})");
                if (!Find.Archive.Contains(l)) StandingFail(check, "the answered letter is not in the archive");
                if (!g.standingAnswered.Contains(l)) StandingFail(check, "the answered letter is not among the desk's lately answered");
                if (!StandingOrders.HasReview(l)) StandingFail(check, "no live message offers to review the answered letter");
                string line = "RP_StandingOrderAnsweredLine".Translate("RP_Mentor_Apart".Translate()).RawText;
                if (!l.Text.RawText.Contains(line)) StandingFail(check, $"the answered letter's text does not say how it was answered: {l.Text.RawText}");
                // (Read-only on review: it is not on the stack, so only "close".)
                List<DiaOption> review = l.Choices.ToList();
                if (!l.ArchivedOnly || review.Count != 1 || review.OfType<DiaOption_StandingOrder>().Any()) StandingFail(check, $"the review is not read-only ({review.Count} choices)");
                string chronicle = "RP_Chr_DecisionStanding".Translate("RP_Mentor_Apart".Translate()).Resolve();
                if (!r.NewEntries.Any(e => e.text.Contains(chronicle))) StandingFail(check, $"the chronicle does not say the decision was a standing order's: {string.Join(" | ", r.NewEntries.Select(e => e.text))}");
                // A full day: still answered, never held.
                g.story.decisionsToday = budget;
                ChoiceLetter_PrisonDecision full = kit.Send("RP_Decision_Mentor");
                if (!full.answered || OnStack(full) || g.story.deferredLetters.Contains(full) || g.story.decisionsToday != budget) StandingFail(check, $"on a full day the letter was not answered at once (held {g.story.deferredLetters.Contains(full)}, {g.story.decisionsToday} of {budget})");
                g.story.decisionsToday = 0;
                // The order is the answer's key: its place among the letter's answers is looked up each time.
                ChoiceLetter_PrisonDecision blank = kit.Make("RP_Decision_Mentor");
                if (StandingOrders.WhyNot(blank, "RP_Mentor_Allow", out int allow) != null || allow != 0 || StandingOrders.WhyNot(blank, "RP_Mentor_Apart", out int apart) != null || apart != 1)
                    StandingFail(check, "the answer's place is not found by its key");
                // A key the letter does not offer falls back to the desk, with its line.
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_NoSuchAnswer";
                ChoiceLetter_PrisonDecision missing = kit.Send("RP_Decision_Mentor");
                if (!OnStack(missing) || missing.answered || !missing.Text.Resolve().Contains("RP_StandingOrderWhyMissing".Translate().Resolve())) StandingFail(check, $"an order for an answer the letter lacks did not fall back with its line (on the desk {OnStack(missing)}): {missing.Text.Resolve()}");
                // Lifted: the next letter comes to the desk, with no line.
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                if (!StandingOrders.Lift(g, "RP_Decision_Mentor") || g.standingOrders.ContainsKey("RP_Decision_Mentor")) StandingFail(check, "lifting the order did not remove it");
                ChoiceLetter_PrisonDecision after = kit.Send("RP_Decision_Mentor");
                if (!OnStack(after) || after.answered || after.standingTried) StandingFail(check, "after the order was lifted the letter did not come to the desk as usual");
                // A decision that allows no order ignores one in the book (an old save's, a hand-edited one).
                g.standingOrders["RP_Decision_Holiday"] = "RP_Holiday_Ordinary";
                ChoiceLetter_PrisonDecision holiday = kit.Send("RP_Decision_Holiday");
                if (holiday.answered || holiday.standingTried) StandingFail(check, "an order for the holiday letter (no standing orders) answered it");
            });
        }

        // ---- falling back --------------------------------------------------------------------------------------------------------------------------------

        private static void CheckStandingFallBack(Map map)
        {
            const string check = "fall back";
            Guarded("standing orders: a disabled answer or an urgent letter falls back to the desk", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                // Putting on the talent show with no money: the answer is disabled for this letter.
                g.standingOrders["RP_Decision_TalentShow"] = "RP_TalentShow_Host";
                g.ledger.funds = 0;
                string cannot = "RP_CannotAfford".Translate(Money.Format(IncidentWorker_TalentShow.Cost)).ToString();
                // (The line as it is added to the letter: raw, its money coloured once when the letter is shown. The run that read the shown text looked for a line resolved once in a text resolved
                // twice: "$150" in two colour tags, never found.)
                string line = StandingOrders.CouldNotLine("RP_TalentShow_Host", cannot);
                ChoiceLetter_PrisonDecision poor = kit.Send("RP_Decision_TalentShow");
                if (!OnStack(poor) || poor.answered) StandingFail(check, $"a letter whose standing answer is disabled did not reach the desk (on it {OnStack(poor)}, answered {poor.answered})");
                if (Occurrences(poor.Text.RawText, line) != 1) StandingFail(check, $"the letter does not say once why the order could not answer it ({line}): {poor.Text.RawText}");
                if (poor.Text.Resolve().Contains("<color=#DBB40CFF><color=")) StandingFail(check, $"the letter shows its money in two colour tags (the line was resolved before the letter): {poor.Text.Resolve()}");
                // The same past the day's budget: held, then delivered at midnight with the one line (not weighed again).
                g.story.decisionsToday = budget;
                ChoiceLetter_PrisonDecision held = kit.Send("RP_Decision_TalentShow");
                if (!g.story.deferredLetters.Contains(held)) StandingFail(check, "the fallen-back letter on a full day was not held (the check's premise)");
                StoryLetters.NewDay(g);
                StoryLetters.Daily(g, map);
                if (!OnStack(held) || held.answered) StandingFail(check, "the held letter was not delivered at midnight");
                if (Occurrences(held.Text.RawText, line) != 1) StandingFail(check, $"the delivered letter says {Occurrences(held.Text.RawText, line)} times why the order could not answer it (once): {held.Text.RawText}");
                // An urgent letter (six hours to answer): never answered by an order.
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                ChoiceLetter_PrisonDecision urgent = kit.Send("RP_Decision_Mentor", 6);
                if (!OnStack(urgent) || urgent.answered || !urgent.Text.Resolve().Contains("RP_StandingOrderWhyUrgent".Translate().Resolve())) StandingFail(check, $"an urgent letter was answered by the order or came without its line (on the desk {OnStack(urgent)}): {urgent.Text.Resolve()}");
            });
        }

        // ---- an order given while a letter waited --------------------------------------------------------------------------------------------------------

        private static void CheckStandingMidnight(Map map)
        {
            const string check = "midnight";
            Guarded("standing orders: one given while a letter waited answers it at midnight", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                g.story.decisionsToday = budget;
                ChoiceLetter_PrisonDecision waiting = kit.Send("RP_Decision_Mentor"), other = kit.Send("RP_Decision_TalentShow");
                if (!g.story.deferredLetters.Contains(waiting) || !g.story.deferredLetters.Contains(other))
                {
                    StandingFail(check, "letters on a full day were not held (the check's premise)");
                    return;
                }
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Allow";
                StoryLetters.NewDay(g);
                // (The new day's own systems have filled it before the backlog: the order answers its letter all the same, and the other waits on.)
                g.story.decisionsToday = budget;
                StoryLetters.Daily(g, map);
                if (!waiting.answered || OnStack(waiting) || g.story.deferredLetters.Contains(waiting)) StandingFail(check, $"the waiting letter was not answered by the order at a full midnight (answered {waiting.answered}, on the desk {OnStack(waiting)}, still held {g.story.deferredLetters.Contains(waiting)})");
                if (g.story.decisionsToday != budget) StandingFail(check, $"the letter the order answered at midnight used the new day's budget ({g.story.decisionsToday} of {budget})");
                if (!g.story.deferredLetters.Contains(other) || other.answered || OnStack(other)) StandingFail(check, "a letter with no order was delivered or answered on a full midnight");
            });
            Guarded("standing orders: one given on a held letter's last day answers it before it lapses", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                g.story.decisionsToday = budget;
                ChoiceLetter_PrisonDecision last = kit.Send("RP_Decision_Mentor"), plain = kit.Send("RP_Decision_TalentShow");
                if (!g.story.deferredLetters.Contains(last) || !g.story.deferredLetters.Contains(plain))
                {
                    StandingFail(check, "letters on a full day were not held (the premise of the last-day check)");
                    return;
                }
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                StoryLetters.NewDay(g);
                g.story.decisionsToday = budget;
                // (The pass as it runs at the midnight after their days are up: asked at that tick, the clock left where it is.)
                int late = last.heldTick + StoryLetters.MaxHeldDays * GenDate.TicksPerDay + GenDate.TicksPerHour;
                if (!StoryLetters.Stale(last, late)) StandingFail(check, "the held letter is not past its days at the tick the check asks about (its premise)");
                StoryLetters.Daily(g, map, late);
                if (!last.answered || !g.standingAnswered.Contains(last) || !last.Text.RawText.Contains("RP_StandingOrderAnsweredLine".Translate("RP_Mentor_Apart".Translate()).RawText))
                    StandingFail(check, $"the letter with an order lapsed by its default instead of being answered by the order (answered {last.answered}, by the order {g.standingAnswered.Contains(last)})");
                if (!plain.answered || g.story.deferredLetters.Contains(plain) || OnStack(plain) || g.standingAnswered.Contains(plain))
                    StandingFail(check, $"the letter with no order did not lapse (answered {plain.answered}, still held {g.story.deferredLetters.Contains(plain)}, on the desk {OnStack(plain)})");
            });
        }

        // ---- the worker's answer: one that throws, one that writes a letter -------------------------------------------------------------------------------

        private static void CheckStandingWorkers(Map map)
        {
            const string check = "workers";
            Guarded("standing orders: a worker that throws does not break the sender; a letter it writes at midnight asks for its place", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                var worker = new StandingTestWorker();
                StandingSwapWorker(DefDatabase<PrisonDecisionDef>.GetNamed("RP_Decision_Mentor"), worker, r);
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                // Throws inside the stack's prefix: caught there (one error logged, taken back here: it is the check's own), the letter archived as answered, the sender goes on.
                worker.onChoose = _ => throw new System.InvalidOperationException("standing order test: a worker that throws");
                int errors0 = ErrorCount, firsts0 = FirstErrors.Count;
                ChoiceLetter_PrisonDecision thrown = null;
                bool senderWentOn = false;
                try
                {
                    thrown = kit.Send("RP_Decision_Mentor");
                    senderWentOn = true;
                }
                catch (System.Exception e)
                {
                    StandingFail(check, $"a worker's exception came out of ReceiveLetter into the sender: {e.Message}");
                }
                bool logged = ErrorCount == errors0 + 1;
                ErrorCount = errors0;
                if (FirstErrors.Count > firsts0) FirstErrors.RemoveRange(firsts0, FirstErrors.Count - firsts0);
                if (senderWentOn && (thrown == null || !thrown.answered || OnStack(thrown) || !Find.Archive.Contains(thrown) || !g.standingAnswered.Contains(thrown) || !logged))
                    StandingFail(check, $"after the worker threw: answered {thrown?.answered}, on the desk {thrown != null && OnStack(thrown)}, archived {thrown != null && Find.Archive.Contains(thrown)}, error logged {logged}");
                // At midnight a letter the worker writes asks for a place in the new day's budget (full here: it waits), as one written at any other hour does.
                ChoiceLetter_PrisonDecision followUp = null;
                worker.onChoose = _ => followUp ??= kit.Send("RP_Decision_Holiday");
                g.standingOrders.Remove("RP_Decision_Mentor");
                g.story.decisionsToday = budget;
                ChoiceLetter_PrisonDecision waiting = kit.Send("RP_Decision_Mentor");
                if (!g.story.deferredLetters.Contains(waiting))
                {
                    StandingFail(check, "a letter on a full day was not held (the premise of the follow-up check)");
                    return;
                }
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                StoryLetters.NewDay(g);
                g.story.decisionsToday = budget;
                StoryLetters.Daily(g, map);
                if (!waiting.answered || followUp == null) StandingFail(check, $"the order did not answer the held letter at midnight, or its worker wrote nothing (answered {waiting.answered})");
                else if (OnStack(followUp) || !g.story.deferredLetters.Contains(followUp))
                    StandingFail(check, $"the letter the worker wrote at a full midnight went past the budget (on the desk {OnStack(followUp)}, held {g.story.deferredLetters.Contains(followUp)})");
            });
            Guarded("standing orders: a letter sent with a delay is weighed once, when it is delivered", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                var queue = (List<Letter>)HarmonyLib.AccessTools.Field(typeof(LetterStack), "letterQueue").GetValue(Find.LetterStack);
                ChoiceLetter_PrisonDecision delayed = kit.Make("RP_Decision_Mentor"), plain = kit.Make("RP_Decision_TalentShow");
                r.Add(() => queue.Remove(delayed));
                r.Add(() => queue.Remove(plain));
                Find.LetterStack.ReceiveLetter(delayed, null, GenDate.TicksPerHour);
                Find.LetterStack.ReceiveLetter(plain, null, GenDate.TicksPerHour);
                if (!queue.Contains(delayed) || !queue.Contains(plain) || delayed.answered || delayed.standingTried || g.story.decisionsToday != 0 || g.story.deferredLetters.Count != 0)
                    StandingFail(check, $"letters sent with a delay were weighed when queued (queued {queue.Contains(delayed)}/{queue.Contains(plain)}, answered {delayed.answered}, {g.story.decisionsToday} of the day used, {g.story.deferredLetters.Count} held)");
                // (Delivered as the queue does it: handed to the stack again, with no delay.)
                queue.Remove(delayed);
                queue.Remove(plain);
                Find.LetterStack.ReceiveLetter(delayed);
                Find.LetterStack.ReceiveLetter(plain);
                if (!delayed.answered || OnStack(delayed) || !OnStack(plain) || g.story.decisionsToday != 1)
                    StandingFail(check, $"on delivery: the ordered letter answered {delayed.answered} (on the desk {OnStack(delayed)}), the other on the desk {OnStack(plain)}, {g.story.decisionsToday} of the day used (1: the other's alone)");
            });
        }

        // ---- the desk ------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckStandingDesk(Map map)
        {
            const string check = "desk";
            Guarded("the desk lists the open and held letters with their deadlines and the day's budget", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StandingKit kit = StandingSetup(map, r);
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                // (Two days on, so a letter held a day ago is held from a tick of this game: the events plan's clock may be hours old, and a tick before 0 reads as "never".)
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + 2 * GenDate.TicksPerDay);
                int now = Find.TickManager.TicksGame;
                int open0 = DeskBoard.OpenLetters();
                ChoiceLetter_PrisonDecision soon = kit.Send("RP_Decision_Mentor", 5), later = kit.Send("RP_Decision_TalentShow", 24);
                g.story.decisionsToday = budget;
                ChoiceLetter_PrisonDecision held = kit.Send("RP_Decision_FamilyDay", 12);
                if (!OnStack(soon) || !OnStack(later) || !g.story.deferredLetters.Contains(held))
                {
                    StandingFail(check, "the letters were not on the desk and held as the check set them up (its premise)");
                    return;
                }
                held.heldTick = now - GenDate.TicksPerDay;
                DeskBoard.DeskState s = DeskBoard.Desk(g, map, now);
                DeskBoard.DeskLetter dSoon = s.open.FirstOrDefault(x => x.letter == soon), dLater = s.open.FirstOrDefault(x => x.letter == later), dHeld = s.held.FirstOrDefault(x => x.letter == held);
                if (dSoon is not { hoursLeft: 5, urgent: true, held: false }) StandingFail(check, $"the five-hour letter reads {(dSoon == null ? "missing" : $"{dSoon.hoursLeft} h, urgent {dSoon.urgent}")}");
                if (dLater is not { hoursLeft: 24, urgent: false }) StandingFail(check, $"the day-long letter reads {(dLater == null ? "missing" : $"{dLater.hoursLeft} h, urgent {dLater.urgent}")}");
                if (dSoon != null && dLater != null && s.open.IndexOf(dSoon) > s.open.IndexOf(dLater)) StandingFail(check, "the open letters are not soonest first");
                if (s.open.Any(x => x.letter == held) || s.held.Any(x => x.letter == soon)) StandingFail(check, "a letter is in the wrong column");
                // It lapses at the midnight pass, the first after its days are up: held a day ago, that is the third midnight from now - the hours to tonight's midnight (the prison's clock, as the
                // settlement keeps its days) and two days more. Not the hour it was held, three days on: that was up to 24 hours early.
                Map home = Find.AnyPlayerHomeMap ?? map;
                int toMidnight = GenDate.TicksPerDay - GenLocalDate.DayTick(home);
                int lapse = DeskBoard.HoursCeil(2 * GenDate.TicksPerDay + toMidnight);
                if (dHeld is not { held: true, hoursLeft: 12 } || dHeld.lapseHours != lapse) StandingFail(check, $"the held letter reads {(dHeld == null ? "missing" : $"answer time {dHeld.hoursLeft} h, lapses in {dHeld.lapseHours} h")} (12 h, {lapse} h: at the third midnight)");
                // At any hour it was held: its lapse is a midnight of the prison's, the first pass that finds it past its days, and the one before does not.
                int heldTick0 = held.heldTick;
                float longitude = Find.WorldGrid.LongLatOf(home.Tile).x;
                var wrong = new List<string>();
                for (int h = 0; h < 24; h++)
                {
                    held.heldTick = now - GenDate.TicksPerDay - h * GenDate.TicksPerHour;
                    int at = StoryLetters.LapseTick(held), dayTick = GenDate.DayTick(GenDate.TickGameToAbs(at), longitude);
                    if (dayTick != 0 || !StoryLetters.Stale(held, at) || StoryLetters.Stale(held, at - GenDate.TicksPerDay)) wrong.Add($"held {h} h earlier: lapse at day tick {dayTick}, past its days then {StoryLetters.Stale(held, at)}, a day before {StoryLetters.Stale(held, at - GenDate.TicksPerDay)}");
                }
                held.heldTick = heldTick0;
                if (wrong.Count > 0) StandingFail(check, $"the lapse is not the first midnight past the held letter's days: {string.Join("; ", wrong.Take(3))}");
                if (s.used != budget || s.budget != budget || s.Left != 0 || !s.budgetOn) StandingFail(check, $"the budget reads {s.used} of {s.budget}, {s.Left} left, on {s.budgetOn} ({budget} of {budget}, none left)");
                if (s.HeadLine().NullOrEmpty()) StandingFail(check, "the desk's head line is empty");
                // (The panel's letters count is still the stack's unanswered letters: the two open ones counted, the held one not.)
                if (DeskBoard.OpenLetters() - open0 != 2) StandingFail(check, $"the panel's letters count rose by {DeskBoard.OpenLetters() - open0} for two open letters and a held one (2)");
            });
        }

        // ---- the saveload plan ---------------------------------------------------------------------------------------------------------------------------

        private static Dictionary<string, string> standingSeedWas;
        private static int standingSeedLetter = -1;

        /// <summary>
        /// Two orders in the book (the field off its default, empty) and a held letter one of them could not answer (its <c>standingTried</c> off its default, false: the snapshot's held letters show it),
        /// put back after the reload.
        /// </summary>
        private static void SeedStandingOrdersForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            standingSeedWas = new Dictionary<string, string>(g.standingOrders);
            g.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
            g.standingOrders["RP_Decision_Adjudication"] = "RP_Adjudication_Policy";
            ChoiceLetter_PrisonDecision held = ChoiceLetter_PrisonDecision.Make("RP_Decision_Mentor", "standing order test (reload)", "standing order test", map);
            held.Text += "\n\n" + StandingOrders.CouldNotLine("RP_Mentor_Apart", "standing order test");
            (held.standingTried, held.heldTick, held.deferredTimeout) = (true, Find.TickManager.TicksGame, 24 * GenDate.TicksPerHour);
            g.story.deferredLetters.Add(held);
            standingSeedLetter = held.ID;
            RPLog.Message($"[RimPrison:test] saveload standing orders: {StandingOrders.Describe(g)}, a held letter weighed once (letter {held.ID})");
        }

        /// <summary>After the round trip (judged by the snapshot): the game's own orders back, the seeded held letter gone.</summary>
        private static void UnseedStandingOrdersAfterReload()
        {
            if (standingSeedWas == null) return;
            PrisonGame g = PrisonGame.Instance;
            g.standingOrders.Clear();
            foreach (KeyValuePair<string, string> kv in standingSeedWas) g.standingOrders[kv.Key] = kv.Value;
            standingSeedWas = null;
            int id = standingSeedLetter;
            if (g.story.deferredLetters.RemoveAll(d => d.ID == id) != 1) problems0.Add($"standing orders (saveload): the seeded held letter {id} was not among the held letters after the load");
            standingSeedLetter = -1;
        }
    }
}
