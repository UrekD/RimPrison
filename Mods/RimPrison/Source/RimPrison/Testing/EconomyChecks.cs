using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The inmate economy (events plan): a dealer sells on credit to a broke inmate and a debt appears, hidden; it is paid, part-paid
    /// or goes overdue and escalates (a threat, then a recorded fight with the debt as its cause, rate-limited, never in protective custody, written
    /// off after the third); the warden's three answers at the beating; the debtor's own (custody, a smuggled delivery, informing); a
    /// card game with a winner and losers who owe; protection bought, its effect, and its lapse; the warden learning of a debt only by a tip, a
    /// search or an event; a party leaving drops it. Works on throwaway inmates standing in the yard, and puts back everything it touches.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The seeded debts of the saveload plan (their ids, and what each read before the save), the arc's id and the counter to put back.</summary>
        private static readonly List<(int id, string described)> debtSeeds = new List<(int, string)>();
        private static int debtSeedArcId = -1, debtSeedNextId = -1, debtSeedFormed = -1;
        private static string debtSeedArc;
        /// <summary>What the seeding replaced or added on the two inmates: their goals as they were, and the economy memory given to one of them (read back after the reload).</summary>
        private static readonly List<(CompInmate c, InmateWant want, WantStatus status, int resolved)> debtSeedWants = new List<(CompInmate, InmateWant, WantStatus, int)>();
        private static int debtSeedMemoryPawn = -1;

        /// <summary>Two debts part-way (a beaten drug debt with a smuggled delivery arranged, a protection fee in its third week) and the arc over the first, so every field makes the round trip.</summary>
        private static void SeedDebtsForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            List<Pawn> pair = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null && !g.story.InArc(x)).Take(2).ToList();
            if (pair.Count < 2)
            {
                problems0.Add("saveload: no two inmates to seed a debt with");
                return;
            }
            int t = Find.TickManager.TicksGame;
            debtSeedNextId = g.story.nextDebtId;
            debtSeedFormed = g.stats.debtsFormed;
            foreach (Pawn x in pair) debtSeedWants.Add((x.Inmate(), x.Inmate().want, x.Inmate().want?.status ?? WantStatus.OnTrack, x.Inmate().want?.resolvedTick ?? -1));
            InmateDebt beaten = DebtSystem.Open(pair[0], pair[1], DebtReason.Drugs, 12, 3);
            (beaten.amount, beaten.dueTick, beaten.stage, beaten.nextTick, beaten.lastBeatingTick, beaten.beatings, beaten.known, beaten.smuggling) =
                (9, t - 500, DebtStage.Beaten, t + 5000, t - 1234, 1, true, true);
            InmateDebt fee = DebtSystem.Open(pair[1], pair[0], DebtReason.Protection, DebtSystem.ProtectionFee, 3, cycles: 2);
            (fee.dueTick, fee.informant, fee.stuckSince) = (t + 7000, true, t - 3333);
            // (A memory the warden knows of, which reads as what it is after the reload too.)
            EconomyThoughts.Gain(pair[1], RPDebtDefOf.RP_DebtThreatened, pair[0], shown: true);
            debtSeedMemoryPawn = pair[1].thingIDNumber;
            debtSeeds.Add((beaten.id, beaten.Describe()));
            debtSeeds.Add((fee.id, fee.Describe()));
            StoryArc arc = g.story.TryStartArc(RPDebtDefOf.RP_Arc_Debt, pair[1], pair[0]);
            if (arc != null)
            {
                (arc.count, arc.note, arc.stage, arc.nextTick) = (beaten.id, "seeded", 1, t + 4321);
                debtSeedArcId = arc.id;
                debtSeedArc = $"{arc.count}:{arc.note}:{arc.stage}:{arc.nextTick}:{arc.A?.thingIDNumber}+{arc.B?.thingIDNumber}";
            }
            else problems0.Add("saveload: the debt arc could not be seeded");
            RPLog.Message($"[RimPrison:test] saveload debts: {beaten.Describe()} | {fee.Describe()} | arc {debtSeedArc}");
        }

        /// <summary>After the reload: the seeded debts and arc came back whole; then the map is as it was.</summary>
        private static void CheckDebtsAfterReload(Map map)
        {
            if (debtSeeds.Count == 0) return;
            PrisonGame g = PrisonGame.Instance;
            string Read(int id) => g.story.debts.FirstOrDefault(d => d.id == id)?.Describe() ?? "(gone)";
            bool whole = debtSeeds.All(s => Read(s.id) == s.described);
            StoryArc arc = g.story.arcs.FirstOrDefault(a => a.id == debtSeedArcId && a.def == RPDebtDefOf.RP_Arc_Debt);
            string arcAfter = arc == null ? "(gone)" : $"{arc.count}:{arc.note}:{arc.stage}:{arc.nextTick}:{arc.A?.thingIDNumber}+{arc.B?.thingIDNumber}";
            bool arcWhole = debtSeedArcId < 0 || arcAfter == debtSeedArc;
            RPLog.Message($"[RimPrison:test] saveload debts: {(whole && arcWhole ? "intact" : "changed")} ({string.Join(" | ", debtSeeds.Select(s => Read(s.id)))}; arc {arcAfter})");
            if (!whole || !arcWhole)
                reloadProblems.Add($"debts after reload: {string.Join(" | ", debtSeeds.Select(s => s.described + " -> " + Read(s.id)))}; arc {debtSeedArc} -> {arcAfter}");
            // The memory kept what it read as (shown), and goes again.
            Pawn remembering = map.mapPawns.PrisonersOfColony.FirstOrDefault(x => x.thingIDNumber == debtSeedMemoryPawn);
            Thought_Memory memory = remembering?.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPDebtDefOf.RP_DebtThreatened);
            bool memoryShown = memory is IEconomyThought { Shown: true } && memory.LabelCap == EconomyThoughts.Label(memory);
            RPLog.Message($"[RimPrison:test] saveload debt memory: {(memoryShown ? "intact" : "changed")} ({memory?.GetType().Name}, {memory?.LabelCap})");
            if (!memoryShown) reloadProblems.Add($"the economy memory the warden knew of did not read as what it is after the reload ({memory?.GetType().Name}, '{memory?.LabelCap}')");
            if (memory != null) remembering.needs.mood.thoughts.memories.RemoveMemory(memory);
            g.story.debts.RemoveAll(d => debtSeeds.Any(s => s.id == d.id));
            if (arc != null) g.story.arcs.Remove(arc);
            g.story.lastArcStart.Remove(RPDebtDefOf.RP_Arc_Debt);
            g.story.nextDebtId = debtSeedNextId;
            g.stats.debtsFormed = debtSeedFormed;
            // (The goal to clear the first debt took the place of whatever they had: it is back.)
            foreach ((CompInmate c, InmateWant want, WantStatus status, int resolved) in debtSeedWants)
            {
                CompInmate now = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).FirstOrDefault(x => x != null && x.parent.thingIDNumber == c.parent.thingIDNumber);
                if (now == null) continue;
                now.want = want;
                if (want != null) (want.status, want.resolvedTick) = (status, resolved);
            }
            debtSeedWants.Clear();
            debtSeeds.Clear();
            debtSeedArcId = -1;
        }

        private static void EconomyTest(Map map)
        {
            Guarded("economy", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                int now() => Find.TickManager.TicksGame;
                SecurityLevelDef sec = InmateUtility.DefaultSecurity, pcDef = DefDatabase<SecurityLevelDef>.GetNamedSilentFail("RP_Protective");
                ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
                ThingDef drugDef = ContrabandUtility.AllContraband.FirstOrDefault(d => ContrabandUtility.Ext(d).kind == ContrabandKind.Drug);
                GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
                List<IntVec3> allYard = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => c.Standable(map)).ToList();
                // (The yard cells nearest its first one: everyone stands within a few steps of everyone.)
                List<IntVec3> yardCells = allYard.Count == 0 ? allYard : allYard.OrderBy(c => c.DistanceToSquared(allYard[0])).Take(40).ToList();
                List<IntVec3> roomCells = pm.zones.CellsOf(RPDefOf.RP_CommonRoom).Where(c => c.Standable(map) && pm.zones.PatchAt(c) >= 0).Take(12).ToList();
                if (drugDef == null || yardCells.Count < 4 || roomCells.Count < 4)
                {
                    problems0.Add($"economy test: nothing to test with (drug {drugDef?.defName}, {yardCells.Count} yard cells, {roomCells.Count} common-room cells in a patch)");
                    return;
                }

                // (Last of all, once the real inmates are active again: the doors' verdicts and the cells are worked out again for them.)
                r.Add(() =>
                {
                    Cellmates.InvalidateCells();
                    pm.ForceDoorUpdate();
                });
                // What is put back: the real inmates' gang, active flag, cash and misconduct records (they are made nobody's and idle, so that nobody real sells, buys,
                // plays or is offered protection), the debts and the debt arcs.
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).ToList();
                var state0 = real.Select(c => (c, c.gang, c.gangKnown, c.activeFlag, c.cash, c.records.Count, c.fear, c.want, c.want?.status ?? WantStatus.OnTrack, c.want?.resolvedTick ?? -1)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, GangDef gang, bool known, bool active, int cash, int records, float fear, InmateWant want, WantStatus status, int resolved) in state0)
                    {
                        (c.gang, c.gangKnown, c.activeFlag, c.cash, c.fear) = (gang, known, active, cash, fear);
                        if (c.records.Count > records) c.records.RemoveRange(records, c.records.Count - records);
                        // (A goal that a debt's goal replaced was put aside: it is back as it was.)
                        c.want = want;
                        if (want != null) (want.status, want.resolvedTick) = (status, resolved);
                    }
                });
                foreach (CompInmate c in real) (c.gang, c.gangKnown, c.activeFlag) = (null, false, false);
                List<InmateDebt> debts0 = g.story.debts;
                int nextDebt0 = g.story.nextDebtId;
                List<StoryArc> arcs0 = g.story.arcs;
                var lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                int nextArc0 = g.story.nextArcId;
                r.Add(() =>
                {
                    g.story.debts = debts0;
                    g.story.nextDebtId = nextDebt0;
                    g.story.arcs = arcs0;
                    g.story.lastArcStart = lastStart0;
                    g.story.nextArcId = nextArc0;
                });
                g.story.debts = new List<InmateDebt>();
                g.story.arcs = arcs0.Where(a => a.def != RPDebtDefOf.RP_Arc_Debt).ToList();

                int spot = 0;
                var made = new List<Pawn>();
                void Place(Pawn p, IntVec3 cell)
                {
                    p.pather.StopDead();
                    p.Position = cell;
                    p.Notify_Teleported();
                }
                Pawn Mk(bool fighter = false)
                {
                    Pawn p = Throwaway(map, r);
                    // (Somebody who can throw a punch: a random backstory can rule it out.)
                    for (int tries = 0; fighter && p.WorkTagIsDisabled(WorkTags.Violent) && tries < 30; tries++)
                    {
                        if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                        p = Throwaway(map, r);
                    }
                    CompInmate c = p.Inmate();
                    (c.activeFlag, c.gang, c.gangKnown, c.cash, c.tension, c.fear, c.want) = (true, null, false, 0, 20f, 0f, null);
                    // (Nor a gang leader without a gang, which a generated inmate in a gang may be: a leader never turns informant, and a gang's protection is offered by them first.)
                    (c.gangLeader, c.snitchExposed) = (false, false);
                    // (A random inmate arrives with reputations of their own: a dealer is nobody's buyer and an extortionist nobody's victim, a vulnerable one is more afraid.)
                    c.reputations.Clear();
                    // (Admitted long ago: not a newcomer, whom a gang offers protection however calm they are.)
                    c.admittedTick = now() - 10 * GenDate.TicksPerDay;
                    Place(p, yardCells[spot++ % yardCells.Count]);
                    made.Add(p);
                    return p;
                }
                string Line(string key, params NamedArgument[] args) => key.Translate(args).Resolve();
                int Lines(string text) => g.story.entries.Count(e => e.text == text);
                // A line that names both of them is a debt's.
                int Naming(Pawn a, Pawn b) => r.NewEntries.Count(e => e.text.Contains(a.LabelShort) && e.text.Contains(b.LabelShort));
                bool Memory(Pawn p, ThoughtDef def) => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(def) != null;
                void Calm(Pawn p) => p.mindState.mentalStateHandler.Reset();
                InmateDebt Owed(Pawn debtor) => g.story.debts.FirstOrDefault(d => d.debtorId == debtor.thingIDNumber);
                // An overdue debt, threatened by the daily pass and then beaten by the hourly one: its pieces.
                InmateDebt Overdue(Pawn creditor, Pawn debtor, int amount = DebtSystem.ProtectionFee, DebtReason reason = DebtReason.Drugs)
                {
                    InmateDebt d = DebtSystem.Open(creditor, debtor, reason, amount, 1);
                    d.dueTick = now() - 10;
                    return d;
                }
                bool Beat(InmateDebt d, Pawn creditor, Pawn debtor, out StoryArc arc, out ChoiceLetter_PrisonDecision letter)
                {
                    DebtSystem.Daily(map);
                    d.nextTick = now() - 13 * GenDate.TicksPerHour;
                    // (The arc's own spacing is for hours; every scene here is in the same tick.)
                    g.story.lastArcStart.Remove(RPDebtDefOf.RP_Arc_Debt);
                    DebtSystem.Hourly(map, g, cards: false);
                    Calm(creditor);
                    StoryArc started = g.story.arcs.FirstOrDefault(a => !a.done && a.def == RPDebtDefOf.RP_Arc_Debt && a.count == d.id);
                    arc = started;
                    letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => started != null && l.arcId == started.id);
                    return d.stage == DebtStage.Beaten;
                }
                string log = "";

                // ---- credit: a dealer sells on credit to a broke inmate, and a debt appears (hidden) -----------------------------------------
                Pawn dealer = Mk(fighter: true), buyer = Mk();
                CompInmate dealerC = dealer.Inmate(), buyerC = buyer.Inmate();
                dealerC.reputations.Add(dealerRep);
                var dealing = (ReputationBehavior_Dealer)dealerRep.Behavior;
                ContrabandUtility.Give(dealer, drugDef);
                buyerC.tension = 50f;
                bool sold = dealing.Deal(dealer, dealerC, pm);
                InmateDebt credit = Owed(buyer);
                bool credited = sold && credit != null && credit.creditorId == dealer.thingIDNumber && credit.reason == DebtReason.Drugs && credit.amount == ReputationBehavior_Dealer.Price * DebtSystem.CreditMarkup
                                && credit.dueTick == now() + DebtSystem.CreditDays * GenDate.TicksPerDay && credit.stage == DebtStage.Current && !credit.known
                                && buyerC.cash == 0 && dealerC.cash == 0 && buyer.inventory.innerContainer.Any(t => t.def == drugDef) && buyerC.lastDealerId == dealer.thingIDNumber
                                && g.story.debts.Count == 1 && buyerC.want is { secret: true } w0 && w0.def == RPWantDefOf.RP_Want_Debt && w0.count == credit.id;
                if (!credited) problems0.Add($"economy test: a dealer's credit sale to an inmate with no cash did not leave a hidden debt of {ReputationBehavior_Dealer.Price * DebtSystem.CreditMarkup} with a secret goal to clear it (sold {sold}, debt {credit?.amount}/{credit?.reason}/{credit?.stage}/known {credit?.known}, cash {buyerC.cash}/{dealerC.cash})");
                // Someone who owes nothing yet and has the cash pays it; nobody owing more is lent to.
                ContrabandUtility.Give(dealer, drugDef);
                bool second = dealing.Deal(dealer, dealerC, pm);
                Pawn payer = Mk();
                payer.Inmate().cash = ReputationBehavior_Dealer.Price;
                payer.Inmate().tension = 50f;
                bool cashSale = dealing.Deal(dealer, dealerC, pm) && payer.Inmate().cash == 0 && dealerC.cash == ReputationBehavior_Dealer.Price && Owed(payer) == null && g.story.debts.Count == 1;
                if (second || !cashSale) problems0.Add($"economy test: a dealer lent to someone who already owed ({second}) or the cash sale did not pay in cash and leave no debt ({cashSale})");
                dealerC.cash = 0;
                // Hidden: no line names the two, the tension cause does not say it, the file shows nothing and the goal is secret.
                buyerC.mainCause = "RP_Cause_Debt";
                bool hiddenCause = TensionUtility.ShownCause(buyerC) == "RP_Cause_WingTrouble" && DebtSystem.Known().Count == 0 && !DebtSystem.KnownAbout(buyerC);
                if (!hiddenCause || Naming(dealer, buyer) != 0) problems0.Add($"economy test: a debt nobody told the warden of was shown (cause {TensionUtility.ShownCause(buyerC)}, known {DebtSystem.Known().Count}, lines naming both {Naming(dealer, buyer)})");
                log += $"credit={credited}";

                // ---- the daily pass: a part payment, then an overdue debt is threatened -----------------------------------------------------------
                buyerC.tension = 20f;
                buyerC.fear = 0f;
                buyerC.cash = 5;
                credit.dueTick = now() - 10;
                DebtSystem.Daily(map);
                bool part = credit.amount == ReputationBehavior_Dealer.Price * DebtSystem.CreditMarkup - 5 && dealerC.cash == 5 && buyerC.cash == 0;
                bool threatened = credit.stage == DebtStage.Threatened && buyerC.tension >= 20f + DebtSystem.ThreatTension && buyerC.fear >= DebtSystem.ThreatFear && buyerC.mainCause == "RP_Cause_Debt"
                                  && Memory(buyer, RPDebtDefOf.RP_DebtThreatened) && credit.nextTick == now() + DebtSystem.BeatingDelayDays * GenDate.TicksPerDay;
                if (!part || !threatened) problems0.Add($"economy test: an overdue debt was not part-paid ({part}: owes {credit.amount}, creditor holds {dealerC.cash}) and threatened ({threatened}: stage {credit.stage}, tension {buyerC.tension}, fear {buyerC.fear})");
                if (Naming(dealer, buyer) != 0 || credit.known) problems0.Add("economy test: the warden was told of a threat over a debt they had not heard of");
                log += $" threat={threatened && part}";

                // ---- the beating: a recorded fight with the debt as cause, rate-limited, never in protective custody, written off after the third ------------
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Place(guard, yardCells[2]);
                credit.nextTick = now() - 13 * GenDate.TicksPerHour;
                SecurityLevelDef buyerSec = buyerC.security;
                bool custodyLeftAlone = true;
                if (pcDef != null)
                {
                    buyerC.security = pcDef;
                    DebtSystem.Hourly(map, g, cards: false);
                    custodyLeftAlone = credit.stage == DebtStage.Threatened && credit.beatings == 0;
                    buyerC.security = buyerSec;
                }
                else problems0.Add("economy test: no protective custody level to check a beating is dropped for");
                int beatings0 = g.stats.debtBeatings;
                DebtSystem.Hourly(map, g, cards: false);
                string beatingLine = Line("RP_Chr_DebtBeating", dealer.LabelShort, buyer.LabelShort, Money.Format(credit.amount), DebtSystem.ReasonText(credit));
                bool beaten = credit.stage == DebtStage.Beaten && credit.beatings == 1 && credit.known && g.stats.debtBeatings == beatings0 + 1 && dealer.MentalStateDef == RPMentalStateDefOf.RP_AttackingInmate
                              && dealerC.records.Any(rec => rec.def.defName == "RP_Fight" && rec.cause == "RP_Cause_Debt" && rec.seen) && Lines(beatingLine) == 1
                              && PrisonRelations.Enemies(buyer, dealer) && Memory(buyer, RPDebtDefOf.RP_BeatenForDebt) && StaffMemories.ScoreFor(buyerC, guard) == StaffMemories.BeatenInSight;
                if (!custodyLeftAlone || !beaten) problems0.Add($"economy test: the beating (stage {credit.stage}, beatings {credit.beatings}, known {credit.known}, state {dealer.MentalStateDef?.defName}, fight with the debt as cause {dealerC.records.Any(rec => rec.def.defName == "RP_Fight" && rec.cause == "RP_Cause_Debt")}, lines {Lines(beatingLine)}, enemies {PrisonRelations.Enemies(buyer, dealer)}) or leaving a debtor in protective custody alone ({custodyLeftAlone}) went wrong");
                // The warden knows now: the cause shows, the file chip and the intel line are there, and the goal was set back and is tried again (not a grudge yet).
                bool shownNow = TensionUtility.ShownCause(buyerC) == "RP_Cause_Debt" && DebtSystem.Known().Count == 1 && DebtSystem.KnownAbout(buyerC)
                                && buyerC.want is { def: var wd, Resolved: false, renewals: 1 } && wd == RPWantDefOf.RP_Want_Debt;
                if (!shownNow) problems0.Add($"economy test: after the beating the debt was not shown (cause {TensionUtility.ShownCause(buyerC)}, known {DebtSystem.Known().Count}) or the debtor's goal was not the debt goal set back once ({buyerC.want?.def?.defName}/{buyerC.want?.renewals})");
                // Not again within three days (even overdue), not in custody, and the third round ends it.
                credit.stage = DebtStage.Threatened;
                credit.nextTick = now() - 13 * GenDate.TicksPerHour;
                DebtSystem.Hourly(map, g, cards: false);
                Calm(dealer);
                bool limited = credit.beatings == 1 && credit.stage == DebtStage.Threatened;
                credit.lastBeatingTick = now() - (DebtSystem.RepeatDays + 1) * GenDate.TicksPerDay;
                DebtSystem.Hourly(map, g, cards: false);
                Calm(dealer);
                bool again = credit.beatings == 2;
                Calm(dealer);
                credit.stage = DebtStage.Threatened;
                credit.nextTick = now() - 13 * GenDate.TicksPerHour;
                credit.beatings = DebtSystem.MaxBeatings;
                string writtenOff = Line("RP_Chr_DebtWrittenOff", buyer.LabelShort, dealer.LabelShort, Money.Format(credit.amount));
                DebtSystem.Hourly(map, g, cards: false);
                bool ended = !g.story.debts.Contains(credit) && Lines(writtenOff) == 1;
                if (!limited || !again || !ended) problems0.Add($"economy test: beatings were not rate-limited per debt ({limited}), did not come again after the cooldown ({again}) or the creditor did not write the debt off after the third ({ended})");
                log += $" beating={beaten && limited && again && ended}";

                // ---- the warden's three answers to a beating ---------------------------------------------------------------------------------------
                // (Pay it off: from the debtor's own account, which must hold it.)
                Pawn payCred = Mk(fighter: true), payDebt = Mk();
                InmateDebt payD = Overdue(payCred, payDebt, 14);
                bool pb = Beat(payD, payCred, payDebt, out StoryArc payArc, out ChoiceLetter_PrisonDecision payLetter);
                if (!pb || payArc == null || payLetter == null)
                    problems0.Add($"economy test: a beating did not start the debt arc with its letter (beaten {pb}, arc {payArc != null}, letter {payLetter != null})");
                else
                {
                    payDebt.Inmate().cash = 5;
                    int payOption = LetterOption(payLetter, "RP_Debt_Pay"), payRunOption = LetterOption(payLetter, "RP_Debt_LetRun");
                    string cannot = payLetter.decision.Worker.Disabled(payLetter, payOption);
                    payDebt.Inmate().cash = 17;
                    int paid0 = g.stats.debtsPaid;
                    PrisonDecisions.Choose(payLetter, payOption);
                    bool payOk = cannot != null && payDebt.Inmate().cash == 3 && payCred.Inmate().cash == 14 && !g.story.debts.Contains(payD) && payArc.done && g.stats.debtsPaid == paid0 + 1
                                 && Memory(payDebt, RPDebtDefOf.RP_DebtSettled) && payLetter.decision.Worker.TimeoutOption(payLetter) == payRunOption;
                    if (!payOk) problems0.Add($"economy test: paying a beaten debtor's debt from their account did not move the cash and end the arc (refused when short: {cannot != null}; cash {payDebt.Inmate().cash}/{payCred.Inmate().cash}, debt gone {!g.story.debts.Contains(payD)}, arc done {payArc.done})");
                    log += $" paid={payOk}";
                }
                // (Let it run: nothing is done, the debtor remembers, the debt stays.)
                Pawn runCred = Mk(fighter: true), runDebt = Mk();
                InmateDebt runD = Overdue(runCred, runDebt, 14);
                if (!Beat(runD, runCred, runDebt, out StoryArc runArc, out ChoiceLetter_PrisonDecision runLetter) || runLetter == null) problems0.Add("economy test: the second beating did not start an arc with a letter");
                else
                {
                    PrisonDecisions.Choose(runLetter, LetterOption(runLetter, "RP_Debt_LetRun"));
                    if (!runArc.done || !g.story.debts.Contains(runD) || !Memory(runDebt, RPDebtDefOf.RP_DebtLeftToIt) || runD.amount != 14)
                        problems0.Add($"economy test: letting a debt run ended wrong (arc done {runArc.done}, debt kept {g.story.debts.Contains(runD)}, memory {Memory(runDebt, RPDebtDefOf.RP_DebtLeftToIt)})");
                }
                // (Move the debtor: a free bed away from the creditor, in a spare bed spawned in a real cell.)
                Pawn moveCred = Mk(fighter: true), moveDebt = Mk();
                InmateDebt moveD = Overdue(moveCred, moveDebt, 14);
                Building_Bed spare = null;
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamed("Bed");
                foreach (Pawn owner in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.ownership?.OwnedBed is { Spawned: true } && x.Inmate()?.security != null && !x.Dead))
                {
                    Room cell = owner.ownership.OwnedBed.GetRoom();
                    // (Not the cell of either of them: the answer never puts the debtor with the creditor, nor back where they are.)
                    if (cell == null || cell == Cellmates.Home(moveDebt, moveDebt.Inmate()) || cell == Cellmates.Home(moveCred, moveCred.Inmate())) continue;
                    IntVec3 free = cell.Cells.FirstOrDefault(x => GenAdj.OccupiedRect(x, Rot4.North, bedDef.size).Cells.All(y => y.InBounds(map) && y.Standable(map) && y.GetEdifice(map) == null
                        && y.GetRoom(map) == cell && y.GetFirstItem(map) == null && y.GetFirstPawn(map) == null));
                    if (!free.IsValid || free == default) continue;
                    spare = (Building_Bed)ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
                    spare.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(spare, free, map, Rot4.North);
                    spare.ForPrisoners = true;
                    moveDebt.Inmate().security = IntakeSystem.WingOf(spare) ?? sec;
                    break;
                }
                if (spare == null) problems0.Add("economy test: could not run the move answer (no room for a spare bed in any cell)");
                else
                {
                    r.Add(() =>
                    {
                        moveDebt.ownership?.UnclaimBed();
                        if (!spare.Destroyed) spare.Destroy(DestroyMode.Vanish);
                        Cellmates.InvalidateCells();
                        pm.ForceDoorUpdate();
                    });
                    pm.ForceDoorUpdate();
                    if (!Beat(moveD, moveCred, moveDebt, out StoryArc moveArc, out ChoiceLetter_PrisonDecision moveLetter) || moveLetter == null) problems0.Add("economy test: the third beating did not start an arc with a letter");
                    else
                    {
                        string noBed = moveLetter.decision.Worker.Disabled(moveLetter, 0);
                        string moveLine = Line("RP_Chr_DebtMoved", moveDebt.LabelShort, moveCred.LabelShort);
                        // (Any free bed away from the creditor will do: other beds freed by earlier checks may fit them better than the spare one.)
                        List<Building_Bed> freeBefore = IntakeSystem.FreePrisonerBeds(map, moveDebt.Inmate().security);
                        Room creditorCell = Cellmates.Home(moveCred, moveCred.Inmate()), debtorCell = Cellmates.Home(moveDebt, moveDebt.Inmate());
                        PrisonDecisions.Choose(moveLetter, LetterOption(moveLetter, "RP_Debt_Move"));
                        Building_Bed movedTo = moveDebt.ownership.OwnedBed;
                        bool moved = noBed == null && freeBefore.Contains(spare) && movedTo != null && freeBefore.Contains(movedTo) && movedTo.GetRoom() != creditorCell && movedTo.GetRoom() != debtorCell && moveD.stage == DebtStage.Answered && moveD.nextTick >= now() + (DebtSystem.RepeatDays - 1) * GenDate.TicksPerDay
                                     && moveArc.done && g.story.debts.Contains(moveD) && Lines(moveLine) == 1;
                        if (!moved) problems0.Add($"economy test: moving the debtor did not put them in a free bed away ({movedTo?.thingIDNumber} of {freeBefore.Count} free, the spare is {spare.thingIDNumber}; refused: {noBed}), give them days, end the arc and say so (stage {moveD.stage}, arc done {moveArc.done})");
                        log += $" moved={moved}";
                    }
                }

                // ---- the debtor's own answers -----------------------------------------------------------------------------------------------------
                // Protective custody: they ask (and say why), and in custody nobody threatens or beats them.
                Pawn pcCred = Mk(fighter: true), pcDebt = Mk();
                InmateDebt pcD = Overdue(pcCred, pcDebt);
                pcD.stage = DebtStage.Beaten;
                pcDebt.Inmate().fear = 60f;
                DebtResponse? custody = DebtSystem.Respond(pcD, pcDebt, pcCred, map, DebtResponse.Custody);
                ChoiceLetter_PrisonDecision pcLetter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_ProtectiveCustody") && l.pawns.Contains(pcDebt));
                bool asked = custody == DebtResponse.Custody && pcLetter != null && pcLetter.Text.ToString().Contains(pcCred.LabelShort) && pcD.known && pcD.stage == DebtStage.Answered;
                if (!asked) problems0.Add($"economy test: a beaten debtor did not ask for protective custody and say why (answer {custody}, letter {pcLetter != null}, known {pcD.known})");
                if (pcLetter != null)
                {
                    // (The events prison has no protective wing to move them to: in custody they are, for what follows.)
                    pcDebt.Inmate().security = pcDef;
                    pcD.nextTick = now() - 1;
                    pcD.stage = DebtStage.Threatened;
                    DebtSystem.Daily(map);
                    DebtSystem.Hourly(map, g, cards: false);
                    bool safe = InCustodyNow(pcDebt) && pcD.beatings == 0 && pcD.stage == DebtStage.Threatened && g.story.debts.Contains(pcD);
                    if (!safe) problems0.Add($"economy test: a debtor accepted into protective custody was beaten or lost the debt (in custody {InCustodyNow(pcDebt)}, beatings {pcD.beatings}, stage {pcD.stage})");
                    log += $" custody={asked && safe}";
                }
                // A smuggled delivery: arranged now, the next one that reaches them goes to the creditor and the debt is paid.
                Pawn smugCred = Mk(), smugDebt = Mk();
                InmateDebt smugD = Overdue(smugCred, smugDebt);
                smugDebt.Inmate().familyCount = 2;
                float mailBefore = MailSystem.ContrabandChance(smugDebt.Inmate());
                DebtResponse? smuggle = DebtSystem.Respond(smugD, smugDebt, smugCred, map, DebtResponse.Smuggle);
                bool arranged = smuggle == DebtResponse.Smuggle && smugDebt.Inmate().smuggleArranged && smugD.smuggling && DebtSystem.Smuggling(smugDebt.Inmate())
                                && MailSystem.ContrabandChance(smugDebt.Inmate()) > mailBefore && VisitationSystem.SmuggleChance(smugDebt, smugDebt.Inmate()) >= 0.75f;
                int paidBefore = g.stats.debtsPaid;
                ContrabandUtility.Give(smugDebt, drugDef, forDebt: true);
                bool inKind = !g.story.debts.Contains(smugD) && g.stats.debtsPaid == paidBefore + 1 && smugCred.inventory.innerContainer.Any(t => t.def == drugDef)
                              && !smugDebt.inventory.innerContainer.Any(t => t.def == drugDef);
                if (!arranged || !inKind) problems0.Add($"economy test: arranging a smuggled delivery to pay a debt (arranged {arranged}) did not hand it to the creditor and settle the debt (settled {inKind})");
                log += $" smuggled={arranged && inKind}";
                // Informing: they turn informant (hidden), and their tip names the debt, which is then known (with its line).
                Pawn infCred = Mk(), infDebt = Mk();
                InmateDebt infD = Overdue(infCred, infDebt);
                DebtResponse? inform = DebtSystem.Respond(infD, infDebt, infCred, map, DebtResponse.Informant);
                CompInmate infC = infDebt.Inmate();
                bool turned = inform == DebtResponse.Informant && infC.HasReputation(ReputationDefOf.RP_Snitch) && !infC.KnownReputation(ReputationDefOf.RP_Snitch) && infD.informant && !infD.known;
                string tip = null;
                // (A tip names a corrupt guard in range before anything else, and 4 times in 100 lies about a clean one: with the staff out of it, the tip is about who owes whom.)
                List<CompStaff> onTheJob = map.mapPawns.FreeColonistsSpawned.Select(x => x.GetComp<CompStaff>()).Where(sc => sc != null && !sc.doubleAgent).ToList();
                foreach (CompStaff sc in onTheJob) sc.doubleAgent = true;
                // (And a tunnel, contraband or a gang nearby comes before a debt: nobody near has anything but the debt to tell of.)
                var others = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(oc => oc != null && oc != infC).Select(oc => (oc, oc.tunnelKnown, oc.suspected)).ToList();
                foreach ((CompInmate oc, bool _, bool _) in others) (oc.tunnelKnown, oc.suspected) = (true, true);
                // (Nor a smuggling ring running in the background, whose tip would come first.)
                List<StoryArc> ringsAside = RingSystem.Rings().Where(a => !a.done).ToList();
                foreach (StoryArc ring in ringsAside) ring.done = true;
                try
                {
                    for (int i = 0; i < 20 && tip == null; i++) tip = InformantSystem.TryTip(infDebt, infC);
                }
                finally
                {
                    foreach (CompStaff sc in onTheJob) sc.doubleAgent = false;
                    foreach ((CompInmate oc, bool known, bool suspected) in others) (oc.tunnelKnown, oc.suspected) = (known, suspected);
                    foreach (StoryArc ring in ringsAside) ring.done = false;
                }
                string knownLine = Line("RP_Chr_DebtKnown", infDebt.LabelShort, infCred.LabelShort, Money.Format(infD.amount), DebtSystem.ReasonText(infD));
                // (The tip is compared as the game turns it into a string, the chronicle's line as it is written: with its colour tags.)
                string expectedTip = "RP_TipDebt".Translate(infDebt.LabelShort, infCred.LabelShort, Money.Format(infD.amount), DebtSystem.ReasonText(infD));
                bool told = tip == expectedTip && infD.known && Lines(knownLine) == 1
                            && infC.want is not { secret: true };
                if (!turned || !told) problems0.Add($"economy test: a debtor turning informant (hidden {turned}) did not name the debt in a tip that made it known ({told}: tip '{tip}', known {infD.known}, line {Lines(knownLine)}, goal secret {infC.want?.secret})");
                log += $" informant={turned && told}";

                // ---- how the warden learns of one: a search (and not otherwise) ------------------------------------------------------------------------
                Pawn seCred = Mk(), seDebt = Mk();
                InmateDebt seD = DebtSystem.Open(seCred, seDebt, DebtReason.Cards, 5, 2);
                DebtSystem.Notify_Searched(seDebt, 0f);
                bool still = !seD.known;
                DebtSystem.Notify_Searched(seDebt, 1f);
                bool found = still && seD.known && Lines(Line("RP_Chr_DebtKnown", seDebt.LabelShort, seCred.LabelShort, Money.Format(5), DebtSystem.ReasonText(seD))) == 1;
                if (!found) problems0.Add($"economy test: a search did not turn up a hidden debt (and only then): hidden before {still}, known after {seD.known}");
                log += $" search={found}";

                // ---- a party leaving drops the debt (silently when the warden never heard of it) ----------------------------------------------------------
                Pawn lvCred = Mk(), lvDebt = Mk(), lvCred2 = Mk(), lvDebt2 = Mk();
                InmateDebt lvHidden = DebtSystem.Open(lvCred, lvDebt, DebtReason.Drugs, 12, 3);
                InmateDebt lvShown = DebtSystem.Open(lvCred2, lvDebt2, DebtReason.Cards, 6, 3);
                lvShown.known = true;
                int entries0 = r.NewEntries.Count();
                g.story.Notify_Leaving(lvDebt);
                bool quietly = !g.story.debts.Contains(lvHidden) && r.NewEntries.Count() == entries0;
                g.story.Notify_Leaving(lvCred2);
                string droppedLine = Line("RP_Chr_DebtDropped", lvDebt2.LabelShort, lvCred2.LabelShort, Money.Format(6));
                bool dropped = !g.story.debts.Contains(lvShown) && Lines(droppedLine) == 1;
                if (!quietly || !dropped) problems0.Add($"economy test: a debt whose party left was not dropped (hidden one quietly {quietly}, known one with its line {dropped})");
                log += $" dropped={quietly && dropped}";

                // ---- cards: seated together in a common-room patch, a winner and losers who owe --------------------------------------------------------
                var table = new List<Pawn>();
                for (int i = 0; i < 5; i++)
                {
                    Pawn p = Mk();
                    Place(p, roomCells[i % roomCells.Count]);
                    table.Add(p);
                }
                Dictionary<int, List<Pawn>> seating = DebtSystem.Seating(map, pm);
                int patch = pm.zones.PatchAt(roomCells[0]);
                bool seated = seating.TryGetValue(patch, out List<Pawn> seats) && table.All(seats.Contains);
                if (!seated) problems0.Add($"economy test: five inmates standing in a common-room patch were not seated together ({seats?.Count ?? 0} seated of 5 in patch {patch})");
                // (The fifth owes and has nothing: not asked to the table.)
                table[4].Inmate().cash = 0;
                DebtSystem.Open(dealer, table[4], DebtReason.Cards, 3, 2);
                int[] cash = { 20, 10, 1, 0 };
                for (int i = 0; i < 4; i++) table[i].Inmate().cash = cash[i];
                int total0 = table.Sum(p => p.Inmate().cash);
                int games0 = g.stats.cardGames;
                Pawn winner = table[0];
                Pawn won = DebtSystem.Deal(table, pm, stake: 4, forcedWinner: winner);
                InmateDebt owes2 = Owed(table[2]), owes3 = Owed(table[3]);
                bool played = won == winner && g.stats.cardGames == games0 + 1 && winner.Inmate().cash == 20 + 4 + 1 && table[1].Inmate().cash == 6 && table[2].Inmate().cash == 0 && table[3].Inmate().cash == 0
                              && table.Take(4).Sum(p => p.Inmate().cash) == total0 && owes2 is { reason: DebtReason.Cards, amount: 3 } && owes3 is { reason: DebtReason.Cards, amount: 4 }
                              && owes2.creditorId == winner.thingIDNumber && owes3.creditorId == winner.thingIDNumber && Owed(table[1]) == null
                              && Memory(winner, RPDebtDefOf.RP_WonAtCards) && Memory(table[1], RPDebtDefOf.RP_LostAtCards) && !Memory(table[4], RPDebtDefOf.RP_LostAtCards)
                              && winner.Inmate().records.Any(rec => rec.def == RPDebtDefOf.RP_Gambling) && !owes2.known && !owes3.known;
                if (!played) problems0.Add($"economy test: a card game did not move the cash to the winner (cash {string.Join("/", table.Select(p => p.Inmate().cash))}), leave the broke losers owing (debts {owes2?.amount}/{owes3?.amount}), seat only those who could play, and go on the winner's record");
                log += $" cards={played && seated}";

                // ---- protection: bought, its effect on fear and extortion, paid week after week, and lapsing ------------------------------------------------
                Pawn protector = Mk(), covered = Mk(), extortionist = Mk(fighter: true);
                (protector.Inmate().gang, protector.Inmate().gangLeader, protector.Inmate().gangKnown) = (ravens, true, false);
                covered.Inmate().fear = 100f;
                covered.skills.GetSkill(SkillDefOf.Melee).Level = 1;
                extortionist.skills.GetSkill(SkillDefOf.Melee).Level = 12;
                extortionist.Inmate().reputations.Add(RPReputationDefOf.RP_Extortionist);
                InmateDebt fee = DebtSystem.OfferProtection(map, force: true);
                bool bought = fee != null && fee.reason == DebtReason.Protection && fee.debtorId == covered.thingIDNumber && fee.creditorId == protector.thingIDNumber && fee.amount == DebtSystem.ProtectionFee
                              && fee.dueTick == now() + DebtSystem.ProtectionDays * GenDate.TicksPerDay && !fee.known && DebtSystem.ProtectionOf(covered) == fee && Memory(covered, RPDebtDefOf.RP_Protected)
                              && covered.Inmate().fear < 100f;
                if (!bought) problems0.Add($"economy test: a gang member did not sell a frightened inmate protection (debt {fee?.reason}/{fee?.amount}/{fee?.debtorId}, protected {DebtSystem.ProtectionOf(covered) == fee})");
                // Effect: less fear an hour, and the extortionist (not the protector's) is turned away.
                CompInmate cc = covered.Inmate();
                cc.fear = 50f;
                FearSystem.Hourly(covered, cc, pm);
                float fearProtected = cc.fear;
                InmateDebt held = fee;
                g.story.debts.Remove(fee);
                cc.fear = 50f;
                FearSystem.Hourly(covered, cc, pm);
                float fearUnprotected = cc.fear;
                g.story.debts.Add(held);
                // (Nobody else standing here has anything to be taken: only the covered one is a victim.)
                foreach (Pawn other in made)
                    if (other != covered) other.Inmate().cash = 0;
                cc.cash = 30;
                // (And next to each other: the victim has to be within four steps.)
                Place(covered, yardCells[0]);
                Place(extortionist, yardCells[1]);
                var extorting = (ReputationBehavior_Extortionist)RPReputationDefOf.RP_Extortionist.Behavior;
                bool turnedAway = !extorting.Extort(extortionist, extortionist.Inmate(), pm) && cc.cash == 30;
                g.story.debts.Remove(held);
                bool extorted = extorting.Extort(extortionist, extortionist.Inmate(), pm) && cc.cash < 30;
                g.story.debts.Add(held);
                Calm(covered);
                Calm(extortionist);
                bool effect = Mathf.Abs(fearUnprotected - fearProtected - DebtSystem.ProtectionFear) < 0.001f && turnedAway && extorted;
                if (!effect) problems0.Add($"economy test: protection did not lower fear by {DebtSystem.ProtectionFear} an hour ({fearProtected} against {fearUnprotected}) or keep an extortionist off ({turnedAway}; once unprotected they extort {extorted})");
                // Paid at the due date: the fee moves, next week's opens (a cycle on), and the protection goes on.
                cc.cash = 20;
                protector.Inmate().cash = 0;
                fee.dueTick = now() - 10;
                DebtSystem.Daily(map);
                InmateDebt next = Owed(covered);
                bool renewed = next != null && next != fee && next.reason == DebtReason.Protection && next.cycles == 1 && cc.cash == 20 - DebtSystem.ProtectionFee && protector.Inmate().cash == DebtSystem.ProtectionFee
                               && DebtSystem.ProtectionOf(covered) == next;
                // Unpaid: it lapses the moment it is due, and the usual threats follow.
                cc.cash = 0;
                next.dueTick = now() - 10;
                bool lapsed = DebtSystem.ProtectionOf(covered) == null;
                DebtSystem.Daily(map);
                bool threatenedFee = next.stage == DebtStage.Threatened && DebtSystem.ProtectionOf(covered) == null;
                if (!renewed || !lapsed || !threatenedFee) problems0.Add($"economy test: protection was not renewed when paid ({renewed}) or did not lapse and turn to threats when not ({lapsed}/{threatenedFee})");
                // (In custody nobody sells it: the fee is dropped.)
                if (pcDef != null)
                {
                    SecurityLevelDef coveredSec = cc.security;
                    cc.security = pcDef;
                    DebtSystem.Daily(map);
                    bool noFee = Owed(covered) == null;
                    cc.security = coveredSec;
                    if (!noFee) problems0.Add("economy test: a protection fee outlived the debtor's move into protective custody");
                }
                (protector.Inmate().gang, protector.Inmate().gangLeader) = (null, false);
                log += $" protection={bought && effect && renewed && lapsed && threatenedFee}";

                // ---- protection for a newcomer: a gang offers it to someone just admitted (nobody is afraid in a quiet prison), and not to a veteran -----------------------
                // (In a wing of their own, so that nobody else here, not even a real inmate admitted last week and in no gang, is a candidate for the offer.)
                SecurityLevelDef ownWing = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(d => real.All(c => c.security != d) && made.All(m => m.Inmate().security != d));
                if (ownWing == null) problems0.Add("economy test: no security level free of inmates to try the protection offers in");
                Pawn seller = Mk(), newcomer = Mk(), veteran = Mk();
                foreach (Pawn mark in new[] { seller, newcomer, veteran })
                    if (ownWing != null) mark.Inmate().security = ownWing;
                (seller.Inmate().gang, seller.Inmate().gangLeader, seller.Inmate().gangKnown) = (ravens, true, false);
                newcomer.Inmate().admittedTick = now() - GenDate.TicksPerDay;
                InmateDebt toNewcomer = DebtSystem.OfferProtection(map, force: true);
                InmateDebt toVeteran = DebtSystem.OfferProtection(map, force: true);
                bool newcomerOffer = toNewcomer != null && toNewcomer.reason == DebtReason.Protection && toNewcomer.debtorId == newcomer.thingIDNumber && toNewcomer.creditorId == seller.thingIDNumber;
                if (!newcomerOffer || toVeteran != null)
                    problems0.Add($"economy test: a gang member's protection offer: to {toNewcomer?.debtorName ?? "nobody"} and not the newcomer {newcomer.LabelShort} (debt {toNewcomer?.reason}), and then to {(toVeteran == null ? "nobody" : toVeteran.debtorName)} (a calm veteran such as {veteran.LabelShort} is nobody's mark)");
                (seller.Inmate().gang, seller.Inmate().gangLeader) = (null, false);
                log += $" newcomer-protection={newcomerOffer && toVeteran == null}";

                // ---- a huddle: free-time inmates standing together outside the common room make a table, in the common room it is the patch's, and one forced elsewhere sits out ---------
                {
                    int hourNow = GenLocalDate.HourOfDay(map);
                    RegimeSchedule schedule = g.RegimeFor(sec);
                    RegimeActivityDef scheduled = schedule.At(hourNow);
                    r.Add(() => schedule.Set(hourNow, scheduled));
                    schedule.Set(hourNow, RPDefOf.RP_FreeTime);
                    // (The pawns made for the checks above are still standing about the yard: one of them within reach of a huddler, ahead of them in the list, takes them into a group of its own. They sit this out.)
                    var elsewhere = made.Select(m => (c: m.Inmate(), was: m.Inmate().forcedActivity)).ToList();
                    foreach ((CompInmate mc, RegimeActivityDef _) in elsewhere) mc.forcedActivity = RPDefOf.RP_Lockup;
                    Pawn[] huddlers = { Mk(), Mk(), Mk() };
                    Pawn sitsOut = Mk();
                    for (int i = 0; i < huddlers.Length; i++) Place(huddlers[i], yardCells[i]);
                    Place(sitsOut, yardCells[3]);
                    sitsOut.Inmate().forcedActivity = RPDefOf.RP_Lockup;
                    Pawn[] atTable = { Mk(), Mk(), Mk() };
                    for (int i = 0; i < atTable.Length; i++) Place(atTable[i], roomCells[i]);
                    List<List<Pawn>> huddles = DebtSystem.Huddles(map, pm);
                    bool together = huddles.Any(group => huddlers.All(group.Contains));
                    bool leftOut = !huddles.Any(group => group.Contains(sitsOut) || atTable.Any(group.Contains));
                    sitsOut.Inmate().forcedActivity = null;
                    foreach ((CompInmate mc, RegimeActivityDef was) in elsewhere) mc.forcedActivity = was;
                    if (!together || !leftOut)
                        problems0.Add($"economy test: three inmates huddled in the yard on free time {(together ? "were" : "were not")} found together, and one forced elsewhere or ones at a common-room table {(leftOut ? "were left out" : "were put in a huddle")} ({huddles.Count} huddles: "
                            + string.Join(" | ", huddles.Select(group => string.Join(",", group.Select(x => $"{x.LabelShort}{x.Position - huddlers[0].Position}")))) + "; the three: "
                            + string.Join(", ", huddlers.Select(x => $"{x.LabelShort} active {InmateUtility.IsActiveInmate(x)} awake {x.Awake()} mental {x.MentalStateDef?.defName} zone {pm.zones.ZoneAt(x.Position)?.defName} activity {g.CurrentActivity(x)?.defName} forced {x.Inmate().forcedActivity?.defName}")) + ")");
                    log += $" huddle={together && leftOut}";
                }

                // ---- an early, full payment settles it and the goal (silently while hidden) -----------------------------------------------------------------
                Pawn earlyCred = Mk(), earlyDebt = Mk();
                InmateDebt earlyD = DebtSystem.Open(earlyCred, earlyDebt, DebtReason.Drugs, 12, 3);
                earlyDebt.Inmate().cash = 15;
                DebtSystem.Daily(map);
                bool early = !g.story.debts.Contains(earlyD) && earlyDebt.Inmate().cash == 3 && earlyCred.Inmate().cash == 12 && earlyDebt.Inmate().want is { status: WantStatus.Fulfilled, secret: true } && Naming(earlyCred, earlyDebt) == 0;
                if (!early) problems0.Add($"economy test: a debtor with the cash did not clear a debt before it was due, quietly, with the goal fulfilled (cash {earlyDebt.Inmate().cash}/{earlyCred.Inmate().cash}, goal {earlyDebt.Inmate().want?.status}, lines {Naming(earlyCred, earlyDebt)})");
                log += $" early={early}";

                bool InCustodyNow(Pawn p) => DebtSystem.InCustody(p.Inmate());
                RPLog.Message($"[RimPrison:test] economy: {log}");
            });
        }
    }
}
