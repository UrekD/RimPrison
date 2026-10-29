using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Fear (0-100): how unsafe an inmate feels. Rival gang members (and a rival gang's turf), extortionists and people who hate them nearby push
    /// it up (faster for the vulnerable: informants found out, inmates with a vulnerable reputation); guards in sight,
    /// their own cell and protective custody bring it down. Frightened inmates stay in their cells in free time, wind up
    /// (fear feeds tension), and the terrified ask for protective custody. Predators pick on the frightened.
    /// </summary>
    public static class FearSystem
    {
        public const float HideAbove = 60f;
        /// <summary>Threats near an inmate that count toward their fear (more add nothing).</summary>
        public const int MaxThreats = 4;
        public const float RequestPcAbove = 80f;
        public const int RequestCooldownTicks = GenDate.TicksPerDay * 3;

        public static bool Vulnerable(CompInmate c) => c.snitchExposed || c.reputations.Exists(r => r.vulnerable);

        /// <summary>Hourly per active inmate (with the tension update).</summary>
        public static void Hourly(Pawn p, CompInmate c, PrisonMap pm) => Hourly(p, c, pm, TensionUtility.Watchers(p, pm));

        /// <param name="watchers"><see cref="TensionUtility.Watchers"/> of the inmate now (the hourly update asks once for all that need it).</param>
        public static void Hourly(Pawn p, CompInmate c, PrisonMap pm, float watchers)
        {
            float delta = -3f;
            int threats = 0;
            IReadOnlyList<Pawn> inmates = p.Map.mapPawns.PrisonersOfColonySpawned;
            Room room = p.GetRoom();
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                if (o == p || !o.Position.InHorDistOf(p.Position, 6f) || !o.Awake() || o.GetRoom() != room || o.Inmate() is not { } oc) continue;
                if (GangUtility.AreRivals(c.gang, oc.gang) || oc.HasReputation(RPReputationDefOf.RP_Extortionist) || (o.relations?.OpinionOf(p) ?? 0) <= -30)
                    threats++;
                else if (c.snitchExposed && oc.gang != null) threats++;
                // (Four threats is all that counts below: the rest of a crowd is not asked what they think of them.)
                if (threats >= MaxThreats) break;
            }
            // A rival's turf frightens (and one's own is a relief).
            float turfFear = GangTurf.FearTerm(p, c, pm);
            float gain = Mathf.Min(threats, MaxThreats) * 4f + Mathf.Max(turfFear, 0f);
            if (Vulnerable(c)) gain *= 1.5f;
            delta += gain + Mathf.Min(turfFear, 0f);
            if (watchers >= 1f) delta -= 4f;
            // Friends around: someone has their back.
            delta -= 2f * Mathf.Min(PrisonRelations.FriendsNear(p, 6f), 2);
            if (room != null && room == InmateUtility.CellOf(p)) delta -= 5f;
            if (Trials.IsProtected(c)) delta -= 8f;
            // A gang that is paid to keep them safe does (see DebtSystem).
            if (DebtSystem.ProtectionOf(p) != null) delta -= DebtSystem.ProtectionFear;
            float was = c.fear;
            c.fear = Mathf.Clamp(c.fear + delta, 0f, 100f);
            if (AutoTest.Active && was < HideAbove && c.fear >= HideAbove)
                RPLog.Message($"[fear] {p.LabelShort} {was:F0} -> {c.fear:F0} at the hour (hides in free time from {HideAbove:F0}): {threats} threats near, turf {turfFear:F0}, vulnerable {Vulnerable(c)}, watched {watchers >= 1f}, in their cell {room != null && room == InmateUtility.CellOf(p)}");
            if (c.fear >= RequestPcAbove && !Trials.IsProtected(c) && DeathRowSystem.Ext(c) == null && c.security is not { escortOnly: true }
                && Find.TickManager.TicksGame - c.lastPcRequestTick >= RequestCooldownTicks)
                RequestProtectiveCustody(p, c);
        }

        /// <summary>Things that happen to an inmate that frighten them.</summary>
        public static void Scare(CompInmate c, float amount)
        {
            float was = c.fear;
            c.fear = Mathf.Min(100f, c.fear + (Vulnerable(c) ? amount * 1.5f : amount));
            // (Who frightened them is the line's stack: the tests read where a fear that hides an inmate came from.)
            if (AutoTest.Active && was < HideAbove && c.fear >= HideAbove) RPLog.Message($"[fear] {c.parent?.LabelShort} {was:F0} -> {c.fear:F0}, scared by {amount:F0} (hides in free time from {HideAbove:F0})");
        }

        private static void RequestProtectiveCustody(Pawn p, CompInmate c) => RequestProtectiveCustody(p, c, "RP_PcRequestLabel".Translate(), "RP_PcRequestText".Translate(p.LabelShort));

        /// <summary>Asks the warden to move them to protective custody (the letter's words are the caller's: a debtor says why).</summary>
        public static void RequestProtectiveCustody(Pawn p, CompInmate c, TaggedString label, TaggedString text)
        {
            SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
            if (pc == null) return;
            c.lastPcRequestTick = Find.TickManager.TicksGame;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_ProtectiveCustody", label, text, p.Map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    public class PrisonDecisionWorker_ProtectiveCustody : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
            SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
            foreach (Pawn p in l.pawns)
                if (p is { Spawned: true, Dead: false, IsPrisonerOfColony: true } && p.Inmate() is { activeFlag: true } c && pc != null)
                {
                    if (!Unlocks.IsUnlocked(pc) || !InmateUtility.HasRoomIn(p, pc))
                    {
                        Messages.Message(InmateUtility.NoRoomText(p, pc), p, MessageTypeDefOf.RejectInput, historical: false);
                        continue;
                    }
                    InmateUtility.ChangeSecurity(p, c, pc);
                    c.fear = Mathf.Max(0f, c.fear - 30f);
                }
        }

        /// <summary>Turned down: they feel abandoned (and the people they fear know they are on their own).</summary>
        public override void Decline(ChoiceLetter_PrisonDecision l, Map map)
        {
            foreach (Pawn p in l.pawns)
                if (p is { Spawned: true, Dead: false } && p.Inmate() is { } c)
                {
                    c.tension = Mathf.Min(100f, c.tension + 10f);
                    c.mainCause = "RP_Cause_Unsafe";
                }
        }
    }

    /// <summary>Mood from fear.</summary>
    public class ThoughtWorker_Fear : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (p.Inmate() is not { activeFlag: true } c) return ThoughtState.Inactive;
            if (c.fear >= FearSystem.RequestPcAbove) return ThoughtState.ActiveAtStage(1);
            if (c.fear >= 50f) return ThoughtState.ActiveAtStage(0);
            return ThoughtState.Inactive;
        }
    }
}
