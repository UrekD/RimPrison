using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPPastimeDefOf
    {
        public static ThoughtDef RP_BigMatch;
        public static ThoughtDef RP_MissedTheMatch;
        public static ThoughtDef RP_TalentShow;
        public static ThoughtDef RP_StarOfTheShow;
        public static ThoughtDef RP_Initiated;
        public static ThoughtDef RP_PressuredToJoin;

        static RPPastimeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPPastimeDefOf));
    }

    /// <summary>
    /// Two inmates who can square up (the same room, within reach; rival gangs first): a social fight between them,
    /// on the record like any other fight.
    /// </summary>
    public static class Scuffles
    {
        public static bool TryStart(Map map, System.Func<Pawn, Pawn, bool> pair = null)
        {
            List<Pawn> able = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState && p.Awake()).ToList();
            foreach (Pawn a in able.InRandomOrder())
            {
                Room room = a.GetRoom();
                if (room == null) continue;
                foreach (Pawn b in able.Where(b => b != a && b.GetRoom() == room && b.Position.InHorDistOf(a.Position, 12f))
                             .OrderByDescending(b => GangUtility.AreRivals(a.Inmate().gang, b.Inmate().gang)))
                {
                    if (pair != null && !pair(a, b) || !a.interactions.SocialFightPossible(b) || !a.CanReach(b, Verse.AI.PathEndMode.Touch, Danger.Deadly)) continue;
                    ContrabandUtility.Notify_ViolenceStarting(a);
                    a.interactions.StartSocialFight(b);
                    MisconductUtility.Record(a, a.Inmate(), PrisonMap.Of(map), RPMisconductDefOf.RP_Fight, chronicle: false);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The big match is on the radio tonight. Let them listen (a good night for most, and the losing side's fans may
    /// take it out on someone) or lights out as usual.
    /// </summary>
    public class IncidentWorker_BigMatch : IncidentWorker_Prison
    {
        public const float FightChance = 0.35f;

        protected override int MinInmates => 4;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_BigMatch", "RP_BigMatchLabel".Translate(),
                "RP_BigMatchText".Translate(FightChance.ToStringPercent()), map);
            letter.StartTimeout(GenDate.TicksPerHour * 6);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class BigMatchDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_BigMatch_Listen", "RP_BigMatch_LightsOut" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true })
                    p.needs.mood?.thoughts.memories.TryGainMemory(option == 0 ? RPPastimeDefOf.RP_BigMatch : RPPastimeDefOf.RP_MissedTheMatch);
            if (option != 0) return;
            bool fight = Rand.Chance(IncidentWorker_BigMatch.FightChance) && Scuffles.TryStart(map);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, (fight ? "RP_Chr_BigMatchFight" : "RP_Chr_BigMatch").Translate());
        }
    }

    /// <summary>
    /// The chaplain wants to put on a talent show. Host it: a good day for everyone, and the best performer
    /// (artistic or social) becomes a minor celebrity; a heckler may start something.
    /// </summary>
    public class IncidentWorker_TalentShow : IncidentWorker_Prison
    {
        public const int Cost = 150;
        public const float HecklerChance = 0.15f;

        protected override int MinInmates => 4;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_TalentShow", "RP_TalentShowLabel".Translate(),
                "RP_TalentShowText".Translate(Money.Format(Cost)), map);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }

        public static Pawn BestPerformer(Map map) => map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate)
            .OrderByDescending(p => Mathf.Max(p.skills?.GetSkill(SkillDefOf.Artistic).Level ?? 0, p.skills?.GetSkill(SkillDefOf.Social).Level ?? 0)).FirstOrDefault();
    }

    public class TalentShowDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_TalentShow_Host", "RP_TalentShow_No" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < IncidentWorker_TalentShow.Cost ? "RP_CannotAfford".Translate(Money.Format(IncidentWorker_TalentShow.Cost)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (option != 0) return;
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Programs, IncidentWorker_TalentShow.Cost, "RP_TalentShowNote".Translate())) return;
            Pawn star = IncidentWorker_TalentShow.BestPerformer(map);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c)
                {
                    p.needs.mood?.thoughts.memories.TryGainMemory(p == star ? RPPastimeDefOf.RP_StarOfTheShow : RPPastimeDefOf.RP_TalentShow);
                    c.tension = Mathf.Max(0f, c.tension - 5f);
                }
            if (Rand.Chance(IncidentWorker_TalentShow.HecklerChance)) Scuffles.TryStart(map, (a, b) => b == star);
            if (star != null) Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_TalentShow".Translate(star.LabelShort), star);
        }
    }

    /// <summary>
    /// A new arrival is being leaned on to join a gang. Protective custody (if there is room), a word with the gang's
    /// leader, or let it play out: they join, or refuse and get a beating for it.
    /// </summary>
    public class IncidentWorker_Initiation : IncidentWorker_Prison
    {
        public const float JoinChance = 0.6f, WarningWorks = 0.7f;

        protected override int MinInmates => 3;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            int now = Find.TickManager.TicksGame;
            Pawn leader = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate() is { gangLeader: true, gangKnown: true, gang: not null } lc0 && !GangDeals.NoRecruiting(lc0.gang))
                .RandomElementWithFallback();
            if (leader == null) return false;
            CompInmate lc = leader.Inmate();
            Pawn fresh = RandomInmate(map, x => x != leader && x.Inmate() is { gang: null } c && now - c.admittedTick < 3 * GenDate.TicksPerDay
                                                && c.security == lc.security && DeathRowSystem.Ext(c) == null);
            if (fresh == null) return false;
            fresh.needs.mood?.thoughts.memories.TryGainMemory(RPPastimeDefOf.RP_PressuredToJoin);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Initiation", "RP_InitiationLabel".Translate(fresh.LabelShort),
                "RP_InitiationText".Translate(fresh.LabelShort, lc.gang.label, leader.LabelShort), map, fresh);
            letter.pawns.Add(fresh);
            letter.pawns.Add(leader);
            letter.amount = lc.gang.shortHash; // pawns are compacted on save: the gang must not depend on the leader
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class InitiationDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Initiation_PC", "RP_Initiation_Warn", "RP_Initiation_PlayOut" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        private static SecurityLevelDef PC => RPSecurityDefOf.RP_Protective;

        private static GangDef Gang(ChoiceLetter_PrisonDecision l) => DefDatabase<GangDef>.GetByShortHash((ushort)l.amount);

        private static Pawn Fresh(ChoiceLetter_PrisonDecision l) => l.pawns.FirstOrDefault(x => x != null && x.IsPrisonerOfColony && x.Inmate() is { gang: null });

        private static string NoPC(Pawn fresh) =>
            PC == null || !Unlocks.IsUnlocked(PC) || fresh is not { Spawned: true } || !InmateUtility.HasRoomIn(fresh, PC)
                ? (fresh is { Spawned: true } && PC != null ? InmateUtility.NoRoomText(fresh, PC) : "RP_PcNoRoom".Translate(fresh?.LabelShort ?? "?")).ToString() : null;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) => option == 0 ? NoPC(Fresh(l)) : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn fresh = Fresh(l);
            GangDef gang = Gang(l);
            if (fresh == null || !InmateUtility.IsActiveInmate(fresh) || fresh.Inmate() is not { } fc || gang == null) return;
            Pawn leader = l.pawns.FirstOrDefault(x => x != null && x != fresh && x.Inmate() is { gangLeader: true } xc && xc.gang == gang);
            switch (option)
            {
                case 0:
                    // The beds may have filled since the letter opened.
                    if (NoPC(fresh) is { } why)
                    {
                        Messages.Message(why, fresh, MessageTypeDefOf.RejectInput, historical: false);
                        break;
                    }
                    InmateUtility.ChangeSecurity(fresh, fc, PC);
                    break;
                case 1:
                    if (leader?.Inmate() is { } lc) lc.tension = Mathf.Min(100f, lc.tension + 10f);
                    if (Rand.Chance(IncidentWorker_Initiation.WarningWorks)) break;
                    goto default;
                default:
                    // (A deal struck since the letter came: the gang takes nobody in, and does not make an example of them either.)
                    if (GangDeals.NoRecruiting(gang)) break;
                    if (Rand.Chance(IncidentWorker_Initiation.JoinChance))
                    {
                        fc.gang = gang;
                        fc.gangKnown = true;
                        fresh.needs.mood?.thoughts.memories.TryGainMemory(RPPastimeDefOf.RP_Initiated);
                        PrisonGame.Instance.stats.gangRecruits++;
                        Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_Initiated".Translate(fresh.LabelShort, gang.label), fresh);
                    }
                    else
                    {
                        // They said no: someone from the gang makes an example of them.
                        Room room = fresh.GetRoom();
                        Pawn enforcer = map.mapPawns.PrisonersOfColonySpawned.Where(p => p != fresh && InmateUtility.IsActiveInmate(p) && p.Inmate().gang == gang
                                                                                         && !p.Downed && !p.InMentalState && p.Awake() && !p.WorkTagIsDisabled(WorkTags.Violent)
                                                                                         && p.GetRoom() == room && p.CanReach(fresh, Verse.AI.PathEndMode.Touch, Danger.Deadly))
                            .RandomElementWithFallback();
                        if (enforcer != null && enforcer.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: fresh))
                            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_RefusedGang".Translate(fresh.LabelShort, gang.label), fresh, enforcer);
                        else
                        {
                            // Nobody within reach yet: they will be waiting for them (fear, and a record of the threat).
                            fc.fear = Mathf.Min(100f, fc.fear + 25f);
                            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_RefusedGangThreat".Translate(fresh.LabelShort, gang.label), fresh);
                        }
                    }
                    break;
            }
        }
    }
}
