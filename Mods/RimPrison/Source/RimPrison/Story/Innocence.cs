using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPInnocenceDefOf
    {
        public static ThoughtDef RP_AppealDenied;
        public static ThoughtDef RP_BackedByWarden;

        static RPInnocenceDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPInnocenceDefOf));
    }

    /// <summary>
    /// Some inmates did not do it (rolled at intake, hidden). Anyone may insist they are innocent; the warden can back
    /// the appeal or stay out of it. The letter carries a clue that is right about 70% of the time. Backed appeals of
    /// the innocent usually succeed (exoneration, reputation); backing a guilty one costs the board's trust; an
    /// innocent left to fight alone sometimes wins anyway, and then the papers ask why the warden did nothing.
    /// </summary>
    public static class Innocence
    {
        public const float Chance = 0.03f, ClueAccuracy = 0.7f, BackedWin = 0.75f, AloneWin = 0.25f;

        /// <summary>
        /// Their appeal is due and the court can hear it now: not mid-riot or the like (the court can wait an hour), and not on remand (an open murder case: the appeal waits until that
        /// court has sat, and only its acquittal frees them).
        /// </summary>
        internal static bool Ready(PrisonGame game, Pawn p) =>
            p.Inmate() is { appealResolveTick: >= 0 } c && Find.TickManager.TicksGame >= c.appealResolveTick && InmateUtility.IsActiveInmate(p) && !Trials.OnRemand(game, p);

        public static void Hourly(PrisonGame game, Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!Ready(game, p)) continue;
                CompInmate c = p.Inmate();
                c.appealResolveTick = -1;
                Resolve(game, p, c);
                return; // the list changes on a release
            }
        }

        public static void Resolve(PrisonGame game, Pawn p, CompInmate c)
        {
            bool backed = c.appealBacked;
            if (c.innocent && Rand.Chance(backed ? BackedWin : AloneWin))
            {
                game.stats.exonerations++;
                game.reputation = Mathf.Clamp(game.reputation + (backed ? 3f : -3f), 0f, 100f);
                Story.Record(RPChronicleDefOf.RP_Chr_Release, 3, (backed ? "RP_Chr_ExoneratedBacked" : "RP_Chr_ExoneratedAlone").Translate(p.LabelShort, c.DaysServed.ToString("F0")), p);
                Find.LetterStack.ReceiveLetter("RP_ExoneratedLabel".Translate(p.LabelShort),
                    (backed ? "RP_ExoneratedBackedText" : "RP_ExoneratedAloneText").Translate(p.LabelShort, c.DaysServed.ToString("F0")),
                    backed ? LetterDefOf.PositiveEvent : LetterDefOf.NegativeEvent, p);
                ReleaseSystem.Release(game, p, record: false);
                return;
            }
            // The board saw the warden vouch and lose, guilty or not (it must not tell the player which).
            if (backed) game.story.boardTrust = Mathf.Max(0.3f, game.story.boardTrust - 0.05f);
            c.tension = Mathf.Min(100f, c.tension + 10f);
            p.needs.mood?.thoughts.memories.TryGainMemory(RPInnocenceDefOf.RP_AppealDenied);
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, "RP_Chr_AppealDenied".Translate(p.LabelShort), p);
        }
    }

    public class IncidentWorker_InnocenceClaim : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            bool Eligible(Pawn x) => x.Inmate() is { innocenceClaimed: false, appealResolveTick: < 0 } c && DeathRowSystem.Ext(c) == null && c.DaysServed >= 1f;
            // The innocent are the ones who keep saying so; the guilty try it too.
            Pawn p = (Rand.Chance(0.6f) ? RandomInmate(map, x => Eligible(x) && x.Inmate().innocent) : null) ?? RandomInmate(map, Eligible);
            if (p == null) return false;
            CompInmate c = p.Inmate();
            c.innocenceClaimed = true;
            bool rings = Rand.Chance(c.innocent ? Innocence.ClueAccuracy : 1f - Innocence.ClueAccuracy);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Innocence", "RP_InnocenceLabel".Translate(p.LabelShort),
                "RP_InnocenceText".Translate(p.LabelShort, c.crime?.label ?? "?", c.DaysServed.ToString("F0"), (rings ? "RP_InnocenceClueTrue" : "RP_InnocenceClueFalse").Translate(p.LabelShort)), map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class InnocenceDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Innocence_Back", "RP_Innocence_Stay" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p == null || !InmateUtility.IsActiveInmate(p) || p.Inmate() is not { } c) return;
            c.appealBacked = option == 0;
            c.appealResolveTick = Find.TickManager.TicksGame + (option == 0 ? Rand.RangeInclusive(2, 4) : Rand.RangeInclusive(3, 6)) * GenDate.TicksPerDay;
            if (option == 0) p.needs.mood?.thoughts.memories.TryGainMemory(RPInnocenceDefOf.RP_BackedByWarden);
        }
    }
}
