using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Makes a security level death row: appeals, then an execution date.</summary>
    public class DeathRowExtension : DefModExtension
    {
        public IntRange appeals = new IntRange(2, 3);
        public FloatRange appealIntervalDays = new FloatRange(1.5f, 3f);
        public float overturnChance = 0.08f;
        public float overturnChanceInnocent = 0.5f;
        public float innocentChance = 0.08f;
        /// <summary>Chance a wrongful execution comes to light (lawsuit, reputation).</summary>
        public float exposedChance = 0.6f;
        public int wrongfulFine = 5000;
        public float wrongfulReputation = 15f;
        public float noticeDays = 1f;
        /// <summary>State fine per day an execution is overdue (no execution room).</summary>
        public int overduePenaltyPerDay = 500;
        /// <summary>Security level after a commuted sentence (life).</summary>
        public SecurityLevelDef commuteTo;
        /// <summary>Tension added to every inmate on the day of an execution.</summary>
        public float executionTension = 6f;
    }

    /// <summary>
    /// When the appeals run out, the condemned's last request comes to the warden: a last meal (they go calmer, and the
    /// wing takes the execution better), a letter to the governor for clemency (decided before the date: the innocent
    /// usually get it, the guilty rarely, and a refused plea makes the warden look soft), or nothing.
    /// </summary>
    public static class LastRequests
    {
        public const int MealCost = 50;
        public const float ClemencyInnocent = 0.6f, ClemencyGuilty = 0.1f;

        public static void Ask(Pawn p, CompInmate c)
        {
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_LastRequest", "RP_LastRequestLabel".Translate(p.LabelShort),
                "RP_LastRequestText".Translate(p.LabelShort, Money.Format(MealCost)), p.Map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerHour * 8);
            Find.LetterStack.ReceiveLetter(letter);
        }

        public static void ResolveClemency(Pawn p, CompInmate c, DeathRowExtension ext)
        {
            PrisonGame game = PrisonGame.Instance;
            if (Rand.Chance(c.innocent ? ClemencyInnocent : ClemencyGuilty))
            {
                game.stats.commuted++;
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 3, "RP_Chr_Clemency".Translate(p.LabelShort), p);
                Find.LetterStack.ReceiveLetter("RP_ClemencyLabel".Translate(p.LabelShort), "RP_ClemencyText".Translate(p.LabelShort, ext.commuteTo.label), LetterDefOf.PositiveEvent, p);
                DeathRowSystem.Commute(p, c, ext);
                return;
            }
            game.reputation = Mathf.Max(0f, game.reputation - 2f);
            Messages.Message("RP_ClemencyDenied".Translate(p.LabelShort), p, MessageTypeDefOf.NegativeEvent);
        }
    }

    public class LastRequestDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_LastRequest_Meal", "RP_LastRequest_Clemency", "RP_LastRequest_Nothing" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < LastRequests.MealCost ? "RP_CannotAfford".Translate(Money.Format(LastRequests.MealCost)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.FirstOrDefault(x => x != null);
            if (p == null || p.Dead || p.Inmate() is not { } c || DeathRowSystem.Ext(c) == null || c.executionTick < 0) return;
            if (option == 0)
            {
                if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Supplies, LastRequests.MealCost, "RP_LastMealNote".Translate(p.LabelShort))) return;
                c.lastMealGranted = true;
                c.tension = Mathf.Max(0f, c.tension - 20f);
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_LastMeal".Translate(p.LabelShort), p);
            }
            else if (option == 1)
            {
                // The governor answers before the date (two hours to spare).
                int now = Find.TickManager.TicksGame;
                c.clemencyResolveTick = Mathf.Max(now + GenDate.TicksPerHour, c.executionTick - 2 * GenDate.TicksPerHour);
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_ClemencyAsked".Translate(p.LabelShort), p);
            }
        }
    }

    /// <summary>
    /// Death row (state-sentenced): the inmate lives out their appeals in a death row cell. Each hearing may commute
    /// the sentence to life (much more likely for the innocent). When appeals run out an execution date is set; on
    /// the day, guards restrain the inmate in their cell and carry them to the execution room.
    /// </summary>
    public static class DeathRowSystem
    {
        public static DeathRowExtension Ext(CompInmate c) => c?.security?.GetModExtension<DeathRowExtension>();

        public static bool ExecutionDue(CompInmate c) =>
            c.executionTick >= 0 && Find.TickManager.TicksGame >= c.executionTick && Ext(c) != null && c.clemencyResolveTick < 0; // the governor answers first

        /// <summary>A day past the date: the state stops paying for the inmate and fines the prison daily.</summary>
        public static bool ExecutionOverdue(CompInmate c) => ExecutionDue(c) && Find.TickManager.TicksGame >= c.executionTick + GenDate.TicksPerDay;

        /// <summary>Restrains the inmate, or keeps an existing restraint from wearing off.</summary>
        public static void Restrain(Pawn p)
        {
            Hediff h = p.health.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained);
            if (h == null) p.health.AddHediff(RPSecurityDefOf.RP_Restrained);
            else if (h.TryGetComp<HediffComp_Disappears>() is { } d) d.ticksToDisappear = d.disappearsAfterTicks;
        }

        /// <summary>Off death row for good: the date, a pending plea and the last meal go with it.</summary>
        public static void Commute(Pawn p, CompInmate c, DeathRowExtension ext)
        {
            c.executionTick = -1;
            c.clemencyResolveTick = -1;
            c.lastMealGranted = false;
            // (A sentence that cannot wait for a bed: reclassified all the same, and moved when a bed of theirs is free; the warden is told if the wing is kept for the other sex.)
            InmateUtility.ChangeSecurity(p, c, ext.commuteTo, onPaper: true);
        }

        public static void OnAdmitted(CompInmate c, DeathRowExtension ext)
        {
            c.appealsLeft = ext.appeals.RandomInRange;
            c.nextAppealTick = Find.TickManager.TicksGame + Mathf.RoundToInt(ext.appealIntervalDays.RandomInRange * GenDate.TicksPerDay);
            c.executionTick = -1;
            c.innocent = Rand.Chance(ext.innocentChance);
        }

        /// <summary>Map hourly pass over death row inmates (also the restrained ones, so not from the inmate's tick).</summary>
        public static void Hourly(Map map)
        {
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = prisoners.Count - 1; i >= 0; i--)
            {
                Pawn p = prisoners[i];
                CompInmate c = p.Inmate();
                DeathRowExtension ext = Ext(c);
                if (ext == null || !c.activeFlag) continue;
                if (c.appealsLeft < 0) OnAdmitted(c, ext);
                int now = Find.TickManager.TicksGame;
                if (c.executionTick < 0)
                {
                    if (now >= c.nextAppealTick) Appeal(p, c, ext);
                    continue;
                }
                if (c.clemencyResolveTick >= 0 && now >= c.clemencyResolveTick)
                {
                    c.clemencyResolveTick = -1;
                    LastRequests.ResolveClemency(p, c, ext);
                    if (Ext(c) == null) continue; // commuted
                }
                if (now < c.executionTick) continue;
                // Whatever the reason (no table, unreachable table, no guards), every full day overdue is fined.
                if (now - c.lastOverdueTick >= GenDate.TicksPerDay)
                {
                    c.lastOverdueTick = now;
                    PrisonGame.Instance.ledger.Add(RPDefOf.RP_Fines, -ext.overduePenaltyPerDay, "RP_ExecutionOverdueNote".Translate(p.LabelShort));
                    string hint = FindTable(map, null) == null ? "RP_ExecutionNoTable".Translate() : "RP_ExecutionNoGuard".Translate();
                    Find.LetterStack.ReceiveLetter("RP_ExecutionOverdueLabel".Translate(), "RP_ExecutionOverdueText".Translate(p.LabelShort, Money.Format(ext.overduePenaltyPerDay), hint), LetterDefOf.NegativeEvent, p);
                }
                if (FindTable(map, null) == null) continue;
                // Guards come for them in their cell (calm and inside it; otherwise next hour). Restraint is renewed
                // every hour while they wait for a guard.
                bool restrained = p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained);
                if (restrained || (!p.InMentalState && p.GetRoom() == InmateUtility.CellOf(p)))
                {
                    Restrain(p);
                    if (!restrained) Messages.Message("RP_ExecutionDay".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent);
                }
            }
        }

        private static void Appeal(Pawn p, CompInmate c, DeathRowExtension ext)
        {
            c.appealsLeft--;
            if (Rand.Chance(c.innocent ? ext.overturnChanceInnocent : ext.overturnChance))
            {
                PrisonGame.Instance.stats.commuted++;
                c.appealsLeft = 0;
                Find.LetterStack.ReceiveLetter("RP_AppealWonLabel".Translate(), "RP_AppealWonText".Translate(p.LabelShort, ext.commuteTo.label), LetterDefOf.PositiveEvent, p);
                Commute(p, c, ext);
                return;
            }
            if (c.appealsLeft > 0)
            {
                c.nextAppealTick = Find.TickManager.TicksGame + Mathf.RoundToInt(ext.appealIntervalDays.RandomInRange * GenDate.TicksPerDay);
                Messages.Message("RP_AppealDenied".Translate(p.LabelShort, c.appealsLeft), p, MessageTypeDefOf.NeutralEvent);
                c.tension = Mathf.Min(100f, c.tension + 10f);
                return;
            }
            c.executionTick = Find.TickManager.TicksGame + Mathf.RoundToInt(ext.noticeDays * GenDate.TicksPerDay);
            c.lastOverdueTick = c.executionTick; // first fine a full day after the date
            c.tension = Mathf.Min(100f, c.tension + 20f);
            Find.LetterStack.ReceiveLetter("RP_AppealsExhaustedLabel".Translate(), "RP_AppealsExhaustedText".Translate(p.LabelShort, ext.noticeDays.ToString("0.#")), LetterDefOf.NegativeEvent, p);
            LastRequests.Ask(p, c);
        }

        public static Building FindTable(Map map, Pawn carrier)
        {
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(RPDeathRowDefOf.RP_ExecutionTable))
                if (carrier == null || carrier.CanReserveAndReach(b, PathEndMode.OnCell, Danger.Deadly)) return b;
            return null;
        }

        /// <summary>The sentence is carried out (the inmate lies on the table).</summary>
        public static void Execute(Pawn inmate, Pawn executioner)
        {
            CompInmate c = inmate.Inmate();
            DeathRowExtension ext = Ext(c);
            Map map = inmate.Map;
            PrisonGame game = PrisonGame.Instance;
            c.executing = true;
            bool innocent = c.innocent;
            string name = inmate.LabelShort;
            // (The Kill prefix skips an execution, so the stories they are in end here, while they are still on the map.)
            game.story.Notify_Leaving(inmate);
            inmate.guilt?.Notify_Guilty();
            inmate.Kill(null);
            // The body is released to the coroner.
            if (inmate.Corpse is { Destroyed: false } corpse) corpse.Destroy();
            game.stats.executions++;
            // Only an innocence that comes out is written down (the rest is hidden: no fine, no letter, no line saying so).
            bool exposed = innocent && ext != null && Rand.Chance(ext.exposedChance);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 3, (exposed ? "RP_Chr_ExecutedInnocent" : "RP_Chr_Executed").Translate(name));
            // The condemned's kin on the staff (and the wing) mourn them (vanilla's family thoughts came with the kill; a staff member's break that night is theirs to have, see Kin.Notify_Died).
            Kin.Notify_Died(inmate, executed: true);
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { } oc)
                {
                    oc.tension = Mathf.Min(100f, oc.tension + (ext?.executionTension ?? 6f) * (c.lastMealGranted ? 0.5f : 1f));
                    oc.mainCause = "RP_Cause_Execution";
                }
            if (exposed)
            {
                game.stats.wrongfulExecutions++;
                game.reputation = Mathf.Max(0f, game.reputation - ext.wrongfulReputation);
                game.ledger.Add(RPDefOf.RP_Fines, -ext.wrongfulFine, "RP_WrongfulExecutionNote".Translate(name));
                Find.LetterStack.ReceiveLetter("RP_WrongfulExecutionLabel".Translate(), "RP_WrongfulExecutionText".Translate(name, Money.Format(ext.wrongfulFine)), LetterDefOf.ThreatBig);
            }
            else Messages.Message("RP_Executed".Translate(name), MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>For the sentence column and inspect string.</summary>
        public static string Status(CompInmate c)
        {
            if (Ext(c) == null) return null;
            int now = Find.TickManager.TicksGame;
            if (c.executionTick >= 0)
                return now >= c.executionTick ? "RP_DeathRowDue".Translate() : "RP_DeathRowScheduled".Translate(((c.executionTick - now) / (float)GenDate.TicksPerHour).ToString("0"));
            return "RP_DeathRowAppeals".Translate(Mathf.Max(0, c.appealsLeft), ((c.nextAppealTick - now) / (float)GenDate.TicksPerDay).ToString("0.0"));
        }
    }

    /// <summary>Guards carry a restrained, due inmate to the execution table and carry out the sentence.</summary>
    public class WorkGiver_CarryOutExecution : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Execution(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || (!forced && InmateCandidates.Shared.Execution(pawn.Map).Count == 0);

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the guard (the date has come and they are down).</summary>
        public static bool Candidate(Pawn inmate) => inmate.Inmate() is { } c && DeathRowSystem.ExecutionDue(c) && inmate.Downed;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate)
            && inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained) && DeathRowSystem.FindTable(pawn.Map, pawn) != null
            && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building table = DeathRowSystem.FindTable(pawn.Map, pawn);
            if (table == null) return null;
            Job job = JobMaker.MakeJob(RPDeathRowDefOf.RP_CarryOutExecution, t, table);
            job.count = 1;
            return job;
        }
    }

    public class JobDriver_CarryOutExecution : JobDriver
    {
        private const int ProcedureTicks = 900;

        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            this.FailOn(() => Inmate.Inmate() is not { } c || DeathRowSystem.Ext(c) == null);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedOrNull(TargetIndex.A).FailOn(() => !Inmate.Downed);
            Toil pickUp = Toils_Haul.StartCarryThing(TargetIndex.A);
            pickUp.AddPreInitAction(() => DeathRowSystem.Restrain(Inmate));
            yield return pickUp;
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            Toil lay = ToilMaker.MakeToil("LayOnTable");
            lay.initAction = () => pawn.carryTracker.TryDropCarriedThing(job.targetB.Thing.Position, ThingPlaceMode.Direct, out Thing _);
            yield return lay;
            Toil procedure = Toils_General.Wait(ProcedureTicks);
            procedure.FailOn(() => !Inmate.Spawned || Inmate.Position != job.targetB.Thing.Position);
            procedure.WithProgressBarToilDelay(TargetIndex.B);
            procedure.tickIntervalAction = _ =>
            {
                // Stay restrained for the whole procedure.
                if (!Inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) Inmate.health.AddHediff(RPSecurityDefOf.RP_Restrained);
            };
            yield return procedure;
            Toil execute = ToilMaker.MakeToil("Execute");
            execute.initAction = () => DeathRowSystem.Execute(Inmate, pawn);
            yield return execute;
        }
    }

    [DefOf]
    public static class RPDeathRowDefOf
    {
        public static ThingDef RP_ExecutionTable;
        public static JobDef RP_CarryOutExecution;

        static RPDeathRowDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDeathRowDefOf));
    }
}
