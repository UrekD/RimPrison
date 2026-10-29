using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Asks to see the warden (<see cref="WardenAsks"/>, gap analysis 7, W2). The queue: one request per inmate (a lesser reason does not replace a more serious one, a more serious one does, the same one starts its day again), the
    /// HUD's row (its count and tip), the lapse after a day (the clock moved forward and back, never before 0), the hour's pass dropping a request whose reason is over or whose inmate is no longer active. Answered by a word: a
    /// chat with an asker gives twice a chat's trust and calm (a control chat beside it), a request made after the day's word lets the warden speak to them again (not sound them out again), a request whose reason is over gives
    /// nothing more. Answered on a walk: a wound-up asker trusts twice the walk's and does not resent it (a control beside them resents it). The reasons' own, each through its system's own hook: a witness got at asks only out of
    /// the report's own dice (failed dice: nothing; a witness who does not trust the warden, or whom the warden does not know of: the guards), and the word reports the threat; a debtor beaten (the request named and held by the
    /// known debt, not a hidden one); a doubter of a known gang asks and is heard out by the word, one of a hidden gang never asks; a hunger striker; a cell request (dropped once its letter is answered); a false rumour has
    /// someone of the wing who did not start it ask, and meeting them on the walk names the origin and the truth (the origin's line written once), a true one has nobody ask, an ended one drops the request, and a request is
    /// tied to its own rumour (not one that replaced it, nor another wing's after a transfer); the step-down letter (dropped once answered or out of supermax). Intel's "Go": why it is off, and the walk it orders for a rumour.
    /// The saveload plan's seed is at the end. The queue is put back by the rollback (see Rollback).
    /// </summary>
    public static partial class AutoTest
    {
        private static void AskFail(string check, string what) => problems0.Add($"warden asks ({check}): {what}");

        private static void CheckWardenAsks(Map map)
        {
            CheckAskQueue(map);
            CheckAskWord(map);
            CheckAskWalk(map);
            CheckAskWitness(map);
            CheckAskDebtor(map);
            CheckAskDoubter(map);
            CheckAskStrike(map);
            CheckAskCellRequest(map);
            CheckAskRumour(map);
            CheckAskRumourTied(map);
            CheckAskStepDown(map);
            CheckAskGo(map);
        }

        /// <summary>The map's queue, emptied for the check (the rollback puts the real one back).</summary>
        private static PrisonMap AskIsolate(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            pm.asks = new List<WardenAsk>();
            return pm;
        }

        // ---- the queue ------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAskQueue(Map map)
        {
            const string check = "the queue";
            Guarded("warden asks: the queue", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = AskIsolate(map);
                Pawn a = s.Inmate(), b = s.Inmate(), idle = s.Inmate();
                int now0 = s.Now;

                // One request each; a lesser reason does not take the place of a more serious one, a more serious one does, the same reason starts the day again.
                if (!WardenAsks.Raise(a, WardenAskReason.Debtor) || WardenAsks.Of(a) is not { reason: WardenAskReason.Debtor } first || first.tick != now0)
                {
                    AskFail(check, $"a debtor's request was not queued ({WardenAsks.Describe(pm)})");
                    return;
                }
                if (WardenAsks.Raise(a, WardenAskReason.CellRequest) || WardenAsks.Of(a).reason != WardenAskReason.Debtor) AskFail(check, $"a cell request took the place of a debtor's request ({WardenAsks.Describe(pm)})");
                if (!WardenAsks.Raise(a, WardenAskReason.Witness) || WardenAsks.Of(a).reason != WardenAskReason.Witness) AskFail(check, $"a witness's request did not take the place of a debtor's ({WardenAsks.Describe(pm)})");
                if (pm.asks.Count(x => x.inmateId == a.thingIDNumber) != 1) AskFail(check, $"two requests for one inmate ({WardenAsks.Describe(pm)})");
                WardenAsks.Raise(b, WardenAskReason.HungerStrike);
                b.Inmate().hungerStrikeUntilTick = now0 + GenDate.TicksPerDay;
                List<(WardenAsk ask, Pawn inmate)> live = WardenAsks.Live(map);
                if (live.Count != 2 || !live.Any(x => x.inmate == a) || !live.Any(x => x.inmate == b) || live.Any(x => x.inmate == idle)) AskFail(check, $"the live list has {live.Count} ({string.Join(",", live.Select(x => x.inmate.LabelShort))}), a and b expected");
                // The HUD's row, as its snapshot builds it: the count, and the tip naming who asks.
                (int hudCount, string hudTip) = PrisonHUD.AsksLine(map);
                if (hudCount != 2 || !hudTip.Contains(a.LabelShort) || !hudTip.Contains(b.LabelShort))
                    AskFail(check, $"the HUD's row counts {hudCount} (2 expected), its tip names a {hudTip.Contains(a.LabelShort)}, b {hudTip.Contains(b.LabelShort)} (both expected)");

                // The hour keeps both (their reasons stand: the witness reason is checked below with a case of its own, so a's is made a strike too).
                a.Inmate().hungerStrikeUntilTick = now0 + GenDate.TicksPerDay;
                WardenAsks.Raise(a, WardenAskReason.HungerStrike);
                WardenAsks.Of(a).reason = WardenAskReason.HungerStrike;
                WardenAsks.Hourly(map, pm);
                if (pm.asks.Count != 2) AskFail(check, $"the hour dropped a request whose reason stands ({WardenAsks.Describe(pm)})");
                // The strike over: the hour drops b's. An inmate no longer active: Raise refuses, the live list leaves them out.
                b.Inmate().hungerStrikeUntilTick = -1;
                WardenAsks.Hourly(map, pm);
                if (WardenAsks.Of(b) != null || WardenAsks.Of(a) == null) AskFail(check, $"after a strike ended the hour left {WardenAsks.Describe(pm)} (b's dropped, a's kept expected)");
                // (Released: what IsActiveInmate reads, which Raise asks. The comp's activeFlag is only its copy for other threads, refreshed every 60 ticks.)
                idle.guest.Released = true;
                bool raisedIdle = WardenAsks.Raise(idle, WardenAskReason.CellRequest);
                idle.guest.Released = false;
                if (raisedIdle) AskFail(check, "an inmate who is not active was queued");

                // A day later the request has lapsed (the clock forward a day and an hour, back in the rollback).
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + GenDate.TicksPerDay + GenDate.TicksPerHour);
                a.Inmate().hungerStrikeUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                if (WardenAsks.Of(a) != null || WardenAsks.Live(map).Count != 0) AskFail(check, "a request a day old is still live");
                WardenAsks.Hourly(map, pm);
                if (pm.asks.Count != 0) AskFail(check, $"the hour kept a lapsed request ({WardenAsks.Describe(pm)})");
                // (Raised again after the lapse: a fresh day.)
                if (!WardenAsks.Raise(a, WardenAskReason.HungerStrike) || WardenAsks.HoursLeft(WardenAsks.Of(a), Find.TickManager.TicksGame) != 24) AskFail(check, "a request raised after one lapsed did not have its full day");
                Find.TickManager.DebugSetTicksGame(clock0);
            });
        }

        // ---- answered by a word ---------------------------------------------------------------------------------------------------------------------------

        private static void CheckAskWord(Map map)
        {
            const string check = "the word";
            Guarded("warden asks: the word", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = AskIsolate(map);
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                Pawn asker = s.Inmate(tension: 40f), control = s.Inmate(tension: 40f);
                int now = s.Now;
                foreach (Pawn p in new[] { asker, control }) p.Inmate().hungerStrikeUntilTick = now + GenDate.TicksPerDay;
                WardenAsks.Raise(asker, WardenAskReason.HungerStrike);
                int answered0 = WardenAsks.Answered;
                sc.wordReadyTick = -1;
                WardenSystem.Word(w, asker, WardenWord.Chat);
                sc.wordReadyTick = -1;
                WardenSystem.Word(w, control, WardenWord.Chat);
                (float ta, float tc) = (StaffMemories.ScoreFor(asker.Inmate(), w), StaffMemories.ScoreFor(control.Inmate(), w));
                (float ea, float ec) = (asker.Inmate().tension, control.Inmate().tension);
                if (Mathf.Abs(ta - (6f + WardenAsks.WordTrust)) > 0.01f || Mathf.Abs(tc - 6f) > 0.01f) AskFail(check, $"trust after a chat - the asker {ta} (expected {6f + WardenAsks.WordTrust}), a control {tc} (expected 6)");
                if (Mathf.Abs(ea - (40f - 5f - WardenAsks.WordTension)) > 0.01f || Mathf.Abs(ec - 35f) > 0.01f) AskFail(check, $"tension after a chat - the asker {ea} (expected {40f - 5f - WardenAsks.WordTension}), a control {ec} (expected 35)");
                if (WardenAsks.Of(asker) != null || WardenAsks.Answered != answered0 + 1) AskFail(check, $"the request was not answered by the word ({WardenAsks.Describe(pm)}, answered {WardenAsks.Answered - answered0})");

                // Spoken to today: no second word - unless they asked since.
                bool Today(Pawn p) => WardenSystem.WordOptions(w, p).First(o => o.kind == WardenWord.Chat).disabled != null;
                if (!Today(control)) AskFail(check, "(control) an inmate spoken to today could be spoken to again");
                // (The request a tick after the word: the clock forward one tick, back in the rollback.)
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + 1);
                WardenAsks.Raise(control, WardenAskReason.HungerStrike);
                if (Today(control)) AskFail(check, "an inmate who asked to see the warden after the day's word could not be spoken to");
                // (Lifted for what answers a request, not for the informant's pitch: one who turned it down is not sounded out again the same day because they asked about something else.)
                List<WordOption> offered = WardenSystem.WordOptions(w, control);
                if (!offered.Any(o => o.kind == WardenWord.Informant))
                    RPLog.Message("[RimPrison:test] warden asks: the word - the informant's pitch not on offer to the control here, its day's limit not checked");
                else if (offered.First(o => o.kind == WardenWord.Informant).disabled == null) AskFail(check, "an inmate spoken to today who then asked to see the warden could be sounded out as an informant again the same day");
                Find.TickManager.DebugSetTicksGame(clock0);

                // A request whose reason is over gives nothing more (the strike ended before the warden came), and goes.
                Pawn late = s.Inmate(tension: 40f);
                late.Inmate().hungerStrikeUntilTick = now + GenDate.TicksPerDay;
                WardenAsks.Raise(late, WardenAskReason.HungerStrike);
                late.Inmate().hungerStrikeUntilTick = -1;
                sc.wordReadyTick = -1;
                WardenSystem.Word(w, late, WardenWord.Chat);
                float tl = StaffMemories.ScoreFor(late.Inmate(), w);
                if (Mathf.Abs(tl - 6f) > 0.01f || WardenAsks.Of(late) != null) AskFail(check, $"a request whose strike was over - trust {tl} (6, a plain chat, expected), request left {WardenAsks.Of(late) != null}");
            });
        }

        // ---- answered on a walk ---------------------------------------------------------------------------------------------------------------------------

        private static void CheckAskWalk(Map map)
        {
            const string check = "the walk";
            Guarded("warden asks: the walk", map, r =>
            {
                var s = new WardenScene(map, r);
                AskIsolate(map);
                Pawn w = s.Warden();
                Pawn asker = s.Inmate(tension: 90f), control = s.Inmate(tension: 90f);
                asker.Inmate().hungerStrikeUntilTick = s.Now + GenDate.TicksPerDay;
                WardenAsks.Raise(asker, WardenAskReason.HungerStrike);
                (int ma, int mc) = (WardenSystem.Meet(w, asker), WardenSystem.Meet(w, control));
                (float ta, float tc) = (StaffMemories.ScoreFor(asker.Inmate(), w), StaffMemories.ScoreFor(control.Inmate(), w));
                if (ma != 1 || Mathf.Abs(ta - WardenAsks.WalkTrust) > 0.01f) AskFail(check, $"a wound-up asker met on the walk - {ma}, trust {ta} (1 and {WardenAsks.WalkTrust} expected)");
                if (mc != -1 || Mathf.Abs(tc - WardenSystem.WalkResentment) > 0.01f) AskFail(check, $"(control) a wound-up inmate met on the walk - {mc}, trust {tc} (-1 and {WardenSystem.WalkResentment} expected)");
                if (WardenAsks.Of(asker) != null) AskFail(check, "the request was not answered by the walk");
                // Met again on the same walk's next stop (a new job meets everyone again): the request is gone, a plain meeting now.
                if (WardenSystem.Meet(w, asker) != -1) AskFail(check, "an answered request was answered twice");
            });
        }

        // ---- the reasons' own -----------------------------------------------------------------------------------------------------------------------------

        private static void CheckAskWitness(Map map)
        {
            const string check = "the witness";
            Guarded("warden asks: the witness", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = AskIsolate(map);
                PrisonGame g = s.g;
                Pawn w = s.Warden();
                Pawn inPost = WardenSystem.InPost(map);
                if (inPost == null)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the witness skipped - no warden in post");
                    return;
                }
                bool? forced0 = Trials.ForcedReport;
                r.Add(() => Trials.ForcedReport = forced0);
                Pawn witness = s.Inmate(), killer = s.Inmate();
                killer.Inmate().security = witness.Inmate().security;
                var t = new MurderTrial { id = 990731, killerId = killer.thingIDNumber, killerName = killer.LabelShort, victimName = "Zvictim", assembled = true };
                var tw = new TrialWitness { id = witness.thingIDNumber, name = witness.LabelShort, known = true };
                t.witnesses.Add(tw);
                g.story.trials.Add(t);
                r.Add(() => g.story.trials.Remove(t));
                if (Trials.Pressurer(map, t, witness) != killer)
                {
                    RPLog.Message($"[RimPrison:test] warden asks: the witness skipped - the killer cannot get at the witness here (awake {killer.Awake()}, {Trials.Pressurer(map, t, witness)?.LabelShort ?? "nobody"})");
                    return;
                }
                // A witness of the same wing as the killer, with their own entry in the case.
                (Pawn p, TrialWitness e) Witness(bool known)
                {
                    Pawn wp = s.Inmate();
                    wp.Inmate().security = witness.Inmate().security;
                    var we = new TrialWitness { id = wp.thingIDNumber, name = wp.LabelShort, known = known };
                    t.witnesses.Add(we);
                    return (wp, we);
                }
                StaffMemories.Note(witness.Inmate(), inPost, 10f);

                // The report's dice fail: nothing comes out, and nobody asks either (the request is taken out of the report's odds, not out of the attempts it misses).
                Trials.ForcedReport = false;
                for (int i = 0; i < 5; i++)
                {
                    tw.pressure = 0f;
                    Trials.TryPressure(g, map, t, tw, witness, beating: false);
                }
                if (WardenAsks.Of(witness) != null || tw.pressureKnown) AskFail(check, $"a witness got at whose report's dice failed - request {WardenAsks.Describe(pm)}, threat known {tw.pressureKnown} (neither expected: the pressure stays hidden)");

                // The dice say they come forward, and they trust the warden: they ask to see the warden instead of going to the guards.
                Trials.ForcedReport = true;
                tw.pressure = 0f;
                Trials.TryPressure(g, map, t, tw, witness, beating: false);
                if (WardenAsks.Of(witness) is not { reason: WardenAskReason.Witness } asked || tw.pressureKnown)
                {
                    AskFail(check, $"a witness who trusts the warden came forward - request {WardenAsks.Describe(pm)}, threat known {tw.pressureKnown} (a witness's request, the threat not known yet, expected)");
                    return;
                }
                if (!WardenAsks.ReasonText(map, witness, witness.Inmate(), asked).Contains(killer.LabelShort)) AskFail(check, "the reason does not name the case");
                // The word: the threat comes out, told to the warden.
                w.GetComp<CompStaff>().wordReadyTick = -1;
                WardenSystem.Word(w, witness, WardenWord.Chat);
                if (!tw.pressureKnown || WardenAsks.Of(witness) != null) AskFail(check, $"a word with the witness who asked - threat known {tw.pressureKnown}, request left {WardenAsks.Of(witness) != null}");

                // Coming forward with no trust in the warden: the guards hear it, nobody asks.
                (Pawn cold, TrialWitness tc) = Witness(known: true);
                if (!Trials.TryPressure(g, map, t, tc, cold, beating: false)) RPLog.Message("[RimPrison:test] warden asks: the witness - the killer cannot get at a second witness here, the guards' report not checked");
                else if (!tc.pressureKnown || WardenAsks.Of(cold) != null) AskFail(check, $"a witness with no trust in the warden came forward - threat known {tc.pressureKnown}, request {WardenAsks.Of(cold) != null} (told the guards, no request, expected)");

                // A witness the warden does not know of never asks (the guards hear it, as before).
                (Pawn unknown, TrialWitness tu) = Witness(known: false);
                StaffMemories.Note(unknown.Inmate(), inPost, 10f);
                if (Trials.TryPressure(g, map, t, tu, unknown, beating: false) && WardenAsks.Of(unknown) != null) AskFail(check, "a witness the warden did not know of asked to see the warden");

                // A witness who hates a guard neither asks nor reports, whatever the dice.
                (Pawn hater, TrialWitness th) = Witness(known: true);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                StaffMemories.Note(hater.Inmate(), guard, -80f);
                StaffMemories.Note(hater.Inmate(), inPost, 10f);
                if (StaffMemories.MostHated(hater.Inmate()) == null)
                {
                    AskFail(check, $"premise: a witness given -80 toward a guard hates nobody (score {StaffMemories.ScoreFor(hater.Inmate(), guard)})");
                    return;
                }
                for (int i = 0; i < 3; i++) Trials.TryPressure(g, map, t, th, hater, beating: false);
                if (WardenAsks.Of(hater) != null || th.pressureKnown) AskFail(check, $"a witness who hates a guard - request {WardenAsks.Of(hater) != null}, threat known {th.pressureKnown} (neither expected)");
            });
        }

        private static void CheckAskDebtor(Map map)
        {
            const string check = "the debtor";
            Guarded("warden asks: the debtor", map, r =>
            {
                var s = new EconomyScene(map, r);
                AskIsolate(map);
                Pawn creditor = s.Inmate(fighter: true), debtor = s.Inmate();
                InmateDebt d = s.Overdue(creditor, debtor);
                (d.stage, d.lastBeatingTick) = (DebtStage.Threatened, -999999);
                creditor.Inmate().forcedActivity = null;
                s.g.story.lastArcStart.Remove(RPDebtDefOf.RP_Arc_Debt);
                bool beaten = DebtSystem.TryBeat(d, debtor, creditor, map, s.pm);
                creditor.mindState.mentalStateHandler.Reset();
                if (!beaten)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the debtor skipped - the beating did not happen here");
                    return;
                }
                if (WardenAsks.Of(debtor) is not { reason: WardenAskReason.Debtor } ask)
                {
                    AskFail(check, $"a debtor beaten over a debt did not ask to see the warden ({WardenAsks.Describe(s.pm)})");
                    return;
                }
                // A hidden debt to someone else, first in the list: the request is named and held by the known one.
                var hidden = new InmateDebt { id = 990733, creditorId = -2, creditorName = "Zhidden", debtorId = debtor.thingIDNumber, debtorName = debtor.LabelShort, amount = 5, original = 5, dueTick = s.Now + GenDate.TicksPerDay, known = false };
                s.g.story.debts.Insert(0, hidden);
                string reason = WardenAsks.ReasonText(map, debtor, debtor.Inmate(), ask);
                if (!reason.Contains(creditor.LabelShort) || reason.Contains(hidden.creditorName)) AskFail(check, $"with a hidden debt listed first the reason is \"{reason}\" (the known debt's creditor {creditor.LabelShort} expected)");
                // The known debt settled: the request is over, though the hidden one stands.
                s.g.story.debts.Remove(d);
                WardenAsks.Hourly(map, s.pm);
                if (WardenAsks.Of(debtor) != null) AskFail(check, "the hour kept the request of a debtor whose known debt is settled (only a hidden one stands)");
            });
        }

        private static void CheckAskDoubter(Map map)
        {
            const string check = "the doubter";
            Guarded("warden asks: the doubter", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = AskIsolate(map);
                Pawn w = s.Warden();
                GangDef gang = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens", errorOnFail: false) ?? DefDatabase<GangDef>.AllDefsListForReading.First();
                // A member of a gang the warden does not know: the goal forms, nobody is asked for (a request would say the gang and the goal exist). They wait for the chaplain.
                Pawn hidden = s.Inmate();
                CompInmate hc = hidden.Inmate();
                (hc.gang, hc.gangKnown, hc.gangLeader, hc.gangDoubtTick) = (gang, false, false, s.Now);
                if (GangExit.RollDoubt(hidden, hc, 1f) == null) RPLog.Message("[RimPrison:test] warden asks: the doubter - a hidden member's doubt did not become the goal to leave here, not checked");
                else if (WardenAsks.Of(hidden) is { } leak) AskFail(check, $"a member of a gang the warden does not know asked to see the warden about leaving it - \"{WardenAsks.ReasonText(map, hidden, hc, leak)}\"");
                // (Put there by hand all the same, the hour drops it.)
                WardenAsks.Raise(hidden, WardenAskReason.Doubter);
                WardenAsks.Hourly(map, pm);
                if (WardenAsks.Of(hidden) != null) AskFail(check, "the hour kept the request of a doubter whose gang the warden does not know");

                // A member of a gang the warden knows asks, and the reason names the gang.
                Pawn p = s.Inmate();
                CompInmate c = p.Inmate();
                (c.gang, c.gangKnown, c.gangLeader, c.gangDoubtTick) = (gang, true, false, s.Now);
                InmateWant want = GangExit.RollDoubt(p, c, 1f);
                if (want == null)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the doubter skipped - the doubt did not become the goal to leave here");
                    return;
                }
                if (WardenAsks.Of(p) is not { reason: WardenAskReason.Doubter } ask)
                {
                    AskFail(check, $"a member of a known gang whose doubt became the goal to leave did not ask to see the warden ({WardenAsks.Describe(pm)})");
                    return;
                }
                string reason = WardenAsks.ReasonText(map, p, c, ask);
                if (!reason.Contains(gang.label)) AskFail(check, $"the reason of a doubter of a known gang does not name it - \"{reason}\"");
                w.GetComp<CompStaff>().wordReadyTick = -1;
                WardenSystem.Word(w, p, WardenWord.Chat);
                if (want.step != 1 || WardenAsks.Of(p) != null) AskFail(check, $"a word with the doubter who asked - step {want.step} (1, heard out, expected), request left {WardenAsks.Of(p) != null}");
                // The request of one already heard out is over: the hour drops it.
                WardenAsks.Raise(p, WardenAskReason.Doubter);
                WardenAsks.Hourly(map, pm);
                if (WardenAsks.Of(p) != null) AskFail(check, "the hour kept the request of a doubter who has been heard out");
            });
        }

        private static void CheckAskStrike(Map map)
        {
            const string check = "the hunger strike";
            Guarded("warden asks: the hunger strike", map, r =>
            {
                var s = new WardenScene(map, r);
                AskIsolate(map);
                Pawn p = s.Inmate();
                // (What the strike's incident does for each striker.)
                HungerStrike.Join(p, s.Now + 2 * GenDate.TicksPerDay);
                if (!HungerStrike.Fasting(p.Inmate()) || WardenAsks.Of(p) is not { reason: WardenAskReason.HungerStrike }) AskFail(check, $"a striker - fasting {HungerStrike.Fasting(p.Inmate())}, request {WardenAsks.Of(p)?.Describe() ?? "none"} (fasting and a striker's request expected)");
            });
        }

        private static void CheckAskCellRequest(Map map)
        {
            const string check = "the cell request";
            Guarded("warden asks: the cell request", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "warden asks: the cell request", 2, 1);
                if (s == null) return;
                PrisonMap pm = AskIsolate(map);
                s.Grades(1, 1);
                Pawn a = s.Inmate(0, 0, 1), b = s.Inmate(0, 1, 1);
                PrisonRelations.MakeEnemies(a, b);
                if (CellRequests.Open())
                {
                    RPLog.Message("[RimPrison:test] warden asks: the cell request skipped - a cell request is already open in the prison");
                    return;
                }
                ChoiceLetter_PrisonDecision letter = CellRequests.TryRaise(a, a.Inmate(), only: b);
                if (letter == null)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the cell request skipped - no request letter for a cellmate they are feuding with here");
                    return;
                }
                if (WardenAsks.Of(a) is not { reason: WardenAskReason.CellRequest }) AskFail(check, $"an inmate who asked for another cell did not ask to see the warden ({WardenAsks.Describe(pm)})");
                if (WardenAsks.Of(b) != null) AskFail(check, "the cellmate a request is about asked to see the warden too");
                WardenAsks.Hourly(map, pm);
                if (WardenAsks.Of(a) == null) AskFail(check, "the hour dropped a cell request's request while its letter waits");
                // (The letter answered: the request is over.)
                letter.answered = true;
                WardenAsks.Hourly(map, pm);
                if (WardenAsks.Of(a) != null) AskFail(check, "the hour kept the request of a cell request whose letter is answered");
            });
        }

        private static void CheckAskRumour(Map map)
        {
            const string check = "the rumour";
            Guarded("warden asks: the rumour", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = AskIsolate(map);
                pm.rumours = new List<Rumour>();
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the rumour skipped - fewer than two levels a rumour can be told on");
                    return;
                }
                Pawn w = s.Warden();
                var cast = new List<Pawn>();
                SecurityLevelDef level = levels[0], other = levels[1];
                Pawn origin = RumourInmate(s, level, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), cast), mate = RumourInmate(s, level, origin.Position, cast);
                int real = RealOn(map, level, cast);
                Rumour rumour = Rumours.Start(pm, level, RumourKind.Supermax, isTrue: false, subject: null, origin: origin);
                List<WardenAsk> asked = pm.asks.Where(a => a.reason == WardenAskReason.Rumour).ToList();
                if (rumour == null || asked.Count != 1 || asked[0].inmateId == origin.thingIDNumber)
                {
                    AskFail(check, $"a false rumour on the wing had {asked.Count} ask ({WardenAsks.Describe(pm)}, origin {origin.thingIDNumber}; one, never the origin, expected)");
                    return;
                }
                Pawn asker = CourtUtil.FindInmate(map, asked[0].inmateId);
                if (asker != mate && real == 0) AskFail(check, $"the asker is {asker?.LabelShort ?? "nobody"}, not the only other inmate of the wing");
                if (!WardenAsks.ReasonText(map, asker, asker.Inmate(), asked[0]).Contains(Rumours.Text(rumour))) AskFail(check, "the reason does not say what the wing is saying");
                // Met on the walk: who started it, and that it is false.
                if (WardenSystem.Meet(w, asker) != 1 || !rumour.originKnown || !rumour.truthKnown || WardenAsks.Of(asker) != null)
                    AskFail(check, $"the asker met on the walk - origin known {rumour.originKnown}, truth known {rumour.truthKnown}, request left {WardenAsks.Of(asker) != null}");
                // A true rumour: nobody asks.
                Pawn o2 = RumourInmate(s, other, origin.Position, cast);
                RumourInmate(s, other, origin.Position, cast);
                int before = pm.asks.Count;
                Rumours.Start(pm, other, RumourKind.Supermax, isTrue: true, subject: null, origin: o2);
                if (pm.asks.Count != before) AskFail(check, $"a true rumour had someone ask ({WardenAsks.Describe(pm)})");
                // The rumour ended: the request goes at the hour.
                Rumour again = Rumours.Start(pm, level, RumourKind.Death, isTrue: false, subject: "Zsubject", origin: origin);
                Pawn second = pm.asks.Where(a => a.reason == WardenAskReason.Rumour).Select(a => CourtUtil.FindInmate(map, a.inmateId)).FirstOrDefault();
                if (again == null || second == null)
                {
                    AskFail(check, $"a second false rumour on the wing had nobody ask ({WardenAsks.Describe(pm)})");
                    return;
                }
                // The origin named already (an earlier walk): answering tells the truth, and the origin's line is in the chronicle once, not again.
                again.originKnown = true;
                string originLine = "RP_Chr_RumourOrigin".Translate(Rumours.WingName(level), Rumours.Text(again), again.originName ?? "?").Resolve();
                int originLines = s.g.story.entries.Count(e => e.text == originLine);
                if (WardenSystem.Meet(w, second) != 1 || !again.truthKnown) AskFail(check, $"the second asker met on the walk - truth known {again.truthKnown} (expected)");
                if (s.g.story.entries.Count(e => e.text == originLine) != originLines) AskFail(check, "answering the rumour's asker wrote the origin's line again when the origin was known");
                // (Asked again, the same tick: tied to the same rumour.)
                WardenAsks.Raise(second, WardenAskReason.Rumour);
                Rumours.End(again, RumourEnd.Calmed, s.Now);
                WardenAsks.Hourly(map, pm);
                if (WardenAsks.Of(second) != null) AskFail(check, "the hour kept a request about a rumour that has ended");
            });
        }

        /// <summary>A request about the talk is about the rumour it was made for: not a worse one that took its place (a true one here), nor another wing's after the asker was moved (the clock a tick forward between the two, back after).</summary>
        private static void CheckAskRumourTied(Map map)
        {
            const string check = "the rumour asked about";
            Guarded("warden asks: the rumour asked about", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonMap pm = AskIsolate(map);
                pm.rumours = new List<Rumour>();
                List<SecurityLevelDef> levels = RumourLevels(map);
                if (levels.Count < 2)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the rumour asked about skipped - fewer than two levels a rumour can be told on");
                    return;
                }
                Pawn w = s.Warden();
                var cast = new List<Pawn>();
                SecurityLevelDef level = levels[0], other = levels[1];
                Pawn origin = RumourInmate(s, level, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), cast), mate = RumourInmate(s, level, origin.Position, cast);
                Pawn otherOrigin = RumourInmate(s, other, origin.Position, cast);
                RumourInmate(s, other, origin.Position, cast);
                // The other wing's false rumour first; a tick later, this wing's, which the made-up mate asks about (as the mill's own asker would, the tick it starts).
                Rumour there = Rumours.Start(pm, other, RumourKind.Supermax, isTrue: false, subject: null, origin: otherOrigin);
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + 1);
                Rumour here = Rumours.Start(pm, level, RumourKind.Supermax, isTrue: false, subject: null, origin: origin);
                WardenAsks.Raise(mate, WardenAskReason.Rumour);
                CompInmate mc = mate.Inmate();
                if (there == null || here == null || WardenAsks.Of(mate) is not { reason: WardenAskReason.Rumour } ask)
                {
                    AskFail(check, $"premise: the two false rumours or the mate's request did not start ({there != null}, {here != null}, {WardenAsks.Describe(pm)})");
                    Find.TickManager.DebugSetTicksGame(clock0);
                    return;
                }
                if (WardenAsks.RumourOf(map, mc, ask) != here) AskFail(check, "a request about the talk is not tied to the rumour it was made for");

                // Moved to the other wing: the request is not about that wing's talk.
                mc.security = other;
                string moved = WardenAsks.ReasonText(map, mate, mc, ask);
                bool movedMoot = WardenAsks.Moot(map, mate, mc, ask);
                mc.security = level;
                if (moved.Contains(Rumours.Text(there)) || !movedMoot) AskFail(check, $"an asker moved to another wing - the reason \"{moved}\", over {movedMoot} (not that wing's talk, and over, expected)");

                // A worse rumour, a true one, takes the place of theirs a tick later: the request is not about it, and meeting them tells nothing of it.
                Find.TickManager.DebugSetTicksGame(clock0 + 2);
                Rumour truth = Rumours.Start(pm, level, RumourKind.Death, isTrue: true, subject: "Zsubject", origin: origin, distorted: true);
                if (truth == null || WardenAsks.Of(mate) != ask)
                {
                    RPLog.Message($"[RimPrison:test] warden asks: the rumour asked about - the true rumour did not take the place of the false one here ({truth != null}, {WardenAsks.Describe(pm)}), not checked");
                    Find.TickManager.DebugSetTicksGame(clock0);
                    return;
                }
                string replaced = WardenAsks.ReasonText(map, mate, mc, ask);
                if (replaced.Contains(Rumours.Text(truth))) AskFail(check, $"the request about a false rumour now describes the true one that replaced it - \"{replaced}\"");
                int answered0 = WardenAsks.Answered;
                WardenSystem.Meet(w, mate);
                if (WardenAsks.Answered != answered0 || truth.truthKnown) AskFail(check, $"meeting the asker of a replaced rumour - answered {WardenAsks.Answered != answered0}, the true one's truth told {truth.truthKnown} (neither expected)");
                if (WardenAsks.Of(mate) != null) AskFail(check, "the request about a replaced rumour was left after the walk met them");
                Find.TickManager.DebugSetTicksGame(clock0);
            });
        }

        private static void CheckAskStepDown(Map map)
        {
            const string check = "the step-down";
            Guarded("warden asks: the step-down", map, r =>
            {
                var s = new WardenScene(map, r);
                AskIsolate(map);
                if (SupermaxSystem.Level == null)
                {
                    RPLog.Message("[RimPrison:test] warden asks: the step-down skipped - no supermax level");
                    return;
                }
                Pawn p = s.Inmate();
                CompInmate c = p.Inmate();
                c.security = SupermaxSystem.Level;
                SupermaxSystem.AskStepDown(p, c, map);
                if (WardenAsks.Of(p) is not { reason: WardenAskReason.StepDown }) AskFail(check, "a step-down candidate put to the warden did not ask to see them");
                WardenAsks.Hourly(map, PrisonMap.Of(map));
                if (WardenAsks.Of(p) == null) AskFail(check, "the hour dropped a step-down candidate's request while they are in supermax and the letter waits");
                // The letter answered (declined, say): the request is over though they are still in supermax.
                ChoiceLetter_PrisonDecision letter = StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => !l.answered && l.Is(SupermaxSystem.StepDownDecisionDef) && l.pawns.Contains(p));
                if (letter == null) AskFail(check, "premise: no open step-down letter for the candidate");
                else
                {
                    letter.answered = true;
                    WardenAsks.Hourly(map, PrisonMap.Of(map));
                    if (WardenAsks.Of(p) != null) AskFail(check, "the hour kept the request of a step-down candidate whose letter is answered");
                    letter.answered = false;
                }
                WardenAsks.Raise(p, WardenAskReason.StepDown);
                c.security = InmateUtility.DefaultSecurity;
                WardenAsks.Hourly(map, PrisonMap.Of(map));
                if (WardenAsks.Of(p) != null) AskFail(check, "the hour kept the request of one who is out of supermax");
            });
        }

        private static void CheckAskGo(Map map)
        {
            const string check = "go";
            Guarded("warden asks: go", map, r =>
            {
                var s = new WardenScene(map, r);
                AskIsolate(map);
                Pawn p = s.Inmate();
                p.Inmate().hungerStrikeUntilTick = s.Now + GenDate.TicksPerDay;
                WardenAsks.Raise(p, WardenAskReason.HungerStrike);
                WardenAsk a = WardenAsks.Of(p);
                if (WardenAsks.GoUnready(null, a, p, null) == null) AskFail(check, "\"Go\" was on with no warden");
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                sc.wordReadyTick = -1;
                if (WardenAsks.GoUnready(w, a, p, null) is { } why) AskFail(check, $"\"Go\" was off for a ready warden - \"{why}\"");
                sc.wordReadyTick = s.Now + 2 * GenDate.TicksPerHour;
                if (WardenAsks.GoUnready(w, a, p, null) == null) AskFail(check, "\"Go\" was on while the word's cooldown runs");
                // A rumour's "Go" is the walk: the word's cooldown does not hold it back, the walk's does.
                a.reason = WardenAskReason.Rumour;
                sc.walkReadyTick = -1;
                List<PrisonZoneDef> wings = WardenSystem.Wings(map);
                string walkWhy = WardenAsks.GoUnready(w, a, p, wings);
                if (walkWhy != Rumours.WalkUnready(w, p.Inmate().security, wings)) AskFail(check, $"a rumour's \"Go\" said \"{walkWhy}\", not the walk's own");
                // And it sends the warden on the walk of the asker's wing.
                PrisonZoneDef zone = Rumours.WingZone(map, p.Inmate().security, wings);
                List<IntVec3> stops = zone == null ? new List<IntVec3>() : WardenSystem.WalkPlan(w, zone);
                if (walkWhy != null || stops.Count == 0) RPLog.Message($"[RimPrison:test] warden asks: go - a rumour's walk cannot be ordered here (\"{walkWhy}\", {stops.Count} stops), its order not checked");
                else
                {
                    s.Place(w, stops[0]);
                    WardenAsks.Go(w, a, p, wings);
                    if (w.CurJobDef != RPWardenDefOf.RP_WardenWalk || w.CurJob.count != zone.gridIndex) AskFail(check, $"a rumour's \"Go\" gave the warden {w.CurJobDef?.defName ?? "no job"} (count {w.CurJob?.count}), not the walk of wing {zone.gridIndex}");
                    w.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                }
                sc.walkReadyTick = s.Now + 2 * GenDate.TicksPerHour;
                if (WardenAsks.GoUnready(w, a, p, wings) == null) AskFail(check, "a rumour's \"Go\" was on while the walk's cooldown runs");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------------

        private static List<WardenAsk> asksBeforeSeed;
        private static string askSeed;

        /// <summary>
        /// Requests with every field off its default, for the round trip: from inmates of the wing the seeded rumour runs on (<see cref="SeedRumoursForReload"/>, which comes first), at the tick it started (a request is tied to
        /// its rumour by that), so the reason stands if an hour passes before the check (never before 1).
        /// </summary>
        private static void SeedWardenAsksForReload(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            List<Pawn> wing = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate()?.security != null).OrderBy(p => p.thingIDNumber).ToList();
            if (pm == null || wing.Count == 0)
            {
                problems0.Add("saveload: no inmate to seed a request to see the warden with");
                return;
            }
            SecurityLevelDef level = wing[0].Inmate().security;
            wing = wing.Where(p => p.Inmate().security == level).Take(2).ToList();
            asksBeforeSeed = pm.asks;
            int tick = Rumours.LiveOn(pm, level) is { isTrue: false } seeded ? seeded.startTick : Mathf.Max(1, Find.TickManager.TicksGame - 1234);
            pm.asks = wing.Select(p => new WardenAsk { inmateId = p.thingIDNumber, reason = WardenAskReason.Rumour, tick = tick }).ToList();
            askSeed = WardenAsks.Describe(pm);
            RPLog.Message($"[RimPrison:test] saveload asks: {askSeed}");
        }

        /// <summary>The seeded requests came back whole; then the map has its own again.</summary>
        private static void CheckWardenAsksAfterReload(Map map)
        {
            if (askSeed == null) return;
            PrisonMap pm = PrisonMap.Of(map);
            string after = WardenAsks.Describe(pm);
            RPLog.Message($"[RimPrison:test] saveload asks: {(after == askSeed ? "intact" : "changed")} ({after})");
            if (after != askSeed) reloadProblems.Add($"the requests to see the warden changed in the round trip: {askSeed} -> {after}");
            // (A request holds an id, a reason and a tick: the old game's are copied over as they were.)
            pm.asks = asksBeforeSeed?.Select(x => x.Copy()).ToList() ?? new List<WardenAsk>();
            asksBeforeSeed = null;
            askSeed = null;
        }
    }
}
