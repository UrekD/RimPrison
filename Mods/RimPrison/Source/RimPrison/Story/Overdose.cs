using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPOverdoseDefOf
    {
        public static ThoughtDef RP_SawOverdose;
        public static ThoughtDef RP_GotHelp;

        static RPOverdoseDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPOverdoseDefOf));
    }

    /// <summary>
    /// An overdose on the wing (vanilla's drug overdose on an inmate): the wing saw it, and the warden decides whether
    /// to go after whoever sold the dose, get the user help, or note it and move on.
    /// </summary>
    public static class Overdoses
    {
        public const float Severity = 0.5f;
        private const int RepeatTicks = 5 * GenDate.TicksPerDay;

        public static void Hourly(Map map)
        {
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c || now - c.lastOverdoseTick < RepeatTicks) continue;
                if (p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.DrugOverdose) is not { Severity: >= Severity }) continue;
                Notify_Overdose(p, c, map);
                return; // one an hour is plenty
            }
        }

        public static void Notify_Overdose(Pawn p, CompInmate c, Map map)
        {
            c.lastOverdoseTick = Find.TickManager.TicksGame;
            PrisonGame.Instance.stats.overdoses++;
            Room wing = p.GetRoom();
            foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
                if (o != p && o.Inmate() is { activeFlag: true } && (o.GetRoom() == wing || o.Position.InHorDistOf(p.Position, 12f)))
                    o.needs.mood?.thoughts.memories.TryGainMemory(RPOverdoseDefOf.RP_SawOverdose);
            Pawn dealer = DealerOf(c, map);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_Overdose".Translate(p.LabelShort), p);
            Wants.Notify_Overdose(p, c);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Overdose", "RP_OverdoseLabel".Translate(p.LabelShort),
                "RP_OverdoseText".Translate(p.LabelShort, dealer != null ? "RP_OverdoseDealerKnown".Translate(dealer.LabelShort) : "RP_OverdoseDealerUnknown".Translate()), map, p);
            letter.pawns.Add(p);
            letter.amount = p.thingIDNumber; // the victim; pawns are compacted on save, so never go by position
            if (dealer != null) letter.pawns.Add(dealer);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>The last inmate who sold them drugs, if still here.</summary>
        public static Pawn DealerOf(CompInmate c, Map map) =>
            c.lastDealerId < 0 ? null : map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(o => o.thingIDNumber == c.lastDealerId && o.Inmate() is { activeFlag: true });
    }

    public class OverdoseDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Overdose_Hunt", "RP_Overdose_Help", "RP_Overdose_Note" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        private static Pawn Victim(ChoiceLetter_PrisonDecision l) => l.pawns.FirstOrDefault(x => x.thingIDNumber == l.amount);

        private static Pawn Dealer(ChoiceLetter_PrisonDecision l) =>
            l.pawns.FirstOrDefault(x => x.thingIDNumber != l.amount && x.Spawned && InmateUtility.IsActiveInmate(x));

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && Dealer(l) == null && !PrisonGame.Instance.ShakedownReady ? "RP_OverdoseNoLead".Translate().ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option == 0)
            {
                // A named dealer is searched and their trade becomes known; without a name, the wing is turned over.
                if (Dealer(l) is { } dealer && dealer.Inmate() is { } dc)
                {
                    dc.suspected = true;
                    foreach (ReputationDef r in dc.reputations)
                        if (r.behaviorClass == typeof(ReputationBehavior_Dealer) && !dc.revealed.Contains(r)) dc.revealed.Add(r);
                    Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_DealerNamed".Translate(dealer.LabelShort, Victim(l)?.LabelShort ?? "?"), dealer);
                }
                else PrisonGame.Instance.StartShakedown();
            }
            else if (option == 1 && Victim(l) is { } p && InmateUtility.IsActiveInmate(p) && p.Inmate() is { } c)
            {
                c.tension = UnityEngine.Mathf.Max(0f, c.tension - 10f);
                p.needs.mood?.thoughts.memories.TryGainMemory(RPOverdoseDefOf.RP_GotHelp);
                // Referred to substance abuse treatment: first seat at the next session.
                ProgramDef rehab = RPProgramDefOf.RP_SubstanceAbuse;
                if (c.referral == null && !c.passedPrograms.Contains(rehab))
                {
                    c.referral = rehab;
                    if (!PrisonGame.Instance.programs.IsRunning(rehab)) Messages.Message("RP_ReferredNotRunning".Translate(p.LabelShort, rehab.label), p, MessageTypeDefOf.CautionInput, historical: false);
                }
            }
        }
    }
}
