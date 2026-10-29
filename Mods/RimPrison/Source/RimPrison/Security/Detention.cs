using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    [DefOf]
    public static class RPDetentionDefOf
    {
        public static DutyDef RP_ArrestSuspect;
        public static DutyDef RP_Detained;
        public static JobDef RP_Cuff;
        public static PawnKindDef RP_PoliceOfficer;
        public static LedgerCategoryDef RP_Bounties;

        static RPDetentionDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDetentionDefOf));
    }

    /// <summary>
    /// Arrests on site: a smuggler caught at visitor reception (or a corrupt member of staff) is held until a police
    /// officer walks in, cuffs them and takes them away. The state pays a small bounty for smugglers. A caught
    /// visitor sometimes bolts; the searching guard may tackle them.
    /// </summary>
    public static class Detention
    {
        public const int SmugglerBounty = 150;
        public const float BoltChance = 0.15f;

        /// <summary>A guard found contraband on a visitor.</summary>
        public static void Notify_VisitorCaught(Pawn guard, Pawn visitor)
        {
            PrisonGame game = PrisonGame.Instance;
            if (Rand.Chance(BoltChance))
            {
                // The guard is right there: a tackle, more likely for a good brawler.
                float tackle = 0.45f + (guard.skills?.GetSkill(SkillDefOf.Melee).Level ?? 0) * 0.025f;
                if (!Rand.Chance(tackle))
                {
                    visitor.GetLord()?.Notify_PawnLost(visitor, PawnLostCondition.ForcedByPlayerAction);
                    LordMaker.MakeNewLord(visitor.Faction, new LordJob_ExitMapBest(LocomotionUrgency.Sprint, canDig: false), visitor.Map, new List<Pawn> { visitor });
                    game.stats.smugglersFled++;
                    Messages.Message("RP_SmugglerFled".Translate(visitor.LabelShort, guard.LabelShort), visitor, MessageTypeDefOf.NegativeEvent);
                    return;
                }
                Messages.Message("RP_SmugglerTackled".Translate(guard.LabelShort, visitor.LabelShort), visitor, MessageTypeDefOf.NeutralEvent, historical: false);
            }
            // The bounty is paid when the officer actually takes them away (JobDriver_Cuff).
            CallPolice(visitor, smuggler: true);
        }

        /// <summary>The officer has the suspect in cuffs.</summary>
        public static void Notify_Collected(Pawn suspect, bool smuggler)
        {
            PrisonGame game = PrisonGame.Instance;
            game.stats.arrestsMade++;
            if (smuggler)
            {
                game.stats.smugglersDetained++;
                game.ledger.Add(RPDetentionDefOf.RP_Bounties, SmugglerBounty, "RP_SmugglerBountyNote".Translate(suspect.LabelShort));
                game.reputation = Mathf.Min(100f, game.reputation + 0.5f);
            }
            else game.stats.staffArrested++;
        }

        /// <summary>Holds <paramref name="suspect"/> (no longer in any lord) and sends an officer to collect them.</summary>
        public static bool CallPolice(Pawn suspect, bool smuggler = false)
        {
            Map map = suspect.Map;
            Faction outside = InmateGenerator.OutsideFaction;
            if (map == null || outside == null) return false;
            if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 entry, map, CellFinder.EdgeRoadChance_Always)) return false;
            Pawn officer = PawnGenerator.GeneratePawn(new PawnGenerationRequest(RPDetentionDefOf.RP_PoliceOfficer, outside, PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowFood: false));
            RiotPolice.Dress(officer, armoured: false);
            GenSpawn.Spawn(officer, entry, map);
            suspect.GetLord()?.Notify_PawnLost(suspect, PawnLostCondition.ForcedByPlayerAction);
            if (suspect.Faction != outside) suspect.SetFaction(outside);
            LordMaker.MakeNewLord(outside, new LordJob_Arrest(suspect, smuggler), map, new List<Pawn> { officer, suspect });
            // Both may pass locked doors (read by the door patch on path threads: a plain field, not saved: the lord job sets it again on load).
            (suspect.GetLord()?.LordJob as LordJob_Arrest)?.GrantPass();
            if (suspect.CurJob is { } job && job.def != JobDefOf.Wait_MaintainPosture) suspect.jobs.EndCurrentJob(JobCondition.InterruptForced);
            return true;
        }
    }

    /// <summary>The officer collects the suspect (who stays put, cuffed), then both leave the map.</summary>
    public class LordJob_Arrest : LordJob
    {
        public const string MemoCollected = "SuspectCollected";

        private Pawn suspect;
        private bool smuggler;
        private bool collected;

        public LordJob_Arrest()
        {
        }

        public LordJob_Arrest(Pawn suspect, bool smuggler)
        {
            this.suspect = suspect;
            this.smuggler = smuggler;
        }

        public Pawn Suspect => suspect;

        public override bool CanOpenAnyDoor(Pawn p) => true;

        /// <summary>The officer and the suspect (when staff) pass locked doors, as long as the arrest runs. The flag is not saved, so a loaded arrest sets it again on its first tick.</summary>
        public void GrantPass()
        {
            if (lord == null) return;
            foreach (Pawn p in lord.ownedPawns)
                if (p.GetComp<CompStaff>() is { passLocked: false } sc) sc.passLocked = true;
        }

        public override void LordJobTick() => GrantPass();

        /// <summary>The officer cuffed them (once).</summary>
        public void Notify_Cuffed()
        {
            if (collected || suspect == null) return;
            collected = true;
            Detention.Notify_Collected(suspect, smuggler);
        }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var collect = new LordToil_Arrest();
            graph.StartingToil = collect;
            var leave = new LordToil_ExitMap(LocomotionUrgency.Walk);
            graph.AddToil(leave);
            var done = new Transition(collect, leave);
            done.AddTrigger(new Trigger_Memo(MemoCollected));
            // The suspect is gone (dead, freed, walked off) or the officer never got there: wrap up.
            done.AddTrigger(new Trigger_PawnLost());
            done.AddTrigger(new Trigger_TicksPassed(GenDate.TicksPerDay / 2));
            done.AddPreAction(new TransitionAction_Custom(() =>
            {
                if (!collected && suspect is { Dead: false })
                    Messages.Message("RP_SuspectSlippedAway".Translate(suspect.LabelShort), MessageTypeDefOf.NegativeEvent);
            }));
            graph.AddTransition(done);
            return graph;
        }

        public override void ExposeData()
        {
            Scribe_References.Look(ref suspect, "suspect");
            Scribe_Values.Look(ref smuggler, "smuggler");
            Scribe_Values.Look(ref collected, "collected");
        }
    }

    public class LordToil_Arrest : LordToil
    {
        public override void UpdateAllDuties()
        {
            Pawn suspect = (lord.LordJob as LordJob_Arrest)?.Suspect;
            foreach (Pawn p in lord.ownedPawns)
                p.mindState.duty = p == suspect ? new PawnDuty(RPDetentionDefOf.RP_Detained) : new PawnDuty(RPDetentionDefOf.RP_ArrestSuspect, suspect);
        }
    }

    /// <summary>Officer: walk to the suspect and cuff them.</summary>
    public class JobGiver_ArrestSuspect : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.mindState.duty?.focus.Thing is not Pawn suspect || !suspect.Spawned) return null;
            if (!pawn.CanReach(suspect, PathEndMode.Touch, Danger.Deadly)) return null;
            return JobMaker.MakeJob(RPDetentionDefOf.RP_Cuff, suspect);
        }
    }

    /// <summary>Detained: stand still, hands where they can be seen.</summary>
    public class JobGiver_Detained : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
            wait.expiryInterval = 600;
            return wait;
        }
    }

    public class JobDriver_Cuff : JobDriver
    {
        private Pawn Suspect => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            Toil approach = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return approach;
            yield return Toils_Jump.JumpIf(approach, () => !pawn.Position.AdjacentTo8WayOrInside(Suspect.Position));
            Toil cuff = Toils_General.Wait(180);
            cuff.handlingFacing = true;
            cuff.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Suspect);
            cuff.WithProgressBarToilDelay(TargetIndex.A);
            yield return cuff;
            Toil done = ToilMaker.MakeToil("Cuffed");
            done.initAction = () =>
            {
                Messages.Message("RP_SuspectTakenAway".Translate(pawn.LabelShort, Suspect.LabelShort), Suspect, MessageTypeDefOf.NeutralEvent, historical: false);
                (pawn.GetLord()?.LordJob as LordJob_Arrest)?.Notify_Cuffed();
                pawn.GetLord()?.ReceiveMemo(LordJob_Arrest.MemoCollected);
            };
            yield return done;
        }
    }
}
