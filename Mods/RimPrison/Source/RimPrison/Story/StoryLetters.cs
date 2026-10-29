using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// A soft budget for the warden's decision letters. The storyteller's beats keep to a daily budget (<see cref="StoryPacing"/>), but the systems around it (rings, complaints, trials,
    /// debts, the shuttle, special measures) post decisions of their own, so a bad day stacked five of them. Past the day's budget a decision letter waits until the next midnight and
    /// is then delivered (with its answer time starting from delivery), oldest first, within that day's budget. A letter that warns of something with a deadline of its own is never held
    /// back, however long its answer time is (see <see cref="Urgent"/>: the decision's or the arc's <c>urgent</c> flag, or an answer time under <see cref="UrgentHours"/> hours), and an urgent
    /// letter does not use up the day's budget. A letter that waits still counts as open for the systems that look for one (see <see cref="Open"/>); one that waits longer than
    /// <see cref="MaxHeldDays"/> days lapses as an unanswered letter does, and one whose arc ended in the meantime is dropped. In an autotest run the budget is off
    /// unless a check turns it on (<see cref="TestBudget"/>), so the plans' own checks see their letters at once. A letter a standing order answers (<see cref="StandingOrders"/>) never asks for a place.
    /// </summary>
    public static class StoryLetters
    {
        public const int UrgentHours = 12;

        /// <summary>The days a letter may wait for a place in the day's budget; then it is answered by default, like one left until it times out.</summary>
        public const int MaxHeldDays = 3;

        /// <summary>Set by a check that tests the budget itself (an autotest run otherwise bypasses it).</summary>
        public static bool TestBudget;

        /// <summary>Letters delivered by the midnight pass are not counted against the day again or held back a second time.</summary>
        private static bool delivering;

        public static bool Active => !AutoTest.Active || TestBudget;

        /// <summary>The day's decisions: three and one per twenty inmates, more at a difficulty where events come more often (never fewer at one where they come less).</summary>
        public static int Budget(int inmates) => Mathf.RoundToInt((3 + inmates / 20) * Mathf.Max(1f, PrisonDifficulty.EventFactor));

        /// <summary>
        /// Whether this letter must reach the desk at once: its decision or its arc is flagged (an inspection, a demand, a charge, a fight that is coming: something that happens on its own
        /// clock, so a letter that arrives after it is worth nothing), or its answer time is short.
        /// </summary>
        public static bool Urgent(ChoiceLetter_PrisonDecision d) =>
            d.decision is { urgent: true } || d.arcId != 0 && PrisonGame.Instance?.story.ArcById(d.arcId) is { def.urgent: true }
            || d.TimeoutActive && d.disappearAtTick - Find.TickManager.TicksGame < UrgentHours * GenDate.TicksPerHour;

        /// <summary>Every decision letter still to be answered: the ones on the stack, then the ones waiting for the next day.</summary>
        public static IEnumerable<Letter> Open()
        {
            foreach (Letter l in Find.LetterStack.LettersListForReading) yield return l;
            if (PrisonGame.Instance?.story.deferredLetters is { } waiting)
                foreach (ChoiceLetter_PrisonDecision d in waiting) yield return d;
        }

        public static bool AnyOpen(string decisionDef) => Open().Any(l => l is ChoiceLetter_PrisonDecision d && !d.answered && d.Is(decisionDef));

        /// <summary>Whether this letter waits for the next day instead of reaching the stack now (it is kept on the story; <see cref="Daily"/> delivers it).</summary>
        public static bool Defer(Letter let)
        {
            if (let is not ChoiceLetter_PrisonDecision d || delivering || !Active || PrisonGame.Instance is not { } game) return false;
            StorySystem story = game.story;
            Map map = d.map ?? Find.AnyPlayerHomeMap;
            int budget = Budget(map?.mapPawns.PrisonersOfColonySpawnedCount ?? 0);
            if (Urgent(d)) return false;
            if (story.decisionsToday < budget)
            {
                story.decisionsToday++;
                return false;
            }
            // (Its answer time starts when it is delivered: a letter that waited a night must not arrive already expired.)
            d.deferredTimeout = d.TimeoutActive ? System.Math.Max(GenDate.TicksPerHour, d.disappearAtTick - Find.TickManager.TicksGame) : -1;
            d.disappearAtTick = -1;
            d.heldTick = Find.TickManager.TicksGame;
            story.deferredLetters.Add(d);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:letters] held back a {d.decision?.defName} decision: {story.decisionsToday} of {budget} today, {story.deferredLetters.Count} waiting");
            return true;
        }

        /// <summary>A held letter that has waited longer than <see cref="MaxHeldDays"/> days at this tick: the midnight pass lapses it.</summary>
        public static bool Stale(ChoiceLetter_PrisonDecision d, int now) => d.heldTick >= 0 && now - d.heldTick > MaxHeldDays * GenDate.TicksPerDay;

        /// <summary>
        /// The tick a held letter lapses if no day has room for it: the first local midnight after it has waited <see cref="MaxHeldDays"/> days (the midnight pass is the only one that lapses
        /// letters, at the prison's midnight as the settlement keeps it: <see cref="Ledger.CurrentDay"/>). -1: it was not held.
        /// </summary>
        public static int LapseTick(ChoiceLetter_PrisonDecision d)
        {
            if (d.heldTick < 0) return -1;
            int due = d.heldTick + MaxHeldDays * GenDate.TicksPerDay;
            Map home = Find.AnyPlayerHomeMap;
            // (Due on a midnight to the tick: that midnight's pass does not lapse it, it has not waited longer; the next one does. No home map: the days are counted from the game's start.)
            int dayTick = home != null ? GenDate.DayTick(GenDate.TickGameToAbs(due), Find.WorldGrid.LongLatOf(home.Tile).x) : (int)GenMath.PositiveMod(due, GenDate.TicksPerDay);
            return due + GenDate.TicksPerDay - dayTick;
        }

        /// <summary>Midnight, before the day's own systems write their letters: a new day's budget.</summary>
        public static void NewDay(PrisonGame game) => game.story.decisionsToday = 0;

        /// <summary>
        /// Midnight, after the day's own systems (the loan, special measures) wrote theirs: the letters that waited are delivered, oldest first, as far as the budget goes. A letter whose
        /// arc is over is dropped, and one that has waited too long lapses (see <see cref="MaxHeldDays"/>).
        /// </summary>
        public static void Daily(PrisonGame game, Map map) => Daily(game, map, Find.TickManager.TicksGame);

        /// <summary>The same at a given tick (a check asks about letters that have waited for days in a game that is hours old).</summary>
        public static void Daily(PrisonGame game, Map map, int now)
        {
            StorySystem story = game.story;
            if (story.deferredLetters.Count == 0) return;
            int budget = Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
            delivering = true;
            try
            {
                foreach (ChoiceLetter_PrisonDecision gone in story.deferredLetters.Where(x => x.arcId != 0 && story.ArcById(x.arcId) is not { done: false }).ToList())
                    story.deferredLetters.Remove(gone);
                // A standing order given while a letter waited answers it now, whatever room the day has (it takes none of it), and before the letters that waited too long lapse: an order given on
                // a letter's last day answers it instead of its default. (Held, it was no urgent letter; its answer time is not started for this: it was twelve hours or more when it was held.) One
                // the order cannot answer gets its line and waits for its place as before. (Not as a delivery: a decision letter a worker sends from here asks for its place in the day's budget.)
                delivering = false;
                foreach (ChoiceLetter_PrisonDecision ordered in story.deferredLetters.Where(x => !x.standingTried && StandingOrders.Offered(x) && StandingOrders.OrderFor(x.decision) != null).ToList())
                    if (StandingOrders.TryAnswer(ordered)) story.deferredLetters.Remove(ordered);
                delivering = true;
                foreach (ChoiceLetter_PrisonDecision stale in story.deferredLetters.Where(x => Stale(x, now)).ToList())
                {
                    story.deferredLetters.Remove(stale);
                    stale.Lapse();
                }
                while (story.deferredLetters.Count > 0 && story.decisionsToday < budget)
                {
                    ChoiceLetter_PrisonDecision d = story.deferredLetters[0];
                    story.deferredLetters.RemoveAt(0);
                    story.decisionsToday++;
                    if (d.deferredTimeout > 0) d.StartTimeout(d.deferredTimeout);
                    d.deferredTimeout = -1;
                    d.heldTick = -1;
                    Find.LetterStack.ReceiveLetter(d);
                }
            }
            finally
            {
                delivering = false;
            }
        }
    }

    /// <summary>
    /// A decision letter a standing order answers never reaches the stack (see <see cref="StandingOrders"/>: it uses none of the day's budget); one past the day's budget is held back (see
    /// <see cref="StoryLetters"/>). A letter sent with a delay is weighed by both when the queue delivers it (the queue hands it to this method again): weighed when it was queued as well, it took a
    /// place in the day's budget twice, or one when a standing order answered it on delivery.
    /// </summary>
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
    public static class Patch_LetterStack_ReceiveLetter_Budget
    {
        public static bool Prefix(Letter let, int delayTicks) => delayTicks > 0 || !StandingOrders.TryAnswer(let) && !StoryLetters.Defer(let);
    }
}
