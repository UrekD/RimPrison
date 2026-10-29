using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    public enum DebtReason : byte
    {
        Drugs,
        Cards,
        Protection,
    }

    /// <summary>Where an unpaid debt stands: not due yet, threatened, the debtor beaten, or the debtor's answer to the beating given (the next round comes after a spell).</summary>
    public enum DebtStage : byte
    {
        Current,
        Threatened,
        Beaten,
        Answered,
    }

    /// <summary>What a debtor does after a beating (see <see cref="DebtSystem.Respond"/>).</summary>
    public enum DebtResponse : byte
    {
        Custody,
        Smuggle,
        Informant,
    }

    /// <summary>
    /// Somebody owes somebody: drugs sold on credit, a lost card game, a few days of protection. Kept by thing id and name (like the
    /// chronicle), so it outlives neither party: it is dropped when either leaves (<see cref="DebtSystem.Notify_Leaving"/>) and written off when it
    /// cannot be collected for long (<see cref="DebtSystem.Daily"/>). Hidden from the
    /// warden until <see cref="known"/> (a tip, a search, a beating or a request for custody).
    /// </summary>
    public class InmateDebt : IExposable
    {
        public int id;
        public int creditorId, debtorId;
        public string creditorName, debtorName;
        /// <summary>What is still owed, and what was owed at first.</summary>
        public int amount, original;
        public int dueTick, formedTick;
        public DebtReason reason;
        public DebtStage stage;
        /// <summary>The next step of the schedule is due at this tick (meaning depends on the stage).</summary>
        public int nextTick;
        public int lastBeatingTick = -999999;
        public int beatings;
        /// <summary>The warden has heard of it (it may be shown and written in the chronicle).</summary>
        public bool known;
        /// <summary>The debtor arranged a smuggled delivery to pay it off, or turned informant over it.</summary>
        public bool smuggling, informant;
        /// <summary>Protection: how many fees have been paid before this one (the first fee is 0).</summary>
        public int cycles;
        /// <summary>The tick the creditor's side was first found unable to get at the debtor (custody, another wing, death row); <see cref="NotStuck"/> while they can.</summary>
        public int stuckSince = NotStuck;
        public const int NotStuck = int.MinValue;

        public bool Overdue => Find.TickManager.TicksGame >= dueTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref creditorId, "creditor");
            Scribe_Values.Look(ref debtorId, "debtor");
            Scribe_Values.Look(ref creditorName, "creditorName");
            Scribe_Values.Look(ref debtorName, "debtorName");
            Scribe_Values.Look(ref amount, "amount");
            Scribe_Values.Look(ref original, "original");
            Scribe_Values.Look(ref dueTick, "due");
            Scribe_Values.Look(ref formedTick, "formed");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref stage, "stage");
            Scribe_Values.Look(ref nextTick, "next");
            Scribe_Values.Look(ref lastBeatingTick, "lastBeating", -999999);
            Scribe_Values.Look(ref beatings, "beatings");
            Scribe_Values.Look(ref known, "known");
            Scribe_Values.Look(ref smuggling, "smuggling");
            Scribe_Values.Look(ref informant, "informant");
            Scribe_Values.Look(ref cycles, "cycles");
            Scribe_Values.Look(ref stuckSince, "stuckSince", NotStuck);
        }

        /// <summary>For the snapshot: everything saved, in one string.</summary>
        public string Describe() =>
            $"{id}:{creditorId}>{debtorId}:{creditorName}>{debtorName}:{amount}/{original}:{dueTick}:{formedTick}:{reason}:{stage}:{nextTick}:{lastBeatingTick}:{beatings}:{known}{smuggling}{informant}:{cycles}:{stuckSince}";
    }

    [DefOf]
    public static class RPDebtDefOf
    {
        public static StoryArcDef RP_Arc_Debt;
        public static MisconductDef RP_Gambling;
        public static ThoughtDef RP_DebtThreatened;
        public static ThoughtDef RP_BeatenForDebt;
        public static ThoughtDef RP_DebtSettled;
        public static ThoughtDef RP_DebtLeftToIt;
        public static ThoughtDef RP_WonAtCards;
        public static ThoughtDef RP_LostAtCards;
        public static ThoughtDef RP_Protected;

        static RPDebtDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDebtDefOf));
    }

    /// <summary>
    /// The inmate economy: who owes whom. Dealers sell on credit to the broke, a card game in the common room moves cash and leaves
    /// losers owing, a gang sells protection for a few days at a time. A debt is paid out of the debtor's cash (all they have, once it is due, or in
    /// full early when they can) and, when it is overdue, escalates on a schedule: a threat (tension, fear, a bad memory), then a
    /// beating (the creditor, or a gang mate, goes for them: a recorded fight with the debt as its cause; rate-limited per debt, never
    /// while the debtor is in protective custody, three at most before the creditor writes it off), then the debtor's answer: they ask
    /// for protective custody, arrange a smuggled delivery to pay with, or turn informant. The warden is asked at the beating
    /// (<see cref="DebtArc"/>). Everything is hidden until the warden hears of it. Debts are judged once a day (money, threats, answers);
    /// only the beating is tried hourly, and cards are played hourly within a common-room patch, one pass over the inmates.
    /// </summary>
    public static class DebtSystem
    {
        /// <summary>A dealer's price for one dose in cash, and what it costs on credit (the markup) and for how many days.</summary>
        public const int CreditMarkup = 2, CreditDays = 3;
        public const int MaxLoansPerCreditor = 3;
        public const int ProtectionFee = 12, ProtectionDays = 3;
        public const float ProtectionOfferChance = 0.35f, ProtectionFear = 4f;
        public const int CardDays = 2, CardMinPlayers = 3, CardMaxPlayers = 4, MinStake = 2, MaxStake = 6;
        public const float CardChance = 0.4f;
        /// <summary>How close free-time inmates stand to be at one game away from the common room (no tables there: three of them in a huddle will do).</summary>
        public const float HuddleRadius = 5f;
        /// <summary>A newcomer is fair game for a protection offer for this long after they are admitted.</summary>
        public const int NewcomerDays = 4;
        /// <summary>Days from the due date to the beating, from the beating to the debtor's answer, and between rounds; beatings before it is written off.</summary>
        public const int BeatingDelayDays = 2, AnswerDelayDays = 1, RepeatDays = 3, MaxBeatings = 3;
        /// <summary>The chance a turned-over cell, or a frisk that found something, turns up the notes of who owes whom.</summary>
        public const float CellSearchFind = 0.5f, FriskFind = 0.3f;
        public const float ThreatTension = 10f, ThreatFear = 20f, BeatingTension = 10f, BeatingFear = 25f;
        /// <summary>A debt the creditor's side cannot get at (custody, another wing, death row) is written off after this many days; any debt, this many days after it fell due.</summary>
        public const int UncollectableDays = 3, MaxOverdueDays = 21;

        private static int Now => Find.TickManager.TicksGame;

        public static List<InmateDebt> Debts => PrisonGame.Instance?.story.debts;

        public static string ReasonText(InmateDebt d) => ("RP_DebtReason_" + d.reason).Translate();

        private static SecurityLevelDef ProtectiveCustody => RPSecurityDefOf.RP_Protective;

        public static bool InCustody(CompInmate c) => c?.security != null && c.security == ProtectiveCustody;

        // ---- lookups ---------------------------------------------------------------------------------------------

        public static InmateDebt ById(int id)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null) return null;
            foreach (InmateDebt d in debts)
                if (d.id == id) return d;
            return null;
        }

        /// <summary>The debt this inmate owes (<paramref name="wantable"/>: one they could set a goal to clear, not a protection fee that renews).</summary>
        public static InmateDebt OwedBy(Pawn p, bool wantable = false)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null) return null;
            foreach (InmateDebt d in debts)
                if (d.debtorId == p.thingIDNumber && (!wantable || d.reason != DebtReason.Protection)) return d;
            return null;
        }

        private static int Loans(Pawn creditor)
        {
            int n = 0;
            foreach (InmateDebt d in Debts)
                if (d.creditorId == creditor.thingIDNumber) n++;
            return n;
        }

        /// <summary>Whether the first can put the second in debt: they owe nothing yet, and the lender has not lent too much.</summary>
        public static bool CanLend(Pawn creditor, Pawn debtor) =>
            Debts != null && creditor != debtor && OwedBy(debtor) == null && Loans(creditor) < MaxLoansPerCreditor;

        private static Pawn PawnOf(Map map, int id)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (p.thingIDNumber == id && !p.Dead) return p;
            return null;
        }

        /// <summary>Whether a gang's protection covers them now: a protection fee is open and not overdue, and the one paid can protect (it is paused while they cannot).</summary>
        public static InmateDebt ProtectionOf(Pawn p)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null) return null;
            foreach (InmateDebt d in debts)
            {
                if (d.debtorId != p.thingIDNumber || d.reason != DebtReason.Protection || d.stage != DebtStage.Current || d.Overdue) continue;
                if (p.MapHeld is { } map && PawnOf(map, d.creditorId) is { } protector && Protects(protector, p)) return d;
            }
            return null;
        }

        /// <summary>Whether this gang member can protect that inmate now: free (not in the hole, in protective custody or on death row) and on their wing.</summary>
        public static bool Protects(Pawn protector, Pawn client) =>
            protector.Inmate() is { activeFlag: true, gang: not null, forcedActivity: null } pc && !InCustody(pc) && DeathRowSystem.Ext(pc) == null
            && client.Inmate() is { } cc && pc.security == cc.security;

        /// <summary>Whether the creditor's side can still get at the debtor: neither is in protective custody or on death row, and they are on the same wing.</summary>
        private static bool Collectable(Pawn creditor, Pawn debtor)
        {
            CompInmate cc = creditor.Inmate(), dc = debtor.Inmate();
            return !InCustody(cc) && !InCustody(dc) && DeathRowSystem.Ext(cc) == null && DeathRowSystem.Ext(dc) == null && cc.security == dc.security;
        }

        /// <summary>The protection keeps this aggressor off them: they are covered, and the aggressor is not the one they pay (or in their gang).</summary>
        public static bool ProtectedFrom(Pawn victim, Pawn aggressor)
        {
            InmateDebt d = ProtectionOf(victim);
            if (d == null) return false;
            if (d.creditorId == aggressor.thingIDNumber) return false;
            CompInmate ac = aggressor.Inmate();
            if (ac?.gang == null) return true;
            foreach (Pawn o in victim.Map.mapPawns.PrisonersOfColonySpawned)
                if (o.thingIDNumber == d.creditorId) return o.Inmate()?.gang != ac.gang;
            return true;
        }

        /// <summary>Someone who arranged a smuggled delivery to pay a debt (the post and the visitors bring more).</summary>
        public static bool Smuggling(CompInmate c)
        {
            if (c?.Pawn is not { } p) return false;
            List<InmateDebt> debts = Debts;
            if (debts == null) return false;
            foreach (InmateDebt d in debts)
                if (d.debtorId == p.thingIDNumber && d.smuggling) return true;
            return false;
        }

        // ---- what the warden knows -------------------------------------------------------------------------------

        public static List<InmateDebt> Known() => Debts?.Where(d => d.known).ToList() ?? new List<InmateDebt>();

        /// <summary>The warden knows of a debt this inmate owes or is owed (what the tension cause "owing money" may be shown for).</summary>
        public static bool KnownAbout(CompInmate c)
        {
            if (c?.Pawn is not { } p) return false;
            List<InmateDebt> debts = Debts;
            if (debts == null) return false;
            foreach (InmateDebt d in debts)
                if (d.known && (d.debtorId == p.thingIDNumber || d.creditorId == p.thingIDNumber)) return true;
            return false;
        }

        /// <summary>
        /// The debt is no secret from now on: it may be shown, and the debtor's goal to clear it with it, and the memories of it read as what they are. Protection is sold only by
        /// a gang member, so what is said of it (who they sold it to, and who for) gives their gang away: it is known with the debt.
        /// </summary>
        private static void MarkKnown(InmateDebt d, Map map)
        {
            d.known = true;
            Concepts.Teach(Concepts.Debt);
            if (map == null) return;
            Pawn debtor = PawnOf(map, d.debtorId), creditor = PawnOf(map, d.creditorId);
            if (debtor?.Inmate()?.want is { secret: true } w && w.def == RPWantDefOf.RP_Want_Debt && w.count == d.id)
            {
                w.secret = false;
                // (Its "has a goal" line was never written: it is, now.)
                w.announced = false;
            }
            if (d.reason == DebtReason.Protection && creditor?.Inmate() is { } cc) GangUtility.Reveal(cc);
            EconomyThoughts.Reveal(debtor, creditor);
            EconomyThoughts.Reveal(creditor, debtor);
        }

        /// <summary>The warden hears of this debt: it may be shown from now on, and a chronicle line says so once.</summary>
        private static void Reveal(InmateDebt d, Map map)
        {
            if (d.known) return;
            MarkKnown(d, map);
            Pawn debtor = map == null ? null : PawnOf(map, d.debtorId), creditor = map == null ? null : PawnOf(map, d.creditorId);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_DebtKnown".Translate(d.debtorName, d.creditorName, Money.Format(d.amount), ReasonText(d)), debtor, creditor);
        }

        /// <summary>
        /// A snitch's tip names a debt the warden does not know: their own, or one among the people near them. <paramref name="own"/> false (a tasked informant asked about their target): never a debt of their own, not even
        /// one they owe the target. <paramref name="map"/>: the map the tip is about (null: the snitch's; a unit officer at home is on none, and the debt's want and the chronicle line need the pawns).
        /// </summary>
        public static string TryTip(Pawn snitch, List<Pawn> near, bool own = true, Map map = null)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null) return null;
            foreach (InmateDebt d in debts)
            {
                if (d.known || d.creditorId == snitch.thingIDNumber || !own && d.debtorId == snitch.thingIDNumber) continue;
                bool about = own && d.debtorId == snitch.thingIDNumber;
                for (int i = 0; i < near.Count && !about; i++)
                    about = near[i].thingIDNumber == d.debtorId || near[i].thingIDNumber == d.creditorId;
                if (!about) continue;
                Reveal(d, map ?? snitch.Map);
                return "RP_TipDebt".Translate(d.debtorName, d.creditorName, Money.Format(d.amount), ReasonText(d));
            }
            return null;
        }

        /// <summary>A search of their cell or pockets: the notes of who owes whom may turn up.</summary>
        public static void Notify_Searched(Pawn p, float chance)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null || p.Map == null) return;
            foreach (InmateDebt d in debts)
            {
                if (d.known || d.debtorId != p.thingIDNumber && d.creditorId != p.thingIDNumber || !Rand.Chance(chance)) continue;
                Reveal(d, p.Map);
                Messages.Message("RP_DebtFoundInSearch".Translate(p.LabelShort, d.debtorName, d.creditorName, Money.Format(d.amount), ReasonText(d)), p, MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        // ---- opening, paying, closing ----------------------------------------------------------------------------

        public static InmateDebt Open(Pawn creditor, Pawn debtor, DebtReason reason, int amount, int dueDays, int cycles = 0, bool known = false)
        {
            PrisonGame game = PrisonGame.Instance;
            var d = new InmateDebt
            {
                id = game.story.nextDebtId++,
                creditorId = creditor.thingIDNumber,
                debtorId = debtor.thingIDNumber,
                creditorName = creditor.LabelShort,
                debtorName = debtor.LabelShort,
                amount = amount,
                original = amount,
                formedTick = Now,
                dueTick = Now + dueDays * GenDate.TicksPerDay,
                reason = reason,
                cycles = cycles,
                known = known,
            };
            game.story.debts.Add(d);
            // (A protection fee's renewal goes on from the one before: it is not another debt.)
            if (cycles == 0) game.stats.debtsFormed++;
            // A debt can become a goal: to clear it (and, once they are beaten for it, a grudge).
            if (reason != DebtReason.Protection && debtor.Inmate() is { } dc) Wants.Form(debtor, dc, RPWantDefOf.RP_Want_Debt);
            return d;
        }

        /// <summary>Moves cash from the debtor to the creditor (never more than they have or owe); the debt is closed when nothing is left.</summary>
        public static int Pay(InmateDebt d, Pawn debtor, Pawn creditor)
        {
            CompInmate dc = debtor.Inmate(), cc = creditor.Inmate();
            if (dc == null || cc == null) return 0;
            int pay = Mathf.Min(dc.cash, d.amount);
            if (pay <= 0) return 0;
            dc.cash -= pay;
            cc.cash += pay;
            d.amount -= pay;
            if (d.amount <= 0) Settled(d, debtor, creditor);
            // Part of it buys the debtor a day.
            else d.nextTick = Mathf.Max(d.nextTick, Now + GenDate.TicksPerDay);
            return pay;
        }

        private static void Settled(InmateDebt d, Pawn debtor, Pawn creditor)
        {
            PrisonGame game = PrisonGame.Instance;
            if (d.cycles == 0) game.stats.debtsPaid++;
            CompInmate dc = debtor.Inmate();
            bool goalLine = false;
            if (dc != null)
            {
                if (d.stage != DebtStage.Current)
                {
                    dc.tension = Mathf.Max(0f, dc.tension - 10f);
                    dc.fear = Mathf.Max(0f, dc.fear - 10f);
                    EconomyThoughts.Gain(debtor, RPDebtDefOf.RP_DebtSettled, null, d.known);
                }
                // (Before the debt goes: the goal's line is written only if the warden knew of it.)
                if (dc.want is { Resolved: false } w && w.def == RPWantDefOf.RP_Want_Debt && w.count == d.id)
                {
                    goalLine = !w.secret;
                    Wants.Fulfil(debtor, dc, w);
                }
            }
            game.story.debts.Remove(d);
            if (d.known && !goalLine && d.reason != DebtReason.Protection)
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_DebtPaid".Translate(d.debtorName, d.creditorName, Money.Format(d.original)), debtor, creditor);
            // The protection goes on for as long as it is paid (and the one paid can protect): the next fee.
            if (d.reason == DebtReason.Protection && !InCustody(dc) && Protects(creditor, debtor))
            {
                EconomyThoughts.Gain(debtor, RPDebtDefOf.RP_Protected, creditor, d.known);
                Open(creditor, debtor, DebtReason.Protection, d.original, ProtectionDays, d.cycles + 1, d.known);
            }
        }

        /// <summary>The debt is let go (the creditor gives up, a party left): no money moves.</summary>
        private static void Drop(InmateDebt d, string lineKey, Map map)
        {
            PrisonGame.Instance.story.debts.Remove(d);
            if (!d.known || lineKey == null) return;
            Pawn debtor = map == null ? null : PawnOf(map, d.debtorId), creditor = map == null ? null : PawnOf(map, d.creditorId);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, lineKey.Translate(d.debtorName, d.creditorName, Money.Format(d.amount)), debtor, creditor);
        }

        /// <summary>Someone left for good (released, transferred, dead, escaped): every debt they were part of is dropped (a debtor still here is freed, and says so only if the warden knew).</summary>
        public static void Notify_Leaving(Pawn p)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null) return;
            for (int i = debts.Count - 1; i >= 0; i--)
                if (debts[i].debtorId == p.thingIDNumber || debts[i].creditorId == p.thingIDNumber) Drop(debts[i], "RP_Chr_DebtDropped", p.MapHeld);
        }

        // ---- the daily pass --------------------------------------------------------------------------------------

        /// <summary>Daily: debts are paid, overdue ones escalate (a threat, the answer to a beating), and the gangs offer protection.</summary>
        public static void Daily(Map map)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null) return;
            var inmates = new Dictionary<int, Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (!p.Dead && p.Inmate() != null) inmates[p.thingIDNumber] = p;
            int now = Now;
            for (int i = debts.Count - 1; i >= 0; i--)
            {
                if (i >= debts.Count) continue;
                InmateDebt d = debts[i];
                // A party who is no longer here (discarded without a farewell): the backstop of Notify_Leaving.
                if (!inmates.TryGetValue(d.debtorId, out Pawn debtor) || !inmates.TryGetValue(d.creditorId, out Pawn creditor))
                {
                    Drop(d, "RP_Chr_DebtDropped", map);
                    continue;
                }
                CompInmate dc = debtor.Inmate();
                bool shielded = InCustody(dc);
                // In protective custody nobody sells them protection any more, and nobody who cannot protect them (in the hole, in custody, on another wing) goes on selling it.
                if (d.reason == DebtReason.Protection && (shielded || d.stage == DebtStage.Current && !Protects(creditor, debtor)))
                {
                    debts.Remove(d);
                    continue;
                }
                if ((d.Overdue || d.reason != DebtReason.Protection && dc.cash >= d.amount) && dc.cash > 0) Pay(d, debtor, creditor);
                if (!debts.Contains(d)) continue;
                // A debt nobody can collect ends: the creditor's side cannot get at the debtor for days on end, or it has been overdue for weeks.
                if (Collectable(creditor, debtor)) d.stuckSince = InmateDebt.NotStuck;
                else if (d.stuckSince == InmateDebt.NotStuck) d.stuckSince = now;
                if (d.stuckSince != InmateDebt.NotStuck && now - d.stuckSince >= UncollectableDays * GenDate.TicksPerDay || now - d.dueTick >= MaxOverdueDays * GenDate.TicksPerDay)
                {
                    Drop(d, "RP_Chr_DebtWrittenOff", map);
                    continue;
                }
                if (shielded) continue;
                switch (d.stage)
                {
                    case DebtStage.Current:
                        if (d.Overdue) Threaten(d, debtor, creditor);
                        break;
                    case DebtStage.Beaten:
                        if (now >= d.nextTick) Respond(d, debtor, creditor, map);
                        break;
                    case DebtStage.Answered:
                        // The next round: a new threat, and the beating follows when the cooldown allows (it is due as soon as it does).
                        if (now >= d.nextTick)
                        {
                            Threaten(d, debtor, creditor);
                            d.nextTick = now;
                        }
                        break;
                }
            }
            OfferProtection(map);
        }

        private static void Threaten(InmateDebt d, Pawn debtor, Pawn creditor)
        {
            CompInmate dc = debtor.Inmate();
            d.stage = DebtStage.Threatened;
            d.nextTick = Now + BeatingDelayDays * GenDate.TicksPerDay;
            dc.tension = Mathf.Min(100f, dc.tension + ThreatTension);
            dc.mainCause = "RP_Cause_Debt";
            FearSystem.Scare(dc, ThreatFear);
            EconomyThoughts.Gain(debtor, RPDebtDefOf.RP_DebtThreatened, creditor, d.known);
            if (d.known) Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_DebtThreat".Translate(d.creditorName, d.debtorName, Money.Format(d.amount)), debtor, creditor);
        }

        // ---- the beating -----------------------------------------------------------------------------------------

        /// <summary>Hourly: an overdue debt's beating is tried (the creditor or a gang mate in the debtor's room), and cards are played (<paramref name="cards"/>: a test leaves them out).</summary>
        public static void Hourly(Map map, PrisonGame game, bool cards = true)
        {
            List<InmateDebt> debts = game.story.debts;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            int now = Now;
            for (int i = debts.Count - 1; i >= 0; i--)
            {
                if (i >= debts.Count) continue;
                InmateDebt d = debts[i];
                if (d.stage != DebtStage.Threatened || now < d.nextTick) continue;
                Pawn debtor = PawnOf(map, d.debtorId), creditor = PawnOf(map, d.creditorId);
                if (debtor == null || creditor == null) continue;
                // Worn out, the creditor writes it off after the third time.
                if (d.beatings >= MaxBeatings)
                {
                    Drop(d, "RP_Chr_DebtWrittenOff", map);
                    continue;
                }
                // Where no guard can see is preferred; a watched one only once the beating is half a day overdue.
                bool overdue = now - d.nextTick > 12 * GenDate.TicksPerHour;
                if (!overdue && TensionUtility.Watched(debtor, pm)) continue;
                TryBeat(d, debtor, creditor, map, pm);
            }
            if (cards) PlayCards(map, pm);
        }

        /// <summary>The creditor, else a member of their gang, who can get at the debtor now.</summary>
        private static Pawn Enforcer(Pawn creditor, Pawn debtor, Map map)
        {
            if (CanBeat(creditor, debtor)) return creditor;
            GangDef gang = creditor.Inmate()?.gang;
            if (gang == null) return null;
            foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
                if (o != debtor && o.Inmate()?.gang == gang && CanBeat(o, debtor)) return o;
            return null;
        }

        internal static bool CanBeat(Pawn e, Pawn debtor) =>
            InmateUtility.IsActiveInmate(e) && !e.Downed && !e.InMentalState && e.Awake() && !e.WorkTagIsDisabled(WorkTags.Violent) && e.Inmate() is { forcedActivity: null }
            && !HostageCrisis.IsHeld(e) && e.GetRoom() is { } room && room == debtor.GetRoom() && e.Position.InHorDistOf(debtor.Position, 15f)
            && e.CanReach(debtor, PathEndMode.Touch, Danger.Deadly);

        /// <summary>
        /// The beating: a targeted attack, on the record as a fight with the debt as its cause. Never in protective custody, not more than
        /// once in <see cref="RepeatDays"/> days per debt. Starts the arc that asks the warden.
        /// </summary>
        public static bool TryBeat(InmateDebt d, Pawn debtor, Pawn creditor, Map map, PrisonMap pm)
        {
            CompInmate dc = debtor.Inmate();
            int now = Now;
            if (dc == null || InCustody(dc) || now - d.lastBeatingTick < RepeatDays * GenDate.TicksPerDay) return false;
            if (!InmateUtility.IsActiveInmate(debtor) || debtor.Downed || debtor.InMentalState || HostageCrisis.IsHeld(debtor)) return false;
            Pawn enforcer = Enforcer(creditor, debtor, map);
            if (enforcer == null || !enforcer.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: debtor)) return false;
            ContrabandUtility.Notify_ViolenceStarting(enforcer);
            CompInmate ec = enforcer.Inmate();
            ec.mainCause = "RP_Cause_Debt";
            d.stage = DebtStage.Beaten;
            d.nextTick = now + AnswerDelayDays * GenDate.TicksPerDay;
            d.lastBeatingTick = now;
            d.beatings++;
            PrisonGame.Instance.stats.debtBeatings++;
            // On the record: a fight (loud, so seen), with the debt as its cause.
            MisconductUtility.Record(enforcer, ec, pm, RPMisconductDefOf.RP_Fight, chronicle: false);
            MarkKnown(d, map);
            Story.Record(RPChronicleDefOf.RP_Chr_Violence, 2, "RP_Chr_DebtBeating".Translate(enforcer.LabelShort, debtor.LabelShort, Money.Format(d.amount), ReasonText(d)), enforcer, debtor, creditor);
            dc.tension = Mathf.Min(100f, dc.tension + BeatingTension);
            FearSystem.Scare(dc, BeatingFear);
            EconomyThoughts.Gain(debtor, RPDebtDefOf.RP_BeatenForDebt, enforcer, shown: true);
            WatchList.Notify_Trigger(debtor, RiskTrigger.DebtBeating);
            // A debt can become a grudge: they are enemies now, and the goal to clear the debt is a setback. It is a retry while it has setbacks to spare (a grudge does not
            // take its place until it is given up: RP_Want_Debt's keepsUntilGivenUp); anyone else is left holding a score.
            PrisonRelations.MakeEnemies(debtor, enforcer);
            if (dc.want is { Resolved: false } w && w.def == RPWantDefOf.RP_Want_Debt) Wants.Thwart(debtor, dc, w, "RP_Want_Reason_Beaten");
            Wants.Notify_Grudge(dc);
            // (After the goals, which name their own cause.)
            dc.mainCause = "RP_Cause_Debt";
            StaffMemories.Notify_BeatenInSight(debtor, dc);
            DebtArc.Begin(d, debtor, creditor, map);
            WardenAsks.Raise(debtor, WardenAskReason.Debtor);
            return true;
        }

        // ---- the debtor's answer ---------------------------------------------------------------------------------

        /// <summary>
        /// A beaten debtor who still owes does something about it, once: asks for protective custody, arranges a smuggled delivery to pay
        /// with, or turns informant (weighted by who they are). <paramref name="force"/> takes that one if it is possible. Returns what was
        /// done, or null when nothing was possible.
        /// </summary>
        public static DebtResponse? Respond(InmateDebt d, Pawn debtor, Pawn creditor, Map map, DebtResponse? force = null)
        {
            CompInmate dc = debtor.Inmate();
            d.stage = DebtStage.Answered;
            d.nextTick = Now + RepeatDays * GenDate.TicksPerDay;
            SecurityLevelDef pc = ProtectiveCustody;
            var options = new List<(DebtResponse what, float weight)>();
            // (As any request for it: whether there is room is the warden's to find out when they answer.)
            if (pc != null && !InCustody(dc) && Now - dc.lastPcRequestTick >= FearSystem.RequestCooldownTicks && DeathRowSystem.Ext(dc) == null)
                options.Add((DebtResponse.Custody, 1f + dc.fear / 50f));
            // (A protection fee comes round again: one delivery would not end it.)
            if (dc.familyCount > 0 && !dc.smuggleArranged && !d.smuggling && d.reason != DebtReason.Protection) options.Add((DebtResponse.Smuggle, 1f));
            if (!dc.HasReputation(ReputationDefOf.RP_Snitch) && !dc.gangLeader && !dc.snitchExposed && !ReputationDefOf.RP_Snitch.conflictsWith.Exists(r => dc.HasReputation(r)))
                options.Add((DebtResponse.Informant, 0.7f + (FearSystem.Vulnerable(dc) ? 1f : 0f)));
            if (force is { } forced) options.RemoveAll(o => o.what != forced);
            if (options.Count == 0) return null;
            DebtResponse chosen = options.RandomElementByWeight(o => o.weight).what;
            switch (chosen)
            {
                case DebtResponse.Custody:
                    // They tell the warden why: the debt is no secret now.
                    Reveal(d, map);
                    FearSystem.RequestProtectiveCustody(debtor, dc, "RP_DebtPcLabel".Translate(debtor.LabelShort),
                        "RP_DebtPcText".Translate(debtor.LabelShort, creditor.LabelShort, Money.Format(d.amount), ReasonText(d)));
                    break;
                case DebtResponse.Smuggle:
                    // The next visitor comes ready, and the post may hide something (see VisitationSystem.SmuggleChance, MailSystem.ContrabandChance).
                    dc.smuggleArranged = true;
                    d.smuggling = true;
                    break;
                default:
                    dc.reputations.Add(ReputationDefOf.RP_Snitch);
                    d.informant = true;
                    break;
            }
            return chosen;
        }

        /// <summary>
        /// Contraband brought in for an inmate who arranged a delivery to pay a debt (a visit, the post, a book, a guard on the take: see <c>ContrabandUtility.Give</c>): the creditor
        /// gets it, and it counts for what it is worth (<see cref="InKindValue"/>): the debt is settled if that covers it, else part-paid. The delivery is used up either way.
        /// </summary>
        public static void Notify_Smuggled(Pawn inmate, Thing item)
        {
            List<InmateDebt> debts = Debts;
            if (debts == null || item == null || inmate.Map == null) return;
            foreach (InmateDebt d in debts)
            {
                if (d.debtorId != inmate.thingIDNumber || !d.smuggling) continue;
                Pawn creditor = PawnOf(inmate.Map, d.creditorId);
                if (creditor?.inventory == null || !creditor.Spawned) continue;
                int count = Mathf.Min(item.stackCount, ContrabandUtility.Ext(item.def)?.stackCount ?? 1);
                int value = InKindValue(item.def, count);
                if (inmate.inventory.innerContainer.TryTransferToContainer(item, creditor.inventory.innerContainer, count) <= 0) continue;
                d.smuggling = false;
                if (inmate.Inmate() is { } ic) ic.smuggleArranged = false;
                int owed = d.amount;
                d.amount = Mathf.Max(0, d.amount - value);
                if (d.amount > 0)
                {
                    // Not enough: part of it buys the debtor a day.
                    d.nextTick = Mathf.Max(d.nextTick, Now + GenDate.TicksPerDay);
                    if (d.known) Messages.Message("RP_DebtPartPaidInKind".Translate(d.debtorName, d.creditorName, Money.Format(value), Money.Format(d.amount)), inmate, MessageTypeDefOf.NeutralEvent, historical: false);
                    return;
                }
                Settled(d, inmate, creditor);
                if (d.known) Messages.Message("RP_DebtPaidInKind".Translate(d.debtorName, d.creditorName, Money.Format(owed)), inmate, MessageTypeDefOf.NeutralEvent, historical: false);
                return;
            }
        }

        /// <summary>What a delivery is worth against a debt: its market value, as cash is.</summary>
        public static int InKindValue(ThingDef def, int count) => Mathf.Max(1, Mathf.RoundToInt(def.BaseMarketValue * count));

        // ---- cards -----------------------------------------------------------------------------------------------

        private static readonly Dictionary<int, List<Pawn>> tables = new Dictionary<int, List<Pawn>>();

        /// <summary>Who is sitting where: the free inmates standing in each common-room patch (patch index to the people there), one pass over the inmates. Shared, do not modify.</summary>
        public static Dictionary<int, List<Pawn>> Seating(Map map, PrisonMap pm)
        {
            tables.Clear();
            PrisonZoneDef room = RPDefOf.RP_CommonRoom;
            if (room == null) return tables;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || p.Downed || p.InMentalState || !p.Awake() || p.Inmate() is not { forcedActivity: null }) continue;
                if (pm.zones.ZoneAt(p.Position) != room) continue;
                int patch = pm.zones.PatchAt(p.Position);
                if (patch < 0) continue;
                if (!tables.TryGetValue(patch, out List<Pawn> seats)) tables[patch] = seats = new List<Pawn>();
                seats.Add(p);
            }
            return tables;
        }

        private static readonly List<List<Pawn>> huddles = new List<List<Pawn>>();
        private static readonly List<Pawn> loose = new List<Pawn>();

        /// <summary>Who is standing together on free time outside the common room (the yard, a wing, the canteen after a meal): groups of three or more in
        /// one room within <see cref="HuddleRadius"/> of one another (the common room has its tables, see <see cref="Seating"/>). Shared, do not modify.</summary>
        public static List<List<Pawn>> Huddles(Map map, PrisonMap pm)
        {
            huddles.Clear();
            loose.Clear();
            PrisonGame game = PrisonGame.Instance;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState && p.Awake() && p.Inmate() is { forcedActivity: null }
                    && HuddleZone(pm.zones.ZoneAt(p.Position)) && game.CurrentActivity(p) == RPDefOf.RP_FreeTime) loose.Add(p);
            for (int i = 0; i < loose.Count; i++)
            {
                if (loose[i] == null) continue;
                List<Pawn> group = null;
                for (int j = i + 1; j < loose.Count; j++)
                {
                    if (loose[j] == null || !loose[j].Position.InHorDistOf(loose[i].Position, HuddleRadius) || loose[j].GetRoom() != loose[i].GetRoom()) continue;
                    group ??= new List<Pawn> { loose[i] };
                    group.Add(loose[j]);
                }
                // (Only a group that is a table takes its members off the list: a pair that was thrown away must not take with it the neighbour who stands with two others.)
                if (group == null || group.Count < CardMinPlayers) continue;
                huddles.Add(group);
                for (int k = 1; k < group.Count; k++) loose[loose.IndexOf(group[k])] = null;
            }
            return huddles;
        }

        private static bool HuddleZone(PrisonZoneDef zone) => zone == null || !zone.staffOnly && zone != RPDefOf.RP_CommonRoom;

        /// <summary>Hourly: three or more of them at a common-room table, or huddled elsewhere on free time, may sit down to a game, if no guard is in sight of it.</summary>
        private static void PlayCards(Map map, PrisonMap pm)
        {
            foreach (List<Pawn> seats in Seating(map, pm).Values)
                TryDeal(seats, pm);
            foreach (List<Pawn> seats in Huddles(map, pm))
                TryDeal(seats, pm);
        }

        private static void TryDeal(List<Pawn> seats, PrisonMap pm)
        {
            if (seats.Count >= CardMinPlayers && Rand.Chance(CardChance) && Unwatched(seats, pm)) Deal(seats, pm);
        }

        /// <summary>No seat at the table is watched (<see cref="TensionUtility.Watched"/>: a guard in sight, a manned tower or one working camera is enough to stop a game; a game is never recorded as seen: see <see cref="Deal"/>).</summary>
        public static bool Unwatched(List<Pawn> seats, PrisonMap pm)
        {
            foreach (Pawn p in seats)
                if (TensionUtility.Watched(p, pm)) return false;
            return true;
        }

        /// <summary>Whether someone sits down to play: they have the stake, or can play on credit.</summary>
        private static bool Plays(Pawn p, CompInmate c, int stake) =>
            !HungerStrike.Fasting(c) && DeathRowSystem.Ext(c) == null && (c.cash >= stake || OwedBy(p) == null);

        /// <summary>
        /// One game among up to four of them: the best hand (their wits and their luck) takes the stake from each of the others, who pay what they
        /// have; what a loser cannot cover (two or more) becomes a debt to the winner, if they owe nothing yet. The winner is on the
        /// record gambling (unseen: the game is only played with no guard in sight of the table). Returns the winner, or null when too few sat down.
        /// </summary>
        public static Pawn Deal(List<Pawn> seats, PrisonMap pm, int stake = 0, Pawn forcedWinner = null)
        {
            if (stake <= 0) stake = Rand.RangeInclusive(MinStake, MaxStake);
            List<Pawn> players = seats.Where(p => p.Inmate() is { } c && Plays(p, c, stake)).InRandomOrder().Take(CardMaxPlayers).ToList();
            if (forcedWinner != null && players.Contains(forcedWinner) is false) players.Add(forcedWinner);
            if (players.Count < CardMinPlayers) return null;
            Pawn winner = forcedWinner ?? players.MaxBy(p => (p.skills?.GetSkill(SkillDefOf.Intellectual).Level ?? 0) + (p.skills?.GetSkill(SkillDefOf.Social).Level ?? 0) + Rand.Range(0f, 14f));
            CompInmate wc = winner.Inmate();
            foreach (Pawn loser in players)
            {
                if (loser == winner) continue;
                CompInmate lc = loser.Inmate();
                int paid = Mathf.Min(lc.cash, stake), short_ = stake - paid;
                lc.cash -= paid;
                wc.cash += paid;
                if (short_ >= 2 && CanLend(winner, loser)) Open(winner, loser, DebtReason.Cards, short_, CardDays);
                // (Nobody saw the game: the memory reads as something generic until a debt from it is known; see MarkKnown.)
                EconomyThoughts.Gain(loser, RPDebtDefOf.RP_LostAtCards, winner, shown: false);
                lc.tension = Mathf.Min(100f, lc.tension + 3f);
            }
            EconomyThoughts.Gain(winner, RPDebtDefOf.RP_WonAtCards, null, shown: false);
            PrisonGame.Instance.stats.cardGames++;
            // (Never seen: the game is only played when nothing watches the table, see Unwatched.)
            MisconductUtility.Record(winner, wc, pm, RPDebtDefOf.RP_Gambling, chronicle: false, unseen: true);
            return winner;
        }

        // ---- protection ------------------------------------------------------------------------------------------

        /// <summary>Daily, now and then: a gang member who is free offers a frightened or vulnerable inmate of their wing protection for a fee every few days, and they take it.</summary>
        public static InmateDebt OfferProtection(Map map, bool force = false)
        {
            if (!force && !Rand.Chance(ProtectionOfferChance)) return null;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate).ToList();
            Pawn protector = inmates.Where(p => p.Inmate() is { gang: not null, forcedActivity: null } c && Loans(p) < MaxLoansPerCreditor && !p.Downed && !p.InMentalState && !InCustody(c))
                .OrderByDescending(p => p.Inmate().gangLeader).ThenBy(_ => Rand.Value).FirstOrDefault();
            if (protector == null) return null;
            CompInmate pc = protector.Inmate();
            // (Fear is rare in a quiet prison: the one a gang always finds is the newcomer, who knows nobody and is told what the wing costs.)
            Pawn buyer = inmates.Where(o => o != protector && o.Inmate() is { } c && c.security == pc.security && c.gang != pc.gang && !GangUtility.AreRivals(c.gang, pc.gang) && !InCustody(c)
                                            && DeathRowSystem.Ext(c) == null && (c.fear >= 35f || FearSystem.Vulnerable(c) || Newcomer(c)) && OwedBy(o) == null)
                .OrderByDescending(o => o.Inmate().fear).ThenByDescending(o => o.Inmate().admittedTick).FirstOrDefault();
            return buyer == null ? null : Buy(protector, buyer);
        }

        /// <summary>Admitted a few days ago, and in no gang yet.</summary>
        private static bool Newcomer(CompInmate c) => c.gang == null && Now - c.admittedTick < NewcomerDays * GenDate.TicksPerDay;

        /// <summary>The buyer pays the protector for protection from now: the first fee is due in a few days.</summary>
        public static InmateDebt Buy(Pawn protector, Pawn buyer)
        {
            InmateDebt d = Open(protector, buyer, DebtReason.Protection, ProtectionFee, ProtectionDays);
            CompInmate bc = buyer.Inmate();
            bc.fear = Mathf.Max(0f, bc.fear - 15f);
            EconomyThoughts.Gain(buyer, RPDebtDefOf.RP_Protected, protector, d.known);
            return d;
        }
    }

    /// <summary>
    /// A debtor has been beaten. Move them away from the creditor (a bed in another wing, else another cell), pay it off from their own
    /// cash, or let it run (they remember that nobody helped). No answer in 12 hours lets it run.
    /// </summary>
    public class DebtArc : StoryArcWorker
    {
        private static readonly string[] Keys = { "RP_Debt_Move", "RP_Debt_Pay", "RP_Debt_Apart", "RP_Debt_LetRun" };

        public static StoryArc Begin(InmateDebt d, Pawn debtor, Pawn creditor, Map map)
        {
            StoryArc arc = PrisonGame.Instance.story.TryStartArc(RPDebtDefOf.RP_Arc_Debt, debtor, creditor);
            if (arc == null) return null;
            arc.count = d.id;
            arc.note = DebtSystem.ReasonText(d);
            ((DebtArc)arc.def.Worker).AskWarden(arc, map, "RP_DebtArcLabel".Translate(debtor.LabelShort),
                "RP_DebtArcText".Translate(debtor.LabelShort, creditor.LabelShort, Money.Format(d.amount), arc.note, Money.Format(debtor.Inmate().cash)));
            return arc;
        }

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override int TimeoutChoice(StoryArc arc) => 3;

        public override string Disabled(StoryArc arc, int option)
        {
            InmateDebt d = DebtSystem.ById(arc.count);
            if (d == null) return null;
            if (option == 2)
            {
                if (arc.A == null || arc.B == null || !KeepApart.Orderable(arc.A) || !KeepApart.Orderable(arc.B)) return "RP_DebtApartGone".Translate();
                if (KeepApart.Ordered(arc.A, arc.B)) return "RP_DebtApartAlready".Translate(arc.B.LabelShort);
                return PrisonGame.Instance.keepApart.Full ? "RP_KeepApartFull".Translate(KeepApartBook.Cap) : null;
            }
            if (option == 0 && AwayBed(arc) == null) return "RP_DebtNoBed".Translate();
            if (option == 1 && (arc.A?.Inmate()?.cash ?? 0) < d.amount) return "RP_DebtCannotPay".Translate(Money.Format(d.amount));
            return null;
        }

        /// <summary>A free bed for the debtor away from the creditor: in another wing if there is one, else another cell.</summary>
        public static Building_Bed AwayBed(StoryArc arc)
        {
            Pawn debtor = arc.A, creditor = arc.B;
            CompInmate dc = debtor?.Inmate(), cc = creditor?.Inmate();
            if (dc?.security == null || cc == null || debtor.MapHeld == null) return null;
            Building_Bed creditorBed = Cellmates.HomeBed(creditor, cc);
            SecurityLevelDef creditorWing = IntakeSystem.WingOf(creditorBed);
            Room creditorCell = creditorBed?.GetRoom(), ownCell = Cellmates.Home(debtor, dc);
            List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(debtor.MapHeld, dc.security, debtor.gender).Where(b => b.GetRoom() is { } r && r != creditorCell && r != ownCell && !KeepApart.RoomHoldsPartner(b, debtor)).ToList();
            List<Building_Bed> otherWing = free.Where(b => IntakeSystem.WingOf(b) != creditorWing).ToList();
            return Cellmates.PickBed(otherWing.Count > 0 ? otherWing : free, dc);
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            Pawn debtor = arc.A, creditor = arc.B;
            InmateDebt d = DebtSystem.ById(arc.count);
            CompInmate dc = debtor?.Inmate();
            if (d == null || dc == null || creditor == null) return;
            switch (option)
            {
                case 0:
                    if (AwayBed(arc) is not { } bed || !Cellmates.Move(debtor, dc, bed))
                    {
                        Messages.Message("RP_DebtNoBed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                        return;
                    }
                    Cellmates.PlacedByWarden(dc);
                    // They need days to find them again.
                    (d.stage, d.nextTick) = (DebtStage.Answered, Find.TickManager.TicksGame + DebtSystem.RepeatDays * GenDate.TicksPerDay);
                    Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_DebtMoved".Translate(debtor.LabelShort, creditor.LabelShort), debtor, creditor);
                    break;
                case 1:
                    if (dc.cash < d.amount)
                    {
                        Messages.Message("RP_DebtCannotPay".Translate(Money.Format(d.amount)), MessageTypeDefOf.RejectInput, historical: false);
                        return;
                    }
                    DebtSystem.Pay(d, debtor, creditor);
                    break;
                case 2:
                    // (The debt and its schedule stay as they are: the order is for the placement, which puts them in other cells and wings as the beds allow, and its line says when they cannot be.)
                    if (KeepApart.Add(debtor, creditor, "debt") != KeepApartResult.Added) return;
                    break;
                default:
                    EconomyThoughts.Gain(debtor, RPDebtDefOf.RP_DebtLeftToIt, null, shown: true);
                    break;
            }
            arc.done = true;
        }

        public override void Hourly(StoryArc arc, Map map)
        {
            // The debt is gone (paid, dropped) or nobody answered for two days.
            if (DebtSystem.ById(arc.count) == null || Find.TickManager.TicksGame - arc.startTick > 2 * GenDate.TicksPerDay) arc.done = true;
        }
    }

    /// <summary>The goal to clear a debt: progress is the share paid, a beating is a setback, and a goal given up for good turns into a grudge.</summary>
    public class WantWorker_Debt : InmateWantWorker
    {
        public override bool CanForm(Pawn p, CompInmate c) => DebtSystem.OwedBy(p, wantable: true) != null;

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            if (DebtSystem.OwedBy(p, wantable: true) is not { } d) return;
            // (Not a target: a goal against a person sends their outbursts at them, and this is only a debt.)
            w.count = d.id;
            w.secret = !d.known;
            w.baseline = d.original;
            w.note = "RP_Want_DebtNote".Translate(d.creditorName, Money.Format(d.original));
        }

        public override bool StillApplies(Pawn p, CompInmate c, InmateWant w) => DebtSystem.ById(w.count) is { } d && d.debtorId == p.thingIDNumber;

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            if (DebtSystem.ById(w.count) is not { } d) return;
            w.progress = 1f - d.amount / (float)Mathf.Max(1, d.original);
            w.status = d.stage == DebtStage.Current && !d.Overdue ? WantStatus.OnTrack : WantStatus.Slipping;
        }

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w) =>
            DebtSystem.ById(w.count) is { } d ? "RP_Want_Debt_Detail".Translate(d.creditorName, Money.Format(d.amount), Money.Format(c.cash)) : "";
    }
}
