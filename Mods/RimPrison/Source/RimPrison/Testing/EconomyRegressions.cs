using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the inmate economy (findings 1-4 and 6-11; the triggers and rates of finding 5 are not touched, only the rank of a debt
    /// tip). Each fails without its fix, is its own method called once from FireAllEvents (last of the self-contained checks), works on throwaway inmates numbered from 91000
    /// (and a throwaway guard on an all-work timetable and full rest) and puts back what it touches (<see cref="Rollback"/>).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>A quiet corner of the prison for an economy check: an empty ledger, the real inmates out of it (no gang, not active, not afraid), throwaway inmates in the yard.</summary>
        private sealed class EconomyScene
        {
            public readonly Map map;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly PrisonMap pm;
            public readonly SecurityLevelDef sec = InmateUtility.DefaultSecurity;
            public readonly SecurityLevelDef custody = DefDatabase<SecurityLevelDef>.GetNamedSilentFail("RP_Protective");
            public readonly SecurityLevelDef otherWing;
            public readonly GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
            private readonly Rollback r;
            private readonly List<IntVec3> yard;
            private int spot;

            public EconomyScene(Map map, Rollback r)
            {
                this.map = map;
                this.r = r;
                pm = PrisonMap.Of(map);
                otherWing = DefDatabase<SecurityLevelDef>.AllDefsListForReading.First(l => l != sec && l != custody);
                List<IntVec3> all = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => c.Standable(map)).ToList();
                yard = all.Count == 0 ? all : all.OrderBy(c => c.DistanceToSquared(all[0])).Take(40).ToList();
                // The real inmates do not take part (a gang member selling protection, a frightened one buying it, a snitch near the one telling).
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null)
                    .Select(c => (c, c.gang, c.gangKnown, c.activeFlag, c.fear, c.cash, c.records.Count, c.want, c.want?.status ?? WantStatus.OnTrack, c.want?.resolvedTick ?? -1)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, GangDef gang, bool known, bool active, float fear, int cash, int records, InmateWant want, WantStatus status, int resolved) in real)
                    {
                        (c.gang, c.gangKnown, c.activeFlag, c.fear, c.cash) = (gang, known, active, fear, cash);
                        if (c.records.Count > records) c.records.RemoveRange(records, c.records.Count - records);
                        c.want = want;
                        if (want != null) (want.status, want.resolvedTick) = (status, resolved);
                    }
                });
                foreach ((CompInmate c, GangDef _, bool _, bool _, float _, int _, int _, InmateWant _, WantStatus _, int _) in real) (c.gang, c.gangKnown, c.activeFlag, c.fear) = (null, false, false, 0f);
                List<InmateDebt> debts0 = g.story.debts;
                List<StoryArc> arcs0 = g.story.arcs;
                int nextDebt0 = g.story.nextDebtId, nextArc0 = g.story.nextArcId;
                var lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                r.Add(() =>
                {
                    g.story.debts = debts0;
                    g.story.nextDebtId = nextDebt0;
                    g.story.arcs = arcs0;
                    g.story.nextArcId = nextArc0;
                    g.story.lastArcStart = lastStart0;
                });
                g.story.debts = new List<InmateDebt>();
                g.story.arcs = arcs0.Where(a => a.def != RPDebtDefOf.RP_Arc_Debt).ToList();
            }

            public int Now => Find.TickManager.TicksGame;

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>A new inmate in the yard with no gang, no cash, no reputations and no goal (<paramref name="fighter"/>: one who can throw a punch).</summary>
            public Pawn Inmate(bool fighter = false)
            {
                Pawn p = Throwaway(map, r);
                for (int tries = 0; fighter && p.WorkTagIsDisabled(WorkTags.Violent) && tries < 30; tries++)
                {
                    if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                    p = Throwaway(map, r);
                }
                CompInmate c = p.Inmate();
                (c.activeFlag, c.gang, c.gangKnown, c.cash, c.tension, c.fear, c.want, c.forcedActivity, c.security) = (true, null, false, 0, 20f, 0f, null, null, sec);
                c.reputations.Clear();
                Place(p, yard[spot++ % yard.Count]);
                return p;
            }

            /// <summary>A gang member (the leader of the Ravens, not known to be).</summary>
            public Pawn GangMember()
            {
                Pawn p = Inmate();
                (p.Inmate().gang, p.Inmate().gangLeader) = (ravens, true);
                return p;
            }

            /// <summary>A guard on site with an all-work timetable and full rest (so nothing sends them to bed), in sight of the cell, counted among the prison's guards until the check is done.</summary>
            public Pawn Guard(IntVec3 at)
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (guard.timetable != null)
                    for (int hour = 0; hour < 24; hour++) guard.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                if (guard.needs?.rest != null) guard.needs.rest.CurLevel = 1f;
                Place(guard, at);
                pm.Guards.Add(guard);
                r.Add(() => pm.Guards.Remove(guard));
                return guard;
            }

            /// <summary>
            /// The prison's own guards, cameras and towers watch nothing until the check is done (put back after): who has the yard in sight depends on the hour (a patrol that
            /// happens to be there, a camera over it), and a check of what is seen and what is not sets up its own watchers.
            /// </summary>
            public void NobodyWatches()
            {
                (List<Pawn> guards0, List<CompSecurityCamera> cameras0, List<CompGuardTower> towers0) = (pm.Guards.ToList(), pm.Cameras.ToList(), pm.Towers.ToList());
                r.Add(() =>
                {
                    pm.Guards.Clear();
                    pm.Guards.AddRange(guards0);
                    pm.Cameras.Clear();
                    pm.Cameras.AddRange(cameras0);
                    pm.Towers.Clear();
                    pm.Towers.AddRange(towers0);
                });
                pm.Guards.Clear();
                pm.Cameras.Clear();
                pm.Towers.Clear();
            }

            /// <summary>A cell in the yard (the nth one used for inmates), for placing something near them.</summary>
            public IntVec3 YardCell(int n) => yard[n % yard.Count];

            /// <summary>An overdue debt: the due date has passed.</summary>
            public InmateDebt Overdue(Pawn creditor, Pawn debtor, int amount = DebtSystem.ProtectionFee, DebtReason reason = DebtReason.Drugs)
            {
                InmateDebt d = DebtSystem.Open(creditor, debtor, reason, amount, 1);
                d.dueTick = Now - 10;
                return d;
            }

            public int Lines(string text) => g.story.entries.Count(e => e.text == text);
        }

        private static string EcoLine(string key, params NamedArgument[] args) => key.Translate(args).Resolve();

        private static bool EcoMemory(Pawn p, ThoughtDef def) => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(def) != null;

        // ---- 1: the memories do not give a hidden debt, card game or protection away -------------------------------------------------------------------

        /// <summary>
        /// Vanilla shows every memory in the Needs tab and a social one, with its label, in the Social tab. Until the warden knows, the economy's memories read as something generic
        /// (nothing of debt, cards, money, protection or a threat); once a debt is known they read as what they are. A goal nobody knows of gives no "reached a goal" memory either.
        /// </summary>
        private static void CheckEconomyMemoriesStayGenericUntilKnown(Map map)
        {
            Guarded("economy: memories do not give a hidden debt away", map, r =>
            {
                var s = new EconomyScene(map, r);
                string[] giveaways = { "debt", "card", "owe", "protect", "money", "threat", "paid", "gang" };
                string Real(ThoughtDef def, string part) => ("RP_Thought_" + def.defName + "_" + part).Translate().ToString();
                // What a memory shows anywhere: the label in the Needs tab, the social label, the tooltip.
                string Shows(Thought_Memory m) => $"{m.LabelCap} / {m.LabelCapSocial} / {m.Description}";
                bool Generic(Thought_Memory m) => m != null && m.LabelCap != Real(m.def, "Label").CapitalizeFirst() && m.Description != Real(m.def, "Description")
                                                 && !giveaways.Any(w => Shows(m).ToLowerInvariant().Contains(w));
                bool Reads(Thought_Memory m) => m != null && m.LabelCap == Real(m.def, "Label").CapitalizeFirst() && m.LabelCapSocial == m.LabelCap && m.Description == Real(m.def, "Description");
                Thought_Memory Mem(Pawn p, ThoughtDef def) => p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(def);
                var seen = new List<string>();

                // A debt threatened, then paid, all hidden: nothing says what it is, and the secret goal gives no "reached a goal".
                Pawn creditor = s.Inmate(), debtor = s.Inmate();
                InmateDebt d = s.Overdue(creditor, debtor);
                DebtSystem.Daily(map);
                Thought_Memory threatened = Mem(debtor, RPDebtDefOf.RP_DebtThreatened);
                bool hidThreat = d.stage == DebtStage.Threatened && !d.known && Generic(threatened);
                if (threatened != null) seen.Add(Shows(threatened));
                debtor.Inmate().cash = 20;
                DebtSystem.Daily(map);
                Thought_Memory settled = Mem(debtor, RPDebtDefOf.RP_DebtSettled);
                bool hidSettled = !s.g.story.debts.Contains(d) && Generic(settled);
                if (settled != null) seen.Add(Shows(settled));
                bool noGoalMemory = !EcoMemory(debtor, RPWantDefOf.RP_WantFulfilled);
                if (!hidThreat || !hidSettled || !noGoalMemory)
                    problems0.Add($"economy memories: a debt nobody told the warden of showed through its memories (threat hidden {hidThreat}, cleared hidden {hidSettled}, no 'reached a goal' for the secret goal {noGoalMemory}): {string.Join(" | ", seen)}");

                // A card game nobody saw: the winner's and the loser's memories (and the loser's opinion of the winner) say nothing of cards.
                var table = new List<Pawn> { s.Inmate(), s.Inmate(), s.Inmate() };
                foreach (Pawn p in table) p.Inmate().cash = 20;
                Pawn won = DebtSystem.Deal(table, s.pm, stake: 4, forcedWinner: table[0]);
                Pawn loser = table[1];
                Thought_Memory wonMem = won == null ? null : Mem(won, RPDebtDefOf.RP_WonAtCards), lostMem = Mem(loser, RPDebtDefOf.RP_LostAtCards);
                bool hidCards = Generic(wonMem) && Generic(lostMem) && lostMem.otherPawn == won;
                if (!hidCards) problems0.Add($"economy memories: an unseen card game showed through its memories ({(wonMem == null ? "none" : Shows(wonMem))} | {(lostMem == null ? "none" : Shows(lostMem))})");

                // Protection nobody has heard of.
                Pawn protector = s.GangMember(), client = s.Inmate();
                DebtSystem.Buy(protector, client);
                Thought_Memory protectedMem = Mem(client, RPDebtDefOf.RP_Protected);
                bool hidProtection = Generic(protectedMem) && protectedMem.otherPawn == protector;
                if (!hidProtection) problems0.Add($"economy memories: protection nobody has heard of showed through its memory ({(protectedMem == null ? "none" : Shows(protectedMem))})");

                // Once the warden knows of the debt, its memories read as what they are (both sides'), and not before.
                Pawn creditor2 = s.Inmate(), debtor2 = s.Inmate();
                InmateDebt d2 = s.Overdue(creditor2, debtor2);
                DebtSystem.Daily(map);
                Thought_Memory before = Mem(debtor2, RPDebtDefOf.RP_DebtThreatened);
                bool hiddenFirst = Generic(before);
                DebtSystem.Notify_Searched(debtor2, 1f);
                bool readsNow = d2.known && Reads(Mem(debtor2, RPDebtDefOf.RP_DebtThreatened));
                if (!hiddenFirst || !readsNow)
                    problems0.Add($"economy memories: after a search turned up the debt its memory should read as what it is (hidden before {hiddenFirst}, read after {readsNow}: {(Mem(debtor2, RPDebtDefOf.RP_DebtThreatened) is { } after ? Shows(after) : "none")})");
            });
        }

        // ---- 2: a debt nobody can collect ends --------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A debtor in protective custody, on another wing than the creditor or long overdue is out of reach: the debt is written off after a few days (the time limit is weeks), and both
        /// are free to owe and lend again. A debt that can be collected is not touched, and a debt whose party is gone is dropped by the daily pass.
        /// </summary>
        private static void CheckUncollectableDebtsEnd(Map map)
        {
            Guarded("economy: a debt nobody can collect ends", map, r =>
            {
                var s = new EconomyScene(map, r);
                int day = GenDate.TicksPerDay;
                if (s.custody == null)
                {
                    problems0.Add("economy uncollectable: no protective custody level to put a debtor in");
                    return;
                }
                // Three debtors in protective custody, all owing one dealer: the dealer is out of loans until the debts end.
                Pawn dealer = s.Inmate();
                var held = new List<(Pawn debtor, InmateDebt d)>();
                for (int i = 0; i < DebtSystem.MaxLoansPerCreditor; i++)
                {
                    Pawn debtor = s.Inmate();
                    InmateDebt d = s.Overdue(dealer, debtor);
                    d.known = i == 0;
                    debtor.Inmate().security = s.custody;
                    held.Add((debtor, d));
                }
                Pawn newcomer = s.Inmate();
                bool lockedOut = !DebtSystem.CanLend(dealer, newcomer) && !DebtSystem.CanLend(dealer, held[0].debtor);
                DebtSystem.Daily(map);
                bool noticed = held.All(h => EcoHas(s, h.d) && h.d.stuckSince != InmateDebt.NotStuck);
                // (Not yet: they have days.) Then the deadline passes.
                foreach ((Pawn _, InmateDebt d) in held) d.stuckSince = s.Now - EcoLimit;
                string line = EcoLine("RP_Chr_DebtWrittenOff", held[0].debtor.LabelShort, dealer.LabelShort, Money.Format(held[0].d.amount));
                DebtSystem.Daily(map);
                bool ended = held.All(h => !EcoHas(s, h.d)) && s.Lines(line) == 1;
                bool freed = DebtSystem.CanLend(dealer, newcomer) && DebtSystem.OwedBy(held[0].debtor) == null;
                if (!lockedOut || !noticed || !ended || !freed)
                    problems0.Add($"economy uncollectable: debts of debtors in protective custody (dealer out of loans {lockedOut}, noticed after a pass {noticed}) were not written off once the deadline passed ({ended}; line said once {s.Lines(line)}) or did not free both ({freed})");

                // Another wing than the creditor's, the same.
                Pawn cred2 = s.Inmate(), debt2 = s.Inmate();
                InmateDebt wing = s.Overdue(cred2, debt2);
                cred2.Inmate().security = s.otherWing;
                DebtSystem.Daily(map);
                bool startedWing = EcoHas(s, wing) && wing.stuckSince != InmateDebt.NotStuck;
                wing.stuckSince = s.Now - EcoLimit;
                DebtSystem.Daily(map);
                bool wingEnded = !EcoHas(s, wing) && DebtSystem.CanLend(cred2, debt2);
                if (!startedWing || !wingEnded) problems0.Add($"economy uncollectable: a debt between inmates on different wings was not written off (timer started {startedWing}, ended and freed {wingEnded})");

                // One that can be collected is left alone, and a timer that was running is stopped when they can be reached again.
                Pawn cred3 = s.Inmate(), debt3 = s.Inmate();
                InmateDebt live = s.Overdue(cred3, debt3);
                live.stuckSince = s.Now - EcoLimit;
                DebtSystem.Daily(map);
                bool kept = EcoHas(s, live) && live.stuckSince == InmateDebt.NotStuck;
                if (!kept) problems0.Add($"economy uncollectable: a debt whose debtor can be reached was written off or kept its timer (kept {EcoHas(s, live)}, timer {live.stuckSince})");

                // Overdue for weeks, whatever the reason: the time limit.
                Pawn cred4 = s.Inmate(), debt4 = s.Inmate();
                InmateDebt old = s.Overdue(cred4, debt4);
                old.dueTick = s.Now - (DebtSystem.MaxOverdueDays + 1) * day;
                DebtSystem.Daily(map);
                bool limited = !EcoHas(s, old) && DebtSystem.OwedBy(debt4) == null && s.Lines(EcoLine("RP_Chr_DebtWrittenOff", debt4.LabelShort, cred4.LabelShort, Money.Format(old.amount))) == 0;
                if (!limited) problems0.Add($"economy uncollectable: a debt overdue past the time limit was kept ({EcoHas(s, old)}) or said so though the warden never heard of it");

                // A party who is gone without a farewell: the daily pass drops it, and the one left is free.
                Pawn cred5 = s.Inmate(), debt5 = s.Inmate();
                InmateDebt gone = s.Overdue(cred5, debt5);
                debt5.Destroy(DestroyMode.Vanish);
                DebtSystem.Daily(map);
                bool dropped = !EcoHas(s, gone) && DebtSystem.Debts.All(x => x.creditorId != cred5.thingIDNumber);
                if (!dropped) problems0.Add("economy uncollectable: the daily pass kept a debt whose debtor was gone");
            });
        }

        private static int EcoLimit => DebtSystem.UncollectableDays * GenDate.TicksPerDay + 10;

        private static bool EcoHas(EconomyScene s, InmateDebt d) => s.g.story.debts.Contains(d);

        // ---- 3: protection follows the protector ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A gang member in the hole, in protective custody or on another wing sells no protection, covers nobody (extortionists are not turned away) and gets no fee: the fee is
        /// dropped, not paid and renewed. A free one on the client's wing does all of it.
        /// </summary>
        private static void CheckProtectionFollowsTheProtector(Map map)
        {
            Guarded("economy: protection follows the protector", map, r =>
            {
                var s = new EconomyScene(map, r);
                if (s.custody == null)
                {
                    problems0.Add("economy protection: no protective custody level to put the protector in");
                    return;
                }
                Pawn protector = s.GangMember(), client = s.Inmate(), extortionist = s.Inmate();
                CompInmate pc = protector.Inmate(), cc = client.Inmate();
                cc.fear = 100f;
                // Nobody sells protection from the hole.
                pc.forcedActivity = RPSecurityDefOf.RP_Solitary;
                InmateDebt offeredFromTheHole = DebtSystem.OfferProtection(map, force: true);
                pc.forcedActivity = null;
                if (offeredFromTheHole != null) problems0.Add("economy protection: a gang leader in solitary sold protection");
                DebtSystem.Debts.Clear();
                cc.fear = 100f;
                // A free one does (and covers the client against an extortionist who is not of their gang).
                InmateDebt fee = DebtSystem.OfferProtection(map, force: true);
                bool free = fee != null && fee.debtorId == client.thingIDNumber && fee.creditorId == protector.thingIDNumber && DebtSystem.ProtectionOf(client) == fee && DebtSystem.ProtectedFrom(client, extortionist);
                if (!free) problems0.Add($"economy protection: a free gang member did not sell protection and cover the client ({fee?.reason})");
                if (fee == null) return;

                // Each way of being unable: the cover is paused at once, and the fee is dropped at the next daily pass.
                var ways = new (string name, Action on, Action off)[]
                {
                    ("in the hole", () => pc.forcedActivity = RPSecurityDefOf.RP_Solitary, () => pc.forcedActivity = null),
                    ("in protective custody", () => pc.security = s.custody, () => pc.security = s.sec),
                    ("on another wing", () => pc.security = s.otherWing, () => pc.security = s.sec),
                };
                foreach ((string name, Action on, Action off) in ways)
                {
                    InmateDebt open = DebtSystem.Debts.FirstOrDefault(x => x.debtorId == client.thingIDNumber) ?? DebtSystem.Buy(protector, client);
                    try
                    {
                        on();
                        bool paused = DebtSystem.ProtectionOf(client) == null && !DebtSystem.ProtectedFrom(client, extortionist);
                        // The fee falls due with them unable: it is not paid, not renewed, and nothing is owed.
                        cc.cash = 20;
                        pc.cash = 0;
                        open.dueTick = s.Now - 10;
                        DebtSystem.Daily(map);
                        bool dropped = DebtSystem.OwedBy(client) == null && cc.cash == 20 && pc.cash == 0;
                        if (!paused || !dropped)
                            problems0.Add($"economy protection: with the protector {name}, protection was not paused ({paused}: it keeps fear down and extortionists off) or the fee was not dropped unpaid ({dropped}: owes {DebtSystem.OwedBy(client)?.amount}, cash {cc.cash}/{pc.cash})");
                    }
                    finally
                    {
                        off();
                    }
                }

                // Control: free again, the fee is paid when due and the protection goes on.
                InmateDebt again = DebtSystem.Buy(protector, client);
                cc.cash = 20;
                again.dueTick = s.Now - 10;
                DebtSystem.Daily(map);
                InmateDebt next = DebtSystem.OwedBy(client);
                bool renewed = next != null && next != again && next.cycles == 1 && cc.cash == 20 - DebtSystem.ProtectionFee && DebtSystem.ProtectionOf(client) == next;
                if (!renewed) problems0.Add($"economy protection: a free protector's fee was not paid and renewed when due (cash {cc.cash}, next {next?.cycles})");
            });
        }

        // ---- 4: a known protection debt names no hidden gang -------------------------------------------------------------------------------------

        /// <summary>A protection debt becomes known by a tip or a search: only a gang sells it, so the creditor's gang is known with it (never a named member of a hidden gang); other debts leave a gang alone.</summary>
        private static void CheckKnownProtectionNamesNoHiddenGang(Map map)
        {
            Guarded("economy: a known protection debt names no hidden gang", map, r =>
            {
                var s = new EconomyScene(map, r);
                // By a snitch's tip.
                Pawn tipped = s.GangMember(), tippedClient = s.Inmate(), snitch = s.Inmate();
                InmateDebt fee = DebtSystem.Buy(tipped, tippedClient);
                bool hiddenBefore = !tipped.Inmate().gangKnown && !fee.known;
                string tip = DebtSystem.TryTip(snitch, new List<Pawn> { tippedClient });
                bool tipRevealed = tip != null && fee.known && tipped.Inmate().gangKnown && tip.Contains(tipped.LabelShort);
                // By a search.
                Pawn found = s.GangMember(), foundClient = s.Inmate();
                InmateDebt fee2 = DebtSystem.Buy(found, foundClient);
                DebtSystem.Notify_Searched(foundClient, 1f);
                bool searchRevealed = fee2.known && found.Inmate().gangKnown;
                // And the other kinds of debt do not give a gang away.
                Pawn dealer = s.GangMember(), buyer = s.Inmate();
                InmateDebt drugs = DebtSystem.Open(dealer, buyer, DebtReason.Drugs, 12, 3);
                DebtSystem.Notify_Searched(buyer, 1f);
                bool drugsQuiet = drugs.known && !dealer.Inmate().gangKnown;
                bool none = DebtSystem.Known().Where(d => d.reason == DebtReason.Protection).All(d => map.mapPawns.PrisonersOfColony.FirstOrDefault(p => p.thingIDNumber == d.creditorId)?.Inmate() is { gangKnown: true });
                if (!hiddenBefore || !tipRevealed || !searchRevealed || !drugsQuiet || !none)
                    problems0.Add($"economy protection: a known protection debt named a member of a hidden gang (hidden before {hiddenBefore}; tip '{tip}' revealed their gang {tipRevealed}; a search revealed it {searchRevealed}; a drug debt left the gang hidden {drugsQuiet}; every known protection debt's creditor is out {none})");
            });
        }

        // ---- 5: debt tips come after tunnels (and the rest) --------------------------------------------------------------------------------------

        /// <summary>A snitch with a tunnel being dug nearby tips about the tunnel, not about who owes whom; the debt tip comes when there is nothing else to tell.</summary>
        private static void CheckDebtTipsDoNotCrowdOutTunnelTips(Map map)
        {
            Guarded("economy: debt tips do not crowd out tunnel tips", map, r =>
            {
                var s = new EconomyScene(map, r);
                Pawn snitch = s.Inmate(), digger = s.Inmate(), creditor = s.Inmate(), debtor = s.Inmate();
                CompInmate sc = snitch.Inmate(), dc = digger.Inmate();
                sc.reputations.Add(ReputationDefOf.RP_Snitch);
                InmateDebt debt = DebtSystem.Open(creditor, debtor, DebtReason.Drugs, 12, 3);
                // (Nobody else near has anything to tell, and no member of staff is on the take.)
                var others = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null && c != dc).Select(c => (c, c.tunnelKnown, c.suspected, c.tunnelProgress, c.cellSearchPending, c.informedBy)).ToList();
                var staff = map.mapPawns.FreeColonistsSpawned.Select(x => x.GetComp<CompStaff>()).Where(c => c != null && !c.doubleAgent).ToList();
                (float progress0, bool known0, bool pending0, Pawn informed0) = (dc.tunnelProgress, dc.tunnelKnown, dc.cellSearchPending, dc.informedBy);
                try
                {
                    foreach ((CompInmate c, bool _, bool _, float _, bool _, Pawn _) in others) (c.tunnelKnown, c.suspected) = (true, true);
                    foreach (CompStaff c in staff) c.doubleAgent = true;
                    (dc.tunnelProgress, dc.tunnelKnown) = (0.5f, false);
                    string first = InformantSystem.TryTip(snitch, sc);
                    bool tunnel = first == "RP_TipTunnel".Translate(digger.LabelShort).ToString() && dc.tunnelKnown && !debt.known;
                    // (Nothing else to tell: now the debt.)
                    string second = InformantSystem.TryTip(snitch, sc);
                    bool debtTip = second == "RP_TipDebt".Translate(debtor.LabelShort, creditor.LabelShort, Money.Format(debt.amount), DebtSystem.ReasonText(debt)).ToString() && debt.known;
                    if (!tunnel || !debtTip)
                        problems0.Add($"economy tips: with a tunnel being dug nearby the snitch's tip was '{first}' (the tunnel's expected; debt known {!tunnel && debt.known}) and the one after '{second}' (the debt's expected, known {debt.known})");
                }
                finally
                {
                    foreach (CompStaff c in staff) c.doubleAgent = false;
                    foreach ((CompInmate c, bool known, bool suspected, float progress, bool pending, Pawn informed) in others) (c.tunnelKnown, c.suspected, c.tunnelProgress, c.cellSearchPending, c.informedBy) = (known, suspected, progress, pending, informed);
                    (dc.tunnelProgress, dc.tunnelKnown, dc.cellSearchPending, dc.informedBy) = (progress0, known0, pending0, informed0);
                }
            });
        }

        // ---- 6: the first beating does not turn the debt goal into a grudge --------------------------------------------------------------------------

        /// <summary>A beating is a setback to the goal of clearing the debt, which is tried again (also over the next midnight): only the one given up for good turns into a grudge.</summary>
        private static void CheckFirstBeatingKeepsTheDebtGoal(Map map)
        {
            Guarded("economy: the debt goal survives the first beatings", map, r =>
            {
                var s = new EconomyScene(map, r);
                Pawn creditor = s.Inmate(fighter: true), debtor = s.Inmate();
                CompInmate dc = debtor.Inmate();
                InmateDebt d = s.Overdue(creditor, debtor);
                InmateWant goal = dc.want;
                if (goal?.def != RPWantDefOf.RP_Want_Debt)
                {
                    problems0.Add($"economy goal: the debt gave the debtor {goal?.def?.defName ?? "no goal"} instead of the goal to clear it");
                    return;
                }
                var stages = new List<string>();
                for (int round = 1; round <= DebtSystem.MaxBeatings; round++)
                {
                    d.stage = DebtStage.Threatened;
                    d.lastBeatingTick = -999999;
                    creditor.Inmate().forcedActivity = null;
                    s.g.story.lastArcStart.Remove(RPDebtDefOf.RP_Arc_Debt);
                    bool beaten = DebtSystem.TryBeat(d, debtor, creditor, map, s.pm);
                    creditor.mindState.mentalStateHandler.Reset();
                    // (The next midnight: a goal for a score may be formed, and would take its place if it could.)
                    Wants.DailyFor(debtor, dc);
                    bool holds = dc.want is { Resolved: false, def: var held } && held == RPWantDefOf.RP_Want_Debt && dc.want.renewals == round;
                    stages.Add($"{round}:{(beaten ? "beaten" : "not beaten")}/{dc.want?.def?.defName}/{dc.want?.renewals}/{dc.want?.status}");
                    bool lastOne = round == DebtSystem.MaxBeatings;
                    if (!beaten || lastOne == holds)
                    {
                        problems0.Add($"economy goal: the debt goal after beating {round} of {DebtSystem.MaxBeatings} should {(lastOne ? "be given up (and turn into a grudge)" : "still be tried again")}: {string.Join(" ", stages)}");
                        return;
                    }
                }
                // The goal given up for good is the grudge against the one who beat them (when their opinion of them is bad enough for one).
                if (dc.want is { Resolved: false, def: var last } && last != RPWantDefOf.RP_Want_Score)
                    problems0.Add($"economy goal: the debt goal given up turned into {last.defName}, not the grudge");
            });
        }

        // ---- 7: the card game is never seen ----------------------------------------------------------------------------------------------------------

        /// <summary>A table is played only when no seat has a guard in sight (not one seat picked at random), and the game on the winner's record is not a sighting whoever is watching the winner now.</summary>
        private static void CheckCardGameIsNeverSeen(Map map)
        {
            Guarded("economy: the card game is never seen", map, r =>
            {
                var s = new EconomyScene(map, r);
                s.NobodyWatches();
                var table = new List<Pawn> { s.Inmate(), s.Inmate(), s.Inmate() };
                foreach (Pawn p in table) p.Inmate().cash = 20;
                bool openTable = DebtSystem.Unwatched(table, s.pm);
                // A guard in sight of the last seat only (the seat picked at random may be any), and the winner watched too.
                Pawn guard = s.Guard(s.YardCell(3));
                float watchedLast = TensionUtility.Watchers(table[2], s.pm);
                // (A cell with the seat in sight: the first of the yard's nearest that has it.)
                for (int cell = 4; cell < 12 && watchedLast < 1f; cell++)
                {
                    s.Place(guard, s.YardCell(cell));
                    watchedLast = TensionUtility.Watchers(table[2], s.pm);
                }
                bool guardedTable = !DebtSystem.Unwatched(table, s.pm);
                Pawn winner = table[2];
                int sanctions0 = winner.Inmate().records.Count;
                DebtSystem.Deal(table, s.pm, stake: 4, forcedWinner: winner);
                MisconductRecord rec = winner.Inmate().records.Skip(sanctions0).FirstOrDefault(x => x.def == RPDebtDefOf.RP_Gambling);
                bool unseen = rec != null && !rec.seen;
                if (!openTable || watchedLast < 1f || !guardedTable || !unseen)
                    problems0.Add($"economy cards: a table with nobody watching should be played ({openTable}); with a guard in sight of one seat ({watchedLast} watching it) it should not ({guardedTable}); the game on the record of a watched winner should be unseen ({unseen}); "
                                  + $"the guard: spawned {guard.Spawned}, downed {guard.Downed}, awake {guard.Awake()}, {(guard.Position - table[2].Position).LengthHorizontal:F1} cells away, in the list {s.pm.Guards.Contains(guard)}, same map {guard.Map == table[2].Map}");
            });
        }

        // ---- 8: the next round is threatened again ---------------------------------------------------------------------------------------------------

        /// <summary>After the debtor's answer, the next round starts with a new threat (tension, fear, a memory), and the beating is due as soon as the cooldown allows.</summary>
        private static void CheckNextRoundIsThreatened(Map map)
        {
            Guarded("economy: the next round starts with a threat", map, r =>
            {
                var s = new EconomyScene(map, r);
                Pawn creditor = s.Inmate(), debtor = s.Inmate();
                CompInmate dc = debtor.Inmate();
                InmateDebt d = s.Overdue(creditor, debtor);
                d.stage = DebtStage.Answered;
                d.nextTick = s.Now - 1;
                d.beatings = 1;
                dc.tension = 20f;
                dc.fear = 0f;
                DebtSystem.Daily(map);
                bool threatened = d.stage == DebtStage.Threatened && d.nextTick <= s.Now && dc.tension >= 20f + DebtSystem.ThreatTension && dc.fear >= DebtSystem.ThreatFear && EcoMemory(debtor, RPDebtDefOf.RP_DebtThreatened);
                if (!threatened) problems0.Add($"economy rounds: the second round did not start with a threat (stage {d.stage}, tension {dc.tension}, fear {dc.fear}, memory {EcoMemory(debtor, RPDebtDefOf.RP_DebtThreatened)}, beating due {d.nextTick <= s.Now})");
            });
        }

        // ---- 9: a delivery pays what it is worth, and only a delivery -------------------------------------------------------------------------

        /// <summary>
        /// Contraband that only turns up on an inmate (a rookie's phone, a plate from the gym) does not pay their debt; a delivery brought in for it counts at its market value (part-paid if it
        /// falls short) and uses up the arrangement; a protection fee is not paid by one.
        /// </summary>
        private static void CheckSmuggledDeliveryPaysByValue(Map map)
        {
            Guarded("economy: a smuggled delivery pays what it is worth", map, r =>
            {
                var s = new EconomyScene(map, r);
                ThingDef drug = ContrabandUtility.AllContraband.FirstOrDefault(x => ContrabandUtility.Ext(x).kind == ContrabandKind.Drug);
                ThingDef phone = DefDatabase<ThingDef>.GetNamedSilentFail("RP_Cellphone");
                if (drug == null || phone == null)
                {
                    problems0.Add("economy delivery: no drug or phone to deliver");
                    return;
                }
                int Held(Pawn p, ThingDef def) => p.inventory.innerContainer.Where(t => t.def == def).Sum(t => t.stackCount);

                // Turning up in their pockets is not a delivery to the creditor.
                Pawn creditor = s.Inmate(), debtor = s.Inmate();
                InmateDebt d = DebtSystem.Open(creditor, debtor, DebtReason.Drugs, 500, 3);
                d.smuggling = true;
                debtor.Inmate().smuggleArranged = true;
                ContrabandUtility.Give(debtor, phone);
                bool stays = Held(debtor, phone) > 0 && Held(creditor, phone) == 0 && d.amount == 500 && d.smuggling;
                // A delivery that is not worth it all: the creditor gets it, the debt is part-paid, and the arrangement is used up.
                int drug0 = Held(creditor, drug);
                ContrabandUtility.Give(debtor, drug, forDebt: true);
                int doses = Held(creditor, drug) - drug0;
                int worth = DebtSystem.InKindValue(drug, doses);
                bool part = doses > 0 && EcoHas(s, d) && d.amount == 500 - worth && !d.smuggling && !debtor.Inmate().smuggleArranged;
                // One that covers it settles it (and is not counted twice).
                Pawn cred2 = s.Inmate(), debt2 = s.Inmate();
                InmateDebt small = DebtSystem.Open(cred2, debt2, DebtReason.Drugs, 1, 3);
                small.smuggling = true;
                ContrabandUtility.Give(debt2, drug, forDebt: true);
                bool settled = !EcoHas(s, small) && Held(cred2, drug) > 0;
                // A protection fee is not paid in kind: the debtor does not even arrange it.
                Pawn gang = s.GangMember(), client = s.Inmate();
                client.Inmate().familyCount = 2;
                InmateDebt fee = DebtSystem.Buy(gang, client);
                DebtResponse? arranged = DebtSystem.Respond(fee, client, gang, map, DebtResponse.Smuggle);
                bool feeUntouched = arranged == null && !fee.smuggling;
                if (!stays || !part || !settled || !feeUntouched)
                    problems0.Add($"economy delivery: contraband that merely turned up stayed with the debtor {stays}; a delivery that fell short part-paid ({part}: owes {d.amount} of 500 after {doses} doses worth {worth}); one that covered it settled ({settled}); a protection fee was not arranged in kind ({feeUntouched})");
            });
        }

        // ---- 10: a secret goal leaves no trace ------------------------------------------------------------------------------------------------------

        /// <summary>A hidden debt's goal that takes the place of an announced one says the old one was put aside (the file would only lose it); when the debt becomes known, the goal is announced then.</summary>
        private static void CheckSecretGoalLeavesNoTrace(Map map)
        {
            Guarded("economy: a secret goal leaves no trace", map, r =>
            {
                var s = new EconomyScene(map, r);
                Pawn creditor = s.Inmate(), debtor = s.Inmate();
                CompInmate dc = debtor.Inmate();
                ProgramDef program = RPWantDefOf.RP_Want_Diploma.programs.FirstOrDefault();
                var diploma = new InmateWant { def = RPWantDefOf.RP_Want_Diploma, formedTick = s.Now, announced = true, program = program, note = program?.label };
                dc.want = diploma;
                string setAside = EcoLine("RP_Chr_WantSetAside", debtor.LabelShort, Wants.Title(diploma));
                InmateDebt d = DebtSystem.Open(creditor, debtor, DebtReason.Drugs, 12, 3);
                bool replaced = dc.want is { def: var now } && now == RPWantDefOf.RP_Want_Debt && dc.want.secret && diploma.status == WantStatus.Lost;
                bool toldAside = s.Lines(setAside) == 1;
                // (Nothing says what replaced it.)
                bool quiet = s.g.story.entries.All(e => !e.text.Contains(d.creditorName) || !e.text.Contains(d.debtorName));
                DebtSystem.Notify_Searched(debtor, 1f);
                Wants.Refresh(debtor, dc);
                string formed = EcoLine("RP_Chr_WantFormed", debtor.LabelShort, Wants.Title(dc.want));
                bool announced = d.known && !dc.want.secret && s.Lines(formed) == 1;
                if (!replaced || !toldAside || !quiet || !announced)
                    problems0.Add($"economy goals: a hidden debt's goal replaced an announced goal (replaced {replaced}) without saying it was put aside ({toldAside}), gave the debt away in a line ({!quiet}) or was never announced once the debt was known ({announced})");
            });
        }

        // ---- 11: the statistics count debts, not renewals --------------------------------------------------------------------------------------------

        /// <summary>A protection fee paid again and again is one debt incurred and one paid (the first fee); its renewals are not new debts.</summary>
        private static void CheckProtectionRenewalsAreNotCountedAsDebts(Map map)
        {
            Guarded("economy: protection renewals are not new debts", map, r =>
            {
                var s = new EconomyScene(map, r);
                Pawn protector = s.GangMember(), client = s.Inmate();
                (int formed0, int paid0) = (s.g.stats.debtsFormed, s.g.stats.debtsPaid);
                InmateDebt fee = DebtSystem.Buy(protector, client);
                client.Inmate().cash = 100;
                int fees = 0;
                // (Paid as the daily pass pays it, without the pass: it would also sell protection to whoever else is afraid.)
                for (int i = 0; i < 3; i++)
                {
                    InmateDebt open = DebtSystem.OwedBy(client);
                    if (open == null) break;
                    DebtSystem.Pay(open, client, protector);
                    fees++;
                }
                bool renewals = DebtSystem.OwedBy(client) is { cycles: 3 };
                bool counted = s.g.stats.debtsFormed == formed0 + 1 && s.g.stats.debtsPaid == paid0 + 1;
                if (!renewals || !counted)
                    problems0.Add($"economy statistics: three fees paid ({renewals}: {fees} rounds) should count one debt incurred and one paid, not {s.g.stats.debtsFormed - formed0} and {s.g.stats.debtsPaid - paid0}");
            });
        }
    }
}
