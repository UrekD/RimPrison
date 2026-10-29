using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Why an inmate asks to see the warden (the order is the rank: a more serious reason takes the place of a lesser one, see <see cref="WardenAsks.Raise"/>).</summary>
    public enum WardenAskReason : byte
    {
        /// <summary>A witness of a murder case who was got at and came forward to the warden rather than the guards (see <see cref="Trials.TryPressure"/>).</summary>
        Witness,
        /// <summary>Beaten over a debt (<see cref="DebtSystem.TryBeat"/>).</summary>
        Debtor,
        /// <summary>A member of a gang the warden knows whose doubts became the goal to leave, still waiting for the session (<see cref="GangExit.RollDoubt"/>).</summary>
        Doubter,
        /// <summary>On hunger strike (<see cref="IncidentWorker_HungerStrike"/>).</summary>
        HungerStrike,
        /// <summary>Clean long enough in supermax to be put to the warden for the step-down (<see cref="SupermaxSystem.AskStepDown"/>).</summary>
        StepDown,
        /// <summary>Asked for another cell (<see cref="CellRequests.TryRaise"/>).</summary>
        CellRequest,
        /// <summary>Someone of a wing where a false rumour has started (<see cref="Rumours.Start"/>).</summary>
        Rumour,
    }

    /// <summary>One inmate asking to see the warden: who (by id), why, and since when. Saved in <see cref="PrisonMap.asks"/>.</summary>
    public class WardenAsk : IExposable
    {
        public int inmateId = -1;
        public WardenAskReason reason;
        public int tick = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref inmateId, "id", -1);
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref tick, "tick", -1);
        }

        public WardenAsk Copy() => (WardenAsk)MemberwiseClone();

        public string Describe() => $"{inmateId}:{reason}:{tick}";
    }

    /// <summary>
    /// Asks to see the warden (gap analysis 7, weak system W2): the systems that give an inmate a reason to want the warden in person put them in a per-map queue - a threatened trial witness, a debtor after a beating, a
    /// gang member with doubts, a hunger striker, a step-down candidate, a cell request, someone of a wing a false rumour runs on. At most one request per inmate (a more serious reason takes the place of a lesser one, the
    /// same reason again starts the day again), each lapses after <see cref="LapseTicks"/>, and one whose reason is gone is dropped at the hour. The HUD counts them; Intel lists them with a "Go". Answered in person (the
    /// warden's word with them, or a walk that meets them) it does more than a generic word or walk: trust and calm scaled up, and the reason's own (<see cref="Answer"/>).
    /// </summary>
    public static class WardenAsks
    {
        /// <summary>A request lapses a day after it was made.</summary>
        public static int LapseTicks => GenDate.TicksPerDay;

        /// <summary>A word with an asker: a chat's worth again on top of what the word itself does (a chat is +6 trust, -5 tension: answered, +12 and -10).</summary>
        public const float WordTrust = 6f, WordTension = 5f;

        /// <summary>A walk that meets an asker: twice the walk's trust (<see cref="WardenSystem.WalkTrust"/>), never resented however wound up they are.</summary>
        public const float WalkTrust = 2f * WardenSystem.WalkTrust;

        private static int Now => Find.TickManager.TicksGame;

        public static bool Lapsed(WardenAsk a, int now) => now - a.tick >= LapseTicks || now < a.tick;

        /// <summary>The live request of an inmate on the map, or null (a short list: one per inmate at most).</summary>
        public static WardenAsk Of(PrisonMap pm, int inmateId)
        {
            if (pm == null) return null;
            int now = Now;
            List<WardenAsk> list = pm.asks;
            for (int i = 0; i < list.Count; i++)
                if (list[i].inmateId == inmateId && !Lapsed(list[i], now)) return list[i];
            return null;
        }

        public static WardenAsk Of(Pawn p) => p?.MapHeld is { } map ? Of(PrisonMap.Of(map), p.thingIDNumber) : null;

        /// <summary>Whether the inmate has asked since <paramref name="tick"/> (a word that day does not stop the warden answering a request made after it).</summary>
        public static bool AskedSince(Pawn p, int tick) => Of(p) is { } a && a.tick > tick;

        /// <summary>
        /// The inmate asks to see the warden. One request per inmate: a reason as serious as the one they have or less is not added (the same reason starts its day again), a more serious one takes its place. Returns
        /// whether the queue changed.
        /// </summary>
        public static bool Raise(Pawn p, WardenAskReason reason)
        {
            if (p?.MapHeld is not { IsPlayerHome: true } map || PrisonMap.Of(map) is not { } pm || p.Inmate() == null || !InmateUtility.IsActiveInmate(p)) return false;
            int now = Now;
            pm.asks.RemoveAll(a => a.inmateId == p.thingIDNumber && Lapsed(a, now));
            if (Of(pm, p.thingIDNumber) is { } had)
            {
                if (reason > had.reason) return false;
                (had.reason, had.tick) = (reason, now);
                return true;
            }
            pm.asks.Add(new WardenAsk { inmateId = p.thingIDNumber, reason = reason, tick = now });
            return true;
        }

        private static readonly Dictionary<int, Pawn> byId = new Dictionary<int, Pawn>();

        /// <summary>The live requests of the map whose inmate is here (what the HUD counts and Intel lists), oldest first.</summary>
        public static List<(WardenAsk ask, Pawn inmate)> Live(Map map)
        {
            var live = new List<(WardenAsk, Pawn)>();
            if (PrisonMap.Of(map) is not { } pm || pm.asks.Count == 0) return live;
            int now = Now;
            // (Intel draws this every frame: one pass over the inmates into a lookup, not a search per request.)
            byId.Clear();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned) byId[p.thingIDNumber] = p;
            foreach (WardenAsk a in pm.asks)
                if (!Lapsed(a, now) && byId.TryGetValue(a.inmateId, out Pawn p) && !p.Dead && InmateUtility.IsActiveInmate(p)) live.Add((a, p));
            byId.Clear();
            live.Sort((x, y) => x.Item1.tick.CompareTo(y.Item1.tick));
            return live;
        }

        /// <summary>The hours a request has left (at least 1 while it lives).</summary>
        public static int HoursLeft(WardenAsk a, int now) => Mathf.Max(1, Mathf.CeilToInt((a.tick + LapseTicks - now) / (float)GenDate.TicksPerHour));

        /// <summary>The hour: lapsed requests go, those of an inmate who is gone or no longer active, and those whose reason is over (the strike ended, the rumour ended, the letter answered...). A pass over a short list.</summary>
        public static void Hourly(Map map, PrisonMap pm)
        {
            if (pm == null || pm.asks.Count == 0) return;
            int now = Now;
            // (One pass over the inmates into a lookup, as CourtUtil.FindInmate would find them, not a search of the prisoner list per request.)
            byId.Clear();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (!p.Dead && !p.Destroyed && p.guest is { Released: false }) byId[p.thingIDNumber] = p;
            pm.asks.RemoveAll(a => a == null || Lapsed(a, now) || !byId.TryGetValue(a.inmateId, out Pawn p) || !InmateUtility.IsActiveInmate(p) || p.Inmate() is not { } c || Moot(map, p, c, a));
            byId.Clear();
        }

        /// <summary>The reason is over: what they wanted to see the warden about no longer stands.</summary>
        public static bool Moot(Map map, Pawn p, CompInmate c, WardenAsk a)
        {
            switch (a.reason)
            {
                case WardenAskReason.Witness:
                    return WitnessCase(p) == null;
                case WardenAskReason.Debtor:
                    return DebtOf(p) == null;
                case WardenAskReason.Doubter:
                    // (A gang the warden does not know is never the reason shown: such a doubter waits for the chaplain, see GangExit.RollDoubt.)
                    return !(GangExit.Seeking(c) && c.want.step == 0 && c.gang != null && c.gangKnown);
                case WardenAskReason.HungerStrike:
                    return !HungerStrike.Fasting(c);
                case WardenAskReason.StepDown:
                    return !SupermaxSystem.IsIn(c) || StepDownLetter(p) == null;
                case WardenAskReason.CellRequest:
                    return CellLetter(p) == null;
                case WardenAskReason.Rumour:
                    return RumourOf(map, c, a) == null;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The rumour a request about the talk is about, while it lives: the false one live on their wing that started the tick they asked (the request is made as the rumour starts, see <see cref="Notify_FalseRumour"/>, and
        /// only a new false rumour on their wing makes it again). A rumour that started later has taken its place (a true one, perhaps); one that started earlier is another wing's, the one they were moved to. Neither is theirs:
        /// null, and the request is over.
        /// </summary>
        public static Rumour RumourOf(Map map, CompInmate c, WardenAsk a) => Rumours.LiveOn(map, c?.security) is { isTrue: false } r && r.startTick == a.tick ? r : null;

        /// <summary>The case this inmate is a witness of and has not recanted in, and their entry in it; null when there is none.</summary>
        public static (MurderTrial trial, TrialWitness witness)? WitnessCase(Pawn p)
        {
            if (PrisonGame.Instance?.story.trials is not { } trials) return null;
            foreach (MurderTrial t in trials)
                foreach (TrialWitness w in t.witnesses)
                    if (!w.staff && !w.recanted && w.id == p.thingIDNumber) return (t, w);
            return null;
        }

        /// <summary>A debt this inmate owes that the warden knows of (the beating marks its debt known before they ask), or null: a hidden one is neither named nor kept the request alive.</summary>
        public static InmateDebt DebtOf(Pawn p)
        {
            List<InmateDebt> debts = DebtSystem.Debts;
            if (debts == null) return null;
            foreach (InmateDebt d in debts)
                if (d.debtorId == p.thingIDNumber && d.known) return d;
            return null;
        }

        /// <summary>The cell request letter this inmate raised that waits for an answer, or null.</summary>
        public static ChoiceLetter_PrisonDecision CellLetter(Pawn p) => OpenLetter(p, CellRequests.DecisionDef);

        /// <summary>The step-down letter about this inmate that waits for an answer, or null (answered either way, or timed out: the request is over).</summary>
        public static ChoiceLetter_PrisonDecision StepDownLetter(Pawn p) => OpenLetter(p, SupermaxSystem.StepDownDecisionDef);

        private static ChoiceLetter_PrisonDecision OpenLetter(Pawn p, string decisionDef)
        {
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is(decisionDef) && d.pawns.Count > 0 && d.pawns[0] == p) return d;
            return null;
        }

        /// <summary>What they want to see the warden about, in the words of Intel's row (only what the warden may know: a witness's case, not the threat; a debt the warden knows of; the rumour they asked about, not another).</summary>
        public static string ReasonText(Map map, Pawn p, CompInmate c, WardenAsk a)
        {
            switch (a.reason)
            {
                case WardenAskReason.Witness:
                    return WitnessCase(p) is { } wc ? "RP_Ask_Reason_Witness".Translate(wc.trial.killerName).Resolve() : "RP_Ask_Reason_WitnessGone".Translate().Resolve();
                case WardenAskReason.Debtor:
                    return DebtOf(p) is { known: true } d ? "RP_Ask_Reason_Debtor".Translate(d.creditorName).Resolve() : "RP_Ask_Reason_DebtorGone".Translate().Resolve();
                case WardenAskReason.Doubter:
                    // (Never shown otherwise: the request of a doubter whose gang is not known is moot, see Moot.)
                    return c.gang != null && c.gangKnown ? "RP_Ask_Reason_DoubterKnown".Translate(c.gang.label).Resolve() : "RP_Ask_Reason_Other".Translate().Resolve();
                case WardenAskReason.HungerStrike:
                    return "RP_Ask_Reason_HungerStrike".Translate().Resolve();
                case WardenAskReason.StepDown:
                    return "RP_Ask_Reason_StepDown".Translate(SupermaxSystem.CleanDays(c)).Resolve();
                case WardenAskReason.CellRequest:
                    return "RP_Ask_Reason_CellRequest".Translate().Resolve();
                case WardenAskReason.Rumour:
                    return RumourOf(map, c, a) is { } r ? "RP_Ask_Reason_Rumour".Translate(Rumours.Text(r)).Resolve() : "RP_Ask_Reason_RumourGone".Translate().Resolve();
                default:
                    return "RP_Ask_Reason_Other".Translate().Resolve();
            }
        }

        // ---- the rumour's asker ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>A false rumour started on the wing: someone of it who did not start it (awake, if anyone is) asks to see the warden about the talk. Returns who, or null.</summary>
        public static Pawn Notify_FalseRumour(Map map, SecurityLevelDef level, int originId)
        {
            if (map == null || level == null) return null;
            var wing = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.thingIDNumber != originId && InmateUtility.IsActiveInmate(p) && p.Inmate()?.security == level && !ShuttleSystem.OnTheWayOut(p)) wing.Add(p);
            if (wing.Count == 0) return null;
            List<Pawn> awake = wing.FindAll(p => p.Awake() && !p.Downed && !p.InMentalState);
            Pawn asker = (awake.Count > 0 ? awake : wing).RandomElement();
            return Raise(asker, WardenAskReason.Rumour) ? asker : null;
        }

        // ---- answered in person ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The warden met an inmate who asked to see them: by a word (<paramref name="byWalk"/> false: after the word has done its own) or on a walk (in place of the walk's meeting). The request goes; if its reason still stands
        /// it is answered: a word adds <see cref="WordTrust"/> trust and takes <see cref="WordTension"/> off their tension (a chat's worth again), a walk gives <see cref="WalkTrust"/> (twice the walk's, never resented) and the
        /// warden's word as a memory; then the reason's own (<see cref="AnswerReason"/>). Returns whether a request was answered.
        /// </summary>
        public static bool Answer(Pawn warden, Pawn inmate, bool byWalk)
        {
            Map map = inmate?.MapHeld;
            PrisonMap pm = PrisonMap.Of(map);
            CompInmate c = inmate?.Inmate();
            if (pm == null || c == null || warden == null || Of(pm, inmate.thingIDNumber) is not { } ask) return false;
            pm.asks.Remove(ask);
            if (Moot(map, inmate, c, ask)) return false;
            string reason = ReasonText(map, inmate, c, ask);
            if (byWalk)
            {
                StaffMemories.Note(c, warden, WalkTrust);
                inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPWardenDefOf.RP_WardenHadAWord);
            }
            else
            {
                StaffMemories.Note(c, warden, WordTrust);
                c.tension = Mathf.Max(0f, c.tension - WordTension);
            }
            string extra = AnswerReason(warden, inmate, c, ask, map);
            string line = "RP_Ask_Answered".Translate(warden.LabelShort, inmate.LabelShort, reason).Resolve();
            Messages.Message(extra == null ? line : line + " " + extra, inmate, MessageTypeDefOf.PositiveEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_AskAnswered".Translate(warden.LabelShort, inmate.LabelShort, reason), inmate, warden);
            Answered++;
            return true;
        }

        /// <summary>How many requests were answered in this session (for a run's own checks).</summary>
        public static int Answered;

        /// <summary>
        /// What answering the reason does beyond trust and calm, each an existing effect at a step up: a witness tells the warden they were got at (the report the guards would otherwise have had: the threat is known); a doubter is heard out
        /// as the chaplain would (<see cref="GangExit.Notify_Session"/>: the gang known, the renouncement on its way); the rumour's asker names who started it and that it is false (the walk names the origin only, an informant both).
        /// The rest (a debtor, a striker, a step-down candidate, a cell request) is the trust and calm alone. Returns a line to add to the message, or null.
        /// </summary>
        private static string AnswerReason(Pawn warden, Pawn inmate, CompInmate c, WardenAsk ask, Map map)
        {
            switch (ask.reason)
            {
                case WardenAskReason.Witness:
                    if (WitnessCase(inmate) is not { } wc || wc.witness.pressureKnown) return null;
                    Trials.Report(wc.trial, wc.witness, inmate, warden);
                    return null;
                case WardenAskReason.Doubter:
                    GangExit.Notify_Session(warden, inmate);
                    return null;
                case WardenAskReason.Rumour:
                    if (RumourOf(map, c, ask) is not { } r) return null;
                    (bool knewOrigin, bool knewTruth) = (r.originKnown, r.truthKnown);
                    r.originKnown = r.truthKnown = true;
                    if (knewOrigin && knewTruth) return null;
                    string wing = Rumours.WingName(r.level), text = Rumours.Text(r);
                    // (The chronicle has the origin once: a walk or a tip that named it already wrote the line.)
                    if (!knewOrigin) Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_RumourOrigin".Translate(wing, text, r.originName ?? "?"));
                    return (r.isTrue ? "RP_Ask_RumourTrue" : "RP_Ask_RumourFalse").Translate(r.originName ?? "?", text).Resolve();
                default:
                    return null;
            }
        }

        // ---- Intel's "Go" -----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Why the "Go" of a request is off (resolved), or null: the warden's walk of the wing for a rumour, the word otherwise. <paramref name="wings"/>: <see cref="WardenSystem.Wings"/> worked out already.</summary>
        public static string GoUnready(Pawn warden, WardenAsk a, Pawn inmate, List<PrisonZoneDef> wings)
        {
            if (warden == null || warden.GetComp<CompStaff>() is not { } sc) return "RP_Rumour_NoWarden".Translate();
            if (a.reason == WardenAskReason.Rumour) return Rumours.WalkUnready(warden, inmate.Inmate()?.security, wings);
            if (!WardenGizmos.CanTalkTo(inmate)) return "RP_Ask_CannotTalk".Translate(inmate.LabelShort);
            return WardenSystem.Unready(warden, sc.wordReadyTick);
        }

        /// <summary>Intel's "Go": the walk of their wing for a rumour; otherwise what the warden can say to them, to choose from (the word answers the request whatever it is).</summary>
        public static void Go(Pawn warden, WardenAsk a, Pawn inmate, List<PrisonZoneDef> wings)
        {
            if (a.reason == WardenAskReason.Rumour)
            {
                if (Rumours.WingZone(inmate.MapHeld, inmate.Inmate()?.security, wings) is { } zone) WardenOrders.Walk(warden, zone);
                return;
            }
            var options = new List<FloatMenuOption>();
            foreach (WordOption o in WardenSystem.WordOptions(warden, inmate))
            {
                WordOption opt = o;
                options.Add(opt.disabled != null
                    ? new FloatMenuOption(opt.label + " (" + opt.disabled + ")", null)
                    : new FloatMenuOption(opt.label, () => WardenOrders.Word(warden, inmate, opt.kind)));
            }
            if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
        }

        // ---- what the screens show ------------------------------------------------------------------------------------------------------------------------

        /// <summary>The HUD's line: how many ask to see the warden.</summary>
        public static string HudLine(int n) => n == 0 ? "RP_HudAsksNone".Translate().Resolve() : n == 1 ? "RP_HudAsksOne".Translate().Resolve() : "RP_HudAsks".Translate(n).Resolve();

        /// <summary>The HUD line's tip: who asks and why (the first few), and the click.</summary>
        public static string HudTip(Map map, List<(WardenAsk ask, Pawn inmate)> live)
        {
            var sb = new System.Text.StringBuilder("RP_HudAsksTip".Translate(Mathf.RoundToInt(LapseTicks / (float)GenDate.TicksPerHour)).Resolve());
            if (live.Count > 0) sb.Append("\n");
            int now = Now;
            foreach ((WardenAsk a, Pawn p) in live.Take(6))
                sb.Append("\n").Append("RP_Ask_TipLine".Translate(p.LabelShort, ReasonText(map, p, p.Inmate(), a), HoursLeft(a, now)).Resolve());
            if (live.Count > 6) sb.Append("\n").Append("RP_Ask_TipMore".Translate(live.Count - 6).Resolve());
            return sb.ToString();
        }

        /// <summary>For the saveload snapshot.</summary>
        public static string Describe(PrisonMap pm) => pm == null ? "" : string.Join(",", pm.asks.Select(a => a.Describe()));
    }
}
