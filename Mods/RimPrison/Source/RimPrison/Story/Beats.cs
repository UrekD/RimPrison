using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPBeatDefOf
    {
        public static StoryArcDef RP_Arc_GangChallenge;
        public static ThoughtDef RP_BirthdayInside;
        public static ThoughtDef RP_BirthdayWithFriends;
        public static ThoughtDef RP_TalentNoticed;

        static RPBeatDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPBeatDefOf));
    }

    /// <summary>
    /// An ambitious gang member squares up to the boss (known gangs only: the warden hears about it). It simmers for a
    /// day or two, then they settle it when they meet: the stronger fighter (with luck) leads, the other is an enemy.
    /// </summary>
    public class IncidentWorker_GangChallenge : IncidentWorker_Prison
    {
        protected override int MinInmates => 4;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            foreach (IGrouping<GangDef, Pawn> gang in map.mapPawns.PrisonersOfColonySpawned
                         .Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate().gang != null && p.Inmate().gangKnown).GroupBy(p => p.Inmate().gang).InRandomOrder())
            {
                Pawn boss = gang.FirstOrDefault(p => p.Inmate().gangLeader);
                if (boss == null || gang.Count() < 3) continue;
                Pawn challenger = gang.Where(p => p != boss && !p.Inmate().gangLeader && p.Inmate().tension >= 35f && !p.WorkTagIsDisabled(WorkTags.Violent))
                    .OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0).FirstOrDefault();
                if (challenger == null) continue;
                StoryArc arc = PrisonGame.Instance.story.TryStartArc(RPBeatDefOf.RP_Arc_GangChallenge, challenger, boss);
                if (arc == null) continue;
                arc.nextTick = Find.TickManager.TicksGame + Rand.RangeInclusive(18, 48) * GenDate.TicksPerHour;
                arc.note = gang.Key.label;
                challenger.Inmate().lastStoryTick = boss.Inmate().lastStoryTick = Find.TickManager.TicksGame;
                Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangChallenge".Translate(challenger.LabelShort, boss.LabelShort, gang.Key.label), challenger, boss);
                ((GangChallengeArc)arc.def.Worker).Ask(arc, map, challenger, boss, gang.Key);
                return true;
            }
            return false;
        }
    }

    public class GangChallengeArc : StoryArcWorker
    {
        private static readonly string[] Keys = { "RP_GangChallenge_Wait", "RP_GangChallenge_Transfer", "RP_GangChallenge_PC" };

        public void Ask(StoryArc arc, Map map, Pawn challenger, Pawn boss, GangDef gang) =>
            AskWarden(arc, map, "RP_GangChallengeLabel".Translate(gang.LabelCap), "RP_GangChallengeText".Translate(challenger.LabelShort, boss.LabelShort, gang.label, Money.Format(InmateUtility.TransferCost)));

        /// <summary>Both are still in the gang (the challenger may have walked out of it: an ex-member would be crowned with no gang), and the boss still leads it.</summary>
        public override bool Valid(StoryArc arc) =>
            base.Valid(arc) && arc.A.Inmate().gang?.label == arc.note && arc.B.Inmate() is { gangLeader: true } bc && bc.gang?.label == arc.note;

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override string Disabled(StoryArc arc, int option)
        {
            SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
            if (option == 2 && (pc == null || !Unlocks.IsUnlocked(pc) || arc.B is not { Spawned: true } || !InmateUtility.HasRoomIn(arc.B, pc)))
                return pc != null && arc.B is { Spawned: true } ? InmateUtility.NoRoomText(arc.B, pc) : "RP_PcNoRoom".Translate(arc.B?.LabelShort ?? "?");
            if (option == 1 && PrisonGame.Instance.ledger.funds < InmateUtility.TransferCost) return "RP_CannotAfford".Translate(Money.Format(InmateUtility.TransferCost));
            return null;
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            // The letter may have sat open while they were carried off, died, or left.
            if (option == 1 && arc.A is { Spawned: true } && InmateUtility.IsActiveInmate(arc.A))
            {
                InmateUtility.TransferOut(arc.A, arc.A.Inmate()); // on success, Notify_Leaving ends the arc
            }
            else if (option == 2 && arc.B is { Spawned: true } && InmateUtility.IsActiveInmate(arc.B))
            {
                // The boss off the wing: the gang drifts (no leader inside), the challenger waits for nobody.
                InmateUtility.ChangeSecurity(arc.B, arc.B.Inmate(), RPSecurityDefOf.RP_Protective);
                GangRanks.SetRank(arc.B.Inmate(), GangRank.Member);
                Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangBossRemoved".Translate(arc.B.LabelShort, arc.note), arc.B);
                arc.done = true;
            }
        }

        public override void Hourly(StoryArc arc, Map map)
        {
            Pawn challenger = arc.A, boss = arc.B;
            CompInmate cc = challenger.Inmate(), bc = boss.Inmate();
            if (Find.TickManager.TicksGame < arc.nextTick) return;
            if (!challenger.Spawned || !boss.Spawned || challenger.Downed || boss.Downed || !challenger.Awake() || !boss.Awake()
                || challenger.InMentalState || boss.InMentalState || challenger.GetRoom() != boss.GetRoom())
            {
                if (Find.TickManager.TicksGame - arc.nextTick > 3 * GenDate.TicksPerDay) arc.done = true;
                return;
            }
            // The showdown: a fight, and the gang watches who comes out on top.
            if (!challenger.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: boss)) return;
            ContrabandUtility.Notify_ViolenceStarting(challenger);
            float c = (challenger.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0) + Rand.Range(0f, 8f);
            float b = (boss.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0) + Rand.Range(0f, 8f) + 2f;
            Pawn winner = c > b ? challenger : boss, loser = winner == challenger ? boss : challenger;
            GangRanks.SetRank(winner.Inmate(), GangRank.Leader);
            GangRanks.SetRank(loser.Inmate(), GangRank.Member);
            PrisonRelations.MakeEnemies(winner, loser);
            PrisonGame.Instance.stats.gangChallenges++;
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, (winner == challenger ? "RP_Chr_GangChallengeWon" : "RP_Chr_GangChallengeLost").Translate(challenger.LabelShort, boss.LabelShort, arc.note), challenger, boss);
            MisconductUtility.Record(challenger, cc, PrisonMap.Of(map), RPMisconductDefOf.RP_Fight, chronicle: false);
            arc.done = true;
        }
    }

    /// <summary>A frightened or vulnerable inmate could be turned into an informant: protection and pay, or a move.</summary>
    public class IncidentWorker_InformantRecruit : IncidentWorker_Prison
    {
        protected override int MinInmates => 3;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Pawn p = RandomInmate(map, x => x.Inmate() is { } c && !c.HasReputation(ReputationDefOf.RP_Snitch) && !c.gangLeader
                                            && (c.fear >= 40f || FearSystem.Vulnerable(c) || c.cash < 5 && c.tension >= 40f));
            if (p == null) return false;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Informant", "RP_InformantLabel".Translate(p.LabelShort),
                "RP_InformantText".Translate(p.LabelShort, Money.Format(InformantDecision.DailyPay)), map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class InformantDecision : PrisonDecisionWorker
    {
        public const int DailyPay = 10;
        private static readonly string[] Keys = { "RP_Informant_Pay", "RP_Informant_Move", "RP_Informant_No" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 1 || l.pawns.Count == 0 || l.pawns[0].Inmate() is not { } c) return null;
            if (c.security?.promoteTo is { } down && Unlocks.IsUnlocked(down) && InmateUtility.HasRoomIn(l.pawns[0], down)) return null;
            return c.security?.promoteTo is { } wing && InmateUtility.WingKeepsThemOut(l.pawns[0], wing) ? InmateUtility.NoRoomText(l.pawns[0], wing) : "RP_InformantNoMove".Translate();
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p == null || !InmateUtility.IsActiveInmate(p) || option == 2) return;
            CompInmate c = p.Inmate();
            if (option == 1) InmateUtility.ChangeSecurity(p, c, c.security.promoteTo);
            Recruit(p, c, paid: option != 1);
        }

        /// <summary>The inmate talks to the warden from now on (<paramref name="paid"/>: for a wage; else for a move to a wing where they are safe).</summary>
        public static void Recruit(Pawn p, CompInmate c, bool paid)
        {
            if (paid) c.informantPaid = true;
            if (!c.reputations.Contains(ReputationDefOf.RP_Snitch)) c.reputations.Add(ReputationDefOf.RP_Snitch);
            if (!c.revealed.Contains(ReputationDefOf.RP_Snitch)) c.revealed.Add(ReputationDefOf.RP_Snitch);
            PrisonGame.Instance.stats.informantsRecruited++;
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_InformantRecruited".Translate(p.LabelShort), p);
        }
    }

    /// <summary>Someone has a real skill (8+) that a prison job uses: put them to it, or not.</summary>
    public class IncidentWorker_Talent : IncidentWorker_Prison
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Pawn p = RandomInmate(map, x => !x.Inmate().talentNoticed && TalentJob(x) != null);
            if (p == null) return false;
            CompInmate c = p.Inmate();
            c.talentNoticed = true;
            InmateJobDef job = TalentJob(p);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Talent", "RP_TalentLabel".Translate(p.LabelShort),
                "RP_TalentText".Translate(p.LabelShort, job.skill.label, p.skills.GetSkill(job.skill).Level, job.label), map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }

        /// <summary>A prison job that uses their best skill (8+), that their level and training allow.</summary>
        public static InmateJobDef TalentJob(Pawn p)
        {
            CompInmate c = p.Inmate();
            if (c == null || p.skills == null) return null;
            return DefDatabase<InmateJobDef>.AllDefsListForReading
                .Where(j => j.skill != null && p.skills.GetSkill(j.skill).Level >= 8 && (j.allowedSecurity.Count == 0 || j.allowedSecurity.Contains(c.security))
                            && (j.requiredProgram == null || c.passedPrograms.Contains(j.requiredProgram)))
                .OrderByDescending(j => p.skills.GetSkill(j.skill).Level).FirstOrDefault();
        }
    }

    public class TalentDecision : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p == null || !InmateUtility.IsActiveInmate(p) || IncidentWorker_Talent.TalentJob(p) is not { } job) return;
            CompInmate c = p.Inmate();
            c.job = job;
            c.jobLocked = true;
            p.needs.mood?.thoughts.memories.TryGainMemory(RPBeatDefOf.RP_TalentNoticed);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_TalentUsed".Translate(p.LabelShort, job.label), p);
        }
    }

    /// <summary>A birthday inside: lonely, unless friends make something of it.</summary>
    [HarmonyPatch(typeof(Pawn_AgeTracker), "BirthdayBiological")]
    public static class Patch_BirthdayInside
    {
        public static void Postfix(Pawn ___pawn, int birthdayAge)
        {
            if (!___pawn.IsPrisonerOfColony || ___pawn.Inmate() is not { activeFlag: true } || PrisonGame.Instance == null) return;
            List<Pawn> friends = (___pawn.relations?.DirectRelations ?? new List<DirectPawnRelation>())
                .Where(r => r.def == RPRelationDefOf.RP_Friend && r.otherPawn.Spawned && r.otherPawn.IsPrisonerOfColony).Select(r => r.otherPawn).ToList();
            if (friends.Count > 0)
            {
                ___pawn.needs.mood?.thoughts.memories.TryGainMemory(RPBeatDefOf.RP_BirthdayWithFriends);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_BirthdayFriends".Translate(___pawn.LabelShort, birthdayAge, friends[0].LabelShort), ___pawn, friends[0]);
            }
            else
            {
                ___pawn.needs.mood?.thoughts.memories.TryGainMemory(RPBeatDefOf.RP_BirthdayInside);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_BirthdayAlone".Translate(___pawn.LabelShort, birthdayAge), ___pawn);
            }
        }
    }
}
