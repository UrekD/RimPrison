using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>What an inmate with a reputation actively does (hourly, while free to act).</summary>
    public abstract class ReputationBehavior
    {
        public ReputationDef def;

        public abstract void Hourly(Pawn p, CompInmate c, PrisonMap pm);

        protected static bool Unwatched(Pawn p, PrisonMap pm) => !TensionUtility.Watched(p, pm);

        /// <summary>An awake, free inmate within <paramref name="radius"/> matching the validator.</summary>
        protected static Pawn InmateNear(Pawn p, float radius, System.Func<Pawn, CompInmate, bool> validator)
        {
            IReadOnlyList<Pawn> inmates = p.Map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                if (o == p || !o.Awake() || o.Downed || o.InMentalState || !o.Position.InHorDistOf(p.Position, radius)) continue;
                if (o.Inmate() is not { activeFlag: true } oc || !validator(o, oc)) continue;
                if (o.GetRoom() != p.GetRoom()) continue;
                return o;
            }
            return null;
        }
    }

    /// <summary>Dealer: sells smuggled drugs to addicts and the stressed, away from the guards. Visitors bring drugs.</summary>
    public class ReputationBehavior_Dealer : ReputationBehavior
    {
        public const int Price = 6;

        public override void Hourly(Pawn p, CompInmate c, PrisonMap pm)
        {
            // (Dealers deal more on their own gang's turf.)
            if (!Rand.Chance(GangTurf.OnOwnTurf(p, c, pm) ? GangTurf.DealChanceOwnTurf : GangTurf.DealChance) || !Unwatched(p, pm)) return;
            Deal(p, c, pm);
        }

        /// <summary>
        /// One sale, if they have a drug and someone close wants it: for cash, else (to the broke, who owe nobody) on credit, at double the
        /// price, due in a few days: that is a debt (see <see cref="DebtSystem"/>).
        /// </summary>
        public bool Deal(Pawn p, CompInmate c, PrisonMap pm)
        {
            Thing drug = null;
            foreach (Thing t in p.inventory.innerContainer)
                if (ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Drug }) { drug = t; break; }
            if (drug == null) return false;
            Pawn buyer = InmateNear(p, 6f, (o, oc) => oc.cash >= Price && !oc.HasReputation(def) && (oc.tension >= 35f || HasAddiction(o)));
            bool credit = false;
            if (buyer == null)
            {
                buyer = InmateNear(p, 6f, (o, oc) => oc.cash < Price && !oc.HasReputation(def) && (oc.tension >= 35f || HasAddiction(o)) && DebtSystem.CanLend(p, o));
                credit = buyer != null;
            }
            if (buyer == null) return false;
            Thing sold = drug.SplitOff(1);
            if (!buyer.inventory.innerContainer.TryAdd(sold))
            {
                p.inventory.innerContainer.TryAdd(sold);
                return false;
            }
            CompInmate bc = buyer.Inmate();
            bc.lastDealerId = p.thingIDNumber;
            if (credit) DebtSystem.Open(p, buyer, DebtReason.Drugs, Price * DebtSystem.CreditMarkup, DebtSystem.CreditDays);
            else
            {
                bc.cash -= Price;
                c.cash += Price;
            }
            PrisonGame.Instance.stats.drugDeals++;
            MisconductUtility.Record(p, c, pm, RPReputationDefOf.RP_Dealing);
            return true;
        }

        internal static bool HasAddiction(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs) if (h is Hediff_Addiction) return true;
            return false;
        }
    }

    /// <summary>Extortionist: leans on weaker inmates for their cash. Victims grow tense; some fight back.</summary>
    public class ReputationBehavior_Extortionist : ReputationBehavior
    {
        public override void Hourly(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (!Rand.Chance(0.35f) || !Unwatched(p, pm)) return;
            Extort(p, c, pm);
        }

        /// <summary>Takes money from a weaker inmate close by, unless a gang's protection covers them from this one.</summary>
        public bool Extort(Pawn p, CompInmate c, PrisonMap pm)
        {
            int melee = p.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 5;
            Pawn victim = InmateNear(p, 4f, (o, oc) => oc.cash >= 3 && !oc.HasReputation(def) && (c.gang == null || oc.gang != c.gang)
                                                         && !DebtSystem.ProtectedFrom(o, p) && (o.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 5) < melee);
            if (victim == null) return false;
            CompInmate vc = victim.Inmate();
            int take = Mathf.Min(vc.cash, 10);
            vc.cash -= take;
            c.cash += take;
            vc.tension = Mathf.Min(100f, vc.tension + 8f);
            vc.mainCause = "RP_Cause_Extorted";
            FearSystem.Scare(vc, 15f);
            victim.needs.mood?.thoughts.memories.TryGainMemory(RPReputationDefOf.RP_Extorted, p);
            PrisonGame.Instance.stats.extortions++;
            MisconductUtility.Record(p, c, pm, RPReputationDefOf.RP_Extortion);
            // Some victims have had enough.
            if (Rand.Chance(0.15f) && victim.interactions.SocialFightPossible(p)) victim.interactions.StartSocialFight(p);
            return true;
        }
    }

    /// <summary>
    /// Jailhouse lawyer: files appeals (for themselves or another inmate; the prison pays court costs, some succeed and
    /// cut the sentence) and, when the prison is badly run, sues it over conditions.
    /// </summary>
    public class ReputationBehavior_Lawyer : ReputationBehavior
    {
        public const int CourtCosts = 150;
        public const int ConditionsSettlement = 800;

        public override void Hourly(Pawn p, CompInmate c, PrisonMap pm)
        {
            // About once every ten days.
            if (!Rand.MTBEventOccurs(10f, GenDate.TicksPerDay, GenDate.TicksPerHour)) return;
            PrisonGame game = PrisonGame.Instance;
            if (game.lastGrade >= 0f && game.lastGrade < 50f && Rand.Chance(0.4f))
            {
                game.ledger.Add(RPDefOf.RP_Fines, -ConditionsSettlement, "RP_LawsuitNote".Translate(p.LabelShort));
                game.reputation = Mathf.Max(0f, game.reputation - 2f);
                game.stats.lawsuits++;
                Find.LetterStack.ReceiveLetter("RP_LawsuitLabel".Translate(), "RP_LawsuitText".Translate(p.LabelShort, Money.Format(ConditionsSettlement), PrisonGrading.Letter(game.lastGrade)), LetterDefOf.NegativeEvent, p);
                return;
            }
            Pawn client = Rand.Chance(0.5f) && !c.IsLifer && c.TermDays > 3 ? p
                : InmateNear(p, 30f, (o, oc) => !oc.IsLifer && oc.TermDays > 3 && DeathRowSystem.Ext(oc) == null);
            if (client == null) return;
            CompInmate cc = client.Inmate();
            game.ledger.Add(RPDefOf.RP_Fines, -CourtCosts, "RP_AppealCostsNote".Translate(client.LabelShort));
            game.stats.appealsFiled++;
            if (Rand.Chance(0.3f))
            {
                int before = cc.TermDays;
                int cut = cc.CutTerm(Mathf.Max(1, Mathf.RoundToInt(before * 0.25f)));
                Messages.Message("RP_LawyerWon".Translate(p.LabelShort, client.LabelShort, cut), client, MessageTypeDefOf.NeutralEvent);
            }
            else Messages.Message("RP_LawyerLost".Translate(p.LabelShort, client.LabelShort, Money.Format(CourtCosts)), client, MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }

    [DefOf]
    public static class RPReputationDefOf
    {
        public static MisconductDef RP_Dealing;
        public static MisconductDef RP_Extortion;
        public static ThoughtDef RP_Extorted;
        public static ReputationDef RP_Extortionist;
        public static ReputationDef RP_JailhouseLawyer;

        static RPReputationDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPReputationDefOf));
    }
}
