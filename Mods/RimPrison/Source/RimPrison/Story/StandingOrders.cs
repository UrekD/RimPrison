using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Standing orders: a routine decision (one whose def is marked <c>standingOrderAllowed</c>) the warden has answered by hand with "do this from now on" ticked is answered the same way from then on.
    /// A later letter of that decision never reaches the desk: it is answered at once as it arrives (before the day's budget is asked, so it uses none of it), archived, and told as a message with a
    /// "Review" link that opens the letter read-only. The order is kept by the answer's key, not its place, so a list of answers that changes keeps its meaning. Where the order cannot answer a
    /// letter (the letter is urgent, or the answer is not offered or is disabled for its case) the letter comes as usual with a line that says why. The Policy tab lists the orders and lifts them.
    /// </summary>
    public static class StandingOrders
    {
        /// <summary>The keys of a plain accept/decline letter's two answers (its buttons' own labels).</summary>
        public const string AcceptKey = "AcceptButton", DeclineKey = "RejectLetter";

        private static readonly string[] AcceptDecline = { AcceptKey, DeclineKey };

        /// <summary>How many of the letters standing orders answered the desk keeps for review (<see cref="PrisonGame.standingAnswered"/>, this session's, newest last).</summary>
        public const int RecentKept = 6;

        /// <summary>The live messages that tell of an answered letter, and the letter each one opens.</summary>
        private static readonly Dictionary<Message, ChoiceLetter_PrisonDecision> reviews = new Dictionary<Message, ChoiceLetter_PrisonDecision>();

        private const float ReviewWidth = 74f;

        /// <summary>The decision allows standing orders at all (marked in its def, and never an urgent one, whatever the XML says).</summary>
        public static bool Allowed(PrisonDecisionDef def) => def is { standingOrderAllowed: true, urgent: false };

        /// <summary>This answer may be given for good (the def's list of them; none listed: any).</summary>
        public static bool OptionAllowed(PrisonDecisionDef def, string key) => Allowed(def) && key != null && (def.standingOrderOptions.NullOrEmpty() || def.standingOrderOptions.Contains(key));

        /// <summary>The letter shows the "do this from now on" box: its decision allows it and it is no story arc's (an arc's letter is that arc's case).</summary>
        public static bool Offered(ChoiceLetter_PrisonDecision l) => PrisonGame.Instance != null && l != null && l.arcId == 0 && Allowed(l.decision);

        /// <summary>The keys of the letter's answers: its decision's options, or accept and decline.</summary>
        public static IReadOnlyList<string> AnswerKeys(ChoiceLetter_PrisonDecision l) => l.decision?.Worker.Options(l) ?? AcceptDecline;

        /// <summary>The standing order for this decision (an answer's key), or null.</summary>
        public static string OrderFor(PrisonDecisionDef def) => def != null && PrisonGame.Instance?.standingOrders is { } book && book.TryGetValue(def.defName, out string key) ? key : null;

        public static string DecisionLabel(string defName) => DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(defName) is { label: { Length: > 0 } } def ? def.LabelCap.ToString() : defName;

        /// <summary>An order the book may keep: the decision allows orders, takes this answer for good and still offers it (an old save's order for an answer since dropped from the decision is let go at load).</summary>
        public static bool Keeps(PrisonDecisionDef def, string key) => OptionAllowed(def, key) && KeyOffered(def, key);

        /// <summary>The decision's letters offer this answer (asked of a blank letter that is never sent: an allowed decision's answers do not depend on the case).</summary>
        public static bool KeyOffered(PrisonDecisionDef def, string key)
        {
            try
            {
                return (def.Worker.Options(new ChoiceLetter_PrisonDecision { decision = def }) ?? AcceptDecline).Contains(key);
            }
            catch (System.Exception e)
            {
                // (A worker that wants a real letter to list its answers: the order is kept, as it was before this test.)
                Log.Warning($"[RimPrison] could not list the answers of {def.defName} for a standing order: {e.Message}");
                return true;
            }
        }

        public static void Set(PrisonGame game, PrisonDecisionDef def, string key) => game.standingOrders[def.defName] = key;

        /// <summary>A game started or loaded: the last game's review links are let go.</summary>
        public static void Reset() => reviews.Clear();

        /// <summary>The Policy tab's "Lift": the decision's letters come to the desk again.</summary>
        public static bool Lift(PrisonGame game, string defName)
        {
            if (!game.standingOrders.Remove(defName)) return false;
            Messages.Message("RP_StandingOrderLifted".Translate(DecisionLabel(defName)), MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        /// <summary>A letter answered by hand with the box ticked: that answer becomes the standing order (unless it is one the decision does not allow for good).</summary>
        public static void AnsweredByHand(ChoiceLetter_PrisonDecision l, string key)
        {
            if (!l.standingTicked || PrisonGame.Instance is not { } game || !Offered(l)) return;
            l.standingTicked = false;
            if (!OptionAllowed(l.decision, key))
            {
                Messages.Message("RP_StandingOrderNotThisAnswer".Translate(key.Translate()), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Set(game, l.decision, key);
            Messages.Message("RP_StandingOrderSet".Translate(DecisionLabel(l.decision.defName), key.Translate()), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// Why the standing order cannot answer this letter (null: it can), and the answer's place among the letter's: the decision no longer allows the answer for good, the letter is urgent,
        /// the answer is not among this letter's, or it is disabled for its case (the reason the letter's button would give).
        /// </summary>
        public static string WhyNot(ChoiceLetter_PrisonDecision l, string key, out int index)
        {
            index = -1;
            if (!OptionAllowed(l.decision, key)) return "RP_StandingOrderWhyNotAllowed".Translate().Resolve();
            if (StoryLetters.Urgent(l)) return "RP_StandingOrderWhyUrgent".Translate().Resolve();
            IReadOnlyList<string> options = l.decision.Worker.Options(l);
            IReadOnlyList<string> keys = options ?? AcceptDecline;
            for (int i = 0; i < keys.Count; i++)
                if (keys[i] == key) index = i;
            if (index < 0) return "RP_StandingOrderWhyMissing".Translate().Resolve();
            return options != null ? l.decision.Worker.Disabled(l, index) : null;
        }

        /// <summary>
        /// The line a letter the order could not answer gets: the answer and the reason (the reason a button would give, a sentence of its own: its full stop dropped, as the line puts one after it).
        /// Raw, not resolved: the letter's text is resolved when it is shown, and a line resolved twice wrapped its money in two colour tags.
        /// </summary>
        public static string CouldNotLine(string key, string why) => "RP_StandingOrderCouldNot".Translate(key.Translate(), why.TrimEnd('.', ' ').CapitalizeFirst()).RawText;

        /// <summary>
        /// A letter on its way to the desk (the letter stack's prefix, and the midnight delivery of one that waited): answered by its decision's standing order if there is one that can answer it.
        /// True: answered, it does not reach the stack and uses none of the day's budget. False: it goes on as usual (with a line that says why the order could not answer it, if there was one).
        /// </summary>
        public static bool TryAnswer(Letter let)
        {
            if (let is not ChoiceLetter_PrisonDecision l || l.answered || l.standingTried || PrisonGame.Instance is not { } game || !Offered(l)) return false;
            string key = OrderFor(l.decision);
            if (key == null) return false;
            string why = WhyNot(l, key, out int index);
            if (why != null)
            {
                l.standingTried = true;
                l.Text += "\n\n" + CouldNotLine(key, why);
                if (AutoTest.Active) RPLog.Message($"[RimPrison:letters] standing order {key} could not answer a {l.decision.defName} letter: {why}");
                return false;
            }
            Answer(game, l, key, index);
            return true;
        }

        private static void Answer(PrisonGame game, ChoiceLetter_PrisonDecision l, string key, int index)
        {
            l.answered = true;
            l.arrivalTick = Find.TickManager.TicksGame;
            l.Text += "\n\n" + "RP_StandingOrderAnsweredLine".Translate(key.Translate()).RawText;
            l.byStandingOrder = true;
            try
            {
                if (l.decision.Worker.Options(l) != null) PrisonDecisions.Choose(l, index);
                else if (key == AcceptKey) PrisonDecisions.Accept(l);
                else PrisonDecisions.Decline(l);
            }
            catch (System.Exception e)
            {
                // (This runs inside the sender's ReceiveLetter: an exception let through would stop the sender half-way and lose the letter. The answer was given as far as the worker got, as one by
                // hand would have been, so the letter is archived and told like any other.)
                Log.Error($"[RimPrison] the standing order {key} threw while answering a {l.decision.defName} letter: {e}");
            }
            finally
            {
                l.byStandingOrder = false;
            }
            // (In the History tab like any letter that reached the desk: the review outlives the message.)
            Find.Archive.Add(l);
            game.standingAnswered.Add(l);
            if (game.standingAnswered.Count > RecentKept) game.standingAnswered.RemoveAt(0);
            var msg = new Message("RP_StandingOrderAnswered".Translate(l.Label, key.Translate()).Resolve(), MessageTypeDefOf.NeutralEvent, l.lookTargets ?? LookTargets.Invalid);
            Messages.Message(msg, historical: false);
            reviews.RemoveAll(kv => !Messages.IsLive(kv.Key));
            if (Messages.IsLive(msg)) reviews[msg] = l;
            if (AutoTest.Active) RPLog.Message($"[RimPrison:letters] standing order answered a {l.decision.defName} letter: {key}");
        }

        /// <summary>A live message offers to review this letter.</summary>
        public static bool HasReview(ChoiceLetter_PrisonDecision l) => reviews.ContainsValue(l);

        /// <summary>The letter a live message opens with its "Review" link, or null.</summary>
        public static ChoiceLetter_PrisonDecision ReviewOf(Message m) => m != null && reviews.TryGetValue(m, out ChoiceLetter_PrisonDecision l) ? l : null;

        /// <summary>The answered letter, read-only (it is not on the stack: only "close" is offered).</summary>
        public static void Review(ChoiceLetter_PrisonDecision l)
        {
            if (l != null && l.ArchivedOnly) l.OpenLetter();
        }

        /// <summary>The message's "Review" link, right of it (the prison panel draws the live messages: <see cref="Patch_Messages_BelowPanel"/>).</summary>
        public static void DrawReview(Message m)
        {
            if (reviews.Count == 0 || !reviews.TryGetValue(m, out ChoiceLetter_PrisonDecision l)) return;
            Rect r = m.lastDrawRect;
            var button = new Rect(r.xMax + 4f, r.y, ReviewWidth, r.height);
            // (It fades with its message.)
            float alpha = m.Alpha;
            Find.WindowStack.ImmediateWindow(Gen.HashCombineInt(l.ID, 61_284_337), button, WindowLayer.Super, () =>
            {
                Text.Font = GameFont.Small;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                if (Widgets.ButtonText(button.AtZero(), "RP_StandingOrderReview".Translate())) Review(l);
                GUI.color = Color.white;
            }, doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
        }

        // ---- the Policy tab's section ---------------------------------------------------------------------------------------------------------------------

        private static Vector2 scroll;

        /// <summary>The Policy tab's section (its right column, picked by the button at its head): what standing orders are, then one row per order (the decision, the answer, "Lift"), or a line on how to give one.</summary>
        public static void DrawSection(Rect rect, PrisonGame game)
        {
            string help = "RP_StandingOrdersTip".Translate();
            float helpH = Text.CalcHeight(help, rect.width);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, helpH), help);
            float y = rect.y + helpH + 8f;
            if (game.standingOrders.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(rect.x, y, rect.width, Mathf.Max(0f, rect.yMax - y)), "RP_StandingOrdersNone".Translate());
                GUI.color = Color.white;
                return;
            }
            const float rowH = 26f;
            var outRect = new Rect(rect.x, y, rect.width, rect.yMax - y);
            var view = new Rect(0f, 0f, outRect.width - 16f, game.standingOrders.Count * rowH);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float ry = 0f;
            string lift = null;
            foreach (KeyValuePair<string, string> order in game.standingOrders)
            {
                var row = new Rect(0f, ry, view.width, rowH - 2f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                string text = "RP_StandingOrderRow".Translate(DecisionLabel(order.Key), order.Value.Translate()).Resolve();
                var label = new Rect(row.x + 4f, row.y + 2f, row.width - 80f, row.height);
                Widgets.Label(label, text.Truncate(label.width));
                TooltipHandler.TipRegion(row, text);
                if (Widgets.ButtonText(new Rect(row.xMax - 72f, row.y + 1f, 72f, row.height - 2f), "RP_StandingOrderLift".Translate())) lift = order.Key;
                ry += rowH;
            }
            Widgets.EndScrollView();
            if (lift != null) Lift(game, lift);
        }

        /// <summary>For the saveload plan: the orders, sorted.</summary>
        public static string Describe(PrisonGame game)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, string> kv in game.standingOrders) parts.Add(kv.Key + ":" + kv.Value);
            parts.Sort(System.StringComparer.Ordinal);
            return string.Join(",", parts);
        }
    }

    /// <summary>The "do this from now on" box among a decision letter's answers (drawn as a checkbox by <see cref="Patch_DiaOption_StandingOrderBox"/>; a click on it does not close the letter).</summary>
    public class DiaOption_StandingOrder : DiaOption
    {
        public readonly ChoiceLetter_PrisonDecision letter;

        public DiaOption_StandingOrder(ChoiceLetter_PrisonDecision letter) : base("RP_StandingOrderBox".Translate())
        {
            this.letter = letter;
            clickSound = null;
        }

        public float Draw(Rect rect, bool active)
        {
            var row = new Rect(rect.x, rect.y, rect.width, 24f);
            bool on = letter.standingTicked;
            Widgets.CheckboxLabeled(row, text, ref on, disabled: !active, placeCheckboxNearText: true);
            letter.standingTicked = on;
            // (Built on hover, not every frame the letter is open.)
            TooltipHandler.TipRegion(row, Tip, Gen.HashCombineInt(letter.ID, 61_284_338));
            return row.height;
        }

        private string Tip()
        {
            string now = StandingOrders.OrderFor(letter.decision);
            string tip = "RP_StandingOrderBoxTip".Translate().Resolve();
            if (letter.decision is { standingOrderOptions.Count: > 0 } def)
                tip += "\n\n" + "RP_StandingOrderBoxOnly".Translate(string.Join(", ", def.standingOrderOptions.Select(k => k.Translate().Resolve()))).Resolve();
            if (now != null) tip += "\n\n" + "RP_StandingOrderBoxNow".Translate(now.Translate()).Resolve();
            return tip;
        }
    }

    [HarmonyPatch(typeof(DiaOption), nameof(DiaOption.OptOnGUI))]
    public static class Patch_DiaOption_StandingOrderBox
    {
        public static bool Prefix(DiaOption __instance, Rect rect, bool active, ref float __result)
        {
            if (__instance is not DiaOption_StandingOrder box) return true;
            __result = box.Draw(rect, active);
            return false;
        }
    }
}
