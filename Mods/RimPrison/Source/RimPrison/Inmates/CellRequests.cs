using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Cell requests: now and then an inmate asks the warden to share with a friend, or to be moved away from a cellmate
    /// they hate, fear or have bad blood with. Only ever from a real relation (a friendship, a feud or bad opinion,
    /// fear with a threat in the cell, rival gangs) and never more than one letter open at a time. Granting moves them (the
    /// same <see cref="Cellmates.Move"/> the daily review and the move menu use), refusing, or letting the letter time
    /// out, leaves them resentful.
    /// </summary>
    public static class CellRequests
    {
        /// <summary>Saved in a letter as its number: only ever add a kind at the end.</summary>
        public enum Kind { None, Friend, Hate, Fear, Crews }

        public const string DecisionDef = "RP_Decision_CellRequest";
        public const int CooldownDays = 4;
        public const float DailyChance = 0.5f, FearFrom = 40f;
        public const float RefusedTension = 5f;

        /// <summary>A request letter waiting for an answer.</summary>
        public static bool Open()
        {
            foreach (Letter l in StoryLetters.Open())
                if (l is ChoiceLetter_PrisonDecision d && d.Is(DecisionDef)) return true;
            return false;
        }

        /// <summary>What, if anything, makes <paramref name="p"/> want a different cell because of <paramref name="other"/>.</summary>
        public static Kind Reason(Pawn p, CompInmate c, Pawn other)
        {
            if (other == null || other == p || other.Inmate() is not { } oc || !other.IsPrisonerOfColony || other.MapHeld != p.MapHeld) return Kind.None;
            int opinion = p.relations?.OpinionOf(other) ?? 0;
            // (Their home cell: someone in solitary still lives with the cellmates they asked about.)
            if (Cellmates.Share(p, other))
            {
                // Bad blood between crews is only the warden's business once both gangs are known to them: nothing else tells them.
                bool rivals = c.gangKnown && oc.gangKnown && GangUtility.AreRivals(c.gang, oc.gang);
                if (c.fear >= FearFrom && (rivals || oc.HasReputation(RPReputationDefOf.RP_Extortionist) || opinion <= -30)) return Kind.Fear;
                if (PrisonRelations.Enemies(p, other) || opinion <= -20) return Kind.Hate;
                if (rivals) return Kind.Crews;
                return Kind.None;
            }
            if (!PrisonRelations.Friends(p, other)) return Kind.None;
            // Not into the cell of someone waiting to die, and not between protective custody and the rest of the prison.
            if (DeathRowSystem.Ext(oc) != null || Protective(c) != Protective(oc)) return Kind.None;
            return Kind.Friend;
        }

        private static bool Protective(CompInmate c) => c.security != null && c.security == RPSecurityDefOf.RP_Protective;

        /// <summary>The bed granting the request would put them in, or null (no suitable bed is free). Away from a cellmate:
        /// the best-fitting free bed, a better cell than this one; in with a friend: a free bed in the friend's cell.</summary>
        public static Building_Bed Target(Pawn p, CompInmate c, Pawn other, Kind kind)
        {
            if (kind == Kind.None || Cellmates.HomeBed(p, c) is not { } own) return null;
            Room theirs = Cellmates.Home(other, other.Inmate());
            if (kind == Kind.Friend)
                return theirs == null ? null : Cellmates.BestFree(p, c, allowed: b => b.GetRoom() == theirs && Cellmates.MateScore(b, c) > Cellmates.BadFit);
            return Cellmates.BestFree(p, c, Cellmates.Fit(own, c) + 1f, b => b.GetRoom() != theirs);
        }

        /// <summary>Not asked for another cell lately (themselves, or about the same pair: raising a request stamps both).</summary>
        public static bool CooledDown(CompInmate c, int now) => now - c.lastCellRequestTick >= CooldownDays * GenDate.TicksPerDay;

        /// <summary>Settled enough to be making requests: in a cell, not in trouble, not waiting to die.</summary>
        private static bool Eligible(Pawn p, CompInmate c) =>
            InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState && c.forcedActivity == null && DeathRowSystem.Ext(c) == null
            && p.ownership?.OwnedBed is { } bed && !PunishmentUtility.IsSolitaryBed(bed);

        /// <summary>Daily: sometimes one inmate with a real reason asks (nothing while a request is still open).</summary>
        public static void Daily(Map map, bool force = false)
        {
            if (Open() || (!force && !Rand.Chance(DailyChance))) return;
            int now = Find.TickManager.TicksGame;
            var askers = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c && CooledDown(c, now) && Eligible(p, c)) askers.Add(p);
            askers.Shuffle();
            foreach (Pawn p in askers)
                if (TryRaise(p, p.Inmate()) != null) return;
        }

        /// <summary>Raises the request letter if this inmate has a reason (to be moved from a cellmate or to join a friend
        /// who has a bed free), optionally only about <paramref name="only"/>; null when there is none or one is open.</summary>
        public static ChoiceLetter_PrisonDecision TryRaise(Pawn p, CompInmate c, Pawn only = null)
        {
            if (Open() || !Eligible(p, c)) return null;
            var reasons = new List<(Pawn other, Kind kind)>();
            foreach (Pawn mate in Cellmates.HomeMates(p, c))
                if (only == null || mate == only) reasons.Add((mate, Reason(p, c, mate)));
            foreach (DirectPawnRelation r in p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
                if (r.def == RPRelationDefOf.RP_Friend && (only == null || r.otherPawn == only) && Reason(p, c, r.otherPawn) == Kind.Friend && Target(p, c, r.otherPawn, Kind.Friend) != null)
                    reasons.Add((r.otherPawn, Kind.Friend));
            reasons.RemoveAll(x => x.kind == Kind.None);
            if (reasons.Count == 0) return null;
            (Pawn other, Kind kind) = reasons.RandomElement();
            string a = p.LabelShort, b = other.LabelShort;
            string text = kind switch
            {
                Kind.Friend => "RP_CellRequestFriendText".Translate(a, b, Cellmates.CellOfInmate(other, other.Inmate(), fresh: true)?.number ?? 0).Resolve(),
                Kind.Hate => "RP_CellRequestHateText".Translate(a, b).Resolve(),
                Kind.Fear => "RP_CellRequestFearText".Translate(a, b).Resolve(),
                _ => "RP_CellRequestCrewsText".Translate(a, b).Resolve(),
            };
            // Both of them have asked, as far as the warden is concerned: the other one does not raise the same grievance tomorrow.
            c.lastCellRequestTick = Find.TickManager.TicksGame;
            other.Inmate().lastCellRequestTick = c.lastCellRequestTick;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(DecisionDef, "RP_CellRequestLabel".Translate(a), text, p.Map, new LookTargets(new[] { p, other }));
            letter.pawns.Add(p);
            letter.pawns.Add(other);
            letter.amount = (int)kind;
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            WardenAsks.Raise(p, WardenAskReason.CellRequest);
            return letter;
        }
    }

    /// <summary>Grant the move or refuse (an unanswered letter is a refusal).</summary>
    public class PrisonDecisionWorker_CellRequest : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_CellRequestGrant", "RP_CellRequestRefuse" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        private static bool Read(ChoiceLetter_PrisonDecision l, out Pawn p, out CompInmate c, out Pawn other, out CellRequests.Kind kind)
        {
            p = l.pawns.Count > 0 ? l.pawns[0] : null;
            other = l.pawns.Count > 1 ? l.pawns[1] : null;
            kind = (CellRequests.Kind)l.amount;
            c = p?.Inmate();
            return p is { Spawned: true, Dead: false } && c != null && InmateUtility.IsActiveInmate(p) && other is { Spawned: true, Dead: false } && other.IsPrisonerOfColony;
        }

        /// <summary>Nothing left to ask for: someone left or died, or the situation already changed (they no longer share,
        /// or already do).</summary>
        private static bool Moot(ChoiceLetter_PrisonDecision l)
        {
            if (!Read(l, out Pawn p, out _, out Pawn other, out CellRequests.Kind kind)) return true;
            bool sharing = Cellmates.Share(p, other);
            return kind == CellRequests.Kind.Friend ? sharing : !sharing;
        }

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 0) return null;
            if (Moot(l)) return "RP_CellRequestMoot".Translate();
            Read(l, out Pawn p, out CompInmate c, out Pawn other, out CellRequests.Kind kind);
            return CellRequests.Target(p, c, other, kind) == null ? "RP_CellRequestNoBed".Translate() : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (Moot(l)) return;
            Read(l, out Pawn p, out CompInmate c, out Pawn other, out CellRequests.Kind kind);
            if (option == 0)
            {
                Building_Bed bed = CellRequests.Target(p, c, other, kind);
                if (bed == null || !Cellmates.Move(p, c, bed))
                {
                    Messages.Message("RP_CellRequestNoBed".Translate(), p, MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                Cellmates.PlacedByWarden(c);
                string key = kind == CellRequests.Kind.Friend ? "RP_Chr_CellRequestFriend" : "RP_Chr_CellRequestAway";
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, key.Translate(p.LabelShort, other.LabelShort), p, other);
                // Away from a cellmate (not in with a friend): from now on they are kept apart.
                if (kind != CellRequests.Kind.Friend) KeepApart.Add(p, other, "request", silent: true);
                return;
            }
            // Refused (or never answered): resentful, and the fear stays. The warden chose to keep them together, so the daily review
            // does not split them behind their back.
            if (kind != CellRequests.Kind.Friend)
            {
                Cellmates.PlacedByWarden(c);
                if (other.Inmate() is { } oc) Cellmates.PlacedByWarden(oc);
            }
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPRelationDefOf.RP_CellRequestRefused);
            c.tension = UnityEngine.Mathf.Min(100f, c.tension + CellRequests.RefusedTension);
            if (kind == CellRequests.Kind.Fear) c.mainCause = "RP_Cause_Unsafe";
        }
    }
}
