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
        /// <summary>The state's fine for a day the case waits for the warden's authority (<see cref="ExecStage.Awaiting"/>): this much the first day, this much more every day after, at most <see cref="overdueDaysMax"/> times it a day.</summary>
        public int overduePenaltyPerDay = 150;
        public int overdueDaysMax = 5;
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

        /// <summary>The warden has authorised it and the night has begun: the restraint, the carry and the table (the governor answers a plea first).</summary>
        public static bool ExecutionDue(CompInmate c) =>
            c.execStage == ExecStage.Authorised && c.executionTick >= 0 && Find.TickManager.TicksGame >= c.executionTick && Ext(c) != null && c.clemencyResolveTick < 0;

        /// <summary>The date has come and the case waits for the warden, or for the night they set: the state stops paying for the inmate, and the table is wanted.</summary>
        public static bool Pending(CompInmate c) => c.execStage is ExecStage.Awaiting or ExecStage.Authorised && Ext(c) != null;

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
            ClearCase(c);
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
                // The case's stage (see LastDay): the date reached waits for the warden; the authorised one has its evening, its detail and its night.
                LastDay.Hourly(map, p, c, ext, now);
                if (!ExecutionDue(c)) continue;
                if (FindTable(map, null) == null) continue;
                // On the night guards come for them in their cell (calm and inside it; otherwise next hour). Restraint is renewed
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
            // The hearing a stay won them is the last one: lost, the case is back with the warden.
            if (c.stayUsed)
            {
                LastDay.StayLost(p, c);
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
                if (!HoldsBody(b) && (carrier == null || carrier.CanReserveAndReach(b, PathEndMode.OnCell, Danger.Deadly))) return b;
            return null;
        }

        /// <summary>A body lies on the table (the executioner moves the one they made at once; one put there otherwise stays until someone takes it): the next cannot be laid on it.</summary>
        public static bool HoldsBody(Building table) => table.Spawned && table.Position.GetFirstThing<Corpse>(table.Map) != null;

        /// <summary>The body that keeps the next execution from the table: no table is free and one holds a body (null when a table is free, or there is no table at all - that is "no table", a different thing to build).</summary>
        public static Corpse BodyOnTable(Map map)
        {
            if (FindTable(map, null) != null) return null;
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(RPDeathRowDefOf.RP_ExecutionTable))
                if (b.Spawned && b.Position.GetFirstThing<Corpse>(b.Map) is { } body) return body;
            return null;
        }

        /// <summary>What the warden reads for <see cref="BodyOnTable"/>: whose body, and what it needs (the body allowed, a morgue painted, room made, or a hauler).</summary>
        public static string BodyOnTableText(Corpse body)
        {
            PrisonMap pm = PrisonMap.Of(body.Map);
            // (Forbidden first: with it the staff pass the body by, whatever else is done.)
            string need = body.IsForbidden(Faction.OfPlayer) ? "RP_Table_NeedAllow" : pm == null || !pm.zones.Any(RPMorgueDefOf.RP_Morgue) ? "RP_Table_NeedMorgue" : Morgue.Full(body.Map, pm) ? "RP_Table_NeedRoom" : "RP_Table_NeedHauler";
            return "RP_Problem_ExecutionBodyOnTable".Translate(body.InnerPawn?.LabelShort ?? body.LabelShort, need.Translate()).Resolve();
        }

        /// <summary>
        /// The stay is over (released, transferred, dead, escaped: <see cref="StorySystem.Notify_Leaving"/>): the case ends and the detail is freed. An executed one stays executed. The one stay of the case is not given back (an escaped one who is
        /// brought back is the same case, with the date it had; a release resets the whole of the stay anyway, <see cref="CompInmate.ResetForNewStay"/>).
        /// </summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p.Inmate() is { } c && c.execStage != ExecStage.Executed) ClearCase(c, keepStay: true);
        }

        /// <summary>Takes the restraint off (the regime walks them back to their cell).</summary>
        public static void RemoveRestraint(Pawn p)
        {
            if (p.health?.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained) is { } restraint) p.health.RemoveHediff(restraint);
        }

        /// <summary>Off death row's calendar for good, or for now: the stage, the stay (unless <paramref name="keepStay"/>), the evening, the detail and its refusals and what came out of the wrongful execution go (see <see cref="LastDay"/>).</summary>
        public static void ClearCase(CompInmate c, bool keepStay = false)
        {
            c.execStage = ExecStage.None;
            if (!keepStay) c.stayUsed = false;
            c.lastEveningDone = c.exposedWrongful = false;
            c.detailIds.Clear();
            c.detailRefused.Clear();
        }

        /// <summary>
        /// The sentence is carried out (the inmate lies on the table): the effects only - <see cref="LastDay.TryExecute"/> is what asks whether it may be. The body stays where it fell, and goes to the morgue and home or to the plot
        /// like any death's; the condemned is <see cref="ExecStage.Executed"/> for good (the funeral is not offered, the family's letter says it was an execution).
        /// </summary>
        public static void Execute(Pawn inmate, Pawn executioner)
        {
            CompInmate c = inmate.Inmate();
            DeathRowExtension ext = Ext(c);
            Map map = inmate.Map;
            PrisonGame game = PrisonGame.Instance;
            c.executing = true;
            bool innocent = c.innocent;
            string name = inmate.LabelShort;
            // Only an innocence that comes out is written down (the rest is hidden: no fine, no letter, no line saying so).
            bool exposed = innocent && ext != null && Rand.Chance(ext.exposedChance);
            LastDay.LastWords(inmate, c);
            // (The ones kept apart from them are noted before the story lets go of them, for the letter that comes with the body.)
            Funerals.Notify_Dying(inmate);
            // (The Kill prefix skips an execution, so the stories they are in end here, while they are still on the map.)
            game.story.Notify_Leaving(inmate);
            inmate.guilt?.Notify_Guilty();
            inmate.Kill(null);
            // (Vanilla forbids a body that falls outside the home area, which the morgue's haulers then pass by: the prison's dead are the staff's to carry.)
            inmate.Corpse?.SetForbidden(false, warnOnFail: false);
            // (After the story let go of the case: the one stage that stays, what the body and its letter ask about.)
            c.execStage = ExecStage.Executed;
            c.exposedWrongful = exposed;
            game.stats.executions++;
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
            if (c.execStage == ExecStage.Awaiting) return "RP_DeathRowAwaiting".Translate();
            if (c.execStage == ExecStage.Authorised)
                return now >= c.executionTick ? "RP_DeathRowDue".Translate() : "RP_DeathRowAuthorised".Translate(((c.executionTick - now) / (float)GenDate.TicksPerHour).ToString("0"));
            if (c.executionTick >= 0) return "RP_DeathRowScheduled".Translate(Mathf.Max(0f, (c.executionTick - now) / (float)GenDate.TicksPerHour).ToString("0"));
            string hearing = ((c.nextAppealTick - now) / (float)GenDate.TicksPerDay).ToString("0.0");
            return c.stayUsed ? "RP_DeathRowStay".Translate(hearing) : "RP_DeathRowAppeals".Translate(Mathf.Max(0, c.appealsLeft), hearing);
        }
    }

    /// <summary>Guards carry a restrained inmate, whose night it is, to the execution table and carry out the sentence: the guards the warden named (<see cref="LastDay.MayTake"/>), or any free one if none is named or nobody has by the third hour.</summary>
    public class WorkGiver_CarryOutExecution : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Execution(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || (!forced && InmateCandidates.Shared.Execution(pawn.Map).Count == 0);

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the guard (the date has come and they are down).</summary>
        public static bool Candidate(Pawn inmate) => inmate.Inmate() is { } c && DeathRowSystem.ExecutionDue(c) && inmate.Downed;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate)
            && inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained) && LastDay.MayTake(pawn, inmate.Inmate()) && DeathRowSystem.FindTable(pawn.Map, pawn) != null
            && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            // (The same tests as HasJobOnThing: a target it passed always yields a job, or vanilla's work giver logs an error. A guard with kin on the row never gets here: LastDay.MayTake.)
            Building table = DeathRowSystem.FindTable(pawn.Map, pawn);
            if (table == null || !pawn.CanReserveAndReach(t, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)) return null;
            Job job = JobMaker.MakeJob(RPDeathRowDefOf.RP_CarryOutExecution, t, table);
            job.count = 1;
            return job;
        }
    }

    public class JobDriver_CarryOutExecution : JobDriver
    {
        private const int ProcedureTicks = 900;

        /// <summary>The toil of the procedure's wait (go to them, lift, go to the table, lay, <b>wait</b>, the final action): from it on the condemned is on the table, and not being there is a stop.</summary>
        public const int WaitToil = 4;

        private Pawn Inmate => (Pawn)job.targetA.Thing;

        /// <summary>The stop was told already (the fail condition is asked every tick, and the finish action asks once more).</summary>
        private bool stopped;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);

        /// <summary>The stop, once: the reason is written and the restraint comes off (the regime walks them back). True: the job is stopped. <paramref name="whyIfNone"/>: the reason when nothing in particular stands in the way (the job was ended from outside).</summary>
        private bool StopIfBlocked(bool onTable, string whyIfNone = null)
        {
            if (stopped) return true;
            Pawn inmate = job.targetA.Thing as Pawn;
            // (Done: the last toil's own success is not a stop.)
            if (inmate?.Inmate() is { execStage: ExecStage.Executed }) return false;
            string why = LastDay.Blocker(inmate, job.targetB.Thing as Building, onTable) ?? whyIfNone;
            if (why == null) return false;
            stopped = true;
            LastDay.Stopped(inmate, pawn, why);
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Whatever stops it on the way (a commutation, a stay, the table gone, the condemned gone) is told and the restraint comes off: LastDay.Blocker, asked again at the last moment by LastDay.TryExecute. First of the fail
            // conditions, which RimWorld asks in the order they were added and ends on the first that fails: after the destroyed-or-null ones the table's destruction would end the job unsaid.
            this.FailOn(() => StopIfBlocked(CurToilIndex >= WaitToil));
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            // Every other way out before the sentence (the executioner drafted, downed or given another order, a toil's own failure, the condemned moved off the table) is told and cleaned up the same, whether or not anything stands in the way.
            AddFinishAction(condition =>
            {
                if (condition != JobCondition.Succeeded) StopIfBlocked(CurToilIndex >= WaitToil, whyIfNone: "RP_ExecStop_Interrupted".Translate().Resolve());
            });
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
            execute.initAction = () =>
            {
                if (LastDay.TryExecute(Inmate, pawn, job.targetB.Thing as Building))
                {
                    // The body is moved off the table at once (a morgue cell, else a cell beside it): the next execution is not kept waiting for the haulers.
                    if (LastDay.BodyJob(pawn, Inmate.Corpse, job.targetB.Thing as Building) is { } move) pawn.jobs.jobQueue.EnqueueFirst(move);
                    return;
                }
                // (Stopped and said by the final action itself.)
                stopped = true;
                EndJobWith(JobCondition.Incompletable);
            };
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
